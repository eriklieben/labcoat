// Scale check: the UI with 20,000 tests (?mock=huge) or 40 projects × 300 tests (?mock=many-projects), measured against
// ui/perf-budgets.json: initial render, time to interactive, scroll frame times, filter / status-filter response,
// selection, long tasks while a full run streams its results, event throughput and memory after the run.
//
// Usage: npm run check:perf:huge            (scenario huge)
//        npm run check:perf:many-projects   (scenario many-projects)
//        node scripts/wc/perf-scale.mjs [--scenario=huge|many-projects] [--repeat=N] [--dev] [--out=<dir>] [--report-only] [appUrl|port]
// --repeat=N measures N times (fresh page each) and compares the median of each metric with the budget (CI uses 3).
// Long tasks that contain a bulk update (run start / finish: thousands of tests in one event) are reported separately
// from the ones while results stream in. Report: <out>/perf-<scenario>.md + .json (default ui/perf-results/).
// Without a URL it builds the UI into a temp dir and serves it with `vite preview` (production timings); --dev uses the
// Vite dev server instead. Exit code 1 when a budget fails (unless --report-only) or the page throws.
import { join } from "node:path";
import { baseUrlOr, startApp } from "../lib/dev-server.mjs";
import {
  evaluate,
  frameStats,
  hookMockEvents,
  launch,
  loadBudgets,
  longTasksByPhase,
  markdownTable,
  mb,
  memorySample,
  newPage,
  parseArgs,
  pct,
  round,
  setPhase,
  splitBulk,
  startPreview,
  uiDir,
  writeReport,
} from "../lib/perf-kit.mjs";

const args = parseArgs();
const scenario = args.scenario ?? "huge";
const budgetsAll = loadBudgets();
const conf = budgetsAll.scenarios[scenario];
if (!conf) throw new Error(`unknown scenario ${scenario} (perf-budgets.json: ${Object.keys(budgetsAll.scenarios).join(", ")})`);
const outDir = args.out ?? join(uiDir, "perf-results");
const { base, stop } = await baseUrlOr(args._[0], args.dev ? startApp : startPreview);
const repeat = Math.max(1, Number(args.repeat ?? 1));
const log = (...a) => console.log(`[${scenario}]`, ...a);
const browser = await launch();
const runs = [];
for (let i = 0; i < repeat; i++) runs.push(await measureOnce(i));

// median per metric over the repetitions (each budget then compares a typical value, not one noisy sample)
const median = (xs) => { const v = xs.filter((x) => typeof x === "number").sort((a, b) => a - b); return v.length ? v[(v.length - 1) >> 1] : undefined; };
const m = {};
for (const k of Object.keys(runs[0].m)) m[k] = median(runs.map((r) => r.m[k]));
const { long, ev, s0, s1 } = runs[runs.length - 1];
const errors = runs.flatMap((r) => r.errors);
const { rows, ok } = evaluate(m, conf.budgets);
const md = [
  `## Labcoat UI scale check: \`?mock=${scenario}\` (${conf.tests.toLocaleString("en")} tests${repeat > 1 ? `, median of ${repeat} runs` : ""})`,
  "",
  markdownTable("Budgets", rows),
  "",
  `### Long tasks (> 50 ms) by phase${repeat > 1 ? " (last run)" : ""}`,
  "",
  "| Phase | Count | Longest (ms) | Total (ms) |",
  "| --- | ---: | ---: | ---: |",
  ...Object.entries(longTasksByPhase(long)).map(([p, b]) => `| ${p} | ${b.n} | ${b.max} | ${b.total} |`),
  "",
  `Other: expand all ${m.expandAllMs} ms, collapse all ${m.collapseAllMs} ms, scroll height ${m.scrollHeightPx} px, run wall time ${(m.runWallMs / 1000).toFixed(1)} s, ` +
    `${ev.events} test update events (${ev.tests} test updates, ${m.runUpdatesKB} KB: ${JSON.stringify(runs[runs.length - 1].m.runUpdatesByType)}), heap ${m.heapAfterLoadMB} → ${m.heapAfterRunMB} MB, ${m.domNodes} DOM nodes, ${m.effects} effects / ${m.owners} owners.`,
  errors.length ? `\nPage errors:\n\n${errors.map((e) => `- ${e}`).join("\n")}` : "",
].join("\n");
writeReport(outDir, `perf-${scenario}`, { scenario, base, at: new Date().toISOString(), metrics: m, runs: runs.map((r) => r.m), budgets: rows, longTasks: long, events: ev, memory: { afterLoad: s0, afterRun: s1 }, errors }, md);
console.log(md);
await browser.close();
await stop();
process.exit(errors.length || (!ok && !args["report-only"]) ? 1 : 0);

async function measureOnce(rep) {
const { ctx, page, cdp, errors } = await newPage(browser);
const m = {}; // metrics
if (repeat > 1) log(`repetition ${rep + 1}/${repeat}`);
// ---- load -------------------------------------------------------------------------------------------------------
await page.goto(`${base}?mock=${scenario}`);
await page.waitForSelector(".tree-row", { timeout: 60000 });
await page.waitForFunction((n) => window.__labcoat?.store?.get().tests.size >= n, conf.tests, { timeout: 60000 });
const firstRow = await page.evaluate(() => window.__perf.firstRow);
// The in-browser mock generates its 20k tests + history in the page (a real host does that on the server): that time is
// reported (mockSetupMs) but not counted in the initial render. uiRenderMs: state snapshot handed over → rows painted.
// The first state snapshot's JSON.stringify (host-side work) is left out too; its JSON.parse stays in.
const marks = await page.evaluate(() => {
  const ser = performance.getEntriesByName("labcoat:mock-serialize")[0];
  return {
    setup: performance.getEntriesByName("labcoat:mock-setup")[0]?.duration ?? 0,
    serialize: ser?.duration ?? 0,
    // the snapshot is handed over right after it was serialized
    state: ser ? ser.startTime + ser.duration : performance.getEntriesByName("labcoat:mock-state")[0]?.startTime,
  };
});
m.mockSetupMs = round(marks.setup + marks.serialize, 0);
m.initialRenderMs = round(firstRow - marks.setup - marks.serialize, 0);
m.uiRenderMs = marks.state != null ? round(firstRow - marks.state, 0) : undefined;
// time to interactive: the end of the last long task before a 1 s quiet window (or the first row, if later)
const quietSince = Date.now();
for (;;) {
  const { lastEnd, now } = await page.evaluate(() => {
    const l = window.__perf.long.filter((x) => x.phase === "load");
    const last = l[l.length - 1];
    return { lastEnd: last ? last.start + last.d : 0, now: performance.now() };
  });
  if (now - lastEnd >= 1000 || Date.now() - quietSince > 20000) {
    m.ttiMs = round(Math.max(lastEnd, firstRow) - m.mockSetupMs, 0);
    break;
  }
  await page.waitForTimeout(200);
}
await hookMockEvents(page);
m.loadLongestTaskMs = (await page.evaluate(() => window.__perf.long.filter((x) => x.phase === "load").reduce((a, x) => Math.max(a, x.d), 0))) || 0;
const s0 = await memorySample(page, cdp);
m.heapAfterLoadMB = mb(s0.heapUsed);
log("loaded", { firstRow: m.initialRenderMs, tti: m.ttiMs, tests: s0.tests, rows: s0.rows, heapMB: m.heapAfterLoadMB });

/** ms from running `action` in the page to the frame after the resulting DOM update was painted */
const timed = (fnSource, arg) =>
  page.evaluate(
    async ({ fnSource, arg }) => {
      const fn = new Function("arg", fnSource);
      const t0 = performance.now();
      fn(arg);
      await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
      return performance.now() - t0;
    },
    { fnSource, arg },
  );

// ---- expand all + scroll ----------------------------------------------------------------------------------------
await setPhase(page, "expand");
m.expandAllMs = round(await timed(`[...document.querySelectorAll("button")].find((b) => b.getAttribute("aria-label") === "Expand all" || b.title?.startsWith("Expand all") || b.textContent.trim() === "Expand all").click()`), 0);
await page.waitForTimeout(300);
m.scrollHeightPx = await page.evaluate(() => document.querySelector(".tree-scroller")?.scrollHeight);
await setPhase(page, "scroll");
const scroll = await page.evaluate(async () => {
  const s = document.querySelector(".tree-scroller");
  const step = async (dy, n) => {
    const out = [];
    let last = performance.now();
    for (let i = 0; i < n; i++) {
      s.scrollTop += dy;
      await new Promise((r) => requestAnimationFrame(r));
      const t = performance.now();
      out.push(t - last);
      last = t;
    }
    return out;
  };
  const steady = await step(240, 240); // ~10 rows per frame
  const jumps = [];
  let last = performance.now();
  for (let i = 0; i < 60; i++) {
    s.scrollTop = ((i * 7919) % 100) / 100 * s.scrollHeight; // random jumps (scrollbar drag / page down)
    await new Promise((r) => requestAnimationFrame(r));
    const t = performance.now();
    jumps.push(t - last);
    last = t;
  }
  s.scrollTop = 0;
  await new Promise((r) => requestAnimationFrame(r));
  return { steady, jumps };
});
const steady = frameStats(scroll.steady);
const jumps = frameStats(scroll.jumps);
m.scrollP95Ms = steady.p95;
m.scrollMaxMs = steady.max;
m.scrollJumpP95Ms = jumps.p95;
log("scroll", { steady, jumps });

// ---- filter -----------------------------------------------------------------------------------------------------
await setPhase(page, "filter");
const filterSrc = `const el = document.querySelector('input[aria-label="Filter tests"]') ?? [...document.querySelectorAll("input")].find((i) => /^Filter tests/.test(i.getAttribute("aria-label") ?? ""));
  el.value = arg; el.dispatchEvent(new Event("input", { bubbles: true }));`;
const queries = conf.filterQueries ?? ["o", "or", "ord", "orde", "order", "", "Validate_Customer", "", "Api.Commands", "", "zz-no-match", ""];
const filterTimes = [];
for (const q of queries) {
  filterTimes.push(await timed(filterSrc, q));
  await page.waitForTimeout(50);
}
m.filterP95Ms = round(pct(filterTimes, 95), 0);
m.filterMaxMs = round(Math.max(...filterTimes), 0);
// status chips (radios in the summary bar)
const chipTimes = [];
for (const name of ["Failed", "Passed", "Skipped", "Not run", "All"]) {
  const t = await page.evaluate(async (name) => {
    const chip = [...document.querySelectorAll('[role="radio"]')].find((r) => (r.textContent ?? "").trim().startsWith(name) || (r.getAttribute("aria-label") ?? "").startsWith(name));
    if (!chip) return undefined;
    const t0 = performance.now();
    chip.click();
    await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
    return performance.now() - t0;
  }, name);
  if (t != null) chipTimes.push(t);
  await page.waitForTimeout(50);
}
m.statusFilterMaxMs = chipTimes.length ? round(Math.max(...chipTimes), 0) : undefined;
log("filter", { filter: filterTimes.map((x) => round(x, 0)), chips: chipTimes.map((x) => round(x, 0)) });

// ---- select a test ----------------------------------------------------------------------------------------------
await setPhase(page, "select");
const selTimes = [];
for (let i = 0; i < 5; i++) {
  const t = await page.evaluate(async (i) => {
    const rows = [...document.querySelectorAll(".tree-row")].filter((r) => !r.getAttribute("aria-expanded"));
    const row = rows[(i * 3) % Math.max(1, rows.length)];
    if (!row) return undefined;
    const t0 = performance.now();
    row.click();
    await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
    return performance.now() - t0;
  }, i);
  if (t != null) selTimes.push(t);
  await page.waitForTimeout(150);
}
m.selectMaxMs = selTimes.length ? round(Math.max(...selTimes), 0) : undefined;

// ---- collapse all / expand all ----------------------------------------------------------------------------------
await setPhase(page, "collapse");
m.collapseAllMs = round(await timed(`[...document.querySelectorAll("button")].find((b) => b.getAttribute("aria-label") === "Collapse all" || b.title?.startsWith("Collapse all") || b.textContent.trim() === "Collapse all").click()`), 0);
await page.waitForTimeout(200);

// ---- full run, results streaming --------------------------------------------------------------------------------
await setPhase(page, "run");
await page.evaluate(() => window.__labcoatMock && (window.__labcoatMock.wire = {}));
const runStart = Date.now();
await page.getByRole("button", { name: "Run all" }).click();
await page.waitForFunction(() => !!window.__labcoat.store.get().currentRun, null, { timeout: 30000 });
const streamFrames = await page.evaluate(async () => {
  const out = [];
  let last = performance.now();
  for (;;) {
    await new Promise((r) => requestAnimationFrame(r));
    const t = performance.now();
    out.push(t - last);
    last = t;
    if (!window.__labcoat.store.get().currentRun) break;
    if (out.length > 60 * 600) break; // 10 min guard
  }
  return out;
});
m.runWallMs = Date.now() - runStart;
await page.waitForTimeout(800);
const { runLong, bulkTimes } = await page.evaluate(() => ({ runLong: window.__perf.long.filter((x) => x.phase === "run"), bulkTimes: window.__perf.bulk }));
const split = splitBulk(runLong, bulkTimes);
const sf = frameStats(streamFrames);
const maxD = (xs) => (xs.length ? Math.max(...xs.map((x) => x.d)) : 0);
m.streamingLongestTaskMs = maxD(split.streaming);
m.streamingLongTasks = split.streaming.length;
m.bulkUpdateLongestTaskMs = maxD(split.bulk);
m.streamingFrameP95Ms = sf.p95;
const ev = await page.evaluate(() => {
  const u = window.__perf.ws.updates ?? [];
  if (u.length < 2) return { events: u.length, tests: u.reduce((a, x) => a + x.tests, 0) };
  const dur = (u[u.length - 1].t - u[0].t) / 1000;
  const tests = u.reduce((a, x) => a + x.tests, 0);
  return { events: u.length, tests, perSec: u.length / dur, testsPerSec: tests / dur };
});
m.testsUpdatedPerSec = round(ev.perSec, 1);
// JSON characters of the run's test updates (testsUpdated + testResults), as the host would send them
const wire = await page.evaluate(() => window.__labcoatMock?.wire ?? {});
m.runUpdatesKB = round(Object.values(wire).reduce((a, x) => a + x, 0) / 1024, 0);
m.runUpdatesByType = Object.fromEntries(Object.entries(wire).map(([k, v]) => [k, round(v / 1024, 0)]));
m.testResultsPerSec = round(ev.testsPerSec, 0);
log("run", { wallMs: m.runWallMs, frames: sf, streaming: split.streaming.map((x) => x.d), bulk: split.bulk.map((x) => x.d), events: ev });

// ---- memory after the run ---------------------------------------------------------------------------------------
await setPhase(page, "idle");
const s1 = await memorySample(page, cdp);
m.heapAfterRunMB = mb(s1.heapUsed);
m.domNodes = s1.nodes;
m.effects = s1.effects;
m.owners = s1.owners;
log("memory", s1);

const long = await page.evaluate(() => window.__perf.long);
await ctx.close();
return { m, long, ev, s0, s1, errors };
}
