// Shared helpers for the scale / soak checks (scripts/wc/perf-scale.mjs, scripts/wc/soak.mjs, eng/perf-real.mjs):
// a Chromium page with probes (long tasks, rAF frame times, WebSocket / mock event counters), CDP memory readings,
// percentiles, budget evaluation against ui/perf-budgets.json and a Markdown + JSON report.
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(new URL("../../package.json", import.meta.url));
export const { chromium } = require("playwright");
export const uiDir = fileURLToPath(new URL("../..", import.meta.url));

export const loadBudgets = () => JSON.parse(readFileSync(join(uiDir, "perf-budgets.json"), "utf8"));

/** `--name=value` / `--flag` arguments → object; positional arguments in `_`. */
export function parseArgs(argv = process.argv.slice(2)) {
  const out = { _: [] };
  for (const a of argv) {
    const m = /^--([^=]+)(?:=(.*))?$/.exec(a);
    if (m) out[m[1]] = m[2] ?? true;
    else out._.push(a);
  }
  return out;
}

export const pct = (xs, p) => {
  if (!xs.length) return 0;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.max(0, Math.ceil((p / 100) * s.length) - 1))];
};
export const round = (x, d = 1) => (x == null || Number.isNaN(x) ? x : Math.round(x * 10 ** d) / 10 ** d);
export const mb = (bytes) => round(bytes / 1048576, 1);

/**
 * Probes installed before any page script:
 *   window.__perf.long          [{ phase, start, d }]     long tasks (> 50 ms)
 *   window.__perf.firstRow      ms since navigation when the first .tree-row was in the DOM
 *   window.__perf.ws            { messages, bytes, maxBytes, byType: { type: { n, bytes, tests, handleMs } } }   (real host)
 *                               handleMs: time in the transport's message handler (JSON.parse + store.apply), per type
 *   window.__perf.frames(ms)    resolves with rAF frame intervals sampled for `ms`
 */
export function probeScript() {
  window.__phase = "load";
  // bulk: times of events that carry thousands of tests at once (run start / finish, snapshots): a long task containing
  // one is a "bulk update" task, every other long task during a run is a "streaming" task
  const P = (window.__perf = { long: [], bulk: [], firstRow: undefined, ws: { messages: 0, bytes: 0, maxBytes: 0, byType: {} } });
  try {
    new PerformanceObserver((l) => {
      for (const e of l.getEntries()) P.long.push({ phase: window.__phase, start: Math.round(e.startTime), d: Math.round(e.duration) });
    }).observe({ type: "longtask", buffered: true });
  } catch {}
  const seeRow = () => {
    if (P.firstRow === undefined && document.querySelector(".tree-row")) P.firstRow = performance.now();
    else if (P.firstRow === undefined) requestAnimationFrame(seeRow);
  };
  requestAnimationFrame(seeRow);
  P.frames = (ms) =>
    new Promise((resolve) => {
      const out = [];
      let last = performance.now();
      const end = last + ms;
      const tick = (t) => {
        out.push(t - last);
        last = t;
        if (t < end) requestAnimationFrame(tick);
        else resolve(out);
      };
      requestAnimationFrame(tick);
    });
  // count / size WebSocket messages (real host)
  const count = (data) => {
    const bytes = typeof data === "string" ? data.length : data.byteLength ?? data.size ?? 0;
    P.ws.messages++;
    P.ws.bytes += bytes;
    P.ws.maxBytes = Math.max(P.ws.maxBytes, bytes);
    let type = "?";
    let tests = 0;
    if (typeof data === "string") {
      const m = /"type"\s*:\s*"(\w+)"/.exec(data.slice(0, 200));
      if (m) type = m[1];
      if (type === "testsUpdated") tests = (data.match(/"projectId"/g) || []).length;
      // Round 7 result patches: one {"id":…} per test (the grouping objects carry projectId, not id)
      else if (type === "testResults") tests = (data.match(/\{"id"/g) || []).length;
    }
    const b = (P.ws.byType[type] ??= { n: 0, bytes: 0, tests: 0, maxBytes: 0 });
    b.n++;
    b.bytes += bytes;
    b.tests += tests;
    b.maxBytes = Math.max(b.maxBytes, bytes);
    if (type === "testsUpdated" || type === "testResults") (P.ws.updates ??= []).push({ t: performance.now(), tests });
    if (tests >= 2000 || type === "state" || bytes > 1_000_000) P.bulk.push(performance.now());
  };
  const Orig = window.WebSocket;
  if (Orig) {
    const onmessage = Object.getOwnPropertyDescriptor(Orig.prototype, "onmessage");
    window.WebSocket = class extends Orig {
      constructor(...a) {
        super(...a);
        this.addEventListener("message", (e) => count(e.data));
      }
      // time the app's handler (parse + store.apply; the per-frame publish is in the long tasks)
      set onmessage(fn) {
        onmessage.set.call(this, (e) => {
          const t0 = performance.now();
          try {
            return fn.call(this, e);
          } finally {
            const m = typeof e.data === "string" ? /"type"\s*:\s*"(\w+)"/.exec(e.data.slice(0, 200)) : null;
            const b = P.ws.byType[m?.[1] ?? "?"];
            if (b) b.handleMs = Math.round(((b.handleMs ?? 0) + performance.now() - t0) * 10) / 10;
          }
        });
      }
      get onmessage() {
        return onmessage.get.call(this);
      }
    };
  }
}

/** Mock backend: count the events the MockTransport hands to the store (same shape as __perf.ws). */
export async function hookMockEvents(page) {
  await page.evaluate(() => {
    const m = window.__labcoatMock;
    const P = window.__perf;
    if (!m?.h || m.__hooked) return;
    m.__hooked = true;
    const orig = m.h.onEvent;
    m.h.onEvent = (e) => {
      P.ws.messages++;
      const n = e.type === "testsUpdated" ? e.tests.length : e.type === "testResults" ? e.projects.reduce((a, g) => a + g.results.length, 0) : 0;
      if (e.type === "state" || n >= 2000) P.bulk.push(performance.now());
      const b = (P.ws.byType[e.type] ??= { n: 0, bytes: 0, tests: 0, maxBytes: 0 });
      b.n++;
      if (e.type === "testsUpdated" || e.type === "testResults") {
        b.tests += n;
        (P.ws.updates ??= []).push({ t: performance.now(), tests: n });
      }
      const t0 = performance.now();
      try {
        return orig(e);
      } finally {
        b.handleMs = Math.round(((b.handleMs ?? 0) + performance.now() - t0) * 10) / 10;
      }
    };
  });
}

export async function launch({ headless = true } = {}) {
  // precise memory info: performance.memory without the 100 KB bucketing / delay
  return chromium.launch({ headless, args: ["--enable-precise-memory-info", "--js-flags=--expose-gc"] });
}

export async function newPage(browser, { viewport = { width: 1440, height: 900 }, storage = {} } = {}) {
  const ctx = await browser.newContext({ viewport, colorScheme: "dark" });
  await ctx.addInitScript((storage) => {
    try {
      localStorage.clear();
      for (const [k, v] of Object.entries(storage)) localStorage.setItem(k, v);
    } catch {}
  }, { "mste.theme": "dark", "mste.logOpen": "false", ...storage });
  await ctx.addInitScript(probeScript);
  const page = await ctx.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  const cdp = await ctx.newCDPSession(page);
  await cdp.send("Performance.enable");
  return { ctx, page, cdp, errors };
}

export const setPhase = (page, p) => page.evaluate((p) => (window.__phase = p), p);

/** After a forced GC: JS heap used, DOM counters, live effect/owner counts, tree rows in the DOM. */
export async function memorySample(page, cdp, { gc = true } = {}) {
  if (gc) await cdp.send("HeapProfiler.collectGarbage").catch(() => {});
  const heap = await cdp.send("Runtime.getHeapUsage");
  const dom = await cdp.send("Memory.getDOMCounters").catch(() => ({}));
  const app = await page.evaluate(() => ({
    ...(window.__labcoat?.stats?.() ?? {}),
    rows: document.querySelectorAll(".tree-row").length,
    elements: document.getElementsByTagName("*").length,
    logs: window.__labcoat?.store?.get?.().logs?.length,
    runs: window.__labcoat?.store?.get?.().runs?.length,
    tests: window.__labcoat?.store?.get?.().tests?.size,
  }));
  return { heapUsed: heap.usedSize, heapTotal: heap.totalSize, nodes: dom.nodes, listeners: dom.jsEventListeners, documents: dom.documents, ...app };
}

export async function writeHeapSnapshot(cdp, file) {
  mkdirSync(dirname(file), { recursive: true });
  const chunks = [];
  const onChunk = (e) => chunks.push(e.chunk);
  cdp.on("HeapProfiler.addHeapSnapshotChunk", onChunk);
  await cdp.send("HeapProfiler.takeHeapSnapshot", { reportProgress: false });
  cdp.off("HeapProfiler.addHeapSnapshotChunk", onChunk);
  writeFileSync(file, chunks.join(""));
}

/** Frame stats from rAF intervals (the first sample is dropped: it includes the wait for the first frame). */
export function frameStats(intervals) {
  const xs = intervals.slice(1);
  return { n: xs.length, p50: round(pct(xs, 50)), p95: round(pct(xs, 95)), max: round(Math.max(0, ...xs)), over50: xs.filter((x) => x > 50).length };
}

export function longTasksByPhase(long) {
  const out = {};
  for (const l of long) {
    const b = (out[l.phase] ??= { n: 0, max: 0, total: 0 });
    b.n++;
    b.max = Math.max(b.max, l.d);
    b.total += l.d;
  }
  return out;
}

/**
 * Compare `metrics` against `budgets` ({ key: { max | min, unit, label } }). A metric that is missing is "n/a"
 * (not a failure). Returns rows for the report and whether everything is within budget.
 */
export function evaluate(metrics, budgets) {
  const rows = [];
  let ok = true;
  for (const [key, b] of Object.entries(budgets)) {
    if (key.startsWith("$")) continue;
    const v = metrics[key];
    let status = "n/a";
    if (v != null && !Number.isNaN(v)) {
      const pass = b.max != null ? v <= b.max : b.min != null ? v >= b.min : true;
      status = pass ? "pass" : b.warnOnly ? "warn" : "FAIL";
      if (status === "FAIL") ok = false;
    }
    rows.push({ key, label: b.label ?? key, value: v, budget: b.max != null ? `≤ ${b.max}` : b.min != null ? `≥ ${b.min}` : "", unit: b.unit ?? "", status });
  }
  return { rows, ok };
}

export function markdownTable(title, rows) {
  const lines = [`### ${title}`, "", "| Metric | Value | Budget | Status |", "| --- | ---: | ---: | --- |"];
  for (const r of rows) lines.push(`| ${r.label} | ${r.value == null ? "–" : r.value}${r.unit && r.value != null ? ` ${r.unit}` : ""} | ${r.budget}${r.unit && r.budget ? ` ${r.unit}` : ""} | ${r.status === "FAIL" ? "**FAIL**" : r.status} |`);
  return lines.join("\n");
}

/** Write `<outDir>/<name>.json` and `<name>.md`, append the Markdown to $GITHUB_STEP_SUMMARY when set. */
export function writeReport(outDir, name, json, md) {
  mkdirSync(outDir, { recursive: true });
  writeFileSync(join(outDir, `${name}.json`), JSON.stringify(json, null, 2));
  writeFileSync(join(outDir, `${name}.md`), md + "\n");
  if (process.env.GITHUB_STEP_SUMMARY) {
    try {
      writeFileSync(process.env.GITHUB_STEP_SUMMARY, md + "\n\n", { flag: "a" });
    } catch {}
  }
  console.log(`report: ${join(outDir, `${name}.md`)}`);
}

/** Least-squares slope of ys over xs (growth per step) and the relative growth first → last of the fitted line. */
export function trend(ys) {
  const n = ys.length;
  if (n < 2) return { slope: 0, growth: 0 };
  const xm = (n - 1) / 2;
  const ym = ys.reduce((a, b) => a + b, 0) / n;
  let num = 0;
  let den = 0;
  ys.forEach((y, i) => {
    num += (i - xm) * (y - ym);
    den += (i - xm) ** 2;
  });
  const slope = num / den;
  const first = ym - slope * xm;
  const last = ym + slope * xm;
  return { slope, growth: first > 0 ? (last - first) / first : 0 };
}

/** True when every step is >= the previous one (and the series grew at all): monotonic growth. */
export const monotonic = (ys) => ys.length > 2 && ys.every((y, i) => i === 0 || y >= ys[i - 1]) && ys[ys.length - 1] > ys[0];

/**
 * A production build of the UI in a temp directory (never src/Labcoat/wwwroot: `configFile: false`, explicit outDir),
 * served by `vite preview`. Timings then match what users get (bundled, minified), not the dev server's module graph.
 * Port: $LABCOAT_CHECK_PORT (default 5300, next free one when taken). `--dev` in the scripts uses the dev server instead.
 * LABCOAT_PERF_MINIFY=0 keeps function names readable (for CPU profiles of the production build).
 */
export async function startPreview({ minify = process.env.LABCOAT_PERF_MINIFY !== "0" } = {}) {
  const { build, preview } = await import("vite");
  const { mkdtempSync, rmSync } = await import("node:fs");
  const { tmpdir } = await import("node:os");
  const outDir = mkdtempSync(join(tmpdir(), "labcoat-perf-ui-"));
  const common = { configFile: false, root: uiDir, base: "./", logLevel: "error" };
  await build({ ...common, build: { outDir, emptyOutDir: true, minify, chunkSizeWarningLimit: 4000, reportCompressedSize: false } });
  const port = Number(process.env.LABCOAT_CHECK_PORT ?? 5300);
  const server = await preview({ ...common, build: { outDir }, preview: { port, strictPort: false, open: false } });
  const url = server.resolvedUrls?.local?.[0] ?? `http://localhost:${port}/`;
  return {
    url: url.endsWith("/") ? url : url + "/",
    close: async () => {
      await new Promise((r) => server.httpServer.close(r));
      rmSync(outDir, { recursive: true, force: true });
    },
  };
}

/** Split long tasks into those that contain a bulk event (run start / finish with thousands of tests) and the rest. */
export function splitBulk(long, bulk) {
  // the event's own task, or the next frame's (the store publishes once per animation frame: tree build, rendering)
  const isBulk = (l) => bulk.some((t) => t >= l.start - 150 && t <= l.start + l.d + 1);
  return { bulk: long.filter(isBulk), streaming: long.filter((l) => !isBulk(l)) };
}
