using Labcoat;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// The explorer against real Microsoft.Testing.Platform test apps (the shared <see cref="ScratchWorkspace"/>): runs,
/// repeat runs, quarantine, output capping, cancel, warm host lifecycle, debug runs, coverage + impact map + smart
/// watch, build failures and discovery diagnostics. Sequential: the tests edit the workspace's sources.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("e2e")]
public partial class ExplorerEndToEndTests
{
    public TestContext TestContext { get; set; }

    /// <summary>Same file (macOS temp paths are symlinks: /var -> /private/var).</summary>
    private static bool SamePath(string a, string? b) =>
        b is not null && string.Equals(PathUtil.Canonicalize(a), PathUtil.Canonicalize(b), PathUtil.Comparison);

    private static string Id(Labcoat.Explorer e, string displayName) => e.Snapshot().Tests.Single(t => t.DisplayName == displayName).Id;

    /// <param name="realBuild">false: skip the (no-op, ~3 s) `dotnet build` the explorer starts; the workspace is built already.</param>
    private static async Task<Labcoat.Explorer> DiscoveredAsync(ScratchWorkspace ws, EventHub? hub = null, bool realBuild = false)
    {
        var explorer = new Labcoat.Explorer(hub ?? new EventHub(), [ws.TestsProject], null, [ws.Root]);
        if (!realBuild) explorer.DotnetOverride = SkipBuild;
        await explorer.DiscoverAsync(null);
        Assert.HasCount(9, explorer.Snapshot().Tests, ScratchWorkspace.Logs(explorer));
        return explorer;
    }

    /// <summary>
    /// The tests a default and a lean client reconstruct from the events (EventRecorder.ClientTests) equal the host's
    /// snapshot of the same kind. Lean clients get no history in snapshots; what they built from patches must be the
    /// tail of the host's history.
    /// </summary>
    internal static async Task AssertClientsInSyncAsync(Labcoat.Explorer explorer, EventRecorder legacy, EventRecorder lean)
    {
        await Task.Delay(300); // pending test updates are flushed within ~50 ms; then mark the end of the stream
        var marker = "sync " + Guid.NewGuid().ToString("N");
        explorer.Log(null, "info", marker);
        foreach (var (rec, isLean) in new[] { (legacy, false), (lean, true) })
        {
            await rec.WaitForAsync("log", e => (string?)e["message"] == marker);
            var state = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(explorer.Snapshot(lean: isLean), Json.Options))!;
            var files = state["files"]?.AsArray();
            var client = rec.ClientTests();
            var expected = state["tests"]!.AsArray();
            Assert.HasCount(expected.Count, client);
            foreach (var t in expected)
            {
                var e = t!.AsObject();
                if (e["fileIndex"] is { } fi)
                {
                    e["filePath"] = (string?)files![(int)fi];
                    e.Remove("fileIndex");
                }
                e["traits"] ??= new System.Text.Json.Nodes.JsonArray();
                var key = $"{(string)e["projectId"]!}|{(string)e["id"]!}";
                var c = client[key].DeepClone().AsObject();
                if (isLean)
                {
                    var hist = c["history"]?.AsArray() ?? [];
                    var host = explorer.GetTest(e["projectId"]!.GetValue<string>(), e["id"]!.GetValue<string>())!.History ?? [];
                    var tail = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(host.Skip(host.Count - hist.Count), Json.Options))!;
                    Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(tail, hist), $"lean history of {key}:\nhost   {tail.ToJsonString()}\nclient {hist.ToJsonString()}");
                    c.Remove("history");
                }
                Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(e, c), $"{(isLean ? "lean" : "default")} client, {key}:\nhost   {e.ToJsonString()}\nclient {c.ToJsonString()}");
            }
        }
    }

    /// <summary>A `dotnet` that succeeds at building without doing anything (the rest, e.g. MSBuild evaluation, is real).</summary>
    internal static Task<(int, string)> SkipBuild(string[] args, string cwd, CancellationToken ct) =>
        args[0] == "build" ? Task.FromResult((0, "Build succeeded.")) : ScratchWorkspace.DotnetAsync(cwd, args);

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Runs_report_results_output_history_quarantine_and_repeats()
    {
        var ws = await ScratchWorkspace.GetAsync();
        var hub = new EventHub();
        await using var explorer = await DiscoveredAsync(ws, hub);
        await using var events = new EventRecorder(hub, explorer);
        await using var leanEvents = new EventRecorder(hub, explorer, lean: true);
        await events.WaitForAsync("state");
        await leanEvents.WaitForAsync("state");
        Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState);

        // ---- run everything
        await explorer.RunAsync(null, null);
        var run = explorer.Summary().LastRun!;
        Assert.AreEqual((9, 7, 1, 1), (run.Total, run.Passed, run.Failed, run.Skipped), ScratchWorkspace.Logs(explorer));
        Assert.AreEqual("all", run.Scope);
        Assert.AreEqual("finished", explorer.GetRunStatus(run.RunId)!.State);

        var failed = explorer.GetTest(null, Id(explorer, "Fails"))!;
        Assert.AreEqual(Outcome.Failed, failed.Outcome);
        Assert.Contains("boom", failed.ErrorMessage!);
        Assert.AreEqual("Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException", failed.ErrorType);
        Assert.AreEqual(Outcome.Skipped, explorer.GetTest(null, Id(explorer, "Skipped"))!.Outcome);
        var passes = explorer.GetTest(null, Id(explorer, "Passes"))!;
        Assert.AreEqual(("X", "MiscTests", "Passes"), (passes.Namespace, passes.ClassName, passes.MethodName));
        Assert.Contains(new Trait("Category", "Integration"), passes.Traits);
        Assert.IsTrue(SamePath(ws.MiscTestsCs, passes.FilePath), passes.FilePath);

        // trace id marker: stripped from the output, kept as the test's trace id
        var traced = Id(explorer, "Traced");
        Assert.AreEqual((true, ScratchWorkspace.TraceId), explorer.TestTraceId(null, traced));
        Assert.AreEqual((true, ScratchWorkspace.TraceId), explorer.TestTraceId("X.Tests", traced));
        Assert.AreEqual((false, (string?)null), explorer.TestTraceId(null, "nope"));
        Assert.DoesNotContain("labcoat:traceId", explorer.GetTest(null, traced)!.StdOut ?? "");

        // big output: capped in the state, the full text on demand
        var big = Id(explorer, "BigOutput");
        var bigNode = explorer.GetTest("X.Tests", big)!;
        Assert.IsTrue(bigNode.OutputTruncated);
        Assert.IsLessThan(70_000, bigNode.StdOut!.Length);
        var full = explorer.GetOutput(null, big)!;
        Assert.IsFalse(full.Truncated);
        Assert.AreEqual(70_000, full.StdOut!.TrimEnd().Length);
        Assert.IsNull(explorer.GetOutput(null, "nope"));
        Assert.IsFalse(explorer.GetOutput(null, traced)!.Truncated);

        // history: one entry per finished test, the run is persisted
        var history = explorer.GetHistory(null, null);
        Assert.HasCount(9, history);
        Assert.HasCount(1, explorer.GetHistory("X.Tests", Id(explorer, "Fails")).Single().History);
        Assert.AreEqual(run.RunId, explorer.Runs()[0].RunId);
        Assert.HasCount(9, explorer.GetRun(run.RunId)!.Results);

        // events: the run in order, test updates in between (the recorder receives them asynchronously: wait for the
        // end of the run, slower runners can still be delivering it)
        await events.WaitForAsync("runFinished");
        var types = events.Types();
        var started = types.IndexOf("runStarted");
        Assert.IsGreaterThanOrEqualTo(0, started);
        Assert.IsGreaterThan(started, types.IndexOf("runFinished"));
        Assert.IsGreaterThan(started, events.TestUpdates().Max(u => u.Index));
        Assert.IsNotEmpty(events.Of("runsUpdated"));

        // ---- quarantine: still runs, counted separately
        explorer.SetQuarantine("X.Tests", Id(explorer, "Fails"), true);
        explorer.SetQuarantine("X.Tests", Id(explorer, "Fails"), true); // no change: no broadcast
        try
        {
            await explorer.RunAsync("X.Tests", [Id(explorer, "Fails"), Id(explorer, "Passes")]);
            run = explorer.Summary().LastRun!;
            Assert.AreEqual((2, 1, 0, 1), (run.Total, run.Passed, run.Failed, run.Quarantined));
            Assert.AreEqual("subset", run.Scope);
            Assert.Contains("Fails (quarantined)", ScratchWorkspace.Logs(explorer));
        }
        finally { explorer.SetQuarantine("X.Tests", Id(explorer, "Fails"), false); }
        Assert.IsEmpty(explorer.Snapshot().Quarantined!);

        // ---- repeat: the flaky test fails in some repetitions; coverage is not collected for repeats
        var flaky = Id(explorer, "Flaky");
        await explorer.RunAsync(null, [flaky, Id(explorer, "Passes")], coverage: true, repeat: 3);
        run = explorer.Summary().LastRun!;
        Assert.AreEqual((3, 3), (run.Repeat!.Count, run.Repeat.Completed));
        var stats = run.Repeat.PerTest.Single(r => r.Id == flaky);
        Assert.AreEqual(3, stats.Passed + stats.Failed);
        Assert.IsTrue(stats.Passed > 0 && stats.Failed > 0, $"{stats.Passed} passed, {stats.Failed} failed");
        Assert.HasCount(3, stats.DurationsMs);
        var flakyNode = explorer.GetTest(null, flaky)!;
        Assert.AreEqual(Outcome.Failed, flakyNode.Outcome); // failed in some repetition: the failure is shown
        Assert.Contains("run ", flakyNode.ErrorMessage!);
        Assert.IsTrue(flakyNode.Flaky);
        Assert.IsTrue(flakyNode.History![^1].Flaky);
        Assert.AreEqual(Outcome.Passed, explorer.GetTest(null, Id(explorer, "Passes"))!.Outcome);
        Assert.Contains("Repeat runs do not collect coverage", ScratchWorkspace.Logs(explorer));
        Assert.Contains("(3/3 repetitions, 1 flaky)", ScratchWorkspace.Logs(explorer));

        // ---- random order: one request per test in the order the seed gives; the seed is in the summary and the log
        var randomIds = new[] { Id(explorer, "Passes"), Id(explorer, "Fails"), Id(explorer, "Skipped"), flaky };
        var eventsBefore = events.All().Count;
        await explorer.RunAsync(null, randomIds, coverage: true, order: "random", seed: 4242);
        run = explorer.Summary().LastRun!;
        Assert.AreEqual(("random", 4242, 4), (run.Order, run.Seed, run.Total));
        Assert.Contains("Random order, seed 4242: 4 tests, one at a time", ScratchWorkspace.Logs(explorer));
        Assert.Contains("Random-order runs do not collect coverage", ScratchWorkspace.Logs(explorer));
        var ranOrder = events.TestUpdates().Where(u => u.Index >= eventsBefore && u.Outcome == "running").Select(u => u.Id).Distinct().ToList();
        var expected = RandomOrder.Shuffle(randomIds, 4242, id => id).Where(id => ranOrder.Contains(id)).ToList();
        CollectionAssert.AreEqual(expected, ranOrder, "tests start in the seed's order");

        // ---- the clients' tests, built from snapshots + full entries + result patches, match the host's
        Assert.IsNotEmpty(events.Of("testResults"), "result-only changes go out as patches");
        await AssertClientsInSyncAsync(explorer, events, leanEvents);

        // ---- a build failure: results are outdated, the error is parsed, runs are skipped
        explorer.DotnetOverride = null;
        var original = File.ReadAllText(ws.AddTestsCs);
        try
        {
            File.WriteAllText(ws.AddTestsCs, original.Replace("Assert.AreEqual", "Assert.AreEqualX"));
            await explorer.DiscoverAsync("X.Tests");
            var project = explorer.Snapshot().Projects[0];
            Assert.AreEqual(ProjectStatus.Error, project.Status);
            Assert.IsTrue(project.ResultsOutdated);
            Assert.IsGreaterThanOrEqualTo(1, project.BuildErrorCount!.Value);
            var error = project.BuildErrors!.First(d => d.Severity == "error");
            Assert.IsTrue(SamePath(ws.AddTestsCs, error.File), error.File);
            Assert.AreEqual("CS0117", error.Code);
            Assert.Contains("error CS0117", project.LastError!);
            Assert.Contains("CS0117", project.BuildOutput!);

            var skipped = explorer.RequestRun(null, null);
            await skipped.Completion!;
            var status = explorer.GetRunStatus(skipped.RunId!)!;
            Assert.AreEqual(("skipped", "The build or discovery failed."), (status.State, status.Reason));
        }
        finally { File.WriteAllText(ws.AddTestsCs, original); }
        await explorer.DiscoverAsync(null);
        Assert.IsNull(explorer.Snapshot().Projects[0].ResultsOutdated);
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Cancel_restart_tracing_and_a_dead_warm_host()
    {
        var ws = await ScratchWorkspace.GetAsync();
        await using var explorer = await DiscoveredAsync(ws);
        var slow = Id(explorer, "Slow");

        // ---- cancel while a test runs: the rest are cancelled, the warm host survives (the test ends within the grace)
        var r = explorer.RequestRun(null, [slow, Id(explorer, "Passes"), Id(explorer, "Fails")]);
        await ScratchWorkspace.WaitUntilAsync(() => explorer.GetTest(null, slow)!.Outcome == Outcome.Running, "Slow running");
        Assert.IsTrue(explorer.IsRunning);
        var cancel = explorer.Cancel();
        Assert.IsTrue(cancel.Running);
        Assert.AreEqual("run", cancel.ActiveOperation);
        await r.Completion!;
        var status = explorer.GetRunStatus(r.RunId!)!;
        Assert.AreEqual("cancelled", status.State, ScratchWorkspace.Logs(explorer));
        Assert.IsTrue(status.Run!.Cancelled);
        Assert.IsGreaterThanOrEqualTo(1, status.Run.NotRun!.Value);
        Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState);

        // ---- restart while a run is busy: the run is cancelled and the host replaced right away
        r = explorer.RequestRun(null, [slow]);
        await ScratchWorkspace.WaitUntilAsync(() => explorer.GetTest(null, slow)!.Outcome == Outcome.Running, "Slow running again");
        await explorer.RestartHostAsync(null);
        await r.Completion!;
        var logs = ScratchWorkspace.Logs(explorer);
        Assert.Contains("Restart requested while a run is in progress", logs);
        Assert.Contains("Test host is warm", logs);
        Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState);

        // ---- idle restart of one project
        await explorer.RestartHostAsync("X.Tests");
        Assert.Contains("Restarting the test host", ScratchWorkspace.Logs(explorer));

        // ---- tracing toggles replace the warm host on its next request
        explorer.SetTracing(true);
        try
        {
            Assert.IsTrue(explorer.TracingEnabled);
            await explorer.RunAsync(null, [Id(explorer, "Passes")]);
            Assert.Contains("Stopping the warm test host: trace collection was turned on", ScratchWorkspace.Logs(explorer));
        }
        finally { explorer.SetTracing(false); }
        await explorer.RunAsync(null, [Id(explorer, "Passes")]);
        Assert.Contains("Stopping the warm test host: trace collection was turned off", ScratchWorkspace.Logs(explorer));

        // ---- the warm host dies on its own: state goes cold, the next request starts a new one
        var pid = int.Parse(StartedHostPid().Matches(ScratchWorkspace.Logs(explorer))[^1].Groups[1].Value);
        using (var process = System.Diagnostics.Process.GetProcessById(pid)) process.Kill();
        await ScratchWorkspace.WaitUntilAsync(() => explorer.Summary().Projects[0].HostState == "cold", "host state cold");
        Assert.Contains("the next request starts a new one", ScratchWorkspace.Logs(explorer));
        await explorer.RunAsync(null, [Id(explorer, "Passes")]);
        Assert.AreEqual(Outcome.Passed, explorer.GetTest(null, Id(explorer, "Passes"))!.Outcome);
        Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Debug_run_waits_for_a_debugger_until_cancelled()
    {
        var ws = await ScratchWorkspace.GetAsync();
        var hub = new EventHub();
        await using var explorer = await DiscoveredAsync(ws, hub);
        await using var events = new EventRecorder(hub, explorer);
        var passes = Id(explorer, "Passes");

        var r = explorer.RequestRun(new() { TestIds = [passes], Debug = true });
        Assert.AreEqual(202, r.Status);
        await events.WaitForAsync("debuggerWaiting");
        var waiting = events.Of("debuggerWaiting").Single();
        Assert.AreEqual("X.Tests", (string?)waiting["projectId"]);
        Assert.AreEqual(r.RunId, (string?)waiting["runId"]);
        Assert.IsGreaterThan(0, (int)waiting["pid"]!);
        Assert.HasCount(3, waiting["hints"]!.AsArray());
        Assert.IsTrue(explorer.Summary().CurrentRun!.Debug);

        explorer.Cancel();
        await r.Completion!;
        Assert.AreEqual("cancelled", explorer.GetRunStatus(r.RunId!)!.State, ScratchWorkspace.Logs(explorer));
        Assert.AreEqual(Outcome.Cancelled, explorer.GetTest(null, passes)!.Outcome);
        Assert.IsEmpty(events.Of("debuggerAttached"));
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Coverage_cache_impact_map_and_smart_watch()
    {
        var ws = await ScratchWorkspace.GetAsync();
        await using var explorer = await DiscoveredAsync(ws, realBuild: true);
        var add = Id(explorer, "Add_Works");
        var mul = Id(explorer, "Mul_Works");
        var addLine = CalcLine("return a + b;");
        var mulLine = CalcLine("return a * b;");

        // ---- per-test coverage: report + cache + "which tests cover this line"
        explorer.SetCoverage(true);
        explorer.SetCoverage(true); // no change
        await explorer.RunAsync(null, [add]);
        var report = explorer.Snapshot().Coverage;
        Assert.IsNotNull(report, ScratchWorkspace.Logs(explorer));
        Assert.AreEqual("subset", report.Scope);
        CollectionAssert.AreEqual(new[] { add }, report.TestIds);
        var ops = report.Files.Single(f => SamePath(f.Path, ws.CalcCs));
        Assert.IsGreaterThan(0, ops.Lines.Single(l => l.Line == addLine).Hits);
        Assert.AreEqual(0, ops.Lines.Single(l => l.Line == mulLine).Hits);
        Assert.IsTrue(report.Files.All(f => !f.Path.Contains("Tests")), "the test assembly is excluded");

        var cached = explorer.GetCachedTestCoverage(null, [add]);
        Assert.IsNotNull(cached);
        Assert.IsNull(cached.Stale);
        Assert.IsNull(explorer.GetCachedTestCoverage("X.Tests", [mul]));
        Assert.IsNull(explorer.GetCachedTestCoverage(null, []));

        var covering = explorer.CoveringTests(ws.CalcCs, addLine);
        Assert.AreEqual((add, "perTest"), (covering.Tests.Single().Id, covering.Tests.Single().Source));
        Assert.IsTrue(covering.Partial);
        Assert.IsNotNull(explorer.Snapshot().ImpactMap);

        // ---- the impact map pass: one coverage run per test class
        await explorer.BuildImpactMapAsync(null);
        var map = explorer.Snapshot().ImpactMap!;
        Assert.IsNotNull(map.BuiltAt, ScratchWorkspace.Logs(explorer));
        Assert.IsNull(map.Building);
        Assert.IsGreaterThanOrEqualTo(2, map.Tests);
        var mulCovering = explorer.CoveringTests(ws.CalcCs, mulLine).Tests;
        Assert.AreEqual((mul, "impactMap"), (mulCovering.Single().Id, mulCovering.Single().Source));
        // impactMap "auto": a class is refined per test on demand (one coverage process per test)
        // (MulTests has a single test: the class pass gave it per-test data already)
        var passes = Id(explorer, "Passes");
        lock (explorer.Sync)
            Assert.IsTrue((explorer.ImpactMapState.IsPerTest("X.Tests", mul), explorer.ImpactMapState.IsPerTest("X.Tests", passes)) == (true, false));
        await explorer.RefineImpactMapAsync([("X.Tests", passes)]);
        lock (explorer.Sync) Assert.IsTrue(explorer.ImpactMapState.IsPerTest("X.Tests", passes), ScratchWorkspace.Logs(explorer));
        Assert.Contains("refined to per-test data", ScratchWorkspace.Logs(explorer));
        explorer.SetCoverage(false);

        // ---- smart watch
        var calcOriginal = File.ReadAllText(ws.CalcCs);
        var addTestsOriginal = File.ReadAllText(ws.AddTestsCs);
        var data = Path.Combine(ws.TestsDir, "settings.json");
        explorer.SetWatch(true);
        try
        {
            // a test file changed: its tests run
            var before = explorer.Summary().LastRun!.RunId;
            File.WriteAllText(ws.AddTestsCs, addTestsOriginal + "\n// edited\n");
            var watchRun = await NextRunAsync(explorer, before);
            Assert.AreEqual(("watch", "testFile", 1), (watchRun.Trigger, watchRun.Affected!.Strategy, watchRun.Total), ScratchWorkspace.Logs(explorer));
            Assert.IsTrue(watchRun.Watch!.ChangedFiles.Any(f => SamePath(f, ws.AddTestsCs)), string.Join(", ", watchRun.Watch.ChangedFiles));

            // a product method changed: the impact map picks the tests that executed it
            File.WriteAllText(ws.CalcCs, calcOriginal.Replace("return a * b;", "return b * a;"));
            watchRun = await NextRunAsync(explorer, watchRun.RunId);
            Assert.AreEqual(("impactMap", 1), (watchRun.Affected!.Strategy, watchRun.Total), ScratchWorkspace.Logs(explorer));
            Assert.AreEqual(Outcome.Passed, explorer.GetTest(null, mul)!.Outcome);
            Assert.Contains("impact map", ScratchWorkspace.Logs(explorer));
            // the cached per-test coverage was collected before the rebuild
            Assert.IsTrue(explorer.GetCachedTestCoverage(null, [add])!.Stale);
            Assert.IsTrue(explorer.CoveringTests(ws.CalcCs, addLine).Tests.Single(t => t.Id == add).Stale);

            // the same content saved again: rebuilt, but nothing to run
            File.WriteAllText(ws.CalcCs, File.ReadAllText(ws.CalcCs));
            await ScratchWorkspace.WaitUntilAsync(() => ScratchWorkspace.Logs(explorer).Contains("→ no affected tests"), "no affected tests", 60_000);
            Assert.AreEqual(watchRun.RunId, explorer.Summary().LastRun!.RunId);

            // a file the map knows nothing about: everything runs
            File.WriteAllText(data, "{}");
            watchRun = await NextRunAsync(explorer, watchRun.RunId);
            Assert.AreEqual(("all", 9), (watchRun.Affected!.Strategy, watchRun.Total));
        }
        finally
        {
            explorer.SetWatch(false);
            File.WriteAllText(ws.CalcCs, calcOriginal);
            File.WriteAllText(ws.AddTestsCs, addTestsOriginal);
            File.Delete(data);
        }
        Assert.IsFalse(explorer.Snapshot().Watch);

        int CalcLine(string text) => Array.FindIndex(ScratchWorkspace.CalcSource.Split('\n'), l => l.Contains(text)) + 1;
    }

    /// <summary>
    /// The bug: with coverage on, a (watch) rerun of a few tests replaced the Coverage page's report with that subset's
    /// numbers (7.2% instead of ~96%). The page's report (state.fullCoverage, GET /api/coverage) is the last full run;
    /// the subset's own report is state.coverage / coverageUpdated (the Tests page's Coverage tab).
    /// </summary>
    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task A_subset_coverage_run_keeps_the_full_report_of_the_coverage_page()
    {
        var ws = await ScratchWorkspace.GetAsync();
        var hub = new EventHub();
        await using var explorer = await DiscoveredAsync(ws, hub);
        await using var events = new EventRecorder(hub, explorer, lean: true);
        await events.WaitForAsync("state");
        var add = Id(explorer, "Add_Works");
        Assert.IsNull(explorer.FullCoverageReport);

        // a subset first: no full report yet
        await explorer.RunAsync(null, [add], coverage: true);
        Assert.AreEqual("subset", explorer.Snapshot().Coverage!.Scope, ScratchWorkspace.Logs(explorer));
        Assert.IsNull(explorer.Snapshot().FullCoverage);
        Assert.Contains("no full report yet for the Coverage page", ScratchWorkspace.Logs(explorer));

        await explorer.RunAsync(null, null, coverage: true);
        var full = explorer.Snapshot().FullCoverage;
        Assert.IsNotNull(full, ScratchWorkspace.Logs(explorer));
        Assert.AreEqual(("all", full.LinesCovered, full.LinesValid), (explorer.Snapshot().Coverage!.Scope, explorer.Snapshot().Coverage!.LinesCovered, explorer.Snapshot().Coverage!.LinesValid));

        // a rerun of selected tests, nothing edited: the run's own report is the subset, the page's report is still the full one
        await explorer.RunAsync(null, [add], coverage: true);
        var subset = explorer.Snapshot().Coverage!;
        Assert.AreEqual("subset", subset.Scope, ScratchWorkspace.Logs(explorer));
        Assert.IsLessThan(full.LinesCovered, subset.LinesCovered, "the subset covers less than the full run");
        var after = explorer.Snapshot().FullCoverage!;
        Assert.AreEqual(("all", full.LinesCovered, full.LinesValid, full.LineRate), (after.Scope, after.LinesCovered, after.LinesValid, after.LineRate));
        Assert.AreSame(after, explorer.FullCoverageReport);
        Assert.AreSame(subset, explorer.LastCoverageReport);
        Assert.Contains("the Coverage page keeps the full report", ScratchWorkspace.Logs(explorer));
        await ScratchWorkspace.WaitUntilAsync(() => events.Of("coverageUpdated").Count == 3, "3 coverageUpdated events", 30_000);
        Assert.HasCount(1, events.Of("fullCoverageUpdated"), "only the full run changed the page's report");
        Assert.AreEqual(full.LinesCovered, (int)events.Of("fullCoverageUpdated")[0]["coverage"]!["linesCovered"]!);

        // a product file edited after the full run (not rebuilt here): the page marks it stale, numbers unchanged
        var calcOriginal = File.ReadAllText(ws.CalcCs);
        try
        {
            File.WriteAllText(ws.CalcCs, "// edited\n" + calcOriginal);
            await explorer.RunAsync(null, [add]); // any run checks the report's files
            var stale = explorer.Snapshot().FullCoverage!;
            Assert.IsTrue(stale.StaleFiles!.Any(f => SamePath(ws.CalcCs, f)), string.Join(", ", stale.StaleFiles!));
            Assert.IsTrue(stale.Files.Single(f => SamePath(ws.CalcCs, f.Path)).Stale);
            Assert.AreEqual((full.LinesCovered, full.LinesValid), (stale.LinesCovered, stale.LinesValid));
            await events.WaitForAsync("fullCoverageUpdated", e => e["coverage"]!["staleFiles"] is not null);
        }
        finally { File.WriteAllText(ws.CalcCs, calcOriginal); }
        await explorer.RunAsync(null, [add]);
        Assert.IsNull(explorer.Snapshot().FullCoverage!.StaleFiles, "the same content again");
    }

    private static async Task<RunSummary> NextRunAsync(Labcoat.Explorer explorer, string previousRunId)
    {
        await ScratchWorkspace.WaitUntilAsync(() => explorer.Summary().LastRun is { } r && r.RunId != previousRunId && explorer.Summary().CurrentRun is null,
            "a watch-triggered run", 90_000);
        return explorer.Summary().LastRun!;
    }

    private static async Task<Labcoat.Explorer> BigAsync(ScratchWorkspace ws)
    {
        var explorer = new Labcoat.Explorer(new EventHub(), [ws.BigTestsProject], null, [ws.Root]) { DotnetOverride = SkipBuild };
        await explorer.DiscoverAsync(null);
        Assert.HasCount(3, explorer.Snapshot().Tests, ScratchWorkspace.Logs(explorer));
        return explorer;
    }

    private static int LastHostPid(Labcoat.Explorer e) =>
        int.Parse(StartedHostPid().Matches(ScratchWorkspace.Logs(e))[^1].Groups[1].Value);

    /// <summary>
    /// Microsoft.CodeCoverage's static instrumentation rewrites the product assemblies of the output folder in place on
    /// Linux / macOS and restores the originals afterwards (same bytes, same time stamp). A warm host that had
    /// Big.Lib.dll loaded kept the instrumented bytes mapped: its next run failed (garbage type / assembly names, or
    /// "Index not found (0x80131124)") until "Restart test host". Now Labcoat turns static instrumentation off wherever
    /// the extension ships its CLR profiler (Windows, linux-x64, osx-x64): nothing is rewritten and the warm host
    /// simply stays. Elsewhere the warm host is stopped before the coverage run and a fresh one is started after it.
    /// </summary>
    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task A_coverage_run_leaves_a_working_warm_host_for_the_next_run()
    {
        var ws = await ScratchWorkspace.GetAsync();
        await using var explorer = await BigAsync(ws);
        var first = Id(explorer, "First");

        await explorer.RunAsync(null, [first]); // the warm host loads Big.Lib, reading only a little of it
        Assert.AreEqual(Outcome.Passed, explorer.GetTest(null, first)!.Outcome, ScratchWorkspace.Logs(explorer));
        var warmPid = LastHostPid(explorer);

        await explorer.RunAsync(null, [first], coverage: true);
        Assert.IsNotNull(explorer.Snapshot().Coverage, ScratchWorkspace.Logs(explorer));
        Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState, "a warm host is ready for the next non-coverage run");
        Assert.DoesNotContain("was rewritten in place", ScratchWorkspace.Logs(explorer));
        var dynamic = explorer.Snapshot().Coverage!.Settings!.Single().Instrumentation == "dynamic";
        Assert.AreEqual(OperatingSystem.IsWindows() || CoverageSettingsResolver.DynamicInstrumentationAvailable(Path.GetDirectoryName(ws.BigLibInTestOutput)!), dynamic);
        var replaced = !dynamic;

        await explorer.RunAsync(null, null); // All reads every type of Big.Lib
        var run = explorer.Summary().LastRun!;
        Assert.AreEqual((3, 3, 0), (run.Total, run.Passed, run.Failed),
            explorer.GetTest(null, Id(explorer, "All"))!.ErrorMessage + "\n" + ScratchWorkspace.Logs(explorer));
        Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState);
        var pidsAfter = StartedBigTestsWarmHostPid().Matches(ScratchWorkspace.Logs(explorer));
        Assert.HasCount(replaced ? 2 : 1, pidsAfter, "warm hosts started: the first, plus one after a static-instrumentation coverage run");
        Assert.AreEqual(warmPid, int.Parse(pidsAfter[0].Groups[1].Value));
    }

    /// <summary>
    /// A build outside Labcoat (a terminal, the IDE, another resource running `dotnet watch test`) overwrites an assembly
    /// the idle warm host has loaded: Labcoat notices, starts a fresh warm host right away, and the next run is correct.
    /// An identical rewrite keeps the host. On Windows a loaded assembly can't be written at all (the build fails to copy
    /// over it instead; Labcoat's own builds stop the warm host first): the write is refused and the host stays.
    /// </summary>
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task An_assembly_rewritten_under_an_idle_warm_host_restarts_it()
    {
        var ws = await ScratchWorkspace.GetAsync();
        await using var explorer = await BigAsync(ws);
        await explorer.RunAsync(null, [Id(explorer, "First")]);
        var pid = LastHostPid(explorer);
        var original = File.ReadAllBytes(ws.BigLibInTestOutput);
        var time = File.GetLastWriteTimeUtc(ws.BigLibInTestOutput);
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(ws.BigLibInTestOutput, [.. original, 0, 0, 0, 0]));
            await Task.Delay(1000, TestContext.CancellationToken);
            Assert.AreEqual(pid, LastHostPid(explorer), ScratchWorkspace.Logs(explorer));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(ws.BigLibInTestOutput));
        }
        else
        {
            try
            {
                // The same bytes written again (a deterministic rebuild): the host stays.
                File.WriteAllBytes(ws.BigLibInTestOutput, original);
                await Task.Delay(1000, TestContext.CancellationToken);
                Assert.AreEqual(pid, LastHostPid(explorer), ScratchWorkspace.Logs(explorer));

                // Different bytes, in place (trailing data: still a valid image): a new warm host.
                File.WriteAllBytes(ws.BigLibInTestOutput, [.. original, 0, 0, 0, 0]);
                await ScratchWorkspace.WaitUntilAsync(() => LastHostPid(explorer) != pid && explorer.Summary().Projects[0].HostState == "warm",
                    "a new warm host", 30_000);
                Assert.Contains("Stopping the warm test host: Big.Lib.dll was rewritten in place", ScratchWorkspace.Logs(explorer));
            }
            finally
            {
                File.WriteAllBytes(ws.BigLibInTestOutput, original);
                File.SetLastWriteTimeUtc(ws.BigLibInTestOutput, time);
            }
        }
        await explorer.RunAsync(null, null);
        var run = explorer.Summary().LastRun!;
        Assert.AreEqual((3, 3), (run.Total, run.Passed), ScratchWorkspace.Logs(explorer));
    }

    /// <summary>
    /// Cancel during a run of two projects: the second project's tests never started. They are cancelled at once (not
    /// left queued in /api/summary and the WS state until the first host stops), and the summary says "cancelling"
    /// until the run has ended.
    /// </summary>
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Cancel_clears_queued_tests_of_every_project_right_away()
    {
        var ws = await ScratchWorkspace.GetAsync();
        var hub = new EventHub();
        // one project at a time, so Big.Tests waits for its turn (ParallelEndToEndTests cancels projects running at once)
        await using var explorer = new Labcoat.Explorer(hub, [ws.TestsProject, ws.BigTestsProject], null, [ws.Root]) { DotnetOverride = SkipBuild, MaxParallelProjects = 1 };
        await explorer.DiscoverAsync(null);
        await using var events = new EventRecorder(hub, explorer);
        var slow = explorer.Snapshot().Tests.Single(t => t.DisplayName == "Slow" && t.ProjectId == "X.Tests").Id;
        var big = explorer.Snapshot().Tests.Where(t => t.ProjectId == "Big.Tests").Select(t => t.Id).ToList();

        var r = explorer.RequestRun(null, [slow, .. big]);
        await ScratchWorkspace.WaitUntilAsync(() => explorer.GetTest("X.Tests", slow)!.Outcome == Outcome.Running, "Slow running");
        Assert.AreEqual(3, explorer.Summary().Tests.Queued, "Big.Tests waits for its turn");

        explorer.Cancel();
        var summary = explorer.Summary();
        Assert.AreEqual(0, summary.Tests.Queued, "no test waits any more");
        Assert.AreEqual(3, summary.Tests.Cancelled);
        Assert.IsTrue(summary.Cancelling);
        await ScratchWorkspace.WaitUntilAsync(() => events.TestUpdates().Any(u => u.Outcome == "cancelled"), "a cancelled test update");

        await r.Completion!;
        summary = explorer.Summary();
        // passed: only this session's results (the workspace's history also restores other tests' earlier results, stale)
        var passedNow = explorer.Snapshot().Tests.Count(t => t.Outcome == Outcome.Passed && t.Stale != true);
        Assert.AreEqual((0, 0, 0), (summary.Tests.Queued, summary.Tests.Running, passedNow), ScratchWorkspace.Logs(explorer));
        Assert.IsNull(summary.Cancelling);
        Assert.IsNull(summary.CurrentRun);
        Assert.IsTrue(summary.LastRun!.Cancelled);
        Assert.AreEqual("cancelled", explorer.GetRunStatus(r.RunId!)!.State);
        Assert.IsTrue(big.All(id => explorer.GetTest("Big.Tests", id)!.Outcome == Outcome.Cancelled));
        Assert.IsFalse(explorer.Snapshot().Tests.Any(t => t.Outcome is Outcome.Queued or Outcome.Running));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Coverage_needs_the_extension_and_hosts_that_exit_or_never_connect_fail_cleanly()
    {
        var ws = await ScratchWorkspace.GetAsync();
        await using var explorer = new Labcoat.Explorer(new EventHub(), [ws.TestsProject], null)
        {
            // as if the test app had no Microsoft.Testing.Extensions.CodeCoverage: --help doesn't list --coverage-output-format
            DotnetOverride = (args, cwd, ct) => args[0] is "build" or "msbuild" ? SkipBuild(args, cwd, ct) : Task.FromResult((0, "Usage: X.Tests [options]")),
        };
        await explorer.DiscoverAsync(null);
        var passes = Id(explorer, "Passes");
        await explorer.RunAsync(null, [passes], coverage: true);
        Assert.AreEqual(Outcome.Passed, explorer.GetTest(null, passes)!.Outcome);
        Assert.IsNull(explorer.Snapshot().Coverage);
        Assert.Contains("Code coverage requested but the test app has no --coverage option", ScratchWorkspace.Logs(explorer));
        await explorer.BuildImpactMapAsync("X.Tests");
        Assert.Contains("Impact map needs code coverage", ScratchWorkspace.Logs(explorer));
        Assert.IsNull(explorer.Snapshot().ImpactMap); // nothing recorded

        // a process that is not a test app exits before any request completes
        await using (var host = MtpHost.Start(ws.NotMtpDll, [], (_, _) => { }, (_, _) => { }))
        {
            var ex = await Assert.ThrowsExactlyAsync<TestHostExitedException>(() =>
                host.RunOperationAsync((conn, ct) => conn.DiscoverAsync(Guid.NewGuid(), ct), CancellationToken.None));
            Assert.AreEqual(2, ex.ExitCode);
            Assert.Contains("Is this a Microsoft.Testing.Platform test app?", ex.Message);
            CollectionAssert.Contains(ex.StderrTail, "not a test app");
            Assert.IsTrue(host.HasExited);
            Assert.AreEqual(2, host.ExitCode);
            Assert.Contains("not a test app", host.OutputTail(1000));
        }

        // a test host that never connects (it waits for a debugger)
        await using (var waiting = MtpHost.Start(ws.TestsDll, [], (_, _) => { }, (_, _) => { },
                         new Dictionary<string, string> { [Labcoat.Explorer.WaitAttachDebuggerEnvVar] = "1" }))
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                waiting.RunOperationAsync((conn, ct) => conn.DiscoverAsync(Guid.NewGuid(), ct), CancellationToken.None, TimeSpan.FromMilliseconds(500)));
            Assert.IsFalse(waiting.HasExited);
            Assert.IsNull(waiting.ExitCode);
            Assert.IsGreaterThan(0, waiting.ProcessId);
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Discovery_diagnoses_projects_without_tests_and_apps_that_are_not_test_apps()
    {
        var ws = await ScratchWorkspace.GetAsync();
        await using var explorer = new Labcoat.Explorer(new EventHub(), [ws.EmptyProject, ws.NotMtpProject], null) { DotnetOverride = SkipBuild };
        await explorer.DiscoverAsync(null);
        var projects = explorer.Snapshot().Projects;

        var empty = projects.Single(p => p.Id == "Empty.Tests");
        Assert.AreEqual(ProjectStatus.Idle, empty.Status, ScratchWorkspace.Logs(explorer));
        Assert.AreEqual("No tests discovered.", empty.Diagnostics!.Single().Message);
        Assert.Contains("Empty.Tests.dll", empty.Diagnostics![0].Hint!);

        // a restored console app (no test framework): never started, the message says why (Explorer.Platform.cs)
        var notMtp = projects.Single(p => p.Id == "NotMtp");
        Assert.AreEqual(ProjectStatus.Idle, notMtp.Status);
        Assert.AreEqual("none", notMtp.TestPlatform);
        Assert.StartsWith("NotMtp is not a Microsoft.Testing.Platform test project", notMtp.Diagnostics!.Single().Message);
        Assert.DoesNotContain("NotMtp.dll --server", ScratchWorkspace.Logs(explorer));

        // a run of a project that cannot discover is skipped
        var r = explorer.RequestRun("NotMtp", null);
        await r.Completion!;
        Assert.AreEqual("skipped", explorer.GetRunStatus(r.RunId!)!.State);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Started test host \(pid (\d+)\)")]
    private static partial System.Text.RegularExpressions.Regex StartedHostPid();

    [System.Text.RegularExpressions.GeneratedRegex(@"Started test host \(pid (\d+)\): dotnet Big.Tests.dll --server --client-port \d+$",
        System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex StartedBigTestsWarmHostPid();
}
