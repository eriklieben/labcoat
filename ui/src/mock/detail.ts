// DETAIL mock: Round 3 behaviour for the detail pane on top of MockTransport (registered at the end of mockServer.ts):
//   - state: tracingEnabled / tracingAvailable / telemetryAvailable / sourceRoots / quarantined / editors
//   - commands: run with `repeat` (aggregated results, RunSummary.repeat, history entry with `flaky`), setQuarantine,
//     setTracing (also `?tracing` in the URL to start with tracing on)
//   - quarantined tests count in RunSummary.quarantined instead of passed/failed/skipped
//   - with tracing on, AspireSample.Tests results get a traceId; mockTelemetry() answers GET /api/tests/telemetry
import type { ClientCommand, ExplorerState, HistoryEntry, Outcome, RunDetails, RunSummary, ServerEvent, TestNode, TestProject, TestTelemetry } from "../contract";

const ROOT = "/src/demo/samples";
const FINAL = new Set<Outcome>(["passed", "failed", "skipped", "timedOut"]);

interface SpecLike {
  node: TestNode;
  behavior: "pass" | "fail" | "skip" | "timeout" | "flaky";
  ms: [number, number];
  error?: { type: string; message: string; stack: string };
  stdOut?: string;
  stdErr?: string;
}

/** The MockTransport internals this module uses (private there; accessed structurally). */
interface MockInternals {
  specs: SpecLike[];
  projects: TestProject[];
  run?: { summary: RunSummary; pending: SpecLike[]; active: Set<SpecLike>; projects: Set<string>; t0: number; coverage: boolean; subsetIds?: string[] };
  runCounter: number;
  lastRun?: RunSummary;
  pastRuns: RunDetails[];
  aspireUrl?: string;
  emit(e: ServerEvent): void;
  log(level: "info" | "warning" | "error", message: string, projectId?: string): void;
  later(ms: number, fn: () => void): number;
  snapshot(): ExplorerState;
  send(cmd: ClientCommand): void;
  complete(s: SpecLike, dur: number): void;
  finish(cancelled?: boolean): void;
  runSummaries(): RunSummary[];
}

interface DetailMockState {
  tracing: boolean;
  quarantined: Set<string>;
}

const states = new WeakMap<object, DetailMockState>();
const stateOf = (m: object): DetailMockState => {
  let s = states.get(m);
  if (!s) states.set(m, (s = { tracing: new URLSearchParams(location.search).has("tracing"), quarantined: new Set() }));
  return s;
};

const key = (t: { projectId: string; id: string }) => `${t.projectId}|${t.id}`;
const now = () => new Date().toISOString();
const rand = (a: number, b: number) => a + Math.random() * (b - a);
const isAspire = (t: TestNode) => t.projectId === "AspireSample.Tests";
const traceIdFor = (t: TestNode) => {
  let h1 = 0x811c9dc5;
  const s = `${key(t)}|${t.lastRunAt ?? ""}`;
  let out = "";
  for (let r = 0; r < 4; r++) {
    for (let i = 0; i < s.length; i++) h1 = Math.imul(h1 ^ s.charCodeAt(i), 16777619) >>> 0;
    h1 = Math.imul(h1 ^ r, 2654435761) >>> 0;
    out += h1.toString(16).padStart(8, "0");
  }
  return out;
};

function outcomeOf(s: SpecLike): Outcome {
  if (s.behavior === "fail") return "failed";
  if (s.behavior === "skip") return "skipped";
  if (s.behavior === "timeout") return "timedOut";
  if (s.behavior === "flaky") return Math.random() < 0.4 ? "failed" : "passed";
  return "passed";
}

/** Same heuristic as the host: ≥ 2 pass/fail transitions in the last 10 entries, or a mixed repeat entry. */
function isFlaky(h: HistoryEntry[]) {
  let flips = 0;
  let prev: boolean | undefined;
  for (const e of h.slice(-10)) {
    if (e.flaky) return true;
    const ok = e.outcome === "passed" ? true : e.outcome === "failed" || e.outcome === "timedOut" ? false : undefined;
    if (ok === undefined) continue;
    if (prev !== undefined && prev !== ok) flips++;
    prev = ok;
  }
  return flips >= 2;
}

export function installDetailMock(cls: { prototype: object }) {
  const proto = cls.prototype as MockInternals;
  const origSnapshot = proto.snapshot;
  const origSend = proto.send;
  const origComplete = proto.complete;
  const origFinish = proto.finish;

  proto.snapshot = function (this: MockInternals) {
    const st = stateOf(this);
    const base = origSnapshot.call(this);
    if (st.tracing) for (const s of this.specs) if (isAspire(s.node) && FINAL.has(s.node.outcome) && !s.node.traceId) s.node.traceId = traceIdFor(s.node);
    return {
      ...base,
      tests: this.specs.map((s) => s.node),
      tracingEnabled: st.tracing,
      tracingAvailable: !!this.aspireUrl,
      telemetryAvailable: this.aspireUrl ? true : undefined,
      sourceRoots: [ROOT],
      quarantined: [...st.quarantined],
      editors: [
        { id: "vscode", name: "Visual Studio Code", available: true },
        { id: "rider", name: "JetBrains Rider", available: false },
        { id: "visualstudio", name: "Visual Studio", available: false },
      ],
    };
  };

  proto.send = function (this: MockInternals, cmd: ClientCommand) {
    const st = stateOf(this);
    const c = cmd as { type: string; projectId?: string; testIds?: string[]; repeat?: number; quarantined?: boolean; id?: string; enabled?: boolean };
    if (c.type === "run" && (c.repeat ?? 0) >= 2) return repeatRun(this, c.projectId, c.testIds, Math.min(50, c.repeat!));
    if ((c.type as string) === "setQuarantine" && c.projectId && c.id) {
      const k = `${c.projectId}|${c.id}`;
      if (c.quarantined ?? true) st.quarantined.add(k);
      else st.quarantined.delete(k);
      this.emit({ type: "state", state: this.snapshot() });
      this.log("info", `${(c.quarantined ?? true) ? "Quarantined" : "Released from quarantine:"} ${c.id}`, c.projectId);
      return;
    }
    if ((c.type as string) === "setTracing") {
      st.tracing = !!c.enabled;
      this.emit({ type: "state", state: this.snapshot() });
      return;
    }
    return origSend.call(this, cmd);
  };

  proto.complete = function (this: MockInternals, s: SpecLike, dur: number) {
    // the result's trace id (tracing on, Aspire tests only) goes out with the result itself
    if (this.run?.active.has(s)) {
      const traced = stateOf(this).tracing && isAspire(s.node);
      s.node.traceId = undefined;
      if (traced) s.node.traceId = traceIdFor({ ...s.node, lastRunAt: String(Math.random()) });
    }
    return origComplete.call(this, s, dur);
  };

  proto.finish = function (this: MockInternals, cancelled?: boolean) {
    const r = this.run;
    const q = stateOf(this).quarantined;
    if (r && q.size) {
      let n = 0;
      for (const s of this.specs) {
        const t = s.node;
        if (!r.projects.has(t.projectId) || !q.has(key(t)) || !t.lastRunAt || t.lastRunAt < r.summary.startedAt || !FINAL.has(t.outcome)) continue;
        if (t.outcome === "passed") r.summary.passed--;
        else if (t.outcome === "skipped") r.summary.skipped--;
        else r.summary.failed--;
        n++;
      }
      if (n) r.summary.quarantined = n;
    }
    return origFinish.call(this, cancelled);
  };
}

/** Run the selected tests `count` times in a row; results are aggregated like the host does (contract "Repeat runs"). */
function repeatRun(m: MockInternals, projectId: string | undefined, testIds: string[] | undefined, count: number) {
  if (m.run) return m.log("warning", "A test run is already in progress. Cancel it first.");
  const runnable = new Set(m.projects.filter((p) => p.status !== "error").map((p) => p.id));
  let targets = m.specs.filter((s) => runnable.has(s.node.projectId));
  if (projectId) targets = targets.filter((s) => s.node.projectId === projectId);
  if (testIds?.length) {
    const ids = new Set(testIds);
    targets = targets.filter((s) => ids.has(s.node.id));
  }
  if (!targets.length) return m.log("warning", "Nothing to run.");
  const st = stateOf(m);
  const projects = new Set(targets.map((s) => s.node.projectId));
  const summary: RunSummary = {
    runId: `run-${++m.runCounter}`,
    startedAt: now(),
    total: targets.length,
    passed: 0,
    failed: 0,
    skipped: 0,
    scope: testIds?.length ? "subset" : "all",
    trigger: "user",
    repeat: { count, completed: 0, perTest: [] },
  };
  m.run = { summary, pending: [], active: new Set(), projects, t0: performance.now(), coverage: false };
  const stats = new Map<SpecLike, { passed: number; failed: number; skipped: number; durationsMs: number[] }>();
  const lastFailure = new Map<SpecLike, { outcome: Outcome; at: string }>();
  m.log("info", `Run ${summary.runId} started: ${targets.length} test(s) × ${count} repetitions`);
  for (const s of targets) s.node.outcome = "queued";
  m.emit({ type: "runStarted", run: structuredClone(summary), testIds: targets.map((s) => s.node.id) });
  m.emit({ type: "testsUpdated", tests: targets.map((s) => s.node) });

  const iteration = (i: number) => {
    if (m.run?.summary !== summary) return; // cancelled
    m.log("info", `Repeat ${i + 1}/${count}`);
    if (i > 0) {
      for (const s of targets) s.node.outcome = "queued";
      m.emit({ type: "testsUpdated", tests: targets.map((s) => s.node) });
    }
    m.later(120, () => {
      if (m.run?.summary !== summary) return;
      for (const s of targets) {
        const o = outcomeOf(s);
        const d = o === "skipped" ? 0 : Math.round(rand(s.ms[0], s.ms[1]) * 10) / 10;
        const n = s.node;
        n.outcome = o;
        n.durationMs = d;
        n.lastRunAt = now();
        n.traceId = st.tracing && isAspire(n) ? traceIdFor({ ...n, lastRunAt: `${n.lastRunAt}${i}` }) : undefined;
        let x = stats.get(s);
        if (!x) stats.set(s, (x = { passed: 0, failed: 0, skipped: 0, durationsMs: [] }));
        if (o === "passed") x.passed++;
        else if (o === "skipped") x.skipped++;
        else {
          x.failed++;
          lastFailure.set(s, { outcome: o, at: n.lastRunAt });
        }
        if (o !== "skipped") x.durationsMs.push(d);
      }
      m.emit({ type: "testsUpdated", tests: targets.map((s) => s.node) });
      summary.repeat!.completed = i + 1;
      if (i + 1 < count) m.later(80, () => iteration(i + 1));
      else done();
    });
  };

  const done = () => {
    const rep = summary.repeat!;
    rep.perTest = targets
      .filter((s) => stats.has(s))
      .map((s) => ({ projectId: s.node.projectId, id: s.node.id, ...structuredClone(stats.get(s)!) }));
    const recorded: TestNode[] = [];
    let quarantined = 0;
    for (const s of targets) {
      const x = stats.get(s);
      if (!x) continue;
      const n = s.node;
      const fail = lastFailure.get(s);
      n.outcome = fail ? fail.outcome : x.passed ? "passed" : "skipped";
      n.durationMs = x.durationsMs.length ? Math.round((x.durationsMs.reduce((a, b) => a + b, 0) / x.durationsMs.length) * 100) / 100 : n.durationMs;
      n.stdOut = s.stdOut;
      n.stdErr = fail ? s.stdErr : undefined;
      if (fail || n.outcome === "skipped") {
        n.errorMessage = s.error?.message;
        n.errorType = s.error?.type || undefined;
        n.stackTrace = s.error?.stack || undefined;
      } else {
        delete n.errorMessage;
        delete n.errorType;
        delete n.stackTrace;
      }
      const mixed = x.passed > 0 && x.failed > 0;
      if (st.quarantined.has(key(n))) quarantined++;
      else if (n.outcome === "passed") summary.passed++;
      else if (n.outcome === "skipped") summary.skipped++;
      else summary.failed++;
      n.history = [...(n.history ?? []), { outcome: n.outcome, durationMs: n.durationMs, at: n.lastRunAt!, runId: summary.runId, ...(mixed ? { flaky: true } : {}) }].slice(-20);
      n.flaky = isFlaky(n.history) || undefined;
      delete n.stale;
      recorded.push(n);
    }
    if (quarantined) summary.quarantined = quarantined;
    summary.finishedAt = now();
    summary.durationMs = Math.round(performance.now() - m.run!.t0);
    m.run = undefined;
    m.lastRun = summary;
    m.emit({ type: "testsUpdated", tests: recorded });
    m.pastRuns = [
      ...m.pastRuns,
      {
        run: structuredClone(summary),
        results: recorded.map((n) => ({
          projectId: n.projectId,
          id: n.id,
          outcome: n.outcome,
          durationMs: n.durationMs,
          ...(n.outcome === "failed" || n.outcome === "timedOut" ? { errorMessage: n.errorMessage?.slice(0, 2048), stackTrace: n.stackTrace?.slice(0, 4096) } : {}),
        })),
      },
    ].slice(-50);
    m.emit({ type: "runsUpdated", runs: m.runSummaries() });
    m.emit({ type: "runFinished", run: structuredClone(summary) });
    const flaky = rep.perTest.filter((x) => x.passed && x.failed).length;
    m.log("info", `Run finished: ${summary.passed} passed, ${summary.failed} failed, ${summary.skipped} skipped (${rep.completed}/${rep.count} repetitions, ${flaky} flaky)`);
  };

  m.later(300, () => iteration(0));
}

// ---- GET /api/tests/telemetry ---------------------------------------------------------------------------------

export function mockTelemetry(t: TestNode, signal: AbortSignal): Promise<TestTelemetry> {
  return new Promise((resolve, reject) => {
    const id = window.setTimeout(() => {
      const traceId = t.traceId ?? "";
      const at = t.lastRunAt ?? now();
      const failed = t.outcome === "failed" || t.outcome === "timedOut";
      if (!failed) return resolve({ traceId, spans: [], logs: [] });
      const name = t.methodName ?? t.displayName;
      if (/Weather/i.test(name))
        return resolve({
          traceId,
          spans: [
            { name: "GET /weatherforecast", resource: "apiservice", status: "error", httpStatusCode: 500, durationMs: 1840, startTime: at, statusMessage: "Redis timeout", exceptionType: "StackExchange.Redis.RedisTimeoutException", exceptionMessage: "Timeout performing GET (5000ms), inst: 1, qu: 0" },
            { name: "GET /weather", resource: "webfrontend", status: "error", httpStatusCode: 500, durationMs: 1912, startTime: at },
          ],
          logs: [
            { resource: "apiservice", level: "error", message: "Unhandled exception while serving GET /weatherforecast: Timeout performing GET (5000ms)", timestamp: at, category: "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware" },
            { resource: "webfrontend", level: "warning", message: "Weather API call failed with 500; rendering empty table", timestamp: at, category: "AspireSample.Web.WeatherApiClient" },
          ],
        });
      if (/Health/i.test(name))
        return resolve({
          traceId,
          spans: [{ name: "GET /health", resource: "apiservice", status: "unset", httpStatusCode: 503, durationMs: 212, startTime: at, statusMessage: "Unhealthy: cache" }],
          logs: [{ resource: "apiservice", level: "warning", message: "Health check 'cache' completed with status Unhealthy (redis: connection refused)", timestamp: at, category: "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService" }],
        });
      if (/Cache/i.test(name))
        return resolve({
          traceId,
          spans: [{ name: "GET /cache/greeting", resource: "apiservice", status: "error", httpStatusCode: 500, durationMs: 5012, startTime: at, exceptionType: "StackExchange.Redis.RedisConnectionException", exceptionMessage: "It was not possible to connect to the redis server(s)." }],
          logs: [{ resource: "cache", level: "warning", message: "redis: connection refused on 6379 (container still starting)", timestamp: at }],
        });
      resolve({
        traceId,
        spans: [{ name: "POST /orders", resource: "apiservice", status: "unset", httpStatusCode: 400, durationMs: 38, startTime: at, statusMessage: "Order must contain items" }],
        logs: [{ resource: "apiservice", level: "error", message: "Order validation failed: Order must contain items", timestamp: at, category: "Program" }],
      });
    }, 250);
    signal.addEventListener("abort", () => {
      window.clearTimeout(id);
      reject(new DOMException("Aborted", "AbortError"));
    });
  });
}
