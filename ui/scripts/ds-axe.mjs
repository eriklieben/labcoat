// DS: axe-core scan of the design-system gallery in every theme family × light / dark, plus open overlays (menu, dialog, tooltip).
// Usage: npm run check:a11y, or node scripts/ds-axe.mjs [galleryUrl] (without a URL it starts the gallery itself).
import { chromium } from "playwright";
import { baseUrlOr, startGallery } from "./lib/dev-server.mjs";
import { THEME_KEYS } from "./theme-families.mjs";
import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const axePath = require.resolve("axe-core/axe.min.js");

const { base, stop } = await baseUrlOr(process.argv[2], startGallery);
const themes = THEME_KEYS;
const browser = await chromium.launch();
let total = 0;
const run = async (page, label) => {
  // let overlays finish fading in first: axe measures the colors as painted, so a tooltip or menu caught mid-fade
  // reports a contrast that never reaches the user (it flaked on CI's slower runners)
  await page.evaluate(() =>
    Promise.all(
      document
        .getAnimations()
        .filter((a) => a.effect?.getComputedTiming().iterations !== Infinity)
        .map((a) => a.finished.catch(() => {})),
    ),
  );
  if (!(await page.evaluate(() => !!window.axe))) await page.addScriptTag({ path: axePath });
  const res = await page.evaluate(async () => await window.axe.run(document, { resultTypes: ["violations"] }));
  for (const v of res.violations) {
    total += v.nodes.length;
    console.log(`[${label}] ${v.id} (${v.impact}) x${v.nodes.length}: ${v.help}`);
    for (const n of v.nodes.slice(0, 4)) console.log(`    ${n.target.join(" ")} — ${n.failureSummary?.split("\n").slice(1, 2).join(" ")}`);
  }
};
for (const t of themes) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto(`${base}?theme=${t}`);
  await page.waitForSelector(".g-section");
  await run(page, t);
  // again with the scroll container unrolled, so color-contrast sees every section (not only the visible one)
  await page.addStyleTag({ content: "body{overflow:visible!important}.g-app{height:auto!important}.g-main{overflow:visible!important}" });
  await run(page, `${t} unrolled`);
  if (t === "aspire-light" || t === "catppuccin-dark") {
    await page.getByRole("button", { name: "Actions", exact: true }).click();
    await page.waitForSelector(".lc-menu");
    await run(page, `${t} menu open`);
    await page.keyboard.press("Escape");
    await page.getByRole("button", { name: "Keyboard shortcuts" }).click();
    await page.waitForSelector("dialog.lc-dialog[open]");
    await run(page, `${t} dialog open`);
    await page.keyboard.press("Escape");
    await page.getByRole("button", { name: "Bottom", exact: true }).focus();
    await page.keyboard.press("Shift+Tab");
    await page.keyboard.press("Tab");
    await page.waitForSelector(".lc-tooltip");
    await run(page, `${t} tooltip open`);
  }
  await page.close();
}
await browser.close();
await stop();
console.log(`axe violations (nodes): ${total}`);
process.exit(total ? 1 : 0);
