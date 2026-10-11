// axe-core on the main page (mock), every theme family × light / dark × scenario (LC_FAMILIES=a,b limits):
//   npm run check:a11y, or node scripts/shell-axe.mjs [appUrl|port] [axePath] (without a URL it starts the dev server).
// Fails on serious / critical violations.
import { createRequire } from "node:module";
import { FAMILIES } from "./theme-families.mjs";
import { baseUrlOr, startApp } from "./lib/dev-server.mjs";
import { readFileSync } from "node:fs";
const require = createRequire(new URL("../package.json", import.meta.url));
const { chromium } = require("playwright");
const { base, stop } = await baseUrlOr(process.argv[2], startApp);
let serious = 0;
const axe = readFileSync(process.argv[3] ?? require.resolve("axe-core/axe.min.js"), "utf8");
const browser = await chromium.launch();
for (const family of FAMILIES) for (const theme of ["light", "dark"]) for (const scenario of ["main", "selected", "watch", "snapshot", "snapshot-image"]) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: theme });
  await ctx.addInitScript(([t, f]) => { try { localStorage.clear(); localStorage.setItem("mste.theme", t); localStorage.setItem("mste.themeFamily", f); } catch {} }, [theme, family]);
  const page = await ctx.newPage();
  await page.goto(`${base}?mock&buildfail${scenario.startsWith("snapshot") ? "&snapshots" : ""}`);
  await page.waitForSelector(".tree-row");
  await page.waitForTimeout(2000);
  if (scenario === "selected") { await page.locator(".tree-row", { hasText: "Add_ReturnsSum" }).click(); await page.waitForTimeout(800); }
  if (scenario === "watch") { await page.keyboard.press("w"); await page.waitForTimeout(500); }
  // Verify snapshot panel (?snapshots): a text diff side by side, an image in onion-skin mode
  if (scenario.startsWith("snapshot")) {
    await page.keyboard.press("/");
    await page.keyboard.type("is:snapshot");
    await page.locator(".tree-row", { hasText: scenario === "snapshot" ? "Order_MatchesSnapshot" : "Chart_MatchesSnapshot" }).first().click();
    await page.waitForSelector(".snapshot-item");
    await page.locator(".snap-mode .segment", { hasText: scenario === "snapshot" ? "Side by side" : "Onion skin" }).click();
    await page.waitForTimeout(400);
  }
  await page.addScriptTag({ content: axe });
  // snapshot scenarios: only what they add (the snapshot panel, the tree rows with their badges, the toolbar button);
  // the rest of the page is covered by the other scenarios
  const scope = scenario.startsWith("snapshot") ? { include: [[".snapshot-panel"], [".tree-scroller"], [".snap-toolbar-btn"]] } : null;
  const res = await page.evaluate(async (scope) => {
    // finished fades only: axe measures painted colors, so a popup caught mid-fade reports a contrast users never see
    await Promise.all(
      document
        .getAnimations()
        .filter((a) => a.effect?.getComputedTiming().iterations !== Infinity)
        .map((a) => a.finished.catch(() => {})),
    );
    const r = await window.axe.run(scope ?? document, { resultTypes: ["violations"] });
    return r.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => ({ id: v.id, impact: v.impact, n: v.nodes.length, targets: v.nodes.slice(0, 4).map((x) => x.target.join(" ") + (x.any[0]?.message ? ` — ${x.any[0].message.slice(0, 110)}` : "")) }));
  }, scope);
  console.log(`${family}-${theme}-${scenario}:`, res.length ? "" : "0 serious/critical");
  for (const v of res) console.log("  ", v.impact, v.id, v.n, "\n     " + v.targets.join("\n     "));
  serious += res.length;
  await ctx.close();
}
await browser.close();
await stop();
console.log(`serious/critical axe violations: ${serious}`);
process.exit(serious ? 1 : 0);
