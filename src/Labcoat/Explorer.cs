using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// Owns the explorer state and orchestrates build / discover / run / watch.
/// All state mutations happen under <see cref="Sync"/>, and every event is published under it too
/// (see <see cref="EventHub.HandleAsync"/>). Operations (build/discover/run) are serialized by a single
/// semaphore, so requests issued while something is in progress are queued.
/// </summary>
public sealed partial class Explorer : IAsyncDisposable
{
    public object Sync { get; } = new();

    private readonly EventHub _hub;
    private readonly List<ProjectEntry> _projects = [];
    private readonly Dictionary<(string ProjectId, string Id), TestNode> _tests = [];
    private readonly List<(string ProjectId, string Id)> _order = [];
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private CancellationTokenSource _cancel = new();
    private readonly CancellationTokenSource _shutdown = new();
    /// <summary>_shutdown.Token, read once: background work started before DisposeAsync may still pass it on after the
    /// source is disposed (the token itself stays valid and cancelled; reading Token from a disposed source throws).</summary>
    private readonly CancellationToken _shutdownToken;
    /// <summary>_cancel linked with _shutdown, one per _cancel (lock Sync): CurrentToken() hands out its token, Cancel()
    /// replaces both and disposes the old pair, so no per-call linked source keeps a registration on _shutdown.</summary>
    private CancellationTokenSource _operation;
    private CancellationToken _operationToken;
    private int _disposed;
    private RunSummary? _currentRun;
    /// <summary>The tests of the run in progress (lock Sync); Cancel() marks the ones still queued as cancelled at once.</summary>
    private List<(string ProjectId, string Id)>? _currentRequested;
    private RunSummary? _lastRun;
    private bool _watch;
    private bool _coverageEnabled;
    private CoverageReport? _coverage;
    /// <summary>The Coverage page's report (FullCoverage.cs): never replaced by a run of selected tests.</summary>
    private readonly FullCoverage _fullCoverage; // persisted in coverage.json next to the history (FullCoverageStore.cs)
    private readonly FullCoverageStore _fullCoverageStore;
    private readonly string? _aspireDashboardUrl;
    private readonly HistoryStore _history; // persisted results + history across restarts (HistoryStore.cs)
    private readonly WorkspaceSettings _settings; // persisted runtime choices: tracing toggle, quarantine (WorkspaceSettings.cs)
    private bool _tracingEnabled;
    private readonly string _workspaceName;
    private readonly List<EditorInfo> _editors;
    // testsUpdated coalescing (TestUpdateCoalescer.cs): flushed by the timer or before any other ordered event.
    private readonly TestUpdateCoalescer _pendingTests = new(TimeSpan.FromMilliseconds(50));
    // full entries vs. result patches per flush (TestUpdateEncoder.cs, contract "Round 7")
    private readonly TestUpdateEncoder _encoder = new();
    // ExplorerState.historyStamps: per project, bumped when discovery restored a previous session's history
    private readonly Dictionary<string, int> _historyStamps = [];
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];
    private readonly Timer _flushTimer;

    private sealed partial class ProjectEntry(TestProject project) // more fields in Explorer.Warm.cs
    {
        public TestProject Project { get; } = project;
        public string? TargetPath;
        public DateTime TargetPathStamp;           // csproj write time when TargetPath was computed
        public List<string> WatchDirs = [];
        public DateTime DiscoveredDllStamp;        // dll write time at last successful discovery
        public DateTime? LastBuildStartUtc;        // start time of the last successful build
        public List<FileSystemWatcher> Watchers = [];
        public Timer? Debounce;
        public MtpHost? ActiveSession;
        public bool? SupportsCoverage;             // cached result of probing `--help` for --coverage
        public DateTime SupportsCoverageStamp;
        public Dictionary<string, string> Properties = new(StringComparer.OrdinalIgnoreCase); // evaluated MSBuild properties (diagnostics)
        public List<string> PackageReferences = [];
    }

    private readonly List<string> _inputRoots;

    /// <param name="inputRoots">Common root of the CLI input paths (for the source endpoint).</param>
    /// <param name="workspaceName">Display name (ExplorerState.workspaceName); null = derived from the project paths.</param>
    /// <param name="tracingOnStart">Initial trace collection state (<c>--traces</c>); a persisted runtime choice wins.</param>
    public Explorer(EventHub hub, IEnumerable<string> projectPaths, string? aspireDashboardUrl, IEnumerable<string>? inputRoots = null,
        string? workspaceName = null, bool tracingOnStart = false, IEnumerable<string>? inputPaths = null)
    {
        projectPaths = projectPaths.ToList();
        _shutdownToken = _shutdown.Token;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, _shutdownToken);
        _operationToken = _operation.Token;
        _inputRoots = inputRoots?.ToList() ?? [];
        _hub = hub;
        _aspireDashboardUrl = aspireDashboardUrl;
        _flushTimer = new Timer(_ => { lock (Sync) FlushTestsLocked(); }, null, Timeout.Infinite, Timeout.Infinite);
        _history = new HistoryStore(projectPaths, (level, msg) => Log(null, level, msg));
        // Quarantine and the coverage report are kept by the project's relative path too: the history knows the current ids' paths.
        _settings = new WorkspaceSettings(_history.DataDirectory, (level, msg) => Log(null, level, msg), _history);
        _fullCoverageStore = new FullCoverageStore(_history.DataDirectory, (level, msg) => Log(null, level, msg)) { Keys = _history };
        _fullCoverage = new FullCoverage(_fullCoverageStore);
        _tracingEnabled = _settings.TracingEnabled ?? tracingOnStart;
        _workspaceName = WorkspaceName.Resolve(workspaceName, inputPaths ?? [], projectPaths);
        _editors = EditorLauncher.Detect();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in projectPaths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var id = name;
            var suffix = 2;
            while (!used.Add(id)) id = $"{name}-{suffix++}";
            _projects.Add(new ProjectEntry(new TestProject { Id = id, Name = name, Path = path }));
        }
    }

    // ------------------------------------------------------------------ state & events

    /// <summary>Recent log lines, replayed to clients that connect later (e.g. build output from startup).</summary>
    private readonly Queue<LogEvent> _logs = new();
    private const int MaxLogs = 2000;

    /// <param name="includeLogs">True for the snapshot a client receives on connect (and GET /api/state);
    /// broadcast snapshots omit the log history so connected clients keep their own (possibly cleared) panel.</param>
    /// <param name="lean">Lean snapshot (<c>?lean=1</c>): tests without history and console output.</param>
    public ExplorerState Snapshot(bool includeLogs = false, bool lean = false)
    {
        lock (Sync)
        {
            // A client that gets this snapshot may be ahead of the encoder's mirror for tests with pending updates.
            if (_pendingTests.Count > 0) _encoder.ForceFull(_pendingTests.Keys);
            List<TestNode> tests;
            List<string>? files = null;
            if (lean) tests = LeanTestsLocked(out files);
            else tests = _order.Select(k => _tests[k].Copy()).ToList();
            return new ExplorerState
            {
                Logs = includeLogs ? _logs.ToList() : null,
                Projects = _projects.Select(p => p.Project.Copy()).ToList(),
                Tests = tests,
                Lean = lean ? true : null,
                Files = files,
                HistoryStamps = lean ? _projects.ToDictionary(p => p.Project.Id, p => $"{_instance}.{_historyStamps.GetValueOrDefault(p.Project.Id)}") : null,
                PendingRun = _pendingRun?.Info(),
                CurrentRun = _currentRun?.Copy(),
                LastRun = _lastRun?.Copy(),
                Watch = _watch,
                AspireDashboardUrl = _aspireDashboardUrl,
                CoverageEnabled = _coverageEnabled,
                Coverage = _coverage,
                FullCoverage = _fullCoverage.Report,
                ImpactMap = _impact.Info(),
                Runs = _history.Runs(),
                TracingEnabled = _tracingEnabled,
                TracingAvailable = Telemetry.Available,
                TracingFilter = TraceFilter,
                WorkspaceName = _workspaceName,
                SourceRoots = SourceRootsLocked(),
                Quarantined = _settings.Quarantined(),
                Editors = _editors,
                TelemetryAvailable = DashboardTelemetry.Available ? true : null,
                UiDefaults = UiDefaults,
                RunningNow = RunningNowLocked(), // Explorer.Hang.cs
                Limits = LimitsInfo(),
            };
        }
    }

    /// <summary>Lean copies of the tests: each distinct source file once (ExplorerState.files), tests carry its index.</summary>
    private List<TestNode> LeanTestsLocked(out List<string> files)
    {
        files = [];
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var tests = new List<TestNode>(_order.Count);
        foreach (var k in _order)
        {
            var t = _tests[k].LeanCopy();
            if (t.FilePath is { } f)
            {
                if (!index.TryGetValue(f, out var i))
                {
                    index[f] = i = files.Count;
                    files.Add(f);
                }
                t.FilePath = null;
                t.FileIndex = i;
            }
            tests.Add(t);
        }
        return tests;
    }

    /// <summary>ExplorerState.uiDefaults (LabcoatConfig.UiDefaults, serialized once; immutable, so snapshots share it).</summary>
    public JsonElement? UiDefaults { get; init; }

    public bool IsRunning { get { lock (Sync) return _currentRun is not null; } }

    /// <summary>Persisted past runs, newest first (GET /api/runs).</summary>
    public List<RunSummary> Runs() => _history.Runs();

    /// <summary>One persisted run with its per-test results (GET /api/runs/{runId}).</summary>
    public RunDetails? GetRun(string runId) => _history.GetRun(runId);

    /// <summary>
    /// Publishes an event to all clients. <c>testsUpdated</c> is coalesced (merged per test, flushed at most every
    /// ~50 ms); every other ordered event flushes the pending test updates first, so e.g. <c>runFinished</c> never
    /// overtakes the run's last results. A <c>state</c> snapshot supersedes pending updates. Logs are not ordered
    /// relative to test updates.
    /// </summary>
    private void Emit(ServerEvent evt)
    {
        lock (Sync)
        {
            switch (evt)
            {
                case TestsUpdatedEvent tu:
                    if (_pendingTests.Add(tu.Tests)) _flushTimer.Change(_pendingTests.Interval, Timeout.InfiniteTimeSpan);
                    return;
                case LogEvent:
                    break;
                case StateEvent se:
                    _pendingTests.Clear();
                    // Every client gets a snapshot: the patches of later flushes start from it.
                    _encoder.Reset(se.State.Lean == true ? _order.Select(k => _tests[k].Copy()) : se.State.Tests);
                    // Lean clients get a lean snapshot (no history / console output).
                    _hub.Publish(evt, () => new StateEvent(Snapshot(lean: true)));
                    return;
                default:
                    FlushTestsLocked();
                    break;
            }
            _hub.Publish(evt);
        }
    }

    /// <summary>
    /// Sends the buffered test updates: full entries (<c>testsUpdated</c>) for new tests and structural changes, compact
    /// patches (<c>testResults</c>) for result-only changes (TestUpdateEncoder.cs).
    /// </summary>
    private void FlushTestsLocked()
    {
        var list = _pendingTests.Drain();
        if (list.Count == 0) return;
        var (full, changed) = _encoder.Split(list);
        if (full.Count > 0) _hub.Publish(new TestsUpdatedEvent(full), () => new TestsUpdatedEvent(full.Select(t => t.LeanCopy()).ToList()));
        if (changed.Count > 0) _hub.Publish(TestUpdateEncoder.Patches(changed, lean: false), () => TestUpdateEncoder.Patches(changed, lean: true));
    }

    private void PublishState() { lock (Sync) Emit(new StateEvent(Snapshot())); }

    // Console lines logged before the startup banner is printed (e.g. "Loaded test history…" from the constructor) are
    // held back and written right after it (ReleaseConsole), so the banner is the first thing on the console.
    private readonly object _consoleGate = new();
    private List<string>? _consoleBacklog = [];

    /// <summary>Writes the log lines held back during startup and prints further lines directly.</summary>
    public void ReleaseConsole()
    {
        lock (_consoleGate)
        {
            if (_consoleBacklog is null) return;
            foreach (var line in _consoleBacklog) Console.WriteLine(line);
            _consoleBacklog = null;
        }
    }

    public void Log(string? projectId, string level, string message)
    {
        var prefix = projectId is null ? "" : $"[{projectId}] ";
        var label = $"{level,-7}";
        label = level switch { LogLevels.Error => Cli.Red(label), LogLevels.Warning => Cli.Yellow(label), _ => Cli.Gray(label) }; // respects NO_COLOR
        var line = $"{label} {prefix}{message}";
        lock (_consoleGate)
        {
            if (_consoleBacklog is not null) _consoleBacklog.Add(line);
            else Console.WriteLine(line);
        }
        var evt = new LogEvent(projectId, level, message, Json.Now());
        lock (Sync)
        {
            _logs.Enqueue(evt);
            while (_logs.Count > MaxLogs) _logs.Dequeue();
            Emit(evt);
        }
    }

    /// <summary>A failed build's diagnostics and its error / warning counts (<see cref="SetStatus"/>).</summary>
    private sealed record BuildFailure(List<BuildDiagnostic> Diagnostics, int ErrorCount, int WarningCount);

    private void SetStatus(ProjectEntry p, ProjectStatus status, string? error = null, string? buildOutput = null, bool keepError = false,
        BuildFailure? build = null)
    {
        lock (Sync)
        {
            p.Project.Status = status;
            if (!keepError)
            {
                p.Project.LastError = error;
                p.Project.BuildOutput = buildOutput;
                p.Project.BuildErrors = build?.Diagnostics is { Count: > 0 } diagnostics ? diagnostics : null;
                p.Project.BuildErrorCount = build?.ErrorCount;
                p.Project.BuildWarningCount = build?.WarningCount;
            }
            Emit(new ProjectStatusEvent(p.Project.Copy()));
        }
    }

    private void PublishTests(IEnumerable<TestNode> nodes)
    {
        lock (Sync)
        {
            var list = nodes.Select(n => n.Copy()).ToList();
            if (list.Count > 0) Emit(new TestsUpdatedEvent(list));
        }
    }

    // ------------------------------------------------------------------ commands

    /// <summary>
    /// Handles a WebSocket command. Returns an event for the sending client only when the command was rejected
    /// (<see cref="CommandRejectedEvent"/>: unknown command / project / tests, missing fields), else null.
    /// </summary>
    public Task<ServerEvent?> HandleCommandAsync(ClientCommand cmd)
    {
        if (cmd.Type == "runAffected") return RunAffectedCommandAsync(cmd); // GitAffected.cs
        ServerEvent? reply = cmd.Type switch
        {
            "discover" => ForKnownProject(cmd, () => _ = DiscoverCommandAsync(cmd.ProjectId)),
            "run" or "debug" => RunCommand(cmd),
            "cancel" => CancelCommand(),
            "setWatch" => WithEnabled(cmd, SetWatch),
            "setCoverage" => WithEnabled(cmd, SetCoverage),
            "restartHost" => ForKnownProject(cmd, () => _ = RestartHostAsync(cmd.ProjectId)),
            "buildImpactMap" => ForKnownProject(cmd, () => _ = BuildImpactMapAsync(cmd.ProjectId)),
            "setTracing" => WithEnabled(cmd, SetTracing),
            "setQuarantine" => SetQuarantineCommand(cmd),
            "setTargetFramework" => SetTargetFrameworkCommand(cmd),
            _ => RejectCommand(cmd, 400, $"Unknown command '{cmd.Type}'"),
        };
        return Task.FromResult(reply);
    }

    private CommandRejectedEvent RejectCommand(ClientCommand cmd, int status, string error, List<string>? unknown = null)
    {
        Log(null, LogLevels.Warning, $"{cmd.Type ?? "command"}: {error}");
        return new CommandRejectedEvent(cmd.Type, status, error, unknown);
    }

    /// <summary>Starts a command for <c>projectId</c> (all projects when null); rejected when the project is unknown.</summary>
    private CommandRejectedEvent? ForKnownProject(ClientCommand cmd, Action start)
    {
        if (cmd.ProjectId is not null && !HasProject(cmd.ProjectId)) return RejectCommand(cmd, 404, $"Unknown project '{cmd.ProjectId}'");
        start();
        return null;
    }

    /// <summary>A command with a required <c>enabled</c> flag.</summary>
    private CommandRejectedEvent? WithEnabled(ClientCommand cmd, Action<bool> apply)
    {
        if (cmd.Enabled is not { } enabled) return RejectCommand(cmd, 400, "'enabled' (boolean) is required");
        apply(enabled);
        return null;
    }

    private CommandRejectedEvent? RunCommand(ClientCommand cmd)
    {
        var debug = cmd.Type == "debug";
        var r = RequestRun(new RunRequestOptions
        {
            ProjectId = cmd.ProjectId, TestIds = cmd.TestIds, Coverage = cmd.Coverage, Trigger = debug ? "debug" : "user", Repeat = cmd.Repeat, Debug = debug,
        });
        return r.Status != 202 ? RejectCommand(cmd, r.Status, r.Error ?? "rejected", r.UnknownTestIds) : null;
    }

    private CommandRejectedEvent? CancelCommand()
    {
        Cancel();
        return null;
    }

    private CommandRejectedEvent? SetQuarantineCommand(ClientCommand cmd)
    {
        if (cmd.ProjectId is null || cmd.Id is null) return RejectCommand(cmd, 400, "setQuarantine needs projectId and id");
        SetQuarantine(cmd.ProjectId, cmd.Id, cmd.Quarantined ?? true);
        return null;
    }

    private CommandRejectedEvent? SetTargetFrameworkCommand(ClientCommand cmd)
    {
        var (status, error) = SetTargetFramework(cmd.ProjectId, cmd.TargetFramework);
        return status != 202 ? RejectCommand(cmd, status, error ?? "rejected") : null;
    }

    public bool HasProject(string projectId)
    {
        lock (Sync) return _projects.Any(p => p.Project.Id == projectId);
    }

    private CancellationToken CurrentToken()
    {
        lock (Sync) return _operationToken;
    }

    private List<ProjectEntry> SelectProjects(string? projectId)
    {
        if (projectId is null) return _projects;
        var p = _projects.FirstOrDefault(p => p.Project.Id == projectId);
        if (p is null) Log(null, LogLevels.Warning, $"Unknown project '{projectId}'");
        return p is null ? [] : [p];
    }

    /// <summary>Build + discover the selected projects (all when null).</summary>
    public async Task DiscoverAsync(string? projectId)
    {
        var ct = CurrentToken();
        if (!await AcquireOpAsync("discovery", ct)) return;
        try
        {
            // one build for all, then parallel discovery (Explorer.Parallel.cs)
            await BuildAndDiscoverAsync(SelectProjects(projectId).ToList(), onlyIfChanged: false, forceDiscovery: true, ct);
        }
        catch (Exception ex) { Log(null, LogLevels.Error, "Discovery failed: " + ex.Message); }
        finally { ReleaseOp(); }
    }

    /// <summary>Max repetitions of a repeat run (<c>run</c> with <c>repeat</c>).</summary>
    public const int MaxRepeat = 50;

    /// <summary>
    /// Executes a run request (the caller holds the op lock). Each project is built first (incremental) and
    /// re-discovered when its output assembly changed; only then does the run start (<c>runStarted</c>) and only the
    /// tests that will actually run are marked queued — tests outside the run (and the tests of a project whose build
    /// failed) keep their previous result. <c>Coverage</c> overrides the global coverage toggle for this run only.
    /// A test host that exits mid-run aborts that project's part of the run: the test(s) in progress fail with
    /// "Test host exited with code N", the rest become notRun, and the summary gets <c>aborted</c>.
    /// </summary>
    private async Task ExecuteRunAsync(RunRequest req, CancellationToken ct)
    {
        bool collectCoverage;
        lock (Sync) collectCoverage = req.Coverage ?? _coverageEnabled;
        if (collectCoverage && req.Iterations > 1)
        {
            Log(null, LogLevels.Info, "Repeat runs do not collect coverage; running without coverage.");
            collectCoverage = false;
        }
        var x = new RunExecution(req, collectCoverage, _settings.Quarantined().ToHashSet(StringComparer.Ordinal));
        try
        {
            var ready = await BuildRunProjectsAsync(req, ct);
            if (ready is null) return;
            StartRun(x, ready);
            SetRunStatus(req.RunId, "running", run: x.Run);
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
        catch (Exception ex)
        {
            Log(null, LogLevels.Error, "Run failed: " + ex.Message);
        }
        finally
        {
            if (x.Run is not null) FinishRun(x, x.Run, ct);
        }
    }

    /// <summary>The state of one <see cref="ExecuteRunAsync"/>: what was requested, the results so far and the coverage collected.</summary>
    private sealed class RunExecution(RunRequest request, bool collectCoverage, HashSet<string> quarantined)
    {
        public RunRequest Request { get; } = request;
        public int Iterations => Request.Iterations;
        public bool CollectCoverage { get; } = collectCoverage;
        public HashSet<string> Quarantined { get; } = quarantined;
        public RunSummary? Run { get; set; }
        public Stopwatch? Stopwatch { get; set; }
        public Dictionary<(string, string), Outcome> Results { get; } = [];
        /// <summary>Outcome before the run, restored when a test never ran.</summary>
        public Dictionary<(string, string), Outcome> Previous { get; } = [];
        public List<(string ProjectId, string Id)> Requested { get; } = [];
        public CoverageAccumulator Accumulator { get; } = new();
        public List<string> CoveredProjects { get; } = [];
        public List<CoverageSettingsInfo> CoverageSettings { get; } = [];
        /// <summary>Per project: its coverage of this run, and whether it ran every test of the project (FullCoverage.cs).</summary>
        public List<(string ProjectId, HashSet<string>? Ids, CoverageAccumulator Data, CoverageSettingsInfo Settings)> ProjectCoverage { get; } = [];
        public Dictionary<(string ProjectId, string Id), RepeatTestResult> RepeatStats { get; } = [];
        /// <summary>Repeat: the most recent failing repetition per test.</summary>
        public Dictionary<(string, string), TestNode> LastFailure { get; } = [];
        public List<string> Aborts { get; } = [];
    }

    /// <summary>One project's part of a run (<see cref="RunProjectAsync"/>).</summary>
    /// <param name="Quarantined">"projectId|id" keys whose results are counted separately (RunSummary.quarantined).</param>
    /// <param name="RequestedKeys">The tests of this project the run asked for (to settle them when the host crashes).</param>
    /// <param name="Debug">Debug run: a one-off host that waits for a debugger (TESTINGPLATFORM_WAIT_ATTACH_DEBUGGER).</param>
    private sealed record ProjectRun(ProjectEntry Project, HashSet<string>? Ids, RunSummary Run, Dictionary<(string, string), Outcome> Results,
        IReadOnlySet<string> Quarantined, List<(string ProjectId, string Id)> RequestedKeys, bool Debug);

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
            Log(null, LogLevels.Warning, "Nothing to run");
            SetRunStatus(req.RunId, "skipped", "Nothing to run.");
            return null;
        }

        // ---- build + discover every project first; the run starts once the binaries are ready
        SetRunStatus(req.RunId, "building");
        // one build for the projects that changed, then parallel discovery (Explorer.Parallel.cs)
        var readyProjects = await BuildAndDiscoverAsync(plan.Select(x => x.Project).ToList(), onlyIfChanged: true, forceDiscovery: false, ct);
        var ready = plan.Where(x => readyProjects.Contains(x.Project)).ToList();
        if (ct.IsCancellationRequested)
        {
            SetRunStatus(req.RunId, "cancelled", "Cancelled while building.");
            return null;
        }
        if (ready.Count == 0)
        {
            Log(null, LogLevels.Warning, "Nothing to run: the build or discovery failed.");
            SetRunStatus(req.RunId, "skipped", "The build or discovery failed.");
            return null;
        }
        return ready;
    }

    private List<(ProjectEntry Project, HashSet<string>? Ids)> RunPlan(RunRequest req)
    {
        var plan = new List<(ProjectEntry Project, HashSet<string>? Ids)>();
        lock (Sync)
        {
            foreach (var (pid, ids) in req.Selection)
            {
                var p = _projects.FirstOrDefault(x => x.Project.Id == pid);
                if (p is null) continue;
                var known = ids?.Where(id => _tests.ContainsKey((pid, id))).ToHashSet();
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
        lock (Sync)
        {
            foreach (var (p, ids) in ready)
                x.Requested.AddRange(_order.Where(k => k.ProjectId == p.Project.Id && (ids is null || ids.Contains(k.Id))));

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
                Debug = req.Debug ? true : null,
            };
            x.Run = run;
            _currentRun = run;
            _currentRequested = x.Requested;
            foreach (var k in x.Requested)
            {
                var t = _tests[k];
                x.Previous[k] = t.Outcome is Outcome.Queued or Outcome.Running ? Outcome.NotRun : t.Outcome;
                t.Outcome = Outcome.Queued;
            }
            Emit(new RunStartedEvent(run.Copy(), x.Requested.Select(k => k.Id).ToList()));
            PublishTests(x.Requested.Select(k => _tests[k]));
        }
    }

    private async Task RunIterationAsync(RunExecution x, List<(ProjectEntry Project, HashSet<string>? Ids)> ready, int iteration, CancellationToken ct)
    {
        var iterationResults = x.Iterations > 1 ? new Dictionary<(string, string), Outcome>() : x.Results;
        if (iteration > 0)
        {
            lock (Sync)
            {
                foreach (var k in x.Requested) if (_tests.TryGetValue(k, out var t)) t.Outcome = Outcome.Queued;
                PublishTests(x.Requested.Where(_tests.ContainsKey).Select(k => _tests[k]));
            }
        }
        if (x.Iterations > 1) Log(null, LogLevels.Info, $"Repeat {iteration + 1}/{x.Iterations}");

        await RunProjectsOfIterationAsync(x, ready, iterationResults, ct); // projects in parallel (Explorer.Parallel.cs)

        if (x.Iterations > 1) RecordRepetition(x, iterationResults, ct);
    }

    private async Task RunProjectOfIterationAsync(RunExecution x, ProjectRun project, CancellationToken ct)
    {
        var p = project.Project;
        string? coverageFile = null;
        CoveragePlan? coveragePlan = null;
        if (x.CollectCoverage)
        {
            if (await SupportsCoverageAsync(p, ct))
            {
                coverageFile = CoverageFilePath(p.Project.Id, project.Run.RunId);
                coveragePlan = CoveragePlanFor(p);
            }
            else Log(p.Project.Id, LogLevels.Warning, "Code coverage requested but the test app has no --coverage option " +
                "(add a reference to Microsoft.Testing.Extensions.CodeCoverage); running without coverage.");
        }

        var abort = await RunProjectAsync(project, coverageFile, coveragePlan, ct);
        if (abort is not null) lock (x.Aborts) x.Aborts.Add(abort); // projects run in parallel (Explorer.Parallel.cs)

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
            Log(p.Project.Id, LogLevels.Warning, "Coverage was enabled but no coverage file was produced.");
            return;
        }
        try
        {
            var projectAcc = new CoverageAccumulator();
            projectAcc.AddCobertura(coverageFile, coveragePlan.Filter);
            lock (x) // projects run in parallel (Explorer.Parallel.cs)
            {
                x.Accumulator.Add(projectAcc);
                x.ProjectCoverage.Add((p.Project.Id, project.Ids, projectAcc, coveragePlan.Info));
                x.CoveredProjects.Add(p.Project.Id);
                x.CoverageSettings.Add(coveragePlan.Info);
            }
            RecordImpact(p, project.Ids, coverageFile); // per-test/subset coverage feeds the watch impact map
            CacheTestCoverage(p, project.Ids, coverageFile, project.Run.RunId, coveragePlan); // per-test coverage cache (CoverageCache.cs)
        }
        catch (Exception ex) { Log(p.Project.Id, LogLevels.Error, "Could not parse coverage file: " + ex.Message); }
        finally { DeleteCoverageFile(coverageFile); }
    }

    private static void DeleteCoverageFile(string coverageFile)
    {
        try { File.Delete(coverageFile); } catch { /* best effort: a temp file left in the temp folder */ }
    }

    /// <summary>Repeat run: one repetition's results go into the per-test statistics and the aggregated results.</summary>
    private void RecordRepetition(RunExecution x, Dictionary<(string, string), Outcome> iterationResults, CancellationToken ct)
    {
        lock (Sync)
        {
            foreach (var (k, o) in iterationResults) RecordRepetitionResultLocked(x, k, o);
            var repeat = x.Run!.Repeat!;
            if (!ct.IsCancellationRequested && x.Aborts.Count == 0) repeat.Completed++;
            repeat.PerTest = x.Requested.Where(x.RepeatStats.ContainsKey).Select(k => x.RepeatStats[k]).ToList();
            UpdateCounts(x.Run, x.Results, x.Quarantined);
        }
    }

    private void RecordRepetitionResultLocked(RunExecution x, (string ProjectId, string Id) k, Outcome o)
    {
        if (!x.RepeatStats.TryGetValue(k, out var r)) x.RepeatStats[k] = r = new RepeatTestResult { ProjectId = k.ProjectId, Id = k.Id };
        if (o == Outcome.Passed) r.Passed++;
        else if (IsFailure(o)) r.Failed++;
        else if (o == Outcome.Skipped) r.Skipped++;
        if (_tests.TryGetValue(k, out var t))
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
        lock (Sync)
        {
            var changed = new List<TestNode>();
            foreach (var (k, agg) in x.Results)
            {
                if (!_tests.TryGetValue(k, out var t)) continue;
                if (x.LastFailure.TryGetValue(k, out var f)) CopyFailure(f, t);
                else if (agg is not (Outcome.Queued or Outcome.Running)) t.Outcome = agg;
                if (x.RepeatStats.TryGetValue(k, out var r) && r.DurationsMs.Count > 0) t.DurationMs = Math.Round(r.DurationsMs.Average(), 2);
                changed.Add(t);
            }
            PublishTests(changed);
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
        lock (Sync)
        {
            var changed = new List<TestNode>();
            foreach (var k in keys)
            {
                if (_tests.TryGetValue(k, out var t) && t.Outcome is Outcome.Queued or Outcome.Running)
                {
                    LiveFinished(t);
                    t.Outcome = outcome ?? x.Previous.GetValueOrDefault(k, Outcome.NotRun);
                    changed.Add(t);
                }
            }
            PublishTests(changed);
        }
    }

    /// <summary>The end of a run that started, whichever way it ended: coverage, final counts, history and the summary log line.</summary>
    private void FinishRun(RunExecution x, RunSummary run, CancellationToken ct)
    {
        if (x.CoveredProjects.Count > 0 && !ct.IsCancellationRequested)
        {
            try { PublishCoverage(x, run.RunId); }
            catch (Exception ex) { Log(null, LogLevels.Error, "Could not publish the coverage report: " + ex.Message); }
        }
        else RefreshFullCoverageStale();
        // Every requested test leaves queued / running, whichever way the run ended (cancelled before a project's
        // turn, an exception, ...): cancelled when the run was cancelled, else its result from before the run.
        FinishTests(x, x.Requested, ct.IsCancellationRequested ? Outcome.Cancelled : null);
        HashSet<(string ProjectId, string Id)>? mixed = x.Iterations > 1
            ? x.RepeatStats.Where(kv => kv.Value.Passed > 0 && kv.Value.Failed > 0).Select(kv => kv.Key).ToHashSet()
            : null;
        RecordFinishedRun(x, run, mixed, ct);
        SetRunStatus(run.RunId, FinalRunStatus(run), run.AbortReason, run);
        // Test failures are an expected outcome, not a host problem: info level (errors are for build / host / process failures).
        var failedNames = FailedTestNames(x);
        if (failedNames.Count > 0)
            Log(null, LogLevels.Info, $"Failed ({failedNames.Count}): {string.Join(", ", failedNames.Take(10))}{MoreSuffix(failedNames.Count, 10)}");
        Log(null, run.Aborted == true ? LogLevels.Warning : LogLevels.Info, RunFinishedLine(run, mixed?.Count ?? 0));
    }

    private void RecordFinishedRun(RunExecution x, RunSummary run, HashSet<(string ProjectId, string Id)>? mixed, CancellationToken ct)
    {
        lock (Sync)
        {
            run.FinishedAt = Json.Now();
            run.DurationMs = Math.Round((x.Stopwatch?.Elapsed ?? TimeSpan.Zero).TotalMilliseconds, 1);
            UpdateCounts(run, x.Results, x.Quarantined);
            run.Total = Math.Max(run.Total, x.Results.Count);
            var notRun = x.Requested.Count(k => !x.Results.ContainsKey(k) || x.Results[k] == Outcome.Cancelled);
            if (ct.IsCancellationRequested && notRun > 0) run.Cancelled = true;
            if (x.Aborts.Count > 0)
            {
                run.Aborted = true;
                run.AbortReason = string.Join(" ", x.Aborts);
            }
            run.NotRun = notRun > 0 ? notRun : null;
            _currentRun = null;
            _currentRequested = null;
            LiveReset();
            _lastRun = run;
            PublishTests(_history.Record(run, x.Results.Keys.Where(_tests.ContainsKey).Select(k => _tests[k]).ToList(), mixed));
            Emit(new RunsUpdatedEvent(_history.Runs()));
            Emit(new RunFinishedEvent(run.Copy()));
            // Clients that kept showing the previous result of tests that never ran (UI "pending" display) resync.
            if (x.Aborts.Count > 0) PublishState();
        }
    }

    private List<string> FailedTestNames(RunExecution x)
    {
        lock (Sync)
            return x.Results.Where(kv => IsFailure(kv.Value) && _tests.ContainsKey(kv.Key))
                .Select(kv => _tests[kv.Key].DisplayName
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

    /// <summary>" (+N more)" when <paramref name="count"/> items were cut to <paramref name="shown"/>, else "".</summary>
    private static string MoreSuffix(int count, int shown) => count > shown ? $" (+{count - shown} more)" : "";

    /// <summary>"s" unless <paramref name="count"/> is 1.</summary>
    private static string Plural(int count) => count == 1 ? "" : "s";

    /// <summary>Assembly names of all test projects; excluded from coverage reports.</summary>
    private HashSet<string> TestAssemblyNames()
    {
        lock (Sync)
            return _projects.Select(p => p.TargetPath is null ? p.Project.Name : Path.GetFileNameWithoutExtension(p.TargetPath))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Run counts; results of quarantined tests ("projectId|id") go to <see cref="RunSummary.Quarantined"/> only.</summary>
    private static void UpdateCounts(RunSummary run, Dictionary<(string, string), Outcome> results, IReadOnlySet<string> quarantined)
    {
        int passed = 0, failed = 0, skipped = 0, q = 0;
        foreach (var ((projectId, id), o) in results)
        {
            if (quarantined.Count > 0 && quarantined.Contains(WorkspaceSettings.QuarantineKey(projectId, id)))
            {
                if (o is not Outcome.Cancelled) q++;
                continue;
            }
            if (o == Outcome.Passed) passed++;
            else if (o is Outcome.Failed or Outcome.TimedOut) failed++;
            else if (o == Outcome.Skipped) skipped++;
        }
        run.Passed = passed;
        run.Failed = failed;
        run.Skipped = skipped;
        run.Quarantined = q > 0 ? q : null;
    }

    // ------------------------------------------------------------------ build

    private async Task<bool> BuildAsync(ProjectEntry p, CancellationToken ct, bool onlyIfChanged = false)
    {
        if (!await CheckPlatformAsync(p, ct)) return false; // not on Microsoft.Testing.Platform: never built or started
        if (onlyIfChanged && p.LastBuildStartUtc is { } last && p.TargetPath is not null && File.Exists(p.TargetPath)
            && !SourcesChangedSince(p, last))
        {
            Log(p.Project.Id, LogLevels.Info, "No source changes since last build; skipping build");
            return true;
        }
        var buildStart = DateTime.UtcNow;
        await StopWarmBeforeBuildAsync(p); // Windows only: loaded assemblies are locked
        SetStatus(p, ProjectStatus.Building);
        Log(p.Project.Id, LogLevels.Info, $"Building {p.Project.Path}");
        var sw = Stopwatch.StartNew();
        // Multi-targeted: build only the framework the tests run on (known after the first evaluation).
        string[] tfmArgs;
        lock (Sync) tfmArgs = p.Project.AvailableFrameworks is { Count: > 1 } && p.Project.TargetFramework is { } tfm ? ["-f", tfm] : [];
        var (code, output) = await RunDotnetAsync(["build", p.Project.Path, "-nologo", "-tl:off", "-v:minimal", .. tfmArgs], Path.GetDirectoryName(p.Project.Path)!, ct);
        if (ct.IsCancellationRequested)
        {
            SetStatus(p, ProjectStatus.Idle);
            return false;
        }
        if (code != 0)
        {
            ReportBuildFailure(p, code, output, sw.Elapsed);
            return false;
        }
        Log(p.Project.Id, LogLevels.Info, $"Build succeeded in {sw.Elapsed.TotalSeconds:0.0}s");
        lock (Sync) p.Project.ResultsOutdated = null; // published with the Idle status below

        try
        {
            await ResolveTargetPathAsync(p, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(p, ProjectStatus.Error, ex.Message);
            Log(p.Project.Id, LogLevels.Error, ex.Message);
            return false;
        }
        if (!AcceptsPlatformAfterBuild(p)) return false;
        p.LastBuildStartUtc = buildStart;
        InvalidateWarmIfStale(p);
        SetStatus(p, ProjectStatus.Idle);
        return true;
    }

    private void ReportBuildFailure(ProjectEntry p, int code, string output, TimeSpan elapsed)
    {
        var tail = TailLines(output, 60);
        var diagnostics = BuildOutputParser.Parse(output, out var errorCount, out var warningCount);
        var firstError = FirstBuildError(diagnostics);
        lock (Sync)
        {
            // Results produced before this build are from older code now.
            var hadResults = _tests.Values.Any(t => t.ProjectId == p.Project.Id && t.Outcome is not (Outcome.NotRun or Outcome.Queued or Outcome.Running));
            if (hadResults) p.Project.ResultsOutdated = true;
        }
        SetStatus(p, ProjectStatus.Error, firstError ?? $"Build failed (exit code {code})", tail,
            build: new BuildFailure(diagnostics, errorCount, warningCount));
        Log(p.Project.Id, LogLevels.Error, $"Build failed after {elapsed.TotalSeconds:0.0}s ({errorCount} error{Plural(errorCount)}, " +
            $"{warningCount} warning{Plural(warningCount)})" + (firstError is null ? "" : ": " + firstError));
    }

    /// <summary>The first build error as one line: <c>file(line,column): error CODE: message</c>.</summary>
    private static string? FirstBuildError(List<BuildDiagnostic> diagnostics)
    {
        var first = diagnostics.FirstOrDefault(d => d.Severity == "error");
        if (first is null) return null;
        var location = first.File is null ? "" : first.File + LinePosition(first) + ": ";
        return location + "error " + (first.Code is null ? "" : first.Code + ": ") + first.Message;
    }

    private static string LinePosition(BuildDiagnostic d)
    {
        if (d.Line is not { } line) return "";
        return d.Column is { } column ? $"({line},{column})" : $"({line})";
    }

    /// <summary>
    /// Cheap up-to-date check: any watched source file (project dir + direct ProjectReference dirs, excluding bin/obj)
    /// modified after the last successful build started? Files outside those dirs (e.g. Directory.Build.props higher up)
    /// are not considered; use "discover" to force a build.
    /// </summary>
    private static bool SourcesChangedSince(ProjectEntry p, DateTime sinceUtc)
    {
        var dirs = p.WatchDirs.Count > 0 ? p.WatchDirs : [Path.GetDirectoryName(p.Project.Path)!];
        return dirs.Distinct().Any(dir => SourcesChangedUnder(dir, sinceUtc));
    }

    private static bool SourcesChangedUnder(string dir, DateTime sinceUtc)
    {
        var stack = new Stack<string>([dir]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            try
            {
                if (Directory.EnumerateFiles(d).Any(f => WatchedExtensions.Contains(Path.GetExtension(f)) && File.GetLastWriteTimeUtc(f) > sinceUtc))
                    return true;
                foreach (var sub in Directory.EnumerateDirectories(d).Where(sub => !IsIgnoredSourceDir(Path.GetFileName(sub))))
                    stack.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: skip it */ }
        }
        return false;
    }

    private static bool IsIgnoredSourceDir(string name) => name is "bin" or "obj" or "node_modules" || name.StartsWith('.');

    // The JSON of `dotnet msbuild -getProperty:… -getItem:…`.
    private const string EvaluatedProperties = "Properties";
    private const string EvaluatedItems = "Items";

    private async Task ResolveTargetPathAsync(ProjectEntry p, CancellationToken ct)
    {
        var stamp = File.GetLastWriteTimeUtc(p.Project.Path);
        if (p.TargetPath is not null && stamp == p.TargetPathStamp && File.Exists(p.TargetPath)) return;

        var dir = Path.GetDirectoryName(p.Project.Path)!;
        string[] args = ["msbuild", p.Project.Path, "-getProperty:TargetPath", "-getProperty:TargetFrameworks", "-getProperty:TargetFramework", "-getItem:ProjectReference",
            "-getProperty:TestingPlatformCommandLineArguments", .. DiagnosticProperties.Select(n => "-getProperty:" + n), "-getItem:PackageReference"];
        var (code, output) = await RunDotnetAsync(args, dir, ct);
        if (code != 0) throw new InvalidOperationException("Could not evaluate project: " + TailLines(output, 20));
        var json = JsonNode.Parse(output)!;
        var targetPath = json[EvaluatedProperties]?["TargetPath"]?.GetValue<string>();
        ApplyEvaluation(p, json);
        var single = json[EvaluatedProperties]?["TargetFramework"]?.GetValue<string>();
        var frameworks = (json[EvaluatedProperties]?["TargetFrameworks"]?.GetValue<string>() ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (frameworks.Count == 0 && !string.IsNullOrEmpty(single)) frameworks.Add(single);
        var chosen = single;
        if (string.IsNullOrEmpty(targetPath)) (targetPath, chosen) = await ResolveMultiTargetAsync(p, dir, frameworks, ct);
        lock (Sync)
        {
            p.Project.TargetFramework = chosen;
            p.Project.AvailableFrameworks = frameworks.Count > 0 ? frameworks : null;
        }

        var watchDirs = WatchDirsOf(json, dir);
        p.TargetPath = targetPath;
        p.TargetPathStamp = stamp;
        var changedWatch = !watchDirs.SequenceEqual(p.WatchDirs);
        p.WatchDirs = watchDirs;
        if (changedWatch && _watch) StartWatching(p);
    }

    /// <summary>The evaluated properties and items of <c>dotnet msbuild -getProperty/-getItem</c> kept for diagnostics and runs.</summary>
    private static void ApplyEvaluation(ProjectEntry p, JsonNode json)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in DiagnosticProperties)
            if (json[EvaluatedProperties]?[n] is JsonValue v && v.TryGetValue<string>(out var sv)) props[n] = sv;
        p.Properties = props;
        p.TestingPlatformArguments = json[EvaluatedProperties]?["TestingPlatformCommandLineArguments"]?.GetValue<string>() is { Length: > 0 } tpa ? tpa : null;
        p.ReferencedAssemblies = ProjectReferenceClosure(json[EvaluatedItems]?["ProjectReference"], assembliesOnly: true).Select(AssemblyNameOf)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        p.PackageReferences = json[EvaluatedItems]?["PackageReference"] is JsonArray pkgs
            ? pkgs.Select(x => x?["Identity"]?.GetValue<string>()).OfType<string>().ToList()
            : [];
    }

    /// <summary>
    /// Multi-targeted project (no single TargetPath): the framework chosen with setTargetFramework, else the highest .NET
    /// version (not just the first), and the TargetPath for it.
    /// </summary>
    private async Task<(string TargetPath, string Framework)> ResolveMultiTargetAsync(ProjectEntry p, string dir, List<string> frameworks, CancellationToken ct)
    {
        var wanted = _settings.TargetFramework(p.Project.Path);
        var chosen = wanted is not null && frameworks.Contains(wanted, StringComparer.OrdinalIgnoreCase) ? wanted : PickTargetFramework(frameworks);
        if (chosen is null) throw new InvalidOperationException("Could not determine TargetPath of the project.");
        var (code, output) = await RunDotnetAsync(["msbuild", p.Project.Path, "-getProperty:TargetPath", $"-p:TargetFramework={chosen}"], dir, ct);
        var targetPath = output.Trim();
        if (code != 0 || targetPath.Length == 0) throw new InvalidOperationException("Could not determine TargetPath of the project.");
        if (frameworks.Count > 1)
            Log(p.Project.Id, LogLevels.Info, $"Multi-targeted project ({string.Join(", ", frameworks)}); running on {chosen}" +
                (wanted is null ? " (highest; pick another with setTargetFramework)" : ""));
        return (targetPath, chosen);
    }

    /// <summary>The project directory, its direct ProjectReference directories and the transitive ones.</summary>
    private static List<string> WatchDirsOf(JsonNode json, string dir)
    {
        var watchDirs = new List<string> { dir };
        if (json[EvaluatedItems]?["ProjectReference"] is JsonArray refs)
            watchDirs.AddRange(refs.Select(r => r?["FullPath"]?.GetValue<string>()).OfType<string>().Select(full => Path.GetDirectoryName(full)!));
        // Transitive references too (product code two hops away still affects the tests).
        foreach (var d in ProjectReferenceClosureDirs(json[EvaluatedItems]?["ProjectReference"]).Where(d => !watchDirs.Contains(d)))
            watchDirs.Add(d);
        return watchDirs;
    }

    // ------------------------------------------------------------------ discovery

    private async Task<bool> EnsureDiscoveredAsync(ProjectEntry p, CancellationToken ct)
    {
        var stamp = File.GetLastWriteTimeUtc(p.TargetPath!);
        if (stamp == p.DiscoveredDllStamp) return true;
        return await DiscoverProjectAsync(p, ct);
    }

    private async Task<bool> DiscoverProjectAsync(ProjectEntry p, CancellationToken ct)
    {
        SetStatus(p, ProjectStatus.Discovering);
        var stamp = File.GetLastWriteTimeUtc(p.TargetPath!);
        var found = new List<TestNode>();
        var runId = Guid.NewGuid();
        try
        {
            var sw = Stopwatch.StartNew();
            await RequestOnHostAsync(p, (method, prm) => CollectDiscovered(p, found, method, prm), (conn, token) => conn.DiscoverAsync(runId, token), ct);
            Log(p.Project.Id, LogLevels.Info, $"Discovered {found.Count} tests in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            SetStatus(p, ProjectStatus.Idle);
            return false;
        }
        catch (Exception ex)
        {
            lock (Sync) p.Project.Diagnostics = DiagnoseProject(p, ex);
            SetStatus(p, ProjectStatus.Error, "Discovery failed: " + ex.Message);
            Log(p.Project.Id, LogLevels.Error, "Discovery failed: " + ex.Message);
            return false;
        }

        ReplaceProjectTests(p, found, stamp);
        return true;
    }

    private static void CollectDiscovered(ProjectEntry p, List<TestNode> found, string method, JsonNode? prm)
    {
        if (method != "testing/testUpdates/tests" || prm?["changes"] is not JsonArray changes) return;
        lock (found)
        {
            foreach (var c in changes)
            {
                if (c?["node"] is JsonObject node && IsTestNode(node))
                    found.Add(MtpMapping.ToTestNode(p.Project.Id, node));
            }
        }
    }

    /// <summary>Replaces the project's tests with the discovered ones, keeping previous results for tests that still exist.</summary>
    private void ReplaceProjectTests(ProjectEntry p, List<TestNode> found, DateTime stamp)
    {
        lock (Sync)
        {
            var newKeys = new HashSet<(string, string)>();
            var newOrder = new List<(string, string)>();
            var restoredHistory = false;
            foreach (var n in found)
            {
                var key = (p.Project.Id, n.Id);
                if (!newKeys.Add(key)) continue;
                if (_tests.TryGetValue(key, out var old)) CarryOverResult(old, n);
                else
                {
                    _history.Restore(n); // previous session's result + history
                    if (n.History is { Count: > 0 }) restoredHistory = true;
                }
                _tests[key] = n;
                newOrder.Add(key);
            }
            foreach (var key in _order.Where(k => k.ProjectId == p.Project.Id && !newKeys.Contains(k)).ToList())
                _tests.Remove(key);

            // Keep project grouping order: insert at the position of the project's first test (or the end).
            var firstIndex = FirstTestIndexLocked(p); // a new project: after the projects before it (Explorer.Projects.cs)
            _order.RemoveAll(k => k.ProjectId == p.Project.Id);
            _order.InsertRange(firstIndex < 0 ? _order.Count : Math.Min(firstIndex, _order.Count), newOrder);

            // lean clients fetch history on demand: tell them this project's changed (ExplorerState.historyStamps)
            if (restoredHistory) _historyStamps[p.Project.Id] = _historyStamps.GetValueOrDefault(p.Project.Id) + 1;
            p.DiscoveredDllStamp = stamp;
            p.Project.Status = ProjectStatus.Idle;
            p.Project.LastError = null;
            p.Project.BuildOutput = null;
            p.Project.BuildErrors = null;
            p.Project.BuildErrorCount = null;
            p.Project.BuildWarningCount = null;
            p.Project.Diagnostics = found.Count == 0 ? DiagnoseProject(p, null) : null;
            Emit(new ProjectStatusEvent(p.Project.Copy()));
            PublishState();
        }
    }

    private static void CarryOverResult(TestNode old, TestNode n)
    {
        n.Outcome = old.Outcome;
        n.DurationMs = old.DurationMs;
        n.ErrorMessage = old.ErrorMessage;
        n.ErrorType = old.ErrorType;
        n.StackTrace = old.StackTrace;
        n.StdOut = old.StdOut;
        n.TraceId = old.TraceId;
        n.StdErr = old.StdErr;
        n.LastRunAt = old.LastRunAt;
        HistoryStore.CarryOver(old, n);
    }

    private static bool IsTestNode(JsonObject node) =>
        node["node-type"]?.GetValue<string>() is null or "action";

    // ------------------------------------------------------------------ run

    /// <param name="coverageFile">When set, the run collects Cobertura coverage into this file.</param>
    /// <returns>The abort reason when the test host exited mid-run, else null.</returns>
    private async Task<string?> RunProjectAsync(ProjectRun pr, string? coverageFile, CoveragePlan? coveragePlan, CancellationToken ct)
    {
        var p = pr.Project;
        SetStatus(p, ProjectStatus.Running);
        var runId = Guid.NewGuid();
        var subset = KnownSubset(pr);
        if (subset is { Count: 0 })
        {
            SetStatus(p, ProjectStatus.Idle);
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
                await RequestOnHostAsync(p, onUpdate, (conn, token) => conn.RunAsync(runId, subset, token), ct);
                await CheckWarmAfterRunAsync(p, pr.RequestedKeys, ct);
            }
            else await RunCoverageHostAsync(p, coveragePlan!, extraArgs, onUpdate, runId, subset, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            Log(p.Project.Id, LogLevels.Info, "Run cancelled" + (ex is MtpRpcException { Code: -32800 } or OperationCanceledException or IOException ? "" : ": " + ex.Message));
        }
        catch (TestHostExitedException ex)
        {
            var reason = SettleCrashedRun(pr, ex);
            SetStatus(p, ProjectStatus.Idle);
            Log(p.Project.Id, LogLevels.Error, reason + (ex.StderrTail.Count > 0 ? "\n" + string.Join('\n', ex.StderrTail.TakeLast(5)) : ""));
            return reason;
        }
        catch (Exception ex)
        {
            SetStatus(p, ProjectStatus.Error, "Run failed: " + ex.Message);
            Log(p.Project.Id, LogLevels.Error, "Run failed: " + ex.Message);
            return null;
        }
        SetStatus(p, ProjectStatus.Idle);
        return null;
    }

    /// <summary>The (id, display name) of the selected tests that are still known; null = every test of the project.</summary>
    private List<(string, string)>? KnownSubset(ProjectRun pr)
    {
        if (pr.Ids is null) return null;
        var projectId = pr.Project.Project.Id;
        lock (Sync)
            return pr.Ids.Where(id => _tests.ContainsKey((projectId, id))).Select(id => (id, _tests[(projectId, id)].DisplayName)).ToList();
    }

    /// <summary>A <c>testing/testUpdates/tests</c> notification of a run: results and in-progress states.</summary>
    private void OnRunUpdate(ProjectRun pr, string method, JsonNode? prm)
    {
        if (method != "testing/testUpdates/tests" || prm?["changes"] is not JsonArray changes) return;
        lock (Sync)
        {
            var changed = new List<TestNode>(); // deduped below: a batch can hold in-progress + final for one test
            foreach (var c in changes)
            {
                if (c?["node"] is not JsonObject node || !IsTestNode(node)) continue;
                if (ApplyRunUpdateLocked(pr, node) is { } t && !changed.Contains(t)) changed.Add(t);
            }
            PublishTests(changed);
        }
    }

    /// <summary>Applies one test node update of a run; returns the test when it changed in a way to publish.</summary>
    private TestNode? ApplyRunUpdateLocked(ProjectRun pr, JsonObject node)
    {
        var projectId = pr.Project.Project.Id;
        var incoming = MtpMapping.ToTestNode(projectId, node);
        var key = (projectId, incoming.Id);
        if (!_tests.TryGetValue(key, out var t))
        {
            // Test that was not known from discovery (e.g. dynamic data) — add it.
            t = incoming;
            _tests[key] = t;
            var insertAt = _order.FindLastIndex(k => k.ProjectId == projectId);
            _order.Insert(insertAt < 0 ? _order.Count : insertAt + 1, key);
        }
        var state = node["execution-state"]?.GetValue<string>();
        var outcome = MtpMapping.MapState(state);
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
            LivePartial(t, incoming); // Explorer.Live.cs
            return started ? t : null;
        }
        if (HangTimedOutLocked(key)) return null; // timed out (Explorer.Hang.cs): a late "cancelled" keeps that
        LiveFinished(t);
        MtpMapping.ApplyResult(t, incoming, outcome.Value, state);
        CapOutput(t);
        pr.Results[key] = outcome.Value;
        UpdateCounts(pr.Run, pr.Results, pr.Quarantined);
        return t;
    }

    /// <summary>Debug run: never the warm host, a fresh process that waits in startup until a debugger attaches.</summary>
    private async Task RunDebugHostAsync(ProjectRun pr, string[] extraArgs, Action<string, JsonNode?> onUpdate, Guid runId,
        List<(string, string)>? subset, CancellationToken ct)
    {
        var p = pr.Project;
        var run = pr.Run;
        await using var host = StartHost(p, extraArgs, onUpdate, new Dictionary<string, string> { [WaitAttachDebuggerEnvVar] = "1" });
        var testIds = subset?.Select(s => s.Item1).ToList() ?? [];
        lock (Sync) Emit(new DebuggerWaitingEvent(p.Project.Id, host.ProcessId, testIds, run.RunId, DebugHints(host.ProcessId)));
        Log(p.Project.Id, LogLevels.Info, $"Debug run: the test host (pid {host.ProcessId}) waits for a debugger to attach " +
            $"(VS Code: .NET attach / Rider: Attach to Process / Visual Studio: Debug > Attach to Process). Cancel to give up.");
        var attached = 0;
        await host.RunOperationAsync(async (conn, token) =>
        {
            if (Interlocked.Exchange(ref attached, 1) == 0)
            {
                lock (Sync) Emit(new DebuggerAttachedEvent(p.Project.Id, host.ProcessId, run.RunId));
                Log(p.Project.Id, LogLevels.Info, "Debugger attached; running the tests");
            }
            await conn.InitializeAsync(token);
            return await conn.RunAsync(runId, subset, token);
        }, ct, connectTimeout: DebugAttachTimeout);
        await host.ShutdownAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Coverage needs the controller launch (out-of-process extension): one-off process, never the warm host (see
    /// docs/troubleshooting.md). What happens to the warm host meanwhile: RunCoverageProcessAsync.
    /// </summary>
    private async Task RunCoverageHostAsync(ProjectEntry p, CoveragePlan coveragePlan, string[] extraArgs, Action<string, JsonNode?> onUpdate, Guid runId,
        List<(string, string)>? subset, CancellationToken ct)
    {
        await RunCoverageProcessAsync(p, coveragePlan, async () =>
        {
            await using var host = StartHost(p, extraArgs, onUpdate);
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
        var warm = CurrentWarm(p);
        if (warm is null || ct.IsCancellationRequested) return;
        if (warm.Watch.Check() is { } rewritten)
        {
            Log(p.Project.Id, LogLevels.Warning, $"{rewritten} was rewritten while the tests ran (a build?): results may come from a mix of old and new code. Run them again.");
            await RenewWarmAsync(p, warm, RewrittenReason(rewritten), ct);
            return;
        }
        int corrupt;
        lock (Sync)
            corrupt = requestedKeys.Count(k => _tests.TryGetValue(k, out var t) && t.Outcome is Outcome.Failed && LooksLikeStaleAssembly(t));
        if (corrupt == 0) return;
        Log(p.Project.Id, LogLevels.Warning, $"{corrupt} test{(corrupt == 1 ? "" : "s")} failed with BadImageFormatException / \"Index not found (0x80131124)\": " +
            "the warm test host had assemblies loaded that changed on disk. Run them again.");
        await RenewWarmAsync(p, warm, "its loaded assemblies no longer match the files on disk", ct);
    }

    private static bool LooksLikeStaleAssembly(TestNode t) =>
        t.ErrorType is "System.BadImageFormatException"
        || (t.ErrorMessage?.Contains("0x80131124", StringComparison.Ordinal) ?? false)
        || (t.ErrorMessage?.Contains("BadImageFormatException", StringComparison.Ordinal) ?? false);

    /// <summary>Error type of tests that were running when their test host exited.</summary>
    public const string TestHostCrashedErrorType = "TestHostCrashed";

    /// <summary>
    /// The test host exited mid-run (Environment.Exit, StackOverflow, FailFast, native crash, killed): the test(s) in
    /// progress fail with "Test host exited with code N" (+ the last stderr lines), the project's other requested tests
    /// that did not report become notRun (their old result is cleared, never left "passed"). When the host died before
    /// it reported any progress and exactly one test was left, that test is the one that was running.
    /// </summary>
    private string SettleCrashedRun(ProjectRun pr, TestHostExitedException ex)
    {
        var code = ex.ExitCode is { } c ? c.ToString() : "?";
        lock (Sync)
        {
            var open = pr.RequestedKeys.Where(k => _tests.TryGetValue(k, out var t) && t.Outcome is Outcome.Queued or Outcome.Running).ToList();
            var running = open.Where(k => _tests[k].Outcome == Outcome.Running).ToList();
            if (running.Count == 0 && open.Count == 1) running = open;
            var stderr = ex.StderrTail.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(10).ToList();
            var changed = new List<TestNode>();
            foreach (var k in open)
            {
                var t = _tests[k];
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
            UpdateCounts(pr.Run, pr.Results, pr.Quarantined);
            PublishTests(changed);
            var names = running.Select(k => _tests[k].DisplayName).ToList();
            return CrashReason(pr.Project.Project.Name, code, names, skipped: open.Count - running.Count);
        }
    }

    private static void MarkCrashed(TestNode t, string code, List<string> stderr)
    {
        t.Outcome = Outcome.Failed;
        t.ErrorType = TestHostCrashedErrorType;
        t.ErrorMessage = $"Test host exited with code {code} while this test was running." +
                         (stderr.Count > 0 ? "\n\nLast stderr lines:\n" + string.Join('\n', stderr) : "");
        t.LastRunAt = Json.Now();
    }

    private static string CrashReason(string projectName, string code, List<string> running, int skipped)
    {
        var what = running.Count > 0
            ? $" while running {string.Join(", ", running.Take(3))}{MoreSuffix(running.Count, 3)}"
            : " before it reported which test was running";
        var rest = skipped > 0 ? $"; {skipped} test{Plural(skipped)} did not run." : ".";
        return $"Test host of {projectName} exited with code {code}" + what + rest;
    }

    private MtpHost StartHost(ProjectEntry p, string[] extraArgs, Action<string, JsonNode?> onUpdate, IReadOnlyDictionary<string, string>? environment = null,
        bool? tracing = null)
    {
        if (p.PlatformBlocks) throw new InvalidOperationException(p.PlatformMessage ?? $"{p.Project.Name} isn't a Microsoft.Testing.Platform test app.");
        bool tracingEnabled;
        lock (Sync) tracingEnabled = tracing ?? _tracingEnabled;
        (extraArgs, environment) = LiveHostOptions(p, extraArgs, environment); // Explorer.Live.cs
        extraArgs = WithProjectArguments(p, extraArgs); // TestingPlatformCommandLineArguments (Explorer.Hang.cs)
        var host = MtpHost.Start(p.TargetPath!, extraArgs, (method, prm) =>
        {
            switch (method)
            {
                case "client/log":
                    var level = prm?["level"]?.GetValue<string>() ?? "";
                    var msg = prm?["message"]?.GetValue<string>() ?? "";
                    // MTP logs the cancellation of a request as an error ("Exception during the cancellation of request id …"):
                    // expected after $/cancelRequest, so not shown as one.
                    if (msg.StartsWith("Exception during the cancellation of request", StringComparison.Ordinal)) break;
                    if (level is "Warning" or "Error" or "Critical")
                        Log(p.Project.Id, level == "Warning" ? LogLevels.Warning : LogLevels.Error, msg);
                    break;
                case "testing/testUpdates/attachments":
                case "telemetry/update":
                    break;
                default:
                    onUpdate(method, prm);
                    break;
            }
        }, (level, msg) => Log(p.Project.Id, level, msg), environment, tracingEnabled, TraceFilter);
        AttachLiveOutput(p, host);
        lock (Sync) p.ActiveSession = host;
        return host;
    }

    // ------------------------------------------------------------------ coverage

    /// <summary>
    /// After a coverage run: the run's own report (state.coverage, <c>coverageUpdated</c>: a subset run gives that
    /// subset's coverage) and the full report of the Coverage page (state.fullCoverage, <c>fullCoverageUpdated</c>):
    /// a project that ran every test replaces its part of it, a project that ran selected tests is merged in where
    /// the code is unchanged (FullCoverage.cs).
    /// </summary>
    private void PublishCoverage(RunExecution x, string runId)
    {
        // Subset as soon as any covered project ran selected tests (testIds then lists every test of the run).
        var subset = x.ProjectCoverage.Any(c => c.Ids is not null);
        var subsetIds = subset ? x.Requested.Select(k => k.Id).Distinct().ToList() : null;
        var report = x.Accumulator.Build(runId, x.CoveredProjects, subsetIds) with { Settings = x.CoverageSettings };

        CoverageReport? full = null;
        var notes = new List<string>();
        foreach (var (projectId, ids, data, settings) in x.ProjectCoverage)
        {
            if (ids is null)
            {
                full = _fullCoverage.SetFull(projectId, data, runId, settings);
                continue;
            }
            var (merge, merged) = _fullCoverage.MergeSubset(projectId, data, runId, ids.Count);
            full = merged ?? full;
            notes.AddRange(MergeNotes(projectId, merge));
        }
        full = _fullCoverage.RefreshStale() ?? full; // the other projects' files may have changed meanwhile

        lock (Sync)
        {
            _coverage = report;
            if (full is not null) Emit(new FullCoverageUpdatedEvent(full));
            Emit(new CoverageUpdatedEvent(report));
        }
        Log(null, LogLevels.Info, CoverageLine(report, subsetIds, notes));
    }

    private static IEnumerable<string> MergeNotes(string projectId, FullCoverage.MergeResult merge)
    {
        if (!merge.HasFull)
        {
            yield return $"{projectId}: no full coverage run yet (Run all with coverage)";
            yield break;
        }
        if (merge.FilesChanged > 0) yield return $"{projectId}: merged into {merge.FilesChanged} unchanged file{Plural(merge.FilesChanged)}";
        if (merge.ChangedFiles.Count > 0)
            yield return $"{projectId}: {merge.ChangedFiles.Count} file{Plural(merge.ChangedFiles.Count)} changed since the full run " +
                         $"({string.Join(", ", merge.ChangedFiles.Select(Path.GetFileName).Distinct().Take(3))}), not merged: run all with coverage to refresh";
    }

    /// <summary>The log line of a run's coverage; <paramref name="subsetIds"/> set when the run covered selected tests.</summary>
    private string CoverageLine(CoverageReport report, List<string>? subsetIds, List<string> notes)
    {
        var what = report.NoCoverableLines == true
            ? $"Coverage: no coverable lines in {report.Files.Count} files (with Labcoat's defaults, test assemblies are left out; put the code under test in a separate project, or check the coverage settings)"
            : $"Coverage: {report.LineRate:P1} lines ({report.LinesCovered}/{report.LinesValid}), {report.BranchRate:P1} branches in {report.Files.Count} files";
        if (subsetIds is null) return what;
        what += $" of this run's {subsetIds.Count} test{Plural(subsetIds.Count)}";
        if (_fullCoverage.Report is { } current)
            return what + "; the Coverage page keeps the full report: " +
                   $"{current.LineRate:P1} lines ({current.LinesCovered}/{current.LinesValid})" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        return what + "; no full report yet for the Coverage page (Run all with coverage)";
    }

    /// <summary>After a run: marks the full report's files that changed since their full run (FullCoverage.cs).</summary>
    private void RefreshFullCoverageStale()
    {
        try
        {
            if (_fullCoverage.RefreshStale() is not { } full) return;
            lock (Sync) Emit(new FullCoverageUpdatedEvent(full));
        }
        catch (Exception ex)
        {
            Log(null, LogLevels.Warning, "Could not check the coverage report's files for changes: " + ex.Message);
        }
    }

    public void SetCoverage(bool enabled)
    {
        lock (Sync)
        {
            if (_coverageEnabled == enabled) return;
            _coverageEnabled = enabled;
            PublishSettings();
        }
        Log(null, LogLevels.Info, enabled ? "Code coverage enabled (applies to the next run)" : "Code coverage disabled");
    }

    /// <summary>Does the test app support <c>--coverage</c> (Microsoft.Testing.Extensions.CodeCoverage)? Cached per build.</summary>
    private async Task<bool> SupportsCoverageAsync(ProjectEntry p, CancellationToken ct)
    {
        var stamp = File.GetLastWriteTimeUtc(p.TargetPath!);
        if (p.SupportsCoverage is { } cached && p.SupportsCoverageStamp == stamp) return cached;
        var (_, output) = await RunDotnetAsync([p.TargetPath!, "--help"], Path.GetDirectoryName(p.TargetPath!)!, ct);
        var supported = output.Contains("--coverage-output-format", StringComparison.Ordinal);
        p.SupportsCoverage = supported;
        p.SupportsCoverageStamp = stamp;
        return supported;
    }

    private static string CoverageFilePath(string projectId, string runId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "labcoat", "coverage");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{projectId}-{runId}.cobertura.xml");
    }

    /// <summary>
    /// Path check for <c>GET /api/source</c>. Allowed: (a) any test location file, (b) any file in the current coverage
    /// report, (c) any file under a source root — the discovered project directories (+ their direct ProjectReference
    /// directories) and the common root of the CLI input paths (this covers files referenced from stack traces).
    /// The path is canonicalized (GetFullPath + symlinks resolved) before the check.
    /// </summary>
    public SourceAccess CheckSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(SourceAccessResult.Forbidden, null);
        string full;
        try { full = PathUtil.Canonicalize(path); } catch { return new(SourceAccessResult.Forbidden, null); }
        var cmp = PathUtil.Comparison;

        bool known;
        List<string> roots;
        lock (Sync)
        {
            known = (_coverage?.Files.Any(f => string.Equals(PathUtil.CanonicalizeOrSelf(f.Path), full, cmp)) ?? false)
                    || (_fullCoverage.Report?.Files.Any(f => string.Equals(PathUtil.CanonicalizeOrSelf(f.Path), full, cmp)) ?? false)
                    || _tests.Values.Any(t => t.FilePath is not null && string.Equals(PathUtil.CanonicalizeOrSelf(t.FilePath), full, cmp));
            roots = SourceRootsLocked();
        }
        if (!known && !roots.Any(r => PathUtil.IsUnder(full, r)))
            return new(SourceAccessResult.Forbidden, null);
        if (!File.Exists(full)) return new(SourceAccessResult.NotFound, null);
        return new(SourceAccessResult.Ok, full);
    }

    /// <summary>
    /// The directories /api/source serves (ExplorerState.sourceRoots): the CLI input root(s) and --root directories,
    /// every test project directory and its ProjectReference closure. Canonical; nested roots are folded into their parent.
    /// </summary>
    private List<string> SourceRootsLocked()
    {
        var all = new List<string>(_inputRoots);
        foreach (var p in _projects)
        {
            all.Add(Path.GetDirectoryName(p.Project.Path)!);
            all.AddRange(p.WatchDirs);
        }
        var canonical = all.Select(r => Path.TrimEndingDirectorySeparator(PathUtil.CanonicalizeOrSelf(r)))
            .Distinct(PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Length).ToList();
        var result = new List<string>();
        // Shortest first: a root under one that is already in the result is folded into it.
        foreach (var r in canonical.Where(r => !result.Any(parent => PathUtil.IsUnder(r, parent)))) result.Add(r);
        return result;
    }

    // ------------------------------------------------------------------ tracing & quarantine

    /// <summary>
    /// setTracing: pass the Aspire dashboard's OTLP endpoint to test hosts (LABCOAT_OTLP_*) or not. Persisted per
    /// workspace. Warm hosts keep the environment they started with, so they are replaced on their next request.
    /// </summary>
    public void SetTracing(bool enabled)
    {
        _settings.SetTracingEnabled(enabled);
        lock (Sync)
        {
            if (_tracingEnabled == enabled) return;
            _tracingEnabled = enabled;
            PublishSettings();
        }
        Log(null, LogLevels.Info, TracingChangedLine(enabled));
    }

    private static string TracingChangedLine(bool enabled)
    {
        if (!enabled) return "Trace collection disabled (warm hosts restart on their next run)";
        return Telemetry.Available
            ? "Trace collection enabled: test hosts get the Aspire dashboard's OTLP endpoint (warm hosts restart on their next run)"
            : "Trace collection enabled, but no OTLP endpoint is known (Labcoat is not hosted by an Aspire AppHost), so it has no effect";
    }

    public bool TracingEnabled { get { lock (Sync) return _tracingEnabled; } }

    /// <summary>
    /// The trace filter (<c>--trace-filter</c>, <c>LABCOAT_TRACE_FILTER</c>, <c>traceFilter</c>): while tracing is on,
    /// only the tests matching it get a trace (passed to test hosts as <c>LABCOAT_OTLP_FILTER</c>). Null: every test.
    /// </summary>
    public string? TraceFilter { get; init; }

    /// <summary>setQuarantine: quarantined tests still run but are counted separately (RunSummary.quarantined). Persisted.</summary>
    public void SetQuarantine(string projectId, string id, bool quarantined)
    {
        if (!_settings.SetQuarantined(projectId, id, quarantined)) return;
        PublishSettings();
        Log(projectId, LogLevels.Info, (quarantined ? "Quarantined " : "Released from quarantine: ") + id);
    }

    // ------------------------------------------------------------------ watch

    public void SetWatch(bool enabled)
    {
        lock (Sync)
        {
            if (_watch == enabled) return;
            _watch = enabled;
            foreach (var p in _projects)
            {
                if (enabled) StartWatching(p);
                else StopWatching(p);
            }
            PublishSettings();
        }
        Log(null, LogLevels.Info, enabled ? "Watch mode enabled" : "Watch mode disabled");
    }

    private void StartWatching(ProjectEntry p)
    {
        StopWatching(p);
        var dirs = p.WatchDirs.Count > 0 ? p.WatchDirs : [Path.GetDirectoryName(p.Project.Path)!];
        foreach (var dir in dirs.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            FileSystemEventHandler h = (_, e) => OnFileChanged(p, e.FullPath);
            w.Changed += h;
            w.Created += h;
            w.Deleted += h;
            w.Renamed += (_, e) => OnFileChanged(p, e.FullPath);
            w.EnableRaisingEvents = true;
            p.Watchers.Add(w);
        }
    }

    private static void StopWatching(ProjectEntry p)
    {
        foreach (var w in p.Watchers) w.Dispose();
        p.Watchers.Clear();
        p.Debounce?.Dispose();
        p.Debounce = null;
    }

    private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".fs", ".fsproj", ".vb", ".vbproj", ".props", ".targets", ".json", ".razor" };

    private void OnFileChanged(ProjectEntry p, string path)
    {
        if (!WatchedExtensions.Contains(Path.GetExtension(path))) return;
        var sep = Path.DirectorySeparatorChar;
        if (path.Contains($"{sep}bin{sep}") || path.Contains($"{sep}obj{sep}")) return;
        lock (Sync)
        {
            if (!_watch) return;
            p.PendingChanges.Add(path);
            p.Debounce?.Dispose();
            p.Debounce = new Timer(state => _ = RunWatchAsync(p), null, 500, Timeout.Infinite); // smart watch: Explorer.Warm.cs
        }
    }

    // ------------------------------------------------------------------ helpers

    public static string DotnetPath { get; } = ResolveDotnet();

    private static string ResolveDotnet()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath)) return hostPath;
        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    private static async Task<(int ExitCode, string Output)> DotnetAsync(string[] args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(DotnetPath)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDTERMINALLOGGER"] = "off";

        var sb = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort: the process may have exited already */ }
            return (-1, "cancelled");
        }
        await proc.WaitForExitAsync(CancellationToken.None); // flush async output (the redirected streams' last lines)
        lock (sb) return (proc.ExitCode, sb.ToString());
    }

    private static string TailLines(string s, int lines)
    {
        var all = s.TrimEnd().Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines))).TrimEnd();
    }

    public async ValueTask DisposeAsync()
    {
        // Disposed twice on shutdown (LabcoatApp stops it before the host disposes its services): the second call has
        // nothing left to do, and _shutdown is disposed by then.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _shutdown.CancelAsync();
        StopProjectWatcher(); // Explorer.Projects.cs
        lock (Sync)
        {
            _hangTimer?.Dispose(); // Explorer.Hang.cs
            foreach (var p in _projects)
            {
                StopWatching(p);
                if (p.ActiveSession != p.Warm?.Host) p.ActiveSession?.Kill(); // warm hosts get a graceful exit below
            }
        }
        await ShutdownWarmHostsAsync();
        lock (Sync) FlushTestsLocked();
        await _flushTimer.DisposeAsync();
        _history.Dispose(); // flush a pending debounced save
        _fullCoverageStore.Dispose(); // same for the full coverage report
        // Background work still holding _shutdownToken or an operation token only reads it: cancelled tokens stay valid.
        // _cancel and _operation stay undisposed: Cancel() may still swap them after this (a late request), and the
        // linked _operation's registration on _shutdown goes with it.
        _shutdown.Dispose();
    }
}

/// <summary>Maps MTP server-mode test node JSON onto the contract's TestNode.</summary>
public static partial class MtpMapping
{
    public static TestNode ToTestNode(string projectId, JsonObject node)
    {
        string? Str(string key) => node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        int? Int(string key) => node[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

        var (ns, cls) = SplitTypeName(Str("location.type"));
        var method = MethodName(Str("location.method"));
        var traits = Traits(node["traits"]);

        double? duration = node["time.duration-ms"] is JsonValue d && d.TryGetValue<double>(out var ms) ? Math.Round(ms, 2) : null;

        return new TestNode
        {
            Id = Str("uid") ?? "",
            ProjectId = projectId,
            DisplayName = Str("display-name") ?? Str("uid") ?? "",
            Namespace = ns,
            ClassName = cls,
            MethodName = method,
            FilePath = Str("location.file"),
            Line = Int("location.line-start"),
            LineEnd = Int("location.line-end"),
            Traits = traits,
            DurationMs = duration,
            ErrorMessage = Str("error.message"),
            StackTrace = Str("error.stacktrace"),
            StdOut = Telemetry.ExtractTraceId(NullIfEmpty(Str("standardOutput")), out var traceId),
            TraceId = traceId,
            StdErr = NullIfEmpty(Str("standardError")),
        };
    }

    /// <summary>Namespace and class of a type name (nested classes are reported as Outer+Inner).</summary>
    private static (string? Namespace, string? Class) SplitTypeName(string? type)
    {
        var lastDot = type?.LastIndexOf('.') ?? -1;
        return lastDot > 0 ? (type![..lastDot], type[(lastDot + 1)..]) : (null, type);
    }

    /// <summary>Parameterized methods are reported with their signature: "Multiply(System.Int32,System.Int32)".</summary>
    private static string? MethodName(string? method)
    {
        var paren = method?.IndexOf('(') ?? -1;
        return paren > 0 ? method![..paren] : method;
    }

    /// <summary>Observed shape: [ { "&lt;key&gt;": "&lt;value&gt;" }, ... ]. MSTest [TestCategory("X")] arrives as { "X": "" }.</summary>
    private static List<Trait> Traits(JsonNode? traits)
    {
        var result = new List<Trait>();
        if (traits is not JsonArray arr) return result;
        foreach (var o in arr.OfType<JsonObject>())
        {
            foreach (var (k, v) in o)
            {
                var value = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "";
                result.Add(string.IsNullOrEmpty(value) ? new Trait("Category", k) : new Trait(k, value));
            }
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s.Replace("\r\n", "\n");

    /// <summary>MTP execution-state -> Outcome. null = ignore (e.g. "discovered").</summary>
    public static Outcome? MapState(string? state) => state switch
    {
        "in-progress" => Outcome.Running,
        "passed" => Outcome.Passed,
        "failed" or "error" => Outcome.Failed,
        "skipped" => Outcome.Skipped,
        "timed-out" => Outcome.TimedOut,
        "cancelled" => Outcome.Cancelled,
        _ => null,
    };

    public static void ApplyResult(TestNode t, TestNode incoming, Outcome outcome, string? rawState)
    {
        t.Outcome = outcome;
        t.DisplayName = incoming.DisplayName;
        t.DurationMs = incoming.DurationMs;
        t.ErrorMessage = incoming.ErrorMessage;
        t.StackTrace = incoming.StackTrace;
        t.StdOut = incoming.StdOut;
        t.TraceId = incoming.TraceId;
        t.StdErr = incoming.StdErr;
        t.ErrorType = ErrorTypeOf(incoming, outcome, rawState);
        t.LastRunAt = Json.Now();
        t.Stale = null;
        t.SnapshotsResolved = false;
        if (incoming.FilePath is not null) t.FilePath = incoming.FilePath;
        if (incoming.Line is not null) t.Line = incoming.Line;
        if (incoming.LineEnd is not null) t.LineEnd = incoming.LineEnd;
    }

    /// <summary>The error type of a failed / timed-out result (null otherwise).</summary>
    private static string? ErrorTypeOf(TestNode incoming, Outcome outcome, string? rawState)
    {
        if (outcome is not (Outcome.Failed or Outcome.TimedOut)) return null;
        return rawState == "timed-out" ? "Timeout" : GuessExceptionType(incoming.ErrorMessage, incoming.StackTrace);
    }

    [GeneratedRegex(@"threw exception:\s*([\w.`+]+(?:Exception|Error))", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ThrewRe();

    [GeneratedRegex(@"^([A-Za-z_][\w.`+]*\.\w*(?:Exception|Error)):", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex LeadingTypeRe();

    /// <summary>
    /// MTP server mode does not carry the exception type of a failure, so this is a best-effort guess:
    /// "...threw exception: System.X: msg" / "System.X: msg" in the message, or well-known assertion frames
    /// at the top of the stack trace.
    /// </summary>
    private static string? GuessExceptionType(string? message, string? stack)
    {
        if (message is not null)
        {
            var m = ThrewRe().MatchOrEmpty(message);
            if (m.Success) return m.Groups[1].Value;
            m = LeadingTypeRe().MatchOrEmpty(message);
            if (m.Success) return m.Groups[1].Value;
        }
        var first = stack?.TrimStart().Split('\n')[0] ?? "";
        if (first.Contains("Microsoft.VisualStudio.TestTools.UnitTesting.Assert", StringComparison.Ordinal))
            return "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException";
        if (first.Contains("Xunit.Assert", StringComparison.Ordinal)) return "Xunit.Sdk.XunitException";
        if (first.Contains("NUnit.Framework.Assert", StringComparison.Ordinal)) return "NUnit.Framework.AssertionException";
        return null;
    }
}
