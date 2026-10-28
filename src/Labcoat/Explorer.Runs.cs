namespace Labcoat;

// Round 4: run request validation + coalescing (at most one queued run), cancel that clears the queue, run status for
// REST callers (Aspire dashboard commands), suite summary, debug runs, target framework selection, lean payloads.
public sealed partial class Explorer
{
    /// <summary>Result of <see cref="RequestRun"/>: HTTP-like status (202 accepted, 400 / 404 rejected).</summary>
    public sealed record RunRequestResult(int Status, string? RunId = null, string? Error = null, bool Queued = false, bool Merged = false,
        List<string>? UnknownTestIds = null, List<string>? KnownProjects = null, Task? Completion = null);

    /// <summary>Lifecycle of a run request, for <c>GET /api/runs/{runId}/status</c>.</summary>
    public sealed record RunRequestStatus(string RunId, string State, string? Reason = null, RunSummary? Run = null, string? SupersededBy = null);

    private readonly Dictionary<string, RunRequestStatus> _runStatus = [];
    private readonly Queue<string> _runStatusOrder = new();
    private void SetRunStatus(string runId, string state, string? reason = null, RunSummary? run = null, string? supersededBy = null)
    {
        lock (Sync)
        {
            if (!_runStatus.ContainsKey(runId))
            {
                _runStatusOrder.Enqueue(runId);
                while (_runStatusOrder.Count > 200) _runStatus.Remove(_runStatusOrder.Dequeue());
            }
            _runStatus[runId] = new RunRequestStatus(runId, state, reason, run?.Copy(), supersededBy);
        }
    }

    /// <summary>
    /// "queued" | "building" (build + discovery before the run) | "running" | "finished" | "cancelled" | "aborted" |
    /// "skipped" (nothing could run, e.g. the build failed) | "superseded" (replaced by another request, see supersededBy);
    /// null when unknown (or older than the last 200 requests).
    /// </summary>
    public RunRequestStatus? GetRunStatus(string runId)
    {
        lock (Sync) return _runStatus.GetValueOrDefault(runId);
    }

    /// <summary>A run request (<see cref="RequestRun(RunRequestOptions)"/>): what to run and how.</summary>
    public sealed record RunRequestOptions
    {
        /// <summary>One project, or every project when null.</summary>
        public string? ProjectId { get; init; }
        /// <summary>The tests to run, or all when null or empty.</summary>
        public IReadOnlyCollection<string>? TestIds { get; init; }
        public bool? Coverage { get; init; }
        public string? Trigger { get; init; }
        public int? Repeat { get; init; }
        public AffectedRun? Affected { get; init; }
        public WatchContext? WatchContext { get; init; }
        public bool Debug { get; init; }
        /// <summary>An exact per-project selection (run affected): null test ids = every test of the project.</summary>
        public Dictionary<string, HashSet<string>?>? Selection { get; init; }
    }

    /// <summary>
    /// Validates a run request and queues it. Unknown project → 404; test ids of which none is known → 404 (some unknown →
    /// they are ignored and listed in <see cref="RunRequestResult.UnknownTestIds"/>). At most one request waits while
    /// another operation runs; a further request is merged into it (union of the selected tests, "run all" absorbs
    /// subsets) when coverage / repeat / debug match, otherwise it replaces the waiting one ("superseded").
    /// </summary>
    public RunRequestResult RequestRun(string? projectId, IReadOnlyCollection<string>? testIds) =>
        RequestRun(new RunRequestOptions { ProjectId = projectId, TestIds = testIds });

    /// <inheritdoc cref="RequestRun(string?, IReadOnlyCollection{string}?)"/>
    public RunRequestResult RequestRun(RunRequestOptions o)
    {
        var iterations = o.Repeat is null or <= 1 ? 1 : Math.Min(o.Repeat.Value, MaxRepeat);
        if (o.Repeat > MaxRepeat) Log(null, LogLevels.Warning, $"repeat is limited to {MaxRepeat}; running {MaxRepeat} times");
        RunRequest req;
        List<string>? unknown;
        RunRequest? superseded;
        bool merged, queued;
        lock (Sync)
        {
            var (selection, unknownIds, rejected) = ResolveSelectionLocked(o);
            if (rejected is not null) return rejected;
            unknown = unknownIds;
            if (o.Debug && !IsTestsOfOneProject(selection))
                return new RunRequestResult(400, Error: "debug needs testIds of exactly one project.");

            (req, merged, superseded) = QueueLocked(new RunRequest
            {
                RunId = Guid.NewGuid().ToString(),
                RequestedAt = Json.Now(),
                Selection = selection,
                Coverage = o.Debug ? false : o.Coverage,
                Iterations = o.Debug ? 1 : iterations,
                Trigger = TriggerOf(o),
                Affected = o.Affected,
                Watch = o.WatchContext,
                Debug = o.Debug,
            });
            queued = _gate.IsBusy;
            Emit(new PendingRunEvent(req.Info()));
        }

        AnnounceQueued(req, merged, superseded);
        if (unknown is not null) Log(null, LogLevels.Warning, $"Ignoring {unknown.Count} unknown test id(s): {string.Join(", ", unknown.Take(5))}");
        return new RunRequestResult(202, req.RunId, Queued: queued, Merged: merged, UnknownTestIds: unknown?.Take(100).ToList(), Completion: req.Completion.Task);
    }

    private static bool IsTestsOfOneProject(Dictionary<string, HashSet<string>?> selection) =>
        selection.Count == 1 && selection.Values.All(s => s is not null);

    /// <summary>After a request was queued: the request it replaced is superseded; a new waiting request gets processed.</summary>
    private void AnnounceQueued(RunRequest req, bool merged, RunRequest? superseded)
    {
        if (superseded is not null)
        {
            SetRunStatus(superseded.RunId, "superseded", "A different run request replaced it while it was waiting.", supersededBy: req.RunId);
            superseded.Completion.TrySetResult();
            Log(null, LogLevels.Info, $"Queued run request replaced by a new one ({(req.Debug ? "debug" : DescribeOptions(req))}).");
        }
        if (merged) Log(null, LogLevels.Info, $"Run request merged into the queued run ({req.Requests} requests).");
        else
        {
            SetRunStatus(req.RunId, "queued");
            _ = ProcessPendingRunAsync(_gate.CurrentToken());
        }
    }

    /// <summary>RunSummary.trigger of runs started by watch mode.</summary>
    private const string WatchTrigger = "watch";

    private static string? TriggerOf(RunRequestOptions o)
    {
        if (o.Debug) return "debug";
        return o.Trigger ?? (o.Affected is not null ? WatchTrigger : null);
    }

    /// <summary>
    /// The per-project selection of a request (null test ids = the whole project), the unknown test ids it ignores, or
    /// the rejection (404) when nothing of it is known.
    /// </summary>
    private (Dictionary<string, HashSet<string>?> Selection, List<string>? Unknown, RunRequestResult? Rejected) ResolveSelectionLocked(RunRequestOptions o)
    {
        var known = _state.Projects.Select(p => p.Project.Id).ToList();
        // An exact per-project selection (run affected): known projects only; null = every test of the project.
        if (o.Selection is not null)
        {
            var selection = o.Selection.Where(kv => known.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value is null ? null : kv.Value.Where(id => _state.Tests.ContainsKey((kv.Key, id))).ToHashSet());
            selection = selection.Where(kv => kv.Value is null || kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            return selection.Count == 0
                ? ([], null, new RunRequestResult(404, Error: "None of the selected tests is known.", KnownProjects: known))
                : (selection, null, null);
        }
        List<ProjectEntry> projects;
        if (o.ProjectId is null) projects = [.. _state.Projects];
        else if (_state.Projects.FirstOrDefault(p => p.Project.Id == o.ProjectId) is { } p) projects = [p];
        else return ([], null, new RunRequestResult(404, Error: $"Unknown project '{o.ProjectId}'.", KnownProjects: known));
        if (projects.Count == 0) return ([], null, new RunRequestResult(404, Error: "No test projects.", KnownProjects: known));

        // A selection from the caller (per project) is used as is.
        if (o.TestIds is { Count: > 0 }) return SelectTestIdsLocked(projects, o.TestIds, o.ProjectId);
        var all = new Dictionary<string, HashSet<string>?>();
        foreach (var id in projects.Select(pr => pr.Project.Id)) all[id] = null;
        return (all, null, null);
    }

    private (Dictionary<string, HashSet<string>?> Selection, List<string>? Unknown, RunRequestResult? Rejected) SelectTestIdsLocked(
        List<ProjectEntry> projects, IReadOnlyCollection<string> testIds, string? projectId)
    {
        var selection = new Dictionary<string, HashSet<string>?>();
        var ids = testIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        foreach (var pid in projects.Select(pr => pr.Project.Id))
        {
            var inProject = ids.Where(id => _state.Tests.ContainsKey((pid, id))).ToHashSet();
            if (inProject.Count > 0) selection[pid] = inProject;
        }
        var unknown = ids.Where(id => !selection.Values.Any(s => s!.Contains(id))).ToList();
        if (selection.Count == 0)
            return (selection, null, new RunRequestResult(404, Error: $"None of the {ids.Count} test id(s) is known" + (projectId is null ? "." : $" in project '{projectId}'."),
                UnknownTestIds: unknown.Take(100).ToList()));
        return (selection, unknown.Count == 0 ? null : unknown, null);
    }

    /// <summary>
    /// At most one request waits: a new one is merged into it when they are compatible, otherwise it replaces it.
    /// Returns the request that now waits.
    /// </summary>
    private (RunRequest Req, bool Merged, RunRequest? Superseded) QueueLocked(RunRequest req)
    {
        if (_state.Run.Pending is not { } pending)
        {
            _state.Run.Pending = req;
            return (req, false, null);
        }
        if (CanMerge(pending, req))
        {
            Merge(pending, req);
            return (pending, true, null);
        }
        _state.Run.Pending = req;
        return (req, false, pending);
    }

    private static string DescribeOptions(RunRequest r) =>
        (r.IsSubset ? $"{r.Selection.Values.Sum(s => s!.Count)} tests" : "all tests") +
        r.Coverage switch { true => ", coverage", false => ", no coverage", null => "" } + (r.Iterations > 1 ? $", repeat {r.Iterations}" : "");

    private static bool CanMerge(RunRequest a, RunRequest b) =>
        !a.Debug && !b.Debug && a.Coverage == b.Coverage && a.Iterations == b.Iterations;

    /// <summary>Union of <paramref name="from"/>'s selection into <paramref name="into"/>; "all tests of a project" absorbs subsets.</summary>
    private static void Merge(RunRequest into, RunRequest from)
    {
        foreach (var (pid, ids) in from.Selection)
        {
            if (!into.Selection.TryGetValue(pid, out var existing)) into.Selection[pid] = ids is null ? null : [.. ids];
            else if (existing is null || ids is null) into.Selection[pid] = null;
            else existing.UnionWith(ids);
        }
        into.Requests++;
        // A user / API request wins over watch: the merged run is no longer "just the affected tests".
        if (into.Trigger != from.Trigger && into.Trigger == WatchTrigger) into.Trigger = from.Trigger;
        MergeAffected(into, from);
    }

    /// <summary>A merged watch run: the changed files of both; any other merged run is no longer "the affected tests".</summary>
    private static void MergeAffected(RunRequest into, RunRequest from)
    {
        if (into.Affected is not null && from.Affected is not null && into.Trigger == WatchTrigger)
        {
            var files = into.Affected.ChangedFiles.Union(from.Affected.ChangedFiles).ToList();
            var strategy = into.Affected.Strategy == from.Affected.Strategy ? into.Affected.Strategy : "mixed";
            into.Affected = new AffectedRun { Strategy = strategy, ChangedFiles = files };
            into.Watch = into.Watch is null || from.Watch is null ? into.Watch
                : new WatchContext { ChangedFiles = files, Strategy = strategy, BuildMs = (into.Watch.BuildMs ?? 0) + (from.Watch.BuildMs ?? 0) };
        }
        else if (into.Trigger != WatchTrigger)
        {
            into.Affected = null;
            into.Watch = null;
        }
    }

    /// <summary>Waits for the op lock, then runs whatever request is pending at that moment (there may be none any more).</summary>
    private async Task ProcessPendingRunAsync(CancellationToken ct)
    {
        if (!await _gate.AcquireAsync("run", ct)) return; // cancelled while waiting: Cancel() cleared the queue
        RunRequest? req;
        lock (Sync)
        {
            req = _state.Run.Pending;
            _state.Run.Pending = null;
            if (req is not null) Emit(new PendingRunEvent(null));
        }
        if (req is null)
        {
            _gate.Release();
            return;
        }
        try { await ExecuteRunAsync(req, ct); }
        finally
        {
            _gate.Release();
            req.Completion.TrySetResult();
        }
    }

    /// <summary>Waits until the request with this id is done (tests / embedding; REST callers poll the status).</summary>
    public Task RunAsync(string? projectId, IReadOnlyCollection<string>? testIds, bool? coverage = null, AffectedRun? affected = null,
        string? trigger = null, int? repeat = null, WatchContext? watchContext = null)
    {
        var r = RequestRun(new RunRequestOptions
        {
            ProjectId = projectId, TestIds = testIds, Coverage = coverage, Trigger = trigger, Repeat = repeat, Affected = affected, WatchContext = watchContext,
        });
        if (r.Status != 202) Log(null, LogLevels.Warning, "Run not started: " + r.Error);
        return r.Completion ?? Task.CompletedTask;
    }

    /// <summary>Cancel: the running operation (if any) and the queued run request (if any).</summary>
    public sealed record CancelResult(bool Running, string? ActiveOperation, string? ClearedRunId, int ClearedRequests);

    public CancelResult Cancel()
    {
        OperationGate.Cancellation cancellation;
        RunRequest? pending;
        lock (Sync)
        {
            cancellation = _gate.BeginCancel();
            pending = _state.Run.Pending;
            _state.Run.Pending = null;
            if (pending is not null) Emit(new PendingRunEvent(null));
            // Tests of the current run that have not started will not run: cancelled now, not once the test host has
            // stopped (that can take the cancel grace period). A result that still arrives replaces it.
            if (_state.Run.CurrentRequested is { } keys)
            {
                var changed = new List<TestNode>();
                foreach (var k in keys)
                {
                    if (_state.Tests.TryGetValue(k, out var t) && t.Outcome == Outcome.Queued)
                    {
                        t.Outcome = Outcome.Cancelled;
                        changed.Add(t);
                    }
                }
                PublishTests(changed);
            }
        }
        if (pending is not null)
        {
            SetRunStatus(pending.RunId, "cancelled", "Cancelled while queued.");
            pending.Completion.TrySetResult();
        }
        var active = cancellation.Active;
        Log(null, LogLevels.Info, "Cancellation requested" +
            (active is not null ? $" (stopping the current {active})" : " (nothing was running)") +
            (pending is not null ? $"; cleared the queued run ({pending.Requests} request{Plural(pending.Requests)})" : ""));
        _gate.EndCancel(cancellation);
        return new CancelResult(active is not null, active, pending?.RunId, pending?.Requests ?? 0);
    }

    // ------------------------------------------------------------------ summary (GET /api/summary)

    public sealed record OutcomeCounts(int Total, int Passed, int Failed, int Skipped, int NotRun, int Running, int Queued, int Cancelled);
    public sealed record ProjectSummary(string Id, string Name, ProjectStatus Status, string? HostState, string? LastError, int Tests, string? TargetFramework);
    /// <param name="Cancelling">True between a cancel and the end of the operation it stops (null otherwise).</param>
    public sealed record SummaryInfo(string Version, string? WorkspaceName, OutcomeCounts Tests, RunSummary? CurrentRun, RunSummary? LastRun,
        PendingRunInfo? PendingRun, string? ActiveOperation, bool Watch, bool CoverageEnabled, List<ProjectSummary> Projects, bool? Cancelling = null);

    /// <summary>Cheap overview for dashboards (Aspire resource state): counts by outcome, current/last run, project states.</summary>
    public SummaryInfo Summary()
    {
        lock (Sync)
        {
            int passed = 0, failed = 0, skipped = 0, notRun = 0, running = 0, queued = 0, cancelled = 0;
            var perProject = new Dictionary<string, int>();
            foreach (var t in _state.Tests.Values)
            {
                perProject[t.ProjectId] = perProject.GetValueOrDefault(t.ProjectId) + 1;
                switch (t.Outcome)
                {
                    case Outcome.Passed: passed++; break;
                    case Outcome.Failed or Outcome.TimedOut: failed++; break;
                    case Outcome.Skipped: skipped++; break;
                    case Outcome.Running: running++; break;
                    case Outcome.Queued: queued++; break;
                    case Outcome.Cancelled: cancelled++; break;
                    default: notRun++; break;
                }
            }
            return new SummaryInfo(Cli.Version, _workspaceName,
                new OutcomeCounts(_state.Tests.Count, passed, failed, skipped, notRun, running, queued, cancelled),
                _state.Run.Current?.Copy(), _state.Run.Last?.Copy(), _state.Run.Pending?.Info(), _gate.Active, _watch, _coverageEnabled,
                _state.Projects.Select(p => new ProjectSummary(p.Project.Id, p.Project.Name, p.Project.Status, p.Project.HostState, p.Project.LastError,
                    perProject.GetValueOrDefault(p.Project.Id), p.Project.TargetFramework)).ToList(),
                _gate.Cancelling && _gate.Active is not null ? true : null);
        }
    }

    // ------------------------------------------------------------------ per-test details (lean clients)

    /// <summary>One test with everything (capped output, history), for GET /api/tests/details. Null = unknown.</summary>
    public TestNode? GetTest(string? projectId, string id)
    {
        lock (Sync)
        {
            var key = _state.Order.FirstOrDefault(k => k.Id == id && (projectId is null || k.ProjectId == projectId));
            return key.Id is null ? null : _state.Tests[key].Copy();
        }
    }

    /// <summary>History of one test (or all tests): GET /api/tests/history.</summary>
    public List<TestHistoryInfo> GetHistory(string? projectId, string? id)
    {
        lock (Sync)
        {
            return _state.Order.Where(k => (projectId is null || k.ProjectId == projectId) && (id is null || k.Id == id))
                .Select(k => _state.Tests[k])
                .Where(t => id is not null || t.History is { Count: > 0 })
                .Select(t => new TestHistoryInfo(t.ProjectId, t.Id, t.History ?? [], t.Flaky))
                .ToList();
        }
    }

    public sealed record TestHistoryInfo(string ProjectId, string Id, List<HistoryEntry> History, bool? Flaky);

    public sealed record TestOutputInfo(string ProjectId, string Id, string? StdOut, string? StdErr, bool Truncated);

    /// <summary>Full console output of a test (GET /api/tests/output): the retained full text when it was cut, else the live text.</summary>
    public TestOutputInfo? GetOutput(string? projectId, string id)
    {
        TestNode? t = GetTest(projectId, id);
        if (t is null) return null;
        if (t.OutputTruncated == true && _outputs.TryGet(t.ProjectId, t.Id, out var o, out var e))
            return new TestOutputInfo(t.ProjectId, t.Id, o, e, false);
        return new TestOutputInfo(t.ProjectId, t.Id, t.StdOut, t.StdErr, t.OutputTruncated == true);
    }

    private readonly OutputStore _outputs = new();

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
            _outputs.Put(t.ProjectId, t.Id, fullOut, fullErr);
        }
        else
        {
            t.OutputTruncated = null;
            _outputs.Remove(t.ProjectId, t.Id);
        }
    }

    // ------------------------------------------------------------------ settings broadcasts

    /// <summary>A toggle changed: legacy clients get a full <c>state</c>, lean clients a small <c>settingsChanged</c>.</summary>
    private void PublishSettings()
    {
        lock (Sync)
        {
            // Lean clients get no snapshot here: send the pending test updates first (they used to be dropped).
            FlushTestsLocked();
            _hub.Publish(new StateEvent(Snapshot()), () => new SettingsChangedEvent(new SettingsInfo
            {
                Watch = _watch,
                CoverageEnabled = _coverageEnabled,
                TracingEnabled = _tracingEnabled,
                Quarantined = _settings.Quarantined(),
                ImpactMap = _impact.Info(),
            }));
        }
    }

    // ------------------------------------------------------------------ target framework (multi-targeting)

    /// <summary>
    /// Default TFM of a multi-targeted project: the highest .NET (5+ / "netX.Y") version, plain before OS-specific
    /// ("net9.0" before "net9.0-windows"); then netcoreapp, then anything else in declaration order.
    /// </summary>
    public static string? PickTargetFramework(IReadOnlyList<string> frameworks)
    {
        static (int Kind, Version Version, int Plain) Rank(string tfm)
        {
            var t = tfm.ToLowerInvariant();
            var dash = t.IndexOf('-');
            var core = dash < 0 ? t : t[..dash];
            var plain = dash < 0 ? 1 : 0;
            if (core.StartsWith("netcoreapp") && Version.TryParse(core["netcoreapp".Length..], out var cv)) return (2, cv, plain);
            if (core.StartsWith("net") && !core.StartsWith("netstandard") && core.Contains('.') && Version.TryParse(core[3..], out var nv)) return (3, nv, plain);
            return (0, new Version(0, 0), plain);
        }
        return frameworks.Select((f, i) => (f, r: Rank(f), i))
            .OrderByDescending(x => x.r.Kind).ThenByDescending(x => x.r.Version).ThenByDescending(x => x.r.Plain).ThenBy(x => x.i)
            .Select(x => x.f).FirstOrDefault();
    }

    /// <summary>setTargetFramework: run a multi-targeted project on another TFM (null/empty = the default). Rebuilds + rediscovers.</summary>
    public (int Status, string? Error) SetTargetFramework(string? projectId, string? tfm)
    {
        ProjectEntry? p;
        lock (Sync) p = _state.Projects.FirstOrDefault(x => x.Project.Id == projectId);
        if (p is null) return (404, $"Unknown project '{projectId}'.");
        if (!string.IsNullOrEmpty(tfm) && p.Project.AvailableFrameworks is { } fws && !fws.Contains(tfm, StringComparer.OrdinalIgnoreCase))
            return (400, $"'{tfm}' is not one of the project's target frameworks ({string.Join(", ", fws)}).");
        if (!string.IsNullOrEmpty(tfm) && p.Project.AvailableFrameworks is null)
            return (409, "The project has not been evaluated yet; try again after discovery.");
        var exact = string.IsNullOrEmpty(tfm) ? null : p.Project.AvailableFrameworks!.First(f => f.Equals(tfm, StringComparison.OrdinalIgnoreCase));
        _settings.SetTargetFramework(p.Project.Path, exact);
        lock (Sync)
        {
            p.TargetPath = null; // re-evaluate with the new TargetFramework
            p.LastBuildStartUtc = null;
            // The build before that evaluation already builds (only) the new framework.
            if (p.Project.AvailableFrameworks is { Count: > 0 } all) p.Project.TargetFramework = exact ?? PickTargetFramework(all);
            Emit(new ProjectStatusEvent(p.Project.Copy()));
        }
        Log(p.Project.Id, LogLevels.Info, string.IsNullOrEmpty(tfm) ? "Target framework: back to the default" : $"Target framework: {tfm}");
        _ = DiscoverAsync(p.Project.Id);
        return (202, null);
    }

    // ------------------------------------------------------------------ debug

    /// <summary>MTP 2.x: the test app waits in startup until a debugger is attached (prints its process id).</summary>
    public const string WaitAttachDebuggerEnvVar = "TESTINGPLATFORM_WAIT_ATTACH_DEBUGGER";

    /// <summary>How long a debug run's test host may wait for the debugger before the run is given up.</summary>
    public static readonly TimeSpan DebugAttachTimeout = TimeSpan.FromMinutes(10);

    public static List<DebugHint> DebugHints(int pid) =>
    [
        new("vscode", "Visual Studio Code",
            $"Run and Debug → \".NET: Attach\" (C# Dev Kit) / \"Debug: Attach to a .NET 5+ or .NET Core process\" and pick process {pid}, " +
            "or add the launch configuration below and start it.",
            $$"""{ "name": "Attach to Labcoat test host", "type": "coreclr", "request": "attach", "processId": "{{pid}}" }"""),
        new("rider", "JetBrains Rider",
            $"Run → Attach to Process… (Ctrl+Alt+F5; macOS ⌥⇧⌘F5), select the dotnet process with PID {pid} (.NET debugger)."),
        new("visualstudio", "Visual Studio",
            $"Debug → Attach to Process… (Ctrl+Alt+P), \"Attach to: Managed (.NET Core, .NET 5+) code\", select the process with ID {pid}."),
    ];
}
