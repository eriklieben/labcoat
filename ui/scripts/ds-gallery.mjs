// DS: screenshots of the design-system gallery in every theme family × light / dark (+ console errors; LC_FAMILIES=a,b limits).
// Usage: npm run gallery -w design-system (port 5200), then node scripts/ds-gallery.mjs <outDir> [baseUrl]
import { chromium } from "playwright";
import { THEME_KEYS } from "./theme-families.mjs";

const out = process.argv[2] ?? "/tmp/ds";
const base = process.argv[3] ?? "http://localhost:5200/";
const themes = THEME_KEYS;
const browser = await chromium.launch();
let errors = 0;
for (const t of themes) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.on("console", (m) => m.type() === "error" && (errors++, console.log(`[${t}] console error:`, m.text())));
  page.on("pageerror", (e) => (errors++, console.log(`[${t}] page error:`, e.message)));
  await page.goto(`${base}?theme=${t}`);
  await page.waitForSelector(".g-section");
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: `${out}/viewport-${t}.png` });
  // unroll the scroll container for a full-page capture
  await page.addStyleTag({ content: "body{overflow:visible!important}.g-app{height:auto!important}.g-main{overflow:visible!important}.g-nav{display:none}.g-app{grid-template-columns:1fr!important;grid-template-areas:'header' 'main'!important}" });
  await page.screenshot({ path: `${out}/full-${t}.png`, fullPage: true });
  // one image per section (easier to inspect)
  const { mkdirSync } = await import("node:fs");
  mkdirSync(`${out}/${t}`, { recursive: true });
  for (const id of await page.$$eval(".g-section", (els) => els.map((e) => e.id))) {
    await page.locator(`#${id}`).screenshot({ path: `${out}/${t}/${id}.png` });
  }
  await page.close();
}
await browser.close();
console.log(`screenshots in ${out}; errors: ${errors}`);
process.exit(errors ? 1 : 0);
