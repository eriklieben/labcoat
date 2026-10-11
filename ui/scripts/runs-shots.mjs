// RUNS: screenshots of the run switcher, past run, run diff, settings, diagnostics (mock mode).
// node scripts/runs-shots.mjs <outDir> [baseUrl]
import { chromium } from "playwright";
import { FAMILIES } from "./theme-families.mjs";
const out = process.argv[2] ?? "/tmp/runs";
const base = process.argv[3] ?? "http://localhost:5240";
const browser = await chromium.launch();
const errors = [];
async function page(family, mode, extra = "", settings = {}) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: mode });
  await ctx.addInitScript(([f, m, s]) => {
    localStorage.setItem("mste.themeFamily", f);
    localStorage.setItem("mste.theme", m);
    localStorage.setItem("mste.settings", JSON.stringify(s));
    localStorage.setItem("mste.logOpen", "false");
  }, [family, mode, settings]);
  const p = await ctx.newPage();
  p.on("pageerror", (e) => errors.push(`${family}/${mode}: ${e.message}`));
  p.on("console", (m) => m.type() === "error" && errors.push(`${family}/${mode} console: ${m.text()}`));
  await p.goto(`${base}/?mock${extra}#/tests`);
  await p.waitForSelector(".run-pill");
  await p.waitForTimeout(700);
  return p;
}
for (const family of FAMILIES) {
  for (const mode of ["dark", "light"]) {
    const tag = `${family}-${mode}`;
    let p = await page(family, mode, "", { partialRunsInHistory: true });
    await p.click(".run-pill");
    await p.waitForTimeout(250);
    await p.screenshot({ path: `${out}/menu-${tag}.png` });
    // keyboard: ArrowDown to the regression run ("+6 failed"), Enter → view that run
    const target = await p.locator(".run-menu-run").evaluateAll((els) => els.findIndex((e) => e.textContent.includes("+6 failed")));
    for (let i = 0; i < target + 2; i++) await p.keyboard.press("ArrowDown");
    await p.keyboard.press("Enter");
    await p.waitForTimeout(500);
    await p.screenshot({ path: `${out}/past-${tag}.png` });
    // compare the viewed run with…
    await p.click(".run-pill");
    await p.click("text=Compare with…");
    await p.waitForTimeout(200);
    await p.screenshot({ path: `${out}/compare-pick-${tag}.png` });
    // the next full run below the viewed one = its predecessor
    await p.locator(".run-menu-run").nth(target + 1).click();
    await p.waitForTimeout(600);
    await p.screenshot({ path: `${out}/diff-${tag}.png` });
    await p.keyboard.press("Escape");
    await p.waitForTimeout(150);
    // live vs. the run before the latest full run
    await p.click(".run-pill");
    await p.locator(".run-menu-item", { hasText: "Live" }).first().click();
    await p.waitForTimeout(300);
    await p.click(".run-pill");
    await p.click("text=Compare with…");
    await p.locator(".run-menu-run", { hasText: "Full" }).nth(1).click();
    await p.waitForTimeout(600);
    await p.screenshot({ path: `${out}/diff-live-${tag}.png` });
    await p.context().close();

    p = await page(family, mode);
    await p.evaluate(() => (location.hash = "#/settings"));
    await p.waitForTimeout(400);
    await p.screenshot({ path: `${out}/settings-${tag}.png` });
    await p.context().close();

    p = await page(family, mode, "&diag");
    await p.waitForTimeout(500);
    await p.evaluate(() => import("/src/components/diagnostics.ts"));
    await p.evaluate(() => {
      const host = document.querySelector(".detail-pane") ?? document.querySelector(".right-col");
      const wrap = document.createElement("div");
      wrap.className = "detail-scroll";
      const el = document.createElement("lc-project-diagnostics");
      el.setAttribute("project-id", "Legacy.Tests");
      wrap.append(el);
      host.replaceChildren(wrap);
    });
    await p.waitForTimeout(300);
    await p.screenshot({ path: `${out}/diag-${tag}.png` });
    await p.context().close();
  }
}
console.log(errors.length ? errors.join("\n") : "no page errors");
await browser.close();
