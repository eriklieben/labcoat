// Foundations: brand, color tokens (every theme family × light / dark), typography, spacing, radii, motion.
import faviconUrl from "../../public/favicon.svg?url";
import nugetUrl from "../../../assets/logo.svg?url";
import {
  animateEnter,
  brandLogo,
  button,
  computed,
  copyButton,
  countUp,
  disclosure,
  flash,
  h,
  icon,
  keyed,
  list,
  locate,
  messageBar,
  meter,
  popover,
  replayClass,
  reveal,
  scrubGuard,
  segmented,
  setMotionLevel,
  signal,
  skeleton,
  statusIcon,
  tabPill,
  tabs,
  toast,
  THEME_FAMILIES,
  type Child,
  type ThemeFamily,
} from "../src";
import { demo, section } from "./util";

/** every family × mode, from the design system's THEME_FAMILIES (label: "Catppuccin Latte", "Aspire dark", …) */
export const THEMES = THEME_FAMILIES.flatMap((f) =>
  (["light", "dark"] as const).map((mode) => ({
    family: f.id as ThemeFamily,
    mode,
    label: f.flavors[mode] === "Light" || f.flavors[mode] === "Dark" ? `${f.label} ${mode}` : f.flavors[mode].startsWith(f.label) ? f.flavors[mode] : `${f.label} ${f.flavors[mode]}`,
  })),
);
export type ThemeKey = `${ThemeFamily}-${"light" | "dark"}`;

const DESCRIPTIONS: Record<string, string> = {
  "--lc-bg-page": "page background (workspace, panes)",
  "--lc-bg-shell": "app shell: header and nav rail",
  "--lc-bg-surface": "cards, controls, inputs",
  "--lc-bg-subtle": "subtle fills: counts, pressed areas",
  "--lc-bg-hover": "row / list item hover",
  "--lc-bg-selected": "selected row",
  "--lc-bg-badge": "badge / pill background",
  "--lc-bg-popup": "menus and popovers",
  "--lc-bg-menu-hover": "menu item hover",
  "--lc-fg": "primary text",
  "--lc-fg-secondary": "secondary text",
  "--lc-fg-muted": "muted text, metadata (≥ 4.5:1)",
  "--lc-fg-subtext": "sub-labels",
  "--lc-stroke": "default borders",
  "--lc-stroke-subtle": "separators inside cards",
  "--lc-stroke-hover": "control border on hover",
  "--lc-stroke-grid": "table / grid lines",
  "--lc-accent": "brand: active tabs, toggles, selection",
  "--lc-accent-hover": "accent hover",
  "--lc-on-accent": "text on an accent fill",
  "--lc-focus-ring": "keyboard focus outline",
  "--lc-link": "links",
  "--lc-link-hover": "link hover",
  "--lc-status-passed": "passed",
  "--lc-status-failed": "failed",
  "--lc-status-skipped": "skipped / warnings",
  "--lc-status-running": "in progress",
  "--lc-status-muted": "not run / cancelled",
  "--lc-status-failed-text": "failure text on tints",
  "--lc-status-passed-text": "passed color as text (≥ 4.5:1)",
  "--lc-status-skipped-text": "skipped color as text (≥ 4.5:1)",
  "--lc-on-status": "text on a solid status fill",
  "--lc-msg-fg": "message bar text",
  "--lc-console-bg": "output panel background",
  "--lc-console-fg": "output text",
  "--lc-code-default": "code text",
  "--lc-coverage-covered": "covered line",
  "--lc-coverage-uncovered": "uncovered line",
  "--lc-coverage-partial": "partially covered line",
};

const GROUPS: [string, RegExp][] = [
  ["Surfaces", /^--lc-(bg-|shadow-)/],
  ["Text", /^--lc-fg/],
  ["Strokes", /^--lc-stroke/],
  ["Accent, links and focus", /^--lc-(accent|on-accent|link|focus)/],
  ["Status", /^--lc-(status-|on-status|host-)/],
  ["Message bars", /^--lc-msg-/],
  ["Console and ANSI", /^--lc-(console-|ansi-)/],
  ["Code", /^--lc-code-/],
  ["Coverage", /^--lc-coverage-/],
  ["Scrollbars", /^--lc-scrollbar-thumb/],
  ["Project tags", /^--lc-tag-/],
  ["Confetti", /^--lc-confetti-/],
];

/** Every --lc-* color token assigned by the theme files (read from the loaded stylesheets). */
function colorTokens(): string[] {
  const names = new Set<string>();
  for (const sheet of [...document.styleSheets]) {
    let rules: CSSRuleList;
    try {
      rules = sheet.cssRules;
    } catch {
      continue;
    }
    for (const r of [...rules]) {
      if (!(r instanceof CSSStyleRule) || !r.selectorText.startsWith(":root") || r.selectorText.includes("data-theme=")) continue;
      for (let i = 0; i < r.style.length; i++) {
        const p = r.style[i];
        if (p.startsWith("--lc-") && !/^--lc-code-(tint-shift|.*-style|.*-weight)$/.test(p) && GROUPS.some(([, re]) => re.test(p))) names.add(p); // shift and font styles are not colors
      }
    }
  }
  return [...names];
}

/** Resolve every token in each theme (switches <html> attributes synchronously, no paint in between). */
function resolveAll(tokens: string[]): Record<ThemeKey, Record<string, string>> {
  const root = document.documentElement;
  const saved = { f: root.dataset.themeFamily, m: root.dataset.theme };
  const out = {} as Record<ThemeKey, Record<string, string>>;
  for (const t of THEMES) {
    root.dataset.themeFamily = t.family;
    root.dataset.theme = t.mode;
    const cs = getComputedStyle(root);
    const vals: Record<string, string> = {};
    for (const n of tokens) vals[n] = cs.getPropertyValue(n).trim();
    out[`${t.family}-${t.mode}`] = vals;
  }
  root.dataset.themeFamily = saved.f;
  root.dataset.theme = saved.m;
  return out;
}

let probe: { el: HTMLElement; ctx: CanvasRenderingContext2D } | undefined;
/** Any CSS color (incl. color-mix(), named colors) → #rrggbb or #rrggbbaa; other values unchanged. */
function toHex(v: string): string {
  if (!v || /shadow|px/.test(v) || v.includes(" 0 ")) return v.replace(/\s+/g, " ");
  if (!probe) {
    const el = document.createElement("div");
    el.style.display = "none";
    document.body.append(el);
    const c = document.createElement("canvas");
    c.width = c.height = 1;
    probe = { el, ctx: c.getContext("2d", { willReadFrequently: true })! };
  }
  probe.el.style.color = "";
  probe.el.style.color = v;
  if (!probe.el.style.color) return v;
  const resolved = getComputedStyle(probe.el).color;
  const { ctx } = probe;
  ctx.clearRect(0, 0, 1, 1);
  ctx.fillStyle = "#000";
  ctx.fillStyle = resolved;
  ctx.fillRect(0, 0, 1, 1);
  const [r, g, b, a] = ctx.getImageData(0, 0, 1, 1).data;
  const hx = (n: number) => n.toString(16).padStart(2, "0");
  return `#${hx(r)}${hx(g)}${hx(b)}${a < 255 ? hx(a) : ""}`;
}

export function brandSection(): HTMLElement {
  const sizes = [16, 20, 24, 32, 64];
  const shell = resolveAll(["--lc-bg-shell", "--lc-fg", "--lc-bg-surface"]);
  return section(
    { id: "brand", title: "Brand", icon: "labcoat", group: "Overview" },
    [
      "The Labcoat mark is a plain lab coat. The color logo (",
      h("code", null, "brandLogo({ size })"),
      " / ",
      h("code", null, "<lc-logo>"),
      ") uses the fixed Aspire purple ramp on a transparent background, like Aspire's own header logo; the one-color mark (",
      h("code", null, 'icon("labcoat")'),
      ") inherits ",
      h("code", null, "currentColor"),
      ". Both switch to an optical small variant (heavier strokes, fewer details) at 20 px and below.",
    ],
    h(
      "div",
      { class: "g-brand-grid" },
      h(
        "div",
        { class: "g-brand-tile" },
        h("div", { class: "g-logo-sizes" }, sizes.map((s) => h("figure", null, brandLogo({ size: s }), h("figcaption", null, `${s}${s <= 20 ? " · sm" : ""}`)))),
        h("span", null, "brandLogo({ size }) · color, assets/labcoat-color(-16).svg"),
      ),
      h(
        "div",
        { class: "g-brand-tile" },
        h("div", { class: "g-logo-sizes" }, sizes.map((s) => h("figure", null, icon("labcoat", { size: s }), h("figcaption", null, `${s}${s <= 20 ? " · sm" : ""}`)))),
        h("span", null, 'icon("labcoat", { size }) · one color, assets/labcoat(-16).svg'),
      ),
      h(
        "div",
        { class: "g-brand-tile" },
        h("div", { class: "g-row", style: "gap:24px" }, h("img", { src: faviconUrl, width: 32, height: 32, alt: "Favicon: color lab coat" }), h("img", { src: nugetUrl, width: 96, height: 96, alt: "NuGet package icon" })),
        h("span", null, "Favicon · ui/public/favicon.svg — NuGet / README · assets/logo.svg"),
      ),
    ),
    h(
      "div",
      { class: "g-headers", role: "list", "aria-label": "Logo on the header of each theme" },
      THEMES.map((t) => {
        const v = shell[`${t.family}-${t.mode}`];
        return h(
          "div",
          { class: "g-header-sample", role: "listitem", style: { background: v["--lc-bg-shell"], color: v["--lc-fg"] } },
          brandLogo({ size: 24 }),
          h("span", { class: "g-brand-name" }, "Labcoat"),
          h("span", { class: "g-header-sample-theme" }, t.label),
          h("span", { class: "g-header-sample-small" }, brandLogo({ size: 16 }), icon("labcoat", { size: 16 })),
        );
      }),
    ),
  );
}

export function colorSection(current: () => ThemeKey): HTMLElement {
  const tokens = colorTokens();
  const values = resolveAll(tokens);
  const filter = signal("");
  const groups = computed(() => {
    const q = filter().trim().toLowerCase();
    return GROUPS.map(([name, re]) => ({ name, tokens: tokens.filter((t) => re.test(t) && (!q || t.includes(q) || (DESCRIPTIONS[t] ?? "").toLowerCase().includes(q))) })).filter((g) => g.tokens.length);
  });
  const cell = (theme: ThemeKey, t: string) => {
    const v = values[theme][t] ?? "";
    const isShadow = t.includes("shadow");
    return h(
      "td",
      { class: () => (current() === theme ? "current" : "") },
      h(
        "div",
        { class: "g-swatch-cell" },
        h("span", { class: "g-swatch", "aria-hidden": "true" }, h("span", { style: isShadow ? { boxShadow: v, background: "transparent" } : { background: v } })),
        h("span", { class: "g-value", title: v }, toHex(v)),
      ),
    );
  };
  return section(
    { id: "colors", title: "Color tokens", icon: "dataPie", group: "Foundations" },
    [
      "Components use only semantic ",
      h("code", null, "--lc-*"),
      " tokens; theme files assign them and nothing else. Values below are resolved live from every theme family in light and dark (the column of the active theme is tinted). ",
      `${tokens.length} color tokens.`,
    ],
    h(
      "div",
      { class: "g-filter" },
      h("input", {
        class: "g-input",
        type: "search",
        placeholder: "Filter tokens (e.g. status, console)",
        "aria-label": "Filter tokens",
        oninput: (e) => filter.set((e.currentTarget as HTMLInputElement).value),
      }),
      h("span", { class: "g-log" }, () => `${groups().reduce((n, g) => n + g.tokens.length, 0)} shown`),
    ),
    h(
      "div",
      { class: "g-table-wrap", tabIndex: 0, role: "region", "aria-label": "Color tokens per theme" },
      h(
        "table",
        { class: "g-table" },
        h(
          "thead",
          null,
          h(
            "tr",
            null,
            h("th", { scope: "col" }, "Token"),
            THEMES.map((t) => h("th", { scope: "col", class: () => (current() === `${t.family}-${t.mode}` ? "current" : "") }, t.label)),
          ),
        ),
        list(
          groups,
          (g) => g.name + "|" + g.tokens.join(","),
          (g) =>
            h(
              "tbody",
              null,
              h("tr", { class: "g-group-row" }, h("th", { colSpan: THEMES.length + 1, scope: "colgroup" }, g.peek().name)),
              g.peek().tokens.map((t) =>
                h(
                  "tr",
                  null,
                  h("th", { scope: "row" }, h("div", { class: "g-token" }, t), DESCRIPTIONS[t] ? h("div", { class: "g-desc" }, DESCRIPTIONS[t]) : null),
                  THEMES.map((th) => cell(`${th.family}-${th.mode}`, t)),
                ),
              ),
            ),
        ),
      ),
    ),
  );
}

export function typographySection(): HTMLElement {
  const fams: [string, string, string][] = [
    ["--lc-font-sans", "Geist · UI text", "Run all tests in the solution"],
    ["--lc-font-mono", "Cascadia Mono / Geist Mono · code, numbers", "Assert.AreEqual(42, result); // 12.4 ms"],
    ["--lc-font-display", "Poppins · app name, titles", "Labcoat Test Explorer"],
  ];
  const scale: [string, string, Child][] = [
    ["30 / 38 · 600 display", "font-size: 30px", h("span", { style: "font: 600 30px/38px var(--lc-font-display)" }, "Page hero")],
    ["22 / 30 · 600 display", "section titles", h("span", { style: "font: 600 22px/30px var(--lc-font-display)" }, "Section title")],
    ["16 / 24 · 500 display", "dialog, detail titles", h("span", { style: "font: 500 16px/24px var(--lc-font-display)" }, "Dialog title")],
    ["16 / 20 · 500 display", "--lc-text-lg: empty state title", h("span", { style: "font: 500 var(--lc-text-lg)/20px var(--lc-font-display)" }, "No coverage report yet")],
    ["14 / 20 · 400", "--lc-font-size: body", h("span", { style: "font-size: 14px" }, "Body text in panes and settings")],
    ["13 / 20 · 400", "controls, tabs, menus", h("span", { style: "font-size: 13px" }, "Controls, tabs, menu items")],
    ["12 / 16 · 400", "chips, small buttons, meta", h("span", { style: "font-size: 12px" }, "Chips, small buttons, metadata")],
    ["11 / 16 · 600 caps", "group labels, kbd", h("span", { style: "font-size: 11px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase" }, "Group label")],
    ["13 / 20 · mono", "--lc-text-md: code blocks, stack traces", h("span", { class: "mono", style: "font-size: var(--lc-text-md)" }, "at Calc.Divide(Int32 a, Int32 b) in Calc.cs:line 18")],
  ];
  return section(
    { id: "typography", title: "Typography", icon: "textWrap", group: "Foundations" },
    "Three families as tokens; sizes come from the --lc-text-* scale (11 / 12 / 13 / 14 / 16 / 18 / 20 / 22 / 28 px; Tokens below) plus the display sizes of page and section titles.",
    h(
      "div",
      { class: "g-card" },
      fams.map(([tok, what, sample]) =>
        h("div", { class: "g-type-row" }, h("div", null, h("div", { class: "g-token" }, tok), h("div", { class: "g-type-meta" }, what)), h("div", { style: `font-family: var(${tok}); font-size: 18px` }, sample)),
      ),
    ),
    h(
      "div",
      { class: "g-card" },
      scale.map(([size, use, sample]) => h("div", { class: "g-type-row" }, h("div", null, h("div", { class: "g-token" }, size), h("div", { class: "g-type-meta" }, use)), h("div", null, sample))),
    ),
  );
}

export function spacingSection(): HTMLElement {
  const space = [2, 4, 6, 8, 10, 12, 16, 20, 24, 32, 40];
  const radii: [string, string][] = [
    ["4px", "kbd, menu items, tooltips"],
    ["var(--lc-control-radius)", "--lc-control-radius · buttons, inputs"],
    ["8px", "cards, message bars, code"],
    ["var(--lc-popup-radius)", "--lc-popup-radius · menus, dialogs"],
    ["999px", "chips, pills, switches"],
  ];
  const sizes: [string, string][] = [
    ["--lc-control-height", "buttons, inputs, icon buttons"],
    ["--lc-row-h", "tree rows"],
    ["--lc-rail-w", "nav rail"],
    ["--lc-scrollbar-size", "scrollbars"],
    ["--lc-disabled-opacity", "disabled controls"],
  ];
  const cs = getComputedStyle(document.documentElement);
  return section(
    { id: "spacing", title: "Spacing, sizes and radii", icon: "expandAll", group: "Foundations" },
    "A 2 / 4 px based spacing scale for gaps and padding; control sizes and radii are tokens.",
    demo({
      title: "Spacing scale",
      preview: h(
        "div",
        { class: "g-scale" },
        space.map((s) => h("div", { class: "g-scale-item" }, h("span", { class: "g-space-bar", style: { width: s, height: 40 } }), `${s}`)),
      ),
    }),
    demo({
      title: "Radii",
      preview: h(
        "div",
        { class: "g-scale" },
        radii.map(([r, label]) => h("div", { class: "g-scale-item", style: "max-width:140px;text-align:center" }, h("span", { class: "g-radius-box", style: { borderRadius: r } }), label)),
      ),
    }),
    demo({
      title: "Size tokens",
      preview: h(
        "div",
        { class: "g-stack" },
        sizes.map(([t, use]) => h("div", { class: "g-row" }, h("span", { class: "g-token", style: "min-width:220px" }, t), h("span", { class: "g-value" }, cs.getPropertyValue(t).trim()), h("span", { class: "g-desc" }, use))),
      ),
    }),
  );
}

const MOTION_TOKENS: [string, string][] = [
  ["--lc-motion-instant", "press, exit of tiny things, hover reveal"],
  ["--lc-motion-fast", "hover/press colors, crossfades, reduced-motion cap"],
  ["--lc-motion-pop", "popover / menu in, list row in, step reveal"],
  ["--lc-motion-enter", "content swap (.lc-enter), caret, switch thumb, palette"],
  ["--lc-motion-dialog", "centered dialog in"],
  ["--lc-motion-exit", "dialog / palette out"],
  ["--lc-motion-base", "tab indicator, height reveal, message bar"],
  ["--lc-motion-slow", "meter fill, count-up"],
  ["--lc-motion-xslow", "progress layers, locator ring, sheen"],
];
const EASINGS: [string, string][] = [
  ["--lc-ease-standard", "color / opacity state changes"],
  ["--lc-ease-decelerate", "things entering"],
  ["--lc-ease-accelerate", "things leaving"],
  ["--lc-ease-emphasized", "things moving (indicators, fills)"],
];

export function motionSection(): HTMLElement {
  const go = signal(false);
  const cs = getComputedStyle(document.documentElement);
  const level = signal<"system" | "full" | "reduced" | "off">("system");
  const setLevel = (v: "system" | "full" | "reduced" | "off") => {
    level.set(v);
    setMotionLevel(v);
  };

  // P1 detail swap
  const tests = ["PlaceOrder_SendsConfirmationEmail", "PlaceOrder_PersistsOrder", "Render_FormatsReceipt", "CalculateTotal_AppliesDiscount"];
  const sel = signal(0);
  const scrub = scrubGuard(150);
  const swap = h(
    "div",
    { class: "g-motion-swap" },
    h(
      "div",
      { class: "g-motion-list", role: "listbox", "aria-label": "Tests", tabIndex: 0, onkeydown: (e: KeyboardEvent) => {
        if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
        e.preventDefault();
        sel.update((i) => (i + (e.key === "ArrowDown" ? 1 : tests.length - 1)) % tests.length);
      } },
      tests.map((t, i) => h("div", { class: () => ["g-motion-item", sel() === i && "sel"], role: "option", "aria-selected": () => sel() === i, onclick: () => sel.set(i) }, statusIcon(i % 2 ? "passed" : "failed"), t)),
    ),
    keyed(sel, (i) => {
      const el = h("div", { class: "g-motion-detail" }, h("strong", null, tests[i]), h("div", { class: "dim" }, i % 2 ? "Passed in 12 ms" : "Assert.AreEqual failed. Expected:<sent>. Actual:<queued>."));
      return scrub() ? animateEnter(el) : el;
    }),
  );

  // P3 message bar reserve
  const bar = signal(false);
  const barDemo = h(
    "div",
    { class: "g-stack" },
    h("div", null, button({ size: "small", onclick: () => bar.update((v) => !v) }, "Toggle build error")),
    reveal(bar, () => messageBar({ intent: "error", title: "Build failed: Inventory.Tests (2 errors).", body: "Fix the errors and save to rebuild.", role: null, onDismiss: () => bar.set(false) })),
    h("div", { class: "g-motion-below" }, "Toolbar, summary and tree sit here: they are pushed down instead of jumping."),
  );

  // P4 popover from the anchor
  const pop = signal(false);
  let pill!: HTMLButtonElement;
  const popDemo = h(
    "div",
    { class: "g-row", style: "justify-content:flex-end" },
    h(
      "button",
      { class: "btn", ref: (b) => (pill = b), "aria-expanded": pop, onclick: () => pop.update((v) => !v) },
      "Run history ",
      icon("chevronDown", { size: 12 }),
    ),
    popover({ open: pop, anchor: () => pill, placement: "bottom-end", onClose: () => pop.set(false), label: "Runs", content: () => h("div", { class: "g-stack", style: "padding:6px 8px" }, "Live · current results", "01:04:37 · 17 failed", "00:58:12 · 24 passed") }),
  );

  // P5 tabs
  const tab = signal("details");
  const tabDemo = tabs({
    label: "Motion demo tabs",
    selected: tab,
    onSelect: tab.set,
    tabs: [
      { id: "details", label: "Test details" },
      { id: "coverage", label: "Coverage", pill: tabPill("82%", "good") },
      { id: "output", label: "Output" },
    ],
  });

  // P10 meters
  const meters = signal(0);
  const files: [string, number][] = [["CacheService.cs", 0.615], ["Program.cs", 0.833], ["WeatherEndpoints.cs", 0.947], ["OrderService.cs", 0.42]];
  const meterDemo = h(
    "div",
    { class: "g-stack" },
    keyed(meters, () =>
      h(
        "div",
        { class: "g-motion-meters" },
        files.map(([f, v], i) => [h("span", null, f), meter({ value: v, label: f, size: "md", appear: i }), h("span", { class: "mono" }, `${(v * 100).toFixed(1)}%`)]),
      ),
    ),
    h("div", null, button({ size: "small", icon: "arrowClockwise", onclick: () => meters.update((n) => n + 1) }, "Remount")),
  );

  // P6 / P11 feedback
  let row!: HTMLElement;
  let num!: HTMLElement;
  let barEl!: HTMLElement;
  let count = 24;
  const feedback = h(
    "div",
    { class: "g-stack" },
    h("div", { class: "g-motion-row", ref: (e) => (row = e) }, statusIcon("passed"), "OrderServiceTests.PlaceOrder_PersistsOrder"),
    h("div", { class: "g-row" }, "Passed ", h("b", { class: "lc-tabular", ref: (e) => (num = e) }, "24")),
    h("div", { class: "g-motion-bar", ref: (e) => (barEl = e) }),
    h(
      "div",
      { class: "g-row" },
      button({ size: "small", onclick: () => flash(row, "bad") }, "Flash: regressed"),
      button({ size: "small", onclick: () => flash(row, "good") }, "Flash: fixed"),
      button({ size: "small", onclick: () => locate(row) }, "Locator"),
      button({ size: "small", onclick: () => (countUp(num, count, count + 7), (count += 7)) }, "Count up"),
      button({ size: "small", onclick: () => replayClass(barEl, "lc-sheen", 600) }, "Sheen"),
    ),
  );

  // P8 copy, P14 collapsible, P16 skeleton, P19 toast
  const loading = signal(true);
  const misc = h(
    "div",
    { class: "g-stack" },
    h("div", { class: "g-row" }, h("code", null, "SampleTests.Orders.OrderServiceTests.PlaceOrder"), copyButton({ text: "SampleTests.Orders.OrderServiceTests.PlaceOrder", label: "Copy full name" })),
    disclosure({ summary: "Raw message", content: () => h("pre", { class: "g-motion-pre" }, "Assert.AreEqual failed.\nExpected:<sent>.\nActual:<queued>.") }),
    h(
      "div",
      { class: "g-row" },
      button({ size: "small", onclick: () => (loading.set(false), setTimeout(() => loading.set(true), 0)) }, "Reload (skeleton after 300 ms)"),
      button({ size: "small", onclick: () => toast("Opened in Rider", { intent: "success" }) }, "Toast"),
    ),
    () => (loading() ? skeleton({ lines: 3, heading: true, label: "Loading demo" }) : null),
  );

  return section(
    { id: "motion", title: "Motion", icon: "timer", group: "Foundations" },
    [
      "Fast, subtle, purposeful: motion orients, confirms or connects; it never bounces. Tokens only; ",
      h("code", null, "prefers-reduced-motion"),
      " and ",
      h("code", null, 'html[data-motion="reduced"|"off"]'),
      " turn movement into short fades (or nothing); ",
      h("code", null, "html.lc-busy"),
      " turns decorative motion off during big runs. See ui/MOTION.md.",
    ],
    demo({
      title: "Motion switch",
      sub: "Applies to every demo on this page (sets html[data-motion]).",
      preview: segmented({
        label: "Motion",
        value: level,
        onChange: setLevel,
        options: [
          { value: "system", label: "System" },
          { value: "full", label: "Full" },
          { value: "reduced", label: "Reduced" },
          { value: "off", label: "Off" },
        ],
      }),
    }),
    demo({
      title: "Durations",
      preview: h(
        "div",
        { class: "g-stack" },
        MOTION_TOKENS.map(([name, use]) =>
          h(
            "div",
            { class: "g-row" },
            h("span", { class: "g-token", style: "min-width:180px" }, name),
            h("span", { class: "g-value", style: "min-width:56px" }, cs.getPropertyValue(name).trim()),
            h("span", { class: () => ["g-motion-track", go() && "go"] }, h("span", { class: "g-motion-dot", style: { transition: `transform var(${name}) var(--lc-ease-emphasized)` } })),
            h("span", { class: "g-desc" }, use),
          ),
        ),
        h("div", null, button({ size: "small", icon: "play", onclick: () => go.update((v) => !v) }, "Play")),
      ),
    }),
    demo({
      title: "Easings",
      preview: h(
        "div",
        { class: "g-stack" },
        EASINGS.map(([name, use]) =>
          h(
            "div",
            { class: "g-row" },
            h("span", { class: "g-token", style: "min-width:180px" }, name),
            h("span", { class: () => ["g-motion-track", go() && "go"] }, h("span", { class: "g-motion-dot", style: { transition: `transform var(--lc-motion-xslow) var(${name})` } })),
            h("span", { class: "g-desc" }, use),
          ),
        ),
      ),
    }),
    demo({ title: "P1 · Content swap", sub: ".lc-enter: 160 ms, opacity + 4 px rise. Hold ↑/↓: re-selections within 150 ms are not animated.", preview: swap, code: "keyed(sel, (id) => { const el = detail(id); return scrub() ? animateEnter(el) : el; })   // scrub = scrubGuard(150)" }),
    demo({ title: "P3 · Message bar with height reserve", sub: "reveal(): grid 0fr→1fr 200 ms; exit 160 ms.", preview: barDemo, code: "reveal(() => buildFailed(), () => messageBar({ intent: \"error\", title: \"Build failed\" }))" }),
    demo({ title: "P4 · Popover from its anchor", sub: "scale .97→1 from the anchor side, 140 ms; exit 80 ms fade.", preview: popDemo }),
    demo({ title: "P5 · Tab indicator", sub: "FLIP on the ::after underline, 200 ms emphasized.", preview: tabDemo }),
    demo({ title: "P10 · Meter fill on first display", sub: "scaleX 0→v, 320 ms emphasized, 20 ms stagger (≤ 12).", preview: meterDemo, code: "meter({ value: rate, label, appear: index })   // or fillIn(container) after mounting a report" }),
    demo({ title: "P6 / P7 / P11 · Feedback", sub: "flash() 900 ms tint · locate() 600 ms ring · countUp() 320 ms · sheen 600 ms (decorative: off when busy).", preview: feedback }),
    demo({ title: "P8 / P14 / P16 / P19", sub: "copyButton() morph + polite \"Copied\" · disclosure()/collapsible() · skeleton() after 300 ms · toast()", preview: misc }),
  );
}
