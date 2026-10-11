using System.Text.Json;
using System.Text.Json.Serialization;

namespace Labcoat;

// Types mirror docs/contract.md exactly. All JSON is camelCase.

[JsonConverter(typeof(JsonStringEnumConverter<Outcome>))]
public enum Outcome
{
    [JsonStringEnumMemberName("notRun")] NotRun,
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("passed")] Passed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("skipped")] Skipped,
    [JsonStringEnumMemberName("timedOut")] TimedOut,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProjectStatus>))]
public enum ProjectStatus
{
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("building")] Building,
    [JsonStringEnumMemberName("discovering")] Discovering,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("error")] Error,
}

public sealed record Trait(string Key, string Value);

/// <summary>One past result of a test (see TestNode.History).</summary>
/// <param name="Flaky">Set on the aggregated entry of a repeat run whose repetitions both passed and failed.</param>
public sealed record HistoryEntry(Outcome Outcome, double? DurationMs, string At, string? RunId = null, bool? Flaky = null);

public sealed record TestNode
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string DisplayName { get; set; }
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public string? MethodName { get; set; }
    public string? FilePath { get; set; }
    /// <summary>Lean snapshots only: index into <see cref="ExplorerState.Files"/> instead of <see cref="FilePath"/>.</summary>
    public int? FileIndex { get; set; }
    public int? Line { get; set; }
    public int? LineEnd { get; set; }
    public List<Trait> Traits { get; set; } = [];
    public Outcome Outcome { get; set; } = Outcome.NotRun;
    public double? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorType { get; set; }
    public string? StackTrace { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
    public string? LastRunAt { get; set; }
    /// <summary>W3C trace id of the test's span (Labcoat.Testing), for linking to the Aspire dashboard.</summary>
    public string? TraceId { get; set; }
    /// <summary>Recent results, oldest first (max 20); persisted across host restarts by <see cref="HistoryStore"/>.</summary>
    public List<HistoryEntry>? History { get; set; }
    /// <summary>Both passed and failed within the last 10 runs.</summary>
    public bool? Flaky { get; set; }
    /// <summary>The result was loaded from a previous session (not produced by this host instance).</summary>
    public bool? Stale { get; set; }
    /// <summary>stdOut/stdErr were cut to <see cref="TestOutput.MaxChars"/> (head + tail); GET /api/tests/output has the full text when retained.</summary>
    public bool? OutputTruncated { get; set; }
    /// <summary>Lean payloads only (stdOut/stdErr omitted): the test has console output to fetch.</summary>
    public bool? HasOutput { get; set; }
    /// <summary>
    /// Failed with a Verify snapshot mismatch (new or changed snapshot; Snapshots.cs) whose received files were not
    /// accepted / rejected yet. Derived from the error message; omitted when false.
    /// </summary>
    public bool? HasSnapshotMismatch =>
        !SnapshotsResolved && Outcome is Outcome.Failed or Outcome.TimedOut && VerifySnapshots.IsMismatchMessage(ErrorMessage) ? true : null;
    /// <summary>The snapshots of this result were accepted / rejected (cleared by the next result).</summary>
    [JsonIgnore] public bool SnapshotsResolved { get; set; }

    public TestNode Copy() => this with { Traits = [.. Traits] };

    /// <summary>
    /// The copy sent to lean clients: no history and no console output (fetched on demand); also no <c>lineEnd</c>
    /// equal to <c>line</c> (the client reads a missing lineEnd as line).
    /// </summary>
    public TestNode LeanCopy() => this with
    {
        Traits = [.. Traits],
        LineEnd = LineEnd == Line ? null : LineEnd,
        History = null,
        StdOut = null,
        StdErr = null,
        HasOutput = StdOut is { Length: > 0 } || StdErr is { Length: > 0 } ? true : null,
    };

    /// <summary>The structural part (what the tree is built from) equals <paramref name="o"/>'s: a result-only change.</summary>
    public bool SameShape(TestNode o) =>
        DisplayName == o.DisplayName && Namespace == o.Namespace && ClassName == o.ClassName && MethodName == o.MethodName
        && FilePath == o.FilePath && Line == o.Line && LineEnd == o.LineEnd
        && (ReferenceEquals(Traits, o.Traits) || Traits.SequenceEqual(o.Traits));

    public void ClearResult()
    {
        DurationMs = null;
        ErrorMessage = null;
        ErrorType = null;
        StackTrace = null;
        StdOut = null;
        StdErr = null;
        TraceId = null;
        OutputTruncated = null;
        SnapshotsResolved = false;
    }
}

public sealed record TestProject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Idle;
    public string? LastError { get; set; }
    public string? BuildOutput { get; set; }
    /// <summary>"cold" | "starting" | "warm": state of the project's long-lived test host process.</summary>
    public string? HostState { get; set; } = "cold";
    /// <summary>Errors (and warnings) parsed from the output of the last build, when it failed.</summary>
    public List<BuildDiagnostic>? BuildErrors { get; set; }
    /// <summary>Number of distinct errors / warnings of the last (failed) build.</summary>
    public int? BuildErrorCount { get; set; }
    public int? BuildWarningCount { get; set; }
    /// <summary>The last build failed after this project's tests had results: those results are from older code.</summary>
    public bool? ResultsOutdated { get; set; }
    /// <summary>Why discovery found nothing / the test host could not start (with hints).</summary>
    public List<ProjectDiagnostic>? Diagnostics { get; set; }
    /// <summary>The target framework the tests run on (multi-targeted projects: the chosen one, see setTargetFramework).</summary>
    public string? TargetFramework { get; set; }
    /// <summary>All target frameworks of the project (from TargetFrameworks / TargetFramework).</summary>
    public List<string>? AvailableFrameworks { get; set; }
    /// <summary>
    /// "vstest" when the project is a test project that isn't on Microsoft.Testing.Platform, "none" when it has no test
    /// framework at all: Labcoat doesn't build or start it, and <see cref="Diagnostics"/> says how to switch it. Null:
    /// Microsoft.Testing.Platform (or not known yet).
    /// </summary>
    public string? TestPlatform { get; set; }

    public TestProject Copy() => this with { BuildErrors = BuildErrors?.ToList(), Diagnostics = Diagnostics?.ToList(), AvailableFrameworks = AvailableFrameworks?.ToList() };
}

public sealed record RunSummary
{
    public required string RunId { get; init; }
    public required string StartedAt { get; init; }
    public string? FinishedAt { get; set; }
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public double? DurationMs { get; set; }
    /// <summary>Set on watch-triggered runs: why these tests were selected.</summary>
    public AffectedRun? Affected { get; init; }
    /// <summary>"all" = no explicit test ids (Run all / a whole project); "subset" = only the given tests ran.</summary>
    public string? Scope { get; init; }
    /// <summary>Who started the run (best effort): "user" (UI), "watch", "api" (REST, e.g. an Aspire dashboard command).</summary>
    public string? Trigger { get; init; }
    /// <summary>Watch-triggered runs: what changed and how long the rebuild took.</summary>
    public WatchContext? Watch { get; init; }
    /// <summary>Repeat runs (run with <c>repeat</c>): per-test pass/fail counts over the repetitions.</summary>
    public RepeatSummary? Repeat { get; set; }
    /// <summary>Results of quarantined tests (not included in passed/failed/skipped).</summary>
    public int? Quarantined { get; set; }
    /// <summary>The run ended early because a test host exited / crashed (see <see cref="AbortReason"/>).</summary>
    public bool? Aborted { get; set; }
    public string? AbortReason { get; set; }
    /// <summary>The run was cancelled (cancel command) before every test reported.</summary>
    public bool? Cancelled { get; set; }
    /// <summary>Requested tests that never ran (aborted / cancelled runs).</summary>
    public int? NotRun { get; set; }
    /// <summary>A debug run (POST /api/debug): one-off test host waiting for a debugger.</summary>
    public bool? Debug { get; init; }

    public RunSummary Copy() => this with { };
}

public sealed record ExplorerState
{
    public required List<TestProject> Projects { get; init; }
    public required List<TestNode> Tests { get; init; }
    public RunSummary? CurrentRun { get; init; }
    public RunSummary? LastRun { get; init; }
    public bool Watch { get; init; }
    public string? AspireDashboardUrl { get; init; }
    public bool CoverageEnabled { get; init; }
    /// <summary>The report of the latest coverage run, whatever it ran (a subset run gives that subset's coverage).</summary>
    public CoverageReport? Coverage { get; init; }
    /// <summary>The full report (Coverage page): per project its last full run, with later subset runs merged in where
    /// the code is unchanged (FullCoverage.cs). Never replaced by a subset run.</summary>
    public CoverageReport? FullCoverage { get; init; }
    /// <summary>Test impact map stats (smart watch); null until any per-test/per-class coverage is known.</summary>
    public ImpactMapInfo? ImpactMap { get; init; }
    /// <summary>Recent log history; only present in the on-connect snapshot and GET /api/state.</summary>
    public List<LogEvent>? Logs { get; init; }
    /// <summary>Persisted past runs (newest first, max <see cref="HistoryStore.MaxStoredRuns"/>).</summary>
    public List<RunSummary>? Runs { get; init; }
    /// <summary>Test hosts get the Aspire dashboard's OTLP endpoint (per-test traces). Off by default.</summary>
    public bool TracingEnabled { get; init; }
    /// <summary>An OTLP endpoint is known (hosted under Aspire), so tracing can be turned on.</summary>
    public bool TracingAvailable { get; init; }
    /// <summary>Trace filter of the host (--trace-filter / LABCOAT_TRACE_FILTER / traceFilter): only matching tests are traced; null = all.</summary>
    public string? TracingFilter { get; init; }
    /// <summary>Display name of the workspace: LABCOAT_NAME (Aspire resource name), else the solution / project name.</summary>
    public string? WorkspaceName { get; init; }
    /// <summary>Directories whose files GET /api/source serves (canonical, absolute).</summary>
    public List<string>? SourceRoots { get; init; }
    /// <summary>Quarantined tests as "projectId|id" keys (persisted per workspace).</summary>
    public List<string>? Quarantined { get; init; }
    /// <summary>Editors for "open in editor" (POST /api/open-in-editor) and whether they were found on this machine.</summary>
    public List<EditorInfo>? Editors { get; init; }
    /// <summary>The backend can fetch a test's error spans / logs from the Aspire dashboard (GET /api/tests/telemetry).</summary>
    public bool? TelemetryAvailable { get; init; }
    /// <summary>A run request waiting for the current operation (at most one; further requests merge into it).</summary>
    public PendingRunInfo? PendingRun { get; init; }
    /// <summary>Lean snapshot (?lean=1): tests carry no history / console output.</summary>
    public bool? Lean { get; init; }
    /// <summary>Lean snapshots only: the distinct test source files (absolute); tests carry <c>fileIndex</c> into it.</summary>
    public List<string>? Files { get; init; }
    /// <summary>
    /// Lean snapshots only: per project an opaque stamp that changes when its tests' history changed other than through a
    /// <c>testResults</c> event (discovery restored a previous session's history): refetch GET /api/tests/history then.
    /// </summary>
    public Dictionary<string, string>? HistoryStamps { get; init; }
    /// <summary>
    /// UI setting defaults and locks from the settings files, the AppHost, LABCOAT_UI_SETTINGS and CLI flags
    /// (LabcoatConfig.cs; contract Round 5). Null when nothing is configured.
    /// </summary>
    public System.Text.Json.JsonElement? UiDefaults { get; init; }
    /// <summary>During a run: the tests running now, longest first (RunningTests.cs); null when nothing runs.</summary>
    public RunningNowInfo? RunningNow { get; init; }
    /// <summary>Run limits in effect (maxParallelProjects, hangThreshold, testTimeout; docs/configuration.md).</summary>
    public RunLimitsInfo? Limits { get; init; }
}

/// <summary>ExplorerState.pendingRun / pendingRun event: the queued run request.</summary>
public sealed record PendingRunInfo
{
    public required string RunId { get; init; }
    public required string RequestedAt { get; init; }
    /// <summary>"all" | "subset"</summary>
    public required string Scope { get; init; }
    /// <summary>Projects whose tests are included (null = all projects).</summary>
    public List<string>? ProjectIds { get; init; }
    /// <summary>Number of explicitly selected tests (subset only).</summary>
    public int? TestCount { get; init; }
    public bool? Coverage { get; init; }
    public int? Repeat { get; init; }
    public string? Trigger { get; init; }
    /// <summary>How many requests were merged into this one (1 = just the original).</summary>
    public int Requests { get; init; } = 1;
}

/// <summary>Lean clients: the toggles changed (instead of a full state snapshot).</summary>
public sealed record SettingsInfo
{
    public bool Watch { get; init; }
    public bool CoverageEnabled { get; init; }
    public bool TracingEnabled { get; init; }
    public List<string>? Quarantined { get; init; }
    public ImpactMapInfo? ImpactMap { get; init; }
}

/// <summary>How to attach a debugger with one editor (debuggerWaiting.hints).</summary>
public sealed record DebugHint(string Editor, string Name, string Steps, string? LaunchJson = null);

/// <summary>One MSBuild diagnostic (TestProject.buildErrors).</summary>
public sealed record BuildDiagnostic
{
    public string? File { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Code { get; init; }
    /// <summary>"error" | "warning"</summary>
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public string? Project { get; init; }
}

/// <summary>TestProject.diagnostics: a finding about why a project discovered no tests / could not start.</summary>
/// <param name="Level">"info" | "warning" | "error"</param>
/// <param name="Details">Extra text, e.g. the last stderr lines of a test host that exited.</param>
/// <param name="Link">A page that explains the fix (e.g. docs/troubleshooting.md#my-project-uses-vstest).</param>
public sealed record ProjectDiagnostic(string Level, string Message, string? Hint = null, string? Details = null, string? Link = null);

/// <summary>RunSummary.watch</summary>
public sealed record WatchContext
{
    public required List<string> ChangedFiles { get; init; }
    public double? BuildMs { get; init; }
    /// <summary>"testFile" | "impactMap" | "mixed" | "all"</summary>
    public required string Strategy { get; init; }
}

/// <summary>RunSummary.repeat</summary>
public sealed record RepeatSummary
{
    /// <summary>Requested repetitions.</summary>
    public int Count { get; init; }
    /// <summary>Repetitions that completed (less than Count when cancelled).</summary>
    public int Completed { get; set; }
    public List<RepeatTestResult> PerTest { get; set; } = [];
}

public sealed record RepeatTestResult
{
    public required string ProjectId { get; init; }
    public required string Id { get; init; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public List<double> DurationsMs { get; init; } = [];
}

/// <summary>ExplorerState.editors</summary>
public sealed record EditorInfo(string Id, string Name, bool Available);
/// <summary>One test's result in a persisted past run (GET /api/runs/{runId}).</summary>
public sealed record RunResult(string ProjectId, string Id, Outcome Outcome, double? DurationMs, string? ErrorMessage = null, string? StackTrace = null);

/// <summary>A persisted past run: its summary and per-test results.</summary>
public sealed record RunDetails(RunSummary Run, List<RunResult> Results);

// ---- coverage (contract "Coverage (added)")

public sealed record CoverageLineHit
{
    public int Line { get; init; }
    public int Hits { get; set; }
    public int? BranchesCovered { get; set; }
    public int? BranchesTotal { get; set; }
}

public sealed record FileCoverage
{
    public required string Path { get; init; }
    public string RelativePath { get; set; } = "";
    public required string Module { get; init; }
    public int LinesCovered { get; set; }
    public int LinesValid { get; set; }
    public double LineRate { get; set; }
    public int BranchesCovered { get; set; }
    public int BranchesValid { get; set; }
    public double BranchRate { get; set; }
    public List<CoverageLineHit> Lines { get; set; } = [];
    /// <summary>Only in the full report (ExplorerState.fullCoverage): the file changed since its coverage was collected,
    /// so its numbers and line marks are from the older version.</summary>
    public bool? Stale { get; set; }
}

public sealed record CoverageReport
{
    public required string RunId { get; init; }
    public required string GeneratedAt { get; init; }
    public required List<string> ProjectIds { get; init; }
    public double LineRate { get; init; }
    public double BranchRate { get; init; }
    public int LinesCovered { get; init; }
    public int LinesValid { get; init; }
    public int BranchesCovered { get; init; }
    public int BranchesValid { get; init; }
    public required List<FileCoverage> Files { get; init; }
    /// <summary>"all" = the run had no explicit test ids; "subset" = only <see cref="TestIds"/> ran.</summary>
    public string Scope { get; init; } = "all";
    /// <summary>The test ids of a "subset" run (omitted for "all").</summary>
    public List<string>? TestIds { get; init; }
    /// <summary>Only on GET /api/coverage/test: the test app's output changed since this report was collected.</summary>
    public bool? Stale { get; init; }
    /// <summary>No coverable lines at all (linesValid == 0; lineRate is then 0, not 100%): e.g. the tests have no product code
    /// in a separate project (test assemblies are excluded from coverage).</summary>
    public bool? NoCoverableLines { get; init; }
    /// <summary>Per covered project: which coverage settings applied (CoverageSettings.cs).</summary>
    public List<CoverageSettingsInfo>? Settings { get; init; }
    /// <summary>Only in the full report: runs of selected tests merged in after the full run (FullCoverage.cs).</summary>
    public List<CoverageMergedRun>? Merged { get; init; }
    /// <summary>Only in the full report: files (absolute paths) changed since their coverage was collected.</summary>
    public List<string>? StaleFiles { get; init; }
    /// <summary>Only in the full report: (part of) it was restored from disk at startup, collected before the host
    /// restarted; the time of the latest such full run. Gone once every restored part is replaced by a new full run.</summary>
    public string? RestoredAt { get; init; }
}

/// <summary>CoverageReport.merged: a run of selected tests whose coverage was merged into the full report.</summary>
public sealed record CoverageMergedRun
{
    public required string RunId { get; init; }
    public required string GeneratedAt { get; init; }
    public required string ProjectId { get; init; }
    public int TestCount { get; init; }
    /// <summary>Files of the full report the run added covered lines to.</summary>
    public int FilesChanged { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StateEvent), "state")]
[JsonDerivedType(typeof(ProjectStatusEvent), "projectStatus")]
[JsonDerivedType(typeof(TestsUpdatedEvent), "testsUpdated")]
[JsonDerivedType(typeof(TestResultsEvent), "testResults")]
[JsonDerivedType(typeof(RunStartedEvent), "runStarted")]
[JsonDerivedType(typeof(RunFinishedEvent), "runFinished")]
[JsonDerivedType(typeof(LogEvent), "log")]
[JsonDerivedType(typeof(CoverageUpdatedEvent), "coverageUpdated")]
[JsonDerivedType(typeof(FullCoverageUpdatedEvent), "fullCoverageUpdated")]
[JsonDerivedType(typeof(RunsUpdatedEvent), "runsUpdated")]
[JsonDerivedType(typeof(PendingRunEvent), "pendingRun")]
[JsonDerivedType(typeof(SettingsChangedEvent), "settingsChanged")]
[JsonDerivedType(typeof(DebuggerWaitingEvent), "debuggerWaiting")]
[JsonDerivedType(typeof(DebuggerAttachedEvent), "debuggerAttached")]
[JsonDerivedType(typeof(CommandRejectedEvent), "commandRejected")]
[JsonDerivedType(typeof(TestOutputEvent), "testOutput")] // LiveOutput.cs
[JsonDerivedType(typeof(RunningNowEvent), "runningNow")] // RunningTests.cs
public abstract record ServerEvent;

public sealed record StateEvent(ExplorerState State) : ServerEvent;
public sealed record ProjectStatusEvent(TestProject Project) : ServerEvent;
public sealed record TestsUpdatedEvent(List<TestNode> Tests) : ServerEvent;
/// <summary>Result-only changes of tests the client already has (TestUpdateEncoder.cs; contract "Round 7").</summary>
/// <param name="RunId">The run that <see cref="TestResultPatch.Recorded"/> refers to.</param>
public sealed record TestResultsEvent(List<ProjectResults> Projects, string? RunId = null) : ServerEvent;
public sealed record ProjectResults(string ProjectId, List<TestResultPatch> Results);

/// <summary>
/// One test's result change: the fields that are set changed to that value, the names in <see cref="Clear"/> were removed,
/// everything else (including the structural fields) is unchanged.
/// </summary>
public sealed record TestResultPatch
{
    public required string Id { get; init; }
    public Outcome? Outcome { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorType { get; init; }
    public string? StackTrace { get; init; }
    public string? StdOut { get; init; }
    public string? StdErr { get; init; }
    public bool? HasOutput { get; init; }
    public bool? OutputTruncated { get; init; }
    public string? LastRunAt { get; init; }
    public string? TraceId { get; init; }
    public bool? Stale { get; init; }
    public bool? Flaky { get; init; }
    public bool? HasSnapshotMismatch { get; init; }
    /// <summary>The history was replaced (not just appended to).</summary>
    public List<HistoryEntry>? History { get; init; }
    /// <summary>One entry was appended to the history (the client keeps the last 20).</summary>
    public HistoryEntry? HistoryAdd { get; init; }
    /// <summary>
    /// Short form of <see cref="HistoryAdd"/>: appended <c>{ outcome, durationMs, at: lastRunAt, runId }</c> with this
    /// test's (patched) values and the event's <c>runId</c>.
    /// </summary>
    public bool? Recorded { get; init; }
    /// <summary>Fields that are no longer set (camelCase names).</summary>
    public List<string>? Clear { get; init; }
}
public sealed record RunStartedEvent(RunSummary Run, List<string> TestIds) : ServerEvent;
public sealed record RunFinishedEvent(RunSummary Run) : ServerEvent;
public sealed record CoverageUpdatedEvent(CoverageReport Coverage) : ServerEvent;
/// <summary>The full report (Coverage page) changed: a full run, a merged run of selected tests, or files changed since.</summary>
public sealed record FullCoverageUpdatedEvent(CoverageReport Coverage) : ServerEvent;
/// <summary>The persisted run list changed (a run was recorded): newest first.</summary>
public sealed record RunsUpdatedEvent(List<RunSummary> Runs) : ServerEvent;
public sealed record LogEvent(string? ProjectId, string Level, string Message, string Timestamp) : ServerEvent;
/// <summary>The queued run request changed (null = nothing queued any more).</summary>
public sealed record PendingRunEvent(PendingRunInfo? Pending) : ServerEvent;
/// <summary>Lean clients only: toggles changed (legacy clients get a full <c>state</c> instead).</summary>
public sealed record SettingsChangedEvent(SettingsInfo Settings) : ServerEvent;
/// <summary>A debug run's test host waits for a debugger to attach to <paramref name="Pid"/>.</summary>
public sealed record DebuggerWaitingEvent(string ProjectId, int Pid, List<string> TestIds, string RunId, List<DebugHint> Hints) : ServerEvent;
/// <summary>The debugger attached (the test host connected back); the tests run now.</summary>
public sealed record DebuggerAttachedEvent(string ProjectId, int Pid, string RunId) : ServerEvent;
/// <summary>Sent only to the client whose WebSocket command was invalid (status like the REST endpoint: 400 / 404).</summary>
public sealed record CommandRejectedEvent(string? Command, int Status, string Error, List<string>? UnknownTestIds = null) : ServerEvent;

/// <summary>Union of all ClientCommand shapes (and the REST bodies).</summary>
public sealed record ClientCommand
{
    public string? Type { get; init; }
    public string? ProjectId { get; init; }
    public List<string>? TestIds { get; init; }
    public bool? Enabled { get; init; }
    /// <summary>run only: overrides the global coverage toggle for this run.</summary>
    public bool? Coverage { get; init; }
    /// <summary>run only: run the tests this many times (2–50) on the warm host.</summary>
    public int? Repeat { get; init; }
    /// <summary>setQuarantine: the test id (with ProjectId).</summary>
    public string? Id { get; init; }
    /// <summary>setQuarantine: add (true) or remove (false).</summary>
    public bool? Quarantined { get; init; }
    /// <summary>setTargetFramework: the TFM to run a multi-targeted project on (null/empty = back to the default).</summary>
    public string? TargetFramework { get; init; }
    /// <summary>runAffected: "working" | "branch" | "range" (GitAffected.cs).</summary>
    public string? Base { get; init; }
    /// <summary>runAffected: the base ref ("branch") or the commit range ("range").</summary>
    public string? Ref { get; init; }
    /// <summary>runAffected: build the impact map first for product files without impact data.</summary>
    public bool? BuildImpactMap { get; init; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Minimal escaping: the default encoder writes '"' as \u0022, '+' (every "+00:00" timestamp) as \u002B and
            // any non-ASCII character as \uXXXX, which made the 20,000-test state ~5% bigger. Same JSON values, fewer
            // bytes. Safe here: this JSON is only served as application/json (with nosniff) and over the WebSocket, never
            // embedded into HTML.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return o;
    }

    public static string Now() => DateTimeOffset.UtcNow.ToString("o");
}
