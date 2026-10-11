// Tree stress test: random watch / partial / full runs, cancels, rediscovers, expand / collapse (incl. FLIP toggles),
// keyboard navigation, selection, filters, status chips, sort / group / flat switches and scrolling against the mock
// backend (?mock and ?mock=large), while an in-page checker asserts the tree's invariants on every animation frame:
//
//   - the rows (<lc-tree-pane>.rows) equal a full, non-incremental build of the published store state, flattened
//     with the pane's own expansion / filter / sort state (same keys, order, depth, aggregates);
//   - the rendered rows are exactly the virtual window for the current scroll position (+ the pinned active row):
//     same keys, same order, no duplicates, no extras, no gaps; each row's translate is index × row height;
//   - when no animation is running on a row: its box is at index × row height, fully opaque, visible, and it is the
//     element hit at its own center (nothing stacked on it);
//   - the window (start / end) follows the real scrollTop / clientHeight within 2 frames (no stale window).
//
// Usage: npm run check:tree-stress                        (starts a dev server; 500 iterations)
//        node scripts/tree-stress.mjs [url|port]          (an already running dev server)
//        TREE_STRESS_N=2000 TREE_STRESS_SEED=7 node scripts/tree-stress.mjs 5380
// Exits 1 on the first page with invariant violations (they are printed with the last actions).
import { createRequire } from "node:module";
import { baseUrlOr, startApp } from "./lib/dev-server.mjs";
import { install, settledCheck } from "./lib/tree-invariants.mjs";
const require = createRequire(new URL("../package.json", import.meta.url));
const { chromium } = require("playwright");

const N = Number(process.env.TREE_STRESS_N ?? 500);
let seed = Number(process.env.TREE_STRESS_SEED ?? Date.now() % 100000);
const { base, stop } = await baseUrlOr(process.argv[2], startApp);
console.log(`tree-stress: ${N} iterations, seed ${seed}, ${base}`);

const rand = () => {
  seed = (seed + 0x6d2b79f5) | 0;
  let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const pick = (a) => a[Math.floor(rand() * a.length)];
const between = (a, b) => a + Math.floor(rand() * (b - a));

const FILTERS = ["", "", "", "calc", "divide", "order", "is:failed", "Multiply", "zzz", "Tests", "is:passed"];
const STATUSES = ["all", "all", "all", "failed", "passed", "notRun", "skipped", "flaky"];

/** Open a toolbar menu (by its button's label prefix) and pick a radio item. */
async function menuPick(page, button, item) {
  await page.locator(`[aria-label^="${button}"]`).first().click();
  await page.getByRole("menuitemradio", { name: new RegExp(`^${item}`) }).first().click({ timeout: 2000 }).catch(() => page.keyboard.press("Escape"));
}

/** One random action. Returns its name (kept in the page's action log for violation reports). */
async function act(page) {
  const name = pick([
    "watchRun", "watchRun", "watchRun", "docsFlow", "partialRun", "partialRun", "runAll", "cancel", "rediscover",
    "toggle", "toggle", "toggle", "toggle", "keys", "keys", "select", "select", "expandAll", "collapseAll",
    "filter", "status", "sort", "group", "flat", "scroll", "scroll", "scroll", "wheel", "wait",
  ]);
  const arg = rand();
  await page.evaluate(({ name, arg }) => window.__ts.actions.push(`${name}:${arg.toFixed(3)}`), { name, arg });
  switch (name) {
    case "watchRun":
      // the mock's watch tick (bypasses the shell's "user run" timestamp, so the run gets trigger "watch")
      await page.evaluate((arg) => {
        const store = window.__labcoat.store;
        if (!store.get().watch) store.send({ type: "setWatch", enabled: true });
        const pids = store.get().projects.filter((p) => p.status !== "error").map((p) => p.id);
        if (!store.get().currentRun && pids.length) store.transport.startRun(pids[Math.floor(arg * pids.length)] ?? pids[0]);
      }, arg);
      break;
    case "partialRun":
      await page.evaluate((arg) => {
        const store = window.__labcoat.store;
        const tests = [...store.get().tests.values()].filter((t) => arg < 0.5 || t.className === [...store.get().tests.values()][Math.floor(arg * store.get().tests.size)]?.className);
        const sub = tests.filter((_, i) => (i * 7919 + Math.floor(arg * 1000)) % 5 === 0).slice(0, 40);
        if (sub.length && !store.get().currentRun) store.send({ type: "run", projectId: sub[0].projectId, testIds: sub.filter((t) => t.projectId === sub[0].projectId).map((t) => t.id) });
      }, arg);
      break;
    case "runAll":
      await page.evaluate(() => !window.__labcoat.store.get().currentRun && window.__labcoat.store.send({ type: "run" }));
      break;
    case "cancel":
      await page.evaluate(() => window.__labcoat.store.get().currentRun && window.__labcoat.store.send({ type: "cancel" }));
      break;
    case "rediscover":
      await page.evaluate(() => !window.__labcoat.store.get().currentRun && window.__labcoat.store.send({ type: "discover" }));
      break;
    case "toggle": {
      // a real click on a twisty: the user toggle path (FLIP when motion is on)
      const tw = page.locator(".tree-row .twisty.has");
      const n = await tw.count();
      if (n) await tw.nth(Math.floor(arg * n)).click({ timeout: 2000 }).catch(() => {});
      break;
    }
    case "keys": {
      await page.locator(".tree-scroller").focus();
      const keys = ["ArrowDown", "ArrowDown", "ArrowUp", "ArrowRight", "ArrowLeft", " ", "PageDown", "PageUp", "End", "Home"];
      for (let i = 0, k = 1 + Math.floor(arg * 6); i < k; i++) await page.keyboard.press(pick(keys));
      break;
    }
    case "select": {
      const rows = page.locator(".tree-row");
      const n = await rows.count();
      if (n) await rows.nth(Math.floor(arg * n)).click({ position: { x: 60, y: 10 }, timeout: 2000 }).catch(() => {});
      break;
    }
    case "expandAll":
      await page.getByRole("button", { name: "Expand all" }).click();
      break;
    case "collapseAll":
      await page.getByRole("button", { name: "Collapse all" }).click();
      break;
    case "filter":
      await page.getByLabel(/^Filter tests/).fill(pick(FILTERS));
      break;
    case "status":
      await page.evaluate((s) => document.querySelector("lc-tree-pane").setStatus(s), pick(STATUSES));
      break;
    case "sort":
      await menuPick(page, "Sort tests", pick(["Default order", "Default order", "Slowest first", "Fastest first"]));
      break;
    case "group":
      await menuPick(page, "Group tests", pick(["Namespace", "Namespace", "Namespace", "Project", "Trait", "Outcome"]));
      break;
    case "flat":
      // the flat / tree toggle shows with the Failed / Flaky / New chips
      await page.getByRole("button", { name: /^Show as (tree|flat list)$/ }).click({ timeout: 1000 }).catch(() => {});
      break;
    case "scroll":
      await page.evaluate((arg) => {
        const s = document.querySelector(".tree-scroller");
        s.scrollTop = arg < 0.3 ? 0 : arg * (s.scrollHeight - s.clientHeight);
      }, arg);
      break;
    case "wheel": {
      const box = await page.locator(".tree-scroller").boundingBox();
      await page.mouse.move(box.x + 120, box.y + 120);
      for (let i = 0, k = 1 + Math.floor(arg * 8); i < k; i++) await page.mouse.wheel(0, arg < 0.5 ? 180 : -180);
      break;
    }
    case "docsFlow": {
      // what the docs screenshots do before the watch shot (where a CalculatorTests row went "missing"): rediscover
      // twice, run all, then a watch run, each waited for; many full tree builds in one long-lived page
      const idle = () =>
        page.waitForFunction(() => { const s = window.__labcoat.store.get(); return !s.currentRun && !s.projects.some((p) => p.status === "discovering" || p.status === "building"); }, null, { timeout: 60000 });
      await idle();
      for (let i = 0; i < 2; i++) {
        await page.evaluate(() => window.__labcoat.store.send({ type: "discover" }));
        await page.waitForTimeout(600);
        await idle();
      }
      await page.evaluate(() => window.__labcoat.store.send({ type: "run" }));
      await page.waitForTimeout(400);
      await idle();
      await page.evaluate(() => {
        const store = window.__labcoat.store;
        if (!store.get().watch) store.send({ type: "setWatch", enabled: true });
        if (!store.get().currentRun) store.transport.startRun("SampleTests");
      });
      await idle();
      break;
    }
    case "wait":
      await page.waitForTimeout(200 + arg * 1500);
      break;
  }
  return name;
}

const VARIANTS = [
  { query: "?mock", motion: "full" },
  { query: "?mock", motion: "off", clock: true },
  { query: "?mock=large", motion: "full" },
  { query: "?mock", motion: "full", clock: true },
  { query: "?mock=large", motion: "off" },
  // the docs screenshots' context: coverage on, 2x, motion off, reduced motion, fixed clock
  { query: "?mock&coverage", motion: "off", clock: true, docs: true },
];

const browser = await chromium.launch();
let done = 0;
let failed = false;
const perPage = Math.max(50, Math.ceil(N / VARIANTS.length));
try {
  for (let v = 0; done < N; v = (v + 1) % VARIANTS.length) {
    const variant = VARIANTS[v];
    const ctx = await browser.newContext({ viewport: variant.docs ? { width: 1440, height: 900 } : { width: 1280, height: between(600, 900) }, deviceScaleFactor: variant.docs ? 2 : 1, reducedMotion: variant.motion === "off" ? "reduce" : "no-preference" });
    await ctx.addInitScript((motion) => {
      if (sessionStorage.getItem("tree-stress")) return;
      sessionStorage.setItem("tree-stress", "1");
      localStorage.clear();
      localStorage.setItem("mste.settings", JSON.stringify({ confetti: false, $v: 2, ...(motion === "off" ? { motion: "off" } : {}) }));
    }, variant.motion);
    if (variant.clock) {
      await ctx.clock.install({ time: new Date(2026, 9, 8, 10, 24, 0) });
      await ctx.clock.resume();
    }
    const page = await ctx.newPage();
    const errors = [];
    page.on("pageerror", (e) => errors.push(e.message));
    page.on("console", (m) => m.type() === "error" && errors.push(`console: ${m.text()}`));
    await page.goto(`${base}${variant.query}`);
    await page.waitForSelector(".tree-row");
    await page.waitForTimeout(600);
    await install(page);
    // the dev server may reload the page (a hot update of a file someone saved, or a dependency re-optimization):
    // that page's run ends there (not a failure), the next variant starts fresh
    let reloaded = false;
    page.on("load", () => (reloaded = true)); // a new document (hash / history updates do not fire load)
    const t0 = Date.now();
    let i = 0;
    try {
      for (; i < perPage && done < N; i++, done++) {
        await act(page);
        const r = rand();
        if (r < 0.5) await page.waitForTimeout(Math.floor(rand() * 120));
        if (r < 0.35) await settledCheck(page);
        const res = await page.evaluate(() => ({ v: window.__ts.violations.length }));
        if (res.v || errors.length) break;
      }
      // drain: let any run finish, then a settled check
      await page.waitForFunction(() => !window.__labcoat.store.get().currentRun, null, { timeout: 60000 }).catch(() => {});
      await page.waitForTimeout(400);
      await settledCheck(page);
    } catch (e) {
      // the error can come before the new document's load event
      if (/Execution context was destroyed|navigation|reading '(check|store|violations)'/.test(String(e?.message))) reloaded = true;
      if (!reloaded) throw e;
    }
    if (reloaded) {
      console.log(`${variant.query}: the dev server reloaded the page (a source file changed) after ${i} actions; next page`);
      await ctx.close();
      continue;
    }
    const out = await page.evaluate(() => ({ violations: window.__ts.violations, frames: window.__ts.frames, checks: window.__ts.checks }));
    console.log(`${variant.query} motion=${variant.motion}${variant.clock ? " clock" : ""}: ${i} actions, ${out.frames} frames checked, ${Math.round((Date.now() - t0) / 1000)} s${out.violations.length ? `, ${out.violations.length} VIOLATIONS` : ""}`);
    if (out.violations.length || errors.length) {
      failed = true;
      for (const x of out.violations.slice(0, 6)) console.log(JSON.stringify(x, null, 1));
      for (const e of errors.slice(0, 6)) console.log("page error:", e);
      await ctx.close();
      break;
    }
    await ctx.close();
  }
} finally {
  await browser.close();
  await stop();
}
console.log(failed ? "tree-stress: FAILED" : `tree-stress: ${done} iterations, no invariant violations`);
process.exit(failed ? 1 : 0);
