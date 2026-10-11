// Theme contrast check: resolves every --lc-* token of every theme family (light + dark) in Chromium and checks the
// WCAG 2.x contrast of the pairs the UI actually draws. Reads the real theme files (design-system/src/themes/*.css,
// in the order design-system/src/styles.css imports them).
//
//   node scripts/themes-check.mjs [--only <family>] [--json out.json] [--tune]
//
// Hard checks fail the run (exit 1); soft checks are reported only. --tune prints, per theme, the smallest
// --lc-code-tint-shift that keeps every syntax color ≥ 4.5:1 on the tinted source lines.
import { chromium } from "playwright";
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const arg = (n) => (process.argv.includes(n) ? process.argv[process.argv.indexOf(n) + 1] : null);
const only = arg("--only");
const jsonOut = arg("--json");
const tune = process.argv.includes("--tune");

const ds = new URL("../design-system/src/", import.meta.url).pathname;
const themeFiles = [...readFileSync(join(ds, "styles.css"), "utf8").matchAll(/@import\s+"\.\/themes\/([a-z0-9-]+)\.css"/g)].map((m) => m[1]);
const css = themeFiles.map((f) => readFileSync(join(ds, `themes/${f}.css`), "utf8")).join("\n");
const families = themeFiles.filter((f) => !only || f === only);
const tokens = [...new Set([...css.matchAll(/(--lc-[a-z0-9-]+)\s*:/g)].map((m) => m[1]))].filter((t) => !/shadow|tint-shift|-style$|-weight$/.test(t));

// Backgrounds the app derives from tokens (keep in sync with src/styles.css: .src-line.in-range / .err-line).
const DERIVED_BG = {
  "range-band": "color-mix(in srgb, var(--lc-accent) 9%, var(--lc-console-bg))",
  "err-band": "color-mix(in srgb, var(--lc-status-failed) 16%, var(--lc-console-bg))",
  // selected segment / current nav link (design-system segmented.css .segment.on and its hover)
  "accent-tint-surface": "color-mix(in srgb, var(--lc-accent) 22%, var(--lc-bg-surface))",
  "accent-tint-page": "color-mix(in srgb, var(--lc-accent) 22%, var(--lc-bg-page))",
  "accent-tint-shell": "color-mix(in srgb, var(--lc-accent) 22%, var(--lc-bg-shell))",
  // coverage / rate pills (.tab-pill.tone-*: tone 18% over the tab bar, text = tone-text mixed with fg)
  ...Object.fromEntries(
    [["passed", "passed"], ["skipped", "skipped"], ["failed", "failed"]].flatMap(([t]) => [
      [`pill-bg-${t}`, `color-mix(in srgb, var(--lc-status-${t}) 18%, var(--lc-bg-surface))`],
      [`pill-bg-page-${t}`, `color-mix(in srgb, var(--lc-status-${t}) 18%, var(--lc-bg-page))`],
      [`pill-fg-${t}`, `color-mix(in srgb, var(--lc-status-${t}-text) 50%, var(--lc-fg))`],
    ]),
  ),
  // the "next" failure in the run overview (src/components/run-overview.css: accent 7% over the card)
  "accent-wash": "color-mix(in srgb, var(--lc-accent) 7%, var(--lc-bg-surface))",
  // project names in the output panel / strip (src/components/log-panel.ts: project accent 35% + text)
  ...Object.fromEntries(
    ["teal", "marigold", "peach", "coral", "royal-blue", "orchid", "brand-blue", "seafoam", "jade", "berry"].flatMap((t) => [
      [`tag-text-${t}`, `color-mix(in srgb, var(--lc-tag-${t}) 35%, var(--lc-fg))`],
      [`tag-console-${t}`, `color-mix(in srgb, var(--lc-tag-${t}) 35%, var(--lc-fg))`],
    ]),
  ),
};
// every syntax token (design-system/src/themes/*.css "syntax" blocks)
const CODE = ["default", "comment", "doctag", "keyword", "built-in", "type", "class", "function", "title", "params", "string", "subst", "number", "literal", "meta", "attr", "attribute", "variable", "symbol", "operator", "punctuation", "tag", "name", "addition", "deletion", "regexp"].map((c) => "code-" + c);
const TINTED = ["coverage-covered", "coverage-uncovered", "coverage-partial", "range-band", "err-band"];
// syntax colors on tinted lines: same hue and chroma, OKLCH lightness moved by the theme's --lc-code-tint-shift
const shifted = (t) => `oklch(from var(--lc-${t}) calc(l + var(--lc-code-tint-shift, 0)) c h)`;

const browser = await chromium.launch();
const page = await browser.newPage();
await page.setContent(`<!doctype html><style>${css}</style><body></body>`);

async function resolve(fam, mode, shiftOverride) {
  return page.evaluate(
    ({ fam, mode, tokens, derived, code, shiftOverride, shiftedExprs }) => {
      const root = document.documentElement;
      root.setAttribute("data-theme-family", fam);
      root.setAttribute("data-theme", mode);
      if (shiftOverride === null) root.style.removeProperty("--lc-code-tint-shift");
      else root.style.setProperty("--lc-code-tint-shift", String(shiftOverride));
      const probe = document.createElement("div");
      document.body.append(probe);
      const c = document.createElement("canvas");
      c.width = c.height = 1;
      const ctx = c.getContext("2d", { willReadFrequently: true });
      const rgba = (expr) => {
        probe.style.color = "";
        probe.style.color = expr;
        const v = getComputedStyle(probe).color;
        ctx.clearRect(0, 0, 1, 1);
        ctx.fillStyle = "#000";
        ctx.fillStyle = v;
        ctx.fillRect(0, 0, 1, 1);
        const [r, g, b, a] = ctx.getImageData(0, 0, 1, 1).data;
        return [r, g, b, a / 255];
      };
      const cs = getComputedStyle(root);
      const missing = tokens.filter((t) => !cs.getPropertyValue(t).trim());
      const col = {};
      for (const t of tokens) col[t] = rgba(`var(${t})`);
      for (const [k, e] of Object.entries(derived)) col["--lc-" + k] = rgba(e);
      for (const t of code) col["--lc-" + t + "@tint"] = rgba(shiftedExprs[t]);
      probe.remove();
      return { col, missing, scheme: cs.colorScheme, shift: cs.getPropertyValue("--lc-code-tint-shift").trim() };
    },
    { fam, mode, tokens, derived: DERIVED_BG, code: CODE, shiftOverride: shiftOverride ?? null, shiftedExprs: Object.fromEntries(CODE.map((t) => [t, shifted(t)])) },
  );
}

// ---- color math ----
const over = (fg, bg) => [0, 1, 2].map((i) => fg[i] * fg[3] + bg[i] * (1 - fg[3])).concat(1);
const lum = (c) => {
  const f = (v) => ((v /= 255) <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
  return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2]);
};
const ratio = (a, b) => {
  const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
};
const hex = (c) => "#" + c.slice(0, 3).map((v) => Math.round(v).toString(16).padStart(2, "0")).join("");

// ---- checks: [fg, bg, min, why] (names without the --lc- prefix) ----
const textBgs = ["bg-page", "bg-shell", "bg-surface", "bg-subtle", "bg-popup", "bg-selected", "bg-hover", "bg-menu-hover"];
const hard = [];
const soft = [];
const add = (list, fgs, bgs, min, why) => fgs.forEach((f) => bgs.forEach((b) => list.push([f, b, min, why])));
add(hard, ["fg"], textBgs, 4.5, "body text");
add(hard, ["fg-secondary"], ["bg-page", "bg-shell", "bg-surface", "bg-subtle", "bg-popup", "bg-selected"], 4.5, "secondary text");
add(hard, ["fg-muted"], ["bg-page", "bg-shell", "bg-surface", "bg-subtle", "bg-popup", "bg-selected", "console-bg"], 4.5, "muted text / line numbers");
add(hard, ["fg-subtext"], ["bg-page", "bg-surface"], 4.5, "sub-labels");
add(hard, ["link"], ["bg-page", "bg-surface", "bg-popup"], 4.5, "links");
add(hard, ["accent", "focus-ring"], ["bg-page", "bg-shell", "bg-surface"], 3, "accent / focus (UI)");
add(hard, ["on-accent"], ["accent"], 4.5, "text on accent");
hard.push(
  ["accent-button-fg", "accent-button-bg", 4.5, "primary button"],
  ["accent-button-hover-fg", "accent-button-hover-bg", 4.5, "primary button hover"],
  ["accent-button-fg", "accent-button-pressed-bg", 4.5, "primary button pressed (keyboard)"],
  ["accent-button-hover-fg", "accent-button-pressed-bg", 4.5, "primary button pressed (pointer)"],
);
add(hard, ["status-passed", "status-failed", "status-skipped", "status-running"], ["bg-page", "bg-surface", "bg-selected"], 3, "status icons (UI)");
add(hard, ["status-muted"], ["bg-page", "bg-surface"], 3, "not-run icon (UI)");
add(hard, ["status-failed-text"], ["bg-page", "bg-surface", "msg-error-bg", "console-error-bg"], 4.5, "failure text");
add(hard, ["status-passed-text", "status-skipped-text"], ["bg-page", "bg-surface", "bg-shell", "bg-hover"], 4.5, "status text (counts, rates)");
add(soft, ["on-status"], ["status-passed", "status-skipped"], 4.5, "text on passed / skipped fills (run diff counts)");
hard.push(["on-status", "status-failed", 4.5, "error badge text"]);
for (const t of ["passed", "skipped", "failed"]) add(hard, [`pill-fg-${t}`], [`pill-bg-${t}`, `pill-bg-page-${t}`], 4.5, "rate pill text");
add(hard, ["fg"], ["accent-tint-surface", "accent-tint-page", "accent-tint-shell"], 4.5, "text on the selected segment / nav item");
add(hard, ["msg-fg"], ["msg-success-bg", "msg-error-bg", "msg-warning-bg", "msg-info-bg"], 4.5, "message bar text");
hard.push(["status-passed", "msg-success-bg", 3, "msg icon"], ["status-failed", "msg-error-bg", 3, "msg icon"], ["status-skipped", "msg-warning-bg", 3, "msg icon"]);
add(hard, ["console-fg"], ["console-bg", "console-hover", "console-error-bg", "console-warn-bg"], 4.5, "console text");
add(hard, ["console-link"], ["console-bg"], 4.5, "console link");
add(hard, CODE, ["console-bg"], 4.5, "syntax");
// tinted source lines (coverage line mode, method range, failing line): shifted syntax colors
for (const t of CODE) for (const b of TINTED) hard.push([t + "@tint", b, 4.5, "syntax on tinted line"]);
const ansi = ["red", "green", "yellow", "blue", "magenta", "cyan", "white", "black"].flatMap((c) => ["ansi-" + c, "ansi-bright-" + c]);
add(hard, ["fg-muted"], ["bg-hover", "accent-wash"], 4.5, "muted text on a hovered / next row");
add(soft, ["fg-muted"], ["bg-menu-hover"], 4.5, "muted on menu hover");
add(hard, ["console-muted"], ["console-bg", "console-hover"], 4.5, "line numbers / console muted text / code header path");
for (const t of ["teal", "marigold", "peach", "coral", "royal-blue", "orchid", "brand-blue", "seafoam", "jade", "berry"]) {
  add(hard, [`tag-text-${t}`], ["bg-surface", "bg-page"], 4.5, "project name (output strip)");
  add(hard, [`tag-console-${t}`], ["console-bg"], 4.5, "project name (output)");
}
add(soft, ["accent"], ["bg-page", "bg-surface"], 4.5, "accent as text");
add(soft, ["status-passed", "status-failed", "status-skipped"], ["bg-page", "bg-surface"], 4.5, "status color as text (should use *-text)");

function evaluate(r, dark) {
  const col = r.col;
  const T = (n) => "--lc-" + n;
  const page = over(col[T("bg-page")], [255, 255, 255, 1]);
  const solid = (t) => {
    const c = col[T(t)];
    if (c[3] >= 1) return c;
    const base = /^(coverage|console|range|err)/.test(t) ? over(col[T("console-bg")], page) : page;
    return over(c, base);
  };
  const run = (list) =>
    list
      .filter(([f]) => !(dark && /ansi-(bright-)?black/.test(f)))
      .map(([f, b, min, why]) => {
        if (!col[T(f)] || !col[T(b)]) return { f, b, min, why, r: 0, fgHex: "missing", bgHex: "missing" };
        const bg = solid(b);
        const fg = over(col[T(f)], bg);
        return { f, b, min, why, r: +ratio(fg, bg).toFixed(2), fgHex: hex(fg), bgHex: hex(bg) };
      });
  const h = run(hard).concat(run(ansi.map((a) => [a, "console-bg", 4.5, "ANSI"])));
  const s = run(soft);
  return { h, s, hardFails: h.filter((x) => x.r < x.min), softFails: s.filter((x) => x.r < x.min) };
}

if (tune) {
  for (const fam of families)
    for (const mode of ["light", "dark"]) {
      const sign = mode === "dark" ? 1 : -1;
      let found = null;
      for (let i = 0; i <= 40 && found === null; i++) {
        const v = +(sign * i * 0.005).toFixed(3);
        const r = await resolve(fam, mode, v);
        const fails = evaluate(r, mode === "dark").hardFails.filter((x) => x.f.endsWith("@tint"));
        if (!fails.length) found = v;
      }
      const cur = (await resolve(fam, mode)).shift;
      console.log(`${fam}-${mode}: minimal --lc-code-tint-shift ${found} (current ${cur || "unset"})`);
    }
  await browser.close();
  process.exit(0);
}

const report = {};
let out = "";
let hardTotal = 0;
for (const fam of families)
  for (const mode of ["light", "dark"]) {
    const key = `${fam}-${mode}`;
    const r = await resolve(fam, mode);
    const { h, s, hardFails, softFails } = evaluate(r, mode === "dark");
    hardTotal += hardFails.length + r.missing.length;
    report[key] = { missing: r.missing, scheme: r.scheme, shift: r.shift, hardFails, softFails, all: h.concat(s), resolved: Object.fromEntries(Object.entries(r.col).map(([t, c]) => [t, c[3] < 1 ? hex(c) + Math.round(c[3] * 255).toString(16).padStart(2, "0") : hex(c)])) };
    out += `\n## ${key}  (color-scheme: ${r.scheme}, code tint shift ${r.shift || "unset"})  hard ${h.length - hardFails.length}/${h.length} pass, soft ${s.length - softFails.length}/${s.length}\n`;
    if (r.scheme !== mode) out += `  WRONG color-scheme: ${r.scheme}\n`;
    if (r.missing.length) out += `  MISSING: ${r.missing.join(", ")}\n`;
    for (const x of hardFails) out += `  HARD ${x.f} on ${x.b}: ${x.r} < ${x.min} (${x.fgHex} on ${x.bgHex}) [${x.why}]\n`;
    for (const x of softFails) out += `  soft ${x.f} on ${x.b}: ${x.r} < ${x.min} (${x.fgHex} on ${x.bgHex}) [${x.why}]\n`;
  }
await browser.close();

// families.ts swatches = [bg-page, fg, accent, status-passed, status-failed, status-skipped] per mode
const famTs = readFileSync(join(ds, "themes/families.ts"), "utf8");
const SW = ["bg-page", "fg", "accent", "status-passed", "status-failed", "status-skipped"];
for (const m of famTs.matchAll(/id: "([a-z0-9-]+)"[\s\S]*?swatches: \{\s*light: \[([^\]]*)\],\s*dark: \[([^\]]*)\]/g)) {
  const [, id, light, dark] = m;
  if (!families.includes(id)) continue;
  for (const [mode, list] of [["light", light], ["dark", dark]]) {
    const got = [...list.matchAll(/"(#[0-9a-f]{6})"/g)].map((x) => x[1]);
    SW.forEach((t, i) => {
      const want = report[`${id}-${mode}`]?.resolved["--lc-" + t];
      if (want && got[i] !== want) {
        hardTotal++;
        out += `  SWATCH families.ts ${id} ${mode}[${i}] (${t}) is ${got[i]}, theme resolves ${want}\n`;
      }
    });
  }
}
for (const id of families) if (!new RegExp(`id: "${id}"`).test(famTs)) (hardTotal++, (out += `  families.ts has no entry for ${id}\n`));
console.log(out);
console.log(`${families.length} families × 2 modes: ${hardTotal} hard failures`);
if (jsonOut) writeFileSync(jsonOut, JSON.stringify(report, null, 1));
process.exit(hardTotal ? 1 : 0);
