import { afterEach, describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system/core";
import { placePopup, popover, popExit, type PopoverOptions } from "@eriklieben/labcoat-design-system/components";
import { animations, finishAnimations, triggerResize } from "../../helpers/dom";

const rect = (left: number, top: number, width: number, height: number) =>
  ({ left, top, width, height, right: left + width, bottom: top + height, x: left, y: top, toJSON() {} }) as DOMRect;

/** give an element a fixed layout size (happy-dom has no layout) */
function sized(el: HTMLElement, w: number, hgt: number) {
  Object.defineProperty(el, "offsetWidth", { configurable: true, get: () => w });
  Object.defineProperty(el, "offsetHeight", {
    configurable: true,
    get: () => (el.style.maxHeight ? Math.min(hgt, parseFloat(el.style.maxHeight)) : hgt),
  });
  return el;
}

function viewport(w: number, hgt: number) {
  Object.defineProperty(document.documentElement, "clientWidth", { configurable: true, get: () => w });
  Object.defineProperty(document.documentElement, "clientHeight", { configurable: true, get: () => hgt });
}

afterEach(() => {
  delete (document.documentElement as unknown as Record<string, unknown>).clientWidth;
  delete (document.documentElement as unknown as Record<string, unknown>).clientHeight;
});

describe("placePopup", () => {
  it("places below the anchor, start-aligned, with data-side/align and transform-origin", () => {
    viewport(1000, 800);
    const el = sized(document.createElement("div"), 200, 100);
    placePopup(el, rect(100, 100, 50, 20));
    expect([el.style.left, el.style.top, el.dataset.side, el.dataset.align, el.style.transformOrigin]).toEqual(["100px", "124px", "bottom", "start", "left top"]);
  });

  it("flips to the top when there is no room below and more above", () => {
    viewport(1000, 800);
    const el = sized(document.createElement("div"), 200, 300);
    placePopup(el, rect(100, 700, 50, 20), "bottom-end");
    expect(el.dataset.side).toBe("top");
    expect(el.style.top).toBe(`${700 - 4 - 300}px`);
    expect(el.style.left).toBe("8px"); // end-aligned (150 - 200) clamped to the 8 px margin
    expect(el.style.transformOrigin).toBe("right bottom");
  });

  it("flips top → bottom and caps the height to the available room", () => {
    viewport(1000, 800);
    const el = sized(document.createElement("div"), 100, 900);
    placePopup(el, rect(400, 50, 100, 20), "top");
    expect(el.dataset.side).toBe("bottom");
    expect(el.style.maxHeight).toBe(`${800 - 70 - 4 - 8}px`);
    expect(el.style.left).toBe("400px"); // centered: 400 + (100 - 100) / 2
    expect(el.style.transformOrigin).toBe("center top");
  });

  it("places right / left with flipping and vertical alignment", () => {
    viewport(1000, 800);
    const el = sized(document.createElement("div"), 200, 100);
    placePopup(el, rect(100, 300, 50, 40), "right-start");
    expect([el.dataset.side, el.style.left, el.style.top, el.style.transformOrigin]).toEqual(["right", "154px", "300px", "left top"]);
    placePopup(el, rect(900, 300, 50, 40), "right-end");
    expect([el.dataset.side, el.style.left, el.style.top, el.style.transformOrigin]).toEqual(["left", `${900 - 4 - 200}px`, `${340 - 100}px`, "right bottom"]);
    placePopup(el, rect(50, 300, 50, 40), "left-start");
    expect(el.dataset.side).toBe("right");
  });

  it("accepts a DOMRect or an element and centers on the cross axis by default", () => {
    viewport(1000, 800);
    const el = sized(document.createElement("div"), 100, 50);
    const anchor = document.createElement("button");
    anchor.getBoundingClientRect = () => rect(300, 300, 100, 30);
    placePopup(el, anchor, "left-start" as never);
    expect(el.dataset.side).toBe("left");
    // align "center" (no align part): top centred on the anchor
    placePopup(el, rect(300, 300, 100, 30), "bottom");
    expect(el.dataset.align).toBe("center");
    expect(el.style.left).toBe("300px");
    expect(el.style.transformOrigin).toBe("center top");
  });
});

describe("popover()", () => {
  function setup(extra: Partial<PopoverOptions> = {}) {
    const open = signal(false);
    const onClose = vi.fn((_r: string) => open.set(false));
    let btn!: HTMLButtonElement;
    let dispose!: () => void;
    const host = root((d) => {
      dispose = d;
      return h(
        "main",
        null,
        h("button", { ref: (b) => (btn = b) }, "Open"),
        popover({
          open,
          anchor: () => btn,
          onClose,
          label: "Runs",
          class: "run-menu",
          content: () => h("div", null, h("button", { class: "first" }, "A"), h("button", { class: "second" }, "B")),
          ...extra,
        }),
      );
    });
    document.body.append(host);
    return { open, onClose, btn: () => btn, host, dispose };
  }

  it("renders into the anchor's landmark, labelled, and focuses the first focusable", () => {
    const { open, host } = setup();
    expect(document.querySelector(".lc-popover")).toBeNull();
    open.set(true);
    const pop = host.querySelector<HTMLElement>(".lc-popover")!;
    expect(pop).not.toBeNull();
    expect(pop.parentElement).toBe(host);
    expect(pop.getAttribute("role")).toBe("dialog");
    expect(pop.getAttribute("aria-label")).toBe("Runs");
    expect(pop.classList.contains("run-menu")).toBe(true);
    expect(pop.style.position).toBe("fixed");
    expect(document.activeElement).toBe(pop.querySelector(".first"));
  });

  it("Escape closes with reason 'escape' and focus returns to the anchor", async () => {
    const { open, onClose, btn } = setup();
    open.set(true);
    const pop = document.querySelector<HTMLElement>(".lc-popover")!;
    const e = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    pop.querySelector("button")!.dispatchEvent(e);
    expect(e.defaultPrevented).toBe(true);
    expect(onClose).toHaveBeenCalledWith("escape");
    expect(document.activeElement).toBe(btn());
    // exit animation: removed after it finishes
    expect(pop.inert).toBe(true);
    await finishAnimations();
    expect(pop.isConnected).toBe(false);
  });

  it("other keys do not close", () => {
    const { open, onClose } = setup();
    open.set(true);
    document.querySelector(".lc-popover")!.dispatchEvent(new KeyboardEvent("keydown", { key: "a", bubbles: true }));
    expect(onClose).not.toHaveBeenCalled();
  });

  it("pointerdown outside closes ('outside'); inside or on the anchor does not", () => {
    const { open, onClose, btn } = setup();
    open.set(true);
    document.querySelector(".first")!.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    btn().dispatchEvent(new Event("pointerdown", { bubbles: true }));
    expect(onClose).not.toHaveBeenCalled();
    const out = document.createElement("div");
    document.body.append(out);
    out.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    expect(onClose).toHaveBeenCalledWith("outside");
  });

  it("focus leaving the popover closes with 'tab' (unless closeOnFocusOut: false)", () => {
    const outside = document.createElement("button");
    document.body.append(outside);
    const { open, onClose } = setup();
    open.set(true);
    const pop = document.querySelector(".lc-popover")!;
    pop.dispatchEvent(new FocusEvent("focusout", { relatedTarget: pop.querySelector(".second") }));
    expect(onClose).not.toHaveBeenCalled();
    pop.dispatchEvent(new FocusEvent("focusout", { relatedTarget: outside }));
    expect(onClose).toHaveBeenCalledWith("tab");
  });

  it("closeOnFocusOut: false keeps it open", () => {
    const outside = document.createElement("button");
    document.body.append(outside);
    const { open, onClose } = setup({ closeOnFocusOut: false });
    open.set(true);
    document.querySelector(".lc-popover")!.dispatchEvent(new FocusEvent("focusout", { relatedTarget: outside }));
    expect(onClose).not.toHaveBeenCalled();
  });

  it("initialFocus: container / none / function; role null; matchWidth; ref", () => {
    const ref = vi.fn();
    const a = setup({ initialFocus: "container", role: null, matchWidth: true, ref });
    Object.defineProperty(a.btn(), "offsetWidth", { get: () => 120 });
    a.btn().focus();
    a.open.set(true);
    const pop = document.querySelector<HTMLElement>(".lc-popover")!;
    expect(document.activeElement).toBe(pop);
    expect(pop.hasAttribute("role")).toBe(false);
    expect(pop.style.minWidth).toBe("120px");
    expect(ref).toHaveBeenCalledWith(pop);
    a.dispose();

    const b = setup({ initialFocus: "none" });
    b.btn().focus();
    b.open.set(true);
    expect(document.activeElement).toBe(b.btn());
    b.dispose();

    const c = setup({ initialFocus: (p) => p.querySelector<HTMLElement>(".second") });
    c.open.set(true);
    expect(document.activeElement?.className).toBe("second");
  });

  it("falls back to the popup itself when it has nothing focusable", () => {
    const open = signal(true);
    root(() => {
      document.body.append(h("div", null, popover({ open, anchor: () => null, onClose: () => {}, content: () => "text" })));
    });
    const pop = document.querySelector<HTMLElement>(".lc-popover")!;
    expect(pop.parentElement).toBe(document.body);
    expect(document.activeElement).toBe(pop);
  });

  it("re-places on window resize / scroll / ResizeObserver, and removes listeners on close", async () => {
    viewport(1000, 800);
    const { open, btn } = setup();
    btn().getBoundingClientRect = () => rect(10, 10, 50, 20);
    open.set(true);
    const pop = document.querySelector<HTMLElement>(".lc-popover")!;
    expect(pop.style.top).toBe("34px");
    btn().getBoundingClientRect = () => rect(10, 100, 50, 20);
    window.dispatchEvent(new Event("resize"));
    expect(pop.style.top).toBe("124px");
    btn().getBoundingClientRect = () => rect(10, 200, 50, 20);
    window.dispatchEvent(new Event("scroll"));
    expect(pop.style.top).toBe("224px");
    btn().getBoundingClientRect = () => rect(10, 300, 50, 20);
    triggerResize(pop);
    expect(pop.style.top).toBe("324px");
    open.set(false);
    await finishAnimations();
    btn().getBoundingClientRect = () => rect(10, 400, 50, 20);
    window.dispatchEvent(new Event("resize"));
    expect(pop.style.top).toBe("324px");
  });

  it("popExit removes after the 80 ms fade (or the safety timeout)", () => {
    vi.useFakeTimers();
    const el = document.createElement("div");
    document.body.append(el);
    const done = vi.fn();
    popExit(el, done);
    expect(animations).toHaveLength(1);
    expect(done).not.toHaveBeenCalled();
    vi.advanceTimersByTime(80 + 120);
    expect(done).toHaveBeenCalledTimes(1);
  });
});
