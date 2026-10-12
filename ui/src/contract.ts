// Types mirroring docs/contract.md (browser <-> explorer host). Keep in sync with the contract.

export type Outcome =
  | "notRun"
  | "queued"
  | "running"
  | "passed"
  | "failed"
  | "skipped"
  | "timedOut"
  | "cancelled";

export interface Trait {
  key: string;
  value: string;
}

export interface TestNode {
  /** MTP node uid (unique per project) */
  id: string;
  projectId: string;
  /** e.g. "Multiply_ReturnsProduct (2,3,6)" */
  displayName: string;
  /** "SampleTests.Math" */
  namespace?: string;
  /** "CalculatorTests" */
  className?: string;
  /** "Multiply_ReturnsProduct" */
  methodName?: string;
  /** absolute source path if known */
  filePath?: string;
  line?: number;
  /** last line of the test method, when MTP reports location.line-end */
  lineEnd?: number;
  traits: Trait[];
  outcome: Outcome;
  durationMs?: number;
  errorMessage?: string;
  errorType?: string;
  stackTrace?: string;
  stdOut?: string;
  stdErr?: string;
  lastRunAt?: string;
  /** W3C trace id of the test's span (Labcoat.Testing); links to the Aspire dashboard when aspireDashboardUrl is set */
  traceId?: string;
  /** recent results, most recent last (max 20); persisted across host restarts */
  history?: HistoryEntry[];
  /** passed and failed both seen in the last 10 runs, with ≥ 2 pass/fail transitions */
  flaky?: boolean;
  /** the result (outcome, duration, error, …) was loaded from a previous session */
  stale?: boolean;
  /** client-side only (past-run snapshot): the test was not part of the run being viewed */
  notInRun?: boolean;
}

export interface HistoryEntry {
  outcome: Outcome;
  durationMs?: number;
  /** ISO-8601 time of the result */
  at: string;
  runId?: string;
}

export type ProjectStatus = "idle" | "building" | "discovering" | "running" | "error";

export interface TestProject {
  /** stable id, e.g. project file name without extension */
  id: string;
  name: string;
  /** absolute .csproj path */
  path: string;
  status: ProjectStatus;
  lastError?: string;
  /** tail of last build output when it failed */
  buildOutput?: string;
  /** long-lived test host process: "warm" = reruns skip process (and fixture) startup */
  hostState?: HostState;
}

export type HostState = "cold" | "starting" | "warm";

/** Why a watch-triggered run selected its tests (RunSummary.affected) */
export interface AffectedRun {
  /** testFile: tests located in a changed file; impactMap: tests whose covered methods changed; mixed; all: fallback;
   *  git: a "Run affected" run (base / ref / tests below) */
  strategy: "testFile" | "impactMap" | "mixed" | "all" | "git";
  changedFiles: string[];
}

/** Smart-watch test impact map stats (ExplorerState.impactMap) */
export interface ImpactMapInfo {
  files: number;
  tests: number;
  builtAt?: string;
  building?: boolean;
}

export interface RunSummary {
  runId: string;
  startedAt: string;
  finishedAt?: string;
  total: number;
  passed: number;
  failed: number;
  skipped: number;
  durationMs?: number;
  /** set on watch-triggered runs */
  affected?: AffectedRun;
  /** "all" = no explicit test ids (Run all / a project); "subset" = only `total` selected tests ran */
  scope?: "all" | "subset";
  /** who started the run (best effort): UI, watch mode, or REST (e.g. an Aspire dashboard command) */
  trigger?: "user" | "watch" | "api" | "debug" | "cli";
}

/** One test's result in a persisted past run (GET /api/runs/{runId}); error/stack only for failed tests (truncated) */
export interface RunResult {
  projectId: string;
  id: string;
  outcome: Outcome;
  durationMs?: number;
  errorMessage?: string;
  stackTrace?: string;
}

export interface RunDetails {
  run: RunSummary;
  results: RunResult[];
}

export interface ExplorerState {
  projects: TestProject[];
  tests: TestNode[];
  currentRun?: RunSummary;
  lastRun?: RunSummary;
  watch: boolean;
  /** set when hosted under Aspire (env ASPIRE_DASHBOARD_URL) */
  aspireDashboardUrl?: string;
  coverageEnabled: boolean;
  /** the latest coverage run's own report (a subset run: just those tests) */
  coverage?: CoverageReport;
  /**
   * the full report (Coverage page): per project its last full run (every test of the project), with later subset
   * runs merged into unchanged files; never replaced by a subset run (docs/contract.md "The full report")
   */
  fullCoverage?: CoverageReport;
  /** smart-watch impact map; absent until any per-test / per-class coverage is known */
  impactMap?: ImpactMapInfo;
  /** recent log history; only present in the on-connect snapshot (and GET /api/state) */
  logs?: { projectId?: string; level: "info" | "warning" | "error"; message: string; timestamp: string }[];
  /** persisted past runs, newest first (max 50) */
  runs?: RunSummary[];
}

// ---- Coverage (added) ------------------------------------------------------------------------

export interface CoverageLineHit {
  line: number;
  hits: number;
  branchesCovered?: number;
  branchesTotal?: number;
}

export interface FileCoverage {
  /** absolute source path */
  path: string;
  /** relative to the common root of all covered files, for display */
  relativePath: string;
  /** assembly name, e.g. "AspireSample.Api" */
  module: string;
  linesCovered: number;
  linesValid: number;
  /** 0..1 */
  lineRate: number;
  branchesCovered: number;
  branchesValid: number;
  /** 0..1 */
  branchRate: number;
  lines: CoverageLineHit[];
  /** full report only: the file changed since its coverage was collected (numbers and line marks are from before) */
  stale?: boolean;
}

/** CoverageReport.merged: a run of selected tests merged into the full report (into files unchanged since the full run). */
export interface CoverageMergedRun {
  runId: string;
  generatedAt: string;
  projectId: string;
  testCount: number;
  /** files the run added covered lines to */
  filesChanged: number;
}

export interface CoverageReport {
  runId: string;
  generatedAt: string;
  projectIds: string[];
  lineRate: number;
  branchRate: number;
  linesCovered: number;
  linesValid: number;
  branchesCovered: number;
  branchesValid: number;
  files: FileCoverage[];
  /** "all" = the run had no explicit testIds; "subset" = only `testIds` ran (e.g. coverage for one test). Missing = "all". */
  scope?: "all" | "subset";
  /** test ids of a "subset" run */
  testIds?: string[];
  /** only from GET /api/coverage/test: the test app was rebuilt since this report was collected */
  stale?: boolean;
  /** per covered project: which coverage settings applied */
  settings?: CoverageSettingsInfo[];
  /** full report only: subset runs merged in since the full run(s) */
  merged?: CoverageMergedRun[];
  /** full report only: files (absolute paths) changed since their coverage was collected */
  staleFiles?: string[];
}

/** CoverageReport.settings: how a project's coverage was collected (docs/features.md "Code coverage"). */
export interface CoverageSettingsInfo {
  projectId: string;
  /** "file": a coverage settings / runsettings file; "testconfig": the codeCoverage section of testconfig.json; "defaults": Labcoat's */
  source: "file" | "testconfig" | "defaults";
  /** the settings file (absolute); absent for "defaults" */
  path?: string;
  /** how the file was found: Coverage.Settings, --coverage-settings in TestingPlatformCommandLineArguments, or by name */
  origin?: "option" | "msbuild" | "convention";
  /** "dynamic": the CLR profiler (no file rewritten); "static": assemblies instrumented in place */
  instrumentation: "dynamic" | "static";
  /** "defaults" only: the assemblies counted (referenced projects); absent = every module except tests, frameworks and Labcoat */
  modules?: string[];
  include?: string[];
  exclude?: string[];
}

export type LogLevel = "info" | "warning" | "error";

export type ServerEvent =
  | { type: "state"; state: ExplorerState }
  | { type: "projectStatus"; project: TestProject }
  | { type: "testsUpdated"; tests: TestNode[] }
  | { type: "runStarted"; run: RunSummary; testIds: string[] }
  | { type: "runFinished"; run: RunSummary }
  | { type: "log"; projectId?: string; level: LogLevel; message: string; timestamp: string }
  /** a coverage run finished: that run's own report (a subset run: just those tests) */
  | { type: "coverageUpdated"; coverage: CoverageReport }
  /** the full report (Coverage page) changed: a full run, a merged subset run, or files changed since */
  | { type: "fullCoverageUpdated"; coverage: CoverageReport }
  /** the persisted run list changed (sent just before runFinished): newest first */
  | { type: "runsUpdated"; runs: RunSummary[] }
  | Round4Event
  | TestOutputEvent
  | TestResultsEvent
  | RunningNowEvent;

export type ClientCommand =
  | { type: "discover"; projectId?: string }
  /** `coverage` overrides the global coverage toggle for this run only */
  | { type: "run"; projectId?: string; testIds?: string[]; coverage?: boolean }
  | { type: "cancel" }
  | { type: "setWatch"; enabled: boolean }
  | { type: "setCoverage"; enabled: boolean }
  /** stop the project's warm test host and start a fresh one (all projects when omitted) */
  | { type: "restartHost"; projectId?: string }
  /** one coverage run per test class to fill the smart-watch impact map */
  | { type: "buildImpactMap"; projectId?: string }
  | Round4Command
  | AffectedCommand;

// --- SHELL additions --- (docs/contract.md "Round 3 additions"; interfaces merge with the declarations above)

/** One MSBuild diagnostic (TestProject.buildErrors). */
export interface BuildDiagnostic {
  file?: string;
  line?: number;
  column?: number;
  code?: string;
  severity: "error" | "warning";
  message: string;
  project?: string;
}

export interface TestProject {
  /** errors (then warnings) parsed from the output of the last build, when it failed */
  buildErrors?: BuildDiagnostic[];
  /** distinct errors / warnings of the last (failed) build */
  buildErrorCount?: number;
  buildWarningCount?: number;
  /** the last build failed after this project's tests had results: those results are from older code */
  resultsOutdated?: boolean;
}

/** RunSummary.watch: what changed and how long the rebuild took (watch-triggered runs). */
export interface WatchContext {
  changedFiles: string[];
  buildMs?: number;
  strategy: "testFile" | "impactMap" | "mixed" | "all";
}

export interface RunSummary {
  watch?: WatchContext;
}

export interface ExplorerState {
  /** display name of the workspace: LABCOAT_NAME (Aspire resource name), else the solution / project name */
  workspaceName?: string;
}

// --- DETAIL additions --- (Round 3: repeat runs, quarantine, tracing, telemetry, editors; mirrors src/Labcoat/Models.cs)

export interface HistoryEntry {
  /** aggregated entry of a repeat run whose repetitions both passed and failed */
  flaky?: boolean;
}

/** RunSummary.repeat: per-test pass/fail counts over the repetitions of a repeat run (run with `repeat`). */
export interface RepeatSummary {
  /** requested repetitions */
  count: number;
  /** repetitions that completed (less than count when cancelled) */
  completed: number;
  perTest: RepeatTestResult[];
}

export interface RepeatTestResult {
  projectId: string;
  id: string;
  passed: number;
  failed: number;
  skipped: number;
  durationsMs: number[];
}

export interface RunSummary {
  repeat?: RepeatSummary;
  /** results of quarantined tests (not included in passed/failed/skipped) */
  quarantined?: number;
}

/** ExplorerState.editors: editors for "open in editor" (POST /api/open-in-editor) and whether they were found. */
export type EditorId = "vscode" | "rider" | "visualstudio";

export interface EditorInfo {
  id: EditorId;
  name: string;
  available: boolean;
}

export interface ExplorerState {
  /** test hosts get the Aspire dashboard's OTLP endpoint (per-test traces); off by default */
  tracingEnabled?: boolean;
  /** an OTLP endpoint is known (hosted under Aspire), so tracing can be turned on */
  tracingAvailable?: boolean;
  /** the host's trace filter (--trace-filter, LABCOAT_TRACE_FILTER, traceFilter, WithTestTracing(filter:)): only matching tests are traced; absent = all */
  tracingFilter?: string;
  /** directories whose files GET /api/source serves (canonical, absolute) */
  sourceRoots?: string[];
  /** quarantined tests as "projectId|id" keys */
  quarantined?: string[];
  editors?: EditorInfo[];
  /** GET /api/tests/telemetry can fetch a test's error spans / logs from the Aspire dashboard */
  telemetryAvailable?: boolean;
}

/** GET /api/tests/telemetry?projectId=&id= (or &traceId=) */
export interface TestTelemetry {
  traceId: string;
  spans: TelemetrySpan[];
  logs: TelemetryLog[];
}

export interface TelemetrySpan {
  name: string;
  /** service.name of the resource that produced the span, e.g. "api" */
  resource: string;
  status: "error" | "ok" | "unset";
  statusMessage?: string;
  durationMs?: number;
  startTime?: string;
  spanId?: string;
  attributes?: Record<string, string>;
  exceptionType?: string;
  exceptionMessage?: string;
  httpStatusCode?: number;
}

export interface TelemetryLog {
  resource: string;
  level: "warning" | "error" | "critical";
  message: string;
  timestamp: string;
  spanId?: string;
  category?: string;
  attributes?: Record<string, string>;
}

/** Round 3 commands not in the ClientCommand union above (send with `store.send(cmd as ClientCommand)`). */
export type DetailCommand =
  | { type: "run"; projectId?: string; testIds?: string[]; coverage?: boolean; repeat?: number }
  | { type: "setQuarantine"; projectId: string; id: string; quarantined: boolean };

// --- RUNS additions --- (Round 3: discovery diagnostics, tracing toggle)

/** TestProject.diagnostics: why discovery found 0 tests / the test host could not start (cleared when tests are found). */
export interface ProjectDiagnostic {
  level: "info" | "warning" | "error";
  /** e.g. "OutputType is 'Library', but Microsoft.Testing.Platform test projects are executables." */
  message: string;
  /** what to do, e.g. "Add <OutputType>Exe</OutputType> …" */
  hint?: string;
  /** extra text: the last ≤ 20 stderr lines of a test host that exited ("\n"-separated) */
  details?: string;
  /** a page that explains the fix, e.g. docs/troubleshooting.md#my-project-uses-vstest */
  link?: string;
}

export interface TestProject {
  diagnostics?: ProjectDiagnostic[];
  /** the project isn't on Microsoft.Testing.Platform: "vstest" (a VSTest test project) or "none" (no test framework).
   *  Labcoat doesn't build or start it; `diagnostics` says how to switch it. Absent: on MTP (or not known yet). */
  testPlatform?: "vstest" | "none";
}

/** Round 3 commands owned by the runs/settings area (send with `store.send(cmd as unknown as ClientCommand)`). */
export type RunsCommand = { type: "setTracing"; enabled: boolean };

// --- COVERAGE additions --- (Round 3: GET /api/coverage/line, "Which tests cover this line?")
export interface CoveringTest {
  projectId: string;
  id: string;
  /** perTest = exact (the line has hits in that test's cached single-test coverage); impactMap = approximate (method span / class granular) */
  source: "perTest" | "impactMap";
  /** perTest only */
  hits?: number;
  /** perTest only: the test app was rebuilt since that coverage was collected */
  stale?: boolean;
}
export interface CoveringTests {
  /** perTest first, then impactMap; each in tree order */
  tests: CoveringTest[];
  /** true when not every test has single-test coverage cached: the list may be incomplete */
  partial: boolean;
}

// --- Round 4 additions --- (docs/contract.md "Round 4 additions": backend hardening)
export interface TestNode {
  /** stdOut/stdErr were cut to 64 K chars each; GET /api/tests/output has the full text while it is retained */
  outputTruncated?: boolean;
  /** lean payloads only (stdOut/stdErr omitted): the test has console output to fetch (GET /api/tests/details) */
  hasOutput?: boolean;
}
export interface TestProject {
  /** TFM the tests run on, e.g. "net10.0" */
  targetFramework?: string;
  /** all TFMs of the project, declaration order */
  availableFrameworks?: string[];
}
export interface RunSummary {
  /** a test host exited / crashed during the run */
  aborted?: boolean;
  abortReason?: string;
  /** cancelled before every requested test reported */
  cancelled?: boolean;
  /** requested tests without a result (aborted / cancelled / never reported); counted in total only */
  notRun?: number;
  /** a debug run (trigger "debug") */
  debug?: boolean;
}
export interface PendingRunInfo {
  runId: string;
  requestedAt: string;
  scope: "all" | "subset";
  projectIds?: string[];
  testCount?: number;
  coverage?: boolean;
  repeat?: number;
  trigger?: "user" | "watch" | "api" | "debug";
  /** how many requests were merged into it */
  requests: number;
}
export interface ExplorerState {
  pendingRun?: PendingRunInfo;
  lean?: boolean;
}
export interface CoverageReport {
  /** linesValid == 0: render "No coverable lines" (lineRate is 0) */
  noCoverableLines?: boolean;
  /** full report only: (part of) it was restored at startup, collected before the host restarted; the latest such full run's time */
  restoredAt?: string;
}
export interface DebugHint {
  editor: "vscode" | "rider" | "visualstudio";
  name: string;
  steps: string;
  launchJson?: string;
}
export type Round4Event =
  | { type: "pendingRun"; pending?: PendingRunInfo }
  | { type: "settingsChanged"; settings: { watch: boolean; coverageEnabled: boolean; tracingEnabled: boolean; quarantined?: string[]; impactMap?: ImpactMapInfo } }
  | { type: "debuggerWaiting"; projectId: string; pid: number; testIds: string[]; runId: string; hints: DebugHint[] }
  | { type: "debuggerAttached"; projectId: string; pid: number; runId: string }
  | { type: "commandRejected"; command?: string; status: 400 | 404 | 409; error: string; unknownTestIds?: string[] };
export type CommandRejectedEvent = Extract<Round4Event, { type: "commandRejected" }>;
export type Round4Command = { type: "debug"; projectId?: string; testIds: string[] } | { type: "setTargetFramework"; projectId: string; targetFramework?: string };

// ---- Round 5: UI setting defaults (docs/contract.md "Round 5 additions") ----
/** Settings that can get a default / lock from outside the browser (settings files, AppHost, env, CLI). */
export type UiSettingKey =
  | "theme"
  | "mode"
  | "motion"
  | "editor"
  | "coverageTint"
  | "confetti"
  | "partialRunsInHistory"
  | "openOutputOnError"
  | "singleKeyShortcuts";
/** user = ~/.labcoat.json (or the config dir), workspace = .labcoat.json / --config, apphost = Labcoat.Aspire.Hosting, env = LABCOAT_UI_SETTINGS, cli = flags */
export type UiSettingSource = "user" | "workspace" | "apphost" | "env" | "cli";
export interface UiDefaults {
  theme?: "catppuccin" | "aspire" | "github" | "one" | "dracula" | "solarized" | "gruvbox";
  mode?: "system" | "light" | "dark";
  motion?: "system" | "reduced" | "off";
  editor?: "vscode" | "rider" | "visualstudio";
  coverageTint?: "line" | "gutter";
  confetti?: boolean;
  partialRunsInHistory?: boolean;
  openOutputOnError?: boolean;
  singleKeyShortcuts?: boolean;
  /** locked settings: the browser can't override them (value = the default above, else the built-in one) */
  locked?: UiSettingKey[];
  /** layer that set each value (the highest one) */
  sources?: Partial<Record<UiSettingKey, UiSettingSource>>;
  /** layer that locked each locked setting (the highest one) */
  lockedBy?: Partial<Record<UiSettingKey, UiSettingSource>>;
  /** loaded settings files, for display: user "~/.labcoat.json", workspace ".labcoat.json" (relative to the workspace) */
  files?: { user?: string; workspace?: string };
}
export interface ExplorerState {
  /** omitted when nothing is configured; also injected into index.html as <html data-labcoat-defaults="…"> */
  uiDefaults?: UiDefaults;
}

// ---- Run affected tests (git) (docs/contract.md "Run affected tests (git)") ----
export type AffectedBase = "working" | "branch" | "range";
/** GET /api/git */
export interface GitInfo {
  /** false: no git executable, or the workspace is not in a repository (see `error`) */
  available: boolean;
  error?: string;
  root?: string;
  /** current branch; absent when detached */
  branch?: string;
  /** short HEAD sha; absent before the first commit */
  head?: string;
  detached?: boolean;
  /** changed + untracked files */
  dirty?: number;
  upstream?: string;
  ahead?: number;
  behind?: number;
  shallow?: boolean;
  /** the base `branch` uses without a ref (e.g. "origin/main") */
  defaultBase?: string;
  /** the server option affectedBase, when configured */
  affectedBase?: string;
}
export type AffectedMapping = "testFile" | "impactMap" | "coverage" | "project" | "none";
export interface AffectedFile {
  path: string;
  /** relative to the repository root, "/" separators */
  relativePath: string;
  status: "added" | "modified" | "deleted" | "renamed" | "copied" | "typeChanged" | "untracked" | "unmerged";
  oldPath?: string;
  kind: "test" | "product" | "project" | "other";
  mapping: AffectedMapping;
  /** tests selected because of this file */
  tests: number;
  projectIds?: string[];
  /** changed line ranges (new side, inclusive) */
  lines?: [number, number][];
  /** impactMap: the executed methods the change overlaps */
  methods?: string[];
}
export interface AffectedTest {
  projectId: string;
  id: string;
  /** one per file that selected it */
  reasons: string[];
}
export interface AffectedProject {
  projectId: string;
  name: string;
  tests: number;
  /** every test of the project */
  all?: boolean;
}
/** GET /api/affected */
export interface AffectedPlan {
  base: AffectedBase;
  ref?: string;
  mergeBase?: string;
  git: GitInfo;
  files: AffectedFile[];
  tests: AffectedTest[];
  projects: AffectedProject[];
  counts: { files: number; tests: number; projects: number };
  strategy: "testFile" | "impactMap" | "coverage" | "project" | "mixed" | "none";
  /** a product file fell back to "all tests of a project": building the impact map would narrow the selection */
  needsImpactMap?: boolean;
  impactMap?: ImpactMapInfo;
  warnings?: string[];
}
export interface AffectedRun {
  /** strategy "git" only */
  base?: AffectedBase;
  ref?: string;
  tests?: number;
}
export type AffectedCommand = { type: "runAffected"; base?: AffectedBase; ref?: string; coverage?: boolean; buildImpactMap?: boolean };

// ---- Round 6: Verify snapshot testing (docs/contract.md "Round 6 additions → Snapshot testing") -----------------
export interface TestNode {
  /** failed with a Verify snapshot mismatch whose received files are not accepted / rejected yet */
  hasSnapshotMismatch?: boolean;
}
export type SnapshotKind = "text" | "image" | "binary";
export interface SnapshotFile {
  /** the verified file name, e.g. "ReceiptTests.Render.verified.txt" (selects it in accept / reject) */
  name: string;
  receivedPath: string;
  verifiedPath: string;
  kind: SnapshotKind;
  /** no verified file yet: accepting creates it */
  isNew: boolean;
}
export interface TestSnapshots {
  projectId: string;
  id: string;
  directory?: string;
  snapshots: SnapshotFile[];
}
export interface SnapshotActionResult {
  tests: { projectId: string; id: string; files: string[] }[];
  errors?: { projectId: string; id: string; name?: string; error: string }[];
  /** accept with rerun: the run that re-runs the tests */
  runId?: string;
}

// --- LIVE OUTPUT additions --- (docs/contract.md "Live console output")

/**
 * Console output of a test that is still running, coalesced by the host (≤ every 100 ms, ≤ 8 K characters per test and
 * flush). `stream`: "stdout" / "stderr" = attributed exactly (framework streaming, xUnit live output, Labcoat.Testing's
 * LabcoatOutput); "host" = a line the test host process wrote to its own stdout (`err`: stderr), attributed by timing.
 * `shared`: several tests of the project were running, the line may belong to another one. `seq` counts the events of
 * one execution of the test from 1. The final result's stdOut/stdErr stays authoritative.
 */
export interface TestOutputEvent {
  type: "testOutput";
  projectId: string;
  id: string;
  stream: "stdout" | "stderr" | "host";
  text: string;
  seq: number;
  shared?: boolean;
  err?: boolean;
}

/** GET /api/tests/live-output?projectId=&id= (404 when the test is not running). */
export interface LiveOutputSnapshot {
  projectId: string;
  id: string;
  seq: number;
  chunks: { stream: TestOutputEvent["stream"]; text: string; shared?: boolean; err?: boolean }[];
  omitted?: number;
}

// --- Round 7 additions --- (docs/contract.md "Round 7 additions": compact run traffic at 20,000 tests)

/**
 * Result-only changes of tests the client already has (the host sends `testsUpdated` full entries only for new tests
 * and structural changes: name, location, traits). Grouped by project; apply with `applyResultPatch` (store.ts).
 */
export interface TestResultsEvent {
  type: "testResults";
  projects: { projectId: string; results: TestResultPatch[] }[];
  /** the run that `recorded` patches refer to */
  runId?: string;
}
/**
 * A patch: every field that is present was set to that value, the fields named in `clear` were removed, everything else
 * (incl. the structural fields) is unchanged. Lean clients get `hasOutput` instead of `stdOut` / `stdErr`.
 */
export interface TestResultPatch extends Partial<Pick<TestNode,
  "outcome" | "durationMs" | "errorMessage" | "errorType" | "stackTrace" | "stdOut" | "stdErr" | "hasOutput" | "outputTruncated"
  | "lastRunAt" | "traceId" | "stale" | "flaky" | "hasSnapshotMismatch" | "history">> {
  id: string;
  /** one entry was appended to the history (keep the last 20) */
  historyAdd?: HistoryEntry;
  /** short form of historyAdd: `{ outcome, durationMs, at: lastRunAt, runId: event.runId }` from the patched test */
  recorded?: boolean;
  /** fields that are no longer set */
  clear?: (keyof TestNode)[];
}
export interface TestNode {
  /** lean snapshots only (wire): index into ExplorerState.files; the store resolves it to `filePath` */
  fileIndex?: number;
}
export interface ExplorerState {
  /** lean snapshots only: the distinct test source files (absolute paths), referenced by TestNode.fileIndex */
  files?: string[];
  /**
   * lean snapshots only: per project an opaque stamp that changes when its tests' history changed other than through
   * `testResults` (discovery restored a previous session's history): refetch GET /api/tests/history?projectId= then
   */
  historyStamps?: Record<string, string>;
}

// --- RUN LIMITS additions --- (docs/contract.md "Running now, slow tests and timeouts")

/** One running test; `elapsedMs` when the event was sent (the client advances it). */
export interface RunningTestInfo {
  projectId: string;
  id: string;
  displayName: string;
  elapsedMs: number;
  /** running longer than the hang threshold ("possibly hanging") */
  slow?: boolean;
}
/** The tests running now (the longest-running ones, longest first, at most 10). */
export interface RunningNowInfo {
  count: number;
  tests: RunningTestInfo[];
  /** a test running this long is possibly hanging; absent = never */
  hangThresholdMs?: number;
  /** the per-test timeout (testTimeout); absent = none */
  testTimeoutMs?: number;
}
/** The running tests changed (at most about once a second during a run); null = the run ended. */
export interface RunningNowEvent {
  type: "runningNow";
  runningNow: RunningNowInfo | null;
}
export interface ExplorerState {
  /** during a run: the tests running now */
  runningNow?: RunningNowInfo;
  /** the run limits in effect */
  limits?: { maxParallelProjects: number; hangThresholdMs?: number; testTimeoutMs?: number };
}

// --- SLOWEST TESTS --- (docs/contract.md "Slowest tests": GET /api/tests/slowest)

export interface SlowTestPoint {
  at: string;
  durationMs: number;
  outcome: Outcome;
}
export interface SlowTest {
  projectId: string;
  id: string;
  name: string;
  className?: string;
  namespace?: string;
  /** the latest duration */
  durationMs: number;
  /** median of the earlier results */
  medianMs?: number;
  /** durationMs / medianMs */
  factor?: number;
  quarantined?: boolean;
  /** recent durations, oldest first, the latest last */
  trend: SlowTestPoint[];
}
export interface SlowestTestsResponse {
  projectId?: string;
  total: number;
  tests: SlowTest[];
}
