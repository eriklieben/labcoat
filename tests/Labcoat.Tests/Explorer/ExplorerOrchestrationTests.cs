using System.Reflection;
using Labcoat;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// Orchestration without real test hosts: run request queueing / merging / cancel, commands, build and evaluation
/// results (through a fake `dotnet`), target framework selection, summaries, coverage lookups and source paths.
/// </summary>
[TestClass]
public class ExplorerOrchestrationTests
{
    public TestContext TestContext { get; set; }

    /// <summary>A temp workspace with one project directory per name (the project files are minimal).</summary>
    private sealed class Workspace : IAsyncDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-orch-");
        public FakeDotnet Dotnet { get; } = new();
        public Labcoat.Explorer Explorer { get; }
        public EventHub Hub { get; } = new();

        public Workspace(string[] names, string projectXml = "<Project Sdk=\"Microsoft.NET.Sdk\" />", IEnumerable<string>? inputRoots = null)
        {
            var paths = names.Select(n =>
            {
                var dir = Directory.CreateDirectory(Path.Combine(Root.FullName, n)).FullName;
                var path = Path.Combine(dir, n + ".csproj");
                File.WriteAllText(path, projectXml);
                return path;
            }).ToList();
            Explorer = new Labcoat.Explorer(Hub, paths, null, inputRoots ?? [Root.FullName]) { DotnetOverride = Dotnet.InvokeAsync };
        }

        public string Dir(string name) => Path.Combine(Root.FullName, name);
        public string Project(string name) => Path.Combine(Root.FullName, name, name + ".csproj");
        public string Logs => ScratchWorkspace.Logs(Explorer);

        public void Seed(string projectId, params (string Id, Outcome Outcome)[] tests) =>
            Explorer.SeedTests(tests.Select(t => new TestNode { Id = t.Id, ProjectId = projectId, DisplayName = "T " + t.Id, Outcome = t.Outcome }));

        public async ValueTask DisposeAsync()
        {
            await Explorer.DisposeAsync();
            try { Root.Delete(recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ run requests

    [TestMethod]
    public async Task Requests_wait_merge_supersede_and_are_cleared_by_cancel_while_an_operation_runs()
    {
        await using var w = new Workspace(["P", "Q"]);
        w.Seed("P", ("a", Outcome.NotRun), ("b", Outcome.NotRun));
        w.Seed("Q", ("c", Outcome.NotRun));
        var e = w.Explorer;
        var hold = await e.HoldOperationAsync("discovery");
        try
        {
            var first = e.RequestRun(null, ["a"]);
            Assert.AreEqual((202, true, false), (first.Status, first.Queued, first.Merged));
            Assert.AreEqual("queued", e.GetRunStatus(first.RunId!)!.State);
            var summary = e.Summary();
            Assert.AreEqual(("discovery", 1, "subset"), (summary.ActiveOperation, summary.PendingRun!.TestCount, summary.PendingRun.Scope));

            // same options: merged into the waiting request, unknown ids are reported but don't fail the request
            var second = e.RequestRun(new() { ProjectId = "P", TestIds = ["b", "zz", ""], Trigger = "user" });
            Assert.AreEqual((true, first.RunId), (second.Merged, second.RunId));
            CollectionAssert.AreEqual(new[] { "zz" }, second.UnknownTestIds);
            Assert.AreEqual((2, 2), (e.Summary().PendingRun!.Requests, e.Summary().PendingRun!.TestCount));

            // "all tests of a project" absorbs the subset
            Assert.IsTrue(e.RequestRun("P", null).Merged);
            Assert.IsNull(e.Summary().PendingRun!.TestCount);
            Assert.AreEqual("all", e.Summary().PendingRun!.Scope);
            Assert.IsTrue(e.RequestRun(null, null).Merged);
            CollectionAssert.AreEquivalent(new[] { "P", "Q" }, e.Summary().PendingRun!.ProjectIds);

            // different options: the new request replaces the waiting one
            var coverage = e.RequestRun(new() { TestIds = ["a"], Coverage = true });
            Assert.IsFalse(coverage.Merged);
            await first.Completion!;
            var superseded = e.GetRunStatus(first.RunId!)!;
            Assert.AreEqual(("superseded", coverage.RunId), (superseded.State, superseded.SupersededBy));
            Assert.IsTrue(e.Summary().PendingRun!.Coverage);

            var repeat = e.RequestRun(new() { TestIds = ["a"], Repeat = Labcoat.Explorer.MaxRepeat + 10 });
            Assert.AreEqual(Labcoat.Explorer.MaxRepeat, e.Summary().PendingRun!.Repeat);
            Assert.Contains($"repeat is limited to {Labcoat.Explorer.MaxRepeat}", w.Logs);

            var debug = e.RequestRun(new() { ProjectId = "P", TestIds = ["a"], Debug = true });
            Assert.AreEqual("debug", e.Summary().PendingRun!.Trigger);
            Assert.AreEqual("superseded", e.GetRunStatus(repeat.RunId!)!.State);
            Assert.Contains("Queued run request replaced by a new one (debug)", w.Logs);

            var cancel = e.Cancel();
            Assert.AreEqual((true, "discovery", debug.RunId, 1), (cancel.Running, cancel.ActiveOperation, cancel.ClearedRunId, cancel.ClearedRequests));
            Assert.AreEqual(("cancelled", "Cancelled while queued."), (e.GetRunStatus(debug.RunId!)!.State, e.GetRunStatus(debug.RunId!)!.Reason));
            Assert.IsNull(e.Summary().PendingRun);
        }
        finally { hold.Dispose(); }

        var idle = e.Cancel();
        Assert.AreEqual((false, (string?)null, (string?)null, 0), (idle.Running, idle.ActiveOperation, idle.ClearedRunId, idle.ClearedRequests));
        Assert.Contains("nothing was running", w.Logs);
        Assert.IsNull(e.GetRunStatus("nope"));
    }

    [TestMethod]
    public async Task Run_requests_are_validated()
    {
        await using var w = new Workspace(["P", "Q"]);
        w.Seed("P", ("a", Outcome.NotRun));
        var e = w.Explorer;
        Assert.AreEqual(404, e.RequestRun("Q", ["a"]).Status); // known test, other project
        Assert.Contains("in project 'Q'", e.RequestRun("Q", ["a"]).Error!);
        Assert.AreEqual(400, e.RequestRun(new() { ProjectId = "P", Debug = true }).Status);
        await e.RunAsync("nope", null);
        Assert.Contains("Run not started: Unknown project 'nope'.", w.Logs);

        await using var none = new Labcoat.Explorer(new EventHub(), [], null);
        var r = none.RequestRun(null, null);
        Assert.AreEqual((404, "No test projects."), (r.Status, r.Error));
    }

    [TestMethod]
    public async Task Watch_requests_merge_changed_files_and_a_user_request_takes_over_the_trigger()
    {
        await using var w = new Workspace(["P"]);
        w.Seed("P", ("a", Outcome.NotRun), ("b", Outcome.NotRun));
        var e = w.Explorer;
        using (await e.HoldOperationAsync("watch rebuild"))
        {
            e.RequestRun(new() { ProjectId = "P", TestIds = ["a"], Affected = new AffectedRun { Strategy = "testFile", ChangedFiles = ["A.cs"] },
                WatchContext = new WatchContext { ChangedFiles = ["A.cs"], Strategy = "testFile", BuildMs = 10 } });
            Assert.AreEqual("watch", e.Summary().PendingRun!.Trigger);
            e.RequestRun(new() { ProjectId = "P", TestIds = ["b"], Affected = new AffectedRun { Strategy = "impactMap", ChangedFiles = ["B.cs", "A.cs"] },
                WatchContext = new WatchContext { ChangedFiles = ["B.cs"], Strategy = "impactMap", BuildMs = 5 } });
            var pending = PendingRequest(e);
            var affected = (AffectedRun)Prop(pending, "Affected")!;
            Assert.AreEqual("mixed", affected.Strategy);
            CollectionAssert.AreEqual(new[] { "A.cs", "B.cs" }, affected.ChangedFiles);
            Assert.AreEqual(15.0, ((WatchContext)Prop(pending, "Watch")!).BuildMs);

            // a user request merged in: no longer "just the affected tests"
            e.RequestRun(new() { ProjectId = "P", TestIds = ["a"], Trigger = "user" });
            Assert.AreEqual("user", e.Summary().PendingRun!.Trigger);
            Assert.IsNull(Prop(PendingRequest(e), "Affected"));
            Assert.IsNull(Prop(PendingRequest(e), "Watch"));
            e.Cancel();
        }

        static object PendingRequest(Labcoat.Explorer e) =>
            typeof(Labcoat.Explorer).GetField("_pendingRun", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!;
        static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);
    }

    [TestMethod]
    public async Task A_run_cancelled_while_building_ends_cancelled_and_a_failed_build_skips_it()
    {
        await using var w = new Workspace(["P"]);
        w.Seed("P", ("a", Outcome.Passed));
        var e = w.Explorer;

        w.Dotnet.BuildHangs = true;
        var r = e.RequestRun(null, ["a"]);
        await ScratchWorkspace.WaitUntilAsync(() => w.Dotnet.Calls.Count > 0, "the build");
        Assert.AreEqual("building", e.GetRunStatus(r.RunId!)!.State);
        Assert.AreEqual(ProjectStatus.Building, e.Snapshot().Projects[0].Status);
        e.Cancel();
        await r.Completion!;
        Assert.AreEqual(("cancelled", "Cancelled while building."), (e.GetRunStatus(r.RunId!)!.State, e.GetRunStatus(r.RunId!)!.Reason));
        Assert.AreEqual(ProjectStatus.Idle, e.Snapshot().Projects[0].Status);

        w.Dotnet.BuildHangs = false;
        w.Dotnet.BuildExitCode = 1;
        w.Dotnet.BuildOutput = "Restore complete\nsomething went wrong";
        r = e.RequestRun(null, ["a"]);
        await r.Completion!;
        Assert.AreEqual("skipped", e.GetRunStatus(r.RunId!)!.State);
        var project = e.Snapshot().Projects[0];
        Assert.AreEqual(("Build failed (exit code 1)", true), (project.LastError, project.ResultsOutdated));
        Assert.AreEqual(Outcome.Passed, e.GetTest("P", "a")!.Outcome); // never queued: keeps its result
    }

    // ------------------------------------------------------------------ build + evaluation

    [TestMethod]
    public async Task Build_errors_are_parsed_and_the_first_error_becomes_the_project_error()
    {
        await using var w = new Workspace(["P"]);
        w.Dotnet.BuildExitCode = 1;
        w.Dotnet.BuildOutput = """
            /src/A.cs(3,5): error CS1002: ; expected [/x/P.csproj]
            /src/B.cs(4,1): warning CS0168: The variable 'e' is declared but never used [/x/P.csproj]
            /src/C.cs(9): error CS0103: The name 'x' does not exist in the current context [/x/P.csproj]
            Build FAILED.
            """;
        await w.Explorer.DiscoverAsync("P");
        var p = w.Explorer.Snapshot().Projects[0];
        Assert.AreEqual(ProjectStatus.Error, p.Status);
        Assert.AreEqual("/src/A.cs(3,5): error CS1002: ; expected", p.LastError);
        Assert.AreEqual((2, 1), (p.BuildErrorCount, p.BuildWarningCount));
        Assert.HasCount(3, p.BuildErrors!);
        Assert.IsNull(p.ResultsOutdated); // nothing had results
        Assert.Contains("Build failed after", w.Logs);
        Assert.Contains("(2 errors, 1 warning)", w.Logs);
        CollectionAssert.AreEqual(new[] { "build", w.Project("P"), "-nologo", "-tl:off", "-v:minimal" }, w.Dotnet.Calls[0]);
    }

    [TestMethod]
    public async Task Evaluation_failures_stop_at_the_build_step()
    {
        await using var w = new Workspace(["P"]);
        w.Dotnet.Evaluation = null;
        await w.Explorer.DiscoverAsync(null);
        Assert.StartsWith("Could not evaluate project: error MSB4025", w.Explorer.Snapshot().Projects[0].LastError);

        // multi-targeted, but the per-framework evaluation fails
        w.Dotnet.Evaluation = FakeDotnet.Eval(null, "net8.0;net10.0");
        await w.Explorer.DiscoverAsync(null);
        Assert.AreEqual("Could not determine TargetPath of the project.", w.Explorer.Snapshot().Projects[0].LastError);

        // no TargetPath and no frameworks at all
        w.Dotnet.Evaluation = FakeDotnet.Eval(null, "");
        await w.Explorer.DiscoverAsync(null);
        Assert.AreEqual("Could not determine TargetPath of the project.", w.Explorer.Snapshot().Projects[0].LastError);
        Assert.AreEqual(ProjectStatus.Error, w.Explorer.Snapshot().Projects[0].Status);
    }

    [TestMethod]
    public async Task Multi_targeted_projects_run_on_the_highest_framework_or_the_chosen_one()
    {
        await using var w = new Workspace(["P"]);
        var e = w.Explorer;
        Assert.AreEqual((404, "Unknown project 'nope'."), e.SetTargetFramework("nope", "net8.0"));
        Assert.AreEqual(409, e.SetTargetFramework("P", "net8.0").Status); // not evaluated yet

        w.Dotnet.Evaluation = FakeDotnet.Eval(null, "net8.0;net10.0");
        w.Dotnet.TargetPathForFramework = Path.Combine(w.Dir("P"), "bin", "P.dll");
        await e.DiscoverAsync(null);
        var p = e.Snapshot().Projects[0];
        Assert.AreEqual("net10.0", p.TargetFramework, w.Logs);
        CollectionAssert.AreEqual(new[] { "net8.0", "net10.0" }, p.AvailableFrameworks);
        Assert.Contains("Multi-targeted project (net8.0, net10.0); running on net10.0 (highest; pick another with setTargetFramework)", w.Logs);
        Assert.IsTrue(w.Dotnet.Calls.Any(c => c.Contains("-p:TargetFramework=net10.0")));

        var (status, error) = e.SetTargetFramework("P", "net9.0");
        Assert.AreEqual(400, status);
        Assert.Contains("net8.0, net10.0", error!);

        var calls = w.Dotnet.Calls.Count;
        Assert.AreEqual(202, e.SetTargetFramework("P", "NET8.0").Status);
        Assert.AreEqual("net8.0", e.Snapshot().Projects[0].TargetFramework);
        await ScratchWorkspace.WaitUntilAsync(() => w.Dotnet.Calls.Skip(calls).Any(c => c.Contains("-p:TargetFramework=net8.0")), "evaluation for net8.0");
        // the build is for the chosen framework only
        Assert.IsTrue(w.Dotnet.Calls.Skip(calls).Any(c => c[0] == "build" && c.SkipWhile(a => a != "-f").Skip(1).FirstOrDefault() == "net8.0"));
        await ScratchWorkspace.WaitUntilAsync(() => e.Summary().ActiveOperation is null, "discovery done");

        // persisted per workspace: a new explorer for the same project uses it
        await using (var again = new Labcoat.Explorer(new EventHub(), [w.Project("P")], null) { DotnetOverride = w.Dotnet.InvokeAsync })
        {
            await again.DiscoverAsync(null);
            Assert.AreEqual("net8.0", again.Snapshot().Projects[0].TargetFramework);
        }

        Assert.AreEqual(202, e.SetTargetFramework("P", null).Status);
        Assert.AreEqual("net10.0", e.Snapshot().Projects[0].TargetFramework);
        Assert.Contains("Target framework: back to the default", w.Logs);
        await ScratchWorkspace.WaitUntilAsync(() => e.Summary().ActiveOperation is null, "discovery done");
    }

    [TestMethod]
    public async Task Discovery_failures_explain_the_output_type_and_a_missing_runner()
    {
        // The output does not exist (its directory does): the test host process exits before it connects.
        static string Dll(Workspace w) => Directory.CreateDirectory(Path.Combine(w.Dir("P"), "bin")).FullName + Path.DirectorySeparatorChar + "P.dll";
        await using (var w = new Workspace(["P"]))
        {
            // no output directory at all: the process can't even start there
            w.Dotnet.Evaluation = FakeDotnet.Eval(Path.Combine(w.Dir("P"), "nowhere", "P.dll"));
            await w.Explorer.DiscoverAsync(null);
            var failed = w.Explorer.Snapshot().Projects[0];
            Assert.StartsWith("Discovery failed: ", failed.Diagnostics![0].Message);
            Assert.AreEqual(ProjectStatus.Error, failed.Status);

            w.Dotnet.Evaluation = FakeDotnet.Eval(Dll(w), properties: new Dictionary<string, string> { ["OutputType"] = "Library" });
            await w.Explorer.DiscoverAsync(null);
            var p = w.Explorer.Snapshot().Projects[0];
            Assert.AreEqual(ProjectStatus.Error, p.Status, w.Logs);
            Assert.StartsWith("Discovery failed: Test host exited with code", p.LastError);
            Assert.AreEqual("cold", p.HostState);
            var messages = p.Diagnostics!.Select(d => d.Message).ToList();
            Assert.StartsWith("The test host exited with code", messages[0]);
            Assert.AreEqual("OutputType is 'Library', but Microsoft.Testing.Platform test projects are executables.", messages[1]);
            Assert.AreEqual("No Microsoft.Testing.Platform runner is enabled for this project.", messages[2]);
        }

        // A runner is enabled (property, package or MSTest.Sdk): only the exit is reported.
        foreach (var (props, packages, xml) in new (Dictionary<string, string>, string[], string)[]
                 {
                     (new() { ["OutputType"] = "Exe", ["EnableMSTestRunner"] = "true" }, [], "<Project />"),
                     (new() { ["OutputType"] = "Exe" }, ["xunit.v3.mtp-v2"], "<Project />"),
                     (new() { ["OutputType"] = "WinExe" }, [], "<Project Sdk=\"MSTest.Sdk/4.0.2\" />"),
                 })
        {
            await using var w = new Workspace(["P"], xml);
            w.Dotnet.Evaluation = FakeDotnet.Eval(Dll(w), properties: props, packages: packages);
            await w.Explorer.DiscoverAsync(null);
            Assert.HasCount(1, w.Explorer.Snapshot().Projects[0].Diagnostics!, w.Logs);
        }
    }

    [TestMethod]
    public async Task Project_references_are_followed_transitively_for_source_roots()
    {
        await using var w = new Workspace(["P", "A", "B", "C"]);
        File.WriteAllText(w.Project("A"), """<Project><ItemGroup><ProjectReference Include="..\B\B.csproj" /><ProjectReference Include="$(Other)/X.csproj" /><ProjectReference Include="" /></ItemGroup></Project>""");
        File.WriteAllText(w.Project("B"), """<Project><ItemGroup><ProjectReference Include="../C/C.csproj" /><ProjectReference Include="../Missing/Missing.csproj" /></ItemGroup></Project>""");
        File.WriteAllText(w.Project("C"), "<Project><ItemGroup><ProjectReference"); // malformed: skipped
        await using var e = new Labcoat.Explorer(new EventHub(), [w.Project("P")], null) { DotnetOverride = w.Dotnet.InvokeAsync };
        w.Dotnet.Evaluation = FakeDotnet.Eval(Path.Combine(w.Dir("P"), "bin", "P.dll"), projectReferences: [w.Project("A")]);
        await e.DiscoverAsync(null);
        var roots = e.Snapshot().SourceRoots!;
        foreach (var name in new[] { "P", "A", "B", "C" })
            CollectionAssert.Contains(roots, PathUtil.Canonicalize(w.Dir(name)), name);
        Assert.IsFalse(roots.Any(r => r.EndsWith("Missing", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ commands

    [TestMethod]
    public async Task Commands_are_dispatched_or_rejected_with_a_status()
    {
        await using var w = new Workspace(["P"]);
        w.Dotnet.BuildExitCode = 1; // background operations end right away
        w.Seed("P", ("a", Outcome.NotRun));
        var e = w.Explorer;

        async Task<int?> Status(ClientCommand c) => (await e.HandleCommandAsync(c) as CommandRejectedEvent)?.Status;

        Assert.AreEqual(404, await Status(new() { Type = "discover", ProjectId = "nope" }));
        Assert.IsNull(await Status(new() { Type = "discover", ProjectId = "P" }));
        var rejected = (CommandRejectedEvent)(await e.HandleCommandAsync(new() { Type = "run", TestIds = ["zz"] }))!;
        Assert.AreEqual(("run", 404), (rejected.Command, rejected.Status));
        CollectionAssert.AreEqual(new[] { "zz" }, rejected.UnknownTestIds);
        Assert.AreEqual(400, await Status(new() { Type = "debug" }));
        Assert.IsNull(await Status(new() { Type = "run", TestIds = ["a"], Repeat = 2 }));
        Assert.IsNull(await Status(new() { Type = "cancel" }));
        Assert.AreEqual(400, await Status(new() { Type = "setWatch" }));
        Assert.IsNull(await Status(new() { Type = "setWatch", Enabled = true }));
        Assert.IsTrue(e.Snapshot().Watch);
        Assert.IsNull(await Status(new() { Type = "setWatch", Enabled = false }));
        Assert.AreEqual(400, await Status(new() { Type = "setCoverage" }));
        Assert.IsNull(await Status(new() { Type = "setCoverage", Enabled = true }));
        Assert.IsTrue(e.Snapshot().CoverageEnabled);
        Assert.AreEqual(404, await Status(new() { Type = "restartHost", ProjectId = "nope" }));
        Assert.IsNull(await Status(new() { Type = "restartHost" }));
        Assert.AreEqual(404, await Status(new() { Type = "buildImpactMap", ProjectId = "nope" }));
        Assert.IsNull(await Status(new() { Type = "buildImpactMap", ProjectId = "P" }));
        Assert.AreEqual(400, await Status(new() { Type = "setTracing" }));
        Assert.IsNull(await Status(new() { Type = "setTracing", Enabled = false }));
        Assert.AreEqual(400, await Status(new() { Type = "setQuarantine", ProjectId = "P" }));
        Assert.IsNull(await Status(new() { Type = "setQuarantine", ProjectId = "P", Id = "a" }));
        CollectionAssert.AreEqual(new[] { "P|a" }, e.Snapshot().Quarantined);
        Assert.IsNull(await Status(new() { Type = "setQuarantine", ProjectId = "P", Id = "a", Quarantined = false }));
        Assert.AreEqual(404, await Status(new() { Type = "setTargetFramework", ProjectId = "nope" }));
        var unknown = (CommandRejectedEvent)(await e.HandleCommandAsync(new() { Type = "bogus" }))!;
        Assert.AreEqual((400, "Unknown command 'bogus'"), (unknown.Status, unknown.Error));
        Assert.Contains("bogus: Unknown command 'bogus'", w.Logs);

        await ScratchWorkspace.WaitUntilAsync(() => e.Summary().ActiveOperation is null && e.Summary().PendingRun is null, "idle");
        Assert.Contains("Impact map built in", w.Logs);
    }

    [TestMethod]
    public async Task Toggles_send_full_state_to_default_clients_and_settings_to_lean_clients()
    {
        await using var w = new Workspace(["P"]);
        var e = w.Explorer;
        await using var legacy = new EventRecorder(w.Hub, e);
        await using var lean = new EventRecorder(w.Hub, e, lean: true);
        await legacy.WaitForAsync("state");
        await lean.WaitForAsync("state", s => s["state"]!["lean"]?.GetValue<bool>() == true);

        e.SetWatch(true);
        e.SetCoverage(true);
        e.SetTracing(true);
        e.SetTracing(true); // persisted, but no change
        e.SetQuarantine("P", "x", true);
        await lean.WaitForAsync("settingsChanged", s => s["settings"]!["quarantined"]?.AsArray().Count == 1);
        var last = lean.Of("settingsChanged")[^1]["settings"]!;
        Assert.IsTrue(last["watch"]!.GetValue<bool>() && last["coverageEnabled"]!.GetValue<bool>() && last["tracingEnabled"]!.GetValue<bool>());
        await legacy.WaitForAsync("state", s => s["state"]!["quarantined"]?.AsArray().Count == 1);
        Assert.IsEmpty(legacy.Of("settingsChanged"));
        Assert.HasCount(5, legacy.Of("state")); // initial + one per change
        Assert.Contains("Trace collection enabled, but no OTLP endpoint is known", w.Logs);

        e.SetTracing(false);
        e.SetWatch(false);
        Assert.Contains("Trace collection disabled", w.Logs);
        Assert.Contains("Watch mode disabled", w.Logs);
    }

    [TestMethod]
    public async Task Watch_mode_rebuilds_after_a_source_change_but_not_for_build_output_or_other_files()
    {
        await using var w = new Workspace(["P"]);
        w.Dotnet.BuildExitCode = 1;
        var e = w.Explorer;
        e.SetWatch(true);
        Directory.CreateDirectory(Path.Combine(w.Dir("P"), "obj"));
        File.WriteAllText(Path.Combine(w.Dir("P"), "obj", "Generated.cs"), "//");
        File.WriteAllText(Path.Combine(w.Dir("P"), "notes.txt"), "hi");
        await Task.Delay(1200, TestContext.CancellationToken);
        Assert.IsFalse(w.Dotnet.Calls.Any(c => c[0] == "build"), "obj/ and .txt changes are ignored");

        File.WriteAllText(Path.Combine(w.Dir("P"), "Code.cs"), "class C { }");
        await ScratchWorkspace.WaitUntilAsync(() => w.Dotnet.Calls.Any(c => c[0] == "build"), "a watch build", 15_000);
        await ScratchWorkspace.WaitUntilAsync(() => w.Logs.Contains("Build failed"), "the build result");
        e.SetWatch(false);
    }

    // ------------------------------------------------------------------ queries

    [TestMethod]
    public async Task Summary_details_history_and_lean_snapshots_come_from_the_tests()
    {
        await using var w = new Workspace(["P", "P2"]);
        var e = w.Explorer;
        w.Seed("P", ("pass", Outcome.Passed), ("fail", Outcome.Failed), ("timeout", Outcome.TimedOut), ("skip", Outcome.Skipped),
            ("run", Outcome.Running), ("queue", Outcome.Queued), ("cancel", Outcome.Cancelled), ("none", Outcome.NotRun));
        e.SeedTests([new TestNode
        {
            Id = "out", ProjectId = "P2", DisplayName = "Out", StdOut = "hello", Outcome = Outcome.Passed,
            History = [new HistoryEntry(Outcome.Passed, 1, "t", "r1")], Flaky = true,
        }]);

        var s = e.Summary();
        Assert.AreEqual(new Labcoat.Explorer.OutcomeCounts(9, 2, 2, 1, 1, 1, 1, 1), s.Tests);
        Assert.AreEqual((8, 1), (s.Projects[0].Tests, s.Projects[1].Tests));
        Assert.AreEqual(Cli.Version, s.Version);
        Assert.IsFalse(e.IsRunning);

        Assert.AreEqual("Out", e.GetTest(null, "out")!.DisplayName);
        Assert.IsNull(e.GetTest("P", "out"));
        var history = e.GetHistory(null, null);
        Assert.AreEqual(("out", true), (history.Single().Id, history.Single().Flaky));
        Assert.HasCount(1, e.GetHistory("P", "pass")); // asked for by id: listed even without history
        Assert.IsEmpty(e.GetHistory("P", "nope"));
        Assert.AreEqual(("hello", false), (e.GetOutput(null, "out")!.StdOut, e.GetOutput(null, "out")!.Truncated));

        var lean = e.Snapshot(lean: true);
        Assert.IsTrue(lean.Lean);
        var leanOut = lean.Tests.Single(t => t.Id == "out");
        Assert.AreEqual((null, (List<HistoryEntry>?)null, true), (leanOut.StdOut, leanOut.History, leanOut.HasOutput));
        Assert.AreEqual("hello", e.Snapshot().Tests.Single(t => t.Id == "out").StdOut);
        Assert.IsNull(e.Snapshot().Logs);
        Assert.IsNotNull(e.Snapshot(includeLogs: true).Logs);
        Assert.IsTrue(e.HasProject("P2"));
        Assert.IsFalse(e.HasProject("nope"));
        Assert.IsEmpty(e.Runs());
        Assert.IsNull(e.GetRun("nope"));
        e.ReleaseConsole();
        e.ReleaseConsole(); // once only
        e.Log("P", "error", "printed directly now");
    }

    [TestMethod]
    public async Task Projects_with_the_same_name_get_distinct_ids()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-ids-");
        try
        {
            var a = Path.Combine(root.FullName, "a", "Same.csproj");
            var b = Path.Combine(root.FullName, "b", "Same.csproj");
            await using var e = new Labcoat.Explorer(new EventHub(), [a, b], "http://dash", workspaceName: "WS");
            CollectionAssert.AreEqual(new[] { "Same", "Same-2" }, e.Snapshot().Projects.Select(p => p.Id).ToArray());
            Assert.AreEqual(("WS", "http://dash"), (e.Snapshot().WorkspaceName, e.Snapshot().AspireDashboardUrl));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task Covering_tests_combine_cached_per_test_coverage_with_the_impact_map()
    {
        await using var w = new Workspace(["P"]);
        var e = w.Explorer;
        w.Seed("P", ("t1", Outcome.Passed), ("t2", Outcome.Passed), ("t3", Outcome.Passed));
        var src = Path.Combine(w.Dir("P"), "Lib.cs");
        File.WriteAllText(src, "a\nb\nc\nd\ne\n");
        var dll = Path.Combine(w.Dir("P"), "bin", "P.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllText(dll, "x");
        e.SetTargetPath("P", dll);

        static CoverageReport Report(string file, string testId) => new()
        {
            RunId = "r", GeneratedAt = "now", ProjectIds = ["P"], Scope = "subset", TestIds = [testId],
            Files = [new FileCoverage { Path = file, Module = "Lib", Lines = [new() { Line = 3, Hits = 2 }, new() { Line = 4, Hits = 0 }] }],
        };
        lock (e.Sync)
        {
            e.CoverageCacheState.Store("P", Report(src, "t1"), "an older build");
            e.CoverageCacheState.Store("P", Report(src, "gone"), "an older build"); // a test that no longer exists
            e.ImpactMapState.Record("P", ["t2", "t1"], new Dictionary<string, List<LineSpan>> { [PathUtil.Canonicalize(src)] = [new(1, 5)] });
        }

        var line3 = e.CoveringTests(src, 3);
        Assert.HasCount(2, line3.Tests);
        Assert.AreEqual(new CoveringTest("P", "t1", "perTest", 2, true), line3.Tests[0]);
        Assert.AreEqual(new CoveringTest("P", "t2", "impactMap"), line3.Tests[1]);
        Assert.IsTrue(line3.Partial);
        // line 4 was not hit in t1's own coverage: both come from the (method-granular) impact map, in test order
        CollectionAssert.AreEqual(new[] { "t1", "t2" }, e.CoveringTests(src, 4).Tests.Select(t => t.Id).ToArray());
        Assert.IsTrue(e.CoveringTests(src, 4).Tests.All(t => t.Source == "impactMap"));
        Assert.IsEmpty(e.CoveringTests(Path.Combine(w.Dir("P"), "Other.cs"), 3).Tests);

        Assert.IsTrue(e.GetCachedTestCoverage(null, ["t1"])!.Stale);
        Assert.IsTrue(e.GetCachedTestCoverage("P", ["t1"])!.Stale);
        Assert.IsNull(e.GetCachedTestCoverage("P", ["t1", "t2"]));
        e.SetTargetPath("P", null);
        Assert.IsTrue(e.GetCachedTestCoverage(null, ["t1"])!.Stale);
    }

    [TestMethod]
    public async Task Source_paths_allow_test_and_coverage_files_outside_the_roots()
    {
        var outside = Directory.CreateTempSubdirectory("labcoat-outside-");
        try
        {
            var covered = Path.Combine(outside.FullName, "Covered.cs");
            var located = Path.Combine(outside.FullName, "Located.cs");
            var other = Path.Combine(outside.FullName, "Other.cs");
            foreach (var f in new[] { covered, located, other }) File.WriteAllText(f, "//");

            await using var w = new Workspace(["P"]);
            var nested = Directory.CreateDirectory(Path.Combine(w.Root.FullName, "P", "sub")).FullName;
            await using var e = new Labcoat.Explorer(new EventHub(), [w.Project("P")], null, [w.Root.FullName, nested]);
            Assert.AreEqual(SourceAccessResult.Forbidden, e.CheckSourcePath(covered).Result);
            e.SetCoverageReport(new CoverageReport { RunId = "r", GeneratedAt = "now", ProjectIds = ["P"], Files = [new FileCoverage { Path = covered, Module = "M" }] });
            e.SeedTests([new TestNode { Id = "t", ProjectId = "P", DisplayName = "t", FilePath = located }]);
            Assert.AreEqual(SourceAccessResult.Ok, e.CheckSourcePath(covered).Result);
            Assert.AreEqual(SourceAccessResult.Ok, e.CheckSourcePath(located).Result);
            Assert.AreEqual(SourceAccessResult.Forbidden, e.CheckSourcePath(other).Result);
            Assert.AreEqual(SourceAccessResult.Forbidden, e.CheckSourcePath("\0bad").Result);
            // nested roots are folded into their parent
            CollectionAssert.AreEqual(new[] { PathUtil.Canonicalize(w.Root.FullName) }, e.Snapshot().SourceRoots);

            Assert.AreEqual(404, e.OpenInEditor("vscode", Path.Combine(w.Root.FullName, "Missing.cs"), 1, 1).Status);
            Assert.AreEqual(400, e.OpenInEditor(null, covered, null, null).Status);
            if (!OperatingSystem.IsWindows())
            {
                var (status, ok, message) = e.OpenInEditor("visualstudio", covered, 0, 0);
                Assert.AreEqual((200, false, "Visual Studio is only available on Windows"), (status, ok, message));
            }
        }
        finally { outside.Delete(recursive: true); }
    }
}
