/**
 * <lc-settings-page>: Settings (nav rail), in the style of the Aspire dashboard settings: sections of labelled
 * options. Port of SettingsPage.tsx plus: theme family (Catppuccin / Aspire, more under "More themes"), editor choice, trace collection
 * (server-side, only when an OTLP endpoint is known), coverage tint. Reads and writes the settings/theme signals and
 * the store directly, so it takes no props. Client settings are stored in this browser only; trace collection is
 * stored by the host per workspace.
 *
 * Host defaults (contract Round 5, ui-defaults.ts): every setting row shows where its value comes from ("Default from
 * the AppHost (Program.cs)", "Set in this browser", …); a setting the host locked is disabled ("Set by …"). "Reset to
 * defaults" forgets this browser's choices.
 */
import { button, define, disclosure, effect, h, icon, keyed, LabcoatElement, rovingIndex, segmented, show, signal, switchControl, untracked, type Child, type IconName } from "@eriklieben/labcoat-design-system";
import type { ClientCommand, EditorInfo } from "../contract";
import { getStore } from "../store";
import "./settings-page.css";
import { EDITORS, hasLocalSettings, isSettingLocked, resetLocalSettings, setSetting, settingOrigin, settings, type CoverageTint, type EditorId, type MotionPref, type Settings } from "../settings";
import { sourceLabel, uiDefaults, type UiSettingKey } from "../ui-defaults";
import { flavorNames, setThemeFamily, setThemePref, THEME_FAMILIES, themeFamily, themeFamilyInfo, themePref, type ThemeFamily, type ThemePref } from "../theme";
import { copyDiagnostics } from "../diagnostics";

const MODES: { value: ThemePref; label: string; icon: IconName }[] = [
  { value: "system", label: "System", icon: "darkTheme" },
  { value: "light", label: "Light", icon: "sun" },
  { value: "dark", label: "Dark", icon: "moon" },
];

// Catppuccin (default) and Aspire as the main pair; the other families are under "More themes".
const FAMILIES: { value: ThemeFamily; label: Child; title: string }[] = THEME_FAMILIES.filter((f) => f.primary).map((f) => ({
  value: f.id,
  label: f.id === "catppuccin" ? [f.label, h("span", { class: "segment-sub" }, flavorNames(f))] : f.label,
  title: f.description,
}));
const MORE_FAMILIES = THEME_FAMILIES.filter((f) => !f.primary);

const MOTIONS: { value: MotionPref; label: string; title: string }[] = [
  { value: "system", label: "System", title: "Follow the operating system's reduce-motion setting" },
  { value: "reduced", label: "Reduced", title: "No movement: short fades only" },
  { value: "off", label: "Off", title: "No animations at all (spinners still turn)" },
];

const TINTS: { value: CoverageTint; label: string }[] = [
  { value: "line", label: "Full line" },
  { value: "gutter", label: "Gutter only" },
];

export class SettingsPage extends LabcoatElement {
  static hostClass = "detail-pane page-view settings-page";

  protected render(): Child {
    this.setAttribute("role", "region");
    this.setAttribute("aria-label", "Settings");
    const store = getStore();
    const $ = store.$;

    // Trace collection lives on the host: show the user's choice at once, until the next state snapshot confirms it.
    const tracingPending = signal<boolean | undefined>(undefined);
    effect(() => {
      $.tracingEnabled();
      tracingPending.set(undefined);
    });
    const tracingOn = () => tracingPending() ?? !!$.tracingEnabled();
    const setTracing = (enabled: boolean) => {
      tracingPending.set(enabled);
      store.send({ type: "setTracing", enabled } as unknown as ClientCommand);
    };

    return h(
      "div",
      { class: "detail-scroll" },
      h(
        "header",
        { class: "detail-header" },
        h("div", { class: "kind-label" }, "Labcoat"),
        h(
          "div",
          { class: "detail-title-row" },
          h("h2", { class: "detail-title" }, "Settings"),
          button(
            {
              size: "small",
              icon: "arrowReset",
              class: "settings-reset",
              disabled: () => !hasLocalSettings(),
              title: "Forget the choices made in this browser: every setting goes back to its default (from the Labcoat host's settings, else the built-in one).",
              onclick: () => resetLocalSettings(),
            },
            "Reset to defaults",
          ),
        ),
        h("div", { class: "settings-note" }, () =>
          (uiDefaults().locked?.length ?? 0) > 0 || Object.keys(uiDefaults().sources ?? {}).length > 0
            ? "Stored in this browser only, except where noted. Defaults come from the Labcoat host where shown."
            : "Stored in this browser only, except where noted.",
        ),
      ),
      h(
        "div",
        { class: "settings-body" },
        section(
          "Appearance",
          row(
            "Theme",
            "The color palette: Catppuccin (default; Latte when light, Macchiato when dark) or Aspire (the Aspire dashboard look). More palettes below.",
            segmented({ label: "Theme", options: FAMILIES, value: themeFamily, onChange: setThemeFamily, disabled: () => isSettingLocked("theme") }),
            "theme",
          ),
          moreThemes(),
          row(
            "Mode",
            "Follow the operating system, or always use the light or dark variant of the theme.",
            segmented({ label: "Mode", options: MODES, value: themePref, onChange: setThemePref, disabled: () => isSettingLocked("mode") }),
            "mode",
          ),
          row(
            "Motion",
            "Small animations that show where things come from and confirm actions. Reduced keeps only short fades; Off turns them all off.",
            segmented({ label: "Motion", options: MOTIONS, value: () => settings().motion ?? "system", onChange: (v) => setSetting("motion", v), disabled: () => isSettingLocked("motion") }),
            "motion",
          ),
        ),
        section(
          "Keyboard",
          row(
            "Single-key shortcuts",
            "W toggles watch mode, ? shows the shortcuts, R / C / O run, collect coverage or open the selected test. They never fire while a button, link or field has focus. Ctrl/Cmd shortcuts and F8 always work.",
            toggle("singleKeyShortcuts", "Single-key shortcuts"),
            "singleKeyShortcuts",
          ),
        ),
        section(
          "Editor",
          row(
            "Open files in",
            () => editorHint($.editors()),
            // Rebuilt only when the host's editor list changes (availability decides which options are enabled).
            keyed(
              () => ($.editors() ?? []).map((e) => `${e.id}:${e.available}`).join(","),
              () => {
                const avail = new Map(($.editors.peek() ?? []).map((e) => [e.id, e.available]));
                return segmented<EditorId>({
                  label: "Editor",
                  options: EDITORS.map((e) => ({
                    value: e.id,
                    label: e.name,
                    disabled: avail.get(e.id) === false,
                    title: avail.get(e.id) === false ? `${e.name} was not found on this machine` : undefined,
                  })),
                  value: () => settings().editor,
                  onChange: (v) => setSetting("editor", v),
                  disabled: () => isSettingLocked("editor"),
                });
              },
            ),
            "editor",
          ),
        ),
        show($.tracingAvailable, () =>
          section(
            "Aspire",
            row(
              "Traces in the Aspire dashboard",
              "Jump from a failing integration test to the traces and logs it caused in the Aspire dashboard. Applies from the next run. Stored for this workspace by the Labcoat host.",
              switchControl({ checked: tracingOn, onChange: setTracing, label: "Traces in the Aspire dashboard" }),
            ),
          ),
        ),
        section(
          "Coverage",
          row(
            "Coverage tint",
            "How covered and uncovered lines are marked in the source view: a tint across the full line, or only a bar in the gutter.",
            segmented({ label: "Coverage tint", options: TINTS, value: () => settings().coverageTint, onChange: (v) => setSetting("coverageTint", v), disabled: () => isSettingLocked("coverageTint") }),
            "coverageTint",
          ),
        ),
        section(
          "Celebrations",
          row(
            "Confetti when tests turn green",
            "A short confetti burst when a run makes the suite green: the first green run, or the run that fixes failing tests. Further green runs stay quiet. Off when motion is reduced (Appearance → Motion, or the system setting).",
            toggle("confetti", "Confetti when tests turn green"),
            "confetti",
          ),
        ),
        section(
          "History",
          row(
            "Show partial runs in history",
            "Include runs of a single test, a class or a few tests in the run switcher and in each test's history boxes. When off, only full runs are listed; partial runs are still recorded and count for flaky detection.",
            toggle("partialRunsInHistory", "Show partial runs in history"),
            "partialRunsInHistory",
          ),
        ),
        section(
          "Output",
          row(
            "Open the output panel on errors",
            "Show the output panel automatically when a build or run logs an error.",
            toggle("openOutputOnError", "Open the output panel on errors"),
            "openOutputOnError",
          ),
        ),
        section(
          "About",
          row(
            "Diagnostics",
            "For a bug report: the Labcoat, .NET and OS versions, the test projects and their test packages, the settings (secrets left out) and the last log lines, as markdown. Remove anything private before you share it.",
            button({ size: "small", icon: "copy", onclick: () => void copyDiagnostics() }, "Copy diagnostics"),
          ),
        ),
      ),
    );
  }
}

define("lc-settings-page", SettingsPage);

declare global {
  interface HTMLElementTagNameMap {
    "lc-settings-page": SettingsPage;
  }
}

function editorHint(editors: EditorInfo[] | undefined): string {
  const base = "Used by every “open in editor” action; the Labcoat host launches it on this machine.";
  const missing = (editors ?? []).filter((e) => !e.available);
  if (!missing.length) return base;
  const names = missing.map((e) => (e.id === "visualstudio" ? `${e.name} (Windows only)` : e.name));
  return `${base} Not found: ${names.join(", ")}. Install it (and put its launcher on PATH), then restart Labcoat.`;
}

/**
 * "More themes": a disclosure (open when one of these families is active) with a radio group of swatch cards. APG
 * radio group: one tab stop (the checked card, else the first), arrow keys / Home / End move and select.
 */
function moreThemes() {
  const isMore = () => MORE_FAMILIES.some((f) => f.id === themeFamily());
  const buttons: HTMLButtonElement[] = [];
  const tabStop = () => Math.max(0, MORE_FAMILIES.findIndex((f) => f.id === themeFamily()));
  const locked = () => isSettingLocked("theme");
  const onkeydown = (e: KeyboardEvent) => {
    if (locked()) return;
    const i = rovingIndex(e, MORE_FAMILIES.length, buttons.indexOf(e.target as HTMLButtonElement), "both");
    if (i === undefined) return;
    e.preventDefault();
    buttons[i].focus();
    setThemeFamily(MORE_FAMILIES[i].id);
  };
  const strip = (colors: readonly string[], mode: string) =>
    h(
      "span",
      { class: "theme-card-strip", "data-mode": mode, style: { background: colors[0] } },
      h("span", { class: "theme-card-text", style: { background: colors[1] } }),
      colors.slice(2).map((c) => h("span", { class: "theme-card-dot", style: { background: c } })),
    );
  return h(
    "div",
    { class: "setting-row setting-row-more" },
    disclosure({
      open: untracked(isMore), // initial state only: afterwards it stays where the user put it
      class: "more-themes",
      summary: h("span", { class: "more-themes-summary" }, h("span", { class: "setting-label" }, "More themes"), () =>
        isMore() ? h("span", { class: "more-themes-current" }, `${themeFamilyInfo(themeFamily()).label} selected`) : null,
      ),
      content: () =>
        h(
          "div",
          { class: "theme-grid", role: "radiogroup", "aria-label": "More themes", "aria-disabled": () => locked(), onkeydown },
          MORE_FAMILIES.map((f, i) =>
            h(
              "button",
              {
                type: "button",
                role: "radio",
                class: () => ["theme-card", themeFamily() === f.id && "on"],
                "aria-checked": () => themeFamily() === f.id,
                tabIndex: () => (i === tabStop() ? 0 : -1),
                disabled: () => locked(),
                title: f.description,
                "aria-label": `${f.label}, ${flavorNames(f)}`,
                onclick: () => setThemeFamily(f.id),
                ref: (b) => (buttons[i] = b),
              },
              h(
                "span",
                { class: "theme-card-head" },
                h("span", { class: "theme-card-name" }, f.label),
                h("span", { class: "theme-card-check", "aria-hidden": "true" }, icon("checkmark", { size: 14 })),
              ),
              h("span", { class: "theme-card-flavors" }, flavorNames(f)),
              h("span", { class: "theme-card-strips", "aria-hidden": "true" }, strip(f.swatches.light, "light"), strip(f.swatches.dark, "dark")),
            ),
          ),
        ),
    }),
  );
}

function section(title: string, ...rows: Child[]) {
  return h("section", { class: "settings-section" }, h("h3", { class: "settings-section-title" }, title), h("div", { class: "settings-card" }, rows));
}

/** A labelled setting; `key` adds the origin hint (host default / this browser / locked) under the description. */
function row(label: string, description: Child, control: Child, key?: UiSettingKey) {
  return h(
    "div",
    { class: () => ["setting-row", key && isSettingLocked(key) && "setting-locked"] },
    h(
      "div",
      { class: "setting-text" },
      h("div", { class: "setting-label" }, label),
      h("div", { class: "setting-desc" }, description),
      key ? originHint(key) : null,
    ),
    h("div", { class: "setting-control" }, control),
  );
}

/**
 * Where the value comes from: "Set by the AppHost (Program.cs)" (locked), "Set in this browser · default from
 * .labcoat.json", "Default from ~/.labcoat.json"; nothing for a built-in default nobody changed.
 */
function originHint(key: UiSettingKey): Child {
  return () => {
    const o = settingOrigin(key);
    if (o.locked)
      return h(
        "div",
        { class: "setting-origin setting-origin-locked", title: "This setting is locked by the Labcoat host's configuration, so it can't be changed here." },
        icon("shieldCheck", { size: 14 }),
        ` Set by ${sourceLabel(o.lockedBy)}`,
      );
    if (o.local) {
      const fallback = o.source ? ` · default from ${sourceLabel(o.source)}` : "";
      return h("div", { class: "setting-origin" }, `Set in this browser${fallback}`);
    }
    if (o.source) return h("div", { class: "setting-origin" }, `Default from ${sourceLabel(o.source)}`);
    return null;
  };
}

/** Switch bound to a boolean setting. */
function toggle(key: { [K in keyof Settings]: Settings[K] extends boolean ? K : never }[keyof Settings], label: string) {
  return switchControl({ checked: () => settings()[key], onChange: (v) => setSetting(key, v), label, disabled: () => isSettingLocked(key) });
}
