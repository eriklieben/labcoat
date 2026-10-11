// DETAIL agent: screenshots of the detail pane scenarios (mock), per theme family / mode / width.
//   node scripts/detail-shots.mjs <baseUrl> <outDir> [combos=all|quick]
import { chromium } from "playwright";
import { FAMILIES } from "./theme-families.mjs";
import fs from "node:fs";

const [base = "http://localhost:5210/", out = "/tmp/detail", which = "all"] = process.argv.slice(2);
fs.mkdirSync(out, { recursive: true });
const combos = [];
for (const family of FAMILIES) for (const mode of ["dark", "light"]) for (const w of [1440, 1000]) combos.push({ family, mode, w });
const list = which === "quick" ? combos.slice(0, 1) : combos;

const browser = await chromium.launch();
const errors = [];
async function runCombo(c) {
  const tag = `${c.family}-${c.mode}-${c.w}`;
  const page = await browser.newPage({ viewport: { width: c.w, height: 900 }, colorScheme: c.mode });
  page.on("pageerror", (e) => errors.push(`${tag}: ${e.message}`));
  page.on("console", (m) => m.type() === "error" && errors.push(`${tag}: ${m.text()}`));
  await page.addInitScript(([family, mode]) => {
    if (sessionStorage.getItem("init")) return;
    sessionStorage.setItem("init", "1");
    localStorage.clear();
    localStorage.setItem("mste.view", JSON.stringify("details"));
    localStorage.setItem("mste.page", JSON.stringify("tests"));
    localStorage.setItem("mste.logOpen", "false");
    localStorage.setItem("mste.themeFamily", family);
    localStorage.setItem("mste.theme", mode);
  }, [c.family, c.mode]);
  await page.goto(`${base}?mock&tracing`);
  await page.waitForSelector(".tree-row");
  const shot = (name) => page.screenshot({ path: `${out}/${name}-${tag}.png` });
  const row = (text) => page.locator(".tree-row", { hasText: text }).first();
  const waitIdle = async () => {
    await page.waitForTimeout(400);
    await page.waitForFunction(() => !window.__labcoat.store.get().currentRun, null, { timeout: 60000 });
    await page.waitForTimeout(600);
  };

  // run all → run overview (nothing selected)
  await page.locator("button", { hasText: "Run all" }).first().click();
  await waitIdle();
  await shot("overview");

  // failed test, failure first
  await row("PlaceOrder_SendsConfirmationEmail").click();
  await page.waitForTimeout(700);
  await shot("failed");
  await page.locator(".detail-scroll").evaluate((el) => (el.scrollTop = el.scrollHeight));
  await page.waitForTimeout(300);
  await shot("failed-bottom");

  // Aspire failure context (tracing on)
  await row("Cache_RoundTripsValue").click();
  await page.waitForTimeout(900);
  await shot("aspire-failed");

  // passed test
  await row("Add_ReturnsSum").click();
  await page.waitForTimeout(600);
  await shot("passed");

  // group summary
  await row("OrderServiceTests").click();
  await page.waitForTimeout(400);
  await shot("group");

  // repeat run (flaky test)
  await row("HealthEndpoint_ReportsHealthy").click();
  await page.waitForTimeout(300);
  await page.locator(".detail-actions button", { hasText: "Run 20" }).click();
  await waitIdle();
  await shot("repeat");

  // snapshot mode: an older history square
  await row("PlaceOrder_SendsConfirmationEmail").click();
  await page.waitForTimeout(400);
  await page.locator(".history-squares .hsq").nth(3).click();
  await page.waitForTimeout(800);
  await shot("snapshot");
  await page.keyboard.press("Escape");
  await page.close();
}
for (const c of list) {
  // other agents' edits can trigger a Vite full reload mid-scenario (mock state resets): retry the combo
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      await runCombo(c);
      break;
    } catch (e) {
      console.warn(`retry ${c.family}-${c.mode}-${c.w}: ${String(e).split("\n")[0]}`);
    }
  }
}
await browser.close();
console.log(errors.length ? errors.join("\n") : "no errors");
