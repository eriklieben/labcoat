// The README / docs screenshots (docs/images/, see docs/images/SHOTLIST.md for the state each image shows), taken from
// the mock backend (?mock) so they are reproducible: fixed clock, seeded Math.random, reduced motion, no confetti.
// Dark mode of the default family (Catppuccin Macchiato) unless a shot says otherwise: hero-light.png and the light half
// of the theme gallery are the only light images.
//
//   npm run screenshots:docs                               starts a Vite dev server (port 5300+, or $LABCOAT_CHECK_PORT)
//   npm run screenshots:docs -- http://127.0.0.1:5370/     use a server that is already running (e.g. `vite preview`)
//   LC_SHOTS=hero,themes npm run screenshots:docs          only some shots (names below; "themes" = all theme images)
//   LC_OUT=/tmp/shots npm run screenshots:docs             write somewhere else than docs/images
//
// aspire-dashboard.png needs the AspireSample AppHost running (LC_ASPIRE_DASHBOARD, below); aspire-trace-dashboard.png
// is taken by hand (Aspire's own trace view).
// The mock's sample paths (/home/<user>/...) are rewritten to /src/demo/... in the served scripts, so no local paths
// show up in the images. Full-window shots are 1440 x 900 CSS px; they are saved at 1.5x (2160 px wide), crops at 2x.
import { chromium } from "playwright";
import { execFileSync } from "node:child_process";
import { mkdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { baseUrlOr, startApp } from "./lib/dev-server.mjs";
import { FAMILIES } from "./theme-families.mjs";

const repo = fileURLToPath(new URL("../..", import.meta.url));
const OUT = process.env.LC_OUT ?? path.join(repo, "docs/images");
const ONLY = process.env.LC_SHOTS?.split(",").map((s) => s.trim()).filter(Boolean);
const { base, stop } = await baseUrlOr(process.argv[2], startApp);

// Thu 8 Oct 2026, 10:24 local time: every "Last run …" / run menu time is the same on each pass
const CLOCK = new Date(2026, 9, 8, 10, 24, 0);
const MOCK_ROOT = /\/home\/[^/"`'\s]+\/Repository\/labcoat/g;

const browser = await chromium.launch();
const errors = [];
const warnings = [];
const written = [];

async function openApp(query, { mode = "dark", family = "catppuccin", width = 1440, height = 900, scale = 2, motionOff = true } = {}) {
  const ctx = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: scale, colorScheme: mode, reducedMotion: "reduce" });
  await ctx.addInitScript(
    ([f, m, off]) => {
      // seeded Math.random (mulberry32): the mock's durations and flaky outcomes are the same on every pass
      let s = 0x5eed;
      Math.random = () => {
        s = (s + 0x6d2b79f5) | 0;
        let t = Math.imul(s ^ (s >>> 15), 1 | s);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
      };
      if (sessionStorage.getItem("docs-shots")) return;
      sessionStorage.setItem("docs-shots", "1");
      localStorage.clear();
      localStorage.setItem("mste.themeFamily", f);
      localStorage.setItem("mste.theme", m);
      // Settings → Motion "off" (html[data-motion]); the context also emulates prefers-reduced-motion
      localStorage.setItem("mste.settings", JSON.stringify(off ? { confetti: false, motion: "off", $v: 2 } : { confetti: false, $v: 2 }));
    },
    [family, mode, motionOff],
  );
  // no local paths in the images: rewrite the mock's sample root in the served scripts (the Vite dev server serves the
  // mock modules as .ts, a built preview as .js)
  await ctx.route(/\.(js|mjs|ts)(\?|$)/, async (route) => {
    const res = await route.fetch();
    const body = (await res.text()).replace(MOCK_ROOT, "/src/demo");
    await route.fulfill({ response: res, body });
  });
  await ctx.clock.install({ time: CLOCK });
  await ctx.clock.resume();
  const page = await ctx.newPage();
  const tag = `${query} ${family}-${mode}`;
  page.on("pageerror", (e) => errors.push(`${tag}: ${e.message}`));
  page.on("console", (m) => m.type() === "error" && errors.push(`${tag} console: ${m.text()}`));
  await page.goto(`${base}${query}`);
  await page.waitForSelector(".tree-row");
  await page.waitForTimeout(800);
  return page;
}

/** no run, build or discovery in progress */
const idle = (page) =>
  page.waitForFunction(
    () => {
      const s = window.__labcoat.store.get();
      return !s.currentRun && !s.projects.some((p) => p.status === "discovering" || p.status === "building");
    },
    null,
    { timeout: 60000 },
  );

const settle = async (page, ms = 500) => {
  await page.mouse.move(0, 899); // out of the way, nothing hovered
  await page.waitForTimeout(ms);
};

/** Clear the mock's Inventory.Tests build failure (the second Rediscover "fixes" it) and run everything once. */
async function prepare(page, { run = true } = {}) {
  for (let i = 0; i < 2; i++) {
    await page.locator("button", { hasText: "Rediscover" }).first().click();
    await page.waitForTimeout(600);
    await idle(page);
  }
  if (run) {
    await page.locator("button", { hasText: "Run all" }).first().click();
    await page.waitForTimeout(400);
    await idle(page);
  }
  await settle(page, 600);
}

async function select(page, name) {
  await page.locator(".tree-row", { hasText: name }).first().click();
  await settle(page, 700);
}

/**
 * Glitch guard for the test tree: visible rows must be evenly spaced (no "sparse tree" gaps) and, in the full tree,
 * CalculatorTests must show all four of its methods (a row once went missing right after a watch run).
 */
async function checkTree(page, file) {
  const problem = await page.evaluate(() => {
    const rows = [...document.querySelectorAll(".tree-row")].map((r) => r.getBoundingClientRect()).filter((b) => b.height > 0 && b.bottom > 0 && b.top < innerHeight);
    for (let i = 1; i < rows.length; i++) if (Math.abs(rows[i].top - rows[i - 1].bottom) > 2) return `gap of ${Math.round(rows[i].top - rows[i - 1].bottom)} px between rows ${i - 1} and ${i}`;
    const names = [...document.querySelectorAll(".tree-row")].map((r) => r.textContent);
    if (names.some((n) => n.startsWith("CalculatorTests")))
      for (const m of ["Add_ReturnsSum", "Divide_ByZero_Throws", "Multiply_ReturnsProduct", "Subtract_ReturnsDifference"])
        if (!names.some((n) => n.startsWith(m))) return `tree row ${m} missing`;
    return null;
  });
  if (problem) throw new Error(`${file}: ${problem}`);
}

async function save(page, file, clip, { resize } = {}) {
  if (page.url().startsWith(base)) await checkTree(page, file);
  const full = path.join(OUT, file);
  mkdirSync(path.dirname(full), { recursive: true });
  await page.screenshot({ path: full, clip, animations: "disabled", caret: "hide" });
  // ImageMagick (when installed): optional downscale, then a lossless re-encode with the best compression; a file
  // still over MAX_BYTES is reduced to a 256-color palette (no dithering: invisible on UI shots, ~1/3 of the size)
  const png = ["-strip", "-define", "png:compression-level=9", "-define", "png:compression-filter=5"];
  try {
    execFileSync("magick", [full, ...(resize ? ["-filter", "Lanczos", "-resize", resize] : []), ...png, full]);
    if (statSync(full).size > MAX_BYTES) execFileSync("magick", [full, "-dither", "None", "-colors", "256", ...png, full]);
  } catch {
    /* keep Playwright's PNG */
  }
  written.push(`${file}  ${Math.round(statSync(full).size / 1024)} KB`);
  await page.context().close();
}

/** bounding box of `locator` grown by `pad` px, kept inside the viewport */
async function boxOf(page, locator, pad = 0, extra = {}) {
  const b = await locator.first().boundingBox();
  if (!b) throw new Error(`no box for ${locator}`);
  const vp = page.viewportSize();
  const x = Math.max(0, b.x - pad);
  const y = Math.max(0, b.y - pad);
  const width = Math.min(vp.width - x, b.width + 2 * pad + (extra.width ?? 0));
  const height = Math.min(vp.height - y, b.height + 2 * pad + (extra.height ?? 0));
  return { x, y, width, height };
}

/** The canonical state (hero and theme images): run done, Render_FormatsReceipt selected, the source in view. */
async function canonical(opts) {
  const page = await openApp("?mock&coverage", opts);
  await prepare(page);
  await select(page, "Render_FormatsReceipt");
  return page;
}

const FULL = "2160x"; // full-window shots: 1440 CSS px at 1.5x
const MAX_BYTES = 400 * 1024;

const shots = {
  "hero-light": async () => save(await canonical({ mode: "light" }), "hero-light.png", undefined, { resize: FULL }),
  "hero-dark": async () => save(await canonical({ mode: "dark" }), "hero-dark.png", undefined, { resize: FULL }),

  "failure-diff": async () => {
    const page = await openApp("?mock&coverage");
    await prepare(page);
    await select(page, "Serialize_ProducesExpectedJson");
    const pane = page.locator("lc-detail-pane");
    const top = await boxOf(page, pane);
    const diff = await page.locator("lc-detail-pane .diff-head").first().locator("..").boundingBox();
    const bottom = diff ? diff.y + diff.height + 46 : top.y + top.height; // + the "Raw message" line and the error box's edge
    await save(page, "failure-diff.png", { x: top.x, y: top.y, width: top.width, height: Math.min(bottom, top.y + top.height) - top.y });
  },

  "failure-stack": async () => {
    // a tall viewport, so the opened source and the stack trace fit in one crop of the detail pane
    const page = await openApp("?mock&coverage", { height: 1500 });
    await prepare(page);
    await select(page, "Cache_RoundTripsValue");
    const frame = page.locator("lc-detail-pane .frame-btn", { hasText: "CacheService.cs" }).first();
    await frame.scrollIntoViewIfNeeded();
    await frame.click();
    await settle(page, 900);
    const sectionBox = (title) => page.locator("lc-detail-pane .detail-section", { has: page.locator(".section-header h3", { hasText: title }) }).first();
    await sectionBox("Source").evaluate((e) => e.scrollIntoView({ block: "start" }));
    await settle(page);
    const pane = await boxOf(page, page.locator("lc-detail-pane"));
    const src = await sectionBox("Source").boundingBox();
    const trace = await sectionBox("Stack trace").boundingBox();
    const y = Math.max(pane.y, src.y - 12);
    await save(page, "failure-stack.png", { x: pane.x, y, width: pane.width, height: trace.y + trace.height + 16 - y });
  },

  "tree-filters": async () => {
    const page = await openApp("?mock&coverage");
    await prepare(page);
    await page.locator(".tree-pane button, lc-tree-pane button", { hasText: /^\s*Failed/ }).first().click();
    await page.locator("input[placeholder^='Filter']").first().fill("proj:Sample");
    await settle(page, 700);
    // the pane down to just below the last row (the empty rest of the full-height pane is cut off)
    const pane = await boxOf(page, page.locator("lc-tree-pane, .tree-pane"));
    const last = await page.locator(".tree-row").last().boundingBox();
    await save(page, "tree-filters.png", { ...pane, height: last.y + last.height + 16 - pane.y });
  },

  "changed-tests": async () => {
    const page = await openApp("?mock&coverage");
    await prepare(page);
    // the Changed chip (the mock's GET /api/changed-tests: the tests of the edited OrderServiceTests.cs)
    await page.locator(".tree-pane button, lc-tree-pane button", { hasText: /^\s*Changed/ }).first().click();
    await settle(page, 700);
    const pane = await boxOf(page, page.locator("lc-tree-pane, .tree-pane"));
    const last = await page.locator(".tree-row").last().boundingBox();
    await save(page, "changed-tests.png", { ...pane, height: last.y + last.height + 16 - pane.y });
  },

  "coverage-report": async () => {
    const page = await openApp("?mock&coverage");
    await prepare(page);
    await page.locator(".rail-item", { hasText: /Coverage/ }).first().click();
    await page.waitForTimeout(400);
    await page.locator(".cov-file", { hasText: "OrderService.cs" }).first().click();
    await page.waitForSelector(".src-line.cov");
    await settle(page);
    await save(page, "coverage-report.png", undefined, { resize: FULL });
  },

  "coverage-line-tests": async () => {
    const page = await openApp("?mock&coverage", { height: 1300 });
    await prepare(page);
    await page.locator(".rail-item", { hasText: /Coverage/ }).first().click();
    await page.waitForTimeout(400);
    await page.locator(".cov-file", { hasText: "OrderService.cs" }).first().click();
    await page.waitForSelector(".src-line.cov");
    await settle(page);
    const line = page.locator(".src-line.cov", { hasText: "CalculateTotal(" }).first();
    await line.locator(".cov-hits, button").first().click();
    await page.waitForSelector(".cov-lt-row");
    await settle(page);
    // the code view's left part: a few lines above the clicked one down to below the popover
    const code = await boxOf(page, page.locator("lc-code-view").first());
    const l = await line.boundingBox();
    const pop = await page.locator(".cov-lt-head").first().evaluate((h) => {
      let e = h;
      while (e.parentElement && !["fixed", "absolute"].includes(getComputedStyle(e).position)) e = e.parentElement;
      const r = e.getBoundingClientRect();
      return { x: r.x, y: r.y, width: r.width, height: r.height };
    });
    const y = Math.max(code.y, Math.min(l.y, pop.y) - 3 * l.height);
    const bottom = Math.min(code.y + code.height, Math.max(l.y + l.height, pop.y + pop.height) + 3 * l.height);
    const width = Math.min(code.width, Math.max(820, pop.x + pop.width + 40 - code.x));
    await save(page, "coverage-line-tests.png", { x: code.x, y, width, height: bottom - y });
  },

  "run-compare": async () => {
    const page = await openApp("?mock");
    await prepare(page);
    await page.locator("lc-run-switcher button").first().click();
    await page.waitForTimeout(300);
    await page.locator(".run-menu-item", { hasText: "Compare with" }).first().click();
    await page.waitForTimeout(300);
    await page.locator(".run-menu-item.run-menu-run").nth(2).click();
    await page.waitForSelector("lc-run-diff");
    await settle(page);
    await save(page, "run-compare.png", await boxOf(page, page.locator("lc-run-diff")));
  },

  "command-palette": async () => {
    const page = await openApp("?mock");
    await prepare(page);
    await page.keyboard.press("Control+k");
    await page.waitForTimeout(300);
    await page.keyboard.type("run");
    await settle(page, 600);
    await save(page, "command-palette.png", undefined, { resize: FULL });
  },

  "build-errors": async () => {
    const page = await openApp("?mock");
    await page.locator("button", { hasText: "Show errors" }).first().click();
    await page.waitForSelector(".build-error-list");
    await settle(page);
    await save(page, "build-errors.png", undefined, { resize: FULL });
  },

  watch: async () => {
    const page = await openApp("?mock&coverage");
    await prepare(page);
    await page.locator("button", { hasText: /^\s*Watch\s*$/ }).first().click();
    // the mock's watch "file change" fires ~3 s later and runs SampleTests' affected tests
    await page.waitForFunction(() => window.__labcoat.store.get().currentRun, null, { timeout: 15000 });
    await idle(page);
    await settle(page, 900);
    const strip = await page.locator("lc-watch-strip").first().boundingBox();
    // down to the last CalculatorTests method (source order: Add, Subtract, Multiply, Divide), at least 6 rows
    const sixth = await page.locator(".tree-row").nth(5).boundingBox();
    const last = await page.locator(".tree-row", { hasText: "Divide_ByZero_Throws" }).first().boundingBox();
    const rows = last && last.y > sixth.y ? last : sixth;
    const top = await page.locator("button", { hasText: "Run all" }).first().boundingBox();
    const x = top.x - 12;
    const y = top.y - 12;
    await save(page, "watch.png", { x, y, width: 1440 - x, height: Math.max(strip.y + strip.height, rows.y + rows.height) + 2 - y });
  },

  "aspire-failure-context": async () => {
    // mock with tracing on: a failed Aspire test shows the services' error spans / logs captured during the test
    const page = await openApp("?mock&tracing", { height: 1200 });
    await prepare(page);
    await select(page, "WeatherPage_RendersForecastTable");
    await page.getByText("Service activity during this test").first().waitFor();
    await settle(page);
    const pane = await boxOf(page, page.locator("lc-detail-pane"));
    const meta = await page.locator("lc-detail-pane lc-history-strip, lc-detail-pane .history-strip").first().boundingBox();
    await save(page, "aspire-failure-context.png", { ...pane, height: meta.y + meta.height + 28 - pane.y });
  },

  "settings-themes": async () => {
    // Motion left at its default ("System", which follows the emulated reduced motion), as the page shows it
    const page = await openApp("?mock", { motionOff: false });
    await prepare(page);
    await page.locator(".rail-item", { hasText: /Settings/ }).first().click();
    await page.waitForSelector("lc-settings-page");
    const more = page.locator(".more-themes .lc-disclosure-btn");
    if ((await more.getAttribute("aria-expanded")) !== "true") await more.click();
    await page.waitForTimeout(300);
    await page.locator(".theme-grid").first().evaluate((e) => e.scrollIntoView({ block: "center" }));
    await settle(page);
    await save(page, "settings-themes.png", undefined, { resize: FULL });
  },
};

// The real Aspire dashboard (AspireSample AppHost running): LC_ASPIRE_DASHBOARD=http://localhost:15179. Read-only: it
// only opens the tests-failing resource's "..." menu and hovers "Commands" (nothing is clicked in the menu). The
// "Endpoint is unsecured" bar is hidden and local paths in the Source column are shortened, in this browser only.
const DASHBOARD = process.env.LC_ASPIRE_DASHBOARD;
if (DASHBOARD)
  shots["aspire-dashboard"] = async () => {
    const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2, colorScheme: "dark", reducedMotion: "reduce" });
    const page = await ctx.newPage();
    await page.goto(DASHBOARD);
    await page.waitForSelector("text=tests-failing");
    await page.waitForTimeout(1500);
    await page.evaluate(() => {
      // the widest element that holds just the warning (the bar, not the page around it)
      const bar = [...document.querySelectorAll("body *")]
        .filter((e) => e.textContent.includes("Endpoint is unsecured") && e.getBoundingClientRect().height < 80)
        .sort((a, b) => b.getBoundingClientRect().width - a.getBoundingClientRect().width)[0];
      if (bar) bar.style.display = "none";
      const home = /\/home\/[^/]+\/(Repository\/)?[^/\s]+/g;
      const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      while (w.nextNode()) w.currentNode.nodeValue = w.currentNode.nodeValue.replace(home, "~/src/labcoat");
    });
    const row = page.locator("[role=row]", { hasText: "tests-failing" }).first();
    await row.locator("fluent-button, button").last().click(); // the "..." (Actions) menu
    await page.waitForTimeout(600);
    const commands = await page.getByRole("menuitem", { name: "Commands" }).first().boundingBox();
    await page.mouse.move(commands.x + commands.width / 2, commands.y + commands.height / 2);
    await page.waitForTimeout(900);
    const sub = await page.getByText("Toggle test tracing").filter({ visible: true }).first().boundingBox();
    await save(page, "aspire-dashboard.png", { x: 0, y: 0, width: 1440, height: Math.min(900, sub.y + sub.height + 48) }, { resize: FULL });
  };

// theme comparison: the canonical state per family × mode, 1280 x 800 at 1x
for (const family of FAMILIES)
  for (const mode of ["light", "dark"])
    shots[`themes/${family}-${mode}`] = async () =>
      save(await canonical({ family, mode, width: 1280, height: 800, scale: 1 }), `themes/${family}-${mode}.png`);

const wanted = Object.keys(shots).filter((k) => !ONLY || ONLY.some((o) => k === o || k.startsWith(`${o}/`)));
let next = 0;
await Promise.all(
  Array.from({ length: 4 }, async () => {
    while (next < wanted.length) {
      const name = wanted[next++];
      // one retry: a glitch caught by checkTree() (or a slow mock step) is retaken, and reported either way
      await shots[name]().catch(async (e) => {
        warnings.push(`${name} (retried): ${e.message.split("\n")[0]}`);
        await shots[name]().catch((e2) => errors.push(`${name}: ${e2.message.split("\n")[0]}`));
      });
    }
  }),
);
await browser.close();
await stop();
console.log(written.sort().join("\n"));
if (warnings.length) console.log(`\n${warnings.join("\n")}`);
console.log(errors.length ? `\n${errors.join("\n")}` : "\nno errors");
process.exit(errors.length ? 1 : 0);
