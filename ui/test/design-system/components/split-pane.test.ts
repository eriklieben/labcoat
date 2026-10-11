import { describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system/core";
import { splitPane, splitter, type SplitterOptions } from "@eriklieben/labcoat-design-system/components";
import { FakeResizeObserver, triggerResize } from "../../helpers/dom";

const key = (t: EventTarget, k: string, init: KeyboardEventInit = {}) => {
  const e = new KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true, ...init });
  t.dispatchEvent(e);
  return e;
};
function ptr(t: EventTarget, type: string, init: { clientX?: number; clientY?: number; button?: number } = {}) {
  const e = new MouseEvent(type, { bubbles: true, cancelable: true, clientX: init.clientX ?? 0, clientY: init.clientY ?? 0, button: init.button ?? 0 }) as MouseEvent & { pointerId: number };
  Object.defineProperty(e, "pointerId", { value: 1 });
  t.dispatchEvent(e);
  return e;
}

function makeSplitter(extra: Partial<SplitterOptions> = {}) {
  const size = signal(300);
  const onResizeEnd = vi.fn();
  let el!: HTMLDivElement;
  root(() => {
    el = splitter({ orientation: "vertical", label: "Resize tree", size, onResize: size.set, onResizeEnd, min: 100, max: 500, ...extra });
  });
  document.body.append(el);
  el.setPointerCapture = vi.fn();
  return { el, size, onResizeEnd };
}

describe("splitter()", () => {
  it("is a focusable window splitter with aria values", () => {
    const { el, size } = makeSplitter({ controls: "pane-1", class: "extra" });
    expect(el.getAttribute("role")).toBe("separator");
    expect(el.tabIndex).toBe(0);
    expect(el.getAttribute("aria-orientation")).toBe("vertical");
    expect(el.getAttribute("aria-label")).toBe("Resize tree");
    expect(el.getAttribute("aria-controls")).toBe("pane-1");
    expect([el.getAttribute("aria-valuenow"), el.getAttribute("aria-valuemin"), el.getAttribute("aria-valuemax")]).toEqual(["300", "100", "500"]);
    expect(el.className).toBe("splitter-v extra");
    size.set(250.4);
    expect(el.getAttribute("aria-valuenow")).toBe("250");
  });

  it("keyboard: arrows by step (Shift ×5), Home/End to min/max, clamped, persisted", () => {
    const { el, size, onResizeEnd } = makeSplitter();
    expect(key(el, "ArrowRight").defaultPrevented).toBe(true);
    expect(size()).toBe(316);
    key(el, "ArrowLeft", { shiftKey: true });
    expect(size()).toBe(236);
    key(el, "End");
    expect(size()).toBe(500);
    key(el, "ArrowRight");
    expect(size()).toBe(500);
    key(el, "Home");
    expect(size()).toBe(100);
    expect(onResizeEnd).toHaveBeenLastCalledWith(100);
    expect(key(el, "x").defaultPrevented).toBe(false);
  });

  it("Enter collapses to min and restores the previous size", () => {
    const { el, size } = makeSplitter();
    key(el, "Enter");
    expect(size()).toBe(100);
    key(el, "Enter");
    expect(size()).toBe(300);
    key(el, "Enter");
    expect(size()).toBe(100);
    key(el, "ArrowRight"); // moved off min: Enter collapses again instead of restoring
    key(el, "Enter");
    expect(size()).toBe(100);
  });

  it("horizontal and inverted key mapping; no max → no aria-valuemax", () => {
    const { el, size } = makeSplitter({ orientation: "horizontal", invert: true, max: undefined, min: undefined, step: 10 });
    expect(el.className).toBe("splitter-h");
    expect(el.hasAttribute("aria-valuemax")).toBe(false);
    expect(el.getAttribute("aria-valuemin")).toBe("0");
    key(el, "ArrowUp");
    expect(size()).toBe(310);
    key(el, "ArrowDown");
    expect(size()).toBe(300);
    const v = makeSplitter({ invert: true });
    key(v.el, "ArrowLeft");
    expect(v.size()).toBe(316);
    key(v.el, "ArrowRight");
    expect(v.size()).toBe(300);
    const hz = makeSplitter({ orientation: "horizontal" });
    key(hz.el, "ArrowDown");
    expect(hz.size()).toBe(316);
    key(hz.el, "ArrowUp");
    expect(hz.size()).toBe(300);
  });

  it("pointer drag resizes (clamped), sets dragging classes, persists on pointerup", () => {
    const { el, size, onResizeEnd } = makeSplitter();
    ptr(el, "pointerdown", { clientX: 400 });
    expect(el.setPointerCapture).toHaveBeenCalledWith(1);
    expect(document.activeElement).toBe(el);
    expect(el.classList.contains("dragging")).toBe(true);
    expect(document.body.classList.contains("is-dragging")).toBe(true);
    ptr(el, "pointermove", { clientX: 450 });
    expect(size()).toBe(350);
    ptr(el, "pointermove", { clientX: 900 });
    expect(size()).toBe(500);
    ptr(el, "pointerup");
    expect(onResizeEnd).toHaveBeenCalledWith(500);
    expect(el.classList.contains("dragging")).toBe(false);
    expect(document.body.classList.contains("is-dragging")).toBe(false);
    ptr(el, "pointermove", { clientX: 0 });
    expect(size()).toBe(500);
  });

  it("horizontal inverted drag; pointercancel ends; non-primary buttons are ignored", () => {
    const { el, size, onResizeEnd } = makeSplitter({ orientation: "horizontal", invert: true });
    ptr(el, "pointerdown", { clientY: 100, button: 2 });
    expect(el.classList.contains("dragging")).toBe(false);
    ptr(el, "pointerdown", { clientY: 100 });
    expect(document.body.classList.contains("is-dragging-row")).toBe(true);
    ptr(el, "pointermove", { clientY: 60 });
    expect(size()).toBe(340);
    ptr(el, "pointercancel");
    expect(onResizeEnd).toHaveBeenCalledWith(340);
    expect(document.body.classList.contains("is-dragging-row")).toBe(false);
  });
});

describe("splitPane()", () => {
  function make(opts: Partial<Parameters<typeof splitPane>[0]> = {}) {
    const onResize = vi.fn();
    const onResizeEnd = vi.fn();
    let el!: HTMLDivElement;
    const dispose = root((d) => {
      el = splitPane({ first: h("div", { class: "a" }), second: h("div", { class: "b" }), initialSize: 300, label: "Resize", onResize, onResizeEnd, ...opts });
      return d;
    });
    document.body.append(el);
    return { el, onResize, onResizeEnd, dispose, handle: el.querySelector<HTMLElement>("[role=separator]")! };
  }

  it("row: first pane sized, handle controls it, max follows the container width", () => {
    const { el, handle, onResize, onResizeEnd } = make({ class: "my" });
    expect(el.className).toBe("lc-split lc-split-row my");
    const [a, b] = el.querySelectorAll<HTMLElement>(".lc-split-pane");
    expect(a.style.width).toBe("300px");
    expect(a.style.flex).toMatch(/none|0 0 auto/);
    expect(b.style.flex).toContain("1 1");
    expect(handle.getAttribute("aria-controls")).toBe(a.id);
    expect(handle.className).toBe("splitter-v");
    expect(handle.hasAttribute("aria-valuemax")).toBe(false);
    Object.defineProperty(el, "clientWidth", { configurable: true, get: () => 1000 });
    triggerResize(el);
    expect(handle.getAttribute("aria-valuemax")).toBe("880");
    key(handle, "End");
    expect(a.style.width).toBe("880px");
    expect(onResize).toHaveBeenCalledWith(880);
    expect(onResizeEnd).toHaveBeenCalledWith(880);
  });

  it("column with the second pane sized: inverted handle, height style; disconnects its observer on dispose", () => {
    const { el, handle, dispose } = make({ direction: "column", sized: "second", min: 50, minOther: 100 });
    const [a, b] = el.querySelectorAll<HTMLElement>(".lc-split-pane");
    expect(b.style.height).toBe("300px");
    expect(a.id).toBe("");
    expect(handle.getAttribute("aria-controls")).toBe(b.id);
    expect(handle.className).toBe("splitter-h");
    Object.defineProperty(el, "clientHeight", { configurable: true, get: () => 600 });
    triggerResize(el);
    expect(handle.getAttribute("aria-valuemax")).toBe("500");
    key(handle, "ArrowUp");
    expect(b.style.height).toBe("316px");
    dispose();
    expect(FakeResizeObserver.instances.every((r) => r.disconnected)).toBe(true);
  });
});
