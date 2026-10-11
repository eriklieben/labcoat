/**
 * Shared inputs and run options for the Vitest bench suite (ui/bench/*.bench.ts, `npm run bench`).
 *
 * Test catalogs come from the mock backend's scale generator (src/mock/scale.ts, the same data as `?mock=huge` and the
 * Playwright scale checks), so the micro-benchmarks and the end-to-end perf checks look at the same shapes.
 * Everything is deterministic: two runs of the suite see identical inputs.
 */
import type { BenchRunOptions } from "vitest";
import type { Outcome, ServerEvent, TestNode, TestProject, TestResultPatch } from "../src/contract";
import { scaleCatalog } from "../src/mock/scale";
import { testKey } from "../src/store";

// ---- run options ------------------------------------------------------------------------------------------------

/** BENCH_TIME=<ms> scales every time budget (default 400 ms per task; CI and quick local runs can lower it). */
const TIME = Number(process.env.BENCH_TIME ?? 400);

/** Cheap tasks: time-boxed (at least 10 samples). */
export const QUICK: BenchRunOptions = { time: TIME, iterations: 10, warmupTime: Math.round(TIME / 4), warmupIterations: 5 };

/**
 * Tasks with an expensive untimed `beforeEach` (fresh 20k-row DOM, fresh store, …): tinybench keeps sampling until the
 * *measured* time reaches `time`, so a 1 ms task with a 100 ms setup would run for minutes. These run a fixed number of
 * samples instead.
 */
export const fixed = (iterations: number): BenchRunOptions => ({ time: 0, iterations, warmupTime: 0, warmupIterations: 2 });

// ---- test catalogs ------------------------------------------------------------------------------------------------

const ROOT = "/home/dev/src/contoso";

const OUTCOME: Record<string, Outcome> = { pass: "passed", fail: "failed", skip: "skipped", timeout: "timedOut", flaky: "failed" };

interface Catalog {
  projects: TestProject[];
  tests: TestNode[];
  /** testKey → test (the store's `tests` map) */
  map: Map<string, TestNode>;
}

let huge: { projects: TestProject[]; specs: TestNode[] } | undefined;

/**
 * `n` tests (≤ 20,000) of the `?mock=huge` catalog after a finished run: 8 projects, 5-6 level namespaces, data rows,
 * ~1 % failed, traits on every 5th test, an error message + stack on failures. For n < 20,000 every (20,000 / n)th test
 * is taken, so all projects and namespace shapes stay represented.
 */
export function catalog(n: number): Catalog {
  huge ??= scaleCatalog("huge", ROOT, (projectId, ns, cls, method, o) => {
    const fq = `${ns}.${cls}.${method}`;
    const outcome = OUTCOME[o.behavior ?? "pass"];
    const t: TestNode = {
      id: o.args ? `${fq}#${o.idx ?? 0}` : fq,
      projectId,
      displayName: o.args ? `${method} (${o.args})` : method,
      namespace: ns,
      className: cls,
      methodName: method,
      filePath: o.file,
      line: o.line,
      traits: (o.traits ?? []).map(([key, value]) => ({ key, value })),
      outcome,
      durationMs: o.ms ? Math.round((o.ms[0] + o.ms[1]) / 2) : 5,
      lastRunAt: "2026-10-01T10:00:00.000Z",
    };
    if (o.error && outcome !== "skipped") {
      t.errorType = o.error.type;
      t.errorMessage = o.error.message;
      t.stackTrace = o.error.stack;
    }
    if (o.behavior === "flaky") t.flaky = true;
    if (o.stdOut) t.stdOut = o.stdOut;
    return t;
  });
  const all = huge.specs;
  const step = all.length / Math.min(n, all.length);
  const tests: TestNode[] = [];
  for (let i = 0; i < Math.min(n, all.length); i++) tests.push({ ...all[Math.floor(i * step)] });
  const used = new Set(tests.map((t) => t.projectId));
  const projects = huge.projects.filter((p) => used.has(p.id)).map((p) => ({ ...p }));
  const map = new Map<string, TestNode>();
  for (const t of tests) map.set(testKey(t), t);
  return { projects, tests, map };
}

/** Every `stride`th test of `tests` (a deterministic "changed" subset). */
export const every = <T>(xs: readonly T[], stride: number): T[] => xs.filter((_, i) => i % stride === 0);

/** A full `state` snapshot event of a catalog (what the host sends on connect). */
export function stateEvent(c: Catalog): ServerEvent {
  return {
    type: "state",
    state: {
      projects: c.projects,
      tests: c.tests.map((t) => ({ ...t })),
      watch: false,
      coverageEnabled: false,
      runs: [],
    },
  } as ServerEvent;
}

/** `testsUpdated` with full entries for `tests`, flipped to a new result (structure unchanged). */
export function testsUpdatedEvent(tests: readonly TestNode[], outcome: Outcome = "passed"): ServerEvent {
  return { type: "testsUpdated", tests: tests.map((t) => ({ ...t, outcome, durationMs: (t.durationMs ?? 0) + 1, lastRunAt: "2026-10-01T11:00:00.000Z" })) };
}

/** `testResults` (results-only patches, Round 7) for `tests`: outcome + duration + time, recorded in history. */
export function testResultsEvent(tests: readonly TestNode[], outcome: Outcome = "passed", runId = "run-2"): ServerEvent {
  const byProject = new Map<string, TestResultPatch[]>();
  for (const t of tests) {
    let g = byProject.get(t.projectId);
    if (!g) byProject.set(t.projectId, (g = []));
    g.push({ id: t.id, outcome, durationMs: (t.durationMs ?? 0) + 1, lastRunAt: "2026-10-01T11:00:00.000Z", recorded: true } as TestResultPatch);
  }
  return { type: "testResults", runId, projects: [...byProject].map(([projectId, results]) => ({ projectId, results })) } as ServerEvent;
}

// ---- frames -----------------------------------------------------------------------------------------------------

/**
 * A manual requestAnimationFrame: the store publishes its state once per frame, so a bench "frame" is apply(…) +
 * flushFrames(). Installed by the benches that need it (happy-dom's rAF is timer based).
 */
const frames: FrameRequestCallback[] = [];
export function installManualRaf() {
  globalThis.requestAnimationFrame = (cb) => (frames.push(cb), frames.length);
  globalThis.cancelAnimationFrame = () => {};
}
export function flushFrames() {
  while (frames.length) frames.splice(0).forEach((cb) => cb(performance.now()));
}

// ---- text inputs -------------------------------------------------------------------------------------------------

/** A deterministic PRNG (same sequence every run). */
export function prng(seed = 1) {
  return () => (seed = (seed * 16807) % 2147483647) / 2147483647;
}

const WORDS = ["order", "customer", "invoice", "total", "price", "tax", "shipping", "address", "status", "pending", "created", "updated", "id", "line", "quantity", "discount"];

/** `n` lines of log-like text. */
export function textLines(n: number, seed = 3): string[] {
  const r = prng(seed);
  return Array.from({ length: n }, (_, i) => {
    const len = 4 + Math.floor(r() * 10);
    return `${String(i).padStart(4, "0")} ${Array.from({ length: len }, () => WORDS[Math.floor(r() * WORDS.length)]).join(" ")}`;
  });
}

/** `lines` with ~`rate` of them edited (changed word, inserted or deleted line). */
export function mutateLines(lines: readonly string[], rate = 0.05, seed = 9): string[] {
  const r = prng(seed);
  const out: string[] = [];
  for (const l of lines) {
    const x = r();
    if (x < rate / 3) continue; // deleted
    if (x < (2 * rate) / 3) out.push(l.replace(/\b(\w+)$/, "$1_changed"));
    else out.push(l);
    if (x > 1 - rate / 3) out.push(`inserted ${WORDS[Math.floor(r() * WORDS.length)]}`);
  }
  return out;
}

/** A realistic API response object (orders with line items), ~`orders` × 12 lines when pretty-printed. */
export function jsonDoc(orders: number, seed = 5) {
  const r = prng(seed);
  return {
    page: 1,
    total: orders,
    orders: Array.from({ length: orders }, (_, i) => ({
      id: `ord-${1000 + i}`,
      customer: { id: `c-${Math.floor(r() * 500)}`, name: `${WORDS[i % WORDS.length]} ltd` },
      status: r() < 0.8 ? "shipped" : "pending",
      lines: Array.from({ length: 1 + Math.floor(r() * 3) }, (_, j) => ({ sku: `SKU-${i}-${j}`, qty: 1 + Math.floor(r() * 4), price: Math.round(r() * 10000) / 100 })),
    })),
  };
}

const SGR = ["\u001b[32m", "\u001b[31m", "\u001b[33m", "\u001b[1m", "\u001b[36m", "\u001b[90m"];

/** Console output lines; `colored` of them (0..1) with ANSI SGR colors, like `dotnet test` / logger output. */
export function consoleLines(n: number, colored = 0.5, seed = 11): string[] {
  const r = prng(seed);
  return textLines(n, seed).map((l, i) => {
    if (r() >= colored) return l;
    const c = SGR[i % SGR.length];
    return `${c}info\u001b[0m: Contoso.Billing[0]\n      ${l} ${c}ok\u001b[39m\u001b[22m`.replaceAll("\n", " ");
  });
}
