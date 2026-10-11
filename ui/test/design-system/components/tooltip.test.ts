import { beforeEach, describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system/core";
import { tooltip } from "@eriklieben/labcoat-design-system/components";

const pointer = (el: Element, type: string, pointerType = "mouse") => {
  const e = new Event(type, { bubbles: false }) as Event & { pointerType: string };
  Object.defineProperty(e, "pointerType", { value: pointerType });
  el.dispatchEvent(e);
};
const tip = () => document.querySelector<HTMLElement>(".lc-tooltip");

function setup(text: string | (() => string) = "Copy the error", opts: Parameters<typeof tooltip>[2] = {}, parent?: HTMLElement) {
  let btn!: HTMLButtonElement;
  const dispose = root((d) => {
    btn = tooltip(h("button", null, "Copy"), text, opts);
    (parent ?? document.body).append(btn);
    return d;
  });
  return { btn, dispose };
}

describe("tooltip()", () => {
  beforeEach(() => vi.useFakeTimers());

  it("shows after the hover delay with role=tooltip and wires aria-describedby", () => {
    const { btn } = setup();
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(499);
    expect(tip()).toBeNull();
    vi.advanceTimersByTime(1);
    const t = tip()!;
    expect(t.getAttribute("role")).toBe("tooltip");
    expect(t.textContent).toBe("Copy the error");
    expect(btn.getAttribute("aria-describedby")).toBe(t.id);
    // entering again while shown keeps it
    pointer(btn, "pointerenter");
    expect(tip()).toBe(t);
  });

  it("hides shortly after pointer leave; hovering the tooltip keeps it open (WCAG 1.4.13)", () => {
    const { btn } = setup();
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(500);
    pointer(btn, "pointerleave");
    pointer(tip()!, "pointerenter");
    vi.advanceTimersByTime(500);
    expect(tip()).not.toBeNull();
    pointer(tip()!, "pointerleave");
    vi.advanceTimersByTime(120);
    expect(tip()).toBeNull();
    expect(btn.hasAttribute("aria-describedby")).toBe(false);
  });

  it("touch pointers don't show it; leaving before the delay cancels it", () => {
    const { btn } = setup();
    pointer(btn, "pointerenter", "touch");
    vi.advanceTimersByTime(1000);
    expect(tip()).toBeNull();
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(200);
    pointer(btn, "pointerleave");
    vi.advanceTimersByTime(1000);
    expect(tip()).toBeNull();
  });

  it("Escape hides it (and stops propagation); pointerdown and blur hide it", () => {
    const { btn } = setup("x", { delay: 10 });
    const outer = vi.fn();
    document.addEventListener("keydown", outer);
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(10);
    expect(tip()).not.toBeNull();
    btn.dispatchEvent(new KeyboardEvent("keydown", { key: "a", bubbles: true }));
    expect(tip()).not.toBeNull();
    btn.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(tip()).toBeNull();
    expect(outer).toHaveBeenCalledTimes(1); // only the "a"
    document.removeEventListener("keydown", outer);

    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(10);
    btn.dispatchEvent(new Event("pointerdown"));
    expect(tip()).toBeNull();

    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(10);
    btn.dispatchEvent(new Event("blur"));
    expect(tip()).toBeNull();
  });

  it("keyboard focus (:focus-visible) shows it immediately", () => {
    const { btn } = setup();
    const matches = btn.matches.bind(btn);
    btn.matches = (s: string) => (s === ":focus-visible" ? true : matches(s));
    btn.dispatchEvent(new Event("focus"));
    expect(tip()).not.toBeNull();
  });

  it("mouse focus (not :focus-visible) does not show it", () => {
    const { btn } = setup();
    btn.matches = () => false;
    btn.dispatchEvent(new Event("focus"));
    expect(tip()).toBeNull();
  });

  it("keeps existing aria-describedby ids; aria: 'none' doesn't touch it", () => {
    const a = setup("A", { delay: 0 });
    a.btn.setAttribute("aria-describedby", "other");
    pointer(a.btn, "pointerenter");
    vi.advanceTimersByTime(0);
    expect(a.btn.getAttribute("aria-describedby")).toBe(`other ${tip()!.id}`);
    a.btn.dispatchEvent(new Event("blur"));
    expect(a.btn.getAttribute("aria-describedby")).toBe("other");
    a.dispose();

    const b = setup("B", { delay: 0, aria: "none" });
    pointer(b.btn, "pointerenter");
    vi.advanceTimersByTime(0);
    expect(tip()).not.toBeNull();
    expect(b.btn.hasAttribute("aria-describedby")).toBe(false);
  });

  it("reactive text updates; empty text shows nothing; detached targets show nothing", () => {
    const text = signal("one");
    const { btn } = setup(text, { delay: 0 });
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(0);
    expect(tip()!.textContent).toBe("one");
    text.set("two");
    expect(tip()!.textContent).toBe("two");
    btn.dispatchEvent(new Event("blur"));
    text.set("");
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(0);
    expect(tip()).toBeNull();
    text.set("three");
    btn.remove();
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(0);
    expect(tip()).toBeNull();
  });

  it("is placed inside the target's landmark / dialog", () => {
    const nav = document.createElement("nav");
    document.body.append(nav);
    const { btn } = setup("in nav", { delay: 0 }, nav);
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(0);
    expect(tip()!.parentElement).toBe(nav);
  });

  it("disposing the owner hides the tooltip and removes the listeners", () => {
    const { btn, dispose } = setup("x", { delay: 0 });
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(0);
    dispose();
    expect(tip()).toBeNull();
    pointer(btn, "pointerenter");
    vi.advanceTimersByTime(1000);
    expect(tip()).toBeNull();
  });
});
