import { setMotionLevel } from "@eriklieben/labcoat-design-system";
import { computed, signal } from "@eriklieben/labcoat-design-system/core";
import { clearThemeOverrides, themeOverrides } from "./theme";
import { isLocked, onUiDefaultsChange, sourceLabel, uiDefaults, type UiDefaults, type UiSettingKey, type UiSettingSource } from "./ui-defaults";
import { loadPref, savePref } from "./util";

/**
 * Client-side settings (Settings page). Each value is this browser's choice (localStorage `mste.settings`, only the
 * settings the user changed), else the host's default (ui-defaults.ts: settings files, AppHost, env, CLI), else the
 * built-in default below. A setting the host locked always uses the host's value (or the built-in one).
 */
export interface Settings {
  /** confetti burst when a run turns the suite green (first green run, or failing -> all green) */
  confetti: boolean;
  /** open the output panel automatically when an error is logged */
  openOutputOnError: boolean;
  /** show partial runs (a single test, a class, ...) in the run switcher and the per-test history boxes */
  partialRunsInHistory: boolean;
  /** editor used by every "open in editor" action (POST /api/open-in-editor); VS Code by default */
  editor: EditorId;
  /** coverage source view: tint the full line (default) or only the gutter */
  coverageTint: CoverageTint;
  /** single-key shortcuts (W, ?, and R / C / O in the tree); Ctrl/Cmd combos and F8 always work */
  singleKeyShortcuts: boolean;
  /** animations: follow the OS (prefers-reduced-motion), reduced (short fades only) or off; `html[data-motion]` */
  motion: MotionPref;
}

export type MotionPref = "system" | "reduced" | "off";

export type EditorId = "vscode" | "rider" | "visualstudio";
export type CoverageTint = "line" | "gutter";

export const EDITORS: { id: EditorId; name: string }[] = [
  { id: "vscode", name: "Visual Studio Code" },
  { id: "rider", name: "JetBrains Rider" },
  { id: "visualstudio", name: "Visual Studio" },
];

/** Built-in defaults (docs/configuration.md "UI settings"). */
export const BUILTIN_SETTINGS: Readonly<Settings> = {
  confetti: false,
  openOutputOnError: false,
  partialRunsInHistory: false,
  editor: "vscode",
  coverageTint: "line",
  motion: "system",
  singleKeyShortcuts: true,
};
const KEYS = Object.keys(BUILTIN_SETTINGS) as (keyof Settings)[];

/** Format marker of `mste.settings`: since v2 only the settings the user changed are stored. */
const VERSION_KEY = "$v";
type Stored = Partial<Settings> & { [VERSION_KEY]?: number };

function loadOverrides(): Partial<Settings> {
  const raw = loadPref<Stored>("settings", {}) ?? {};
  const out: Partial<Settings> = {};
  const legacy = raw[VERSION_KEY] !== 2;
  for (const k of KEYS) {
    const v = raw[k];
    if (v === undefined || typeof v !== typeof BUILTIN_SETTINGS[k]) continue;
    // v1 stored every setting: a value equal to the built-in default was (almost always) never chosen, so it must
    // not hide a default from the host.
    if (legacy && v === BUILTIN_SETTINGS[k]) continue;
    (out as Record<string, unknown>)[k] = v;
  }
  if (legacy && Object.keys(raw).length) saveOverrides(out);
  return out;
}

function saveOverrides(o: Partial<Settings>) {
  savePref("settings", { ...o, [VERSION_KEY]: 2 });
}

/** This browser's own choices (only the settings the user changed). */
export const localSettings = signal<Partial<Settings>>(loadOverrides());

function effective(key: keyof Settings, d: UiDefaults, local: Partial<Settings>): unknown {
  const def = (d as Record<string, unknown>)[key];
  if (isLocked(key, d)) return def ?? BUILTIN_SETTINGS[key];
  return local[key] ?? def ?? BUILTIN_SETTINGS[key];
}

/** Reactive effective settings: read `settings().confetti` inside effects/bindings. */
export const settings = computed<Settings>(() => {
  const d = uiDefaults();
  const local = localSettings();
  const out = {} as Record<string, unknown>;
  for (const k of KEYS) out[k] = effective(k, d, local);
  return out as unknown as Settings;
});

export function getSettings() {
  return settings.peek();
}

/** True when the host locked `key`: setSetting / the Settings page leave it alone. */
export function isSettingLocked(key: UiSettingKey) {
  return isLocked(key);
}

export function setSetting<K extends keyof Settings>(key: K, value: Settings[K]) {
  if (isLocked(key, uiDefaults.peek())) return;
  const next = { ...localSettings.peek(), [key]: value };
  localSettings.set(next);
  saveOverrides(next);
  applyMotion();
}

/** "Set by the AppHost (Program.cs)" when the host locked `key` (reactive), else undefined: the hint next to a disabled control. */
export function lockedHint(key: UiSettingKey): string | undefined {
  const d = uiDefaults();
  return isLocked(key, d) ? `Set by ${sourceLabel(d.lockedBy?.[key], d)}` : undefined;
}

/** Where the effective value of a setting comes from (Settings page hints). */
export interface SettingOrigin {
  locked: boolean;
  /** layer that locked it */
  lockedBy?: UiSettingSource;
  /** this browser has its own choice (ignored while locked) */
  local: boolean;
  /** layer of the host's default, when there is one */
  source?: UiSettingSource;
}

export function settingOrigin(key: UiSettingKey): SettingOrigin {
  const d = uiDefaults();
  const local = hasLocalChoice(key);
  return {
    locked: isLocked(key, d),
    lockedBy: d.lockedBy?.[key],
    local,
    source: (d as Record<string, unknown>)[key] !== undefined ? d.sources?.[key] : undefined,
  };
}

/** This browser has its own choice for `key`. */
function hasLocalChoice(key: UiSettingKey) {
  if (key === "theme") return themeOverrides().family !== undefined;
  if (key === "mode") return themeOverrides().mode !== undefined;
  return localSettings()[key as keyof Settings] !== undefined;
}

/** Any choice stored in this browser (Settings → Reset to defaults is enabled). */
export const hasLocalSettings = computed(() => Object.keys(localSettings()).length > 0 || themeOverrides().family !== undefined || themeOverrides().mode !== undefined);

/** Forget this browser's choices (settings, theme, mode): every setting falls back to the host's / built-in default. */
export function resetLocalSettings() {
  localSettings.set({});
  try {
    localStorage.removeItem("mste.settings");
  } catch {
    /* ignore */
  }
  clearThemeOverrides();
  applyMotion();
}

function applyMotion() {
  setMotionLevel(settings.peek().motion ?? "system");
}

// the host's defaults can change the effective motion level (snapshot after a reconnect to a restarted host)
onUiDefaultsChange(applyMotion);

// apply the motion preference before the first render (also read by the pre-paint script in index.html)
applyMotion();
