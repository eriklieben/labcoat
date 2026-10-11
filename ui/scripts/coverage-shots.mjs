// COVERAGE agent: screenshots of the coverage tab / page / line-tests popover / log panel + strip, in mock mode.
//   node scripts/coverage-shots.mjs <baseUrl> <outDir> [--quick]
import { chromium } from "playwright";
import { FAMILIES } from "./theme-families.mjs";
import { mkdirSync } from "node:fs";
import path from "node:path";

const base = process.argv[2] ?? "http://localhost:5220/";
const out = process.argv[3] ?? "/tmp/coverage-shots";
const quick = process.argv.includes("--quick");
mkdirSync(out, { recursive: true });
const browser = await chromium.launch();
const errors = [];

async function session({ theme, family = "aspire", width = 1440, height = 900, query = "?mock&coverage", logOpen = false }) {
  const ctx = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1, colorScheme: theme });
  await ctx.addInitScript(
    ([t, f, lo]) => {
      if (sessionStorage.getItem("init")) return;
      sessionStorage.setItem("init", "1");
      localStorage.clear();
      localStorage.setItem("mste.theme", t); // raw strings (src/theme.ts)
      localStorage.setItem("mste.themeFamily", f);
      localStorage.setItem("mste.logOpen", JSON.stringify(lo));
    },
    [theme, family, logOpen],
  );
  const page = await ctx.newPage();
  page.on("pageerror", (e) => errors.push(`[pageerror] ${e.message}`));
  page.on("console", (m) => m.type() === "error" && errors.push(`[console] ${m.text()}`));
  await page.goto(base + query);
  await page.waitForSelector(".tree-row");
  await page.waitForTimeout(400);
  return { ctx, page };
}

const shot = (page, name) => page.screenshot({ path: path.join(out, name) });

async function runAllAndWait(page) {
  await page.locator("button", { hasText: "Run all" }).first().click();
  await page.waitForTimeout(500);
  await page.waitForFunction(() => !window.__labcoat.store.get().currentRun, null, { timeout: 60000 });
  await page.waitForTimeout(400);
}

async function selectTestByName(page, name) {
  const box = page.locator(".tree-pane input[type=search]").first();
  await box.fill(name);
  await page.waitForTimeout(150);
  await page.locator(".tree-row", { hasText: name }).first().click();
  await page.waitForTimeout(500);
}

/** names of the mock's cached per-test coverage: [found, stale]; plus a test without coverage */
async function names(page) {
  return page.evaluate(() => {
    const tests = [...window.__labcoat.store.get().tests.values()];
    return { tests: tests.map((t) => ({ name: t.displayName, pid: t.projectId, outcome: t.outcome })) };
  });
}

async function scenario(theme, family, width, suffix) {
  const { ctx, page } = await session({ theme, family, width });
  const tag = `${family}-${theme}-${width}${suffix ?? ""}`;
  await page.getByRole("tab", { name: /Coverage/ }).click();
  await page.waitForTimeout(300);
  await shot(page, `tab-empty-${tag}.png`);
  await runAllAndWait(page);
  // full report overview in the tab
  await page.evaluate(() => window.__labcoat.store.showFullCoverage());
  await page.waitForTimeout(300);
  await shot(page, `tab-overview-${tag}.png`);
  const { tests } = await names(page);
  const sample = tests.filter((t) => t.pid === "SampleTests");
  // cached: the first 3 passing SampleTests (behavior pass), the 2nd is stale — find via lookup
  const found = [];
  for (const t of sample.filter((x) => !x.name.includes("(")).slice(0, 12)) {
    const r = await page.evaluate(
      async ([n]) => {
        const s = window.__labcoat.store;
        const t = [...s.get().tests.values()].find((x) => x.displayName === n);
        return t ? await s.transport.fetchTestCoverage(t.projectId, [t.id]) : null;
      },
      [t.name],
    );
    if (r) found.push({ name: t.name, stale: !!r.stale });
  }
  const f = found.find((x) => !x.stale);
  const st = found.find((x) => x.stale);
  if (f) {
    await selectTestByName(page, f.name);
    await shot(page, `tab-found-${tag}.png`);
  }
  if (st) {
    await selectTestByName(page, st.name);
    await shot(page, `tab-stale-${tag}.png`);
  }
  const missing = tests.find((t) => t.pid === "AspireSample.Tests" && /Demo_intentionally|returns_400/.test(t.name)) ?? tests.find((t) => !t.name.includes("(") && !found.some((x) => x.name === t.name));
  await selectTestByName(page, missing.name);
  await shot(page, `tab-missing-${tag}.png`);
  if (!quick) {
    // source view in the tab + popover
    await page.locator("button", { hasText: "Show full coverage" }).first().click();
    await page.waitForTimeout(300);
    if (!(await page.locator(".cov-file").count())) {
      console.log("NOTE: pane still showed a file after Show full coverage (core batch bug); using Back");
      await page.locator(".source-view .icon-btn[aria-label='Back to all files']").click();
    }
    await page.locator(".cov-file").first().click();
    await page.waitForSelector(".src-hits-btn");
    await page.waitForTimeout(300);
    await shot(page, `tab-source-${tag}.png`);
  }
  // Coverage page
  await page.locator(".rail-item", { hasText: /Coverage/ }).first().click();
  await page.waitForTimeout(400);
  await shot(page, `page-table-${tag}.png`);
  await page.locator(".cov-file").nth(1).click();
  await page.waitForSelector(".src-line.cov .src-hits-btn");
  await page.waitForTimeout(300);
  await shot(page, `page-source-${tag}.png`);
  await page.locator(".src-line.cov .src-hits-btn").nth(2).click();
  await page.waitForSelector(".cov-lt-row, .cov-lt-empty");
  await page.waitForTimeout(250);
  await shot(page, `page-linetests-${tag}.png`);
  await page.keyboard.press("Escape");
  // gutter-only tint
  await page.locator(".segment", { hasText: "Gutter only" }).click();
  await page.waitForTimeout(150);
  await shot(page, `page-source-gutter-${tag}.png`);
  await page.locator(".segment", { hasText: "Tint lines" }).click();
  // log panel: closed (strip, injected until the shell renders it) and open
  await page.locator(".rail-item", { hasText: "Tests" }).first().click();
  await page.waitForTimeout(300);
  const hasStrip = await page.locator("lc-log-strip").count();
  if (!hasStrip)
    await page.evaluate(() => {
      const ws = document.querySelector(".workspace");
      const s = document.createElement("lc-log-strip");
      s.onOpen = () => document.querySelector('.rail-item[aria-pressed]')?.click();
      s.style.marginTop = "8px";
      ws.append(s);
    });
  await page.waitForTimeout(200);
  await shot(page, `log-closed-strip-${tag}.png`);
  await page.locator('.rail-item[aria-pressed]').click();
  await page.waitForSelector("lc-log-panel");
  await page.waitForTimeout(300);
  await shot(page, `log-open-${tag}.png`);
  await ctx.close();
}

const combos = quick
  ? [["light", "aspire", 1440]]
  : [
      ...FAMILIES.flatMap((f) => [
        ["light", f, 1440],
        ["dark", f, 1440],
      ]),
      ["light", "aspire", 1000],
      ["dark", "catppuccin", 1000],
    ];
for (const [t, f, w] of combos) await scenario(t, f, w);
await browser.close();
console.log(errors.length ? errors.join("\n") : "no page errors");
