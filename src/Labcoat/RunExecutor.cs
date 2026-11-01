using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml;

namespace Labcoat;

/// <summary>
/// A run request, from the build before it to its summary line (Explorer.Runs.cs queues the requests and holds the op
/// lock while one executes): the projects run in parallel, repetition by repetition, on the warm test host, a coverage
/// host (CoverageService.cs) or a debug host; results stream into the test tree as they come.
/// </summary>
internal sealed class RunExecutor(WorkspaceState state, IExplorerEffects effects, ITestHostOwner hostOwner, TestHostPool hosts,
    CoverageService coverage, LiveOutputService live, HangMonitor hang, HistoryStore history, WorkspaceSettings settings, OutputStore outputs,
    RunStatuses statuses, int maxParallel, Func<IReadOnlyList<ProjectEntry>, CancellationToken, Task<List<ProjectEntry>>> buildAndDiscover,
    Func<ProjectEntry, TestingPlatformArguments> hostArguments, Action<ProjectRun, string, CoveragePlan> recordCoverage, Action publishState)
{
    /// <summary>
    /// Executes a run request (the caller holds the op lock). Each project is built first (incremental) and
    /// re-discovered when its output assembly changed; only then does the run start (<c>runStarted</c>) and only the
    /// tests that will actually run are marked queued — tests outside the run (and the tests of a project whose build
    /// failed) keep their previous result. <c>Coverage</c> overrides the global coverage toggle for this run only.
    /// A test host that exits mid-run aborts that project's part of the run: the test(s) in progress fail with
    /// "Test host exited with code N", the rest become notRun, and the summary gets <c>aborted</c>.
    /// </summary>
    /// <param name="coverageEnabled">The coverage toggle (setCoverage) when the run starts.</param>
    public async Task ExecuteAsync(RunRequest req, bool coverageEnabled, CancellationToken ct)
    {
        var collectCoverage = req.Coverage ?? coverageEnabled;
        if (collectCoverage && req.Iterations > 1)
        {
            effects.Log(null, LogLevels.Info, "Repeat runs do not collect coverage; running without coverage.");
            collectCoverage = false;
        }
        if (collectCoverage && req.Order is not null)
        {
            effects.Log(null, LogLevels.Info, "Random-order runs do not collect coverage; running without coverage.");
            collectCoverage = false;
        }
        var x = new RunExecution(req, collectCoverage, settings.Quarantined().ToHashSet(StringComparer.Ordinal));
        try
        {
            var ready = await BuildRunProjectsAsync(req, ct);
            if (ready is null) return;
            StartRun(x, ready);
            statuses.Set(req.RunId, "running", run: x.Run);
            x.Stopwatch = Stopwatch.StartNew();

            // ---- execute: repetition by repetition, project by project
            for (var iteration = 0; iteration < x.Iterations && !ct.IsCancellationRequested && x.Aborts.Count == 0; iteration++)
                await RunIterationAsync(x, ready, iteration, ct);

            if (x.Iterations > 1)
            {
                ApplyRepeatResults(x);
                FinishTests(x, x.Requested, ct.IsCancellationRequested ? Outcome.Cancelled : null);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            effects.Log(null, LogLevels.Error, "Run failed: " + ex.Message);
        }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
        finally
        {
            if (x.Run is not null) FinishRun(x, x.Run, ct);
        }
    }

    private static bool IsFailure(Outcome o) => o is Outcome.Failed or Outcome.TimedOut;

    /// <summary>
    /// Plans the run by the tests known now, then builds and discovers every project; null (status already set) when
    /// nothing is left to run.
    /// </summary>
    private async Task<List<(ProjectEntry Project, HashSet<string>? Ids)>?> BuildRunProjectsAsync(RunRequest req, CancellationToken ct)
    {
        var plan = RunPlan(req);
        if (plan.Count == 0)
        {
            effects.Log(null, LogLevels.Warning, "Nothing to run");
            statuses.Set(req.RunId, "skipped", "Nothing to run.");
            return null;
        }

        // ---- build + discover every project first; the run starts once the binaries are ready
        statuses.Set(req.RunId, "building");
        // one build for the projects that changed, then parallel discovery (Explorer.Parallel.cs)
        var readyProjects = await buildAndDiscover(plan.Select(x => x.Project).ToList(), ct);
        var ready = plan.Where(x => readyProjects.Contains(x.Project)).ToList();
        if (ct.IsCancellationRequested)
        {
            statuses.Set(req.RunId, "cancelled", "Cancelled while building.");
            return null;
        }
        if (ready.Count == 0)
        {
            effects.Log(null, LogLevels.Warning, "Nothing to run: the build or discovery failed.");
            statuses.Set(req.RunId, "skipped", "The build or discovery failed.");
            return null;
        }
        return ready;
    }

    private List<(ProjectEntry Project, HashSet<string>? Ids)> RunPlan(RunRequest req)
    {
        var plan = new List<(ProjectEntry Project, HashSet<string>? Ids)>();
        lock (state.Sync)
        {
            foreach (var (pid, ids) in req.Selection)
            {
                var p = state.Projects.FirstOrDefault(x => x.Project.Id == pid);
                if (p is null) continue;
                var known = ids?.Where(id => state.Tests.ContainsKey((pid, id))).ToHashSet();
                if (known is { Count: 0 }) continue;
                plan.Add((p, known));
            }
        }
        return plan;
    }

    /// <summary>The run starts: queue exactly the tests that run (<c>runStarted</c>).</summary>
    private void StartRun(RunExecution x, List<(ProjectEntry Project, HashSet<string>? Ids)> ready)
    {
        var req = x.Request;
        lock (state.Sync)
        {
            foreach (var (p, ids) in ready)
                x.Requested.AddRange(state.Order.Where(k => k.ProjectId == p.Project.Id && (ids is null || ids.Contains(k.Id))));

            var run = new RunSummary
            {
                RunId = req.RunId,
                StartedAt = Json.Now(),
                Total = x.Requested.Count,
                Affected = req.Affected,
                Scope = req.IsSubset ? "subset" : "all",
                Trigger = req.Trigger,
                Watch = req.Watch,
                Repeat = x.Iterations > 1 ? new RepeatSummary { Count = x.Iterations } : null,
                Order = req.Order,
                Seed = req.Seed,
                Debug = req.Debug ? true : null,
            };
            x.Run = run;
            state.Run.Current = run;
            state.Run.CurrentRequested = x.Requested;
            foreach (var k in x.Requested)
            {
                var t = state.Tests[k];
                x.Previous[k] = t.Outcome is Outcome.Queued or Outcome.Running ? Outcome.NotRun : t.Outcome;
                t.Outcome = Outcome.Queued;
            }
            effects.Emit(new RunStartedEvent(run.Copy(), x.Requested.Select(k => k.Id).ToList()));
            effects.PublishTests(x.Requested.Select(k => state.Tests[k]));
        }
    }

    private async Task RunIterationAsync(RunExecution x, List<(ProjectEntry Project, HashSet<string>? Ids)> ready, int iteration, CancellationToken ct)
    {
        var iterationResults = x.Iterations > 1 ? new Dictionary<(string, string), Outcome>() : x.Results;
        if (iteration > 0)
        {
            lock (state.Sync)
            {
                foreach (var k in x.Requested) if (state.Tests.TryGetValue(k, out var t)) t.Outcome = Outcome.Queued;
                effects.PublishTests(x.Requested.Where(state.Tests.ContainsKey).Select(k => state.Tests[k]));
            }
        }
        if (x.Iterations > 1) effects.Log(null, LogLevels.Info, $"Repeat {iteration + 1}/{x.Iterations}");

        await RunProjectsOfIterationAsync(x, ready, iterationResults, ct); // projects in parallel (RunProjectsOfIterationAsync)

        if (x.Iterations > 1) RecordRepetition(x, iterationResults, ct);
    }

    private async Task RunProjectOfIterationAsync(RunExecution x, ProjectRun project, CancellationToken ct)
    {
        var p = project.Project;
        string? coverageFile = null;
        CoveragePlan? coveragePlan = null;
        if (x.CollectCoverage)
        {
            if (await coverage.SupportsAsync(p, ct))
            {
                coverageFile = CoverageService.FilePath(p.Project.Id, project.Run.RunId);
                coveragePlan = coverage.PlanFor(p);
            }
            else effects.Log(p.Project.Id, LogLevels.Warning, "Code coverage requested but the test app has no --coverage option " +
                "(add a reference to Microsoft.Testing.Extensions.CodeCoverage); running without coverage.");
        }

        var abort = await RunProjectAsync(project, coverageFile, coveragePlan, ct);
        if (abort is not null) lock (x.Aborts) x.Aborts.Add(abort); // projects run in parallel (RunProjectsOfIterationAsync)

        if (coverageFile is not null)
        {
            if (coveragePlan is not null && !ct.IsCancellationRequested && abort is null) CollectProjectCoverage(x, project, coverageFile, coveragePlan);
            else DeleteCoverageFile(coverageFile);
        }
        // Anything requested that never reported a result.
        if (x.Iterations == 1) FinishTests(x, project.RequestedKeys, ct.IsCancellationRequested ? Outcome.Cancelled : null);
    }

    private void CollectProjectCoverage(RunExecution x, ProjectRun project, string coverageFile, CoveragePlan coveragePlan)
    {
        var p = project.Project;
        if (!File.Exists(coverageFile))
        {
            effects.Log(p.Project.Id, LogLevels.Warning, "Coverage was enabled but no coverage file was produced.");
            return;
        }
        try
        {
            var projectAcc = new CoverageAccumulator();
            projectAcc.AddCobertura(coverageFile, coveragePlan.Filter);
            lock (x) // projects run in parallel (RunProjectsOfIterationAsync)
            {
                x.Accumulator.Add(projectAcc);
                x.ProjectCoverage.Add((p.Project.Id, project.Ids, projectAcc, coveragePlan.Info));
                x.CoveredProjects.Add(p.Project.Id);
                x.CoverageSettings.Add(coveragePlan.Info);
            }
            recordCoverage(project, coverageFile, coveragePlan); // the impact map and the per-test coverage cache
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { effects.Log(p.Project.Id, LogLevels.Error, "Could not parse coverage file: " + ex.Message); }
        finally { DeleteCoverageFile(coverageFile); }
    }

    private static void DeleteCoverageFile(string coverageFile)
    {
        try { File.Delete(coverageFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort: a temp file left in the temp folder */ }
    }

    /// <summary>Repeat run: one repetition's results go into the per-test statistics and the aggregated results.</summary>
    private void RecordRepetition(RunExecution x, Dictionary<(string, string), Outcome> iterationResults, CancellationToken ct)
    {
        lock (state.Sync)
        {
            foreach (var (k, o) in iterationResults) RecordRepetitionResultLocked(x, k, o);
            var repeat = x.Run!.Repeat!;
            if (!ct.IsCancellationRequested && x.Aborts.Count == 0) repeat.Completed++;
            repeat.PerTest = x.Requested.Where(x.RepeatStats.ContainsKey).Select(k => x.RepeatStats[k]).ToList();
            RunCounts.Update(x.Run, x.Results, x.Quarantined);
        }
    }

    private void RecordRepetitionResultLocked(RunExecution x, (string ProjectId, string Id) k, Outcome o)
    {
        if (!x.RepeatStats.TryGetValue(k, out var r)) x.RepeatStats[k] = r = new RepeatTestResult { ProjectId = k.ProjectId, Id = k.Id };
        if (o == Outcome.Passed) r.Passed++;
        else if (IsFailure(o)) r.Failed++;
        else if (o == Outcome.Skipped) r.Skipped++;
        if (state.Tests.TryGetValue(k, out var t))
        {
            if (o is not Outcome.Cancelled && t.DurationMs is { } d) r.DurationsMs.Add(d);
            if (IsFailure(o)) x.LastFailure[k] = t.Copy();
        }
        // Aggregate: failed if any repetition failed, else the latest outcome.
        if (!x.Results.TryGetValue(k, out var agg) || !IsFailure(agg) || IsFailure(o))
            x.Results[k] = o;
    }

    /// <summary>Repeat run: each test shows its aggregated result, the failing repetition's details when any failed.</summary>
    private void ApplyRepeatResults(RunExecution x)
    {
        lock (state.Sync)
        {
            var changed = new List<TestNode>();
            foreach (var (k, agg) in x.Results)
            {
                if (!state.Tests.TryGetValue(k, out var t)) continue;
                if (x.LastFailure.TryGetValue(k, out var f)) CopyFailure(f, t);
                else if (agg is not (Outcome.Queued or Outcome.Running)) t.Outcome = agg;
                if (x.RepeatStats.TryGetValue(k, out var r) && r.DurationsMs.Count > 0) t.DurationMs = Math.Round(r.DurationsMs.Average(), 2);
                changed.Add(t);
            }
            effects.PublishTests(changed);
        }
    }

    private static void CopyFailure(TestNode from, TestNode to)
    {
        to.Outcome = from.Outcome;
        to.ErrorMessage = from.ErrorMessage;
        to.ErrorType = from.ErrorType;
        to.StackTrace = from.StackTrace;
        to.StdOut = from.StdOut;
        to.StdErr = from.StdErr;
        to.OutputTruncated = from.OutputTruncated;
        to.TraceId = from.TraceId;
    }

    /// <summary>
    /// Settles the requested tests that are still queued / running: <paramref name="outcome"/>, or (null) what the test had
    /// before the run (e.g. the build failed).
    /// </summary>
    private void FinishTests(RunExecution x, List<(string, string)> keys, Outcome? outcome)
    {
        lock (state.Sync)
        {
            var changed = new List<TestNode>();
            foreach (var k in keys)
            {
                if (state.Tests.TryGetValue(k, out var t) && t.Outcome is Outcome.Queued or Outcome.Running)
                {
                    LiveFinished(t);
                    t.Outcome = outcome ?? x.Previous.GetValueOrDefault(k, Outcome.NotRun);
                    changed.Add(t);
                }
            }
            effects.PublishTests(changed);
        }
    }

    /// <summary>The end of a run that started, whichever way it ended: coverage, final counts, history and the summary log line.</summary>
    private void FinishRun(RunExecution x, RunSummary run, CancellationToken ct)
    {
        try
        {
            if (x.CoveredProjects.Count > 0 && !ct.IsCancellationRequested) coverage.Publish(x, run.RunId);
            else coverage.RefreshFullStale();
        }
        catch (Exception ex) // whatever went wrong with the coverage reports, the run still ends below
        {
            effects.Log(null, LogLevels.Error, "Could not update the coverage report: " + ex.Message);
        }
        // Every requested test leaves queued / running, whichever way the run ended (cancelled before a project's
        // turn, an exception, ...): cancelled when the run was cancelled, else its result from before the run.
        FinishTests(x, x.Requested, ct.IsCancellationRequested ? Outcome.Cancelled : null);
        HashSet<(string ProjectId, string Id)>? mixed = x.Iterations > 1
            ? x.RepeatStats.Where(kv => kv.Value.Passed > 0 && kv.Value.Failed > 0).Select(kv => kv.Key).ToHashSet()
            : null;
        RecordFinishedRun(x, run, mixed, ct);
        statuses.Set(run.RunId, FinalRunStatus(run), run.AbortReason, run);
        // Test failures are an expected outcome, not a host problem: info level (errors are for build / host / process failures).
        var failedNames = FailedTestNames(x);
        if (failedNames.Count > 0)
            effects.Log(null, LogLevels.Info, $"Failed ({failedNames.Count}): {string.Join(", ", failedNames.Take(10))}{Words.MoreSuffix(failedNames.Count, 10)}");
        effects.Log(null, run.Aborted == true ? LogLevels.Warning : LogLevels.Info, RunFinishedLine(run, mixed?.Count ?? 0));
    }

    private void RecordFinishedRun(RunExecution x, RunSummary run, HashSet<(string ProjectId, string Id)>? mixed, CancellationToken ct)
    {
        lock (state.Sync)
        {
            run.FinishedAt = Json.Now();
            run.DurationMs = Math.Round((x.Stopwatch?.Elapsed ?? TimeSpan.Zero).TotalMilliseconds, 1);
            RunCounts.Update(run, x.Results, x.Quarantined);
            run.Total = Math.Max(run.Total, x.Results.Count);
            var notRun = x.Requested.Count(k => !x.Results.ContainsKey(k) || x.Results[k] == Outcome.Cancelled);
            if (ct.IsCancellationRequested && notRun > 0) run.Cancelled = true;
            if (x.Aborts.Count > 0)
            {
                run.Aborted = true;
                run.AbortReason = string.Join(" ", x.Aborts);
            }
            run.NotRun = notRun > 0 ? notRun : null;
            state.Run.Current = null;
            state.Run.CurrentRequested = null;
            LiveReset();
            state.Run.Last = run;
            effects.PublishTests(history.Record(run, x.Results.Keys.Where(state.Tests.ContainsKey).Select(k => state.Tests[k]).ToList(), mixed));
            effects.Emit(new RunsUpdatedEvent(history.Runs()));
            effects.Emit(new RunFinishedEvent(run.Copy()));
            // Clients that kept showing the previous result of tests that never ran (UI "pending" display) resync.
            if (x.Aborts.Count > 0) publishState();
        }
    }

    private List<string> FailedTestNames(RunExecution x)
    {
        lock (state.Sync)
            return x.Results.Where(kv => IsFailure(kv.Value) && state.Tests.ContainsKey(kv.Key))
                .Select(kv => state.Tests[kv.Key].DisplayName
                    + (x.Quarantined.Contains(WorkspaceSettings.QuarantineKey(kv.Key.Item1, kv.Key.Item2)) ? " (quarantined)" : ""))
                .ToList();
    }

    private static string FinalRunStatus(RunSummary run) => run switch
    {
        { Aborted: true } => "aborted",
        { Cancelled: true } => "cancelled",
        _ => "finished",
    };

    private static string RunFinishedLine(RunSummary run, int flaky)
    {
        var verb = run switch
        {
            { Aborted: true } => "Run aborted",
            { Cancelled: true } => "Run cancelled",
            _ => "Run finished",
        };
        return $"{verb}: {run.Passed} passed, {run.Failed} failed, {run.Skipped} skipped" +
            (run.Quarantined is { } q ? $", {q} quarantined" : "") +
            (run.NotRun is { } nr ? $", {nr} not run" : "") +
            (run.Repeat is { } rep ? $" ({rep.Completed}/{rep.Count} repetitions, {flaky} flaky)" : "") +
            $", {run.Total} total in {run.DurationMs / 1000:0.0}s" + (run.AbortReason is { } why ? $". {why}" : "");
    }

    // ------------------------------------------------------------------ one project

    /// <param name="coverageFile">When set, the run collects Cobertura coverage into this file.</param>
    /// <returns>The abort reason when the test host exited mid-run, else null.</returns>
    private async Task<string?> RunProjectAsync(ProjectRun pr, string? coverageFile, CoveragePlan? coveragePlan, CancellationToken ct)
    {
        var p = pr.Project;
        effects.SetStatus(p, ProjectStatus.Running);
        var runId = Guid.NewGuid();
        var subset = KnownSubset(pr);
        if (subset is { Count: 0 })
        {
            effects.SetStatus(p, ProjectStatus.Idle);
            return null;
        }

        try
        {
            string[] extraArgs = coverageFile is null || coveragePlan is null ? [] : coveragePlan.Arguments(coverageFile);
            Action<string, JsonNode?> onUpdate = (method, prm) => OnRunUpdate(pr, method, prm);
            if (pr.Debug) await RunDebugHostAsync(pr, extraArgs, onUpdate, runId, subset, ct);
            else if (coverageFile is null)
            {
                // Warm test host: the process (and whatever its fixtures keep alive) is reused across runs.
                await hosts.RequestAsync(p, onUpdate, (conn, token) => RunInOrderAsync(conn, pr, runId, subset, token), ct);
                await CheckWarmAfterRunAsync(p, pr.RequestedKeys, ct);
            }
            else await RunCoverageHostAsync(p, coveragePlan!, extraArgs, onUpdate, runId, subset, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            effects.Log(p.Project.Id, LogLevels.Info, "Run cancelled" + (ex is MtpRpcException { Code: -32800 } or OperationCanceledException or IOException ? "" : ": " + ex.Message));
        }
        catch (TestHostExitedException ex)
        {
            var reason = SettleCrashedRun(pr, ex);
            effects.SetStatus(p, ProjectStatus.Idle);
            effects.Log(p.Project.Id, LogLevels.Error, reason + (ex.StderrTail.Count > 0 ? "\n" + string.Join('\n', ex.StderrTail.TakeLast(5)) : ""));
            return reason;
        }
        catch (Exception ex)
        {
            effects.SetStatus(p, ProjectStatus.Error, "Run failed: " + ex.Message);
            effects.Log(p.Project.Id, LogLevels.Error, "Run failed: " + ex.Message);
            return null;
        }
        effects.SetStatus(p, ProjectStatus.Idle);
        return null;
    }

    /// <summary>
    /// <c>testing/runTests</c> for a project's tests. In random order (RunSummary.order) one request per test, in the order
    /// the seed gives: MTP runs the requested nodes in the framework's own order and ignores the order of the list (docs/protocol.md
    /// "Test order"), so a request per test is the only way to control it. Otherwise one request for all of them.
    /// </summary>
    private async Task<JsonNode?> RunInOrderAsync(MtpConnection conn, ProjectRun pr, Guid runId, List<(string, string)>? subset, CancellationToken ct)
    {
        if (pr.Run.Order is null || pr.Run.Seed is not { } seed) return await conn.RunAsync(runId, subset, ct);
        var projectId = pr.Project.Project.Id;
        List<(string Id, string Name)> tests;
        lock (state.Sync)
            tests = subset ?? pr.RequestedKeys.Where(k => state.Tests.ContainsKey(k)).Select(k => (k.Id, state.Tests[k].DisplayName)).ToList();
        var shuffled = RandomOrder.Shuffle(tests, seed, t => t.Id);
        effects.Log(projectId, LogLevels.Info, $"Random order, seed {seed}: {shuffled.Count} tests, one at a time (reproduce with the same seed).");
        JsonNode? last = null;
        for (var i = 0; i < shuffled.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            last = await conn.RunAsync(i == 0 ? runId : Guid.NewGuid(), [shuffled[i]], ct);
        }
        return last;
    }

    /// <summary>The (id, display name) of the selected tests that are still known; null = every test of the project.</summary>
    private List<(string, string)>? KnownSubset(ProjectRun pr)
    {
        if (pr.Ids is null) return null;
        var projectId = pr.Project.Project.Id;
        lock (state.Sync)
            return pr.Ids.Where(id => state.Tests.ContainsKey((projectId, id))).Select(id => (id, state.Tests[(projectId, id)].DisplayName)).ToList();
    }

    /// <summary>A <c>testing/testUpdates/tests</c> notification of a run: results and in-progress states.</summary>
    private void OnRunUpdate(ProjectRun pr, string method, JsonNode? prm)
    {
        if (method != "testing/testUpdates/tests" || prm?["changes"] is not JsonArray changes) return;
        lock (state.Sync)
        {
            var changed = new List<TestNode>(); // deduped below: a batch can hold in-progress + final for one test
            foreach (var c in changes)
            {
                if (c?["node"] is not JsonObject node || !MtpMapping.IsTestNode(node)) continue;
                if (ApplyRunUpdateLocked(pr, node) is { } t && !changed.Contains(t)) changed.Add(t);
            }
            effects.PublishTests(changed);
        }
    }

    /// <summary>Applies one test node update of a run; returns the test when it changed in a way to publish.</summary>
    private TestNode? ApplyRunUpdateLocked(ProjectRun pr, JsonObject node)
    {
        var projectId = pr.Project.Project.Id;
        var incoming = MtpMapping.ToTestNode(projectId, node);
        var key = (projectId, incoming.Id);
        if (!state.Tests.TryGetValue(key, out var t))
        {
            // Test that was not known from discovery (e.g. dynamic data) — add it.
            t = incoming;
            state.Tests[key] = t;
            var insertAt = state.Order.FindLastIndex(k => k.ProjectId == projectId);
            state.Order.Insert(insertAt < 0 ? state.Order.Count : insertAt + 1, key);
        }
        var executionState = node["execution-state"]?.GetValue<string>();
        var outcome = MtpMapping.MapState(executionState);
        if (outcome is null) return null;
        if (outcome == Outcome.Running)
        {
            // Later in-progress updates of a running test only carry its output so far (TUnit streaming).
            var started = t.Outcome != Outcome.Running;
            if (started)
            {
                t.ClearResult();
                t.Outcome = Outcome.Running;
                LiveStarted(t);
            }
            LivePartial(t, incoming); // LiveOutputService.cs
            return started ? t : null;
        }
        if (hang.IsTimedOutLocked(key)) return null; // timed out (HangMonitor.cs): a late "cancelled" keeps that
        LiveFinished(t);
        MtpMapping.ApplyResult(t, incoming, outcome.Value, executionState);
        CapOutput(t);
        pr.Results[key] = outcome.Value;
        RunCounts.Update(pr.Run, pr.Results, pr.Quarantined);
        return t;
    }

    /// <summary>Debug run: never the warm host, a fresh process that waits in startup until a debugger attaches.</summary>
    private async Task RunDebugHostAsync(ProjectRun pr, string[] extraArgs, Action<string, JsonNode?> onUpdate, Guid runId,
        List<(string, string)>? subset, CancellationToken ct)
    {
        var p = pr.Project;
        var run = pr.Run;
        await using var host = hostOwner.StartHost(p, extraArgs, onUpdate, new Dictionary<string, string> { [Explorer.WaitAttachDebuggerEnvVar] = "1" });
        var testIds = subset?.Select(s => s.Item1).ToList() ?? [];
        lock (state.Sync) effects.Emit(new DebuggerWaitingEvent(p.Project.Id, host.ProcessId, testIds, run.RunId, Explorer.DebugHints(host.ProcessId)));
        effects.Log(p.Project.Id, LogLevels.Info, $"Debug run: the test host (pid {host.ProcessId}) waits for a debugger to attach " +
            $"(VS Code: .NET attach / Rider: Attach to Process / Visual Studio: Debug > Attach to Process). Cancel to give up.");
        var attached = 0;
        await host.RunOperationAsync(async (conn, token) =>
        {
            if (Interlocked.Exchange(ref attached, 1) == 0)
            {
                lock (state.Sync) effects.Emit(new DebuggerAttachedEvent(p.Project.Id, host.ProcessId, run.RunId));
                effects.Log(p.Project.Id, LogLevels.Info, "Debugger attached; running the tests");
            }
            await conn.InitializeAsync(token);
            return await conn.RunAsync(runId, subset, token);
        }, ct, connectTimeout: Explorer.DebugAttachTimeout);
        await host.ShutdownAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Coverage needs the controller launch (out-of-process extension): one-off process, never the warm host (see
    /// docs/troubleshooting.md). What happens to the warm host meanwhile: CoverageService.RunProcessAsync.
    /// </summary>
    private async Task RunCoverageHostAsync(ProjectEntry p, CoveragePlan coveragePlan, string[] extraArgs, Action<string, JsonNode?> onUpdate, Guid runId,
        List<(string, string)>? subset, CancellationToken ct)
    {
        await coverage.RunProcessAsync(p, coveragePlan, async () =>
        {
            await using var host = hostOwner.StartHost(p, extraArgs, onUpdate);
            await host.RunOperationAsync(async (conn, token) =>
            {
                await conn.InitializeAsync(token);
                return await conn.RunAsync(runId, subset, token);
            }, ct);
            // The coverage extension writes its file while the process shuts down: wait for a clean exit.
            await host.ShutdownAsync(TimeSpan.FromSeconds(60));
        }, ct);
    }

    /// <summary>
    /// After a run on the warm host: replace the host when an assembly was rewritten in place during the run (results may
    /// mix old and new code), or when tests failed the way such a host fails (BadImageFormatException, "Index not found
    /// (0x80131124)"), so the next run gets a process with intact assemblies.
    /// </summary>
    private async Task CheckWarmAfterRunAsync(ProjectEntry p, List<(string ProjectId, string Id)> requestedKeys, CancellationToken ct)
    {
        var warm = hosts.CurrentWarm(p);
        if (warm is null || ct.IsCancellationRequested) return;
        if (warm.Watch.Check() is { } rewritten)
        {
            effects.Log(p.Project.Id, LogLevels.Warning, $"{rewritten} was rewritten while the tests ran (a build?): results may come from a mix of old and new code. Run them again.");
            await hosts.RenewAsync(p, warm, TestHostPool.RewrittenReason(rewritten), ct);
            return;
        }
        int corrupt;
        lock (state.Sync)
            corrupt = requestedKeys.Count(k => state.Tests.TryGetValue(k, out var t) && t.Outcome is Outcome.Failed && LooksLikeStaleAssembly(t));
        if (corrupt == 0) return;
        effects.Log(p.Project.Id, LogLevels.Warning, $"{corrupt} test{Words.Plural(corrupt)} failed with BadImageFormatException / \"Index not found (0x80131124)\": " +
            "the warm test host had assemblies loaded that changed on disk. Run them again.");
        await hosts.RenewAsync(p, warm, "its loaded assemblies no longer match the files on disk", ct);
    }

    private static bool LooksLikeStaleAssembly(TestNode t) =>
        t.ErrorType is "System.BadImageFormatException"
        || (t.ErrorMessage?.Contains("0x80131124", StringComparison.Ordinal) ?? false)
        || (t.ErrorMessage?.Contains("BadImageFormatException", StringComparison.Ordinal) ?? false);
    /// <summary>
    /// The test host exited mid-run (Environment.Exit, StackOverflow, FailFast, native crash, killed): the test(s) in
    /// progress fail with "Test host exited with code N" (+ the last stderr lines), the project's other requested tests
    /// that did not report become notRun (their old result is cleared, never left "passed"). When the host died before
    /// it reported any progress and exactly one test was left, that test is the one that was running.
    /// </summary>
    private string SettleCrashedRun(ProjectRun pr, TestHostExitedException ex)
    {
        var code = ex.ExitCode is { } c ? c.ToString() : "?";
        lock (state.Sync)
        {
            var open = pr.RequestedKeys.Where(k => state.Tests.TryGetValue(k, out var t) && t.Outcome is Outcome.Queued or Outcome.Running).ToList();
            var running = open.Where(k => state.Tests[k].Outcome == Outcome.Running).ToList();
            if (running.Count == 0 && open.Count == 1) running = open;
            var stderr = ex.StderrTail.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(10).ToList();
            var changed = new List<TestNode>();
            foreach (var k in open)
            {
                var t = state.Tests[k];
                LiveFinished(t);
                t.ClearResult();
                t.Stale = null;
                if (running.Contains(k))
                {
                    MarkCrashed(t, code, stderr);
                    pr.Results[k] = Outcome.Failed;
                }
                else t.Outcome = Outcome.NotRun;
                changed.Add(t);
            }
            RunCounts.Update(pr.Run, pr.Results, pr.Quarantined);
            effects.PublishTests(changed);
            var names = running.Select(k => state.Tests[k].DisplayName).ToList();
            return CrashReason(pr.Project.Project.Name, code, names, skipped: open.Count - running.Count);
        }
    }

    private static void MarkCrashed(TestNode t, string code, List<string> stderr)
    {
        t.Outcome = Outcome.Failed;
        t.ErrorType = Explorer.TestHostCrashedErrorType;
        t.ErrorMessage = $"Test host exited with code {code} while this test was running." +
                         (stderr.Count > 0 ? "\n\nLast stderr lines:\n" + string.Join('\n', stderr) : "");
        t.LastRunAt = Json.Now();
    }

    private static string CrashReason(string projectName, string code, List<string> running, int skipped)
    {
        var what = running.Count > 0
            ? $" while running {string.Join(", ", running.Take(3))}{Words.MoreSuffix(running.Count, 3)}"
            : " before it reported which test was running";
        var rest = skipped > 0 ? $"; {skipped} test{Words.Plural(skipped)} did not run." : ".";
        return $"Test host of {projectName} exited with code {code}" + what + rest;
    }

    /// <summary>
    /// One repetition of a run: the projects run at the same time (up to maxParallelProjects), each with its
    /// own cancellation (linked to the run's) so a test timeout stops that project only (HangMonitor.cs). Results stream
    /// as they come; the tree keeps its order (discovery order).
    /// </summary>
    private async Task RunProjectsOfIterationAsync(RunExecution x, List<(ProjectEntry Project, HashSet<string>? Ids)> ready,
        Dictionary<(string, string), Outcome> iterationResults, CancellationToken ct)
    {
        await BoundedParallel.ForEachAsync(ready, maxParallel, async item =>
        {
            var (p, ids) = item;
            if (ct.IsCancellationRequested) return false;
            var projectRequested = x.Requested.Where(k => k.ProjectId == p.Project.Id).ToList();
            var project = new ProjectRun(p, ids, x.Run!, iterationResults, x.Quarantined, projectRequested, x.Request.Debug);
            var args = hostArguments(p);
            using var scope = hang.BeginProjectRun(project, args.SessionTimeout, args.SessionTimeoutText, ct);
            await RunProjectOfIterationAsync(x, project, scope.Token);
            if (scope.TimeoutReason is { } why) lock (x.Aborts) x.Aborts.Add(why);
            return true;
        }, ct);
    }

    // ------------------------------------------------------------------ test lifecycle (caller holds Sync)

    private void LiveStarted(TestNode t)
    {
        live.Started(t);
        hang.Started(t);
    }

    private void LivePartial(TestNode t, TestNode incoming) => live.Partial(t, incoming);

    private void LiveFinished(TestNode t)
    {
        if (t.Outcome != Outcome.Running) return;
        live.Finished(t);
        hang.Finished(t);
    }

    private void LiveReset()
    {
        live.Reset();
        hang.Reset();
    }

    /// <summary>Caps a fresh result's stdOut/stdErr (<see cref="TestOutput"/>) and keeps the full text of cut outputs.</summary>
    private void CapOutput(TestNode t)
    {
        var fullOut = t.StdOut;
        var fullErr = t.StdErr;
        t.StdOut = TestOutput.Cap(fullOut, out var cutOut);
        t.StdErr = TestOutput.Cap(fullErr, out var cutErr);
        if (cutOut || cutErr)
        {
            t.OutputTruncated = true;
            outputs.Put(t.ProjectId, t.Id, fullOut, fullErr);
        }
        else
        {
            t.OutputTruncated = null;
            outputs.Remove(t.ProjectId, t.Id);
        }
    }
}
