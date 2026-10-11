/**
 * Keyboard shortcut labels.
 *
 *   kbd("Mod+K")        → <span class="kbd-combo" aria-label="Control+K"><kbd>Ctrl</kbd><kbd>K</kbd></span>  (⌘ K on macOS)
 *   kbd(["Shift", "?"]) → keys as given
 *   kbd("g g", )        → a space separates a sequence: <kbd>G</kbd> then <kbd>G</kbd>
 *
 * "Mod" is Ctrl on Windows/Linux and ⌘ on macOS. `<kbd>` itself is styled in base.css.
 */
import { define, h, LabcoatElement } from "../core";

export const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

const MAC: Record<string, string> = { Mod: "⌘", Ctrl: "⌃", Control: "⌃", Alt: "⌥", Option: "⌥", Shift: "⇧", Meta: "⌘", Cmd: "⌘" };
const OTHER: Record<string, string> = { Mod: "Ctrl", Control: "Ctrl", Meta: "Win", Cmd: "Ctrl" };
const COMMON: Record<string, string> = {
  ArrowUp: "↑",
  ArrowDown: "↓",
  ArrowLeft: "←",
  ArrowRight: "→",
  Escape: "Esc",
  Enter: "↵",
  Space: "Space",
  " ": "Space",
};
// Screen-reader names: by key name first, then by the displayed label (the Mac glyphs have no spoken name of their own).
const SPOKEN: Record<string, string> = {
  Ctrl: "Control",
  Esc: "Escape",
  "↵": "Enter",
  "↑": "Up",
  "↓": "Down",
  "←": "Left",
  "→": "Right",
  "⌘": "Command",
  "⌃": "Control",
  "⌥": "Option",
  "⇧": "Shift",
};
/** "Mod" follows the `mac` option (not only the platform), like its label. */
const spokenKey = (k: string, mac: boolean) => {
  if (k === "Mod") return mac ? "Command" : "Control";
  return SPOKEN[k] ?? SPOKEN[keyLabel(k, mac)] ?? keyLabel(k, mac);
};

/** The display text for one key name. */
export function keyLabel(key: string, mac = isMac): string {
  const k = (mac ? MAC[key] : OTHER[key]) ?? COMMON[key] ?? key;
  return k.length === 1 ? k.toUpperCase() : k;
}

/** Split "Mod+Shift+K" into keys (a literal "+" key is written "Plus" or as the last key: "Ctrl++"). */
export function parseCombo(combo: string): string[] {
  if (combo === "+") return ["+"];
  const keys = combo.split("+").filter(Boolean);
  if (combo.endsWith("++")) keys.push("+");
  return keys.map((k) => (k === "Plus" ? "+" : k));
}

export interface KbdOptions {
  mac?: boolean;
  class?: string;
}

/** Render a shortcut (one combo, or a space-separated sequence of combos). */
export function kbd(keys: string | readonly string[], opts: KbdOptions = {}): HTMLSpanElement {
  const mac = opts.mac ?? isMac;
  const combos: string[][] = typeof keys === "string" ? keys.split(" ").filter(Boolean).map(parseCombo) : [keys.slice()];
  const spoken = combos.map((c) => c.map((k) => spokenKey(k, mac)).join("+")).join(" then ");
  const parts: (Node | string)[] = [];
  combos.forEach((c, i) => {
    if (i) parts.push(h("span", { class: "kbd-then", "aria-hidden": "true" }, "then"));
    for (const k of c) parts.push(h("kbd", { "aria-hidden": "true" }, keyLabel(k, mac)));
  });
  return h("span", { class: ["kbd-combo", opts.class], role: "img", "aria-label": spoken }, parts);
}

export interface KbdProps {
  keys: string;
}
export interface LcKbd extends KbdProps {}
/** `<lc-kbd keys="Mod+K">`. Host is `display: contents`. */
export class LcKbd extends LabcoatElement<KbdProps> {
  static defaults: KbdProps = { keys: "" };
  static observedAttributes = ["keys"];
  attributeChangedCallback(_a: string, _o: string | null, v: string | null) {
    this.keys = v ?? "";
  }
  protected render() {
    this.style.display = "contents";
    return () => kbd(this.props.keys());
  }
}
define("lc-kbd", LcKbd);

declare global {
  interface HTMLElementTagNameMap {
    "lc-kbd": LcKbd;
  }
}
