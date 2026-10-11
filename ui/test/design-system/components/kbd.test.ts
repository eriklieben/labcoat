import { describe, expect, it } from "vitest";
import { isMac, kbd, keyLabel, parseCombo } from "../../../design-system/src/components/kbd";

const keys = (el: HTMLElement) => [...el.querySelectorAll("kbd")].map((k) => k.textContent);

describe("keyLabel", () => {
  it("maps modifiers per platform and arrows/escape everywhere; single letters are upper-cased", () => {
    expect(keyLabel("Mod", false)).toBe("Ctrl");
    expect(keyLabel("Mod", true)).toBe("⌘");
    expect(keyLabel("Alt", true)).toBe("⌥");
    expect(keyLabel("Shift", true)).toBe("⇧");
    expect(keyLabel("Shift", false)).toBe("Shift");
    expect(keyLabel("Meta", false)).toBe("Win");
    expect(keyLabel("ArrowUp", false)).toBe("↑");
    expect(keyLabel("Escape", true)).toBe("Esc");
    expect(keyLabel(" ", false)).toBe("Space");
    expect(keyLabel("k", false)).toBe("K");
    expect(keyLabel("F8", false)).toBe("F8");
    expect(keyLabel("k")).toBe("K");
  });

  it("isMac reflects the platform (happy-dom: Linux)", () => {
    expect(isMac).toBe(false);
  });
});

describe("parseCombo", () => {
  it("splits on + with a literal plus key", () => {
    expect(parseCombo("Mod+Shift+K")).toEqual(["Mod", "Shift", "K"]);
    expect(parseCombo("+")).toEqual(["+"]);
    expect(parseCombo("Ctrl++")).toEqual(["Ctrl", "+"]);
    expect(parseCombo("Ctrl+Plus")).toEqual(["Ctrl", "+"]);
  });
});

describe("kbd()", () => {
  it("renders one <kbd> per key with a spoken label", () => {
    const el = kbd("Mod+K", { mac: false, class: "x" });
    expect(el.className).toBe("kbd-combo x");
    expect(el.getAttribute("role")).toBe("img");
    expect(keys(el)).toEqual(["Ctrl", "K"]);
    expect(el.getAttribute("aria-label")).toBe("Control+K");
    expect(el.querySelectorAll('kbd[aria-hidden="true"]')).toHaveLength(2);
  });

  it("mac glyphs; space-separated sequences; arrays as given", () => {
    const m = kbd("Mod+Shift+P", { mac: true });
    expect(keys(m)).toEqual(["⌘", "⇧", "P"]);
    // the spoken label follows the `mac` option and names the glyphs (regression: was "Control+⇧+P" on Linux)
    expect(m.getAttribute("aria-label")).toBe("Command+Shift+P");
    expect(kbd("Ctrl+Alt+Delete", { mac: true }).getAttribute("aria-label")).toBe("Control+Option+Delete");
    expect(kbd("Mod+Alt+K", { mac: false }).getAttribute("aria-label")).toBe("Control+Alt+K");
    const seq = kbd("g g", { mac: false });
    expect(keys(seq)).toEqual(["G", "G"]);
    expect(seq.querySelector(".kbd-then")!.textContent).toBe("then");
    expect(seq.getAttribute("aria-label")).toBe("G then G");
    const arr = kbd(["Shift", "?"], { mac: false });
    expect(keys(arr)).toEqual(["Shift", "?"]);
    expect(kbd("Escape").getAttribute("aria-label")).toBe("Escape");
    expect(kbd("ArrowDown Enter").getAttribute("aria-label")).toBe("Down then Enter");
  });
});

describe("<lc-kbd>", () => {
  it("renders the keys attribute", () => {
    const el = document.createElement("lc-kbd");
    el.setAttribute("keys", "Mod+K");
    document.body.append(el);
    expect(el.style.display).toBe("contents");
    expect(keys(el.querySelector(".kbd-combo") as HTMLElement)).toEqual(["Ctrl", "K"]);
    el.setAttribute("keys", "F8");
    expect(el.textContent).toBe("F8");
    el.removeAttribute("keys");
    expect(el.keys).toBe("");
    expect(el.querySelectorAll("kbd")).toHaveLength(0);
  });
});
