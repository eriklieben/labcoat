// Soak check: a long watch session, accelerated on the mock backend (?mock=<scenario>&fast: no demo pacing).
// Cycles of watch-triggered partial runs (20-300 tests) plus one full run, with UI activity in between (filter, status
// chips, selecting tests, expand / collapse). After each cycle (forced GC): JS heap, DOM nodes, event listeners,
// live effects / owners (window.__labcoat.stats()), store buffers (logs, runs, history entries). After the warm-up
// cycles every series must stay flat: budgets in ui/perf-budgets.json "soak"; a series that grows at every sample is
// flagged as monotonic growth.
//
// Usage: npm run check:soak         short CI variant: ?mock=huge, 8 cycles × (5 partial + 1 full) = 40 partial + 8 full
//        npm run check:soak:long    local: 50 cycles × (4 partial + 1 full) = 200 partial + 50 full runs
//        node scripts/wc/soak.mjs [--scenario=huge] [--cycles=N] [--partial=N] [--warmup=N] [--heap-snapshots]
//                                 [--dev] [--out=<dir>] [--report-only] [appUrl|port]
// --heap-snapshots writes <out>/soak-warm.heapsnapshot and soak-end.heapsnapshot (open both in DevTools → Memory →
// Comparison to see what grew).
import { join } from "node:path";
import { baseUrlOr, startApp } from "../lib/dev-server.mjs";
import {
  evaluate,
  hookMockEvents,
  launch,
  loadBudgets,
  markdownTable,
  mb,
  memorySample,
  monotonic,
  newPage,
  parseArgs,
  round,
  setPhase,
  startPreview,
  trend,
  uiDir,
  writeHeapSnapshot,
  writeReport,
} from "../lib/perf-kit.mjs";

const args = parseArgs();
const scenario = args.scenario ?? "huge";
const cycles = Number(args.cycles ?? 8);
const partialPerCycle = Number(args.partial ?? 5);
const warmup = Number(args.warmup ?? 2);
const outDir = args.out ?? join(uiDir, "perf-results");
const budgets = loadBudgets().soak.budgets;
const { base, stop } = await baseUrlOr(args._[0], args.dev ? startApp : startPreview);
const browser = await launch();
const { page, cdp, errors } = await newPage(browser);
const t0 = Date.now();
const log = (...a) => console.log(`[soak ${((Date.now() - t0) / 1000).toFixed(0)}s]`, ...a);

await page.goto(`${base}?mock=${scenario}&fast`);
await page.waitForSelector(".tree-row", { timeout: 60000 });
await page.waitForFunction(() => window.__labcoat?.store?.get().loaded && window.__labcoatMock, null, { timeout: 60000 });
await page.waitForTimeout(1500);
await hookMockEvents(page);

const idle = () => page.waitForFunction(() => !window.__labcoat.store.get().currentRun, null, { timeout: 600000, polling: 100 });
const started = (before) => page.waitForFunction((n) => (window.__labcoat.store.get().runs?.[0]?.runId ?? "") !== n || !!window.__labcoat.store.get().currentRun, before, { timeout: 30000, polling: 50 });
const lastRunId = () => page.evaluate(() => window.__labcoat.store.get().runs?.[0]?.runId ?? "");

/** a watch-triggered partial run: 20-300 tests of one project (like the host after a file change) */
async function partialRun(i) {
  const before = await lastRunId();
  await page.evaluate((i) => {
    const s = window.__labcoat.store.get();
    const projects = s.projects.filter((p) => p.status !== "error");
    const p = projects[i % projects.length];
    const ids = [];
    for (const t of s.tests.values()) if (t.projectId === p.id) ids.push(t.id);
    const n = 20 + ((i * 97) % 280);
    const from = (i * 331) % Math.max(1, ids.length - n);
    window.__labcoatMock.simulateWatchRun(p.id, ids.slice(from, from + n));
  }, i);
  await started(before);
  await idle();
}

async function fullRun() {
  const before = await lastRunId();
  await page.evaluate(() => window.__labcoat.store.send({ type: "run" }));
  await started(before);
  await idle();
}

/** UI activity between runs: filter, status chips, select tests, expand / collapse all */
async function uiActivity(i) {
  await page.evaluate(async (i) => {
    const frame = () => new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
    const input = [...document.querySelectorAll("input")].find((x) => /^Filter tests/.test(x.getAttribute("aria-label") ?? ""));
    if (input) {
      for (const q of ["val", "Validate_", "", `is:failed`, ""]) {
        input.value = q;
        input.dispatchEvent(new Event("input", { bubbles: true }));
        await frame();
      }
    }
    const radios = [...document.querySelectorAll('[role="radio"]')];
    for (const name of ["Failed", "All"]) {
      radios.find((r) => (r.textContent ?? "").trim().startsWith(name))?.click();
      await frame();
    }
    const btn = (label) => [...document.querySelectorAll("button")].find((b) => b.getAttribute("aria-label") === label || (b.textContent ?? "").trim() === label);
    btn(i % 2 ? "Collapse all" : "Expand all")?.click();
    await frame();
    const rows = [...document.querySelectorAll(".tree-row")];
    for (let k = 0; k < 3 && rows.length; k++) {
      rows[(i * 7 + k * 5) % rows.length]?.click();
      await frame();
    }
    const sc = document.querySelector(".tree-scroller");
    if (sc) for (let k = 0; k < 20; k++) { sc.scrollTop = ((i * 13 + k) * 977) % Math.max(1, sc.scrollHeight); await frame(); }
  }, i);
}

async function sample(cycle) {
  // the same view for every sample: no filter, status "All", everything expanded, scrolled to the top, no selection
  await page.evaluate(async () => {
    const frame = () => new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
    const input = [...document.querySelectorAll("input")].find((x) => /^Filter tests/.test(x.getAttribute("aria-label") ?? ""));
    if (input) { input.value = ""; input.dispatchEvent(new Event("input", { bubbles: true })); }
    [...document.querySelectorAll('[role="radio"]')].find((r) => (r.textContent ?? "").trim().startsWith("All"))?.click();
    [...document.querySelectorAll("button")].find((b) => b.getAttribute("aria-label") === "Expand all" || (b.textContent ?? "").trim() === "Expand all")?.click();
    await frame();
    const sc = document.querySelector(".tree-scroller");
    if (sc) sc.scrollTop = 0;
    await frame();
  });
  await page.keyboard.press("Escape");
  await page.waitForTimeout(700); // trailing snapshots (run overview: 500 ms) and removal microtasks settle
  const s = await memorySample(page, cdp);
  const buffers = await page.evaluate(() => {
    const st = window.__labcoat.store.get();
    let hist = 0;
    let maxHist = 0;
    for (const t of st.tests.values()) {
      const n = t.history?.length ?? 0;
      hist += n;
      maxHist = Math.max(maxHist, n);
    }
    return { historyEntries: hist, maxHistory: maxHist, logs: st.logs.length, runs: st.runs.length, pastRuns: window.__labcoatMock.pastRuns?.length };
  });
  return { cycle, at: Date.now() - t0, ...s, ...buffers };
}

const samples = [await sample(0)];
log("start", JSON.stringify(samples[0]));
let partialN = 0;
let fullN = 0;
for (let c = 1; c <= cycles; c++) {
  await setPhase(page, c <= warmup ? "warmup" : "soak");
  for (let k = 0; k < partialPerCycle; k++) {
    await partialRun(partialN++);
    if (k % 2 === 0) await uiActivity(partialN);
  }
  await fullRun();
  fullN++;
  await uiActivity(c);
  if (c === warmup && args["heap-snapshots"]) await writeHeapSnapshot(cdp, join(outDir, "soak-warm.heapsnapshot"));
  const s = await sample(c);
  samples.push(s);
  log(`cycle ${c}/${cycles}`, `heap ${mb(s.heapUsed)} MB, nodes ${s.nodes}, listeners ${s.listeners}, effects ${s.effects}, owners ${s.owners}, logs ${s.logs}, runs ${s.runs}, history ${s.historyEntries}`);
}
if (args["heap-snapshots"]) await writeHeapSnapshot(cdp, join(outDir, "soak-end.heapsnapshot"));

// ---- analysis: samples after the warm-up ------------------------------------------------------------------------
const after = samples.filter((s) => s.cycle >= warmup);
const series = (k) => after.map((s) => s[k] ?? 0);
const pctGrowth = (k) => {
  const xs = series(k);
  // fitted trend (robust against one noisy GC sample), in % of the first fitted value
  return round(trend(xs).growth * 100, 1);
};
const m = {
  heapGrowthPct: pctGrowth("heapUsed"),
  domNodeGrowthPct: pctGrowth("nodes"),
  listenerGrowthPct: pctGrowth("listeners"),
  effectGrowth: series("effects").at(-1) - series("effects")[0],
  ownerGrowth: series("owners").at(-1) - series("owners")[0],
};
const long = await page.evaluate(() => window.__perf.long.filter((l) => l.phase === "soak"));
m.runLongestTaskMs = long.length ? Math.max(...long.map((l) => l.d)) : 0;
const watched = ["heapUsed", "nodes", "listeners", "effects", "owners", "historyEntries", "logs", "runs"];
const flagged = watched.filter((k) => monotonic(series(k)));
// buffers that are capped by design (logs 5000, runs 50, history 20 per test) may grow until the cap: only flag them
// when they are still growing at the end AND past their cap
const caps = { logs: 5000, runs: 50, historyEntries: 20 * (after.at(-1)?.tests ?? 0) };
const flaggedReal = flagged.filter((k) => !(k in caps) || series(k).at(-1) > caps[k]);
const { rows, ok } = evaluate(m, budgets);
const monoRow = { key: "monotonic", label: "Series growing at every sample after warm-up", value: flaggedReal.length ? flaggedReal.join(", ") : "none", budget: "none", unit: "", status: flaggedReal.length ? "FAIL" : "pass" };
const allOk = ok && !flaggedReal.length;
const md = [
  `## Labcoat UI soak: \`?mock=${scenario}&fast\`, ${partialN} watch-triggered partial runs + ${fullN} full runs (${((Date.now() - t0) / 60000).toFixed(1)} min)`,
  "",
  markdownTable("Budgets (after " + warmup + " warm-up cycles)", [...rows, monoRow]),
  "",
  "### Samples (after each cycle, forced GC)",
  "",
  "| Cycle | Heap MB | DOM nodes | Listeners | Effects | Owners | Logs | Runs | History entries |",
  "| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
  ...samples.map((s) => `| ${s.cycle}${s.cycle <= warmup ? " (warm-up)" : ""} | ${mb(s.heapUsed)} | ${s.nodes} | ${s.listeners} | ${s.effects} | ${s.owners} | ${s.logs} | ${s.runs} | ${s.historyEntries} |`),
  flagged.length && !flaggedReal.length ? `\nStill growing but within their caps: ${flagged.join(", ")}.` : "",
  errors.length ? `\nPage errors:\n\n${errors.map((e) => `- ${e}`).join("\n")}` : "",
].join("\n");
writeReport(outDir, `soak-${scenario}`, { scenario, cycles, partialRuns: partialN, fullRuns: fullN, metrics: m, flagged, samples, errors }, md);
console.log(md);
await browser.close();
await stop();
process.exit(errors.length || (!allOk && !args["report-only"]) ? 1 : 0);
