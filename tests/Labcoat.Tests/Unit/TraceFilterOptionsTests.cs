using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The trace filter on the host side: --trace-filter, LABCOAT_TRACE_FILTER and the "traceFilter" setting (checked when
/// Labcoat starts), and LABCOAT_OTLP_FILTER for the test hosts, only while tracing is on.
/// </summary>
[TestClass]
public sealed class TraceFilterOptionsTests
{
    // ------------------------------------------------------------------ command line

    [TestMethod]
    public void Trace_filter_on_the_command_line()
    {
        Assert.AreEqual("cat:Integration trait:Kind=Slow", StartOptions.Parse(["--trace-filter", " cat:Integration trait:Kind=Slow "], out var error)!.TraceFilter);
        Assert.IsNull(error);
        Assert.AreEqual("cat:\"slow tests\"", StartOptions.Parse(["--trace-filter=cat:\"slow tests\""], out _)!.TraceFilter);
        Assert.IsNull(StartOptions.Parse([], out _)!.TraceFilter);
    }

    [TestMethod]
    [DataRow("cat:\"slow tests\" trait:Kind=Slow trait:Owner proj:Shop OrdersApi other:x")]
    [DataRow("\"a b\"x \"\"")]
    public void Valid_trace_filters_are_accepted(string filter) =>
        Assert.AreEqual(filter, StartOptions.Parse(["--trace-filter", filter], out _)!.TraceFilter);

    [TestMethod]
    [DataRow("is:failed", "--trace-filter: 'is:' depends on the test's result")]
    [DataRow("msg:boom", "--trace-filter: 'msg:' depends on the test's result")]
    [DataRow("trait:", "--trace-filter: 'trait:' needs a value, e.g. trait:Kind=Slow.")]
    [DataRow("proj:", "--trace-filter: 'proj:' needs a value, e.g. proj:Orders.Tests.")]
    [DataRow("trait:=x", "--trace-filter: 'trait:' needs a key")]
    [DataRow("cat:\"open", "--trace-filter: The quote at position 5 is not closed.")]
    [DataRow("cat:", "--trace-filter: 'cat:' needs a value")]
    [DataRow("  ", "--trace-filter needs a filter: ")]
    public void An_invalid_trace_filter_on_the_command_line_is_an_error(string filter, string message)
    {
        Assert.IsNull(StartOptions.Parse(["--trace-filter", filter], out var error));
        StringAssert.StartsWith(error, message);
        Assert.IsNull(StartOptions.Parse(["--trace-filter"], out error));
        StringAssert.StartsWith(error, "--trace-filter needs a filter");
    }

    // ------------------------------------------------------------------ settings files

    private sealed class Sandbox : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-trace-filter-");
        public string Home => Path.Combine(Root.FullName, "home");
        public string Workspace => Path.Combine(Home, "repo", "tests");

        public Sandbox()
        {
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Path.Combine(Home, "repo", ".git"));
        }

        public void Write(string relative, string text) => File.WriteAllText(Path.Combine(Root.FullName, relative), text);
        public LabcoatConfig Load() => LabcoatConfig.Load(new LabcoatConfig.Inputs { WorkspaceRoot = Workspace, Home = Home });
        public void Dispose() => Root.Delete(recursive: true);
    }

    [TestMethod]
    public void The_workspace_files_trace_filter_wins_over_the_users()
    {
        using var s = new Sandbox();
        s.Write("home/.labcoat.json", """{"traceFilter": "proj:Shop"}""");
        Assert.AreEqual("proj:Shop", s.Load().TraceFilter);

        s.Write("home/repo/.labcoat.json", """{"traceFilter": " cat:Integration "}""");
        var config = s.Load();
        Assert.AreEqual("cat:Integration", config.TraceFilter);
        Assert.IsEmpty(config.Warnings);

        s.Write("home/repo/.labcoat.json", """{"traceFilter": ""}""");
        Assert.AreEqual("proj:Shop", s.Load().TraceFilter, "blank: no filter of its own");
    }

    [TestMethod]
    [DataRow("""{"traceFilter": "msg:timeout"}""", ".labcoat.json: 'traceFilter': 'msg:' depends on the test's result")]
    [DataRow("""{"traceFilter": 42}""", ".labcoat.json: 'traceFilter' must be a string")]
    public void An_invalid_trace_filter_in_a_settings_file_is_an_error_not_a_warning(string json, string message)
    {
        using var s = new Sandbox();
        s.Write("home/repo/.labcoat.json", json);
        var ex = Assert.ThrowsExactly<ConfigException>(s.Load);
        StringAssert.Contains(ex.Message, message);
    }

    // ------------------------------------------------------------------ precedence

    [TestMethod]
    [DoNotParallelize]
    public void Command_line_wins_over_the_environment_which_wins_over_the_files()
    {
        using var s = new Sandbox();
        s.Write("home/repo/.labcoat.json", """{"traceFilter": "cat:FromFile"}""");
        var config = s.Load();
        var saved = Environment.GetEnvironmentVariable(LabcoatApp.TraceFilterEnv);
        try
        {
            Environment.SetEnvironmentVariable(LabcoatApp.TraceFilterEnv, null);
            Assert.AreEqual("cat:FromFile", LabcoatApp.ResolveTraceFilter(new StartOptions(), config, out var error));
            Assert.IsNull(error);

            Environment.SetEnvironmentVariable(LabcoatApp.TraceFilterEnv, " cat:FromEnv ");
            Assert.AreEqual("cat:FromEnv", LabcoatApp.ResolveTraceFilter(new StartOptions(), config, out error));
            Assert.IsNull(error);
            Assert.AreEqual("cat:FromCli", LabcoatApp.ResolveTraceFilter(new StartOptions { TraceFilter = "cat:FromCli" }, config, out _));

            Environment.SetEnvironmentVariable(LabcoatApp.TraceFilterEnv, "is:failed");
            Assert.IsNull(LabcoatApp.ResolveTraceFilter(new StartOptions(), config, out error));
            StringAssert.StartsWith(error, "LABCOAT_TRACE_FILTER: 'is:' depends on the test's result");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LabcoatApp.TraceFilterEnv, saved);
        }
    }

    // ------------------------------------------------------------------ test hosts

    [TestMethod]
    [DoNotParallelize]
    public void The_filter_reaches_test_hosts_only_with_the_otlp_endpoint_while_tracing_is_on()
    {
        using (Telemetry.Override(hosted: true, endpoint: "https://localhost:21000"))
        {
            var env = new Dictionary<string, string?> { ["LABCOAT_TRACE_FILTER"] = "cat:Integration", ["LABCOAT_OTLP_FILTER"] = "stale" };
            var removed = Telemetry.ConfigureTestHost(env, tracingEnabled: true, traceFilter: "cat:Integration");
            Assert.AreEqual("cat:Integration", env[Telemetry.FilterVariable]);
            Assert.AreEqual("https://localhost:21000", env["LABCOAT_OTLP_ENDPOINT"]);
            Assert.IsFalse(env.ContainsKey("LABCOAT_TRACE_FILTER"), "Labcoat's own setting");
            CollectionAssert.Contains(removed, "LABCOAT_TRACE_FILTER");

            env = [];
            Telemetry.ConfigureTestHost(env, tracingEnabled: false, traceFilter: "cat:Integration");
            Assert.IsFalse(env.ContainsKey(Telemetry.FilterVariable), "tracing off");

            env = [];
            Telemetry.ConfigureTestHost(env, tracingEnabled: true, traceFilter: " ");
            Assert.IsFalse(env.ContainsKey(Telemetry.FilterVariable), "no filter");

            env = [];
            MtpHost.ConfigureTestHostEnvironment(env, null, tracingEnabled: true, traceFilter: "trait:Kind=Slow");
            Assert.AreEqual("trait:Kind=Slow", env[Telemetry.FilterVariable]);
        }
        using (Telemetry.Override(hosted: true, endpoint: null))
        {
            var env = new Dictionary<string, string?>();
            Telemetry.ConfigureTestHost(env, tracingEnabled: true, traceFilter: "cat:Integration");
            Assert.IsFalse(env.ContainsKey(Telemetry.FilterVariable), "no OTLP endpoint known: nothing to filter");
        }
    }

    // ------------------------------------------------------------------ state and diagnostics

    [TestMethod]
    public async Task The_state_and_the_diagnostics_show_the_filter()
    {
        await using (var explorer = new Explorer(new EventHub(), [], null) { TraceFilter = "cat:Integration" })
            Assert.AreEqual("cat:Integration", explorer.Snapshot().TracingFilter);
        await using (var explorer = new Explorer(new EventHub(), [], null))
            Assert.IsNull(explorer.Snapshot().TracingFilter);

        Assert.AreEqual("cat:Integration", new DiagnosticsSettings { TraceFilter = "cat:Integration" }.ToDictionary()["traceFilter"]);
        Assert.IsFalse(new DiagnosticsSettings().ToDictionary().ContainsKey("traceFilter"));
    }
}
