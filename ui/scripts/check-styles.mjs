// Style regression check for CSS refactors: freeze a baseline, change the CSS, compare.
//
//   npm run check:styles -- --update       write the baseline (ui/.styles-baseline/baseline, not in git)
//   npm run check:styles                   compare against it; exit 1 when a style or a screenshot changed
//   npm run check:styles -- --screens=selected,gallery --themes=github-dark   only some screens / themes
//   npm run check:styles -- --threshold=0.2                                  pixel-diff threshold in % (default 0.05)
//   npm run check:styles -- http://127.0.0.1:5490/                           use a running dev server (else one is
//                                                                             started: $LABCOAT_CHECK_PORT, default 5300)
//
// Each screen is taken in 3 theme families (catppuccin, github, dracula) × light / dark at 1440 x 900, from the mock
// backend (?mock) with the same determinism as docs-screenshots.mjs: fixed clock, seeded Math.random, motion off,
// no confetti. Per screen and theme it records
//   - a screenshot (pixel diff against the baseline, a red-on-grey diff image in .styles-baseline/diff/)
//   - the computed style of key elements (fonts, colors, spacing, radius, transitions / animations)
// and once per page kind (app, gallery) the declared CSS rules: every custom property, font-size / line-height and
// transition / animation declaration by @layer / @media / selector. Motion is off while capturing, so computed
// transitions are "none": the rule snapshot is what shows easing and duration changes.
import { chromium } from "playwright";
import { mkdirSync, readFileSync, rmSync, writeFileSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { baseUrlOr, startApp, startGallery } from "./lib/dev-server.mjs";

const ui = fileURLToPath(new URL("..", import.meta.url));
const ROOT = path.join(ui, ".styles-baseline");
const args = process.argv.slice(2);
const opt = (name) => args.find((a) => a.startsWith(`--${name}=`))?.split("=")[1];
const UPDATE = args.includes("--update");
const THRESHOLD = Number(opt("threshold") ?? 0.05); // % of pixels that may differ per screenshot
const OUT = path.join(ROOT, UPDATE ? "baseline" : "current");
const BASE = path.join(ROOT, "baseline");
const list = (s) => s?.split(",").map((x) => x.trim()).filter(Boolean);

const FAMILIES = ["catppuccin", "github", "dracula"];
const THEMES = FAMILIES.flatMap((f) => ["light", "dark"].map((m) => `${f}-${m}`)).filter((t) => !list(opt("themes")) || list(opt("themes")).includes(t));
const CLOCK = new Date(2026, 9, 8, 10, 24, 0); // the docs screenshots' clock

// computed style per element: the first visible match of each selector (missing ones are skipped)
const PROPS = [
  "font-family", "font-size", "line-height", "font-weight", "letter-spacing",
  "color", "background-color", "border-top-color", "border-top-width", "outline-color",
  "padding-top", "padding-right", "padding-bottom", "padding-left", "margin-top", "margin-bottom", "gap",
  "border-top-left-radius", "border-bottom-right-radius", "box-shadow",
  "transition-property", "transition-duration", "transition-timing-function",
  "animation-name", "animation-duration", "animation-timing-function",
];
const APP_ELEMENTS = [
  "body", ".rail-item", ".rail-item.active, .rail-item[aria-current]", ".rail-label", ".toolbar", ".toolbar .btn", ".btn-accent",
  ".search-box", ".tree-filters .chip", ".chip-count", ".tree-row", ".tree-row.selected, .tree-row[aria-selected=true]",
  ".run-switcher .run-trigger", ".run-dot", ".run-progress", ".run-summary", ".run-pill",
  ".pane-tab", ".pane-tab[aria-selected=true]", ".tab-pill",
  ".detail-title", ".detail-meta", ".section-header h3", ".stat-card", ".stat-label", ".stat-value",
  ".detail-error", ".mb-message", ".mb-title", ".lc-code", ".lc-code-line", ".prop-value.mono", ".empty-title", ".empty-sub",
  ".badge", ".kbd-combo kbd, kbd", ".ws-step", ".ws-count",
  ".coverage-page", ".cov-card", ".cov-card-value", ".cov-file", ".cov-table", "lc-code-view", ".src-line", ".src-line.cov",
  ".settings-page", ".settings-section-title", ".settings-card", ".setting-label", ".setting-desc", ".switch-track", ".segment", ".lc-disclosure-btn",
  ".palette-dialog", ".palette-input", ".palette-item", ".palette-item[aria-selected=true]", ".palette-kbd",
  ".lc-dialog", ".lc-tooltip", ".lc-menu",
];
const GALLERY_ELEMENTS = [
  "body", ".g-section h2", ".btn", ".btn-accent", ".chip", ".badge", ".pane-tab", ".segment", ".switch-track",
  ".mb-title", ".mb-message", ".mb-message.plain", ".empty-title", ".empty-sub", ".prop-name", ".prop-value", ".prop-value.mono",
  ".lc-code", ".lc-code-line", ".meter", ".lc-progress", "kbd", ".lc-disclosure-btn",
];
const GALLERY_SECTIONS = ["typography", "messagebar", "empty", "propgrid", "code", "button", "chip"];

const { base, stop } = await baseUrlOr(args.find((a) => /^(\d+|https?:)/.test(a)), () => startApp({ frozen: true }));
const SCREENS_WANTED = (name) => !list(opt("screens")) || list(opt("screens")).includes(name);
const gallery = SCREENS_WANTED("gallery") ? await startGallery({ frozen: true }) : null;

const browser = await chromium.launch();
const errors = [];

async function open(url, { family, mode, app = true, height = 900 }) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height }, deviceScaleFactor: 1, colorScheme: mode, reducedMotion: "reduce" });
  await ctx.addInitScript(
    ([f, m, app]) => {
      let s = 0x5eed; // seeded Math.random (mulberry32), as in docs-screenshots.mjs
      Math.random = () => {
        s = (s + 0x6d2b79f5) | 0;
        let t = Math.imul(s ^ (s >>> 15), 1 | s);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
      };
      if (!app || sessionStorage.getItem("check-styles")) return;
      sessionStorage.setItem("check-styles", "1");
      localStorage.clear();
      localStorage.setItem("mste.themeFamily", f);
      localStorage.setItem("mste.theme", m);
      localStorage.setItem("mste.settings", JSON.stringify({ confetti: false, motion: "off", $v: 2 }));
    },
    [family, mode, app],
  );
  await ctx.clock.install({ time: CLOCK });
  await ctx.clock.resume();
  const page = await ctx.newPage();
  page.on("pageerror", (e) => errors.push(`${url} ${family}-${mode}: ${e.message}`));
  await page.goto(url);
  if (!app) await page.evaluate(() => document.documentElement.setAttribute("data-motion", "off"));
  return page;
}

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
  await page.mouse.move(0, 899);
  await page.waitForTimeout(ms);
};
/** fix the mock's Inventory.Tests build (the second Rediscover does), run everything once */
async function prepare(page) {
  await page.waitForSelector(".tree-row");
  await page.waitForTimeout(500);
  for (let i = 0; i < 2; i++) {
    await page.locator("button", { hasText: "Rediscover" }).first().click();
    await page.waitForTimeout(600);
    await idle(page);
  }
  await page.locator("button", { hasText: "Run all" }).first().click();
  await page.waitForTimeout(400);
  await idle(page);
  await settle(page, 600);
}
const appPage = async (t) => {
  const [family, mode] = t.split("-");
  const page = await open(`${base}?mock&coverage&snapshots`, { family, mode });
  await prepare(page);
  return page;
};

const SCREENS = {
  main: appPage,
  selected: async (t) => {
    // a new Verify snapshot: failure message and the "Received output" code block (DS code block), scrolled into view
    const page = await appPage(t);
    await page.locator(".tree-row", { hasText: "Invoice_NewSnapshot" }).first().click();
    await page.waitForSelector("lc-detail-pane .lc-code");
    await page.locator("lc-detail-pane .lc-code").first().evaluate((e) => {
      e.scrollIntoView({ block: "center" });
      // only the pane's own scroller moves: undo what scrollIntoView did to overflow-hidden ancestors (the shell)
      for (let a = e.parentElement; a; a = a.parentElement) if (!/auto|scroll/.test(getComputedStyle(a).overflowY)) a.scrollTop = 0;
    });
    await settle(page, 900);
    return page;
  },
  watch: async (t) => {
    const page = await appPage(t);
    await page.locator("button", { hasText: /^\s*Watch\s*$/ }).first().click();
    await page.waitForFunction(() => window.__labcoat.store.get().currentRun, null, { timeout: 15000 });
    await idle(page);
    await settle(page, 900);
    return page;
  },
  coverage: async (t) => {
    const page = await appPage(t);
    await page.locator(".rail-item", { hasText: /Coverage/ }).first().click();
    await page.waitForTimeout(400);
    await page.locator(".cov-file", { hasText: "OrderService.cs" }).first().click();
    await page.waitForSelector(".src-line.cov");
    await settle(page);
    return page;
  },
  settings: async (t) => {
    const page = await appPage(t);
    await page.locator(".rail-item", { hasText: /Settings/ }).first().click();
    await page.waitForSelector("lc-settings-page");
    await settle(page);
    return page;
  },
  palette: async (t) => {
    const page = await appPage(t);
    await page.keyboard.press("Control+k");
    await page.waitForTimeout(300);
    await page.keyboard.type("run");
    await settle(page, 600);
    return page;
  },
  gallery: async (t) => {
    const [family, mode] = t.split("-");
    const page = await open(`${gallery.url}?theme=${t}`, { family, mode, app: false });
    await page.waitForSelector(".g-section");
    // one tall page with only the sections of interest, no scroll container, no nav
    await page.addStyleTag({
      content: `body{overflow:visible!important}.g-app{height:auto!important;grid-template-columns:1fr!important;grid-template-areas:'header' 'main'!important}.g-main{overflow:visible!important}.g-nav{display:none}
        .g-section:not(${GALLERY_SECTIONS.map((s) => `#${s}`).join(",")}){display:none}`,
    });
    await settle(page, 400);
    return page;
  },
};

/** Wall-clock times and measured durations (the clock runs; runs take real time) → zeros, so they don't show up as
 *  pixel changes. Same digit count, same layout on every pass. */
function normalizeText() {
  const re = /\b\d{1,2}:\d{2}(:\d{2})?\b|\b\d+(\.\d+)?(?=\s?(ms|s|min)\b)/g;
  const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  while (w.nextNode()) {
    const v = w.currentNode.nodeValue;
    if (re.test(v)) w.currentNode.nodeValue = v.replace(re, (m) => m.replace(/\d/g, "0"));
    re.lastIndex = 0;
  }
}

/** computed styles of `selectors` + (rules=true) the declared rules of every stylesheet, in the page */
function capture([selectors, props, rules]) {
  const elements = {};
  for (const sel of selectors) {
    const el = [...document.querySelectorAll(sel)].find((e) => {
      const r = e.getBoundingClientRect();
      return r.width > 0 && r.height > 0;
    });
    if (!el) continue;
    const cs = getComputedStyle(el);
    elements[sel] = Object.fromEntries(props.map((p) => [p, cs.getPropertyValue(p)]));
  }
  if (!rules) return { elements };
  const out = {};
  const wanted = (p) => p.startsWith("--") || /^(font-size|line-height|transition|animation)/.test(p);
  const norm = (t) => t.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\s+/g, " ").replace(/\*(?=:)/g, "").replace(/ ?([,{};:]) ?/g, "$1");
  const fromSource = (raw, selector, prop) => {
    const i = raw.indexOf(`${norm(selector)}{`);
    if (i < 0) return "";
    const body = raw.slice(i, raw.indexOf("}", i));
    return body.match(new RegExp(`[{;]${prop}:([^;]*)`))?.[1].replace(/,/g, ", ").replace(/!important/, " !important");
  };
  let raw = "";
  const walk = (list, ctx) => {
    for (const r of list) {
      if (r instanceof CSSKeyframesRule) continue;
      let c = ctx;
      if (r instanceof CSSMediaRule) c = `${ctx}@media ${r.conditionText} `;
      else if (r instanceof CSSSupportsRule) c = `${ctx}@supports ${r.conditionText} `;
      else if (window.CSSLayerBlockRule && r instanceof CSSLayerBlockRule) c = `${ctx}@layer ${r.name} `;
      else if (r instanceof CSSStyleRule) {
        c = `${ctx}${r.selectorText} `;
        const decl = {};
        const s = r.style;
        for (let i = 0; i < s.length; i++) {
          let p = s[i];
          if (!wanted(p)) continue;
          let v = s.getPropertyValue(p).trim();
          // a longhand of a shorthand with var(): only the shorthand has a value; when another longhand of it is set
          // too, the CSSOM has no value at all: then the declaration is read from the stylesheet's source text
          if (!v)
            for (const sh of ["animation", "transition", "font"]) {
              if (!p.startsWith(`${sh}-`)) continue;
              p = sh;
              v = s.getPropertyValue(sh).trim() || fromSource(raw, r.selectorText, sh) || "(unreadable)";
            }
          decl[p] = v.replace(/\s*!important$/, "") + (s.getPropertyPriority(s[i]) || /!important$/.test(v) ? " !important" : "");
        }
        if (Object.keys(decl).length) {
          let key = c.trim();
          for (let n = 2; key in out; n++) key = `${c.trim()} #${n}`;
          out[key] = decl;
        }
      }
      if (r.cssRules) walk(r.cssRules, c);
    }
  };
  for (const sheet of [...document.styleSheets, ...(document.adoptedStyleSheets ?? [])]) {
    try {
      raw = norm(sheet.ownerNode?.textContent ?? "");
      walk(sheet.cssRules, "");
    } catch {
      /* cross-origin sheet (fonts): no rules to read */
    }
  }
  return { elements, rules: out };
}

const result = { elements: {}, rules: {} };
const jobs = [];
for (const screen of Object.keys(SCREENS)) if (SCREENS_WANTED(screen)) for (const t of THEMES) jobs.push([screen, t]);
const started = Date.now();
let next = 0;
await Promise.all(
  Array.from({ length: Number(opt("jobs") ?? 4) }, async () => {
    while (next < jobs.length) {
      const [screen, t] = jobs[next++];
      const take = async () => {
        const page = await SCREENS[screen](t);
        const isGallery = screen === "gallery";
        await page.evaluate(() => document.fonts.ready);
        if (!isGallery) await page.evaluate(normalizeText);
        const kind = isGallery ? "gallery" : "app";
        // rules: the union over all captures (components load their CSS with them)
        const snap = await page.evaluate(capture, [isGallery ? GALLERY_ELEMENTS : APP_ELEMENTS, PROPS, true]);
        Object.assign((result.rules[kind] ??= {}), snap.rules);
        (result.elements[screen] ??= {})[t] = snap.elements;
        mkdirSync(path.join(OUT, screen), { recursive: true });
        await page.screenshot({ path: path.join(OUT, screen, `${t}.png`), fullPage: isGallery, animations: "disabled", caret: "hide" });
        await page.context().close();
      };
      await take().catch(async (e) => {
        console.log(`  ${screen} ${t}: retrying (${e.message.split("\n")[0]})`);
        await take().catch((e2) => errors.push(`${screen} ${t}: ${e2.message.split("\n")[0]}`));
      });
    }
  }),
);
// a partial run (--screens / --themes) only replaces what it captured; its rules are added to the full run's
const PARTIAL = !!(opt("screens") || opt("themes"));
const stylesFile = path.join(OUT, "styles.json");
if (UPDATE && PARTIAL && existsSync(stylesFile)) {
  const old = JSON.parse(readFileSync(stylesFile, "utf8"));
  for (const [screen, themes] of Object.entries(result.elements)) Object.assign((old.elements[screen] ??= {}), themes);
  for (const [kind, rules] of Object.entries(result.rules)) Object.assign((old.rules[kind] ??= {}), rules);
  writeFileSync(stylesFile, JSON.stringify(old, null, 1));
} else writeFileSync(stylesFile, JSON.stringify(result, null, 1));

let failed = errors.length > 0;
if (UPDATE) {
  console.log(`baseline: ${jobs.length} captures in ${BASE} (${Math.round((Date.now() - started) / 1000)} s)`);
} else if (!existsSync(path.join(BASE, "styles.json"))) {
  console.log("no baseline yet: run `npm run check:styles -- --update` first (before the change)");
  failed = true;
} else {
  failed = (await compare()) || failed;
}
await browser.close();
await stop();
await gallery?.close();
if (errors.length) console.log(`\nerrors:\n  ${errors.join("\n  ")}`);
process.exit(failed ? 1 : 0);

// ---- compare ------------------------------------------------------------------------------------------------------
async function compare() {
  const before = JSON.parse(readFileSync(path.join(BASE, "styles.json"), "utf8"));
  let changes = 0;
  const lines = [];
  const show = (v) => (v === undefined ? "(none)" : v === "" ? '""' : v);

  // computed styles: per screen, per element, property before → after (the themes it changed in, if not all)
  for (const screen of Object.keys(result.elements)) {
    const byChange = new Map();
    for (const t of Object.keys(result.elements[screen])) {
      const a = before.elements[screen]?.[t];
      const b = result.elements[screen][t];
      if (!a) continue;
      for (const sel of new Set([...Object.keys(a), ...Object.keys(b)])) {
        if (!a[sel] || !b[sel]) {
          const k = `${sel}\u0000${a[sel] ? "element gone" : "element new"}`;
          byChange.set(k, [...(byChange.get(k) ?? []), t]);
          continue;
        }
        for (const p of PROPS)
          if (a[sel][p] !== b[sel][p]) {
            const k = `${sel}\u0000${p}  ${show(a[sel][p])} → ${show(b[sel][p])}`;
            byChange.set(k, [...(byChange.get(k) ?? []), t]);
          }
      }
    }
    if (!byChange.size) continue;
    changes += byChange.size;
    lines.push(`\n${screen}`);
    let last = "";
    for (const [k, themes] of [...byChange].sort()) {
      const [sel, what] = k.split("\u0000");
      if (sel !== last) lines.push(`  ${sel}`);
      last = sel;
      const n = Object.keys(result.elements[screen]).length;
      lines.push(`    ${what}${themes.length === n ? "" : `   [${themes.join(", ")}]`}`);
    }
  }

  // declared rules
  for (const kind of Object.keys(result.rules)) {
    const a = before.rules?.[kind] ?? {};
    const b = result.rules[kind];
    const out = [];
    for (const key of [...new Set([...Object.keys(a), ...Object.keys(b)])].sort()) {
      if (!a[key]) {
        out.push(`  + ${key}  { ${Object.entries(b[key]).map(([p, v]) => `${p}: ${v}`).join("; ")} }`);
        continue;
      }
      if (!b[key]) {
        if (PARTIAL) continue; // not loaded by the screens of a partial run
        out.push(`  - ${key}  { ${Object.entries(a[key]).map(([p, v]) => `${p}: ${v}`).join("; ")} }`);
        continue;
      }
      for (const p of new Set([...Object.keys(a[key]), ...Object.keys(b[key])]))
        if (a[key][p] !== b[key][p]) out.push(`  ${key}\n    ${p}  ${show(a[key][p])} → ${show(b[key][p])}`);
    }
    if (out.length) {
      changes += out.length;
      lines.push(`\nCSS rules (${kind})`, ...out);
    }
  }

  // pixels, in a browser canvas (no image dependencies)
  const pixels = [];
  const page = await browser.newPage();
  for (const screen of Object.keys(result.elements))
    for (const t of Object.keys(result.elements[screen])) {
      const fa = path.join(BASE, screen, `${t}.png`);
      const fb = path.join(OUT, screen, `${t}.png`);
      if (!existsSync(fa)) continue;
      const d = await page.evaluate(pixelDiff, [readFileSync(fa).toString("base64"), readFileSync(fb).toString("base64")]);
      const file = path.join(ROOT, "diff", screen, `${t}.png`);
      rmSync(file, { force: true });
      if (d.pct === 0) continue;
      mkdirSync(path.dirname(file), { recursive: true });
      if (d.png) writeFileSync(file, Buffer.from(d.png.split(",")[1], "base64"));
      pixels.push({ screen, t, ...d, over: d.pct > THRESHOLD });
    }
  await page.close();
  const over = pixels.filter((p) => p.over);
  if (pixels.length) {
    lines.push(`\npixels (threshold ${THRESHOLD} %; diff images in ${path.relative(ui, path.join(ROOT, "diff"))}/)`);
    for (const p of pixels) lines.push(`  ${p.over ? "✗" : "·"} ${`${p.screen} ${p.t}`.padEnd(30)} ${p.pct < 0.001 ? "< 0.001" : p.pct.toFixed(3)} %${p.size ? `  (size ${p.size})` : ""}`);
  }

  console.log(lines.join("\n") || "no style changes, no pixel changes");
  console.log(`\n${changes} style change(s), ${over.length} screenshot(s) over ${THRESHOLD} % (${Math.round((Date.now() - started) / 1000)} s)`);
  return changes > 0 || over.length > 0;
}

/** share of pixels that differ (any channel by more than 2), and a diff image: changed pixels red over a faded copy */
async function pixelDiff([a, b]) {
  const load = (b64) =>
    new Promise((res, rej) => {
      const img = new Image();
      img.onload = () => res(img);
      img.onerror = rej;
      img.src = `data:image/png;base64,${b64}`;
    });
  const [ia, ib] = await Promise.all([load(a), load(b)]);
  const w = Math.max(ia.width, ib.width);
  const h = Math.max(ia.height, ib.height);
  const data = (img) => {
    const c = new OffscreenCanvas(w, h);
    const x = c.getContext("2d");
    x.drawImage(img, 0, 0);
    return x.getImageData(0, 0, w, h).data;
  };
  const da = data(ia);
  const db = data(ib);
  const c = new OffscreenCanvas(w, h);
  const x = c.getContext("2d");
  const out = x.createImageData(w, h);
  let diff = 0;
  for (let i = 0; i < da.length; i += 4) {
    const changed = Math.abs(da[i] - db[i]) > 2 || Math.abs(da[i + 1] - db[i + 1]) > 2 || Math.abs(da[i + 2] - db[i + 2]) > 2 || Math.abs(da[i + 3] - db[i + 3]) > 2;
    if (changed) diff++;
    const g = (db[i] + db[i + 1] + db[i + 2]) / 3;
    out.data.set(changed ? [230, 30, 40, 255] : [g, g, g, 60], i);
  }
  const size = ia.width !== ib.width || ia.height !== ib.height ? `${ia.width}x${ia.height} → ${ib.width}x${ib.height}` : undefined;
  const pct = (diff / (w * h)) * 100;
  if (!pct) return { pct: 0 };
  x.putImageData(out, 0, 0);
  const blob = await c.convertToBlob({ type: "image/png" });
  const png = await new Promise((res) => {
    const r = new FileReader();
    r.onload = () => res(r.result);
    r.readAsDataURL(blob);
  });
  return { pct, size, png };
}
