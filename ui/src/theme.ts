import { DEFAULT_THEME_FAMILY, prefersReducedMotion, THEME_FAMILY_IDS, type ThemeFamily } from "@eriklieben/labcoat-design-system";
import { computed, effect, onCleanup, signal } from "@eriklieben/labcoat-design-system/core";
import "./theme.css";
import { isLocked, uiDefaults, watchUiDefaults } from "./ui-defaults";

/**
 * Theme = family (color palette) × mode. Applied as `<html data-theme-family="<id>" data-theme="light|dark">`;
 * the design system's theme files key their semantic tokens off those two attributes. The families (ids, labels,
 * flavor names, swatches) are the design system's THEME_FAMILIES list, re-exported here for the app.
 */
export { THEME_FAMILIES, flavorNames, isThemeFamily, themeFamilyInfo, type ThemeFamily, type ThemeFamilyInfo } from "@eriklieben/labcoat-design-system";
export type ThemePref = "system" | "light" | "dark";

// Raw strings, written only when the user picks a mode / family (so they are this browser's overrides); also read by
// the pre-paint script in index.html (keep its family whitelist in sync with THEME_FAMILIES).
const MODE_KEY = "mste.theme";
const FAMILY_KEY = "mste.themeFamily";
const MODES: readonly ThemePref[] = ["system", "light", "dark"];

function read<T extends string>(key: string, allowed: readonly T[]): T | undefined {
  try {
    const v = localStorage.getItem(key) as T | null;
    if (v && allowed.includes(v)) return v;
  } catch {
    /* ignore */
  }
  return undefined;
}
function write(key: string, v: string | undefined) {
  try {
    if (v === undefined) localStorage.removeItem(key);
    else localStorage.setItem(key, v);
  } catch {
    /* ignore */
  }
}

const mq = () => window.matchMedia?.("(prefers-color-scheme: dark)");

function applyNow(mode: "light" | "dark", family: ThemeFamily) {
  const root = document.documentElement;
  root.dataset.theme = mode;
  root.dataset.themeFamily = family;
}

// ---- animated switching ------------------------------------------------------------------------------
// A theme change fades (200 ms crossfade, --lc-motion-base) instead of snapping: the View Transitions API where available, else a
// short color transition on every element (`html.theme-transitioning`, see theme.css). Not on first paint / during
// load, not with prefers-reduced-motion; a new change while one is running finishes the previous one first.

/** set once the app has painted with its initial theme: changes before that apply instantly */
let ready = false;
let running: { skipTransition(): void } | undefined;
let fallbackTimer: number | undefined;
// the system setting or Settings → Motion (html[data-motion="reduced"|"off"])
const reducedMotion = () => prefersReducedMotion();

function apply(pref: ThemePref, family: ThemeFamily) {
  const mode = pref === "dark" || (pref === "system" && !!mq()?.matches) ? "dark" : "light";
  const root = document.documentElement;
  if (root.dataset.theme === mode && root.dataset.themeFamily === family) return;
  if (!ready || reducedMotion() || document.visibilityState === "hidden") return applyNow(mode, family);
  // don't stack: jump the previous fade to its end state
  running?.skipTransition();
  window.clearTimeout(fallbackTimer);
  const doc = document as Document & { startViewTransition?: (cb: () => void) => { finished: Promise<void>; skipTransition(): void } };
  if (doc.startViewTransition) {
    root.classList.add("theme-vt");
    const t = doc.startViewTransition(() => applyNow(mode, family));
    running = t;
    void t.finished.finally(() => {
      if (running !== t) return;
      running = undefined;
      root.classList.remove("theme-vt");
    });
    return;
  }
  root.classList.add("theme-transitioning");
  applyNow(mode, family);
  fallbackTimer = window.setTimeout(() => root.classList.remove("theme-transitioning"), 250);
}

/** This browser's own choices (undefined = not chosen: the host's default or the built-in one applies). */
export const themeOverrides = signal<{ mode?: ThemePref; family?: ThemeFamily }>({
  mode: read(MODE_KEY, MODES),
  family: read(FAMILY_KEY, THEME_FAMILY_IDS),
});

/** Mode preference (system / light / dark): locked ? host default : (this browser ?? host default ?? system). */
export const themePref = computed<ThemePref>(() => {
  const d = uiDefaults();
  const host = d.mode && MODES.includes(d.mode) ? d.mode : undefined;
  if (isLocked("mode", d)) return host ?? "system";
  return themeOverrides().mode ?? host ?? "system";
});
/** Palette family: locked ? host default : (this browser ?? host default ?? Catppuccin). */
export const themeFamily = computed<ThemeFamily>(() => {
  const d = uiDefaults();
  const host = d.theme && (THEME_FAMILY_IDS as readonly string[]).includes(d.theme) ? (d.theme as ThemeFamily) : undefined;
  if (isLocked("theme", d)) return host ?? DEFAULT_THEME_FAMILY;
  return themeOverrides().family ?? host ?? DEFAULT_THEME_FAMILY;
});

/** Pick a mode (ignored while the host locks it). */
export function setThemePref(p: ThemePref) {
  if (isLocked("mode", uiDefaults.peek())) return;
  themeOverrides.set({ ...themeOverrides.peek(), mode: p });
  write(MODE_KEY, p);
}

/** Pick a palette family (ignored while the host locks it). */
export function setThemeFamily(f: ThemeFamily) {
  if (isLocked("theme", uiDefaults.peek())) return;
  themeOverrides.set({ ...themeOverrides.peek(), family: f });
  write(FAMILY_KEY, f);
}

/** Forget this browser's mode / family (Settings → Reset to defaults). */
export function clearThemeOverrides() {
  themeOverrides.set({});
  write(MODE_KEY, undefined);
  write(FAMILY_KEY, undefined);
}

/** Apply the theme and follow the OS setting while "system" is chosen. Call inside an owner (the app element). */
export function followTheme() {
  // the host's UI defaults (theme / mode, …) from state snapshots
  watchUiDefaults();
  // Changes from the first painted frame on are animated (not the initial application during load).
  let raf = requestAnimationFrame(() => (raf = requestAnimationFrame(() => (ready = true))));
  onCleanup(() => {
    cancelAnimationFrame(raf);
    ready = false;
  });
  effect(() => {
    const pref = themePref();
    const family = themeFamily();
    apply(pref, family);
    if (pref !== "system") return;
    const m = mq();
    const on = () => apply("system", family);
    m?.addEventListener("change", on);
    onCleanup(() => m?.removeEventListener("change", on));
  });
}
