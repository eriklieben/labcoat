/**
 * Keyboard shortcuts (P1-7): the global key handler of the shell and the `?` shortcut overlay. Tree keys (Enter, O,
 * C, arrows) are handled by <lc-tree-pane>; they are listed here for the overlay only.
 *
 * Also exports `appDialog()`, the modal used by the shell's dialogs (shortcut overlay, build errors, command palette).
 */
import { dialog, h, kbd, onCleanup, signal, type Child } from "@eriklieben/labcoat-design-system";
import { getSettings, settings } from "../settings";
import "./shortcuts.css";

// ---- modal dialog -------------------------------------------------------------------------------------------

export interface AppDialogOptions {
  open: () => boolean;
  onClose: () => void;
  /** visible heading (also the accessible name); omit with `label` for a header-less dialog */
  title?: Child;
  label?: string;
  class?: string;
  size?: "sm" | "md" | "lg";
  placement?: "center" | "top";
  bare?: boolean;
  content: () => Child;
  footer?: () => Child;
  /** element to focus when opened (default: the first focusable) */
  initialFocus?: (dlg: HTMLDialogElement) => HTMLElement | null | undefined;
}

/** The design-system modal dialog with the shell's defaults. */
export function appDialog(o: AppDialogOptions): Child {
  return dialog({
    open: o.open,
    onClose: () => o.onClose(),
    title: o.title,
    label: o.label,
    class: o.class,
    size: o.size ?? "md",
    placement: o.placement,
    bare: o.bare,
    content: o.content,
    footer: o.footer,
    initialFocus: o.initialFocus,
  });
}

// ---- global shortcuts -----------------------------------------------------------------------------------------

export interface ShortcutActions {
  runAll: () => void;
  toggleWatch: () => void;
  /** F8 / Shift+F8 */
  nextFailure: (dir: 1 | -1) => void;
  openPalette: () => void;
  toggleOutput: () => void;
  /** Escape outside inputs/dialogs/popups: leave a viewed past run (never cancels a run: that is Stop / Mod+.) */
  escape: () => void;
  /** Ctrl/Cmd+. : stop the current run */
  stop: () => void;
}

/** The shortcut overlay is open. */
export const shortcutsOpen = signal(false);

const isTyping = (t: EventTarget | null) => {
  const el = t as HTMLElement | null;
  return !!el && (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement || el.isContentEditable);
};
/** Single-key shortcuts don't fire on controls: a focused button / link / field keeps its own keys (Space, Enter, typing). */
export const isOnControl = (t: EventTarget | null) => {
  const el = t as HTMLElement | null;
  return isTyping(el) || !!el?.closest?.("button, a[href], [role=button], [role=link], [role=menuitem], [role=menuitemradio], [role=option], [role=tab], [role=radio], [role=switch], summary");
};

/** Install the global keydown handler for the lifetime of the current owner. */
export function installShortcuts(a: ShortcutActions) {
  // keys that work everywhere outside dialogs, also while typing
  const globalAction = (e: KeyboardEvent, mod: boolean): (() => void) | undefined => {
    if (e.key === "F8") return () => a.nextFailure(e.shiftKey ? -1 : 1);
    // Mod+Shift+Enter is "Preview affected tests" (run-affected.ts installs it)
    if (mod && !e.shiftKey && e.key === "Enter") return () => a.runAll();
    if (e.key === "`" && e.ctrlKey) return () => a.toggleOutput();
    if (mod && !e.altKey && e.key === ".") return () => a.stop();
    return undefined;
  };
  const singleKeyAction = (e: KeyboardEvent): (() => void) | undefined => {
    if (e.key === "?") return () => shortcutsOpen.set(true);
    if (e.key === "w" || e.key === "W") return () => a.toggleWatch();
    return undefined;
  };
  const onKey = (e: KeyboardEvent) => {
    if (e.defaultPrevented) return;
    const mod = e.ctrlKey || e.metaKey;
    // Ctrl/Cmd+K works everywhere (also from the filter box), except inside an open dialog.
    if (mod && !e.altKey && !e.shiftKey && (e.key === "k" || e.key === "K")) {
      if (document.querySelector("dialog[open]")) return;
      e.preventDefault();
      a.openPalette();
      return;
    }
    if (document.querySelector("dialog[open]")) return; // dialogs handle their own keys
    const action = globalAction(e, mod);
    if (action) {
      e.preventDefault();
      action();
      return;
    }
    if (isTyping(e.target)) return;
    if (e.key === "Escape") {
      a.escape();
      return;
    }
    if (mod || e.altKey) return;
    // single-key shortcuts: off in Settings, and never while a control has focus
    if (!getSettings().singleKeyShortcuts || isOnControl(e.target)) return;
    const single = singleKeyAction(e);
    if (single) {
      e.preventDefault();
      single();
    }
  };
  window.addEventListener("keydown", onKey);
  onCleanup(() => window.removeEventListener("keydown", onKey));
}

/** A shortcut's keys: "g t"-style sequences as separate keys, "?" and "/" as a single literal key, else a combo. */
function shortcutKeys(keys: string): Child {
  if (keys.includes(" ") && !keys.startsWith("Mod")) return keys.split(" ").map((k) => kbd([k]));
  return kbd(keys === "?" || keys === "/" ? [keys] : keys);
}

// ---- overlay ---------------------------------------------------------------------------------------------------

const GROUPS: { title: string; items: [keys: string, what: string][] }[] = [
  {
    title: "Anywhere",
    items: [
      ["Mod+K", "Command palette: run, go to test, toggle watch, …"],
      ["Mod+Enter", "Run all tests"],
      ["Mod+Shift+Enter", "Preview the tests affected by your changes (git)"],
      ["F8", "Next failure"],
      ["Shift+F8", "Previous failure"],
      ["Mod+.", "Stop the current run"],
      ["W", "Toggle watch mode"],
      ["/", "Filter the test tree"],
      ["Ctrl+`", "Show / hide the output panel"],
      ["Escape", "Close a popup · back to live results (never stops a run)"],
      ["?", "This overview"],
    ],
  },
  {
    title: "Test tree",
    items: [
      ["ArrowUp ArrowDown", "Move the selection"],
      ["ArrowLeft ArrowRight", "Collapse / expand"],
      ["Space", "Collapse / expand"],
      ["Enter", "Run the selected test or group (also R, double-click)"],
      ["C", "Collect coverage for the selection"],
      ["O", "Open in editor"],
      ["Home End", "First / last row"],
    ],
  },
];

export function shortcutsDialog(): Child {
  return appDialog({
    open: shortcutsOpen,
    onClose: () => shortcutsOpen.set(false),
    title: "Keyboard shortcuts",
    class: "shortcuts-dialog",
    size: "lg",
    initialFocus: (d) => d.querySelector<HTMLElement>(".lc-dialog-close"),
    content: () => [
      h(
        "div",
        { class: "shortcut-groups" },
        GROUPS.map((g) =>
          h(
            "section",
            { class: "shortcut-group" },
            h("h3", { class: "shortcut-group-title" }, g.title),
            h(
              "dl",
              { class: "shortcut-list" },
              g.items.map(([keys, what]) =>
                h(
                  "div",
                  { class: "shortcut-item" },
                  h("dt", shortcutKeys(keys)),
                  h("dd", what),
                ),
              ),
            ),
          ),
        ),
      ),
      h("p", { class: "shortcut-note dim" }, () =>
        settings().singleKeyShortcuts
          ? "Single-key shortcuts (W, ?, R, C, O) work when no button, link or field has focus; turn them off in Settings → Keyboard."
          : "Single-key shortcuts (W, ?, R, C, O) are off (Settings → Keyboard).",
      ),
    ],
  });
}
