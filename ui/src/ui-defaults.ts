/**
 * Defaults for the client settings that come from outside the browser (contract Round 5): the user's and the
 * workspace's settings files, the Aspire AppHost, LABCOAT_UI_SETTINGS and CLI flags, merged by the host.
 *
 * Effective value of a setting = locked ? (default ?? built-in) : (this browser's choice ?? default ?? built-in).
 *
 * Sources, in the order they arrive:
 *  1. `<html data-labcoat-defaults="{…}">`, injected into index.html by the host (also read by the pre-paint script
 *     there), so the first paint already uses them;
 *  2. `ExplorerState.uiDefaults` of every state snapshot (watchUiDefaults(), from the app element);
 *  3. mock mode only: `?mock&uiDefaults=<url-encoded JSON>` (UI checks without a host).
 */
import { signal } from "@eriklieben/labcoat-design-system/core";
import type { UiDefaults, UiSettingKey, UiSettingSource } from "./contract";
import { serverEventListeners } from "./server-events";

export type { UiDefaults, UiSettingKey, UiSettingSource } from "./contract";

const CHOICES: Partial<Record<UiSettingKey, readonly string[]>> = {
  theme: ["catppuccin", "aspire", "github", "one", "dracula", "solarized", "gruvbox"],
  mode: ["system", "light", "dark"],
  motion: ["system", "reduced", "off"],
  editor: ["vscode", "rider", "visualstudio"],
  coverageTint: ["line", "gutter"],
};
const FLAGS: readonly UiSettingKey[] = ["confetti", "partialRunsInHistory", "openOutputOnError", "singleKeyShortcuts"];
export const UI_SETTING_KEYS = [...(Object.keys(CHOICES) as UiSettingKey[]), ...FLAGS];
const SOURCES: readonly UiSettingSource[] = ["user", "workspace", "apphost", "env", "cli"];

/** Keeps only known keys with valid values (the host validates too; this guards the mock hook and old hosts). */
export function sanitizeUiDefaults(raw: unknown): UiDefaults {
  const out: Record<string, unknown> = {};
  if (!raw || typeof raw !== "object") return out;
  const r = raw as Record<string, unknown>;
  for (const key of UI_SETTING_KEYS) if (isValidValue(key, r[key])) out[key] = r[key];
  if (Array.isArray(r.locked)) out.locked = UI_SETTING_KEYS.filter((k) => (r.locked as unknown[]).includes(k));
  for (const map of ["sources", "lockedBy"] as const) {
    const m = r[map];
    if (m && typeof m === "object") out[map] = sanitizeSources(m as Record<string, unknown>);
  }
  if (r.files && typeof r.files === "object") {
    const f = r.files as Record<string, unknown>;
    out.files = { user: stringOrUndefined(f.user), workspace: stringOrUndefined(f.workspace) };
  }
  return out as UiDefaults;
}

/** One of the setting's choices, or a boolean for a flag. */
function isValidValue(key: UiSettingKey, v: unknown) {
  const choices = CHOICES[key];
  if (choices) return typeof v === "string" && choices.includes(v);
  return typeof v === "boolean";
}

/** A setting → layer map with only known settings and layers. */
function sanitizeSources(m: Record<string, unknown>) {
  const clean: Record<string, UiSettingSource> = {};
  for (const key of UI_SETTING_KEYS) {
    const s = m[key];
    if (typeof s === "string" && (SOURCES as readonly string[]).includes(s)) clean[key] = s as UiSettingSource;
  }
  return clean;
}

const stringOrUndefined = (v: unknown) => (typeof v === "string" ? v : undefined);

const isMock = (() => {
  try {
    return new URLSearchParams(location.search).has("mock");
  } catch {
    return false;
  }
})();

function readInjected(): UiDefaults {
  const root = document.documentElement;
  // Mock hook: ?mock&uiDefaults=<json> behaves like a host-injected attribute.
  if (isMock) {
    try {
      const p = new URLSearchParams(location.search).get("uiDefaults");
      if (p) root.dataset.labcoatDefaults = p;
    } catch {
      /* ignore */
    }
  }
  try {
    return sanitizeUiDefaults(JSON.parse(root.dataset.labcoatDefaults || "{}"));
  } catch {
    return {};
  }
}

/** The merged defaults from the host (reactive). */
export const uiDefaults = signal<UiDefaults>(readInjected());

const uiDefaultsListeners = new Set<() => void>();

/** Call `listener` after `uiDefaults` changed (settings.ts re-applies the motion level). Returns the unsubscribe. */
export function onUiDefaultsChange(listener: () => void): () => void {
  uiDefaultsListeners.add(listener);
  return () => uiDefaultsListeners.delete(listener);
}

export function setUiDefaults(raw: unknown) {
  const next = sanitizeUiDefaults(raw);
  if (JSON.stringify(next) === JSON.stringify(uiDefaults.peek())) return;
  uiDefaults.set(next);
  for (const l of uiDefaultsListeners) l();
}

export function isLocked(key: UiSettingKey, d: UiDefaults = uiDefaults()) {
  return !!d.locked?.includes(key);
}

/** The host's default for `key` (undefined = the built-in one applies). */
export function defaultOf<K extends UiSettingKey>(key: K, d: UiDefaults = uiDefaults()): UiDefaults[K] | undefined {
  return d[key];
}

/** "the AppHost (Program.cs)", "~/.labcoat.json", ".labcoat.json", … */
export function sourceLabel(source: UiSettingSource | undefined, d: UiDefaults = uiDefaults()): string {
  switch (source) {
    case "user":
      return d.files?.user ?? "your user settings file";
    case "workspace":
      return d.files?.workspace ?? ".labcoat.json";
    case "apphost":
      return "the AppHost (Program.cs)";
    case "env":
      return "LABCOAT_UI_SETTINGS";
    case "cli":
      return "the command line";
    default:
      return "the Labcoat host";
  }
}

let watching = false;
/**
 * Follow `uiDefaults` of the state snapshots. Called from the app element (theme.ts followTheme), i.e. after the
 * store exists; registered once.
 */
export function watchUiDefaults() {
  if (watching) return;
  watching = true;
  serverEventListeners.add((e) => {
    if (e.type !== "state") return;
    const d = (e as unknown as { state?: { uiDefaults?: UiDefaults } }).state?.uiDefaults;
    // a host without defaults sends none: clear (except in mock mode, where the URL hook is the source)
    if (d !== undefined || !isMock) setUiDefaults(d ?? {});
  });
}
