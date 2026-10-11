using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Api;

/// <summary>
/// The whole program (<see cref="LabcoatApp.RunAsync"/>) in-process on real Kestrel ports (4520-4529): exit codes of the
/// command line, settings files, port precedence and shutdown. Environment variables are process-wide: not parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunAsyncTests
{
    public TestContext TestContext { get; set; }

    private static readonly string[] IsolatedEnv =
        ["PORT", "ASPNETCORE_URLS", "LABCOAT_ALLOWED_HOSTS", "LABCOAT_ALLOWED_ORIGINS", "LABCOAT_TOKEN", "LABCOAT_NAME",
         "LABCOAT_UI_SETTINGS", LabcoatConfig.AppHostUiEnv, LabcoatConfig.NoUserConfigEnv, "ASPIRE_DASHBOARD_URL", "CI"];

    private Dictionary<string, string?> _savedEnv = [];
    private DirectoryInfo _dir = null!;
    private string _workspace = null!;

    [TestInitialize]
    public void Init()
    {
        // Read once per process from the environment: initialize them now, so the variables set below can't leak into
        // other tests through them.
        _ = (DashboardTelemetry.Available, Telemetry.Available, Cli.UseColor);
        _savedEnv = IsolatedEnv.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var k in IsolatedEnv) Environment.SetEnvironmentVariable(k, null);
        _dir = Directory.CreateTempSubdirectory("labcoat-run-");
        _workspace = Directory.CreateDirectory(Path.Combine(_dir.FullName, "shop")).FullName;
        Directory.CreateDirectory(Path.Combine(_workspace, ".git")); // the settings file walk-up stops here
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var (k, v) in _savedEnv) Environment.SetEnvironmentVariable(k, v);
        try { _dir.Delete(recursive: true); } catch { }
    }

    /// <summary>Starts RunAsync, waits until <paramref name="baseUrl"/> answers /api/health, runs <paramref name="check"/>, stops it.</summary>
    private static async Task RunUntilHealthy(string[] args, string baseUrl, Func<HttpClient, Task> check)
    {
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => LabcoatApp.RunAsync(args, cts.Token));
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                if (run.IsCompleted) Assert.Fail($"RunAsync ended early with exit code {await run}");
                try
                {
                    using var res = await http.GetAsync("/api/health");
                    if (res.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                if (DateTime.UtcNow > deadline) Assert.Fail($"{baseUrl} did not answer");
                await Task.Delay(100);
            }
            await check(http);
        }
        finally
        {
            cts.Cancel();
            var stopped = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.AreSame(run, stopped, "RunAsync did not stop after cancellation");
        }
        Assert.AreEqual(0, await run);
    }

    [TestMethod]
    public async Task Help_version_and_invalid_command_lines_exit_without_starting()
    {
        Assert.AreEqual(0, await LabcoatApp.RunAsync(["--help"], TestContext.CancellationToken));
        Assert.AreEqual(0, await LabcoatApp.RunAsync(["--version"], TestContext.CancellationToken));
        Assert.AreEqual(1, await LabcoatApp.RunAsync(["--bogus"], TestContext.CancellationToken));
        Assert.AreEqual(1, await LabcoatApp.RunAsync(["--port", "0"], TestContext.CancellationToken));
        Assert.AreEqual(1, await LabcoatApp.RunAsync([Path.Combine(_workspace, "missing"), Path.Combine(_workspace, "also-missing.csproj")], TestContext.CancellationToken));
        Assert.AreEqual(1, await LabcoatApp.RunAsync([_workspace, "--no-user-config", "--config", Path.Combine(_workspace, "missing.json")], TestContext.CancellationToken));
    }

    [TestMethod]
    public void The_entry_point_runs_the_app() =>
        Assert.AreEqual(0, typeof(LabcoatApp).Assembly.EntryPoint!.Invoke(null, [new[] { "--version" }]));

    [TestMethod]
    public async Task Starts_with_the_settings_file_and_command_line_and_stops_on_cancellation()
    {
        File.WriteAllText(Path.Combine(_workspace, ".labcoat.json"), """
            { "watch": true, "coverage": true, "ui": { "theme": "dracula", "mode": "dark" }, "colour": "blue" }
            """);
        // Hosted by an Aspire AppHost: the dashboard URL is passed through and no browser is opened.
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_URL", "http://localhost:18888");
        Environment.SetEnvironmentVariable("LABCOAT_NAME", "tests");
        await RunUntilHealthy([_workspace, "--no-user-config", "--port", "4521", "--theme", "one", "--lock", "theme"],
            "http://localhost:4521", async http =>
            {
                var state = JsonNode.Parse(await http.GetStringAsync("/api/state", TestContext.CancellationToken))!;
                Assert.AreEqual("tests", state["workspaceName"]!.GetValue<string>());
                Assert.AreEqual("http://localhost:18888", state["aspireDashboardUrl"]!.GetValue<string>());
                Assert.IsTrue(state["watch"]!.GetValue<bool>());
                Assert.IsTrue(state["coverageEnabled"]!.GetValue<bool>());
                Assert.IsFalse(state["tracingEnabled"]!.GetValue<bool>());
                Assert.IsEmpty(state["projects"]!.AsArray());
                var ui = state["uiDefaults"]!;
                Assert.AreEqual("one", ui["theme"]!.GetValue<string>());   // command line over the workspace file
                Assert.AreEqual("dark", ui["mode"]!.GetValue<string>());
                Assert.AreEqual("cli", ui["lockedBy"]!["theme"]!.GetValue<string>());
                Assert.AreEqual(".labcoat.json", ui["files"]!["workspace"]!.GetValue<string>());
                var logs = state["logs"]!.AsArray().Select(l => l!["message"]!.GetValue<string>()).ToList();
                Assert.IsTrue(logs.Any(l => l.StartsWith("Settings: .labcoat.json: unknown setting 'colour'", StringComparison.Ordinal)), string.Join("\n", logs));
                Assert.IsTrue(logs.Any(l => l.StartsWith("UI defaults: theme=one (cli, locked)", StringComparison.Ordinal)), string.Join("\n", logs));

                // The listen host is the only extra Host name; anything else is rejected.
                using var req = new HttpRequestMessage(HttpMethod.Get, "/api/health");
                req.Headers.Host = "attacker.example:4521";
                Assert.AreEqual(HttpStatusCode.BadRequest, (await http.SendAsync(req, TestContext.CancellationToken)).StatusCode);
            });
    }

    [TestMethod]
    public async Task A_listen_address_that_is_busy_is_an_error()
    {
        using var busy = new TcpListener(IPAddress.Loopback, 4522);
        busy.Start();
        Assert.AreEqual(1, await LabcoatApp.RunAsync([_workspace, "--no-open", "--no-user-config", "--port", "4522"], TestContext.CancellationToken));
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:4522");
        Assert.AreEqual(1, await LabcoatApp.RunAsync([_workspace, "--no-open", "--no-user-config"], TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Without_paths_the_current_directory_is_the_workspace()
    {
        var cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_workspace);
        Environment.SetEnvironmentVariable("CI", "true"); // "open the browser" is a no-op on CI
        try
        {
            await RunUntilHealthy(["--no-user-config", "--port", "4526"], "http://localhost:4526", async http =>
            {
                var state = JsonNode.Parse(await http.GetStringAsync("/api/state", TestContext.CancellationToken))!;
                Assert.AreEqual("shop", state["workspaceName"]!.GetValue<string>());
                CollectionAssert.Contains(state["sourceRoots"]!.AsArray().Select(n => n!.GetValue<string>()).ToList(), PathUtil.Canonicalize(_workspace));
            });
        }
        finally { Directory.SetCurrentDirectory(cwd); }
    }

    [TestMethod]
    public async Task Port_precedence_PORT_then_settings_file_then_ASPNETCORE_URLS()
    {
        File.WriteAllText(Path.Combine(_workspace, ".labcoat.json"), """{ "port": 4524 }""");
        string[] args = [_workspace, "--no-open", "--no-user-config"];

        Environment.SetEnvironmentVariable("PORT", "4523");
        await RunUntilHealthy(args, "http://localhost:4523", _ => Task.CompletedTask);

        Environment.SetEnvironmentVariable("PORT", null);
        await RunUntilHealthy(args, "http://localhost:4524", _ => Task.CompletedTask);

        // ASPNETCORE_URLS is used as is (and beats the file's port); its host is an allowed Host name.
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:4525");
        Environment.SetEnvironmentVariable("LABCOAT_ALLOWED_HOSTS", "*"); // warns: the Host check is off
        await RunUntilHealthy(args, "http://127.0.0.1:4525", async http =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            req.Headers.Host = "anything.example";
            Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(req, TestContext.CancellationToken)).StatusCode);
        });
    }

    [TestMethod]
    public void Web_root_is_the_project_wwwroot_from_source_else_next_to_the_binaries()
    {
        var project = Directory.CreateDirectory(Path.Combine(_dir.FullName, "src", "Labcoat")).FullName;
        File.WriteAllText(Path.Combine(project, "Labcoat.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(project, "wwwroot"));
        var bin = Directory.CreateDirectory(Path.Combine(project, "bin", "Debug", "net10.0")).FullName;
        Assert.AreEqual(Path.Combine(project, "wwwroot"), LabcoatApp.FindWebRoot(bin));

        var installed = Directory.CreateDirectory(Path.Combine(_dir.FullName, "tools", "labcoat")).FullName;
        Assert.AreEqual(Path.Combine(installed, "wwwroot"), LabcoatApp.FindWebRoot(installed));
        Assert.IsTrue(Directory.Exists(Path.Combine(installed, "wwwroot")), "created so static files have a root");
    }
}
