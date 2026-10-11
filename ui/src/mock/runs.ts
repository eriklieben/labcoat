// RUNS mock: run-switcher / run-diff / diagnostics data on top of MockTransport (registered at the end of mockServer.ts).
//   - the seeded past runs get varied triggers (user / watch / api), two partial runs (hidden unless Settings → partial
//     runs in history) and a "newly slow" test in the latest run, with matching per-test history entries
//   - `?diag`: an extra project "Legacy.Tests" with 0 tests and TestProject.diagnostics (like a Library project
//     without an MTP runner whose test host exited), plus its discovery log lines
//   - `?vstest`: an extra project "Legacy.VSTest" that uses VSTest (TestProject.testPlatform): Labcoat doesn't start it
// (tracing / editors state is mocked by the DETAIL mock.)
import type { HistoryEntry, Outcome, RunDetails, RunSummary, ServerEvent, TestNode, TestProject } from "../contract";

const ROOT = "/src/demo/samples";

interface SpecLike {
  node: TestNode;
  behavior: "pass" | "fail" | "skip" | "timeout" | "flaky";
  ms: [number, number];
}

interface MockInternals {
  specs: SpecLike[];
  projects: TestProject[];
  pastRuns: RunDetails[];
  start(h: unknown): void;
  emit(e: ServerEvent): void;
  log(level: "info" | "warning" | "error", message: string, projectId?: string): void;
  later(ms: number, fn: () => void): number;
}

const done = new WeakSet<object>();

export function installRunsMock(cls: { prototype: object }) {
  const proto = cls.prototype as MockInternals;
  const origStart = proto.start;
  proto.start = function (this: MockInternals, h: unknown) {
    if (!done.has(this)) {
      done.add(this);
      augmentRuns(this);
      if (new URLSearchParams(location.search).has("diag")) addDiagnosticsProject(this);
      if (new URLSearchParams(location.search).has("vstest")) addVsTestProject(this);
    }
    origStart.call(this, h);
    if (this.projects.some((p) => p.id === "Legacy.Tests")) this.later(320, () => legacyLogs(this));
  };
}

const at = (iso: string, ms: number) => new Date(Date.parse(iso) + ms).toISOString();

function insertHistory(n: TestNode, e: HistoryEntry) {
  const h = [...(n.history ?? []), e].sort((a, b) => a.at.localeCompare(b.at));
  n.history = h.slice(-20);
}

function summarize(run: RunSummary, results: RunDetails["results"]) {
  const c = (o: Outcome[]) => results.filter((x) => o.includes(x.outcome)).length;
  run.total = results.length;
  run.passed = c(["passed"]);
  run.failed = c(["failed", "timedOut"]);
  run.skipped = c(["skipped"]);
  run.durationMs = Math.round(results.reduce((a, x) => a + (x.durationMs ?? 0), 0) + 900);
  run.finishedAt = at(run.startedAt, run.durationMs);
}

function augmentRuns(m: MockInternals) {
  const runs = m.pastRuns;
  if (runs.length < 12) return;
  // Triggers: a couple of runs started through the REST API (e.g. an Aspire dashboard command).
  runs[5].run.trigger = "api";
  runs[9].run.trigger = "api";

  const live = new Map(m.specs.map((s) => [`${s.node.projectId}|${s.node.id}`, s]));
  const sample = m.specs.filter((s) => s.node.projectId === "SampleTests");
  const failing = sample.find((s) => s.behavior === "fail");
  const passing = sample.filter((s) => s.behavior === "pass");

  // Partial runs: a class re-run after the regression (user) and a single test started via the API.
  const partial = (afterIdx: number, minutes: number, trigger: RunSummary["trigger"], specs: SpecLike[], runId: string) => {
    const startedAt = at(runs[afterIdx].run.startedAt, minutes * 60_000);
    const results = specs.map((s) => {
      const prev = s.node.history?.filter((e) => e.at <= startedAt).pop();
      const outcome: Outcome = prev?.outcome ?? "passed";
      const durationMs = Math.round(((s.ms[0] + s.ms[1]) / 2) * 10) / 10;
      insertHistory(s.node, { outcome, durationMs, at: at(startedAt, 300), runId });
      const r: RunDetails["results"][number] = { projectId: s.node.projectId, id: s.node.id, outcome, durationMs };
      if (outcome === "failed" && s.node.errorMessage) r.errorMessage = s.node.errorMessage;
      return r;
    });
    const run: RunSummary = { runId, startedAt, total: 0, passed: 0, failed: 0, skipped: 0, scope: "subset", trigger };
    summarize(run, results);
    return { run, results };
  };
  const p1 = partial(9, 25, "user", [failing, ...passing.slice(0, 2)].filter((s): s is SpecLike => !!s), "prev-partial-1");
  const p2 = partial(10, 40, "api", passing.slice(2, 3), "prev-partial-2");
  runs.splice(11, 0, p2);
  runs.splice(10, 0, p1);

  // Newly slow: the slowest passing test took ~2.5× longer in the latest run.
  const last = runs[runs.length - 1];
  const prevFull = runs[runs.length - 2].run.scope === "subset" ? runs[runs.length - 3] : runs[runs.length - 2];
  const slow = [...passing].sort((a, b) => b.ms[1] - a.ms[1])[0];
  if (slow) {
    const k = `${slow.node.projectId}|${slow.node.id}`;
    const before = prevFull.results.find((r) => `${r.projectId}|${r.id}` === k);
    const after = last.results.find((r) => `${r.projectId}|${r.id}` === k);
    if (before?.durationMs != null && after) {
      after.durationMs = Math.round(before.durationMs * 2.5 + 60);
      const n = live.get(k)!.node;
      const he = n.history?.find((e) => e.runId === last.run.runId);
      if (he) he.durationMs = after.durationMs;
      n.durationMs = after.durationMs;
      summarize(last.run, last.results);
    }
  }
}

function addDiagnosticsProject(m: MockInternals) {
  m.projects.push({
    id: "Legacy.Tests",
    name: "Legacy.Tests",
    path: `${ROOT}/Legacy.Tests/Legacy.Tests.csproj`,
    status: "idle",
    hostState: "cold",
    diagnostics: [
      {
        level: "error",
        message: "The test host exited with code 0 before it answered.",
        hint: "Run the test app directly (dotnet <test dll> --list-tests) to see why it does not start.",
        details: "Microsoft (R) Test Execution Command Line Tool Version 17.12.0\nUnrecognized option: --server\nUsage: Legacy.Tests [options]",
      },
      {
        level: "error",
        message: "OutputType is 'Library', but Microsoft.Testing.Platform test projects are executables.",
        hint: "Add <OutputType>Exe</OutputType> to the project (MSTest.Sdk and xunit.v3 set it for you).",
      },
      {
        level: "warning",
        message: "No Microsoft.Testing.Platform runner is enabled for this project.",
        hint: "Use MSTest.Sdk, or set <EnableMSTestRunner>true</EnableMSTestRunner> (MSTest), <EnableNUnitRunner>true</EnableNUnitRunner> (NUnit), or reference xunit.v3 / TUnit.",
      },
    ],
  });
}

function addVsTestProject(m: MockInternals) {
  m.projects.push({
    id: "Legacy.VSTest",
    name: "Legacy.VSTest",
    path: `${ROOT}/Legacy.VSTest/Legacy.VSTest.csproj`,
    status: "idle",
    hostState: "cold",
    testPlatform: "vstest",
    diagnostics: [
      {
        level: "warning",
        message: "Legacy.VSTest uses VSTest (Microsoft.NET.Test.Sdk + MSTest.TestAdapter). Labcoat runs Microsoft.Testing.Platform projects.",
        hint: "Set <EnableMSTestRunner>true</EnableMSTestRunner> and <OutputType>Exe</OutputType> (or use MSTest.Sdk), then Rediscover.",
        link: "https://github.com/eriklieben/labcoat/blob/main/docs/troubleshooting.md#my-project-uses-vstest",
      },
    ],
  });
}

function legacyLogs(m: MockInternals) {
  const pid = "Legacy.Tests";
  [
    `dotnet build "${ROOT}/Legacy.Tests/Legacy.Tests.csproj" -c Debug --nologo`,
    "  Legacy.Tests -> bin/Debug/net10.0/Legacy.Tests.dll",
    "Build succeeded. 0 Warning(s), 0 Error(s)",
    "Starting test host: dotnet bin/Debug/net10.0/Legacy.Tests.dll --server --client-port 41877",
  ].forEach((l) => m.log("info", l, pid));
  m.log("warning", "Test host exited with code 0 before the MTP handshake.", pid);
  m.log("info", "Discovered 0 tests in Legacy.Tests", pid);
}
