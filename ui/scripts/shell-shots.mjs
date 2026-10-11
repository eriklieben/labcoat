// SHELL screenshots: node scripts/shell-shots.mjs <outDir> [port=5230] [filter]
import { createRequire } from "node:module";
import { FAMILIES } from "./theme-families.mjs";
const require = createRequire(new URL("../package.json", import.meta.url));
const { chromium } = require("playwright");
const out = process.argv[2] ?? "/tmp/shell";
const port = process.argv[3] ?? "5230";
const only = process.argv[4];
const base = `http://localhost:${port}/`;

const scenarios = {
  partial: async (page) => {
    await page.goto(base + "?mock");
    await page.waitForSelector(".tree-row");
    await page.waitForTimeout(600);
    await page.locator(".tree-row", { hasText: "Add_ReturnsSum" }).dblclick();
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${out}/partial-running-${page.__tag}.png` });
    await page.waitForTimeout(2500);
  },
  buildfail: async (page) => {
    await page.goto(base + "?mock&buildfail");
    await page.waitForSelector(".tree-row");
    await page.waitForTimeout(2000);
  },
  builderrors: async (page) => {
    await page.goto(base + "?mock&buildfail");
    await page.waitForSelector(".tree-row");
    await page.waitForTimeout(2000);
    await page.getByRole("button", { name: "Show errors" }).click();
    await page.waitForTimeout(400);
  },
  watch: async (page) => {
    await page.goto(base + "?mock");
    await page.waitForSelector(".tree-row");
    await page.waitForTimeout(500);
    await page.keyboard.press("w");
    await page.waitForTimeout(4200);
    await page.screenshot({ path: `${out}/watch-running-${page.__tag}.png` });
    await page.waitForTimeout(5000);
  },
  palette: async (page) => {
    await page.goto(base + "?mock");
    await page.waitForSelector(".tree-row");
    await page.waitForTimeout(500);
    await page.keyboard.press("Control+k");
    await page.keyboard.type("add");
    await page.waitForTimeout(300);
  },
  shortcuts: async (page) => {
    await page.goto(base + "?mock");
    await page.waitForSelector(".tree-row");
    await page.waitForTimeout(500);
    await page.keyboard.press("?");
    await page.waitForTimeout(300);
  },
  offline: async (page) => {
    await page.goto(base + "?mock&offline=1200");
    await page.waitForSelector(".tree-row");
    await page.waitForTimeout(2000);
  },
  neverconnected: async (page) => {
    await page.goto(base);
    await page.waitForTimeout(2500);
  },
};

const combos = [];
for (const family of FAMILIES) for (const theme of ["light", "dark"]) for (const w of [1440, 1000]) combos.push({ family, theme, w });
const browser = await chromium.launch();
const errors = [];
const jobs = [];
for (const [name, fn] of Object.entries(scenarios)) {
  if (only && !only.split(",").includes(name)) continue;
  for (const c of combos) {
    jobs.push(async () => {
      const ctx = await browser.newContext({ viewport: { width: c.w, height: 860 }, colorScheme: c.theme });
      await ctx.addInitScript(([t, f]) => {
        try {
          if (!sessionStorage.getItem("init")) {
            localStorage.clear();
            sessionStorage.setItem("init", "1");
          }
          localStorage.setItem("mste.theme", t);
          localStorage.setItem("mste.themeFamily", f);
        } catch {}
      }, [c.theme, c.family]);
      const page = await ctx.newPage();
      page.__tag = `${c.family}-${c.theme}-${c.w}`;
      page.on("pageerror", (e) => errors.push(`${name} ${page.__tag}: ${e.message}`));
      await fn(page);
      await page.screenshot({ path: `${out}/${name}-${page.__tag}.png` });
      await ctx.close();
    });
  }
}
const N = 6;
let i = 0;
await Promise.all(Array.from({ length: N }, async () => { while (i < jobs.length) await jobs[i++](); }));
await browser.close();
console.log(errors.length ? errors.join("\n") : "no page errors", `\n${jobs.length} scenarios`);
