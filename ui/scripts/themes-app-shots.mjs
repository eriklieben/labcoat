// Theme screenshots of the app (mock mode), per theme family × light / dark: test tree + failed test detail (code view
// with the method range and the failing line), the coverage page with a tinted source file, Settings with
// "More themes" open, and a crop of the code view. LC_FAMILIES=github,one limits the families.
//   node scripts/themes-app-shots.mjs <baseUrl> <outDir>
import { chromium } from "playwright";
import { mkdirSync } from "node:fs";
import { FAMILIES } from "./theme-families.mjs";

const [base = "http://localhost:5174/", out = "/tmp/theme-shots"] = process.argv.slice(2);
mkdirSync(out, { recursive: true });
const browser = await chromium.launch();
const errors = [];

async function combo(family, mode) {
  const tag = `${family}-${mode}`;
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: mode, reducedMotion: "reduce" });
  await ctx.addInitScript(
    ([f, m]) => {
      if (sessionStorage.getItem("init")) return;
      sessionStorage.setItem("init", "1");
      localStorage.clear();
      localStorage.setItem("mste.themeFamily", f);
      localStorage.setItem("mste.theme", m);
      localStorage.setItem("mste.logOpen", "false");
      localStorage.setItem("mste.settings", JSON.stringify({ confetti: false }));
    },
    [family, mode],
  );
  const page = await ctx.newPage();
  page.on("pageerror", (e) => errors.push(`${tag}: ${e.message}`));
  page.on("console", (m) => m.type() === "error" && errors.push(`${tag} console: ${m.text()}`));
  await page.goto(`${base}?mock&coverage`);
  await page.waitForSelector(".tree-row");
  const got = await page.evaluate(() => [document.documentElement.dataset.themeFamily, document.documentElement.dataset.theme]);
  if (got[0] !== family || got[1] !== mode) errors.push(`${tag}: html has ${got.join("/")}`);
  await page.locator("button", { hasText: "Run all" }).first().click();
  await page.waitForTimeout(400);
  await page.waitForFunction(() => window.__labcoat && !window.__labcoat.store.get().currentRun, null, { timeout: 60000 });
  await page.waitForTimeout(500);
  // tree + failed test detail
  await page.locator(".tree-row", { hasText: "PlaceOrder_SendsConfirmationEmail" }).first().click();
  await page.waitForTimeout(900);
  await page.screenshot({ path: `${out}/detail-${tag}.png` });
  const code = page.locator(".src-line.in-range").first();
  if (await code.count()) {
    await page.locator("lc-code-view").first().evaluate((e) => e.scrollIntoView({ block: "start" }));
    await page.waitForTimeout(200);
    const box = await page.locator("lc-code-view").first().boundingBox();
    if (box) await page.screenshot({ path: `${out}/code-${tag}.png`, clip: { x: box.x, y: box.y, width: Math.min(box.width, 820), height: Math.min(box.height, 300) } });
  }
  // coverage page with a source file (full-line tint)
  await page.locator(".rail-item", { hasText: /Coverage/ }).first().click();
  await page.waitForTimeout(400);
  await page.locator(".cov-file").nth(1).click();
  await page.waitForSelector(".src-line.cov");
  await page.waitForTimeout(300);
  await page.screenshot({ path: `${out}/coverage-${tag}.png` });
  // settings, "More themes" open
  await page.locator(".rail-item", { hasText: /Settings/ }).first().click();
  await page.waitForSelector("lc-settings-page");
  const more = page.locator(".more-themes .lc-disclosure-btn");
  if ((await more.getAttribute("aria-expanded")) !== "true") await more.click();
  await page.waitForTimeout(400);
  await page.locator(".theme-grid .theme-card").first().evaluate((e) => e.scrollIntoView({ block: "center" }));
  await page.screenshot({ path: `${out}/settings-${tag}.png` });
  await ctx.close();
}

// one retry: a dev-server reload (another file saved) in the middle of a combo is not a theme problem
const jobs = FAMILIES.flatMap((f) => ["light", "dark"].map((m) => () => combo(f, m).catch(() => combo(f, m))));
let i = 0;
await Promise.all(Array.from({ length: 4 }, async () => { while (i < jobs.length) await jobs[i++]().catch((e) => errors.push(e.message)); }));
await browser.close();
console.log(errors.length ? errors.join("\n") : "no page errors", `\n${jobs.length} combos → ${out}`);
