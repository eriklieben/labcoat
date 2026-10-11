import { describe, expect, it } from "vitest";
import { h, root, signal } from "../../../design-system/src/core";
import { fillIn, formatPercent, meter, progressBar } from "../../../design-system/src/components/meter";
import { rateTone } from "../../../design-system/src/components/tabs";
import { animations, type FakeAnimation } from "../../helpers/dom";

describe("formatPercent / rateTone", () => {
  it("one decimal except 0 % and 100 %", () => {
    expect(formatPercent(0.824)).toBe("82.4%");
    expect(formatPercent(1)).toBe("100%");
    expect(formatPercent(0)).toBe("0%");
    expect(formatPercent(0.5)).toBe("50.0%");
  });

  it("tones from thresholds (good ≥ 0.8, warn ≥ 0.5)", () => {
    expect(rateTone(0.9)).toBe("good");
    expect(rateTone(0.8)).toBe("good");
    expect(rateTone(0.6)).toBe("warn");
    expect(rateTone(0.1)).toBe("bad");
    expect(rateTone(0.6, { good: 0.5 })).toBe("good");
    expect(rateTone(0.4, { warn: 0.3 })).toBe("warn");
  });
});

describe("meter()", () => {
  it("renders an ARIA meter with a scaleX fill and tone class", () => {
    const m = meter({ value: 0.42, label: "Line coverage", size: "sm", class: "x" });
    expect(m.getAttribute("role")).toBe("meter");
    expect(m.className).toBe("meter meter-sm tone-bad x");
    expect(m.getAttribute("aria-label")).toBe("Line coverage");
    expect(m.getAttribute("aria-valuemin")).toBe("0");
    expect(m.getAttribute("aria-valuemax")).toBe("100");
    expect(m.getAttribute("aria-valuenow")).toBe("42");
    expect(m.getAttribute("aria-valuetext")).toBe("42.0%");
    expect((m.firstElementChild as HTMLElement).style.transform).toBe("scaleX(0.42)");
  });

  it("clamps to 0..1 (NaN → 0), follows a signal, explicit tone wins", () => {
    root((dispose) => {
      const v = signal(1.5);
      const m = meter({ value: v, label: "x" });
      expect(m.getAttribute("aria-valuenow")).toBe("100");
      expect(m.className).toBe("meter meter-md tone-good");
      v.set(Number.NaN);
      expect(m.getAttribute("aria-valuenow")).toBe("0");
      v.set(-1);
      expect((m.firstElementChild as HTMLElement).style.transform).toBe("scaleX(0)");
      const accent = meter({ value: 0.2, label: "a", tone: "accent" });
      expect(accent.className).toBe("meter meter-md");
      const t = signal<"good" | "warn">("warn");
      const toned = meter({ value: 0.2, label: "a", tone: t });
      expect(toned.className).toBe("meter meter-md tone-warn");
      t.set("good");
      expect(toned.className).toBe("meter meter-md tone-good");
      dispose();
    });
  });

  it("showValue wraps the bar with a visible percentage (class on the wrapper)", () => {
    const w = meter({ value: 0.555, label: "x", showValue: true, class: "c", thresholds: { good: 0.5 } });
    expect(w.className).toBe("meter-wrap meter-wrap-md tone-good c");
    expect(w.querySelector(".meter")!.className).toBe("meter meter-md tone-good");
    expect(w.querySelector(".meter-value")!.textContent).toBe("55.5%");
    expect(w.querySelector(".meter-value")!.getAttribute("aria-hidden")).toBe("true");
    expect(meter({ value: 0.1, label: "x", showValue: true, tone: "accent" }).className).toBe("meter-wrap meter-wrap-md");
  });

  it("appear: the fill grows in once (stagger index)", () => {
    meter({ value: 0.5, label: "x", appear: true });
    meter({ value: 0.5, label: "x", appear: 3 });
    meter({ value: 0.5, label: "x", appear: false });
    expect(animations).toHaveLength(2);
    expect((animations[1].options as KeyframeAnimationOptions).delay).toBe(60);
  });
});

describe("fillIn", () => {
  it("animates the fills inside a root, at most 12 minus the start index", () => {
    const host = h("div", null, Array.from({ length: 14 }, () => h("span", { class: "meter-fill" })), h("div", { class: "lc-progress-fill" }));
    fillIn(host, 2);
    expect(animations).toHaveLength(10);
    const a = animations[0] as FakeAnimation;
    expect(a.keyframes).toEqual([{ transform: "scaleX(0)", offset: 0 }]);
    expect(a.options).toMatchObject({ duration: 320, delay: 40, fill: "backwards" });
  });

  it("accepts an element list; skipped without movement", () => {
    const fills = [h("span"), h("span")];
    fillIn(fills);
    expect(animations).toHaveLength(2);
    document.documentElement.dataset.motion = "reduced";
    fillIn(fills);
    expect(animations).toHaveLength(2);
  });
});

describe("progressBar()", () => {
  it("determinate: aria-valuenow 0..100 and a scaleX fill", () => {
    const p = progressBar({ value: 0.255, label: "Run progress", tone: "passed", size: "sm", class: "x" });
    expect(p.getAttribute("role")).toBe("progressbar");
    expect(p.className).toBe("lc-progress lc-progress-sm lc-progress-passed x");
    expect(p.getAttribute("aria-valuenow")).toBe("26");
    expect(p.getAttribute("aria-valuemin")).toBe("0");
    expect((p.firstElementChild as HTMLElement).style.transform).toBe("scaleX(0.255)");
  });

  it("indeterminate when the value is undefined (no value attributes)", () => {
    root((dispose) => {
      const v = signal<number | undefined>(undefined);
      const p = progressBar({ value: v, label: "x" });
      expect(p.className).toBe("lc-progress indeterminate lc-progress-accent");
      expect(p.hasAttribute("aria-valuenow")).toBe(false);
      expect(p.hasAttribute("aria-valuemax")).toBe(false);
      expect((p.firstElementChild as HTMLElement).style.transform).toBe("");
      v.set(2);
      expect(p.getAttribute("aria-valuenow")).toBe("100");
      expect(p.className).toBe("lc-progress lc-progress-accent");
      dispose();
    });
  });
});
