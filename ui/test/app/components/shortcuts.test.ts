import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h, root } from "@eriklieben/labcoat-design-system";
import { installShortcuts, isOnControl, shortcutsDialog, shortcutsOpen, type ShortcutActions } from "../../../src/components/shortcuts";
import { resetLocalSettings, setSetting } from "../../../src/settings";
import { key, text } from "../../helpers/app";

function actions(): { [K in keyof ShortcutActions]: ReturnType<typeof vi.fn> } & ShortcutActions {
  return {
    runAll: vi.fn(),
    toggleWatch: vi.fn(),
    nextFailure: vi.fn(),
    openPalette: vi.fn(),
    toggleOutput: vi.fn(),
    escape: vi.fn(),
    stop: vi.fn(),
  } as never;
}

let dispose: (() => void) | undefined;
let a: ReturnType<typeof actions>;

beforeEach(() => {
  resetLocalSettings();
  shortcutsOpen.set(false);
  a = actions();
  root((d) => {
    dispose = d;
    installShortcuts(a);
  });
});
afterEach(() => {
  dispose?.();
  dispose = undefined;
});

const press = (k: string, init: KeyboardEventInit = {}, target: EventTarget = document.body) => key(target, k, init);

describe("installShortcuts", () => {
  it("Ctrl/Cmd+K opens the palette from anywhere, also from a text field", () => {
    const input = h("input");
    document.body.append(input);
    const e = press("k", { ctrlKey: true }, input);
    expect(a.openPalette).toHaveBeenCalledTimes(1);
    expect(e.defaultPrevented).toBe(true);
    press("K", { metaKey: true });
    expect(a.openPalette).toHaveBeenCalledTimes(2);
    // with Shift / Alt it is something else
    press("k", { ctrlKey: true, shiftKey: true });
    press("k", { ctrlKey: true, altKey: true });
    expect(a.openPalette).toHaveBeenCalledTimes(2);
  });

  it("does nothing while a dialog is open (dialogs handle their own keys)", () => {
    const dlg = h("dialog");
    dlg.setAttribute("open", "");
    document.body.append(dlg);
    press("k", { ctrlKey: true });
    press("F8");
    press("Enter", { ctrlKey: true });
    press("w");
    expect(a.openPalette).not.toHaveBeenCalled();
    expect(a.nextFailure).not.toHaveBeenCalled();
    expect(a.runAll).not.toHaveBeenCalled();
    expect(a.toggleWatch).not.toHaveBeenCalled();
  });

  it("ignores events another handler already handled", () => {
    const e = new KeyboardEvent("keydown", { key: "F8", bubbles: true, cancelable: true });
    e.preventDefault();
    document.body.dispatchEvent(e);
    expect(a.nextFailure).not.toHaveBeenCalled();
  });

  it("F8 / Shift+F8 move to the next / previous failure", () => {
    expect(press("F8").defaultPrevented).toBe(true);
    press("F8", { shiftKey: true });
    expect(a.nextFailure.mock.calls).toEqual([[1], [-1]]);
  });

  it("Mod+Enter runs all, Ctrl+` toggles output, Mod+. stops — also from inputs", () => {
    const input = h("input");
    document.body.append(input);
    press("Enter", { ctrlKey: true }, input);
    press("Enter", { metaKey: true });
    expect(a.runAll).toHaveBeenCalledTimes(2);
    // Mod+Shift+Enter is "Preview affected tests" (run-affected.ts), not Run all
    press("Enter", { ctrlKey: true, shiftKey: true });
    expect(a.runAll).toHaveBeenCalledTimes(2);
    press("`", { ctrlKey: true }, input);
    expect(a.toggleOutput).toHaveBeenCalledTimes(1);
    press(".", { ctrlKey: true }, input);
    press(".", { metaKey: true });
    expect(a.stop).toHaveBeenCalledTimes(2);
    press(".", { ctrlKey: true, altKey: true });
    expect(a.stop).toHaveBeenCalledTimes(2);
  });

  it("Escape leaves a viewed run, but not from a text field", () => {
    press("Escape");
    expect(a.escape).toHaveBeenCalledTimes(1);
    const ta = h("textarea");
    document.body.append(ta);
    press("Escape", {}, ta);
    const sel = h("select");
    document.body.append(sel);
    press("Escape", {}, sel);
    expect(a.escape).toHaveBeenCalledTimes(1);
  });

  it("single keys: ? opens the overlay, W toggles watch", () => {
    expect(press("?").defaultPrevented).toBe(true);
    expect(shortcutsOpen()).toBe(true);
    press("w");
    press("W");
    expect(a.toggleWatch).toHaveBeenCalledTimes(2);
    // other keys and modified ones do nothing
    press("x");
    press("w", { ctrlKey: true });
    press("w", { altKey: true });
    expect(a.toggleWatch).toHaveBeenCalledTimes(2);
  });

  it("single keys never fire on a focused control or in a field", () => {
    const btn = h("button");
    const link = h("a", { href: "#x" });
    const tab = h("div", { role: "tab" }, h("span"));
    const input = h("input");
    document.body.append(btn, link, tab, input);
    press("w", {}, btn);
    press("w", {}, link);
    press("w", {}, tab.firstChild!);
    press("w", {}, input);
    expect(a.toggleWatch).not.toHaveBeenCalled();
  });

  it("single keys are off when the setting is off", () => {
    setSetting("singleKeyShortcuts", false);
    press("w");
    press("?");
    expect(a.toggleWatch).not.toHaveBeenCalled();
    expect(shortcutsOpen()).toBe(false);
    // combos still work
    press("Enter", { ctrlKey: true });
    expect(a.runAll).toHaveBeenCalled();
  });

  it("the listener goes away with its owner", () => {
    dispose!();
    dispose = undefined;
    press("F8");
    expect(a.nextFailure).not.toHaveBeenCalled();
  });
});

describe("isOnControl", () => {
  it("knows controls, fields and plain content", () => {
    const ce = h("div");
    ce.contentEditable = "true";
    expect(isOnControl(h("button"))).toBe(true);
    expect(isOnControl(h("input"))).toBe(true);
    expect(isOnControl(h("summary"))).toBe(true);
    expect(isOnControl(h("span", { role: "switch" }))).toBe(true);
    expect(isOnControl(h("a"))).toBe(false); // no href: not a link
    expect(isOnControl(h("div"))).toBe(false);
    expect(isOnControl(null)).toBe(false);
  });
});

describe("shortcutsDialog", () => {
  it("lists the groups and reflects the single-key setting", async () => {
    let d!: () => void;
    root((dispose) => {
      d = dispose;
      document.body.append(h("div", null, shortcutsDialog()));
    });
    expect(document.querySelector("dialog.shortcuts-dialog[open]")).toBeNull();
    shortcutsOpen.set(true);
    const dlg = document.querySelector<HTMLDialogElement>("dialog.shortcuts-dialog")!;
    expect(dlg).toBeTruthy();
    expect(dlg.open).toBe(true);
    const titles = [...dlg.querySelectorAll(".shortcut-group-title")].map((e) => e.textContent);
    expect(titles).toEqual(["Anywhere", "Test tree"]);
    expect(dlg.querySelectorAll(".shortcut-item")).toHaveLength(18);
    expect(text(dlg.querySelector(".shortcut-note"))).toContain("work when no button");
    setSetting("singleKeyShortcuts", false);
    expect(text(dlg.querySelector(".shortcut-note"))).toContain("are off");
    // the close button closes it through onClose
    dlg.querySelector<HTMLElement>(".lc-dialog-close")?.click();
    expect(shortcutsOpen()).toBe(false);
    d();
  });
});
