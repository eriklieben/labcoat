// Leak check: repeats expand/scroll/filter/collapse/page switches and prints live effect/owner counts per round (must stay flat).
// Usage: npm run check:leaks, or node scripts/wc/leak.mjs [appUrl|port] (without a URL it starts the dev server).
import { createRequire } from "node:module";
import { baseUrlOr, startApp } from "../lib/dev-server.mjs";
const require = createRequire(new URL("../../package.json", import.meta.url));
const server = await baseUrlOr(process.argv[2], startApp);
const { chromium } = require("playwright");
const browser = await chromium.launch();
const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
await page.addInitScript(() => { try { localStorage.clear(); } catch {} });
await page.goto(`${server.base}?mock=large`);
await page.waitForSelector(".tree-row");
await page.waitForTimeout(1000);
const stats = () => page.evaluate(() => JSON.stringify(window.__labcoat.stats()));
const base = await stats();
for (let round = 0; round < 5; round++) {
  await page.getByRole("button", { name: "Expand all" }).click();
  await page.evaluate(async () => { const s = document.querySelector(".tree-scroller"); for (let i = 0; i < 80; i++) { s.scrollTop = (i * 997) % s.scrollHeight; await new Promise((r) => requestAnimationFrame(r)); } s.scrollTop = 0; await new Promise((r) => requestAnimationFrame(r)); });
  await page.getByLabel(/^Filter tests/).fill("order");
  await page.waitForTimeout(50);
  await page.getByLabel(/^Filter tests/).fill("");
  await page.getByRole("button", { name: "Collapse all" }).click();
  await page.locator(".rail-item", { hasText: "Settings" }).click();
  await page.waitForTimeout(50);
  await page.locator(".rail-item", { hasText: "Tests" }).click();
  await page.waitForSelector(".tree-row");
  await page.getByRole("tab", { name: /Coverage/ }).click();
  await page.getByRole("tab", { name: /Test details/ }).click();
  await page.waitForTimeout(100);
  console.log("round", round, await stats());
}
// back to the initial view state (default expansion is not restorable after expand/collapse all, so compare rounds)
console.log("base", base);
await browser.close();
await server.stop();
