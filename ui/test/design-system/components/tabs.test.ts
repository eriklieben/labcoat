import { describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system/core";
import { rateTone, setMotionLevel, slideIndicator, tabIds, tabPanel, tabPill, tabs, type TabItem } from "@eriklieben/labcoat-design-system/components";
import { animations, flushMicrotasks } from "../../helpers/dom";

const key = (t: EventTarget, k: string) => {
  const e = new KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true });
  t.dispatchEvent(e);
  return e;
};

type Id = "details" | "coverage" | "logs";
function make(opts: { disabled?: () => boolean; orientation?: "horizontal" | "vertical"; ids?: ReturnType<typeof tabIds>; staticSel?: boolean } = {}) {
  const sel = signal<Id>("details");
  const onSelect = vi.fn((id: Id) => sel.set(id));
  const items: TabItem<Id>[] = [
    { id: "details", label: "Test details", icon: "taskList", title: "Details" },
    { id: "coverage", label: "Coverage", pill: tabPill("82%", "good"), disabled: opts.disabled },
    { id: "logs", label: "Logs" },
  ];
  let el!: HTMLDivElement;
  root(() => {
    el = tabs({ tabs: items, selected: opts.staticSel ? "details" : sel, onSelect, label: "Detail views", ids: opts.ids, orientation: opts.orientation, class: "x" });
  });
  document.body.append(el);
  return { el, sel, onSelect, btns: [...el.querySelectorAll<HTMLButtonElement>("[role=tab]")] };
}

describe("tabs()", () => {
  it("renders a labelled tablist with selected state and roving tabindex", () => {
    const { el, btns, sel } = make();
    expect(el.getAttribute("role")).toBe("tablist");
    expect(el.getAttribute("aria-label")).toBe("Detail views");
    expect(el.className).toBe("pane-tabs x");
    expect(el.hasAttribute("aria-orientation")).toBe(false);
    expect(btns.map((b) => b.getAttribute("aria-selected"))).toEqual(["true", "false", "false"]);
    expect(btns.map((b) => b.tabIndex)).toEqual([0, -1, -1]);
    expect(btns[0].title).toBe("Details");
    expect(btns[0].querySelector(".icon")).not.toBeNull();
    expect(btns[1].querySelector(".tab-pill.tone-good")!.textContent).toBe("82%");
    expect(btns[0].hasAttribute("aria-controls")).toBe(false);
    sel.set("logs");
    expect(btns.map((b) => b.getAttribute("aria-selected"))).toEqual(["false", "false", "true"]);
    expect(btns.map((b) => b.tabIndex)).toEqual([-1, -1, 0]);
  });

  it("click selects; arrows / Home / End move focus and select (automatic activation, wrapping)", () => {
    const { btns, onSelect } = make();
    btns[1].click();
    expect(onSelect).toHaveBeenLastCalledWith("coverage");
    expect(key(btns[1], "ArrowRight").defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(btns[2]);
    expect(onSelect).toHaveBeenLastCalledWith("logs");
    key(btns[2], "ArrowRight");
    expect(onSelect).toHaveBeenLastCalledWith("details");
    key(btns[0], "ArrowLeft");
    expect(onSelect).toHaveBeenLastCalledWith("logs");
    key(btns[2], "Home");
    expect(onSelect).toHaveBeenLastCalledWith("details");
    key(btns[0], "End");
    expect(onSelect).toHaveBeenLastCalledWith("logs");
    expect(key(btns[2], "ArrowDown").defaultPrevented).toBe(false);
  });

  it("skips disabled tabs; vertical orientation uses Up/Down", () => {
    const { el, btns, onSelect } = make({ disabled: () => true, orientation: "vertical" });
    expect(el.getAttribute("aria-orientation")).toBe("vertical");
    expect(el.classList.contains("pane-tabs-vertical")).toBe(true);
    expect(btns[1].disabled).toBe(true);
    key(btns[0], "ArrowDown");
    expect(onSelect).toHaveBeenLastCalledWith("logs");
    key(btns[2], "ArrowUp");
    expect(onSelect).toHaveBeenLastCalledWith("details");
  });

  it("with ids: tabs point at their panels (aria-controls) and tabPanel is labelled by its tab", () => {
    const ids = tabIds("detail");
    const { btns } = make({ ids });
    expect(btns[0].id).toBe("detail-tab-details");
    expect(btns[0].getAttribute("aria-controls")).toBe("detail-panel-details");
    const p = tabPanel(ids, "details", "content");
    expect(p.getAttribute("role")).toBe("tabpanel");
    expect(p.id).toBe("detail-panel-details");
    expect(p.getAttribute("aria-labelledby")).toBe("detail-tab-details");
    expect(p.tabIndex).toBe(0);
    expect(p.textContent).toBe("content");
    expect(tabIds().tab("a")).toMatch(/^lc-tabs-\d+-tab-a$/);
  });

  it("slides the indicator from the old tab to the new one on a reactive change", async () => {
    const { sel, btns } = make();
    sel.set("logs");
    await flushMicrotasks();
    const a = animations.find((x) => x.target === btns[2]);
    expect(a).toBeDefined();
    expect((a!.options as KeyframeAnimationOptions).pseudoElement).toBe("::after");
    sel.set("logs"); // same: nothing new
    await flushMicrotasks();
    expect(animations).toHaveLength(1);
  });

  it("a static selection never animates", async () => {
    const { btns } = make({ staticSel: true });
    btns[1].click();
    await flushMicrotasks();
    expect(animations).toHaveLength(0);
  });
});

describe("slideIndicator()", () => {
  it("animates translate/scale on the pseudo element; vertical uses translateY/scaleY", () => {
    const a = h("button");
    const b = h("button");
    document.body.append(a, b);
    a.getBoundingClientRect = () => ({ left: 0, top: 0, width: 100, height: 30 }) as DOMRect;
    b.getBoundingClientRect = () => ({ left: 120, top: 40, width: 60, height: 30 }) as DOMRect;
    slideIndicator(a, b);
    expect((animations[0].keyframes as Keyframe[])[0].transform).toBe(`translateX(-120px) scaleX(${80 / 40})`);
    slideIndicator(a, b, "vertical", "::before");
    expect((animations[1].keyframes as Keyframe[])[0].transform).toBe(`translateY(-40px) scaleY(${14 / 14})`);
    expect((animations[1].options as KeyframeAnimationOptions).pseudoElement).toBe("::before");
  });

  it("does nothing with reduced motion or detached tabs", () => {
    const a = h("button");
    const b = h("button");
    slideIndicator(a, b);
    document.body.append(a, b);
    setMotionLevel("reduced");
    slideIndicator(a, b);
    expect(animations).toHaveLength(0);
  });
});

describe("rateTone / tabPill", () => {
  it("maps rates to tones with default and custom thresholds", () => {
    expect([rateTone(0.9), rateTone(0.8), rateTone(0.6), rateTone(0.1)]).toEqual(["good", "good", "warn", "bad"]);
    expect(rateTone(0.7, { good: 0.7, warn: 0.2 })).toBe("good");
    expect(rateTone(0.3, { good: 0.7, warn: 0.2 })).toBe("warn");
  });

  it("tabPill: reactive text and tone; no tone → plain pill", () => {
    const t = signal("50%");
    const tone = signal<"good" | "bad" | undefined>("bad");
    const p = root(() => tabPill(t, tone));
    expect(p.className).toBe("tab-pill tone-bad");
    expect(p.textContent).toBe("50%");
    t.set("90%");
    tone.set(undefined);
    expect(p.textContent).toBe("90%");
    expect(p.className).toBe("tab-pill");
  });
});

describe("<lc-tabs>", () => {
  it("keeps its own selection and reports it", () => {
    const onSelect = vi.fn();
    const el = document.createElement("lc-tabs");
    el.tabs = [
      { id: "a", label: "A" },
      { id: "b", label: "B" },
    ];
    el.label = "Views";
    el.selected = "a";
    el.onSelect = onSelect;
    document.body.append(el);
    expect(el.style.display).toBe("contents");
    const btns = [...el.querySelectorAll<HTMLButtonElement>("[role=tab]")];
    expect(el.querySelector("[role=tablist]")!.getAttribute("aria-label")).toBe("Views");
    btns[1].click();
    expect(onSelect).toHaveBeenCalledWith("b");
    expect(el.selected).toBe("b");
    expect(btns[1].getAttribute("aria-selected")).toBe("true");
  });
});
