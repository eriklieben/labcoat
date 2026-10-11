import { describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system/core";
import { menu, menuButton, type MenuEntry } from "@eriklieben/labcoat-design-system/components";

const key = (t: EventTarget, k: string, init: KeyboardEventInit = {}) => {
  const e = new KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true, ...init });
  t.dispatchEvent(e);
  return e;
};
const items = () => [...document.querySelectorAll<HTMLButtonElement>(".lc-menu-item")];
const focused = () => (document.activeElement as HTMLElement | null)?.dataset.text;

function entries(log: string[]): MenuEntry[] {
  return [
    { label: "Copy name", icon: "copy", shortcut: "Mod+C", onSelect: () => log.push("copy") },
    { label: "Cut", description: "Remove it", onSelect: () => log.push("cut"), danger: true },
    { separator: true },
    { heading: "Sort" },
    { label: "Disabled", disabled: true, onSelect: () => log.push("disabled") },
    { label: "Check", type: "checkbox", checked: true, keepOpen: true, onSelect: () => log.push("check") },
    { label: h("b", null, "Bold"), text: "bold", onSelect: () => log.push("bold") },
  ];
}

function mountButton(opts: Partial<Parameters<typeof menuButton>[0]> = {}) {
  const log: string[] = [];
  let btn!: HTMLButtonElement;
  const dispose = root((d) => {
    btn = menuButton({ label: "More", items: entries(log), ...opts });
    document.body.append(h("main", null, btn));
    return d;
  });
  return { btn, log, dispose };
}

describe("menuButton()", () => {
  it("wires aria-haspopup / aria-expanded / aria-controls and a chevron", () => {
    const { btn } = mountButton();
    expect(btn.getAttribute("aria-haspopup")).toBe("menu");
    expect(btn.getAttribute("aria-expanded")).toBe("false");
    expect(btn.hasAttribute("aria-controls")).toBe(false);
    expect(btn.querySelector(".lc-menu-chevron")).not.toBeNull();
    expect(btn.textContent).toContain("More");
    btn.click();
    expect(btn.getAttribute("aria-expanded")).toBe("true");
    const list = document.querySelector<HTMLElement>("[role=menu]")!;
    expect(btn.getAttribute("aria-controls")).toBe(list.id);
    expect(list.getAttribute("aria-label")).toBe("More");
    btn.click();
    expect(btn.getAttribute("aria-expanded")).toBe("false");
  });

  it("renders items with roles, checked state, separators, headings, icons, descriptions and shortcuts", () => {
    const { btn } = mountButton();
    btn.click();
    const its = items();
    expect(its.map((b) => b.getAttribute("role"))).toEqual(["menuitem", "menuitem", "menuitem", "menuitemcheckbox", "menuitem"]);
    expect(its[0].hasAttribute("aria-checked")).toBe(false);
    expect(its[3].getAttribute("aria-checked")).toBe("true");
    expect(its[3].querySelector(".lc-menu-check .icon")).not.toBeNull();
    expect(its[1].classList.contains("danger")).toBe(true);
    expect(its[1].querySelector(".lc-menu-desc")!.textContent).toBe("Remove it");
    expect(its[0].querySelector(".lc-menu-icon")).not.toBeNull();
    expect(its[0].querySelector(".lc-menu-kbd")).not.toBeNull();
    expect(its[2].disabled).toBe(true);
    expect(its[4].dataset.text).toBe("bold");
    expect(document.querySelector(".lc-menu-sep")!.getAttribute("role")).toBe("separator");
    expect(document.querySelector(".lc-menu-heading")!.textContent).toBe("Sort");
    // all items roving: tabindex -1
    expect(its.every((b) => b.tabIndex === -1)).toBe(true);
  });

  it("opens at the first item; ArrowDown/Up wrap and skip disabled; Home/End", () => {
    const { btn } = mountButton();
    btn.click();
    const list = document.querySelector("[role=menu]")!;
    expect(focused()).toBe("Copy name");
    key(list, "ArrowDown");
    expect(focused()).toBe("Cut");
    key(document.activeElement!, "ArrowDown");
    expect(focused()).toBe("Check"); // "Disabled" skipped
    key(document.activeElement!, "End");
    expect(focused()).toBe("bold");
    key(document.activeElement!, "ArrowDown");
    expect(focused()).toBe("Copy name");
    key(document.activeElement!, "ArrowUp");
    expect(focused()).toBe("bold");
    key(document.activeElement!, "Home");
    expect(focused()).toBe("Copy name");
  });

  it("type-ahead jumps to the next item starting with the letter; modifier keys and spaces don't", () => {
    const { btn } = mountButton();
    btn.click();
    key(document.activeElement!, "c");
    expect(focused()).toBe("Cut");
    key(document.activeElement!, "c");
    expect(focused()).toBe("Check");
    key(document.activeElement!, "c");
    expect(focused()).toBe("Copy name");
    key(document.activeElement!, "d"); // only a disabled item starts with d
    expect(focused()).toBe("Copy name");
    const e = key(document.activeElement!, "b", { ctrlKey: true });
    expect(e.defaultPrevented).toBe(false);
    expect(focused()).toBe("Copy name");
  });

  it("ArrowDown / ArrowUp on the button open at the first / last item", () => {
    const { btn } = mountButton();
    const e = key(btn, "ArrowUp");
    expect(e.defaultPrevented).toBe(true);
    expect(focused()).toBe("bold");
    key(document.activeElement!, "Escape");
    expect(document.activeElement).toBe(btn);
    expect(btn.getAttribute("aria-expanded")).toBe("false");
    key(btn, "ArrowDown");
    expect(focused()).toBe("Copy name");
    key(btn, "x"); // ignored
  });

  it("selecting an item runs it and closes, focus back on the button; keepOpen items stay open", () => {
    const { btn, log } = mountButton();
    btn.click();
    items()[3].click();
    expect(log).toEqual(["check"]);
    expect(btn.getAttribute("aria-expanded")).toBe("true");
    items()[0].click();
    expect(log).toEqual(["check", "copy"]);
    expect(btn.getAttribute("aria-expanded")).toBe("false");
    expect(document.activeElement).toBe(btn);
  });

  it("Tab in the menu closes it", () => {
    const { btn } = mountButton();
    btn.click();
    key(document.activeElement!, "Tab");
    expect(btn.getAttribute("aria-expanded")).toBe("false");
  });

  it("icon-only variant: aria-label/title, small size, no chevron; disabled prop", () => {
    const disabled = signal(false);
    const { btn } = mountButton({ icon: "settings", iconOnly: true, size: "small", class: "x", disabled });
    expect(btn.classList.contains("icon-btn")).toBe(true);
    expect(btn.classList.contains("small")).toBe(true);
    expect(btn.classList.contains("x")).toBe(true);
    expect(btn.getAttribute("aria-label")).toBe("More");
    expect(btn.title).toBe("More");
    expect(btn.querySelector(".lc-menu-chevron")).toBeNull();
    disabled.set(true);
    expect(btn.disabled).toBe(true);
  });

  it("chevron: false hides the chevron; menuLabel names the menu", () => {
    const { btn } = mountButton({ chevron: false, menuLabel: "Actions" });
    expect(btn.querySelector(".lc-menu-chevron")).toBeNull();
    btn.click();
    expect(document.querySelector("[role=menu]")!.getAttribute("aria-label")).toBe("Actions");
  });
});

describe("menu()", () => {
  it("a radio-only menu opens on the checked item; reactive items re-render", () => {
    const open = signal(false);
    const sel = signal("b");
    const onClose = vi.fn(() => open.set(false));
    let anchor!: HTMLButtonElement;
    root(() => {
      anchor = h("button", null, "Runs");
      const list = () =>
        ["a", "b", "c"].map((x) => ({ label: x.toUpperCase(), checked: sel() === x, onSelect: () => sel.set(x) })) as MenuEntry[];
      document.body.append(anchor, h("div", null, menu({ open, anchor: () => anchor, onClose, items: list, label: "Runs", matchWidth: true, class: "run-list" })));
    });
    open.set(true);
    expect(document.querySelector(".lc-menu.run-list")).not.toBeNull();
    expect(items().map((b) => b.getAttribute("role"))).toEqual(["menuitemradio", "menuitemradio", "menuitemradio"]);
    expect(focused()).toBe("B");
    items()[2].click();
    expect(onClose).toHaveBeenCalledWith("select");
    open.set(true);
    expect(focused()).toBe("C");
    expect(items()[2].getAttribute("aria-checked")).toBe("true");
  });

  it("an empty menu still opens; navigation keys are ignored", () => {
    const open = signal(true);
    root(() => {
      const a = h("button");
      document.body.append(a, h("div", null, menu({ open, anchor: () => a, onClose: () => {}, items: [], label: "Empty" })));
    });
    const list = document.querySelector<HTMLElement>("[role=menu]")!;
    expect(list).not.toBeNull();
    expect(key(list, "ArrowDown").defaultPrevented).toBe(false);
  });

  it("entries that are neither item, separator nor heading render nothing", () => {
    const open = signal(true);
    root(() => {
      const a = h("button");
      document.body.append(a, h("div", null, menu({ open, anchor: () => a, onClose: () => {}, items: [{} as MenuEntry, { label: "X", onSelect() {} }], label: "M" })));
    });
    expect(document.querySelector("[role=menu]")!.children).toHaveLength(1);
  });
});
