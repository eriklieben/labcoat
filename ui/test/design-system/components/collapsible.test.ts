import { beforeEach, describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system/core";
import { caret, collapsible, disclosure } from "@eriklieben/labcoat-design-system/components";

describe("collapsible()", () => {
  beforeEach(() => vi.useFakeTimers());

  it("lazy: content is created on first open and kept; .open toggles with an animating window", () => {
    const open = signal(false);
    const content = vi.fn(() => h("p", null, "Body"));
    const el = root(() => collapsible({ open, content, id: "sec", class: "extra" }));
    expect(el.id).toBe("sec");
    expect(el.className).toBe("lc-collapsible extra");
    expect(content).not.toHaveBeenCalled();
    expect(el.querySelector("p")).toBeNull();
    open.set(true);
    expect(content).toHaveBeenCalledTimes(1);
    expect(el.classList.contains("open")).toBe(true);
    expect(el.classList.contains("lc-animating")).toBe(true);
    vi.advanceTimersByTime(260);
    expect(el.classList.contains("lc-animating")).toBe(false);
    open.set(false);
    expect(el.classList.contains("open")).toBe(false);
    expect(el.querySelector("p")).not.toBeNull(); // kept so it can animate closed
    open.set(true);
    expect(content).toHaveBeenCalledTimes(1);
  });

  it("initially open: no animation classes on first render; lazy: false renders closed content", () => {
    const el = root(() => collapsible({ open: true, content: () => "x" }));
    expect(el.className).toBe("lc-collapsible open");
    const closed = root(() => collapsible({ open: false, lazy: false, content: () => h("i") }));
    expect(closed.querySelector("i")).not.toBeNull();
    expect(closed.classList.contains("open")).toBe(false);
  });

  it("tall content toggles instantly (lc-instant)", () => {
    const open = signal(true);
    const el = root(() => collapsible({ open, content: () => "x", maxAnimatedHeight: 100 }));
    Object.defineProperty(el.firstElementChild!, "scrollHeight", { get: () => 500 });
    open.set(false);
    expect(el.classList.contains("lc-instant")).toBe(true);
    expect(el.classList.contains("lc-animating")).toBe(false);
    vi.advanceTimersByTime(260);
    expect(el.classList.contains("lc-instant")).toBe(false);
  });
});

describe("disclosure()", () => {
  it("uncontrolled: the button toggles aria-expanded and the collapsible; onToggle reports", () => {
    const onToggle = vi.fn();
    const el = root(() => disclosure({ summary: "Raw message", content: () => h("pre", null, "msg"), onToggle, class: "d", buttonClass: "b" }));
    const btn = el.querySelector<HTMLButtonElement>("button")!;
    const panel = el.querySelector<HTMLElement>(".lc-collapsible")!;
    expect(el.className).toBe("lc-disclosure d");
    expect(btn.className).toBe("lc-disclosure-btn b");
    expect(btn.getAttribute("aria-expanded")).toBe("false");
    expect(btn.getAttribute("aria-controls")).toBe(panel.id);
    expect(btn.querySelector(".lc-caret")).not.toBeNull();
    expect(btn.textContent).toBe("Raw message");
    btn.click();
    expect(onToggle).toHaveBeenCalledWith(true);
    expect(btn.getAttribute("aria-expanded")).toBe("true");
    expect(panel.classList.contains("open")).toBe(true);
    expect(panel.querySelector("pre")!.textContent).toBe("msg");
    btn.click();
    expect(onToggle).toHaveBeenLastCalledWith(false);
    expect(btn.getAttribute("aria-expanded")).toBe("false");
  });

  it("controlled by an accessor: follows it; the click only reports", () => {
    const open = signal(true);
    const onToggle = vi.fn((v: boolean) => open.set(v));
    const el = root(() => disclosure({ summary: "S", content: () => "c", open, onToggle }));
    const btn = el.querySelector<HTMLButtonElement>("button")!;
    expect(btn.getAttribute("aria-expanded")).toBe("true");
    btn.click();
    expect(onToggle).toHaveBeenCalledWith(false);
    expect(btn.getAttribute("aria-expanded")).toBe("false");
    open.set(true);
    expect(btn.getAttribute("aria-expanded")).toBe("true");
  });

  it("static open: true starts expanded", () => {
    const el = root(() => disclosure({ summary: "S", content: () => "c", open: true }));
    expect(el.querySelector("button")!.getAttribute("aria-expanded")).toBe("true");
  });
});

describe("caret()", () => {
  it("is a chevron icon with .lc-caret", () => {
    const c = caret(20);
    expect(c.classList.contains("lc-caret")).toBe(true);
    expect(c.classList.contains("icon")).toBe(true);
  });
});
