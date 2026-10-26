using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// Run limits (RunLimits.cs): durations, maxParallelProjects, TestingPlatformCommandLineArguments sorted for server mode,
/// the running-tests tracker (RunningTests.cs), the batch build (BuildBatch.cs), the project watcher and the settings.
/// </summary>
[TestClass]
public class RunLimitsTests
{
    // ------------------------------------------------------------------ durations / numbers

    [TestMethod]
    [DataRow("90", 90.0)]
    [DataRow("1.5", 1.5)]
    [DataRow("500ms", 0.5)]
    [DataRow(" 30s ", 30.0)]
    [DataRow("30 sec", 30.0)]
    [DataRow("5m", 300.0)]
    [DataRow("2min", 120.0)]
    [DataRow("1.5h", 5400.0)]
    [DataRow("0", 0.0)]
    [DataRow("off", 0.0)]
    [DataRow("None", 0.0)]
    public void Durations_parse_seconds_units_and_off(string text, double seconds) =>
        Assert.AreEqual(TimeSpan.FromSeconds(seconds), RunLimits.ParseDuration(text));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("soon")]
    [DataRow("5 days")]
    [DataRow("-3")]
    [DataRow("m")]
    [DataRow("100000h")]
    public void Invalid_durations_are_null(string? text) => Assert.IsNull(RunLimits.ParseDuration(text));

    [TestMethod]
    public void MaxParallel_is_1_to_64_and_durations_describe_themselves()
    {
        Assert.AreEqual(4, RunLimits.ParseMaxParallel(" 4 "));
        Assert.AreEqual(64, RunLimits.ParseMaxParallel("64"));
        Assert.IsNull(RunLimits.ParseMaxParallel("0"));
        Assert.IsNull(RunLimits.ParseMaxParallel("65"));
        Assert.IsNull(RunLimits.ParseMaxParallel("two"));
        Assert.IsNull(RunLimits.ParseMaxParallel(null));
        Assert.IsTrue(RunLimits.DefaultMaxParallel is >= 1 and <= 4);
        Assert.AreEqual("250 ms", RunLimits.Describe(TimeSpan.FromMilliseconds(250)));
        Assert.AreEqual("1.5 s", RunLimits.Describe(TimeSpan.FromSeconds(1.5)));
        Assert.AreEqual("5 min", RunLimits.Describe(TimeSpan.FromMinutes(5)));
        Assert.AreEqual("3 h", RunLimits.Describe(TimeSpan.FromHours(3)));
    }

    // ------------------------------------------------------------------ TestingPlatformCommandLineArguments

    [TestMethod]
    public void TestingPlatformArguments_pass_server_mode_options_skip_conflicting_ones_and_take_the_timeout()
    {
        var a = TestingPlatformArguments.Parse(
            "--settings test.runsettings --filter \"TestCategory!=Slow\" --timeout 5m --report-trx --coverage --coverage-settings c.xml " +
            "--minimum-expected-tests 10 --results-directory out --client-port 1234 --server --list-tests --treenode-filter=/*/*/*/Fast " +
            "--maximum-failed-tests 3 --no-progress stray");
        CollectionAssert.AreEqual(new[]
        {
            "--settings", "test.runsettings", "--filter", "TestCategory!=Slow", "--report-trx", "--treenode-filter=/*/*/*/Fast", "--maximum-failed-tests", "3",
        }, a.Passed);
        Assert.AreEqual(TimeSpan.FromMinutes(5), a.SessionTimeout);
        Assert.AreEqual("5m", a.SessionTimeoutText);
        var skipped = a.Skipped.Select(s => s.Option).ToList();
        CollectionAssert.AreEqual(new[]
        {
            "--coverage", "--coverage-settings c.xml", "--minimum-expected-tests 10", "--results-directory out", "--client-port 1234", "--server",
            "--list-tests", "--no-progress stray",
        }, skipped);
        var line = a.Describe()!;
        StringAssert.StartsWith(line, "TestingPlatformCommandLineArguments: passed to the test host: --settings test.runsettings");
        StringAssert.Contains(line, "--timeout 5m: applied by Labcoat to each run of the project (5 min)");
        StringAssert.Contains(line, "--server (Labcoat starts the test host in server mode itself)");
    }

    [TestMethod]
    public void TestingPlatformArguments_edge_cases()
    {
        Assert.IsNull(TestingPlatformArguments.Parse(null).Describe());
        Assert.IsNull(TestingPlatformArguments.Parse("   ").Describe());
        var bad = TestingPlatformArguments.Parse("--timeout soon positional --retry-failed-tests 2 --diagnostic --seed -5");
        Assert.IsNull(bad.SessionTimeout);
        CollectionAssert.AreEqual(new[] { "--timeout soon positional" }, bad.Skipped.Select(s => s.Option).ToList());
        Assert.AreEqual("not a duration", bad.Skipped[0].Reason);
        CollectionAssert.AreEqual(new[] { "--retry-failed-tests", "2", "--diagnostic", "--seed", "-5" }, bad.Passed);
        var notOption = TestingPlatformArguments.Parse("value");
        Assert.AreEqual(("value", "not an option"), notOption.Skipped.Single());
        CollectionAssert.AreEqual(new[] { "a b", "c", "d'e", "" }, TestingPlatformArguments.Tokenize("'a b' c \"d'e\" \"\""));
    }

    [TestMethod]
    public void TestingPlatformArguments_make_relative_settings_files_absolute_when_they_exist_in_the_project_directory()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-tpa-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "test.runsettings"), "<RunSettings />");
            var a = TestingPlatformArguments.Parse("--settings test.runsettings --config-file=missing.json", dir.FullName);
            CollectionAssert.AreEqual(new[] { "--settings", Path.Combine(dir.FullName, "test.runsettings"), "--config-file=missing.json" }, a.Passed);
            var rooted = Path.Combine(dir.FullName, "x.runsettings");
            CollectionAssert.AreEqual(new[] { "--settings", rooted }, TestingPlatformArguments.Parse($"--settings \"{rooted}\"", dir.FullName).Passed);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ------------------------------------------------------------------ running tests

    [TestMethod]
    public void RunningTests_track_elapsed_time_slow_tests_and_timeouts()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var r = new RunningTests(() => now) { HangThreshold = TimeSpan.FromSeconds(60), TestTimeout = TimeSpan.FromSeconds(120), MaxListed = 2 };
        r.Started("P", "a", "A");
        now = now.AddSeconds(10);
        r.Started("P", "b", "B");
        r.Started("Q", "c", "C");
        Assert.AreEqual(3, r.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(10), r.Elapsed("P", "a"));
        Assert.IsNull(r.Elapsed("P", "zz"));

        var info = r.Info();
        Assert.AreEqual(3, info.Count);
        CollectionAssert.AreEqual(new[] { "a", "b" }, info.Tests.Select(t => t.Id).ToList()); // longest first, at most 2
        Assert.AreEqual(10_000, info.Tests[0].ElapsedMs);
        Assert.IsNull(info.Tests[0].Slow);
        Assert.AreEqual(60_000, info.HangThresholdMs);
        Assert.AreEqual(120_000, info.TestTimeoutMs);
        Assert.IsNotNull(r.Changed());
        Assert.IsNull(r.Changed()); // same list, only time passed
        now = now.AddSeconds(1);
        Assert.IsNull(r.Changed());

        now = now.AddSeconds(55); // a: 66 s
        var (slow, timedOut) = r.Check();
        Assert.AreEqual("A", slow.Single().DisplayName);
        Assert.IsEmpty(timedOut);
        Assert.IsTrue(r.Info().Tests[0].Slow);
        Assert.IsNotNull(r.Changed()); // became slow
        Assert.IsEmpty(r.Check().NewlySlow); // reported once

        r.Finished("Q", "c");
        CollectionAssert.AreEqual(new[] { "b" }, r.OfProject("P").Where(x => x.Id == "b").Select(x => x.Id).ToList());
        now = now.AddSeconds(60); // a: 126 s, b: 116 s
        (slow, timedOut) = r.Check();
        Assert.AreEqual(("P", "a"), (timedOut.Single().ProjectId, timedOut.Single().Id));
        Assert.AreEqual("B", slow.Single().DisplayName);
        Assert.IsTrue(r.IsTimedOut(("P", "a")));
        Assert.IsNull(r.Elapsed("P", "a"));
        r.MarkTimedOut("P", "b");
        Assert.AreEqual(0, r.Count);
        Assert.IsTrue(r.IsTimedOut(("P", "b")));
        r.Reset();
        Assert.IsFalse(r.IsTimedOut(("P", "a")));
    }

    [TestMethod]
    public void RunningTests_without_threshold_or_timeout_never_flag_tests()
    {
        var now = DateTime.UtcNow;
        var r = new RunningTests(() => now) { HangThreshold = TimeSpan.Zero };
        r.Started("P", "a", "A");
        now = now.AddHours(2);
        var (slow, timedOut) = r.Check();
        Assert.IsEmpty(slow);
        Assert.IsEmpty(timedOut);
        Assert.IsNull(r.Info().HangThresholdMs);
        Assert.IsNull(r.Info().TestTimeoutMs);
        Assert.IsNull(r.Info().Tests[0].Slow);
    }

    // ------------------------------------------------------------------ batch build

    [TestMethod]
    public void BuildBatch_writes_a_solution_with_every_project_in_Debug()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-batch-");
        try
        {
            var a = Path.Combine(dir.FullName, "A", "A.csproj");
            var b = Path.Combine(dir.FullName, "B", "B.fsproj");
            var sln = BuildBatch.WriteSolution([a, b], Path.Combine(dir.FullName, "obj", "labcoat"));
            Assert.IsTrue(File.Exists(sln));
            StringAssert.EndsWith(sln, ".sln");
            var text = File.ReadAllText(sln);
            StringAssert.Contains(text, "Microsoft Visual Studio Solution File, Format Version 12.00");
            StringAssert.Contains(text, $"Project(\"{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}\") = \"A\", \"{a}\"");
            StringAssert.Contains(text, $"Project(\"{{F2A71F9B-5D33-465A-A702-920D77279786}}\") = \"B\", \"{b}\"");
            Assert.AreEqual(2, text.Split(".Debug|Any CPU.Build.0 = Debug|Any CPU").Length - 1);
            StringAssert.Contains(BuildBatch.SolutionText(["/x/C.vbproj"]), "{778DAE3C-4631-46EA-AA77-85C1314464D9}");
            // two projects with the same file name: MSBuild rejects duplicate names in a solution
            var twins = BuildBatch.SolutionText(["/ws/a/X.csproj", "/ws/b/X.csproj", "/ws/c/x.csproj"]);
            StringAssert.Contains(twins, "= \"X\", \"/ws/a/X.csproj\"");
            StringAssert.Contains(twins, "= \"X-2\", \"/ws/b/X.csproj\"");
            StringAssert.Contains(twins, "= \"x-3\", \"/ws/c/x.csproj\"");
            // same projects, same file name (deterministic)
            Assert.AreEqual(sln, BuildBatch.WriteSolution([a, b], Path.Combine(dir.FullName, "obj", "labcoat")));
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public void BuildBatch_splits_the_output_per_project_with_the_errors_of_its_references()
    {
        const string a = "/ws/A.Tests/A.Tests.csproj", b = "/ws/B.Tests/B.Tests.csproj", c = "/ws/C.Tests/C.Tests.csproj", lib = "/ws/Lib/Lib.csproj";
        var output = string.Join('\n',
            "  Determining projects to restore...",
            "/ws/Lib/Thing.cs(3,5): error CS1002: ; expected [/ws/Lib/Lib.csproj]",
            "/ws/A.Tests/Warn.cs(1,1): warning CS0168: unused [/ws/A.Tests/A.Tests.csproj::TargetFramework=net10.0]",
            "  B.Tests -> /ws/B.Tests/bin/Debug/net10.0/B.Tests.dll",
            "/ws/C.Tests/X.cs(9,9): error CS0103: nope [/ws/C.Tests/C.Tests.csproj]",
            "  C.Tests -> /ws/C.Tests/bin/Debug/net10.0/C.Tests.dll", // printed, but C itself reported an error
            "Build FAILED.");
        IReadOnlyCollection<string> Closure(string p) => p == a ? [a, lib] : [p];
        var split = BuildBatch.Split(output, 1, [a, b, c], Closure);
        Assert.IsFalse(split[a].Succeeded);
        StringAssert.Contains(split[a].Output, "error CS1002");
        StringAssert.Contains(split[a].Output, "warning CS0168");
        Assert.DoesNotContain("CS0103", split[a].Output);
        Assert.IsTrue(split[b].Succeeded);
        Assert.AreEqual("", split[b].Output);
        Assert.IsFalse(split[c].Succeeded);
        StringAssert.Contains(split[c].Output, "CS0103");
        Assert.DoesNotContain("CS1002", split[c].Output);

        // exit code 0: everything built
        Assert.IsTrue(BuildBatch.Split(output, 0, [a, b, c], Closure).Values.All(r => r.Succeeded));
        // a failure without any diagnostic about the project: the whole output; an error naming no project counts for all
        var global = BuildBatch.Split("MSBUILD : error MSB1009: Project file does not exist.\nother", 1, [a], Closure);
        Assert.IsFalse(global[a].Succeeded);
        StringAssert.Contains(global[a].Output, "MSB1009");
        var nothing = BuildBatch.Split("killed", 137, [b], Closure);
        Assert.AreEqual("killed", nothing[b].Output);
        Assert.AreEqual("/ws/x.csproj", BuildOutputLine("error X [/ws/x.csproj::TargetFramework=net10.0]"));
        Assert.IsNull(BuildOutputLine("[not a project]"));
        Assert.IsNull(BuildOutputLine("no bracket"));
    }

    private static string? BuildOutputLine(string line) => BuildBatch.ProjectOfLine(line);

    // ------------------------------------------------------------------ project watcher

    [TestMethod]
    [DataRow("src/A.Tests/A.Tests.csproj", true)]
    [DataRow("B.fsproj", true)]
    [DataRow("C.vbproj", true)]
    [DataRow("All.slnx", true)]
    [DataRow("All.sln", true)]
    [DataRow("All.slnf", true)]
    [DataRow("Directory.Build.props", true)]
    [DataRow("src/Directory.Packages.props", true)]
    [DataRow("Directory.Build.targets", true)]
    [DataRow("src/A/Code.cs", false)]
    [DataRow("src/A/obj/A.csproj.nuget.g.props", false)]
    [DataRow("src/A/obj/labcoat/labcoat-build-1.sln", false)]
    [DataRow("src/A/bin/Debug/x.csproj", false)]
    [DataRow(".git/x.sln", false)]
    [DataRow("node_modules/p/p.csproj", false)]
    [DataRow("Other.props", false)]
    public void The_project_watcher_reports_project_solution_and_directory_props_files(string path, bool relevant) =>
        Assert.AreEqual(relevant, ProjectWatcher.IsRelevant(path));

    [TestMethod]
    public void Solutions_and_props_affect_every_project()
    {
        Assert.IsTrue(ProjectWatcher.AffectsAll("/x/All.slnx"));
        Assert.IsTrue(ProjectWatcher.AffectsAll("/x/Directory.Build.props"));
        Assert.IsFalse(ProjectWatcher.AffectsAll("/x/A.csproj"));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task The_project_watcher_reports_a_burst_once_and_sees_a_new_project_file()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-watch-");
        var bursts = new List<IReadOnlyCollection<string>>();
        var warnings = new List<string>();
        using (var w = new ProjectWatcher([dir.FullName, Path.Combine(dir.FullName, "missing")], c => { lock (bursts) bursts.Add(c); }, warnings.Add)
               { Quiet = TimeSpan.FromMilliseconds(300), MaxDelay = TimeSpan.FromSeconds(5) })
        {
            Assert.AreEqual(1, w.WatchedRoots);
            // a branch switch: many changes at once → one report
            for (var i = 0; i < 20; i++) w.OnChange(Path.Combine(dir.FullName, $"P{i}", $"P{i}.csproj"));
            w.OnChange(Path.Combine(dir.FullName, "src", "Code.cs")); // not relevant
            var made = Directory.CreateDirectory(Path.Combine(dir.FullName, "Made.Tests")).FullName;
            Directory.CreateDirectory(Path.Combine(dir.FullName, "obj"));
            w.OnChange(made, structural: true); // a new folder may hold a project whose file event was missed
            w.OnChange(made); // a folder's write time: nothing
            w.OnChange(Path.Combine(dir.FullName, "Gone"), structural: true); // deleted: its files report themselves
            w.OnChange(Path.Combine(dir.FullName, "obj"), structural: true); // ignored folder
            await WaitUntilAsync(() => { lock (bursts) return bursts.Count == 1; });
            await Task.Delay(500);
            lock (bursts)
            {
                Assert.HasCount(1, bursts);
                Assert.Contains(made, bursts[0]); // 20 projects + the new folder (its real event may add it once more: a set)
                Assert.HasCount(21, bursts[0]);
            }

            // a real file system event
            Directory.CreateDirectory(Path.Combine(dir.FullName, "New.Tests"));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "New.Tests", "New.Tests.csproj"), "<Project />");
            await WaitUntilAsync(() => { lock (bursts) return bursts.Count == 2; });
            lock (bursts) Assert.IsTrue(bursts[1].Any(p => p.Contains("New.Tests", StringComparison.Ordinal)), string.Join(", ", bursts[1]));
        }
        Assert.IsEmpty(warnings);
        dir.Delete(recursive: true);
    }

    [TestMethod]
    public async Task The_project_watcher_reports_at_the_latest_after_MaxDelay_and_nothing_after_dispose()
    {
        var bursts = 0;
        var w = new ProjectWatcher([], _ => Interlocked.Increment(ref bursts)) { Quiet = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromMilliseconds(200) };
        w.OnChange("/x/A.csproj");
        await WaitUntilAsync(() => Volatile.Read(ref bursts) == 1);
        w.Dispose();
        w.OnChange("/x/B.csproj");
        w.Dispose(); // twice is fine
        await Task.Delay(300);
        Assert.AreEqual(1, Volatile.Read(ref bursts));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("condition not met in time");
            await Task.Delay(50);
        }
    }

    // ------------------------------------------------------------------ settings

    [TestMethod]
    public void Command_line_options_set_the_run_limits()
    {
        var o = StartOptions.Parse(["--max-parallel", "2", "--hang-threshold=90s", "--test-timeout", "5m"], out var error)!;
        Assert.IsNull(error);
        Assert.AreEqual((2, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5)), (o.MaxParallel, o.HangThreshold, o.TestTimeout));
        Assert.AreEqual(TimeSpan.Zero, StartOptions.Parse(["--test-timeout", "off"], out _)!.TestTimeout);
        Assert.IsNull(StartOptions.Parse(["--max-parallel", "0"], out error));
        StringAssert.Contains(error, "--max-parallel needs a number from 1 to 64");
        Assert.IsNull(StartOptions.Parse(["--test-timeout", "later"], out error));
        StringAssert.Contains(error, "--test-timeout needs seconds or a duration");
        Assert.IsNull(StartOptions.Parse(["--hang-threshold"], out error));
        StringAssert.Contains(error, "--hang-threshold");
    }

    [TestMethod]
    public void The_duration_budget_is_a_run_option()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), StartOptions.Parse(["--run", "--max-test-duration", "500ms"], out var error)!.MaxTestDuration);
        Assert.IsNull(error);
        Assert.AreEqual(TimeSpan.FromSeconds(2), StartOptions.Parse(["--run", "--max-test-duration=2s"], out _)!.MaxTestDuration);
        Assert.IsNull(StartOptions.Parse(["--run", "--max-test-duration", "slow"], out error));
        StringAssert.Contains(error, "--max-test-duration needs seconds or a duration");
        Assert.IsNull(StartOptions.Parse(["--max-test-duration", "2s"], out error));
        StringAssert.Contains(error, "--max-test-duration need --run");
    }

    [TestMethod]
    public void The_duration_budget_comes_from_the_command_line_then_the_environment_then_the_settings_files()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-budget-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, ".git"));
            File.WriteAllText(Path.Combine(dir.FullName, ".labcoat.json"), """{ "maxTestDuration": "5s" }""");
            var config = LabcoatConfig.Load(new LabcoatConfig.Inputs { WorkspaceRoot = dir.FullName, Home = Path.Combine(dir.FullName, "home"), NoUserConfig = true });
            Assert.IsEmpty(config.Warnings);
            Assert.AreEqual(TimeSpan.FromSeconds(5), config.MaxTestDuration);

            var env = new Dictionary<string, string?>();
            Assert.AreEqual(TimeSpan.FromSeconds(5), LabcoatApp.ResolveRunLimits(new StartOptions(), config, n => env.GetValueOrDefault(n)).EffectiveMaxTestDuration);
            env[RunLimits.MaxTestDurationEnv] = "2";
            Assert.AreEqual(TimeSpan.FromSeconds(2), LabcoatApp.ResolveRunLimits(new StartOptions(), config, n => env.GetValueOrDefault(n)).EffectiveMaxTestDuration);
            var cli = StartOptions.Parse(["--run", "--max-test-duration", "off"], out _)!;
            Assert.AreEqual(TimeSpan.Zero, LabcoatApp.ResolveRunLimits(cli, config, n => env.GetValueOrDefault(n)).EffectiveMaxTestDuration);
            Assert.AreEqual(TimeSpan.Zero, RunLimitOptions.Default.EffectiveMaxTestDuration);

            File.WriteAllText(Path.Combine(dir.FullName, ".labcoat.json"), """{ "maxTestDuration": "soon" }""");
            config = LabcoatConfig.Load(new LabcoatConfig.Inputs { WorkspaceRoot = dir.FullName, Home = Path.Combine(dir.FullName, "home"), NoUserConfig = true });
            StringAssert.Contains(config.Warnings.Single(), "'maxTestDuration' must be seconds or a duration");
            Assert.IsNull(config.MaxTestDuration);
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public void Run_limits_come_from_the_command_line_then_the_environment_then_the_settings_files()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-limits-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, ".git"));
            File.WriteAllText(Path.Combine(dir.FullName, ".labcoat.json"),
                """{ "maxParallelProjects": 3, "hangThreshold": "2m", "testTimeout": 30, "port": 4999 }""");
            var config = LabcoatConfig.Load(new LabcoatConfig.Inputs { WorkspaceRoot = dir.FullName, Home = Path.Combine(dir.FullName, "home"), NoUserConfig = true });
            Assert.IsEmpty(config.Warnings);
            Assert.AreEqual((3, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30)), (config.MaxParallelProjects, config.HangThreshold, config.TestTimeout));

            var none = new Dictionary<string, string?>();
            var fromFile = LabcoatApp.ResolveRunLimits(new StartOptions(), config, n => none.GetValueOrDefault(n));
            Assert.AreEqual(new RunLimitOptions(3, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30)), fromFile);

            var env = new Dictionary<string, string?> { [RunLimits.MaxParallelEnv] = "6", [RunLimits.HangThresholdEnv] = "45", [RunLimits.TestTimeoutEnv] = "nope" };
            var fromEnv = LabcoatApp.ResolveRunLimits(new StartOptions(), config, n => env.GetValueOrDefault(n));
            Assert.AreEqual(new RunLimitOptions(6, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30)), fromEnv);
            StringAssert.Contains(config.Warnings.Single(), "LABCOAT_TEST_TIMEOUT: 'nope' is not a duration");

            var cli = StartOptions.Parse(["--max-parallel", "1", "--test-timeout", "0"], out _)!;
            var fromCli = LabcoatApp.ResolveRunLimits(cli, config, n => env.GetValueOrDefault(n));
            Assert.AreEqual((1, TimeSpan.Zero), (fromCli.EffectiveMaxParallel, fromCli.EffectiveTestTimeout));

            Assert.AreEqual((RunLimits.DefaultMaxParallel, RunLimits.DefaultHangThreshold, TimeSpan.Zero),
                (RunLimitOptions.Default.EffectiveMaxParallel, RunLimitOptions.Default.EffectiveHangThreshold, RunLimitOptions.Default.EffectiveTestTimeout));

            File.WriteAllText(Path.Combine(dir.FullName, ".labcoat.json"), """{ "maxParallelProjects": 0, "hangThreshold": true, "testTimeout": "soon" }""");
            config = LabcoatConfig.Load(new LabcoatConfig.Inputs { WorkspaceRoot = dir.FullName, Home = Path.Combine(dir.FullName, "home"), NoUserConfig = true });
            Assert.HasCount(3, config.Warnings);
            Assert.IsNull(config.MaxParallelProjects);
            Assert.IsNull(config.TestTimeout);
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public void Workspace_roots_are_input_folders_and_solution_folders_not_project_files()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-roots-");
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "sub")).FullName;
            var roots = LabcoatApp.WorkspaceRootsOf([dir.FullName, Path.Combine(sub, "All.slnx"), Path.Combine(sub, "A.csproj"), dir.FullName]);
            CollectionAssert.AreEqual(new[] { dir.FullName, sub }, roots);
        }
        finally { dir.Delete(recursive: true); }
    }
}
