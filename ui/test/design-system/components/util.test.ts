import { describe, expect, it, vi } from "vitest";
import { h, root } from "../../../design-system/src/core";
import { announce, focusables, listen, portal, prefersReducedMotion, rovingIndex, uid } from "../../../design-system/src/components/util";
import { setMedia } from "../../helpers/dom";

const kd = (key: string) => new KeyboardEvent("keydown", { key });

describe("uid", () => {
  it("returns unique, prefixed ids", () => {
    const a = uid("menu");
    const b = uid("menu");
    expect(a).toMatch(/^lc-menu-\d+$/);
    expect(a).not.toBe(b);
    expect(uid()).toMatch(/^lc-id-\d+$/);
  });
});

describe("rovingIndex", () => {
  it("moves and wraps horizontally", () => {
    expect(rovingIndex(kd("ArrowRight"), 3, 0)).toBe(1);
    expect(rovingIndex(kd("ArrowRight"), 3, 2)).toBe(0);
    expect(rovingIndex(kd("ArrowLeft"), 3, 0)).toBe(2);
    expect(rovingIndex(kd("ArrowDown"), 3, 0)).toBeUndefined();
  });

  it("vertical and both orientations", () => {
    expect(rovingIndex(kd("ArrowDown"), 3, 0, "vertical")).toBe(1);
    expect(rovingIndex(kd("ArrowUp"), 3, 0, "vertical")).toBe(2);
    expect(rovingIndex(kd("ArrowRight"), 3, 0, "vertical")).toBeUndefined();
    expect(rovingIndex(kd("ArrowDown"), 3, 0, "both")).toBe(1);
    expect(rovingIndex(kd("ArrowLeft"), 3, 1, "both")).toBe(0);
  });

  it("Home / End jump to the first / last enabled item; disabled items are skipped", () => {
    const disabled = (i: number) => i === 0 || i === 3;
    expect(rovingIndex(kd("Home"), 4, 2, "horizontal", disabled)).toBe(1);
    expect(rovingIndex(kd("End"), 4, 1, "horizontal", disabled)).toBe(2);
    expect(rovingIndex(kd("ArrowRight"), 4, 2, "horizontal", disabled)).toBe(1);
    expect(rovingIndex(kd("ArrowRight"), 2, 0, "horizontal", () => true)).toBeUndefined();
  });

  it("ignores other keys and empty lists", () => {
    expect(rovingIndex(kd("a"), 3, 0)).toBeUndefined();
    expect(rovingIndex(kd("ArrowRight"), 0, 0)).toBeUndefined();
  });
});

describe("portal", () => {
  it("appends to the trigger's closest landmark / dialog and removes on dispose", () => {
    const nav = h("nav", null, h("button", { id: "t" }));
    document.body.append(nav);
    const pop = h("div", { class: "pop" });
    root((dispose) => {
      expect(portal(pop, nav.querySelector("#t"))).toBe(pop);
      expect(pop.parentElement).toBe(nav);
      dispose();
    });
    expect(pop.isConnected).toBe(false);
  });

  it("falls back to document.body and uses `exit` when connected", () => {
    const pop = h("div");
    const exit = vi.fn((el: HTMLElement, done: () => void) => {
      expect(el).toBe(pop);
      done();
    });
    root((dispose) => {
      portal(pop, null, { exit });
      expect(pop.parentElement).toBe(document.body);
      dispose();
    });
    expect(exit).toHaveBeenCalledTimes(1);
    expect(pop.isConnected).toBe(false);

    // already detached: no exit
    const pop2 = h("div");
    const exit2 = vi.fn();
    root((dispose) => {
      portal(pop2, undefined, { exit: exit2 });
      pop2.remove();
      dispose();
    });
    expect(exit2).not.toHaveBeenCalled();
  });
});

describe("listen", () => {
  it("adds a listener for the owner's lifetime", () => {
    const fn = vi.fn();
    root((dispose) => {
      listen(window, "resize", fn);
      listen(document, "keydown", fn, { capture: true });
      window.dispatchEvent(new Event("resize"));
      document.dispatchEvent(new KeyboardEvent("keydown"));
      expect(fn).toHaveBeenCalledTimes(2);
      dispose();
    });
    window.dispatchEvent(new Event("resize"));
    document.dispatchEvent(new KeyboardEvent("keydown"));
    expect(fn).toHaveBeenCalledTimes(2);
  });
});

describe("prefersReducedMotion", () => {
  it("reads html[data-motion] first, then the media query", () => {
    expect(prefersReducedMotion()).toBe(false);
    setMedia("(prefers-reduced-motion: reduce)", true);
    expect(prefersReducedMotion()).toBe(true);
    document.documentElement.dataset.motion = "full";
    expect(prefersReducedMotion()).toBe(false);
    setMedia("(prefers-reduced-motion: reduce)", false);
    document.documentElement.dataset.motion = "reduced";
    expect(prefersReducedMotion()).toBe(true);
    document.documentElement.dataset.motion = "off";
    expect(prefersReducedMotion()).toBe(true);
  });
});

describe("announce", () => {
  it("writes into one shared polite live region after a tick (repeats are re-announced)", () => {
    vi.useFakeTimers();
    announce("Copied");
    const live = document.querySelector('[role="status"][aria-live="polite"]')!;
    expect(live.className).toBe("visually-hidden");
    expect(live.textContent).toBe("");
    vi.advanceTimersByTime(30);
    expect(live.textContent).toBe("Copied");
    announce("Copied");
    expect(live.textContent).toBe("");
    vi.advanceTimersByTime(30);
    expect(live.textContent).toBe("Copied");
    expect(document.querySelectorAll("[aria-live]")).toHaveLength(1);
    // recreated after the region was removed
    live.remove();
    announce("Again");
    vi.advanceTimersByTime(30);
    expect(document.querySelector("[aria-live]")!.textContent).toBe("Again");
  });
});

describe("focusables", () => {
  it("lists enabled, tabbable descendants", () => {
    const box = h(
      "div",
      null,
      h("a", { href: "#x" }, "link"),
      h("a", null, "no href"),
      h("button", null, "ok"),
      h("button", { disabled: true }, "off"),
      h("input", { type: "hidden" }),
      h("input", { type: "text" }),
      h("select"),
      h("textarea", { disabled: true }),
      h("div", { tabIndex: 0 }, "tab"),
      h("div", { tabIndex: -1 }, "skip"),
      h("div", { contentEditable: "true" }),
    );
    document.body.append(box);
    const got = focusables(box).map((e) => e.tagName.toLowerCase() + (e.textContent ? `:${e.textContent}` : ""));
    expect(got).toEqual(["a:link", "button:ok", "input", "select", "div:tab", "div"]);
  });
});
