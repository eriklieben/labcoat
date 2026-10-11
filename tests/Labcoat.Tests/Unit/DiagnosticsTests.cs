using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Labcoat;
using Labcoat.Tests.Api;
using Microsoft.Extensions.Logging;

namespace Labcoat.Tests.Unit;

/// <summary>The UI language of child processes, the host log (level, file, rotation) and the diagnostics report.</summary>
[TestClass]
public class DiagnosticsTests
{
    public TestContext TestContext { get; set; }

    // ------------------------------------------------------------------ UI language

    [TestMethod]
    public void The_test_host_runs_in_the_users_ui_language()
    {
        var env = new Dictionary<string, string?> { ["DOTNET_CLI_UI_LANGUAGE"] = "de", ["LANG"] = "de_DE.UTF-8" };
        MtpHost.ConfigureTestHostEnvironment(env, new Dictionary<string, string> { ["EXTRA"] = "1" }, tracingEnabled: false);
        Assert.AreEqual("de", env["DOTNET_CLI_UI_LANGUAGE"], "inherited as under dotnet test");
        Assert.AreEqual("de_DE.UTF-8", env["LANG"]);
        Assert.AreEqual("1", env["LABCOAT_TEST_HOST"]);
        Assert.AreEqual("1", env["EXTRA"]);

        var none = new Dictionary<string, string?>();
        MtpHost.ConfigureTestHostEnvironment(none, null, tracingEnabled: false);
        Assert.IsFalse(none.ContainsKey("DOTNET_CLI_UI_LANGUAGE"), "Labcoat doesn't force English on the tests");
    }

    [TestMethod]
    public void Labcoats_own_msbuild_calls_run_in_english()
    {
        var psi = ProjectDiscovery.EvaluationStartInfo(Path.Combine(Path.GetTempPath(), "P.csproj"));
        Assert.AreEqual("en", psi.Environment["DOTNET_CLI_UI_LANGUAGE"]);
        Assert.AreEqual("msbuild", psi.ArgumentList[0]);
    }

    [TestMethod]
    public async Task Builds_and_the_help_probe_run_in_english()
    {
        // The explorer's dotnet calls (build, msbuild -getProperty, the --help coverage probe) share one helper. MSBuild
        // exposes environment variables as properties: ask it for the UI language the call got.
        var dir = Directory.CreateTempSubdirectory("labcoat-lang-").FullName;
        try
        {
            var project = Path.Combine(dir, "P.proj");
            File.WriteAllText(project, "<Project />");
            var dotnet = typeof(Explorer).GetMethod("DotnetAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var task = (Task<(int ExitCode, string Output)>)dotnet.Invoke(null, [new[] { "msbuild", project, "-getProperty:DOTNET_CLI_UI_LANGUAGE" }, dir, TestContext.CancellationToken])!;
            var (code, output) = await task;
            Assert.AreEqual(0, code, output);
            Assert.AreEqual("en", output.Trim());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------ host log

    [TestMethod]
    public void Log_levels_parse_by_name()
    {
        Assert.AreEqual(LogLevel.Debug, HostLog.ParseLevel("Debug"));
        Assert.AreEqual(LogLevel.Information, HostLog.ParseLevel("info"));
        Assert.AreEqual(LogLevel.Information, HostLog.ParseLevel(" information "));
        Assert.AreEqual(LogLevel.Warning, HostLog.ParseLevel("warn"));
        Assert.AreEqual(LogLevel.Trace, HostLog.ParseLevel("trace"));
        Assert.AreEqual(LogLevel.Error, HostLog.ParseLevel("error"));
        Assert.AreEqual(LogLevel.Critical, HostLog.ParseLevel("CRITICAL"));
        Assert.AreEqual(LogLevel.None, HostLog.ParseLevel("none"));
        Assert.IsNull(HostLog.ParseLevel("loud"));
        Assert.IsNull(HostLog.ParseLevel(null));
        Assert.AreEqual(Path.Combine("/data/ws", "labcoat.log"), HostLog.ResolveFile("AUTO", "/data/ws"));
        Assert.AreEqual(Path.GetFullPath("x.log"), HostLog.ResolveFile("x.log", "/data/ws"));
    }

    [TestMethod]
    public void The_log_file_is_rotated_at_its_size_cap_and_keeps_recent_lines()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-log-").FullName;
        try
        {
            var file = Path.Combine(dir, "sub", "labcoat.log");
            using (var log = new HostLog(LogLevel.Information, file, maxFileBytes: 400))
            {
                for (var i = 0; i < 20; i++) log.Write("info", $"line {i:00} " + new string('x', 20));
                log.Write(new LogEvent("P", "warning", "from the explorer", "2026-10-10T00:00:00Z"));
                Assert.IsLessThanOrEqualTo(400L, new FileInfo(file).Length);
                Assert.IsTrue(File.Exists(file + ".1"));
                var recent = log.Recent(2);
                Assert.HasCount(2, recent);
                Assert.AreEqual("2026-10-10T00:00:00Z warning [P] from the explorer", recent[1]);
                Assert.HasCount(21, log.Recent(1000));
            }

            var aspnet = Path.Combine(dir, "aspnet.log");
            using (var log = new HostLog(LogLevel.Information, aspnet))
            {
                var logger = log.CreateLogger("Microsoft.AspNetCore.Test");
                Assert.IsFalse(logger.IsEnabled(LogLevel.Debug));
                Assert.IsTrue(logger.IsEnabled(LogLevel.Information));
                Assert.IsFalse(logger.IsEnabled(LogLevel.None));
                Assert.IsNull(logger.BeginScope("scope"));
                logger.LogDebug("not logged");
                logger.LogError(new InvalidOperationException("boom"), "failed {What}", "thing");
                foreach (var level in new[] { LogLevel.Information, LogLevel.Warning, LogLevel.Critical }) logger.Log(level, "level {Level}", level);
            }
            var tail = HostLog.Tail(aspnet, 20);
            Assert.IsTrue(tail.Any(l => l.Contains("error   Microsoft.AspNetCore.Test: failed thing")), string.Join("\n", tail));
            Assert.IsTrue(tail.Any(l => l.Contains("critical Microsoft.AspNetCore.Test: level Critical")), string.Join("\n", tail));
            Assert.DoesNotContain("not logged", File.ReadAllText(aspnet));
            Assert.HasCount(2, HostLog.Tail(aspnet, 2));
            Assert.IsEmpty(HostLog.Tail(Path.Combine(dir, "missing.log"), 5));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void An_unwritable_log_file_is_one_warning()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-log-").FullName;
        var savedErr = Console.Error;
        var err = new StringWriter();
        try
        {
            var blocked = Path.Combine(dir, "file");
            File.WriteAllText(blocked, "");
            Console.SetError(err);
            using var log = new HostLog(LogLevel.Warning, Path.Combine(blocked, "labcoat.log")); // a file as its directory
            log.Write("info", "a");
            log.Write("info", "b");
            Assert.HasCount(2, log.Recent(10));
        }
        finally
        {
            Console.SetError(savedErr);
            Directory.Delete(dir, recursive: true);
        }
        Assert.AreEqual(1, err.ToString().Split("Could not write the log file").Length - 1, err.ToString());
    }

    [TestMethod]
    public void The_event_hub_reports_every_log_line()
    {
        var hub = new EventHub();
        var seen = new List<string>();
        hub.Logged += e => seen.Add(e.Message);
        hub.Publish(new LogEvent(null, "info", "hello", Json.Now()));
        hub.Publish(new PendingRunEvent(null));
        CollectionAssert.AreEqual(new[] { "hello" }, seen);
    }

    // ------------------------------------------------------------------ the report

    [TestMethod]
    public void Secrets_never_reach_the_report()
    {
        var env = new Dictionary<string, string?>
        {
            ["LABCOAT_TOKEN"] = "tok-123456789",
            ["LABCOAT_OTLP_HEADERS"] = "x-otlp-api-key=hdr-abcdefgh",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=Bearer otel-secret-1",
            ["LABCOAT_DASHBOARD_API_KEY"] = "dash-key-xyz",
            ["LABCOAT_NAME"] = "shop",
            ["ASPNETCORE_URLS"] = "http://user:pass@localhost:4300",
            ["PWD"] = "/home/me/work",
            ["UNRELATED_SECRET"] = "not-listed-at-all",
        };
        var selected = Diagnostics.SelectedEnvironment(env);
        Assert.AreEqual("(set, redacted)", selected["LABCOAT_TOKEN"]);
        Assert.AreEqual("(set, redacted)", selected["LABCOAT_OTLP_HEADERS"]);
        Assert.AreEqual("(set, redacted)", selected["OTEL_EXPORTER_OTLP_HEADERS"]);
        Assert.AreEqual("(set, redacted)", selected["LABCOAT_DASHBOARD_API_KEY"]);
        Assert.AreEqual("shop", selected["LABCOAT_NAME"]);
        Assert.AreEqual("http://***@localhost:4300", selected["ASPNETCORE_URLS"]);
        Assert.IsFalse(selected.ContainsKey("PWD"));
        Assert.IsFalse(selected.ContainsKey("UNRELATED_SECRET"));
        Assert.AreEqual("a https://***@h/x and ftp://plain/ y", Diagnostics.RedactUrlCredentials("a https://u:p@h/x and ftp://plain/ y"));
        Assert.AreEqual("no url", Diagnostics.RedactUrlCredentials("no url"));
        Assert.IsTrue(Diagnostics.IsSecretName("my_api_key"));
        Assert.IsFalse(Diagnostics.IsSecretName("LABCOAT_NAME"));
    }

    [TestMethod]
    public async Task The_report_lists_versions_projects_settings_and_the_log_without_secrets()
    {
        var home = Directory.CreateTempSubdirectory("labcoat-home-").FullName;
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(home, "repo")).FullName;
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var projectDir = Directory.CreateDirectory(Path.Combine(repo, "tests", "Shop.Tests")).FullName;
            var project = Path.Combine(projectDir, "Shop.Tests.csproj");
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup></Project>""");
            Directory.CreateDirectory(Path.Combine(projectDir, "obj"));
            File.WriteAllText(Path.Combine(projectDir, "obj", "project.assets.json"), """
                {
                  "version": 3,
                  "libraries": {
                    "MSTest.TestFramework/4.0.2": { "type": "package" },
                    "Microsoft.Testing.Platform/2.0.2": { "type": "package" },
                    "Microsoft.Testing.Extensions.CodeCoverage/18.1.0": { "type": "package" },
                    "Newtonsoft.Json/13.0.3": { "type": "package" },
                    "Shop/1.0.0": { "type": "project" }
                  },
                  "project": { "frameworks": { "net9.0": {}, "net10.0": {} } }
                }
                """);
            var notRestored = Path.Combine(Directory.CreateDirectory(Path.Combine(repo, "tests", "New.Tests")).FullName, "New.Tests.csproj");
            File.WriteAllText(notRestored, """<Project Sdk="MSTest.Sdk/4.1.0"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

            var settings = new DiagnosticsSettings
            {
                Watch = true, AffectedBase = "origin/develop", CoverageOptions = new CoverageOptions { Settings = "cov.xml", Include = ["Shop.*"], Exclude = ["**/Gen/**"] },
                UiDefaults = new JsonObject { ["theme"] = "dracula" }, SettingsFiles = [".labcoat.json"], LogLevel = LogLevel.Debug,
                LogFile = Path.Combine(home, "labcoat.log"), Url = "http://localhost:4300",
            };
            var report = await Diagnostics.CollectAsync(new Diagnostics.Input
            {
                Projects = [project, notRestored],
                Live = [new TestProject { Id = "Shop.Tests", Name = "Shop.Tests", Path = project, Status = ProjectStatus.Building }],
                Settings = settings.ToDictionary(),
                Log = ["info    started", "warning token tok-123456789 leaked", "header hdr-abcdefgh leaked", $"path {repo}/x"],
                LogSource = "this session",
                Environment = new Dictionary<string, string?> { ["LABCOAT_TOKEN"] = "tok-123456789", ["LABCOAT_OTLP_HEADERS"] = "k=hdr-abcdefgh", ["CI"] = "true" },
                Home = home,
            }, TestContext.CancellationToken);

            Assert.AreEqual(Cli.Version, report.LabcoatVersion);
            Assert.AreEqual("git", report.WorkspaceKind);
            Assert.AreEqual("~" + Path.DirectorySeparatorChar + "repo", report.Workspace);
            var shop = report.Projects[0];
            Assert.AreEqual("tests/Shop.Tests/Shop.Tests.csproj", shop.Path);
            Assert.AreEqual("Building", shop.Status);
            CollectionAssert.AreEqual(new[] { "net9.0", "net10.0" }, shop.TargetFrameworks);
            Assert.AreEqual("MSTest 4.0.2", shop.TestFramework);
            Assert.AreEqual("2.0.2", shop.TestingPlatform);
            CollectionAssert.AreEqual(new[] { "Microsoft.Testing.Extensions.CodeCoverage", "Microsoft.Testing.Platform", "MSTest.TestFramework" },
                shop.Packages.Select(p => p.Name).ToArray());
            var fresh = report.Projects[1];
            Assert.IsNull(fresh.Status);
            CollectionAssert.AreEqual(new[] { "net10.0" }, fresh.TargetFrameworks, "from the project file");
            Assert.AreEqual("MSTest 4.1.0", fresh.TestFramework, "MSTest.Sdk");
            StringAssert.Contains(fresh.Note, "not restored");

            var md = Diagnostics.ToMarkdown(report);
            StringAssert.Contains(md, "### Labcoat diagnostics");
            StringAssert.Contains(md, $"| Labcoat | {Cli.Version}");
            StringAssert.Contains(md, "| Shop.Tests (`tests/Shop.Tests/Shop.Tests.csproj`) | net9.0, net10.0 | MSTest 4.0.2 | 2.0.2 | Building |");
            StringAssert.Contains(md, "Shop.Tests: Microsoft.Testing.Extensions.CodeCoverage 18.1.0, Microsoft.Testing.Platform 2.0.2, MSTest.TestFramework 4.0.2");
            StringAssert.Contains(md, "affectedBase: origin/develop");
            StringAssert.Contains(md, "coverage.include: Shop.*");
            StringAssert.Contains(md, "logLevel: Debug");
            StringAssert.Contains(md, "logFile: ~");
            StringAssert.Contains(md, "LABCOAT_TOKEN=(set, redacted)");
            StringAssert.Contains(md, "CI=true");
            StringAssert.Contains(md, "warning token *** leaked");
            StringAssert.Contains(md, "path ~");
            Assert.DoesNotContain("tok-123456789", md);
            Assert.DoesNotContain("hdr-abcdefgh", md);
            Assert.DoesNotContain(home, md);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [TestMethod]
    public void How_labcoat_was_started_is_named()
    {
        var none = new Dictionary<string, string?>();
        Assert.AreEqual("Aspire (Labcoat.Aspire.Hosting)", Diagnostics.HowStarted(new Dictionary<string, string?> { ["ASPIRE_DASHBOARD_URL"] = "https://d" }, "/x/"));
        Assert.AreEqual("Aspire (Labcoat.Aspire.Hosting)", Diagnostics.HowStarted(new Dictionary<string, string?> { ["LABCOAT_APPHOST_UI_SETTINGS"] = "{}" }, "/x/"));
        Assert.AreEqual("dotnet tool (global)", Diagnostics.HowStarted(none, "/home/me/.dotnet/tools/.store/labcoat/1.0.0/labcoat/1.0.0/tools/net10.0/any/"));
        Assert.AreEqual("dotnet tool (local) or dnx", Diagnostics.HowStarted(none, @"C:\Users\me\.nuget\packages\labcoat\1.0.0\tools\net10.0\any\"));
        Assert.AreEqual("from source", Diagnostics.HowStarted(none, "/repo/src/Labcoat/bin/Debug/net10.0/"));
        Assert.AreEqual("other (/opt/labcoat/)", Diagnostics.HowStarted(none, "/opt/labcoat/"));
    }

    [TestMethod]
    public void The_test_framework_is_named_by_its_package()
    {
        Assert.AreEqual("xUnit v3 3.1.0", Diagnostics.TestFrameworkOf([new("xunit.v3.core", "3.1.0")]));
        Assert.AreEqual("NUnit 4.4.0", Diagnostics.TestFrameworkOf([new("NUnit", "4.4.0")]));
        Assert.AreEqual("TUnit 0.70.0", Diagnostics.TestFrameworkOf([new("TUnit.Core", "0.70.0")]));
        Assert.IsNull(Diagnostics.TestFrameworkOf([new("Microsoft.Testing.Platform", "2.0.0")]));
    }

    [TestMethod]
    public async Task Get_api_diagnostics_returns_the_report_and_its_markdown()
    {
        await using var host = await ApiHost.StartAsync();
        host.Explorer.Log(null, "info", "a line for the report");
        var json = await host.GetJson("/api/diagnostics");
        var markdown = (string)json["markdown"]!;
        StringAssert.Contains(markdown, "### Labcoat diagnostics");
        StringAssert.Contains(markdown, "a line for the report");
        StringAssert.Contains(markdown, "| A.Tests (`");
        Assert.AreEqual(Cli.Version, (string?)json["report"]!["labcoatVersion"]);
        Assert.AreEqual("A.Tests", (string?)json["report"]!["projects"]![0]!["id"]);
        Assert.AreEqual("this session", (string?)json["report"]!["logSource"]);
        Assert.AreEqual("off", (string?)json["report"]!["settings"]!["watch"]);
    }
}

/// <summary><c>labcoat --diagnostics</c> and the logging options of the command line (process-wide console and environment).</summary>
[TestClass]
[DoNotParallelize]
public class DiagnosticsCliTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void Verbose_log_file_and_diagnostics_options_parse()
    {
        var o = StartOptions.Parse(["-v", "--log-file", "x.log", "--diagnostics"], out var error)!;
        Assert.IsNull(error);
        Assert.IsTrue(o.Verbose);
        Assert.IsTrue(o.Diagnostics);
        Assert.AreEqual("x.log", o.LogFile);
        Assert.AreEqual("auto", StartOptions.Parse(["--verbose", "--log-file=auto"], out _)!.LogFile);
        Assert.IsNull(StartOptions.Parse(["--log-file"], out error));
        Assert.AreEqual("--log-file needs a file (or 'auto').", error);
    }

    [TestMethod]
    public void The_help_names_the_new_options_and_the_prerelease_flag_only_for_a_prerelease()
    {
        Assert.AreEqual(" --prerelease", Cli.PrereleaseFlag("1.1.0-beta.1"));
        Assert.AreEqual("", Cli.PrereleaseFlag("1.0.0"));
        var usage = Cli.Usage;
        var pre = Cli.PrereleaseFlag(Cli.Version);
        StringAssert.Contains(usage, $"dotnet tool install -g Labcoat{pre})");
        StringAssert.Contains(usage, $"dotnet tool install Labcoat{pre},");
        StringAssert.Contains(usage, $"'dnx Labcoat{pre} -- --help'");
        foreach (var text in new[] { "--verbose", "--log-file", "--diagnostics", "LABCOAT_LIVE_OUTPUT", "LABCOAT_LOG_LEVEL", "LABCOAT_LOG_FILE" })
            StringAssert.Contains(usage, text);
    }

    [TestMethod]
    public async Task Diagnostics_prints_the_report_and_starts_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-diag-cli-");
        Directory.CreateDirectory(Path.Combine(dir.FullName, ".git")); // the settings file walk-up stops here
        var savedOut = Console.Out;
        var output = new StringWriter();
        var savedLevel = Environment.GetEnvironmentVariable(HostLog.LevelVariable);
        var savedCopy = Diagnostics.CopyToClipboard;
        var copied = false;
        try
        {
            Diagnostics.CopyToClipboard = _ => copied = true;
            Environment.SetEnvironmentVariable(HostLog.LevelVariable, "loud");
            Console.SetOut(output);
            var log = Path.Combine(dir.FullName, "my.log");
            File.WriteAllText(log, "2026-10-10T00:00:00Z info    an earlier session\n");
            var code = await LabcoatApp.RunAsync([dir.FullName, "--no-user-config", "--diagnostics", "-v", "--log-file", log], TestContext.CancellationToken);
            Assert.AreEqual(0, code);
        }
        finally
        {
            Console.SetOut(savedOut);
            Environment.SetEnvironmentVariable(HostLog.LevelVariable, savedLevel);
            Diagnostics.CopyToClipboard = savedCopy;
            dir.Delete(recursive: true);
        }
        var text = output.ToString();
        StringAssert.Contains(text, "### Labcoat diagnostics");
        StringAssert.Contains(text, "logLevel: Debug");
        StringAssert.Contains(text, "an earlier session");
        StringAssert.Contains(text, "#### Test projects (0)");
        Assert.IsFalse(copied, "redirected output: not copied");
    }

    [TestMethod]
    public async Task Diagnostics_takes_the_log_settings_from_the_environment_and_copies_on_a_terminal()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-diag-env-");
        Directory.CreateDirectory(Path.Combine(dir.FullName, ".git"));
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var output = new StringWriter();
        var errors = new StringWriter();
        var saved = (Environment.GetEnvironmentVariable(HostLog.LevelVariable), Environment.GetEnvironmentVariable(HostLog.FileVariable));
        var (savedCopy, savedCan) = (Diagnostics.CopyToClipboard, Diagnostics.CanCopy);
        string? copied = null;
        try
        {
            Diagnostics.CanCopy = () => true;
            Diagnostics.CopyToClipboard = text => (copied = text) is not null;
            Environment.SetEnvironmentVariable(HostLog.LevelVariable, "error");
            Environment.SetEnvironmentVariable(HostLog.FileVariable, Path.Combine(dir.FullName, "env.log"));
            Console.SetOut(output);
            Console.SetError(errors);
            Assert.AreEqual(0, await LabcoatApp.RunAsync([dir.FullName, "--no-user-config", "--diagnostics"], TestContext.CancellationToken));
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
            Environment.SetEnvironmentVariable(HostLog.LevelVariable, saved.Item1);
            Environment.SetEnvironmentVariable(HostLog.FileVariable, saved.Item2);
            (Diagnostics.CopyToClipboard, Diagnostics.CanCopy) = (savedCopy, savedCan);
            dir.Delete(recursive: true);
        }
        StringAssert.Contains(output.ToString(), "logLevel: Error");
        StringAssert.Contains(output.ToString(), "env.log");
        StringAssert.Contains(output.ToString(), "no log file; start Labcoat with --log-file auto");
        Assert.AreEqual(output.ToString().TrimEnd(), copied!.TrimEnd());
        StringAssert.Contains(errors.ToString(), "Copied to the clipboard");
    }
}
