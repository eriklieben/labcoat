// Perf on the 2000-test mock: long tasks (>50 ms) per phase, rAF frame times while scrolling, live effect/owner counts.
// Reports numbers (compare before / after a change); fails only on page errors.
// Usage: npm run check:perf, or node scripts/wc/perf.mjs [appUrl|port] (without a URL it starts the dev server).
import { createRequire } from "node:module";
import { baseUrlOr, startApp } from "../lib/dev-server.mjs";
const require = createRequire(new URL("../../package.json", import.meta.url));
const { chromium } = require("playwright");
const { base, stop } = await baseUrlOr(process.argv[2], startApp);
const wc = true;
const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: "dark" });
await ctx.addInitScript(() => {
  try { localStorage.clear(); localStorage.setItem("mste.theme", "dark"); localStorage.setItem("mste.logOpen", "false"); } catch {}
  window.__long = [];
  new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__long.push({ phase: window.__phase, d: Math.round(e.duration) }); }).observe({ type: "longtask", buffered: true });
});
const page = await ctx.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push(e.message));
await page.goto(`${base}?mock=large`);
await page.waitForSelector(".tree-row");
await page.waitForTimeout(1500);
const phase = (p) => page.evaluate((p) => (window.__phase = p), p);
const stats = () => (wc ? page.evaluate(() => ({ ...window.__labcoat.stats(), rows: document.querySelectorAll(".tree-row").length, nodes: document.getElementsByTagName("*").length })) : page.evaluate(() => ({ rows: document.querySelectorAll(".tree-row").length, nodes: document.getElementsByTagName("*").length })));
const total = await page.evaluate(() => document.querySelector(".chip-count")?.textContent);
console.log("tests:", total, "rows in DOM:", (await stats()).rows);
const s0 = await stats();
console.log("stats initial", JSON.stringify(s0));

// expand all so there are ~2000+ rows to scroll through
await phase("expandAll");
await page.getByRole("button", { name: "Expand all" }).click();
await page.waitForTimeout(300);
const rowsTotal = await page.evaluate(() => document.querySelector(".tree-scroller > div")?.style.height);
console.log("scroll height (expand all):", rowsTotal);
const box = await page.locator(".tree-scroller").boundingBox();
await page.mouse.move(box.x + 100, box.y + 100);
await phase("scroll");
const t0 = Date.now();
for (let i = 0; i < 120; i++) { await page.mouse.wheel(0, 400); await page.waitForTimeout(16); }
for (let i = 0; i < 120; i++) { await page.mouse.wheel(0, -400); await page.waitForTimeout(16); }
// fast flings
await page.evaluate(async () => { const s = document.querySelector(".tree-scroller"); for (let i = 0; i < 60; i++) { s.scrollTop = Math.random() * s.scrollHeight; await new Promise((r) => requestAnimationFrame(r)); } s.scrollTop = 0; });
console.log("scroll ms", Date.now() - t0);
// frame times during scripted scroll
const frames = await page.evaluate(async () => {
  const s = document.querySelector(".tree-scroller"); const out = []; let last = performance.now();
  for (let i = 0; i < 120; i++) { s.scrollTop += 240; await new Promise((r) => requestAnimationFrame(r)); const n = performance.now(); out.push(n - last); last = n; }
  s.scrollTop = 0; await new Promise((r) => requestAnimationFrame(r));
  out.sort((a, b) => a - b); return { p50: out[60].toFixed(1), p95: out[114].toFixed(1), max: out[119].toFixed(1) };
});
console.log("rAF frame ms during scroll", JSON.stringify(frames));
await page.waitForTimeout(300);
console.log("stats after scroll", JSON.stringify(await stats()));

await phase("filter");
await page.getByLabel(/^Filter tests/).pressSequentially("order", { delay: 60 });
await page.waitForTimeout(200);
await page.getByLabel(/^Filter tests/).fill("");
for (const c of ["Failed", "Passed", "Skipped", "Not run", "All"]) { const chip = page.getByRole("radio", { name: new RegExp("^" + c) }); if (!(await chip.count())) continue; await chip.first().click(); await page.waitForTimeout(80); }
await phase("collapse");
await page.getByRole("button", { name: "Collapse all" }).click();
await page.waitForTimeout(150);
await page.getByRole("button", { name: "Expand all" }).click();
await page.waitForTimeout(150);
await page.getByRole("button", { name: "Collapse all" }).click();
await page.waitForTimeout(150);
console.log("stats collapsed", JSON.stringify(await stats()));
await page.getByRole("button", { name: "Expand all" }).click();
await page.waitForTimeout(300);
console.log("stats expanded again", JSON.stringify(await stats()));

await phase("run");
await page.getByRole("button", { name: "Run all" }).click();
await page.waitForTimeout(300);
await page.getByRole("button", { name: "Run all" }).waitFor({ timeout: 120000 });
await page.waitForTimeout(500);
await page.evaluate(() => (document.querySelector(".tree-scroller").scrollTop = 0));
await page.waitForTimeout(300);
console.log("stats after run", JSON.stringify(await stats()));
const long = await page.evaluate(() => window.__long);
const byPhase = {};
for (const l of long) (byPhase[l.phase ?? "load"] ??= []).push(l.d);
console.log("long tasks (>50ms) by phase:", JSON.stringify(byPhase));
console.log(errors.join("\n") || "no page errors");
await browser.close();
await stop();
process.exit(errors.length ? 1 : 0);
