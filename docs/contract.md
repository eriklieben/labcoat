# Browser <-> Explorer host contract

The explorer host (ASP.NET Core, `src/Labcoat`) serves the built UI from `wwwroot` and exposes:

- `GET  /api/state`  -> `ExplorerState` snapshot (JSON)
- `WS   /ws`         -> server pushes `ServerEvent` JSON messages; client sends `ClientCommand` JSON messages
- REST mirrors of commands (handy for curl / tests):
  - `POST /api/discover`            body: `{ "projectId"?: string }`
  - `POST /api/run`                 body: `{ "projectId"?: string, "testIds"?: string[], "coverage"?: boolean }`  (no testIds = run all;
                                    `coverage` overrides the global coverage toggle for this run only, see "Per-test coverage")
  - `POST /api/cancel`
  - `POST /api/watch`               body: `{ "enabled": boolean }`
  - `POST /api/restart-host`        body: `{ "projectId"?: string }`  (see "Warm test hosts")
  - `POST /api/impact-map`          body: `{ "projectId"?: string }`  (see "Smart watch")
- `GET  /api/coverage/test?projectId=<id>&id=<testId>[&id=…]` -> cached per-test `CoverageReport` or 404 (see "Cached per-test coverage")

All JSON is camelCase. Timestamps are ISO-8601 strings.

## Types (TypeScript notation)

```ts
type Outcome = "notRun" | "queued" | "running" | "passed" | "failed" | "skipped" | "timedOut" | "cancelled";

interface TestNode {
  id: string;              // MTP node uid (unique per project)
  projectId: string;
  displayName: string;     // e.g. "Multiply_ReturnsProduct (2,3,6)"
  namespace?: string;      // "SampleTests.Math"
  className?: string;      // "CalculatorTests"
  methodName?: string;     // "Multiply_ReturnsProduct"
  filePath?: string;       // absolute source path if known
  line?: number;
  lineEnd?: number;        // last line of the test method, when MTP reports location.line-end
  traits: { key: string; value: string }[];   // e.g. TestCategory
  outcome: Outcome;
  durationMs?: number;
  errorMessage?: string;
  errorType?: string;
  stackTrace?: string;
  stdOut?: string;
  stdErr?: string;
  lastRunAt?: string;
  traceId?: string;        // W3C trace id (32 hex) of the test's span, reported by Labcoat.Testing through a
                           // "labcoat:traceId=<hex>" stdOut line (stripped from stdOut). With aspireDashboardUrl:
                           // trace = {aspireDashboardUrl}/traces/detail/{traceId}, logs = {aspireDashboardUrl}/structuredlogs?traceId={traceId}
  history?: { outcome: Outcome; durationMs?: number; at: string; runId?: string }[];
                           // recent results, most recent last (max 20), persisted across host restarts (see "History")
  flaky?: boolean;         // passed and failed both seen in the last 10 runs (≥ 2 transitions, see "History")
  stale?: boolean;         // result loaded from a previous session, not produced by this host instance
}

interface TestProject {
  id: string;              // stable id, e.g. project file name without extension (a runner package's project: its folder name)
  name: string;            // "SampleTests"
  path: string;            // absolute .csproj path (a runner package's project: its package.json or config file)
  status: "idle" | "building" | "discovering" | "running" | "error";
  lastError?: string;
  buildOutput?: string;    // tail of last build output when it failed
  runner?: string;         // the runner package that runs the project, e.g. "vitest" (runners.md); absent = a .NET test project
}

interface RunSummary {
  runId: string;
  startedAt: string;
  finishedAt?: string;
  total: number; passed: number; failed: number; skipped: number;
  durationMs?: number;
}

interface ExplorerState {
  projects: TestProject[];
  tests: TestNode[];
  currentRun?: RunSummary;
  lastRun?: RunSummary;
  watch: boolean;
  aspireDashboardUrl?: string;   // set when hosted under Aspire (env ASPIRE_DASHBOARD_URL)
  logs?: { projectId?: string; level: "info" | "warning" | "error"; message: string; timestamp: string }[];
                                 // recent log history (last 2000); ONLY in the on-connect snapshot and GET /api/state,
                                 // omitted from broadcast `state` events so clients keep their own (possibly cleared) panel
}

type ServerEvent =
  | { type: "state"; state: ExplorerState }                       // full snapshot (sent on connect and after discovery)
  | { type: "projectStatus"; project: TestProject }
  | { type: "testsUpdated"; tests: TestNode[] }                   // partial: upsert by (projectId,id)
  | { type: "runStarted"; run: RunSummary; testIds: string[] }
  | { type: "runFinished"; run: RunSummary }
  | { type: "log"; projectId?: string; level: "info" | "warning" | "error"; message: string; timestamp: string };

type ClientCommand =
  | { type: "discover"; projectId?: string }
  | { type: "run"; projectId?: string; testIds?: string[]; coverage?: boolean }
  | { type: "cancel" }
  | { type: "setWatch"; enabled: boolean };
```

## Dev setup
- Host listens on `http://localhost:4300` by default (`--port` to change). Vite dev server (port 5174) proxies `/api` and `/ws` to it.
- `npm run build` in `ui/` outputs to `src/Labcoat/wwwroot`.

## Coverage (added)

Uses MTP's code coverage extension (Microsoft.Testing.Extensions.CodeCoverage, included in the MSTest metapackage):
the host appends `--coverage --coverage-output-format cobertura --coverage-output <tmpdir>/<project>-<runId>.cobertura.xml
--coverage-settings <file>` to the test-host command line for **run** operations when coverage is enabled, then parses
the Cobertura XML. `<file>` is the project's own settings file (Coverage.Settings, `--coverage-settings` in
`TestingPlatformCommandLineArguments`, or `coverage.settings.xml` / `CodeCoverage.xml` / `*.coverage.xml` / `*.runsettings`
found by name), or settings Labcoat writes for its defaults (docs/features.md "Code coverage"); with a `testconfig.json`
`codeCoverage` section nothing is passed. Where the extension has a CLR profiler for the platform, static instrumentation
is turned off in that file (results are the same; no assembly is rewritten in place).
Projects without the extension (no `--coverage` in `--help`) -> skip coverage for that project and emit a `log` warning.
With a settings file the report holds exactly what it collected (minus `Coverage.Exclude`); with Labcoat's defaults,
Labcoat's own assemblies, test projects, test frameworks, generated code and `obj/` are left out. There are two reports:
`coverage` is the latest coverage run's own report (a single-test run gives that test's coverage), and `fullCoverage` is
the full report the Coverage page shows, which a run of selected tests never replaces (see "The full report" below).

```ts
interface CoverageLineHit { line: number; hits: number; branchesCovered?: number; branchesTotal?: number }
interface FileCoverage {
  path: string;            // absolute source path
  relativePath: string;    // relative to the common root of all covered files, for display
  module: string;          // assembly name, e.g. "AspireSample.Api"
  linesCovered: number; linesValid: number; lineRate: number;          // 0..1
  branchesCovered: number; branchesValid: number; branchRate: number;  // 0..1
  lines: CoverageLineHit[];
  stale?: boolean;         // full report only: the file changed since its coverage was collected
}
interface CoverageReport {
  runId: string;
  generatedAt: string;
  projectIds: string[];
  lineRate: number; branchRate: number;
  linesCovered: number; linesValid: number; branchesCovered: number; branchesValid: number;
  files: FileCoverage[];
  scope: "all" | "subset";  // "subset" when the run had explicit testIds (see "Per-test coverage")
  testIds?: string[];       // the test ids that ran, only for scope "subset"
  stale?: boolean;          // only on GET /api/coverage/test: the test app was rebuilt since (see "Cached per-test coverage")
  settings?: CoverageSettingsInfo[]; // per covered project: which coverage settings applied
  merged?: CoverageMergedRun[];      // full report only: subset runs merged in since the full run(s)
  staleFiles?: string[];             // full report only: files (absolute) changed since their coverage was collected
  restoredAt?: string;               // full report only: restored at startup; the latest restored full run's time
}
interface CoverageMergedRun { runId: string; generatedAt: string; projectId: string; testCount: number; filesChanged: number }
interface CoverageSettingsInfo {
  projectId: string;
  source: "file" | "testconfig" | "defaults" | "runner"; // runner: a runner package's project, the runner's own settings
  path?: string;                                  // the settings file (absent for "defaults"; "runner": its config file)
  origin?: "option" | "msbuild" | "convention" | string; // Coverage.Settings / TestingPlatformCommandLineArguments / found by name; "runner": its name ("Vitest")
  instrumentation: "dynamic" | "static" | "runner"; // static = assemblies of the output folder instrumented in place; runner = the runner's own
  modules?: string[];                             // "defaults": the assemblies counted (referenced projects)
  include?: string[]; exclude?: string[];         // Coverage.Include / Coverage.Exclude in effect
}

// ExplorerState gains:
//   coverageEnabled: boolean;
//   coverage?: CoverageReport;       // the latest coverage run's own report (any scope)
//   fullCoverage?: CoverageReport;   // the full report (Coverage page), scope "all"; see "The full report"
// ServerEvent gains:
//   | { type: "coverageUpdated"; coverage: CoverageReport }       // after every coverage run: that run's own report
//   | { type: "fullCoverageUpdated"; coverage: CoverageReport }   // the full report changed (sent before coverageUpdated)
// ClientCommand gains:
//   | { type: "setCoverage"; enabled: boolean }
// REST:
//   POST /api/coverage   body: { "enabled": boolean }
//   GET  /api/coverage[?scope=full|last]  -> the full report (default) or the latest coverage run's own report;
//                                            404 when there is none yet, 400 for another scope
//   GET  /api/source?path=<abs path>  -> text/plain file contents; ONLY for files that appear in the current
//                                       coverage report or test locations (403 otherwise).
```

### The full report (added)
What the Coverage page shows: overall coverage, like other tools' coverage reports. The host keeps it per project
(FullCoverage.cs):
- A run of **every test of a project** (run all, or all of one project; with coverage) replaces that project's part.
  The report is all projects' parts combined (hits summed, as in one run of several projects); `projectIds` lists them,
  `runId` is the run that last changed it, `generatedAt` the latest full run, `scope` is always `"all"`.
- A run of **selected tests** (a watch rerun, per-test coverage, a selection) never replaces it. Per file, its
  coverage is merged in only where that is sound: a file whose content (SHA-256) is unchanged since the project's full
  run takes the union (a line is covered when either covers it; hits and branch counts take the max, so nothing is
  counted twice). The subset ran the current code and the line numbers still match, so the union only adds lines that
  really run now, e.g. after a test edit. A module no test of the full run loaded is added. A changed or deleted file,
  or a file added to a module since the full run, is not merged: its lines no longer match and a subset cannot tell
  what the other tests cover now. Runs that added lines are listed in `merged`.
- **Stale files:** after every run (with or without coverage) the host re-checks the report's files (length + write
  time, then content). Changed files get `stale: true` and are listed in `staleFiles` (plus files added to a module,
  found by a subset run); their numbers stay those of the full run. Only a staleness change sends
  `fullCoverageUpdated` with the same `runId`. The next full run of the project clears them.
- A subset run of a project without a full run yet changes nothing (the log says "no full report yet").
- A run's own report has `scope: "subset"` as soon as any of its projects ran selected tests (`testIds` then lists
  every test of the run).
- **Persisted** (unlike `coverage`, which a host restart starts without): see "Persisting the full report" below.

### Persisting the full report (added)
The host saves the full report to `coverage.json` next to `history.json` (FullCoverageStore.cs) and restores it at
startup, so the Coverage page shows it before anything runs.
- Per project it stores its part of the report (module, line numbers, hits and branches per line, in columns: line
  numbers delta-encoded, `-1` = no branch info), the stamp of every file at the full run (length, write time, SHA-256),
  the time of the full run (`generatedAt`), its run id and coverage settings, and files added to its modules since;
  plus the report's `runId` and `merged` runs. `version: 1`.
- Saved debounced (2 s) when the report itself changes: a full run, or a subset run that added covered lines (or
  found a file added to a module). Not on a subset run that changed nothing, and not on staleness changes alone (they
  are re-checked at startup). Streamed to a temp file that then replaces the old one; on shutdown a pending save is
  written. A report over 128 MB is not saved (warning).
- On load, files changed since their stamp (the same check as after a run) are stale at once: `staleFiles` and
  `files[].stale`. A missing file means no report (as before); a corrupt one, one of another version or one over the
  size cap is ignored with a `log` warning.
- `restoredAt` (full report only) is set while part of the report comes from before the restart: the latest restored
  full run's time. It equals `generatedAt` when the whole report is restored, and disappears once every restored
  project has had a new full run.

```ts
interface CoverageReport { /* … */ restoredAt?: string }   // full report only: restored at startup (see above)
```

## Source access (updated)
`GET /api/source?path=<abs>` serves: test location files, coverage report files (the latest run's and the full one), and any file under a discovered
project directory or the common root of the CLI input paths (so stack-trace frames into product code resolve).
Paths are canonicalized; outside those roots -> 403, missing -> 404, max ~2 MB. Used by the UI's inline code view.

## Host implementation notes (added by the backend)
- REST command endpoints are fire-and-forget: `POST /api/discover|run|cancel` return `202 { "accepted": true }`
  immediately; progress arrives over `/ws`. `POST /api/watch` returns `{ "watch": bool }`, `POST /api/coverage`
  returns `{ "coverageEnabled": bool }`. Operations are serialized: a run requested while another runs is queued.
- `/api/source` extra statuses: 413 when the file is over 2 MB, 415 for binary files (NUL bytes). "Discovered project
  directories" also include the directories of each test project's direct `ProjectReference`s.
- Traits: MSTest `[TestCategory("X")]` arrives from MTP as `{ "X": "" }` and is mapped to `{ key: "Category", value: "X" }`;
  key/value traits (`[TestProperty("k","v")]`) map 1:1.
- `errorType` is best effort (MTP does not report exception types): parsed from "threw exception: T" in the message, or
  `…AssertFailedException` (MSTest) / `Xunit.Sdk.XunitException` / `NUnit.Framework.AssertionException` when the top
  stack frame is an Assert; `"Timeout"` for timed-out tests.
- `lineEnd`: MSTest 4 reports `location.line-end == location.line-start` (both the line of the `[TestMethod]` attribute), so it
  is not the end of the method body.
- `RunSummary.failed` counts failed + timed-out tests; `RunSummary.total` is the number of requested tests (or more if
  the run reported tests that were not known from discovery).

## Per-test coverage (added)
MTP's Cobertura output has no per-test attribution, so "coverage for one test" = run just that test with coverage.
- `run` (WS command and `POST /api/run`) takes optional `coverage?: boolean`. When set it overrides the global
  `coverageEnabled` for that run only; the global toggle is not changed (no `state` change is broadcast).
  Omitted -> the global toggle decides, as before.
- `CoverageReport.scope` is `"subset"` when the run had explicit `testIds` (`testIds` then lists the ids that actually
  ran, in tree order), `"all"` otherwise (whole project(s)). Old clients can ignore both fields; a missing `scope`
  means `"all"`.
- The UI (double-click a tree node while the Coverage tab is active, or the shield hover action) sends
  `{ type: "run", testIds, coverage: true }`, shows the subset report with a scope banner, and switches back to
  `fullCoverage` without re-running. The Coverage page always shows `fullCoverage`, with the latest subset run's own
  numbers as a note.

### Cached per-test coverage (added)
Every coverage run with explicit `testIds` is also cached by the host, per project, keyed by
`(projectId, sorted testIds)` together with a build stamp (the content fingerprint of the test app's output directory,
the same one the warm host uses to detect rebuilds). Single-test entries are indexed by `(projectId, testId)`.
In memory only (lost on restart), LRU-bounded (256 entries). Run-all coverage and the class-granular impact-map pass
are not cached here.

```
GET /api/coverage/test?projectId=<id>&id=<testId>[&id=<testId>…]
  200 -> CoverageReport (scope "subset", testIds = the ids of that run, in tree order, projectIds = [projectId]);
         `stale: true` when the test app's output changed since it was collected
  404 -> nothing cached for exactly that id set (projectId omitted = search every project)
  400 -> no id
```
Never starts a run. The UI uses it when a tree node is selected while the Coverage tab is active (debounced for
keyboard navigation, stale requests aborted, memoized client-side): a hit is shown like a double-click result
(scope banner, touched files, most covered file opened; `stale` adds a "Code changed since — Recollect" chip); a miss
on a single test shows "No coverage collected for <test> yet" with a "Collect coverage" button (= double-click); a miss
on a class/namespace, a project node, or no selection shows the full report. A run without `testIds` ("Run all")
returns the view to the full report.

## History & flaky detection (added)
The host persists, per test, the last 20 results (`outcome`, `durationMs`, `at`, `runId`) plus the last full result
(outcome, duration, error, stack, output) to `<LocalApplicationData>/labcoat/<sha256(project paths)[..16]>/history.json`
(e.g. `~/.local/share/labcoat/…` on Linux; override the base dir with env `LABCOAT_HISTORY_DIR`). Written debounced
(~1 s) after each run and on shutdown, streamed to a temp file that then replaces the old one. The file layout is
internal, not part of this contract: version 2 stores the history and past runs in columns with project ids, test ids
and run ids written once (version 1 files are still read and rewritten as version 2).
- On startup, tests found by discovery that have no result in memory get the stored result with `stale: true`, plus
  `history`/`flaky`. A new result for the test clears `stale`.
- After `runFinished`'s run, the host appends one entry per reported result (not cancelled/not-run) and publishes
  `testsUpdated` with the new `history`/`flaky` (sent just before `runFinished`).
- `flaky`: both `passed` and `failed`/`timedOut` appear in the last 10 history entries **with at least two pass/fail
  transitions** (so a plain regression or fix — one transition — is not flaky). Omitted when false.

## Warm test hosts (added)
The test host process of a project is kept alive between requests ("warm"): discovery and every non-coverage run are
sent as further `testing/discoverTests` / `testing/runTests` requests over the same MTP server connection, so reruns
skip process start-up and, with a warm-mode friendly fixture, expensive fixtures (e.g. an Aspire app). The process gets
`LABCOAT_WARM_HOST=1` in its environment.

```ts
interface TestProject {
  // ...
  hostState?: "cold" | "starting" | "warm";   // missing = "cold"
}
// ClientCommand gains:
//   | { type: "restartHost"; projectId?: string }   // stop + start a fresh test host (all projects when omitted)
// REST: POST /api/restart-host  body: { "projectId"?: string }  -> 202
```
- Changes are pushed with `projectStatus` events.
- The warm host is replaced when the output directory's content changed (test dll or copied references; a rebuild that
  writes identical bytes keeps it), when an assembly it has loaded is rewritten in place with other content (watched
  with a FileSystemWatcher: replaced right away when idle, else after the operation), when tests on it fail with
  `BadImageFormatException` / 0x80131124, when it exits, or when a cancel had to kill it. Coverage runs (and the
  impact-map pass) always use a separate one-off process (the coverage extension needs the controller launch, attaches
  its profiler at process start and writes the report at process exit); the warm host stays, except where the coverage
  run instrumented assemblies in place under it (Linux / macOS static instrumentation): then a fresh warm host is
  started right after the coverage run.
- Apps whose launch is always a test host controller (an out-of-process extension enabled by default) cannot be kept
  warm; they fall back to one process per request (hostState stays `"cold"`).
- Explorer shutdown (incl. Ctrl+C / SIGTERM) sends `exit` to every warm host and waits up to ~30 s before killing it,
  so fixtures can dispose (an Aspire app needs ~12 s). If the explorer dies hard, the test host exits as soon as its
  connection drops.

## Smart watch (added)
Watch runs only the affected tests. After the debounced rebuild + rediscovery, per changed file:
1. a test file (any `TestNode.filePath`) -> the tests located in it;
2. a source file known to the impact map -> the tests whose executed methods overlap the changed lines (diff against
   a snapshot of the file taken when coverage was recorded); a change outside every executed method -> all tests that
   touch the file;
3. anything else -> all tests of the project.
Watched directories are the project directory plus its transitive ProjectReference closure.

The impact map is filled from coverage runs with explicit `testIds` (per-test coverage, scope `"subset"`) and by an
optional pass (`buildImpactMap`; progress in the log). It lives in memory (lost on restart). Granularity: methods
covered per test. Cobertura has no per-test attribution, so data recorded from a coverage process that ran several
tests is **class-level** (every test of that process is credited with everything it covered); data from a process that
ran one test is **per-test**. A shared recording never replaces a test's per-test data, except a new pass.

The server option **`impactMap`** (settings file key, `--impact-map`, env `LABCOAT_IMPACT_MAP`, Aspire `ImpactMap` /
`WithImpactMap`; precedence CLI > env > workspace file > user file) chooses the pass:
- `"class"`: one coverage process per test class.
- `"test"`: one coverage process per test.
- `"auto"` (default): per class; then, whenever a plan (`GET /api/affected`, run affected) or a watch run selects tests
  through class-level data, the classes of those tests (with 2+ tests) are queued for per-test refinement in the
  background (once per class until the next pass; it takes the operation lock like a run, sets `impactMap.building`
  while it works and logs `Impact map: refining …` / `… refined to per-test data`).
With dynamic instrumentation (the coverage extension's profiler, CodeCoverage ≥ 18.11 off Windows) up to
min(6, cores/2) coverage processes run at once; with static instrumentation one at a time (it rewrites the output
folder), with the warm host stopped for the whole pass.

```ts
interface ImpactMapInfo {
  files: number; tests: number;
  perTest: number;                  // tests with per-test data (the others are class-level)
  mode?: "class" | "test" | "auto"; // the impactMap option
  builtAt?: string; building?: boolean;   // building: a pass or a refinement is running
}
interface AffectedRun {
  strategy: "testFile" | "impactMap" | "mixed" | "all";   // "all" = fallback (unknown file)
  changedFiles: string[];                                // absolute paths
}
// ExplorerState gains:  impactMap?: ImpactMapInfo        (absent until something was recorded or a pass started)
// RunSummary gains:     affected?: AffectedRun           (only on watch-triggered runs)
// ClientCommand gains:  | { type: "buildImpactMap"; projectId?: string }
// REST:                 POST /api/impact-map  body: { "projectId"?: string }  -> 202
```


## Past runs / run switcher (added)
The host also persists the last 50 runs in `history.json` (next to the per-test history): the `RunSummary` plus, per
test that reported a result, `{ projectId, id, outcome, durationMs, errorMessage?, stackTrace? }` (error ≤ 2 KB and
stack ≤ 4 KB, only for failed/timed-out tests). Runs without any result (e.g. cancelled before the first result) are
not stored.

```ts
interface RunSummary {
  // … as above, plus:
  scope?: "all" | "subset";            // subset = explicit testIds (total = number of selected tests)
  trigger?: "user" | "watch" | "api";  // best effort: UI command, watch mode, REST (e.g. Aspire dashboard command)
}
interface RunResult { projectId: string; id: string; outcome: Outcome; durationMs?: number; errorMessage?: string; stackTrace?: string }
interface RunDetails { run: RunSummary; results: RunResult[] }

interface ExplorerState { /* … */ runs?: RunSummary[] }   // persisted runs, newest first
type ServerEvent = /* … */ | { type: "runsUpdated"; runs: RunSummary[] }   // after a run was recorded, just before runFinished
```
- `GET /api/runs` → `RunSummary[]` (newest first).
- `GET /api/runs/{runId}` → `RunDetails`, 404 when unknown (or pruned).

The UI's header run switcher ("Live" pill) lists these runs; picking one shows a read-only snapshot of the Tests page
(outcomes/durations/errors from that run). Starting a new run returns to Live.

## Round 3 additions
All additions are optional fields / new message types: old clients keep working. JSON stays camelCase, `null` fields are
omitted. Copy the shapes below into `ui/src/contract.ts`.

### Types

```ts
// ---- ExplorerState gains
interface ExplorerState {
  // … everything above, plus:
  tracingEnabled: boolean;      // per-test trace collection (test hosts get the Aspire dashboard's OTLP endpoint). Default false.
  tracingAvailable: boolean;    // an OTLP endpoint is known (Labcoat is hosted by an Aspire AppHost). When false, the toggle has no effect.
  tracingFilter?: string;       // the host's trace filter (--trace-filter / LABCOAT_TRACE_FILTER / traceFilter): while tracing is on only
                                // matching tests are traced (test hosts get LABCOAT_OTLP_FILTER). Omitted when every test is traced.
  telemetryAvailable?: boolean; // true when the Aspire dashboard's telemetry API is known (GET /api/tests/telemetry can work
                                // once tracingEnabled is on). Omitted when not hosted by Aspire.
  workspaceName?: string;       // display name: env LABCOAT_NAME (Labcoat.Aspire.Hosting sets the resource name, e.g. "tests-failing"),
                                // else the solution name / single test project name / input directory name
  sourceRoots?: string[];       // canonical absolute directories GET /api/source serves (CLI root(s), --root dirs, project dirs and
                                // their ProjectReference closure; nested roots folded). A stack frame whose file is under one of
                                // these is "my code". Paths outside (e.g. "/_/src/…" from NuGet packages) are not served.
  quarantined?: string[];       // quarantined tests as "projectId|id" keys (persisted per workspace)
  editors?: EditorInfo[];       // exactly 3 entries, in this order: vscode, rider, visualstudio
}
interface EditorInfo { id: "vscode" | "rider" | "visualstudio"; name: string; available: boolean }
                                // name: "Visual Studio Code" | "JetBrains Rider" | "Visual Studio"; available = found at startup
                                // (visualstudio is never available off Windows)

// ---- TestProject gains
interface TestProject {
  // … as above, plus:
  buildErrors?: BuildDiagnostic[];  // set when the last build FAILED: parsed MSBuild diagnostics, deduplicated, errors first then
                                    // warnings (each in order of appearance), max 200. Cleared by the next build start.
  buildErrorCount?: number;         // distinct errors / warnings of that failed build (before the 200 cap)
  buildWarningCount?: number;
  resultsOutdated?: boolean;        // true when a build failed while the project's tests had results: those results are from older
                                    // code (dim them / "outdated"). Cleared by the next successful build.
  diagnostics?: ProjectDiagnostic[];// set when discovery found 0 tests or the test host could not start/answer; cleared when a
                                    // discovery finds tests
  testPlatform?: "vstest" | "none"; // the project isn't on Microsoft.Testing.Platform: a VSTest test project, or (restored) no test
                                    // framework at all. Labcoat never builds or starts it; diagnostics[0] has the switch for its
                                    // framework (link: docs/troubleshooting.md#my-project-uses-vstest). Absent: MTP / not known yet
  // lastError keeps the first error as one line ("<file>(<line>,<col>): error <code>: <message>"); buildOutput keeps the tail
}
interface BuildDiagnostic {
  file?: string;        // absolute path as MSBuild printed it; absent for tool origins (CSC, MSBUILD)
  line?: number; column?: number;
  code?: string;        // "CS1002", "NU1603", "MSB1009", "MSTEST0032" …
  severity: "error" | "warning";
  message: string;
  project?: string;     // the "[…csproj]" suffix (without "::TargetFramework=…")
}
interface ProjectDiagnostic {
  level: "info" | "warning" | "error";
  message: string;      // e.g. "The test host exited with code 0 before it answered.",
                        //      "OutputType is 'Library', but Microsoft.Testing.Platform test projects are executables.",
                        //      "No Microsoft.Testing.Platform runner is enabled for this project.", "No tests discovered."
  hint?: string;        // what to do, e.g. "Add <OutputType>Exe</OutputType> …", "set <EnableMSTestRunner>true</EnableMSTestRunner> …"
  details?: string;     // extra text: the last ≤ 20 stderr lines of a test host that exited ("\n"-separated)
  link?: string;        // a page that explains the fix (the VSTest message links docs/troubleshooting.md#my-project-uses-vstest)
}

// ---- RunSummary gains
interface RunSummary {
  // … as above, plus:
  watch?: WatchContext;     // watch-triggered runs only (alongside `affected`, which stays)
  repeat?: RepeatSummary;   // runs started with repeat ≥ 2
  quarantined?: number;     // results of quarantined tests in this run (NOT included in passed/failed/skipped; total still counts them)
}
interface WatchContext {
  changedFiles: string[];   // absolute paths that triggered the rebuild
  buildMs?: number;         // rebuild + rediscovery time before the run started
  strategy: "testFile" | "impactMap" | "mixed" | "all";
}
interface RepeatSummary {
  count: number;            // requested repetitions (2–50)
  completed: number;        // fully completed repetitions (< count when cancelled)
  perTest: { projectId: string; id: string; passed: number; failed: number; skipped: number; durationsMs: number[] }[];
                            // one entry per test that reported a result, in tree order; failed includes timed out
}

// ---- TestNode.history entries gain
//   { outcome; durationMs?; at; runId?; flaky?: boolean }   // flaky: aggregated entry of a repeat run whose repetitions both passed and failed

// ---- ClientCommand gains
type ClientCommand =
  /* … */
  | { type: "run"; projectId?: string; testIds?: string[]; coverage?: boolean; repeat?: number }   // repeat: 2–50 (values > 50 are clamped)
  | { type: "setTracing"; enabled: boolean }
  | { type: "setQuarantine"; projectId: string; id: string; quarantined: boolean };

// ---- GET /api/coverage/line response
interface CoveringTests {
  tests: {
    projectId: string; id: string;
    source: "perTest" | "impactMap";  // perTest = exact (line hits in that test's cached single-test coverage);
                                      // impactMap = approximate (the test executed a method whose span contains the line;
                                      // class granular for the "build impact map" pass)
    hits?: number;                    // perTest only
    stale?: boolean;                  // perTest only: the test app was rebuilt since that coverage was collected
  }[];                                // perTest first, then impactMap; each in tree order
  partial: boolean;                   // true when not every test has single-test coverage cached → the list may be incomplete
}

// ---- GET /api/tests/telemetry response
interface TestTelemetry {
  traceId: string;
  spans: TelemetrySpan[];   // spans of the trace with status error OR an HTTP status ≥ 400 (a 4xx server span is not an
                            // OpenTelemetry error, but it is usually what a failing test hit); ordered by start time
  logs: TelemetryLog[];     // warning / error / critical logs with this trace id; ordered by time
}
interface TelemetrySpan {
  name: string;             // e.g. "POST /orders/"
  resource: string;         // service.name, e.g. "api" (the test's own span has the test assembly name)
  status: "error" | "ok" | "unset";
  statusMessage?: string;
  httpStatusCode?: number;  // http.response.status_code (or http.status_code)
  durationMs?: number;
  startTime?: string;       // ISO-8601
  spanId?: string;
  exceptionType?: string; exceptionMessage?: string;  // from the span's "exception" event
  attributes?: Record<string, string>;                 // max 40, values ≤ 2000 chars
}
interface TelemetryLog {
  resource: string;
  level: "warning" | "error" | "critical";
  message: string;
  timestamp: string;        // ISO-8601
  spanId?: string;
  category?: string;        // logger category (OTLP scope name), e.g. "Program"
  attributes?: Record<string, string>;
}
```

### REST

| Endpoint | Body / query | Response |
| --- | --- | --- |
| `POST /api/run` | `{ projectId?, testIds?, coverage?, repeat? }` | `202 { accepted: true }` |
| `POST /api/tracing` | `{ enabled: boolean }` | `200 { tracingEnabled: boolean }` |
| `POST /api/quarantine` | `{ projectId, id, quarantined?: boolean }` (default `true`) | `200 { quarantined: string[] }`, `400` without projectId/id |
| `GET /api/coverage/line` | `?path=<abs>&line=<n>` | `200 CoveringTests`, `400` without path / line ≥ 1. Never starts a run. |
| `GET /api/tests/telemetry` | `?projectId=<id>&id=<testId>` (or `?traceId=<hex>`) | `200 TestTelemetry`; `409` tracing off; `503` not hosted by Aspire; `404` unknown test / test without trace id; `502` dashboard error (body `{ error }`) |
| `POST /api/open-in-editor` | `{ editor: "vscode" \| "rider" \| "visualstudio", path, line?, column? }` | `200 { ok: boolean, message? }`; `400` unknown editor; `403` path outside the served roots (same rule as `/api/source`); `404` missing file |

### Behaviour

- **Trace collection is opt-in (off by default).** Sources, last one wins: CLI `--traces` / `WithTestTracing()` (initial
  value) < the runtime choice (`setTracing` / `POST /api/tracing` / the "Toggle test tracing" dashboard command), which is
  persisted per workspace in `settings.json` next to `history.json`. Only while it is on do test hosts get
  `LABCOAT_OTLP_ENDPOINT/PROTOCOL/HEADERS` (and `LABCOAT_OTLP_FILTER` with a trace filter); `OTEL_*` is never passed on. Toggling broadcasts `state`; warm hosts are
  replaced lazily on their next request (log: "Stopping the warm test host: trace collection was turned on.").
  `traceId` only appears on tests while tracing is on and the tests use Labcoat.Testing.
- **Run lifecycle changed (P1-8):** `run` now builds (incremental) and rediscovers every selected project FIRST; only
  then does the host send `runStarted` and mark exactly the tests that will run as `queued`. While building, clients
  only see `projectStatus` (`building` / `discovering`); tests keep their previous outcome. Projects whose build fails
  are left out (their tests are not touched and get `resultsOutdated`); if nothing can run, no run starts at all (no
  `runStarted`/`runFinished`; a `log` warning "Nothing to run: the build or discovery failed."). Consequently
  `RunSummary.startedAt`/`durationMs` exclude the build (for watch runs see `watch.buildMs`). Tests outside the run are
  never reset.
- **Repeat runs (F5):** `repeat` 2–50 runs the selected tests that many times in a row on the warm host (no coverage —
  a coverage request is ignored with a log line). Each repetition re-queues the tests and streams results as usual
  (log "Repeat i/N"). At the end every test shows its aggregated result: `failed` (with the error/stack/output of its
  last failing repetition) if any repetition failed, else the last outcome; `durationMs` = mean. `RunSummary` counts
  use the aggregated outcomes; `repeat.perTest` has the per-test tallies. History gets ONE entry per test (aggregated
  outcome) with `flaky: true` when it both passed and failed, which makes the test `flaky`.
- **Quarantine (F5):** quarantined tests still run and keep their outcomes, but their results count in
  `RunSummary.quarantined` instead of passed/failed/skipped. Persisted per workspace (`settings.json`); changes broadcast
  `state`. Keys of tests that no longer exist are kept.
- **Coalesced test updates (P2-13):** `testsUpdated` is buffered and flushed at most every ~50 ms, merged per
  (projectId, id) with the last write winning, in order of first appearance. Any other ordered event (`runStarted`,
  `runFinished`, `runsUpdated`, `projectStatus`, `coverageUpdated`) flushes pending updates first; a `state` snapshot
  supersedes them; `log` events are not ordered relative to test updates. (Measured: a 14-test run → 5 `testsUpdated`.)
- **Logging (P1-5):** test failures are never logged at `error`; after a run with failures there is one `info` line
  "Failed (n): A, B, … (+k more)" (quarantined ones marked). `error` is reserved for build / host / process problems.
- **Discovery diagnostics (P2-3):** project properties (`OutputType`, `IsTestingPlatformApplication`,
  `EnableMSTestRunner`, `EnableNUnitRunner`, `UseMicrosoftTestingPlatformRunner`, `UsingMSTestSdk`, PackageReferences)
  come from MSBuild evaluation; checks: host exit code + stderr tail, timeout, OutputType not Exe, no MTP runner
  (MSTest.Sdk / EnableMSTestRunner / EnableNUnitRunner / xunit.v3 / TUnit / Microsoft.Testing.Platform), no tests found.
- **VSTest projects:** test projects on VSTest (Microsoft.NET.Test.Sdk with MSTest.TestAdapter / NUnit3TestAdapter /
  xunit.runner.visualstudio and no MTP switch, MSTest.Sdk with UseVSTest) are listed but never built or started
  (TestPlatform.cs, Explorer.Platform.cs): before a build from the project and Directory.* files (confirmed by an MSBuild
  evaluation, no restore needed), after a build from the restored evaluation. The project gets `testPlatform`, one
  `warning` log line (the first for the project) and `diagnostics` with the switch for its framework.
- **Open in editor (F8):** launched without a shell (argument list), via `setsid` on Linux so Ctrl+C on Labcoat does not
  close the editor. VS Code: `code --goto <path>:<line>[:<col>]` (`code`/`code-insiders` on PATH, macOS app bundle CLI,
  `Code.exe` on Windows). Rider: `rider --line <n> [--column <c>] <path>` (`rider`/`rider.sh` on PATH, JetBrains Toolbox
  scripts/apps, macOS app bundle, `rider64.exe` on Windows). Visual Studio (Windows only): `devenv.exe /Edit <path>`
  located with vswhere; going to the line (`/Command "Edit.GoTo <n>"`) is best effort; elsewhere
  `{ ok: false, message: "Visual Studio is only available on Windows" }`. `ok: true` means the launcher started (and did
  not exit with an error within ~3 s), not that the editor shows the file.
- **Aspire failure context (F3):** reads the Aspire 13.6 dashboard's telemetry HTTP API
  (`{dashboard}/api/telemetry/spans?traceId=` and `/logs?traceId=&severity=Warning`, header `x-api-key`).
  Labcoat.Aspire.Hosting passes `LABCOAT_DASHBOARD_API_URL` (the dashboard URL, preferring http) and, when the dashboard
  API is secured, `LABCOAT_DASHBOARD_API_KEY` (the AppHost's `AppHost:DashboardApiKey`). Spans/logs arrive in the
  dashboard with the exporters' batch delay (~1–5 s after the test), so a fetch right after `runFinished` can be
  incomplete — refetch when the user opens the failure, or after a few seconds.
- **Workspace name (P2-1):** Labcoat.Aspire.Hosting sets `LABCOAT_NAME` to the resource name.

## Round 4 additions
Backend hardening. Everything below is additive for the current UI (new optional fields, new event types it ignores,
opt-in lean mode); the only behaviour changes for existing calls are the security checks and the stricter input
validation listed under "Breaking-ish". Copy the shapes into `ui/src/contract.ts`.

### Security (see `SECURITY.md`)
- Every request: the `Host` header must be `localhost`, `*.localhost`, a loopback IP (`127.0.0.1`, `[::1]`), the listen
  host, or listed in env `LABCOAT_ALLOWED_HOSTS` (comma-separated; `*` disables the check) → else **400** `{ error }`.
- `/ws` and every non-GET `/api/*`: `Origin` must be the same origin (Origin host:port == Host header) or listed in env
  `LABCOAT_ALLOWED_ORIGINS` → else **403** `{ error }`. Requests without `Origin` (curl, scripts, Aspire commands) are
  accepted over loopback only, not with a cross-site `Sec-Fetch-Site`, and — when env `LABCOAT_TOKEN` is set (Aspire
  hosting sets it) — only with header `X-Labcoat-Token: <token>`.
- POST/PUT/PATCH `/api/*` with a body: `Content-Type: application/json` (or `application/*+json`) → else **415**.
- Response headers: `Content-Security-Policy` (`default-src 'self'`; `script-src 'self'` + sha256 hashes of
  `index.html`'s inline scripts, computed at runtime; `style-src 'self' 'unsafe-inline'`; `img-src 'self' data: blob:`;
  `font-src 'self' data:`; `connect-src 'self' ws://<host> wss://<host>`; `worker-src 'self' blob:`; `object-src 'none'`;
  `base-uri 'none'`; `form-action 'self'`; `frame-ancestors 'none'`), `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Cross-Origin-Opener-Policy` / `-Resource-Policy: same-origin`.
- `index.html` (and the SPA fallback) is served with `Cache-Control: no-cache`; `/assets/*` with
  `public, max-age=31536000, immutable`; other static files `no-cache`.
- **UI dev server:** Vite (`:5174`) proxies with `changeOrigin`, so the browser's `Origin: http://localhost:5174` no
  longer matches. Either start the backend with `LABCOAT_ALLOWED_ORIGINS=http://localhost:5174`, or (better) let the
  Vite proxy rewrite `Origin` to the backend origin (`configure: (proxy) => { proxy.on("proxyReq", r => r.setHeader("origin", backend)); proxy.on("proxyReqWs", r => r.setHeader("origin", backend)); }`).
  New UI code must keep inline `<script>`s out of `index.html` or rely on the runtime hash (any inline script in the
  built `index.html` is hashed automatically), must not load third-party origins, and must use `style` attributes /
  CSSOM only (allowed) — no `eval`/`new Function`.

### Types

```ts
// ---- TestNode gains
interface TestNode {
  // … as above, plus:
  outputTruncated?: boolean;   // stdOut/stdErr were cut to 64 K chars each (first 16 K + marker line + last 48 K);
                               // GET /api/tests/output has the full text while it is retained (32 M chars LRU, memory only)
  hasOutput?: boolean;         // lean payloads only (stdOut/stdErr omitted): the test has console output to fetch
}
// errorType "TestHostCrashed": the test was running when its test host exited (see "Crashed test host")

// ---- TestProject gains
interface TestProject {
  // … as above, plus:
  targetFramework?: string;       // TFM the tests run on, e.g. "net10.0"
  availableFrameworks?: string[]; // all TFMs of the project (from TargetFrameworks / TargetFramework), declaration order
}

// ---- RunSummary gains
interface RunSummary {
  // … as above, plus:
  aborted?: boolean;       // a test host exited / crashed during the run
  abortReason?: string;    // e.g. "Test host of Shop.Tests exited with code 3 while running Crash_ExitsProcess; 4 tests did not run."
  cancelled?: boolean;     // cancelled before every requested test reported
  notRun?: number;         // requested tests without a result (aborted / cancelled / never reported); counted in total only
  debug?: boolean;         // a debug run (POST /api/debug / "debug" command); trigger is "debug"
}

// ---- ExplorerState gains
interface ExplorerState {
  // … as above, plus:
  pendingRun?: PendingRunInfo;  // the queued run request (at most one), see "Run queue"
  lean?: boolean;               // true in lean snapshots (?lean=1)
}
interface PendingRunInfo {
  runId: string;                // the id the run will have (RunSummary.runId) — returned by POST /api/run
  requestedAt: string;
  scope: "all" | "subset";
  projectIds?: string[];
  testCount?: number;           // subset only: number of selected tests (after merging)
  coverage?: boolean; repeat?: number;
  trigger?: "user" | "watch" | "api" | "debug";
  requests: number;             // how many requests were merged into it
}

// ---- CoverageReport gains
interface CoverageReport {
  // … as above, plus:
  noCoverableLines?: boolean;   // linesValid == 0: lineRate is 0 (was 1 = "100%"). Render "No coverable lines" (test
                                // assemblies are excluded; product code must live in a separate project). For branches
                                // check branchesValid == 0 yourself (branchRate stays 1 there, per Cobertura).
}

interface DebugHint { editor: "vscode" | "rider" | "visualstudio"; name: string; steps: string; launchJson?: string }

// ---- ServerEvent gains
type ServerEvent = /* … */
  | { type: "pendingRun"; pending?: PendingRunInfo }          // queued request changed; no `pending` = queue empty
  | { type: "settingsChanged"; settings: { watch: boolean; coverageEnabled: boolean; tracingEnabled: boolean;
        quarantined?: string[]; impactMap?: ImpactMapInfo } }   // LEAN clients only, instead of a full `state` on toggles
  | { type: "debuggerWaiting"; projectId: string; pid: number; testIds: string[]; runId: string; hints: DebugHint[] }
  | { type: "debuggerAttached"; projectId: string; pid: number; runId: string }
  | { type: "commandRejected"; command?: string; status: 400 | 404 | 409; error: string; unknownTestIds?: string[] };
                                                               // only to the client whose WS command was invalid

// ---- ClientCommand gains
type ClientCommand = /* … */
  | { type: "debug"; projectId?: string; testIds: string[] }        // tests of exactly one project
  | { type: "setTargetFramework"; projectId: string; targetFramework?: string };  // omitted/"" = default (highest)
```

### REST

| Endpoint | Body / query | Response |
| --- | --- | --- |
| `GET /api/health` | | `200 { status: "ok", version, running }` — cheap liveness (Aspire health check) |
| `GET /api/summary` | | `200 { version, workspaceName?, tests: { total, passed, failed, skipped, notRun, running, queued, cancelled }, currentRun?, lastRun?, pendingRun?, activeOperation?, watch, coverageEnabled, projects: [{ id, name, status, hostState?, lastError?, tests, targetFramework? }], cancelling? }` (`failed` includes timedOut; `cancelling: true` between a cancel and the end of the operation it stops) |
| `GET /api/state?lean=1` | | lean snapshot (see "Lean mode"); without `lean` unchanged |
| `POST /api/run` | `{ projectId?, testIds?, coverage?, repeat? }` | `202 { accepted: true, runId, queued, merged, unknownTestIds? }`; `400` malformed JSON / wrong types; `404 { error, projects }` unknown projectId; `404 { error, unknownTestIds }` when none of the testIds is known (some unknown → they are ignored and listed in the 202) |
| `GET /api/runs/{runId}/status` | | `200 { runId, state, reason?, run?, supersededBy? }`; state: `queued` → `building` → `running` → `finished` \| `cancelled` \| `aborted` \| `skipped` (nothing could run, e.g. build failed) \| `superseded` (replaced while queued, see `supersededBy`); `404` unknown / older than the last 200 requests |
| `POST /api/cancel` | (no body) | `202 { accepted: true, cancelledRunning, activeOperation?, clearedQueuedRunId?, clearedQueuedRequests }` |
| `POST /api/debug` | `{ projectId?, testIds }` | like `/api/run`; `400` without testIds or with tests of several projects |
| `POST /api/target-framework` | `{ projectId, targetFramework? }` | `202` (rebuild + rediscover start); `400` not one of `availableFrameworks`; `404` unknown project; `409` project not evaluated yet |
| `GET /api/tests/details` | `?projectId=&id=` | `200 TestNode` (full: capped output, history); `404`; `400` without id |
| `GET /api/tests/history` | `?projectId=&id=` (both optional) | `200 { tests: [{ projectId, id, history, flaky? }] }` — without `id`: every test that has history; `404` unknown project / test |
| `GET /api/tests/output` | `?projectId=&id=` | `200 { projectId, id, stdOut?, stdErr?, truncated }` — full output when retained (`truncated: false`), else the capped text with `truncated: true` |
| `POST /api/discover`, `/api/restart-host`, `/api/impact-map` | `{ projectId? }` | `404 { error, projectId }` for an unknown project (was 202) |
| `POST /api/watch`, `/api/coverage`, `/api/tracing` | `{ enabled: boolean }` | `400` when `enabled` is missing or not a boolean (was: treated as false) |
| `POST /api/open-in-editor` | as before | `400` malformed JSON (was: treated as empty) |

### Behaviour

- **Run queue (coalescing).** At most one run request waits while another operation (run, discovery, host restart,
  impact-map pass, watch rebuild) holds the lock. A new request while one is waiting is **merged** into it when coverage,
  repeat and debug match: the selections are united per project ("all tests of a project" absorbs subsets), identical
  requests simply dedupe; the merged run keeps the first `runId` (`merged: true` in the 202), a user/API trigger wins over
  `watch`. A request with different options **replaces** the waiting one (`superseded`, `supersededBy` = the new id);
  log lines say which. `pendingRun` events / `ExplorerState.pendingRun` / `GET /api/summary` show the queue. Requests
  are validated synchronously before anything is queued.
- **Cancel** cancels the running operation **and** clears the queued request (`pendingRun` event without `pending`,
  log "Cancellation requested (stopping the current run); cleared the queued run (3 requests)"). A warm host gets 10 s
  (`LABCOAT_CANCEL_GRACE_SECONDS`) to answer `$/cancelRequest` before it is killed (log names the reason); MSTest
  finishes the test in progress unless it observes `TestContext.CancellationToken`, so the warm host (and e.g. an Aspire
  fixture) usually survives. Tests of the run that had not started become `cancelled` right away (`testsUpdated`), not
  when the host has stopped; every requested test leaves `queued` / `running` when the run ends, whichever way it ended.
  The summary gets `cancelled: true` + `notRun`; log "Run cancelled: …, 1 not run". MTP's
  own "Exception during the cancellation of request id …" log is no longer shown.
- **restartHost preempts:** while an operation runs, `restartHost` cancels it and kills the selected test hosts right away
  (no grace period), then starts fresh ones.
- **Crashed test host.** When the test host exits mid-run (`Environment.Exit`, StackOverflow, `FailFast`, native crash,
  killed), the tests that were `running` become `failed` with `errorType: "TestHostCrashed"`,
  `errorMessage: "Test host exited with code N while this test was running.\n\nLast stderr lines:\n…"` (≤ 10 lines of
  the process's own stderr; test output captured by the framework is not in it); the project's other requested tests that
  did not report become `notRun` (their old result and details are cleared — never left "passed"). If the host died
  before it reported any progress and exactly one test was left, that test is the one marked failed; otherwise the
  reason says "before it reported which test was running". Results that MTP had not flushed yet are lost (they show as
  `notRun`). The run continues with the other projects; `runFinished` carries `aborted: true` + `abortReason`, and a
  `state` snapshot follows it so clients drop any "keep the previous result while pending" display. The warm host is
  restarted lazily by the next request. Persisted history gets the failed entry.
- **Console output cap.** Each result's `stdOut`/`stdErr` is cut to 64 K chars (head 16 K + `… [N characters omitted;
  full output: GET /api/tests/output] …` + tail 48 K) in every payload, with `outputTruncated: true`. The full text is
  kept in memory (32 M chars total, least recently stored dropped first; a single output over that is not kept).
- **Lean mode** (`/ws?lean=1`, `GET /api/state?lean=1`): tests carry no `history`, `stdOut`, `stdErr` (but
  `hasOutput: true` when there is output) in `state` snapshots **and** `testsUpdated`; toggles (watch, coverage,
  tracing, quarantine, impact map progress) send `settingsChanged` instead of a full `state`. Measured on a 7-test
  workspace with one 2 MB output: `/api/state` 81 KB → 13 KB lean. Default (legacy) mode is unchanged apart from the
  output cap, so the current UI keeps working.
  **UI migration (recommended):** connect with `?lean=1`; fetch `GET /api/tests/details` for the selected test (and
  again on `testsUpdated`/`runFinished` touching it), `GET /api/tests/history` (all tests, once, then per test after
  `runFinished`) for the history strip / run diff / sparklines, `GET /api/tests/output` when "show full output" is
  clicked on a truncated test; handle `settingsChanged`, `pendingRun` ("Queued: 3 requests" / "Run 7 tests next"),
  `commandRejected` (toast), `aborted`/`cancelled`/`notRun` in the run pill.
- **WebSocket limits:** client messages over 1 MB close the socket with **1009** ("Messages are limited to 1024 KB.").
  A client more than 10 000 messages behind is disconnected with **1008** (it reconnects and gets a fresh snapshot)
  instead of silently losing events. Malformed / invalid commands get a `commandRejected` reply (400 malformed or
  missing fields, 404 unknown project / tests, 400 unknown command) and never start anything — `run` with unknown ids
  no longer falls back to a full run.
- **Debug a test** (groundwork, UI later): `POST /api/debug { projectId, testIds }` (or WS `{ type: "debug", … }`) runs
  the tests in a **fresh one-off test host** (never the warm one) started with `TESTINGPLATFORM_WAIT_ATTACH_DEBUGGER=1`
  (verified in MTP 2.0.2: the app blocks in `TestApplication.WaitForDebuggerToAttach()` before connecting back). Flow:
  `runStarted` (`debug: true`, trigger `"debug"`) → `debuggerWaiting { pid, hints }` → (user attaches) →
  `debuggerAttached` → results as usual → `runFinished`. The host waits up to 10 minutes; `cancel` gives up and kills
  it immediately. `hints` (one per editor, `steps` mention the pid):
  VS Code — Run and Debug → ".NET: Attach" / "Debug: Attach to a .NET 5+ or .NET Core process", or the `launchJson`
  configuration `{ "type": "coreclr", "request": "attach", "processId": "<pid>" }`; Rider — Run → Attach to Process…
  (Ctrl+Alt+F5); Visual Studio — Debug → Attach to Process… (Ctrl+Alt+P), "Managed (.NET Core, .NET 5+)". Verified by
  attaching netcoredbg (`--attach <pid>`, then `continue`).
- **Multi-targeting:** the default TFM is the highest .NET one, not the first: `net10.0` over `net8.0`; plain `net9.0`
  over `net9.0-windows` of the same version; `netcoreapp*` after `net5+`; `netstandard` / `net4x` last.
  `setTargetFramework` / `POST /api/target-framework` switches (persisted per workspace in `settings.json`), rebuilds
  with `-f <tfm>`, retires the warm host and rediscovers. `TestProject.targetFramework` / `availableFrameworks` are set
  after the first evaluation (also for single-target projects). UI later: a TFM picker on multi-target projects.
- **Framework detection** uses MSBuild evaluation for candidates the text check can't decide (see `docs/protocol.md`):
  TUnit, xunit.v3 (incl. `xunit.v3.mtp-v2`), NUnit with `EnableNUnitRunner`, `Microsoft.Testing.Platform.*`, and
  settings from `Directory.Build.props` / central package management are found.
- **CLI:** an explicit path that does not exist, an unsupported file or an unreadable solution → `error …` + exit 1
  (no stack trace). `--help` documents `LABCOAT_HISTORY_DIR`, `LABCOAT_NAME`, `LABCOAT_ALLOWED_HOSTS`,
  `LABCOAT_ALLOWED_ORIGINS`, `LABCOAT_TOKEN`, `LABCOAT_CANCEL_GRACE_SECONDS`, `LABCOAT_PATH`, `LABCOAT_DEFAULT_PORT`.
- Test hosts no longer inherit Labcoat's own `PORT`, `ASPNETCORE_URLS`, `ASPNETCORE_HTTP(S)_PORTS`, `LABCOAT_TOKEN` or
  `LABCOAT_ALLOWED_*`.

### Aspire hosting (Labcoat.Aspire.Hosting)
- Health check `GET /api/health` (was `/api/state`).
- Resource state = suite result, polled from `/api/summary` every 2 s once the resource is ready: `✓ 12 passed`
  (success), `✕ 1 failed · 20 tests` / `✕ Run aborted · 20 tests` / `✕ Build / host error` (error), `▶ Running 7/20`
  (info; warn with "· n failed"), `◼ Cancelling…` (warn, while a cancel is in progress), `◌ Building`,
  `○ 20 tests · not run` (info), `No tests` (warn). Trade-off: Aspire
  computes `HealthStatus` only for the literal "Running" state, so the dashboard shows no health while the custom state
  is shown; `.WithSuiteResultState(false)` opts out.
- New commands **Cancel run** and **Restart test host**. **Run all tests** / **Run failed tests** wait for exactly their
  own run (the `runId` from `POST /api/run`, polling `/api/runs/{runId}/status`) and report "20 tests: 18 passed,
  1 failed, 1 skipped." (failure when tests failed, the run was cancelled / aborted / nothing ran).
- Passes a random per-AppHost-run `LABCOAT_TOKEN` (commands send `X-Labcoat-Token`) and
  `LABCOAT_ALLOWED_HOSTS=<endpoint host>`. Verified: DCP's proxy is a TCP proxy, the browser's `Host`
  (`localhost:<proxy port>`) reaches Labcoat unchanged, so same-origin checks work through it.

### Breaking-ish (for non-UI callers)
- Cross-origin / foreign-Host requests are refused (by design). Scripts: no `Origin` header, loopback, JSON content
  type, and `X-Labcoat-Token` when `LABCOAT_TOKEN` is set (always under Aspire).
- `/api/run` with unknown project / ids → 404 instead of 202 (+ a full run); malformed JSON → 400 everywhere;
  `/api/watch|coverage|tracing` require `enabled`.
- `CoverageReport.lineRate` is 0 (not 1) when there are no coverable lines (`noCoverableLines: true`).

## Round 5 additions
UI setting defaults from outside the browser (settings files, the Aspire AppHost, env, CLI). Additive: a host without
configuration sends nothing new; old clients ignore the new field. Implementation: `src/Labcoat/LabcoatConfig.cs`,
`ui/src/ui-defaults.ts`. User-facing reference: `docs/configuration.md` ("Settings files", "UI settings").

### Types

```ts
// ---- ExplorerState gains (on-connect snapshot, every broadcast `state`, GET /api/state incl. ?lean=1)
interface ExplorerState {
  // … as above, plus:
  uiDefaults?: UiDefaults;      // omitted when no layer sets or locks anything
}
type UiSettingKey = "theme" | "mode" | "motion" | "editor" | "coverageTint"
                  | "confetti" | "partialRunsInHistory" | "openOutputOnError" | "singleKeyShortcuts";
type UiSettingSource = "user" | "workspace" | "apphost" | "env" | "cli";
interface UiDefaults {
  theme?: "catppuccin" | "aspire" | "github" | "one" | "dracula" | "solarized" | "gruvbox";
  mode?: "system" | "light" | "dark";
  motion?: "system" | "reduced" | "off";
  editor?: "vscode" | "rider" | "visualstudio";
  coverageTint?: "line" | "gutter";
  confetti?: boolean; partialRunsInHistory?: boolean; openOutputOnError?: boolean; singleKeyShortcuts?: boolean;
  locked?: UiSettingKey[];                                  // in the order above
  sources?: Partial<Record<UiSettingKey, UiSettingSource>>; // layer of each value (the highest that sets it)
  lockedBy?: Partial<Record<UiSettingKey, UiSettingSource>>;// layer of each lock (the highest that locks it)
  files?: { user?: string; workspace?: string };            // display paths: "~/.labcoat.json", ".labcoat.json"
                                                            // (workspace: relative to the workspace root)
}
// e.g. { "theme": "dracula", "editor": "rider", "locked": ["editor"],
//        "sources": { "theme": "workspace", "editor": "apphost" }, "lockedBy": { "editor": "apphost" },
//        "files": { "workspace": ".labcoat.json" } }
```

### index.html

`GET /`, `GET /index.html` and the SPA fallback serve `index.html` with the same object as an attribute:
`<html data-labcoat-defaults="{&quot;theme&quot;:&quot;dracula&quot;,…}" lang="en">` (HTML-encoded JSON; no
attribute when there are no defaults). The pre-paint script reads it, so the first paint uses the right theme, mode
and motion before the WebSocket connects. Being an attribute, it leaves the inline scripts — and their CSP
`sha256-…` hashes — unchanged.

### Sources and precedence (host side)

Lowest first; per setting the highest layer that sets a value wins, locks from all layers add up:

1. built-in (the UI's own defaults);
2. user file: `~/.labcoat.json`, else `$XDG_CONFIG_HOME/labcoat/config.json` / `~/.config/labcoat/config.json`
   (`%APPDATA%\Labcoat\config.json` on Windows); both present → the home file + a warning. Skipped with
   `--no-user-config` or `LABCOAT_NO_USER_CONFIG=1`;
3. workspace file: `--config <file>`, else the first `.labcoat.json` / `.config/labcoat.json` walking up from the
   common root of the workspace paths, stopping after the directory with `.git` and before the home directory;
4. `LABCOAT_APPHOST_UI_SETTINGS` — set by Labcoat.Aspire.Hosting (`AddLabcoat(name, o => o.Ui…)`,
   `.WithUiSettings(…)`, configuration section `Labcoat` / `Labcoat:Resources:<name>`);
5. `LABCOAT_UI_SETTINGS`;
6. CLI: `--theme`, `--mode`, `--editor`, `--motion` (values), `--lock <names|all>`.

Files: `{ "$schema"?, "ui"?: { <UiSettingKey>: value, "locked"?: string[] | "all" | true }, "watch"?, "coverage"?,
"traces"?, "port"? }` (schema: `docs/labcoat.schema.json`; comments and trailing commas allowed). The env vars carry
the `ui` object only. Keys are matched case-insensitively, choice values too (normalized to lower case); booleans also
accept "true"/"false" strings.

### Behaviour

- **Validation:** unknown keys and invalid values are dropped with a startup log line (`warning Settings: <source>:
  'theme' must be one of …; ignored.` — on the console and in the UI's log), the rest of the source still applies;
  malformed env JSON or a malformed file is ignored with a warning. Fatal (`error …`, exit 1): an invalid CLI value,
  and a `--config` file that is missing or not valid JSON. One `info` line lists the result: `UI defaults:
  theme=dracula (workspace), editor=rider (apphost, locked)`. The banner lists the files: `Settings:  ~/.labcoat.json,
  .labcoat.json`.
- **File-only start options:** `watch` / `coverage` / `traces` `true` turn the mode on at start (OR with the CLI
  flags); `port` applies only when neither `--port` nor `PORT` / `ASPNETCORE_URLS` is set (used as an explicit port).
- **Client rule:** effective value = `locked ? (default ?? built-in) : (browser choice ?? default ?? built-in)`.
  The UI stores only the settings the user changed (`mste.settings` gets `"$v": 2`; a v1 object, which stored every
  setting, drops values equal to the built-in default on load). `mste.theme` / `mste.themeFamily` are written only
  when the user picks them. Settings → **Reset to defaults** removes all three keys.
- `uiDefaults` is fixed for the lifetime of the host process (files are read at startup).

## Run affected tests (git)
"Run the tests affected by my changes": the host asks `git` which files changed against a base, maps them to tests
with the smart-watch strategy and either returns the plan (preview) or runs it. Additive (new endpoints, a new command,
optional fields). Implementation: `src/Labcoat/GitAffected.cs`; UI: `ui/src/components/run-affected.ts`.

### Bases

| `base` | Changed files | `ref` |
| --- | --- | --- |
| `working` (default) | uncommitted changes: staged + unstaged vs `HEAD`, plus untracked files (not ignored) | ignored |
| `branch` | everything since the branch left the base: working tree (incl. uncommitted + untracked) vs `merge-base(HEAD, <base>)` | the base ref; default = server option `affectedBase`, else `origin/HEAD` (the remote default branch), else the first of `origin/main`, `origin/master`, `main`, `master` that exists |
| `range` | a commit range: `A..B` (`git diff A B`), `A...B` (merge-base(A, B) vs B), or one commit `A` (its own changes, `A^` vs `A`) | required |

`git` runs as a child process with an argument list (no shell), `GIT_OPTIONAL_LOCKS=0`, `GIT_TERMINAL_PROMPT=0`, a 15 s
timeout, in the repository that contains the workspace (`git rev-parse --show-toplevel` from the common root of the test
projects). A `ref` must match `[A-Za-z0-9._/@{}~^:+-]` and not start with `-`. Renames are detected (`-M`; the new path
counts, `oldPath` is reported). Detached HEAD works (`branch` absent). A shallow clone without the merge base compares
with the base directly and adds a warning. Repositories without commits compare with the empty tree.

### Mapping (same strategy as smart watch, per changed file)
1. **Test file** (a `TestNode.filePath`) → the tests located in it. Reason: `"OrdersTests.cs changed (test file)"`.
2. **Project file** (`.csproj`/`.fsproj`/`.vbproj`, `Directory.Build.props|targets`, `Directory.Packages.props`,
   `*.props`, `*.targets`, `global.json`, `NuGet.config`) → all tests of the test projects it affects: the project
   itself, test projects referencing it (ProjectReference closure), or — for `Directory.*` / `*.props` / `*.targets` /
   `global.json` / `NuGet.config` — every test project under its directory. Reason: `"AspireSample.Tests.csproj changed: all tests of AspireSample.Tests"`.
3. **Product code** (`.cs`, `.fs`, `.vb`, `.razor`, …) known to the **impact map** → the tests whose executed methods
   overlap the changed lines (from `git diff -U0` hunks, new-side line numbers; spans are those recorded by the last
   coverage of each test, so edits made after that are approximate). A change outside every executed method → every
   test that touches the file. Reasons name the data's granularity: `"Orders.cs changed (method Validate, per-test data)"`,
   `"Orders.cs changed (method Validate, class-level data)"`, `"Orders.cs changed (outside executed methods, class-level data)"`.
4. Else, product code covered by a **cached per-test coverage** report (see "Cached per-test coverage") → those tests.
   Reason: `"Orders.cs changed (covered by cached coverage)"`.
5. Else, any other file under a test project's directory or its ProjectReference closure → **all tests of those
   projects**. Reason: `"no impact data for Orders.cs: all tests of AspireSample.Tests"`.
6. Files outside every project (docs, CI files, `.sln`), documentation (`.md`) and output of test runs (Verify
   `*.received.*`, anything under `TestResults/`) → no tests (`mapping: "none"`).
Deleted files map like the file did (test file → its former tests while they are still discovered; product file →
the tests that touched it, else the project fallback).

### Types

```ts
type AffectedBase = "working" | "branch" | "range";

interface GitInfo {                 // GET /api/git
  available: boolean;               // false: no git executable, or the workspace is not in a repository (see `error`)
  error?: string;
  root?: string;                    // repository root (absolute)
  branch?: string;                  // current branch; absent when detached
  head?: string;                    // short HEAD sha; absent before the first commit
  detached?: boolean;
  dirty?: number;                   // changed + untracked files (git status --porcelain=v2, ignored files excluded)
  upstream?: string; ahead?: number; behind?: number;
  shallow?: boolean;
  defaultBase?: string;             // the base `branch` uses without a ref (e.g. "origin/main"); absent when none exists
  affectedBase?: string;            // the server option, when configured
}

interface AffectedPlan {            // GET /api/affected
  base: AffectedBase;
  ref?: string;                     // the base ref / range that was used ("origin/main", "HEAD~3..HEAD")
  mergeBase?: string;               // branch: the merge-base commit (short sha)
  git: GitInfo;
  files: AffectedFile[];            // every changed file, sorted by relativePath
  tests: AffectedTest[];            // selected tests, in tree order
  projects: AffectedProject[];      // projects with selected tests, in project order
  counts: { files: number; tests: number; projects: number };
  strategy: "testFile" | "impactMap" | "coverage" | "project" | "mixed" | "none";
                                    // how the tests were found ("none": no changed file maps to a test)
  needsImpactMap?: boolean;         // a product file fell back to "all tests of a project" (no impact data): building the
                                    // impact map would narrow the selection
  impactMap?: ImpactMapInfo;
  warnings?: string[];              // e.g. shallow clone without merge base, tests of projects not discovered yet
}
interface AffectedFile {
  path: string;                     // absolute
  relativePath: string;             // relative to the repository root, "/" separators
  status: "added" | "modified" | "deleted" | "renamed" | "copied" | "typeChanged" | "untracked" | "unmerged";
  oldPath?: string;                 // renamed / copied: the old relative path
  kind: "test" | "product" | "project" | "other";
  mapping: "testFile" | "impactMap" | "coverage" | "project" | "none";
  tests: number;                    // tests selected because of this file
  projectIds?: string[];            // projects the file selected tests in
  lines?: [number, number][];       // changed line ranges (new side, inclusive); absent for added / deleted / binary files
  methods?: string[];               // impactMap: the executed methods the change overlaps
}
interface AffectedTest {
  projectId: string; id: string;
  reasons: string[];                // one per file that selected it
  granularity?: "test" | "class";   // selected through the impact map: per-test data, or class-level data (any file)
}
interface AffectedProject { projectId: string; name: string; tests: number; all?: boolean }  // all = every test of the project

// RunSummary.affected (AffectedRun) gains, for these runs:
//   strategy: "git"; base?: AffectedBase; ref?: string; tests?: number   (changedFiles = absolute paths, as for watch)
// ClientCommand gains:
//   | { type: "runAffected"; base?: AffectedBase; ref?: string; coverage?: boolean; buildImpactMap?: boolean }
```

### REST

| Endpoint | Body / query | Response |
| --- | --- | --- |
| `GET /api/git` | | `200 GitInfo` (cached ~2 s; `available: false` + `error` outside a repository) |
| `GET /api/affected` | `?base=working\|branch\|range&ref=<ref>` | `200 AffectedPlan` (never runs anything); `400 { error, code }` (`code`: `badBase`, `badRef`, `refRequired`); `404 { error, code: "unknownRef" \| "noBase" }`; `409 { error, code: "notGitRepo" \| "gitUnavailable" \| "noCommits" }` |
| `POST /api/run-affected` | `{ base?, ref?, coverage?, buildImpactMap? }` | `202 { accepted: true, runId, queued, merged, tests, files, projects }`; with `buildImpactMap` and projects lacking impact data `202 { accepted: true, impactMapFirst: true, tests, files, projects }` (no `runId` yet: the pass runs first, then the run is queued); `200 { accepted: false, reason, files }` when no test is affected; `400` malformed JSON; other errors as `GET /api/affected` (`404` when none of the selected tests is known any more) |

### Behaviour
- `runAffected` / `POST /api/run-affected` computes the plan, then queues one run (like `run`, it may merge with a
  queued request) with an exact per-project selection (`all` projects run whole). `trigger` is `"user"` (WS) or `"api"`
  (REST); `RunSummary.affected = { strategy: "git", base, ref, changedFiles, tests }`. Log: `Run affected (uncommitted
  changes): 3 files → 12 tests in 2 projects`. Nothing affected → log `info` and no run. Errors → `commandRejected`.
- `buildImpactMap: true` runs the impact-map pass first for the projects whose product files had no impact data
  (`needsImpactMap`), then (impactMap `"auto"`) refines the classes the plan selected through class-level data per test,
  then recomputes the plan and runs it. Without it, a run uses the plan as it is (class-level selections are a safe
  superset) while the refinement continues in the background for the next plan.
- The server option **`affectedBase`** (settings file key, `--affected-base <ref>`, env `LABCOAT_AFFECTED_BASE`,
  Aspire `AffectedBase` / `WithAffectedBase(ref)`) sets the default base ref of `branch`. Precedence: CLI > env >
  workspace file > user file. It is not validated against the repository at startup; an unknown ref gives `404 unknownRef`.
- `GET /api/git` only reads (`git status --porcelain=v2`, `rev-parse`, `symbolic-ref`); with `GIT_OPTIONAL_LOCKS=0` it
  never refreshes the index, so it does not get in the way of the user's own git commands.
- Impact-map spans now carry the Cobertura method name (async state machines / lambdas map to the declaring method,
  constructors to the class name); used only for the `methods` / reasons of the plan.

## Round 6 additions

### Snapshot testing (Verify)

Failed tests that use [Verify](https://github.com/VerifyTests/Verify) (Verify.MSTest / .NUnit / .XunitV3 / .TUnit)
get their snapshot files shown and handled in the UI. Additive: a new optional `TestNode` field and new endpoints.
Implementation: `src/Labcoat/Snapshots.cs`, `src/Labcoat/Explorer.Snapshots.cs`, `ui/src/snapshots.ts`,
`ui/src/components/snapshot-view.ts`.

```ts
// ---- TestNode gains
interface TestNode {
  // … as above, plus:
  hasSnapshotMismatch?: boolean;  // failed / timed out with a Verify mismatch (its message has "Directory: …" followed by
                                  // "New:" or "NotEqual:") and the received files were not accepted / rejected since.
                                  // Omitted when false; in every payload (also lean). The next result recomputes it.
}

type SnapshotKind = "text" | "image" | "binary";   // image: .png .jpg .jpeg .gif .bmp .webp .avif; otherwise binary
                                                   // when the first 8000 bytes contain a NUL byte, else text
interface SnapshotFile {
  name: string;          // the verified file name, "ReceiptTests.Render_n=2.verified.txt": identifies it in accept / reject
  receivedPath: string;  // canonical absolute paths (symlinks resolved)
  verifiedPath: string;  // may not exist yet (isNew)
  kind: SnapshotKind;
  isNew: boolean;        // Verify reported it under "New:", or there is no (or an empty) verified file
}
interface TestSnapshots { projectId: string; id: string; directory?: string; snapshots: SnapshotFile[] }
interface SnapshotActionResult {
  tests: { projectId: string; id: string; files: string[] }[];   // files = SnapshotFile.name of what was accepted / rejected
  errors?: { projectId: string; id: string; name?: string; error: string }[];
  runId?: string;        // accept with rerun: the queued run (RunSummary.runId)
}
```

| Endpoint | Body / query | Response |
| --- | --- | --- |
| `GET /api/tests/snapshots` | `?projectId=&id=` | `200 TestSnapshots` (empty `snapshots` when the test has no pending mismatch); `400` without id; `404` unknown test |
| `GET /api/snapshots/file` | `?path=<abs>` | the file: text kinds as `text/plain; charset=utf-8` (BOM dropped), images as `image/*`, binary as `application/octet-stream` + `Content-Disposition: attachment`; `Cache-Control: no-store`. `403` outside the source roots (same rule as `/api/source`) or not a Verify file name (`<prefix>.received.<ext>` / `<prefix>.verified.<ext>`); `404` missing; `413` over 10 MB |
| `POST /api/snapshots/accept` | `{ projectId?, id?, names?: string[], rerun?: boolean }` | `200 SnapshotActionResult`. Moves each received file onto its verified file (a rename: atomic on one volume; creates it for new snapshots), then re-runs the accepted tests unless `rerun: false`. No `id` = every test with `hasSnapshotMismatch` (of `projectId`, else all projects). `400` malformed JSON / `names` without `id`; `404` unknown project, test or name; `409` the test is queued / running |
| `POST /api/snapshots/reject` | `{ projectId?, id?, names? }` | like accept, but deletes the received files (the verified snapshots stay); never re-runs |

Behaviour:

- **Detection.** The host parses Verify's exception message (`Directory: <dir>`, then `New:` / `NotEqual:` entries
  `- Received: <file>` / `Verified: <file>`, up to `FileContent:`). Only received files that still exist are listed.
  When the message names no files (e.g. cut short), the test file's directory is scanned for
  `<Class>.<Method>{.|_|#}*.received.*` (parameterized tests: `Method_a=1_b=2`; several targets: `Method#name` / `.00`).
- **Containment.** Every received and verified path must be inside the test's **project directory** (canonical, symlinks
  resolved), both in the same directory, and the verified name must be the received name with `.received.` →
  `.verified.`; anything else is skipped (listing) or reported in `errors` (accept / reject). A received file that is
  a link is refused. Paths are never taken from the request, only names that select from the host's own list.
- After an accept / reject that left no pending file for a test, its `hasSnapshotMismatch` clears (`testsUpdated`); a
  log line `Accepted 2 snapshots of Render: …` is written.
- **No warm host for Verify projects.** Verify allows each snapshot file prefix once per process (a static set: a
  second run of the same test in one process fails with "The prefix has already been used"), so projects that reference
  a `Verify` / `Verify.*` package start a new test host for every request (`hostState` stays `"cold"`; one `info` log
  line says why). `LABCOAT_VERIFY_WARM=1` keeps them warm, for tests that call
  `VerifierSettings.DisableRequireUniquePrefix()`.
- **Diff tools.** Test hosts started by Labcoat get `DiffEngine_Disabled=true`, so Verify never opens an external
  diff tool (DiffEngine also turns itself off on CI servers); the UI shows the diff instead.

## Live console output (added)
Console output of tests **while they run**. Additive: old clients ignore the new event; nothing else changes. Research
and per-framework limits: `docs/protocol.md` "Live console output". Implementation: `src/Labcoat/LiveOutput.cs`,
`Explorer.Live.cs`; UI `ui/src/live-output.ts`, `ui/src/components/live-console.ts`.

```ts
// ---- ServerEvent gains (legacy and lean clients alike; only for tests that are currently running)
type ServerEvent = /* … */
  | {
      type: "testOutput";
      projectId: string;
      id: string;
      stream: "stdout" | "stderr" | "host";
      // stdout / stderr: attributed exactly — the framework streamed it (TUnit in-progress output, xUnit
      //   --show-live-output) or Labcoat.Testing's LabcoatOutput tagged it with the test's identity
      // host: a line the test host process wrote to its own stdout (err: true = stderr), attributed by timing
      text: string;      // one or more lines ("\n"-terminated; a TUnit chunk may end mid-line)
      seq: number;       // 1, 2, 3 … per execution of the test (a new execution starts at 1 again, e.g. repeat runs)
      shared?: boolean;  // several tests of the project were running: the host line may belong to another one
      err?: boolean;     // host lines from the process's stderr
    };

// ---- GET /api/tests/live-output?projectId=&id=   (projectId optional)
interface LiveOutputSnapshot {
  projectId: string; id: string;
  seq: number;                                  // last seq sent; apply later events with a higher seq
  chunks: { stream: "stdout" | "stderr" | "host"; text: string; shared?: boolean; err?: boolean }[];
  omitted?: number;                             // characters dropped between the kept head and tail
}
// 200 while the test runs; 404 { error } when it is not running; 400 without id
```

### Behaviour
- **Coalescing:** output is buffered per running test and flushed at most every **100 ms**, one event per run of the
  same kind (stream / err / shared) in order. At most **8 K characters per test and flush**: beyond that the tail is
  kept, prefixed by `… [N characters skipped: …] …`.
- **Ordering:** `testOutput` is published like other ordered events (pending `testsUpdated` are flushed first), so a
  client always sees the test `running` before its output. Output is only sent for running tests: once the result
  arrives, pending output is dropped, and a client should ignore `testOutput` for a test it does not consider
  running/queued.
- **Final output is authoritative.** The result's `stdOut`/`stdErr` (64 K cap, Round 4) replaces the live view; the UI
  keeps only streamed lines that are not in it ("live-only", e.g. xUnit console output, MSTest `DisplayMessage`),
  excluding `shared` lines, and only when the final output is complete (not truncated).
- **Per-test cap:** the snapshot keeps the first 16 K characters and the most recent chunks up to 64 K, like the result
  cap; the UI applies the same head + tail cap to its buffer with an "N characters omitted" line.
- **Attribution of host lines:** 200 ms after arrival (MTP batches test updates every 200 ms) to the single running test
  of the project, else to all running tests with `shared: true`; dropped while nothing runs; known platform banner lines
  are ignored. TUnit projects use only the exact stream.
- **Test host options** (when the output folder shows support): TUnit `TUNIT_ENABLE_IDE_STREAMING=1`; xUnit v3
  `--show-live-output on`. `LABCOAT_LIVE_OUTPUT=0` turns the feature off (no options, no events).
- **Labcoat.Testing marker:** lines `labcoat:live:<base64 UTF-8 JSON { s: "out" | "err", t: text, c: fully qualified
  class, m: method, n: display name, i?: test node uid (when the framework exposes it; not used for matching yet) }>`
  on the raw stdout are attributed to the running test with that class + method
  (+ display name for data rows), waiting up to 2 s for it to be reported running; they never appear as host output or
  in the crash diagnostics' stderr tail.
- **UI:** buffers only running tests, the test shown in the detail pane and the last 20 finished ones (64 K each);
  the console is not an `aria-live` region (one polite "Test finished; N lines of live output." on finish).

## Round 7 additions
Run traffic and snapshot size at 20,000 tests. **Breaking for non-UI WebSocket clients** (pre-1.0; the UI and host ship
together): during a run, result changes arrive as `testResults` patches instead of `testsUpdated` full entries, and lean
snapshots carry file indexes instead of `filePath`. REST endpoints other than `GET /api/state?lean=1` are unchanged:
`GET /api/state` (default), `/api/tests/details`, `/api/tests/history`, `/api/runs/*` and every coverage / affected /
snapshot endpoint keep absolute paths and full entries. Implementation: `src/Labcoat/TestUpdateEncoder.cs`,
`ui/src/test-results.ts`, `ui/src/store.ts`; the mock (`ui/src/mock/results.ts`) speaks the same protocol.

### Types

```ts
// ---- ServerEvent gains (default and lean clients)
type ServerEvent = /* … */
  | {
      type: "testResults";
      projects: { projectId: string; results: TestResultPatch[] }[];   // grouped by project, in order of first change
      runId?: string;                                                 // the run that `recorded` patches refer to
    };
interface TestResultPatch {
  id: string;
  // Set fields changed to that value; fields named in `clear` were removed; EVERYTHING ELSE IS UNCHANGED (incl. the
  // structural fields: displayName, namespace, className, methodName, filePath, line, lineEnd, traits).
  outcome?: Outcome; durationMs?: number; errorMessage?: string; errorType?: string; stackTrace?: string;
  stdOut?: string; stdErr?: string;          // default clients only
  hasOutput?: boolean;                       // lean clients only (as in lean full entries)
  outputTruncated?: boolean; lastRunAt?: string; traceId?: string; stale?: boolean; flaky?: boolean;
  hasSnapshotMismatch?: boolean;
  history?: HistoryEntry[];                  // the history was replaced (rare)
  historyAdd?: HistoryEntry;                 // one entry was appended (keep the last 20)
  recorded?: true;                           // short form of historyAdd: { outcome, durationMs, at: lastRunAt, runId: event.runId }
                                             //   built from the test AFTER applying this patch (durationMs left out when absent)
  clear?: (keyof TestNode)[];                // e.g. ["errorMessage", "stackTrace", "stale"]
}

// ---- ExplorerState gains (lean snapshots only: /ws?lean=1 and GET /api/state?lean=1)
interface ExplorerState {
  files?: string[];                          // distinct test source files (absolute), each once
  historyStamps?: Record<string, string>;    // per project; opaque. Changes when the project's history changed other
                                             // than through testResults (discovery restored a previous session's history)
}
// ---- TestNode in lean snapshots
//   fileIndex?: number  instead of filePath (index into ExplorerState.files)
//   lineEnd is omitted when it equals line (read a missing lineEnd as line), in every lean entry
```

### Behaviour
- **Full entry or patch.** Per test and flush (the ~50 ms coalescing of Round 3), the host compares the test with the
  copy every client already has (the last one it broadcast in a `state` or a flush): a test that clients don't know
  yet, or whose structure changed (rename, new location, traits, e.g. a data row's display name reported by a run), goes
  out as a full entry in `testsUpdated`; otherwise a `testResults` patch with only the changed result fields (nothing
  when nothing a client sees changed). A flush sends `testsUpdated` first, then `testResults`; one test is in at most one
  of them. A client that connects while updates are pending gets those tests as full entries in the next flush, so a
  patch always applies to the version the client has. Discovery still sends a `state` snapshot.
- **History** is no longer re-sent per test: the end-of-run update appends one entry (`recorded`, or `historyAdd` for a
  repeat run's flaky entry or another run). A client ignores an appended entry whose `at` + `runId` equal its last
  entry (it may have fetched it already). Lean clients keep their fetched history current with these and fetch
  `GET /api/tests/history` only on connect and, per project (`?projectId=`), when that project's `historyStamps` value
  changed — no longer after every run.
- **Client apply** (`applyResultPatch`): copy the test, set `clear`ed fields to undefined, assign the present fields,
  append the history entry. Patches for an unknown test are ignored (never sent by the host). The UI keeps a pending
  test's previous result on screen during a run (its `queued` patch is not shown); later patches of that test apply to
  the host's version, not to the displayed one.
- **Lean `/api/state`** resolves `fileIndex` against `files` in the store, so every UI consumer (tree, detail pane,
  open-in-editor, code / source view, coverage file ↔ test mapping, stack-frame links, run-affected, snapshots, live
  output) still reads an absolute `TestNode.filePath`; the strings are shared per file.
- **Toggles** (`settingsChanged` for lean clients) now flush pending test updates first; they used to be dropped for
  lean clients.

Measured with `node eng/perf-real.mjs` (8 projects × 2,500 MSTest tests, first full run): WebSocket bytes per run, lean
29.0 MB → 5.9 MB, default 31.8 MB → 5.9 MB (the test updates: 28.3 MB → 5.1 MB); `GET /api/state?lean=1`
9.9 MB → 7.7 MB; after a run the UI no longer refetches the history (6.8 MB) and merges it into every test.

## Diagnostics (added)

`GET /api/diagnostics` → `200 { markdown: string, report: DiagnosticsReport }`: the report behind **Copy diagnostics**
(command palette, Settings → About) and `labcoat --diagnostics` ([configuration.md](configuration.md#diagnostics-for-a-bug-report)).
A read, so no token is needed; it never contains the values of `LABCOAT_TOKEN`, OTLP headers or other secret-looking
variables, and paths in the home directory are shown as `~`.

```ts
interface DiagnosticsReport {
  labcoatVersion: string;
  startedAs: string;                  // "dotnet tool (global)", "Aspire (Labcoat.Aspire.Hosting)", "from source", …
  os: string;                         // "Linux 6.9.1 (X64)"
  runtime: string;                    // ".NET 10.0.1 (X64)"
  sdk?: string;                       // `dotnet --version` in the workspace
  workspaceKind: "git" | "solution" | "directory" | "none";
  workspace: string;                  // the repository root / solution file / common directory
  dataDirectory: string;              // the workspace's data folder
  dataReadOnly: boolean;              // another Labcoat owns the data folder: this one doesn't save
  projects: {
    name: string; path: string;       // path relative to the workspace root
    id?: string; status?: string;     // the explorer's project id and status
    targetFrameworks: string[];
    testFramework?: string;           // "MSTest 4.0.2", "xUnit v3 3.1.0", …
    testingPlatform?: string;         // Microsoft.Testing.Platform version
    packages: { name: string; version: string }[]; // test framework, platform, extensions, Labcoat, Verify, coverage
    note?: string;                    // e.g. "not restored (no obj/project.assets.json)"
  }[];
  settings: Record<string, string>;   // watch, coverage, traces (current), impactMap, affectedBase, coverage.*, uiDefaults, settingsFiles, logLevel, logFile, url
  environment: Record<string, string>; // LABCOAT_* and related variables; secrets as "(set, redacted)"
  log: string[];                      // the last 100 log lines (the explorer's and the host's)
  logSource?: string;
}
```

## Running now, slow tests and timeouts (added)

See [configuration.md](configuration.md#parallel-projects-slow-tests-and-timeouts) for the options.

### Types

```ts
interface RunningTestInfo {
  projectId: string;
  id: string;
  displayName: string;
  elapsedMs: number;        // when the event was sent; the client advances it with its own clock
  slow?: true;              // running longer than the hang threshold ("possibly hanging")
}
interface RunningNowInfo {
  count: number;            // tests running now (may be more than listed)
  tests: RunningTestInfo[]; // the longest-running ones, longest first, at most 10
  hangThresholdMs?: number; // absent = never slow
  testTimeoutMs?: number;   // absent = no per-test timeout
}
interface ExplorerState {
  runningNow?: RunningNowInfo;  // only while a run is in progress and a test runs
  limits?: { maxParallelProjects: number; hangThresholdMs?: number; testTimeoutMs?: number };
}
type ServerEvent = /* … */ | { type: "runningNow"; runningNow: RunningNowInfo | null };
```

### Behaviour

- `runningNow` is sent when the listed tests, their count or a `slow` flag changed, at most about once a second while
  a run is in progress (not for elapsed time alone); `runningNow: null` before `runFinished`.
- A test that runs longer than `testTimeout` gets `outcome: "timedOut"`, `errorType: "Timeout"` and an `errorMessage`
  saying so; its project's run is cancelled (its tests that had not run become `cancelled`), the other projects go on,
  and the run ends `aborted` with an `abortReason` naming the test. A result the test host still reports for the test
  (usually `cancelled`) does not replace `timedOut`. The `--timeout` of a project's `TestingPlatformCommandLineArguments`
  does the same for a run of that project that takes longer (the tests running then time out). A run that was
  cancelled (Stop) is not timed out on top of that.
- Projects run in parallel (`maxParallelProjects`), so `testsUpdated` / `testResults` of several projects interleave;
  the order of `state.tests` stays the discovery order, project by project.
- `discover` / `POST /api/discover` without `projectId` first looks for test projects again: new ones are added
  (`state` with the new project, then its `projectStatus` updates), removed ones are dropped with their tests (`state`).
  The same happens by itself when project, solution or `Directory.*.props` files change under the workspace roots.
- A batch build reports `projectStatus` `building` for every project in it at once, then each project's result; build
  errors (`buildErrors`) are those of the project and the projects it references.

## Export (added)
Downloads of a run and of the coverage report (LabcoatApp.Export.cs, Export/). Plain GETs like the rest of the read
API: the `Host` check applies (Round 4 "Security"); no Origin / token check, since a cross-site page can trigger a
download but never read it (no CORS headers). Every response is an attachment (`Content-Disposition: attachment;
filename=…; filename*=UTF-8''…`) with `Cache-Control: no-store`.

| Endpoint | Query | Response |
| --- | --- | --- |
| `GET /api/runs/{runId}/export` | `format=junit\|html`; `runId` = a persisted run (`GET /api/runs`) or `latest` (the newest) | `200` `application/xml` (JUnit) or `text/html` (report), file `labcoat-<workspace>-<runId>.junit.xml` / `.html`; `400 { error }` other format; `404 { error, runId }` unknown run / no runs yet |
| `GET /api/coverage/export` | `format=lcov\|cobertura`, `scope=full` (default: `fullCoverage`) `\|last` (`coverage`), `paths=relative` (default) `\|absolute` | `200` `text/plain` (lcov) or `application/xml` (Cobertura), file `labcoat-<workspace>-<report runId>.info` / `.cobertura.xml`; `400 { error }`; `404 { error }` no such report yet |

- **Run data:** a persisted run keeps outcome, duration, error message (≤ 2 KB) and stack (≤ 4 KB) per test; names,
  namespace and class come from the current tests. While a test's current result is still that run's (its last
  `history` entry has the run id), the export uses it: full error, `errorType`, console output.
- **JUnit:** `testsuites > testsuite` (one per project with results: `name`, `tests`, `failures`, `errors`, `skipped`,
  `time`, `timestamp` (UTC, no zone), `id`, `package`) `> properties` (`labcoat.version`, `labcoat.runId`,
  `labcoat.trigger`, `labcoat.scope`, `labcoat.startedAt`, `labcoat.finishedAt`, `labcoat.workspace`,
  `labcoat.projectId`, `labcoat.project`, `targetFramework`, `labcoat.aborted`, `labcoat.cancelled`) `+ testcase`
  (`name` = display name, `classname` = `Namespace.Class` (else the project name), `time` in seconds). `failure` = an
  assertion (no `errorType`, or an assertion library's type), `error` = any other exception, a timeout or a crashed test
  host; `message` attribute = the error message, text = the stack trace. `skipped` = skipped, not run, cancelled, and
  failures of quarantined tests (`message` says which). `system-out` / `system-err` per test case, 16 K chars each,
  8 M chars per report (failed tests first). Text without ANSI sequences; characters XML 1.0 can't carry become U+FFFD.
  Valid against the Jenkins xunit plugin's `junit-10.xsd` (tests/Labcoat.Tests/Fixtures/JUnit).
- **HTML:** one static file; its CSP (`<meta http-equiv>`) is `default-src 'none'` plus the hashes of its one inline
  style and script. All test data HTML-escaped. Passed / skipped tests one line each; details for the first 500
  failures (message 8 K, stack 16 K, output 8 K chars each).
- **lcov:** per file `TN:`, `SF:`, `BRDA:<line>,0,<i>,<taken>` (the report has per-line branch counts: branches
  `0..n-1` of one block, the first `branchesCovered` taken, `-` when the line never ran), `BRF`, `BRH`, `DA`, `LF`, `LH`,
  `end_of_record`. **Cobertura:** `coverage > sources > packages > package (module) > classes > class (file) > lines`, a
  leading comment with the run, scope, path root and the coverage settings per project. Paths relative to the workspace
  root (the git repository root, else the common root of the inputs and source roots) with `/`, absolute outside it.
- `RunSummary.trigger` gains `"cli"`: a run of `labcoat --run` (docs/standalone.md "Labcoat in CI").
