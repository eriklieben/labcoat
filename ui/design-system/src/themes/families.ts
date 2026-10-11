/**
 * The theme families: one list for the app (theme signal, Settings, command palette, pre-paint whitelist) and the
 * gallery. Each family is a CSS file in this folder that assigns every --lc-* token for
 * `<html data-theme-family="<id>" data-theme="light|dark">` (imported by ../styles.css).
 *
 * `swatches` are the resolved theme colors per mode, for pickers that show a family without switching to it:
 * [page background, text, accent, passed, failed, skipped]. scripts/themes-check.mjs fails when they drift from
 * the CSS.
 */
export type ThemeMode = "light" | "dark";

export interface ThemeFamilyInfo {
  /** value of `data-theme-family` (and of the theme file name) */
  id: string;
  label: string;
  /** flavor / variant names of the palette in each mode, e.g. "Latte" / "Macchiato" */
  flavors: Readonly<Record<ThemeMode, string>>;
  /** modes the family provides (all current families have both) */
  modes: readonly ThemeMode[];
  /** listed first in Settings; the others are under "More themes" */
  primary: boolean;
  /** one line for tooltips / descriptions */
  description: string;
  /** [page background, text, accent, passed, failed, skipped] per mode */
  swatches: Readonly<Record<ThemeMode, readonly [string, string, string, string, string, string]>>;
}

export const THEME_FAMILIES = [
  {
    id: "catppuccin",
    label: "Catppuccin",
    flavors: { light: "Latte", dark: "Macchiato" },
    modes: ["light", "dark"],
    primary: true,
    description: "Catppuccin (default): Latte when light, Macchiato when dark",
    swatches: {
      light: ["#eff1f5", "#4c4f69", "#8839ef", "#429037", "#d20f39", "#c95e27"],
      dark: ["#24273a", "#cad3f5", "#c6a0f6", "#a6da95", "#ed8796", "#f5a97f"],
    },
  },
  {
    id: "aspire",
    label: "Aspire",
    flavors: { light: "Light", dark: "Dark" },
    modes: ["light", "dark"],
    primary: true,
    description: "The Aspire dashboard look",
    swatches: {
      light: ["#f7f7f7", "#1a1a1a", "#512bd4", "#2b9649", "#d13438", "#c27000"],
      dark: ["#221e2d", "#ffffff", "#b9aaee", "#2da44e", "#da5d60", "#e8890c"],
    },
  },
  {
    id: "github",
    label: "GitHub",
    flavors: { light: "Light Default", dark: "Dark Default" },
    modes: ["light", "dark"],
    primary: false,
    description: "GitHub's Primer colors: Light Default and Dark Default",
    swatches: {
      light: ["#ffffff", "#1f2328", "#0969da", "#1a7f37", "#d1242f", "#9a6700"],
      dark: ["#0d1117", "#f0f6fc", "#1f6feb", "#3fb950", "#f85149", "#d29922"],
    },
  },
  {
    id: "one",
    label: "One",
    flavors: { light: "One Light", dark: "One Dark" },
    modes: ["light", "dark"],
    primary: false,
    description: "Atom's One Light and One Dark",
    swatches: {
      light: ["#fafafa", "#383a42", "#4f6aec", "#49824b", "#ca1243", "#986801"],
      dark: ["#282c34", "#c1c6cf", "#528bff", "#98c379", "#e06c75", "#d19a66"],
    },
  },
  {
    id: "dracula",
    label: "Dracula",
    flavors: { light: "Alucard", dark: "Dracula" },
    modes: ["light", "dark"],
    primary: false,
    description: "Dracula, with Alucard as its light variant",
    swatches: {
      light: ["#fffbeb", "#1f1f1f", "#644ac9", "#14710a", "#cb3a2a", "#a34d14"],
      dark: ["#282a36", "#f8f8f2", "#bd93f9", "#50fa7b", "#ff6e6e", "#ffb86c"],
    },
  },
  {
    id: "solarized",
    label: "Solarized",
    flavors: { light: "Solarized Light", dark: "Solarized Dark" },
    modes: ["light", "dark"],
    primary: false,
    description: "Ethan Schoonover's Solarized, light and dark",
    swatches: {
      light: ["#fdf6e3", "#073642", "#1e76ae", "#728a0a", "#d13230", "#9b7d0a"],
      dark: ["#002b36", "#aeb6b1", "#3a94d2", "#859900", "#e16961", "#b58900"],
    },
  },
  {
    id: "gruvbox",
    label: "Gruvbox",
    flavors: { light: "Gruvbox Light", dark: "Gruvbox Dark" },
    modes: ["light", "dark"],
    primary: false,
    description: "Gruvbox (medium contrast), light and dark",
    swatches: {
      light: ["#fbf1c7", "#3c3836", "#076678", "#79740e", "#9d0006", "#a97017"],
      dark: ["#282828", "#ebdbb2", "#83a598", "#b8bb26", "#fb4934", "#fabd2f"],
    },
  },
] as const satisfies readonly ThemeFamilyInfo[];

export type ThemeFamily = (typeof THEME_FAMILIES)[number]["id"];

export const DEFAULT_THEME_FAMILY: ThemeFamily = "catppuccin";

export const THEME_FAMILY_IDS: readonly ThemeFamily[] = THEME_FAMILIES.map((f) => f.id);

export function isThemeFamily(v: unknown): v is ThemeFamily {
  return typeof v === "string" && (THEME_FAMILY_IDS as readonly string[]).includes(v);
}

export function themeFamilyInfo(id: ThemeFamily): (typeof THEME_FAMILIES)[number] {
  return THEME_FAMILIES.find((f) => f.id === id) ?? THEME_FAMILIES[0];
}

/** "Latte / Macchiato" */
export const flavorNames = (f: ThemeFamilyInfo) => `${f.flavors.light} / ${f.flavors.dark}`;
