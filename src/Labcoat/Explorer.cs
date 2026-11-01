using System.Text.Json;
using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>
/// Owns the explorer state and orchestrates build / discover / run / watch.
/// All state mutations happen under <see cref="Sync"/>, and every event is published under it too
/// (see <see cref="EventHub.HandleAsync"/>). Operations (build/discover/run) are serialized by a single
/// semaphore, so requests issued while something is in progress are queued.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling",
    Justification = "The orchestrator behind every endpoint; features move out into their own services. "
        + "Over the limit since before CA1506 was on: don't add to it, and remove this once a split brings it "
        + "under (src/CodeMetricsConfig.txt).")]
public sealed partial class Explorer : IAsyncDisposable, IExplorerEffects
{
    private readonly WorkspaceState _state = new();

    public object Sync => _state.Sync;

    private readonly EventHub _hub;
    private readonly OperationGate _gate; // the operation lock and the cancellation tokens (OperationGate.cs)
    private int _disposed;
    private bool _coverageEnabled;
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
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];
    private readonly Timer _flushTimer;

    private readonly List<string> _inputRoots;

    /// <param name="inputRoots">Common root of the CLI input paths (for the source endpoint).</param>
    /// <param name="workspaceName">Display name (ExplorerState.workspaceName); null = derived from the project paths.</param>
    /// <param name="tracingOnStart">Initial trace collection state (<c>--traces</c>); a persisted runtime choice wins.</param>
    public Explorer(EventHub hub, IEnumerable<string> projectPaths, string? aspireDashboardUrl, IEnumerable<string>? inputRoots = null,
        string? workspaceName = null, bool tracingOnStart = false, IEnumerable<string>? inputPaths = null)
    {
        projectPaths = projectPaths.ToList();
        _gate = new OperationGate(_state.Sync);
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
            _state.Projects.Add(new ProjectEntry(new TestProject { Id = id, Name = name, Path = path }));
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
            else tests = _state.Order.Select(k => _state.Tests[k].Copy()).ToList();
            return new ExplorerState
            {
                Logs = includeLogs ? _logs.ToList() : null,
                Projects = _state.Projects.Select(p => p.Project.Copy()).ToList(),
                Tests = tests,
                Lean = lean ? true : null,
                Files = files,
                HistoryStamps = lean ? _state.Projects.ToDictionary(p => p.Project.Id, p => $"{_instance}.{Discovery.HistoryStampLocked(p.Project.Id)}") : null,
                PendingRun = _state.Run.Pending?.Info(),
                CurrentRun = _state.Run.Current?.Copy(),
                LastRun = _state.Run.Last?.Copy(),
                Watch = WatchMode.Enabled,
                AspireDashboardUrl = _aspireDashboardUrl,
                CoverageEnabled = _coverageEnabled,
                Coverage = Coverage.Last,
                FullCoverage = _fullCoverage.Report,
                ImpactMap = _impact.Info(),
                Runs = _history.Runs(),
                TracingEnabled = _tracingEnabled,
                TracingAvailable = Telemetry.Available,
                TracingFilter = TraceFilter,
                WorkspaceName = _workspaceName,
                SourceRoots = SourceRootsLocked(),
                Quarantined = QuarantinedKeysLocked(),
                Editors = _editors,
                TelemetryAvailable = DashboardTelemetry.Available ? true : null,
                UiDefaults = UiDefaults,
                RunningNow = Hang.RunningNowLocked(),
                Limits = LimitsInfo(),
            };
        }
    }

    /// <summary>Lean copies of the tests: each distinct source file once (ExplorerState.files), tests carry its index.</summary>
    private List<TestNode> LeanTestsLocked(out List<string> files)
    {
        files = [];
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var tests = new List<TestNode>(_state.Order.Count);
        foreach (var k in _state.Order)
        {
            var t = _state.Tests[k].LeanCopy();
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

    public bool IsRunning { get { lock (Sync) return _state.Run.IsRunning; } }

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
                    _encoder.Reset(se.State.Lean == true ? _state.Order.Select(k => _state.Tests[k].Copy()) : se.State.Tests);
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
    private readonly Lock _consoleGate = new();
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

    void IExplorerEffects.SetStatus(ProjectEntry p, ProjectStatus status, string? error, string? buildOutput, bool keepError, BuildFailure? build) =>
        SetStatus(p, status, error, buildOutput, keepError, build);

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
        if (cmd.Type == "runAffected") return Affected.CommandAsync(cmd); // AffectedService.cs
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
            ProjectId = cmd.ProjectId, TestIds = cmd.TestIds, Coverage = cmd.Coverage, Trigger = debug ? "debug" : "user", Repeat = cmd.Repeat, Order = cmd.Order, Seed = cmd.Seed, Debug = debug,
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
        return SetQuarantine(cmd.ProjectId, cmd.Id, cmd.Quarantined ?? true) is { } error ? RejectCommand(cmd, 409, error) : null;
    }

    private CommandRejectedEvent? SetTargetFrameworkCommand(ClientCommand cmd)
    {
        var (status, error) = SetTargetFramework(cmd.ProjectId, cmd.TargetFramework);
        return status != 202 ? RejectCommand(cmd, status, error ?? "rejected") : null;
    }

    public bool HasProject(string projectId)
    {
        lock (Sync) return _state.Projects.Any(p => p.Project.Id == projectId);
    }

    private List<ProjectEntry> SelectProjects(string? projectId)
    {
        if (projectId is null) return _state.Projects;
        var p = _state.Projects.FirstOrDefault(p => p.Project.Id == projectId);
        if (p is null) Log(null, LogLevels.Warning, $"Unknown project '{projectId}'");
        return p is null ? [] : [p];
    }

    /// <summary>Build + discover the selected projects (all when null).</summary>
    public async Task DiscoverAsync(string? projectId)
    {
        var ct = _gate.CurrentToken();
        if (!await _gate.AcquireAsync("discovery", ct)) return;
        try
        {
            // one build for all, then parallel discovery (Explorer.Parallel.cs)
            await BuildAndDiscoverAsync(SelectProjects(projectId).ToList(), onlyIfChanged: false, forceDiscovery: true, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Log(null, LogLevels.Error, "Discovery failed: " + ex.Message); }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ runs (RunExecutor.cs)

    /// <summary>Max repetitions of a repeat run (<c>run</c> with <c>repeat</c>).</summary>
    public const int MaxRepeat = 50;

    private RunExecutor? _runner;

    private RunExecutor Runner => LazyInitializer.EnsureInitialized(ref _runner, () => new RunExecutor(_state, this, this, Hosts, Coverage, LiveService,
        Hang, _history, QuarantinedKeysLocked, _outputs, _runStatuses, MaxParallelProjects,
        (projects, ct) => BuildAndDiscoverAsync(projects, onlyIfChanged: true, forceDiscovery: false, ct), HostArgumentsOf,
        (run, coverageFile, plan) =>
        {
            RecordImpact(run.Project, run.Ids, coverageFile); // per-test/subset coverage feeds the watch impact map
            CacheTestCoverage(run.Project, run.Ids, coverageFile, run.Run.RunId, plan); // per-test coverage cache (CoverageCache.cs)
        }, PublishState));

    /// <summary>Executes a run request (RunExecutor.cs); the caller holds the op lock.</summary>
    private Task ExecuteRunAsync(RunRequest req, CancellationToken ct)
    {
        bool coverageEnabled;
        lock (Sync) coverageEnabled = _coverageEnabled;
        return Runner.ExecuteAsync(req, coverageEnabled, ct);
    }

    /// <summary>Assembly names of all test projects; excluded from coverage reports.</summary>
    private HashSet<string> TestAssemblyNames()
    {
        lock (Sync)
            return _state.Projects.Select(p => p.TargetPath is null ? p.Project.Name : Path.GetFileNameWithoutExtension(p.TargetPath))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ test hosts

    /// <summary>Error type of tests that were running when their test host exited.</summary>
    public const string TestHostCrashedErrorType = "TestHostCrashed";

    private MtpHost StartHost(ProjectEntry p, string[] extraArgs, Action<string, JsonNode?> onUpdate, IReadOnlyDictionary<string, string>? environment = null,
        bool? tracing = null)
    {
        if (p.PlatformBlocks) throw new InvalidOperationException(p.PlatformMessage ?? $"{p.Project.Name} isn't a Microsoft.Testing.Platform test app.");
        bool tracingEnabled;
        lock (Sync) tracingEnabled = tracing ?? _tracingEnabled;
        (extraArgs, environment) = LiveService.HostOptions(p, extraArgs, environment);
        extraArgs = WithProjectArguments(p, extraArgs); // TestingPlatformCommandLineArguments
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
        LiveService.Attach(p, host);
        lock (Sync) p.ActiveSession = host;
        return host;
    }

    // ------------------------------------------------------------------ coverage

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
        try { full = PathUtil.Canonicalize(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new(SourceAccessResult.Forbidden, null); }
        var cmp = PathUtil.Comparison;

        bool known;
        List<string> roots;
        lock (Sync)
        {
            known = (Coverage.Last?.Files.Any(f => string.Equals(PathUtil.CanonicalizeOrSelf(f.Path), full, cmp)) ?? false)
                    || (_fullCoverage.Report?.Files.Any(f => string.Equals(PathUtil.CanonicalizeOrSelf(f.Path), full, cmp)) ?? false)
                    || _state.Tests.Values.Any(t => t.FilePath is not null && string.Equals(PathUtil.CanonicalizeOrSelf(t.FilePath), full, cmp));
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
        foreach (var p in _state.Projects)
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
    /// <returns>An error message when the test is listed in the quarantine file (read-only: edit the file), else null.</returns>
    public string? SetQuarantine(string projectId, string id, bool quarantined)
    {
        if (InQuarantineFile(projectId, id))
            return quarantined ? null : $"Quarantined by {QuarantineFile!.Path}; remove it from that file to release it.";
        if (!_settings.SetQuarantined(projectId, id, quarantined)) return null;
        PublishSettings();
        Log(projectId, LogLevels.Info, (quarantined ? "Quarantined " : "Released from quarantine: ") + id);
        return null;
    }

    /// <summary>The shared quarantine list (<c>--quarantine-file</c>, <c>quarantineFile</c>): additive to the tests quarantined in the UI.</summary>
    public QuarantineFile? QuarantineFile { get; init; }

    private bool InQuarantineFile(string projectId, string id)
    {
        if (QuarantineFile is null) return false;
        lock (Sync) return _state.Tests.TryGetValue((projectId, id), out var t) && QuarantineFile.Matches(t);
    }

    /// <summary>The quarantined tests as "projectId|id" keys: the ones set in the UI plus the tests the quarantine file lists.</summary>
    private List<string> QuarantinedKeysLocked()
    {
        var keys = _settings.Quarantined();
        if (QuarantineFile is null) return keys;
        var known = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var t in _state.Tests.Values)
            if (QuarantineFile.Matches(t) && known.Add(WorkspaceSettings.QuarantineKey(t.ProjectId, t.Id))) keys.Add(WorkspaceSettings.QuarantineKey(t.ProjectId, t.Id));
        return keys;
    }

    // ------------------------------------------------------------------ watch

    private WatchMode? _watchMode;

    /// <summary>Watch mode (WatchMode.cs): its file changes start the smart watch run (Explorer.ImpactAndWatch.cs).</summary>
    private WatchMode WatchMode => LazyInitializer.EnsureInitialized(ref _watchMode, () => new WatchMode(_state, this, PublishSettings, RunWatchAsync));

    public void SetWatch(bool enabled) => WatchMode.Set(enabled);

    // ------------------------------------------------------------------ shutdown

    public async ValueTask DisposeAsync()
    {
        // Disposed twice on shutdown (LabcoatApp stops it before the host disposes its services): the second call has
        // nothing left to do, and _shutdown is disposed by then.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.CancelShutdownAsync();
        StopProjectWatcher(); // Explorer.Projects.cs
        lock (Sync)
        {
            Hang.Dispose(); // also when no run used it yet: a later access must not start a timer on a disposed explorer
            LiveService.Dispose(); // same for the live output flush timer
            foreach (var p in _state.Projects)
            {
                WatchMode.Stop(p);
                if (p.ActiveSession != p.Warm?.Host) p.ActiveSession?.Kill(); // warm hosts get a graceful exit below
            }
        }
        await Hosts.ShutdownAsync();
        lock (Sync) FlushTestsLocked();
        await _flushTimer.DisposeAsync();
        _history.Dispose(); // flush a pending debounced save
        _fullCoverageStore.Dispose(); // same for the full coverage report
        // Background work still holding the shutdown token or an operation token only reads it: cancelled tokens stay valid.
        _gate.Dispose();
    }
}
