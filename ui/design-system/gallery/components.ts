// Component demos: every primitive with variants, states, keyboard notes and a usage snippet.
import {
  badge,
  button,
  chip,
  codeBlock,
  dialog,
  emptyState,
  h,
  icon,
  iconButton,
  iconNames,
  kbd,
  menuButton,
  messageBar,
  messageBarAction,
  meter,
  popover,
  progressBar,
  propertyGrid,
  rateTone,
  segmented,
  signal,
  splitPane,
  STATUS_LABEL,
  statusBadge,
  statusIcon,
  switchControl,
  tabIds,
  tabPanel,
  tabPill,
  tabs,
  tooltip,
  formatPercent,
  type MenuEntry,
  type Status,
} from "../src";
import { demo, force, highlight, row, section, state } from "./util";

const STATUSES = Object.keys(STATUS_LABEL) as Status[];

// ---- buttons, chips, badges, status, icons ------------------------------------------------------------

function buttonsSection() {
  const watch = signal(true);
  const busy = signal(false);
  return section(
    { id: "button", title: "Button", icon: "playFilled", group: "Components" },
    ["Text buttons in five variants and two sizes, plus 32 px icon buttons. Factories return a plain ", h("code", null, "<button>"), "; any h() prop passes through."],
    demo({
      title: "Variants",
      preview: [
        button({ variant: "accent", icon: "playFilled" }, "Run all"),
        button({ icon: "arrowSync" }, "Rerun failed"),
        button({ variant: "danger", icon: "stopFilled" }, "Stop"),
        button({ variant: "toggle", pressed: watch, icon: () => (watch() ? "eyeFilled" : "eye"), onclick: () => watch.update((v) => !v) }, "Watch"),
        button({ variant: "link" }, "Open in editor"),
        button({ size: "small", icon: "copy" }, "Copy"),
        iconButton({ icon: "expandAll", label: "Expand all" }),
        iconButton({ icon: "collapseAll", label: "Collapse all", toggled: true }),
      ],
      code: `
button({ variant: "accent", icon: "playFilled", onclick: runAll }, "Run all")
button({ variant: "toggle", pressed: () => watch(), icon: () => (watch() ? "eyeFilled" : "eye") }, "Watch")
iconButton({ icon: "expandAll", label: "Expand all", onclick })`,
      keys: [
        ["Tab", "focus"],
        ["Enter", "activate"],
        ["Space", "activate"],
      ],
    }),
    demo({
      title: "States",
      preview: h(
        "div",
        { class: "g-states" },
        state("Default", button({ variant: "accent" }, "Run all"), button({}, "Rerun")),
        state("Hover", force(button({ variant: "accent" }, "Run all"), "hover"), force(button({}, "Rerun"), "hover")),
        state("Focus", force(button({ variant: "accent" }, "Run all"), "focus"), force(button({}, "Rerun"), "focus")),
        state("Disabled", button({ variant: "accent", disabled: true }, "Run all"), button({ disabled: true }, "Rerun")),
        state(
          "Loading",
          button({ variant: "accent", icon: "arrowSync", iconClass: () => busy() && "spin", disabled: busy, onclick: () => (busy.set(true), setTimeout(() => busy.set(false), 2000)) }, () => (busy() ? "Running…" : "Run (2 s)")),
          button({ disabled: true, icon: "arrowSync", iconClass: "spin" }, "Building…"),
        ),
      ),
    }),
  );
}

function chipsSection() {
  const active = signal("all");
  const counts: Record<string, number> = { all: 128, failed: 3, passed: 119, skipped: 4, notRun: 2 };
  const labels: Record<string, string> = { all: "All", failed: "Failed", passed: "Passed", skipped: "Skipped", notRun: "Not run" };
  return section(
    { id: "chip", title: "Chips, badges and status", icon: "checkCircle", group: "Components" },
    "Filter chips (radio-like, with a status dot and a count), badges, and the status icons used across the tree and detail views.",
    demo({
      title: "Filter chips",
      preview: h(
        "div",
        { class: "chips", role: "radiogroup", "aria-label": "Status filter" },
        Object.keys(counts).map((id) => chip({ id, label: labels[id], count: counts[id], active: () => active() === id, onclick: () => active.set(id) })),
      ),
      code: `chip({ id: "failed", label: "Failed", count: () => counts().failed, active: () => status() === "failed", onclick })`,
    }),
    demo({
      title: "Badges",
      preview: [STATUSES.slice(0, 8).map((s) => statusBadge(s)), badge({}, "Category: Smoke"), badge({ class: "trait" }, "Owner: erik")],
      code: `statusBadge("failed")\nbadge({ class: "trait" }, "Owner: erik")`,
    }),
    demo({
      title: "Status icons",
      sub: "statusIcon(status | () => status, size): pops only when the status changes",
      preview: STATUSES.map((s) => h("span", { class: "g-row", style: "gap:6px;min-width:120px" }, statusIcon(s, 16), h("span", { style: "font-size:var(--lc-text-md)" }, STATUS_LABEL[s]))),
    }),
  );
}

function iconsSection() {
  const cells = iconNames.map((n) => h("div", { class: "g-icon-cell" }, icon(n, { size: 20 }), n));
  return section(
    { id: "icon", title: "Icons", icon: "beaker", group: "Components" },
    ["Fluent UI System Icons (20 px regular/filled), parsed once per name and cloned. ", h("code", null, 'icon(name, { size })'), " → ", h("code", null, "span.icon > svg"), "."],
    demo({ title: `${iconNames.length} icons`, preview: h("div", { class: "g-icons" }, cells), code: `icon("playFilled", { size: 16 })\nh("lc-icon", { name: "beaker", size: 20 })` }),
  );
}

// ---- form-ish ----------------------------------------------------------------------------------------

function switchSection() {
  const confetti = signal(true);
  const sounds = signal(false);
  return section(
    { id: "switch", title: "Switch", icon: "checkmark", group: "Components" },
    ["An on/off setting that applies immediately (APG Switch). A native checkbox with ", h("code", null, 'role="switch"'), "; same markup as the Settings page."],
    demo({
      title: "Switch",
      preview: h(
        "div",
        { class: "g-states" },
        state("On", switchControl({ checked: confetti, onChange: confetti.set, label: "Confetti" })),
        state("Off", switchControl({ checked: sounds, onChange: sounds.set, label: "Sounds" })),
        state("Hover", force(switchControl({ checked: false, onChange: () => {}, label: "Hover demo" }), "hover")),
        state("Focus", (() => {
          const s = switchControl({ checked: true, onChange: () => {}, label: "Focus demo" });
          force(s.querySelector("input")!, "focus");
          return s;
        })()),
        state("Disabled", switchControl({ checked: true, disabled: true, onChange: () => {}, label: "Disabled demo" })),
        state("With text", switchControl({ checked: confetti, onChange: confetti.set, text: "Confetti", stateText: false })),
      ),
      code: `
switchControl({
  checked: () => settings().confetti,
  onChange: (v) => setSetting("confetti", v),
  label: "Confetti when tests turn green",
})`,
      keys: [
        ["Tab", "focus the switch"],
        ["Space", "toggle"],
      ],
    }),
  );
}

function segmentedSection() {
  const family = signal<"aspire" | "catppuccin">("aspire");
  const mode = signal<"system" | "light" | "dark">("system");
  const view = signal<"list" | "tree">("tree");
  return section(
    { id: "segmented", title: "Segmented control", icon: "darkTheme", group: "Components" },
    "A compact single-choice group (APG Radio Group with roving tabindex).",
    demo({
      title: "Segmented control",
      preview: h(
        "div",
        { class: "g-stack" },
        row("Text", segmented({ label: "Theme", value: family, onChange: family.set, options: [{ value: "aspire", label: "Aspire" }, { value: "catppuccin", label: "Catppuccin" }] })),
        row(
          "Icon + text",
          segmented({
            label: "Mode",
            value: mode,
            onChange: mode.set,
            options: [
              { value: "system", label: "System", icon: "darkTheme" },
              { value: "light", label: "Light", icon: "sun" },
              { value: "dark", label: "Dark", icon: "moon" },
            ],
          }),
        ),
        row(
          "Icon only, small",
          segmented({
            label: "Layout",
            size: "small",
            value: view,
            onChange: view.set,
            options: [
              { value: "tree", label: "", icon: "taskList", title: "Tree" },
              { value: "list", label: "", icon: "textWrap", title: "List" },
            ],
          }),
        ),
        row("Disabled", segmented({ label: "Disabled", value: "a", disabled: true, onChange: () => {}, options: [{ value: "a", label: "One" }, { value: "b", label: "Two" }] })),
      ),
      code: `
segmented({ label: "Mode", value: themePref, onChange: setThemePref, options: [
  { value: "system", label: "System", icon: "darkTheme" },
  { value: "light", label: "Light", icon: "sun" },
  { value: "dark", label: "Dark", icon: "moon" },
] })`,
      keys: [
        ["Tab", "focus the checked option"],
        ["ArrowRight", "next option (selects)"],
        ["ArrowLeft", "previous option"],
        ["Home", "first"],
        ["End", "last"],
      ],
    }),
  );
}

function tabsSection() {
  const view = signal<"details" | "coverage" | "history">("details");
  const rate = signal(0.824);
  const ids = tabIds("gallery-tabs");
  return section(
    { id: "tabs", title: "Tabs", icon: "taskList", group: "Components" },
    "Pane tabs with an accent underline (APG Tabs, automatic activation). Same markup as the detail pane tabs.",
    demo({
      title: "Pane tabs",
      preview: h(
        "div",
        { class: "g-tabs-demo" },
        tabs({
          ids,
          label: "Detail views",
          selected: view,
          onSelect: view.set,
          tabs: [
            { id: "details", label: "Test details", icon: "taskList" },
            { id: "coverage", label: "Coverage", icon: "shieldCheck", pill: tabPill(() => formatPercent(rate()), () => rateTone(rate())) },
            { id: "history", label: "History", icon: "history" },
          ],
        }),
        () => {
          const v = view();
          return tabPanel(ids, v, v === "details" ? "Status, duration, output and stack trace of the selected test." : v === "coverage" ? "Lines covered by this test." : "The last 20 runs of this test.");
        },
      ),
      code: `
const ids = tabIds("detail");
tabs({ ids, label: "Detail views", selected: view, onSelect: setView, tabs: [
  { id: "details", label: "Test details", icon: "taskList" },
  { id: "coverage", label: "Coverage", icon: "shieldCheck",
    pill: tabPill(() => formatPercent(rate()), () => rateTone(rate())) },
] })
tabPanel(ids, "details", detailContent)`,
      keys: [
        ["Tab", "focus the selected tab, then the panel"],
        ["ArrowRight", "next tab (selects)"],
        ["ArrowLeft", "previous tab"],
        ["Home", "first tab"],
        ["End", "last tab"],
      ],
    }),
    demo({
      title: "Tab pills and tones",
      sub: "rateTone(rate): ≥ 80 % good, ≥ 50 % warn, else bad",
      preview: [tabPill("92%", "good"), tabPill("64%", "warn"), tabPill("31%", "bad"), tabPill("12")],
    }),
  );
}

// ---- overlays ----------------------------------------------------------------------------------------

function menuSection() {
  const sort = signal<"name" | "duration" | "outcome">("name");
  const showSkipped = signal(true);
  const last = signal("–");
  const items = (): MenuEntry[] => [
    { label: "Run", icon: "play", shortcut: "Enter", onSelect: () => last.set("Run") },
    { label: "Debug", icon: "beaker", shortcut: "F5", onSelect: () => last.set("Debug") },
    { label: "Open in editor", icon: "open", shortcut: "O", onSelect: () => last.set("Open in editor") },
    { label: "Copy name", icon: "copy", shortcut: "Mod+C", onSelect: () => last.set("Copy name") },
    { separator: true },
    { heading: "Sort by" },
    { label: "Name", checked: sort() === "name", onSelect: () => sort.set("name") },
    { label: "Duration", checked: sort() === "duration", onSelect: () => sort.set("duration") },
    { label: "Outcome", checked: sort() === "outcome", onSelect: () => sort.set("outcome") },
    { separator: true },
    { label: "Show skipped", type: "checkbox", checked: showSkipped(), keepOpen: true, onSelect: () => showSkipped.update((v) => !v) },
    { label: "Collect coverage", description: "Not available while a run is in progress", disabled: true, onSelect: () => {} },
    { label: "Delete history", icon: "del", danger: true, onSelect: () => last.set("Delete history") },
  ];
  // custom popover content (like the run switcher)
  const popOpen = signal(false);
  let popBtn!: HTMLButtonElement;
  return section(
    { id: "menu", title: "Menu and popover", icon: "history", group: "Components" },
    [
      "Menus follow the APG Menu Button pattern. Popups are appended to the nearest landmark or dialog (else ",
      h("code", null, "document.body"),
      ") with fixed positioning: they flip and clamp to the viewport, follow scrolling, and are never clipped by an ",
      h("code", null, "overflow: hidden"),
      " header.",
    ],
    demo({
      title: "Menu button",
      preview: [
        menuButton({ label: "Actions", icon: "settings", items }),
        menuButton({ label: "More actions", icon: "settings", iconOnly: true, items, placement: "bottom-end" }),
        menuButton({ label: "Small", size: "small", items }),
        menuButton({ label: "Disabled", items, disabled: true }),
        h("span", { class: "g-log", role: "status" }, () => `last: ${last()} · sort: ${sort()} · skipped: ${showSkipped() ? "shown" : "hidden"}`),
      ],
      code: `
menuButton({ label: "Actions", icon: "settings", items: () => [
  { label: "Run", icon: "play", shortcut: "Enter", onSelect: run },
  { separator: true },
  { heading: "Sort by" },
  { label: "Name", checked: sort() === "name", onSelect: () => setSort("name") },
  { label: "Show skipped", type: "checkbox", checked: showSkipped(), keepOpen: true, onSelect: toggle },
  { label: "Delete history", icon: "del", danger: true, onSelect: clear },
] })`,
      keys: [
        ["Enter", "open at the first item"],
        ["ArrowDown", "open, or next item"],
        ["ArrowUp", "open at last, or previous item"],
        ["Home", "first item"],
        ["End", "last item"],
        ["a", "type-ahead: next item starting with a"],
        ["Escape", "close, focus back to the button"],
        ["Tab", "close and move on"],
      ],
    }),
    demo({
      title: "Escapes overflow: hidden",
      sub: "the dashed bar clips its content; the menu still renders fully",
      preview: h("div", { class: "g-clip-demo" }, "Header with overflow: hidden", h("span", { style: "flex:1" }), menuButton({ label: "Live", size: "small", icon: "history", items, placement: "bottom-end" })),
    }),
    demo({
      title: "Popover (custom content)",
      preview: [
        button({ ref: (b) => (popBtn = b), icon: "history", "aria-expanded": popOpen, "aria-haspopup": "dialog", onclick: () => popOpen.update((v) => !v) }, "Run history"),
        popover({
          open: popOpen,
          anchor: () => popBtn,
          onClose: () => popOpen.set(false),
          label: "Run history",
          class: "g-popover-demo",
          content: () =>
            h(
              "div",
              { style: "width:280px;padding:8px" },
              h("div", { style: "font-weight:600;margin-bottom:6px" }, "Run history"),
              h("p", { style: "margin:0 0 10px;color:var(--lc-fg-muted);font-size:var(--lc-text-md)" }, "A popover is a non-modal dialog: focus moves in, Escape or a click outside closes it."),
              h("div", { class: "g-row" }, button({ size: "small", onclick: () => popOpen.set(false) }, "Close"), button({ size: "small", variant: "accent" }, "View run")),
            ),
        }),
      ],
      code: `
popover({ open, anchor: () => btn, onClose: () => open.set(false), label: "Runs",
  class: "run-menu", placement: "bottom-end", content: () => runList() })`,
      keys: [
        ["Escape", "close, focus back to the anchor"],
        ["Tab", "moves through the content; leaving closes"],
      ],
    }),
  );
}

function tooltipSection() {
  const mk = (label: string, placement: "top" | "bottom" | "right-start" | "left-start") => tooltip(button({}, label), `Tooltip on the ${placement.split("-")[0]}`, { placement });
  return section(
    { id: "tooltip", title: "Tooltip", icon: "info", group: "Components" },
    ["Shown after 500 ms of hover or immediately on keyboard focus; wires ", h("code", null, "aria-describedby"), "; hoverable and dismissible with Escape (WCAG 1.4.13)."],
    demo({
      title: "Placements",
      preview: [
        mk("Top", "top"),
        mk("Bottom", "bottom"),
        mk("Right", "right-start"),
        mk("Left", "left-start"),
        tooltip(iconButton({ icon: "copy", label: "Copy" }), "Copy the error message and stack trace"),
      ],
      code: `tooltip(iconButton({ icon: "copy", label: "Copy" }), "Copy the error message", { placement: "top" })`,
      keys: [
        ["Tab", "focus shows the tooltip"],
        ["Escape", "hide it"],
      ],
      notes: "Don't also set title on the target. Icon buttons already have a title + aria-label; add a tooltip only for extra description.",
    }),
  );
}

function dialogSection() {
  const open = signal(false);
  const palette = signal(false);
  const query = signal("");
  const sel = signal(0);
  const commands: [string, string, string][] = [
    ["play", "Run all tests", "Mod+Enter"],
    ["arrowSync", "Rerun failed tests", "Mod+Shift+Enter"],
    ["eye", "Toggle watch mode", "W"],
    ["shieldCheck", "Collect coverage", "C"],
    ["history", "View previous run", "Mod+H"],
    ["settings", "Open settings", "Mod+,"],
  ];
  const filtered = () => commands.filter((c) => c[1].toLowerCase().includes(query().toLowerCase()));
  const shortcuts: [string, string][] = [
    ["Mod+K", "Command palette"],
    ["Mod+Enter", "Run all"],
    ["F8", "Next failure"],
    ["W", "Toggle watch"],
    ["Enter", "Run selected test"],
    ["O", "Open in editor"],
    ["Mod+`", "Toggle output"],
    ["?", "This overlay"],
  ];
  return section(
    { id: "dialog", title: "Dialog", icon: "open", group: "Components" },
    ["A modal on the native ", h("code", null, "<dialog>"), " (", h("code", null, "showModal()"), "): top layer, inert page, focus trap, Escape and backdrop click close it, focus returns to the opener."],
    demo({
      title: "Shortcut overlay",
      preview: [
        button({ variant: "accent", onclick: () => open.set(true) }, "Keyboard shortcuts"),
        dialog({
          open,
          onClose: () => open.set(false),
          title: "Keyboard shortcuts",
          description: "Shortcuts work anywhere except in text fields.",
          size: "md",
          content: () => h("table", { class: "g-shortcuts" }, h("tbody", null, shortcuts.map(([k, what]) => h("tr", null, h("td", null, what), h("td", null, kbd(k)))))),
          footer: () => button({ onclick: () => open.set(false) }, "Close"),
        }),
      ],
      code: `
dialog({ open, onClose: () => open.set(false), title: "Keyboard shortcuts", size: "md",
  content: () => shortcutTable(), footer: () => button({ onclick: close }, "Close") })`,
      keys: [
        ["Tab", "cycles inside the dialog"],
        ["Shift+Tab", "backwards"],
        ["Escape", "close, focus back to the opener"],
      ],
    }),
    demo({
      title: "Command palette shell",
      sub: "placement: top, bare, initialFocus on the input",
      preview: [
        button({ icon: "search", onclick: () => (query.set(""), sel.set(0), palette.set(true)) }, "Command palette ", kbd("Mod+K")),
        dialog({
          open: palette,
          onClose: () => palette.set(false),
          label: "Command palette",
          placement: "top",
          bare: true,
          size: "md",
          initialFocus: "input",
          content: () =>
            h(
              "div",
              null,
              h(
                "div",
                { class: "g-palette-search" },
                icon("search", { size: 18 }),
                h("input", {
                  class: "g-palette-input",
                  type: "text",
                  role: "combobox",
                  "aria-expanded": "true",
                  "aria-controls": "g-palette-list",
                  "aria-activedescendant": () => (filtered().length ? `g-cmd-${sel()}` : undefined),
                  "aria-label": "Type a command",
                  placeholder: "Type a command…",
                  oninput: (e) => (query.set((e.currentTarget as HTMLInputElement).value), sel.set(0)),
                  onkeydown: (e) => {
                    const n = filtered().length;
                    if (e.key === "ArrowDown") (e.preventDefault(), sel.set((sel.peek() + 1) % Math.max(1, n)));
                    if (e.key === "ArrowUp") (e.preventDefault(), sel.set((sel.peek() - 1 + n) % Math.max(1, n)));
                    if (e.key === "Enter") palette.set(false);
                  },
                }),
              ),
              h("ul", { class: "g-palette-list", id: "g-palette-list", role: "listbox", "aria-label": "Commands" }, () =>
                filtered().map(([ic, label, keys], i) =>
                  h("li", { class: "g-palette-item", role: "option", id: `g-cmd-${i}`, "aria-selected": () => sel() === i, onclick: () => palette.set(false) }, icon(ic as never, { size: 16 }), label, kbd(keys)),
                ),
              ),
              h("div", { class: "g-palette-foot" }, h("span", null, kbd("ArrowUp"), " ", kbd("ArrowDown"), " navigate"), h("span", null, kbd("Enter"), " run"), h("span", null, kbd("Escape"), " close")),
            ),
        }),
      ],
      code: `
dialog({ open, onClose, label: "Command palette", placement: "top", bare: true,
  initialFocus: "input", content: () => palette() })`,
    }),
  );
}

// ---- feedback ----------------------------------------------------------------------------------------

function messageBarSection() {
  const dismissed = signal(false);
  return section(
    { id: "messagebar", title: "Message bar", icon: "warning", group: "Components" },
    ["Inline status with a title, a body, actions and an optional dismiss button. Errors are ", h("code", null, 'role="alert"'), ", the rest ", h("code", null, 'role="status"'), "."],
    demo({
      title: "Intents",
      col: true,
      page: true,
      preview: [
        messageBar({ intent: "info", title: "Coverage is collected per test", body: "Select a test to see the lines it covers.", role: null }),
        messageBar({ intent: "success", title: "All 128 tests passed", body: "Finished in 4.2 s.", role: null }),
        messageBar({ intent: "warning", title: "Skipped", body: "Requires a database connection.", role: null }),
        messageBar({ intent: "error", title: "System.DivideByZeroException", body: "Attempted to divide by zero.\n   at Calc.Divide(Int32 a, Int32 b) in Calc.cs:line 18", mono: true, role: null, actions: messageBarAction("Copy", () => {}) }),
        () =>
          dismissed()
            ? button({ size: "small", onclick: () => dismissed.set(false) }, "Show the dismissible bar again")
            : messageBar({ intent: "info", title: "Dismissible", body: "Click × to dismiss.", onDismiss: () => dismissed.set(true), role: null }),
      ],
      code: `
messageBar({ intent: "error", title: test.errorType, body: test.errorMessage, mono: true,
  actions: messageBarAction("Copy", copy), onDismiss: () => hide() })`,
    }),
    demo({
      title: "Top bars (shell)",
      sub: "top: true: one line, title and body inline, ellipsis",
      col: true,
      page: true,
      preview: [
        messageBar({ intent: "warning", top: true, icon: "plugDisconnected", title: "Connection to the explorer host lost.", body: "Reconnecting in 3s… Results shown may be stale.", actions: messageBarAction("Retry now", () => {}), role: null }),
        messageBar({ intent: "snapshot", top: true, title: "Viewing run from 10:42", body: "(118 passed, 3 failed) — read-only snapshot", actions: messageBarAction("Back to live", () => {}), role: null }),
        messageBar({ intent: "error", top: true, title: "Calc.Tests failed to build.", body: "Calc.cs(18,5): error CS1002: ; expected", actions: messageBarAction("View details", () => {}), role: null }),
      ],
      code: `messageBar({ intent: "warning", top: true, icon: "plugDisconnected", title: "Connection lost.",
  body: "Reconnecting…", actions: messageBarAction("Retry now", retry) })`,
    }),
  );
}

function meterSection() {
  const v = signal(0.64);
  const running = signal<number | undefined>(0.4);
  return section(
    { id: "meter", title: "Meter and progress", icon: "dataPie", group: "Components" },
    ["A meter shows a value in a known range (coverage) with tones from thresholds; a progress bar shows task progress, or an indeterminate sweep when ", h("code", null, "value"), " is undefined."],
    demo({
      title: "Meter",
      preview: h(
        "div",
        { class: "g-stack" },
        row("92 % good", meter({ value: 0.92, label: "Line coverage (good)", showValue: true })),
        row("64 % warn", meter({ value: 0.64, label: "Line coverage (warn)", showValue: true })),
        row("31 % bad", meter({ value: 0.31, label: "Line coverage (bad)", showValue: true })),
        row("small", meter({ value: 0.82, label: "Branch coverage", size: "sm" }), meter({ value: 0.55, label: "Branch coverage 2", size: "sm" }), meter({ value: 0.2, label: "Branch coverage 3", size: "sm" })),
        row(
          "live",
          h("input", { class: "g-range", type: "range", min: "0", max: "100", value: "64", "aria-label": "Meter value", oninput: (e) => v.set(Number((e.currentTarget as HTMLInputElement).value) / 100) }),
          meter({ value: v, label: "Live meter", showValue: true, class: "g-live-meter" }),
        ),
      ),
      code: `meter({ value: () => report().lineRate, label: "Line coverage", showValue: true })\nmeter({ value: rate, size: "sm", label: "Branch coverage", thresholds: { good: 0.9, warn: 0.6 } })`,
    }),
    demo({
      title: "Progress",
      preview: h(
        "div",
        { class: "g-stack" },
        row("determinate", progressBar({ value: running, label: "Run progress" }), h("span", { class: "g-out" }, () => (running() === undefined ? "…" : formatPercent(running()!)))),
        row("indeterminate", progressBar({ label: "Discovering tests" })),
        row("passed", progressBar({ value: 1, tone: "passed", label: "Passed run" })),
        row("failed", progressBar({ value: 0.7, tone: "failed", label: "Failing run" })),
        h("div", { class: "g-row" }, button({ size: "small", onclick: () => running.set(Math.min(1, (running.peek() ?? 0) + 0.15)) }, "Advance"), button({ size: "small", onclick: () => running.set(undefined) }, "Indeterminate"), button({ size: "small", onclick: () => running.set(0) }, "Reset")),
      ),
      code: `progressBar({ value: () => done() / total(), label: "Run progress" })\nprogressBar({ label: "Discovering tests" })   // indeterminate`,
      notes: "With reduced motion the indeterminate bar is a static striped bar.",
    }),
  );
}

function emptySection() {
  return section(
    { id: "empty", title: "Empty state", icon: "beakerFilled", group: "Components" },
    "Explains why a view is empty and what to do next.",
    demo({
      title: "With illustration and actions",
      page: true,
      preview: emptyState({
        icon: "shieldCheck",
        title: "No coverage report yet",
        body: "This page shows the overall line and branch coverage of a full run. Run all tests once with coverage to fill it.",
        actions: [button({ variant: "accent", icon: "play" }, "Run all with coverage"), button({ variant: "link" }, "Learn more")],
      }),
      code: `
emptyState({ icon: "shieldCheck", title: "No coverage report yet", body: "…",
  actions: button({ variant: "accent", icon: "play", onclick: run }, "Run all with coverage") })`,
    }),
    demo({
      title: "Loading (small)",
      page: true,
      preview: emptyState({ spinner: true, size: "small", title: "Connecting to the explorer host…", body: "Waiting for the first state snapshot." }),
      code: `emptyState({ spinner: true, size: "small", title: "Loading tests…" })`,
    }),
  );
}

function propertySection() {
  return section(
    { id: "propgrid", title: "Property grid", icon: "document", group: "Components" },
    ["Label/value rows as a description list (", h("code", null, "dl > dt + dd"), ")."],
    demo({
      title: "Property grid",
      preview: h(
        "div",
        { style: "width:100%;max-width:560px" },
        propertyGrid(
          [
            { name: "Status", value: statusBadge("failed") },
            { name: "Duration", value: "12.4 ms" },
            { name: "Last run", value: "Today 10:42 · 3 failed, 118 passed" },
            { name: "Traits", value: [badge({}, "Category: Math"), badge({}, "Owner: erik")], badges: true },
            { name: "Source", value: "tests/Calc.Tests/CalcTests.cs:42", mono: true },
          ],
          { label: "Test properties" },
        ),
      ),
      code: `
propertyGrid([
  { name: "Status", value: statusBadge(() => test().outcome) },
  { name: "Duration", value: () => formatMs(test().durationMs) },
  { name: "Traits", value: traitBadges, badges: true },
], { label: "Test properties" })`,
    }),
  );
}

function codeSection() {
  const src = `using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Calc.Tests;

[TestClass]
public sealed class CalculatorTests
{
    [TestMethod]
    [DataRow(6, 3, 2)]
    public void Divide_ReturnsQuotient(int a, int b, int expected)
    {
        // Arrange
        var calc = new Calculator();
        var result = calc.Divide(a, b);
        Assert.AreEqual(expected, result, "a very long assertion message that keeps going so the line overflows the box");
    }
}`;
  return section(
    { id: "code", title: "Code block", icon: "code", group: "Components" },
    [
      "Monospace block with optional line numbers (CSS-generated, never copied), marked lines and soft wrap. Highlighting is a function you pass in, so the design system has no highlight.js dependency (the gallery passes one). The block is focusable so keyboard users can scroll it.",
    ],
    demo({
      title: "Highlighted, numbered, marked line",
      preview: h("div", { style: "width:100%" }, codeBlock({ code: src, language: "csharp", highlight, lineNumbers: true, startLine: 1, markLines: [15], label: "CalculatorTests.cs" })),
      code: `
codeBlock({ code: () => source(), language: "csharp", highlight, lineNumbers: true,
  startLine: 40, markLines: () => [failingLine()], label: "Source of CalculatorTests.cs" })`,
    }),
    demo({
      title: "Plain, wrapped",
      preview: h("div", { style: "width:100%" }, codeBlock({ code: "Assert.AreEqual failed. Expected:<3>. Actual:<2>. a very long assertion message that keeps going so the line overflows the box and needs to wrap onto the next line\n   at Calc.Tests.CalculatorTests.Divide_ReturnsQuotient(Int32 a, Int32 b, Int32 expected) in CalcTests.cs:line 15", wrap: true, label: "Stack trace" })),
      code: `codeBlock({ code: stackTrace, wrap: true, label: "Stack trace" })`,
    }),
  );
}

function splitSection() {
  const saved = signal(260);
  const savedH = signal(120);
  return section(
    { id: "split", title: "Split pane", icon: "expandAll", group: "Components" },
    ["Resizable panes (APG Window Splitter). ", h("code", null, "splitPane()"), " owns its size and reports it for persisting; ", h("code", null, "splitter()"), " is the bare handle for layouts you own."],
    demo({
      title: "Row split",
      sub: () => `persisted width: ${saved()} px`,
      preview: h(
        "div",
        { class: "g-split-demo" },
        splitPane({
          direction: "row",
          label: "Resize test tree",
          initialSize: saved.peek(),
          min: 160,
          minOther: 200,
          onResizeEnd: saved.set,
          first: h("div", { class: "g-pane" }, h("strong", null, "Tree"), "Drag the handle or focus it and use the arrow keys."),
          second: splitPane({
            direction: "column",
            sized: "second",
            label: "Resize output panel",
            initialSize: savedH.peek(),
            min: 60,
            minOther: 60,
            onResizeEnd: savedH.set,
            first: h("div", { class: "g-pane" }, h("strong", null, "Detail"), "The bottom pane is sized; the top one flexes."),
            second: h("div", { class: "g-pane" }, h("strong", null, "Output"), () => `height ${savedH()} px`),
          }),
        }),
      ),
      code: `
splitPane({ direction: "row", label: "Resize test tree", initialSize: loadPref("treeWidth", 440),
  min: 280, minOther: 320, onResizeEnd: (px) => savePref("treeWidth", px),
  first: treePane, second: detailPane })

// bare handle in your own grid:
splitter({ orientation: "vertical", label: "Resize test tree", size: treeW, onResize: treeW.set,
  min: 280, max: () => main.clientWidth - 320, onResizeEnd: (px) => savePref("treeWidth", px) })`,
      keys: [
        ["ArrowRight", "grow by 16 px (Shift: 80)"],
        ["ArrowLeft", "shrink"],
        ["Home", "minimum"],
        ["End", "maximum"],
        ["Enter", "collapse / restore"],
      ],
    }),
  );
}

function kbdSection() {
  return section(
    { id: "kbd", title: "Kbd", icon: "textWrap", group: "Components" },
    ["Shortcut labels. ", h("code", null, "Mod"), " is Ctrl on Windows/Linux and ⌘ on macOS; a space separates a key sequence. Screen readers get one spoken label."],
    demo({
      title: "Shortcuts",
      preview: [kbd("Mod+K"), kbd("Mod+Shift+Enter"), kbd("F8"), kbd("?"), kbd("g g"), kbd("ArrowUp"), kbd("Escape"), kbd("Mod+K", { mac: true })],
      code: `kbd("Mod+K")          // Ctrl K  /  ⌘ K\nkbd("Mod+Shift+Enter")\nkbd("g g")            // a sequence`,
    }),
  );
}

export function componentSections(): HTMLElement[] {
  return [
    buttonsSection(),
    chipsSection(),
    iconsSection(),
    switchSection(),
    segmentedSection(),
    tabsSection(),
    menuSection(),
    tooltipSection(),
    dialogSection(),
    messageBarSection(),
    meterSection(),
    codeSection(),
    emptySection(),
    propertySection(),
    splitSection(),
    kbdSection(),
  ];
}
