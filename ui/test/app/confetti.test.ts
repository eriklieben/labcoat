import { describe, expect, it, vi } from "vitest";
import { celebrate, prefersReducedMotion } from "../../src/confetti";
import { canvasCalls, setMedia } from "../helpers/dom";

describe("celebrate (confetti)", () => {
  it("draws on a fixed, aria-hidden canvas and removes it after the burst", () => {
    vi.useFakeTimers();
    const ok = celebrate({ left: 0, top: 0, width: 800, height: 600 } as DOMRect);
    expect(ok).toBe(true);
    const canvas = document.querySelector<HTMLCanvasElement>("canvas.confetti-canvas")!;
    expect(canvas).not.toBeNull();
    expect(canvas.getAttribute("aria-hidden")).toBe("true");
    expect(canvas.style.position).toBe("fixed");
    expect(canvas.style.pointerEvents).toBe("none");
    // the palette probe is gone again
    expect(document.body.querySelectorAll("span")).toHaveLength(0);
    vi.advanceTimersToNextFrame();
    const names = new Set(canvasCalls.map((c) => c[0]));
    expect(names.has("scale")).toBe(true);
    expect(names.has("clearRect")).toBe(true);
    expect(names.has("fillRect") || names.has("arc")).toBe(true);
    for (let i = 0; i < 200 && canvas.isConnected; i++) vi.advanceTimersToNextFrame();
    expect(canvas.isConnected).toBe(false);
  });

  it("bursts from the viewport center without an origin; both shapes are drawn", () => {
    vi.useFakeTimers();
    let n = 0;
    vi.spyOn(Math, "random").mockImplementation(() => ((n = (n * 7 + 3) % 10), n / 10));
    expect(celebrate()).toBe(true);
    for (let i = 0; i < 5; i++) vi.advanceTimersToNextFrame();
    const names = new Set(canvasCalls.map((c) => c[0]));
    expect(names.has("arc")).toBe(true);
    expect(names.has("fillRect")).toBe(true);
  });

  it("does nothing with reduced motion (system setting or Settings → Motion)", () => {
    setMedia("(prefers-reduced-motion: reduce)", true);
    expect(prefersReducedMotion()).toBe(true);
    expect(celebrate()).toBe(false);
    setMedia("(prefers-reduced-motion: reduce)", false);
    document.documentElement.dataset.motion = "off";
    expect(celebrate()).toBe(false);
    expect(document.querySelector("canvas")).toBeNull();
  });

  it("gives up without a 2D context", () => {
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
    expect(celebrate()).toBe(false);
    expect(document.querySelector("canvas")).toBeNull();
  });

  it("falls back to built-in colors when the theme tokens resolve to nothing", () => {
    vi.useFakeTimers();
    vi.spyOn(window, "getComputedStyle").mockReturnValue({ color: "" } as CSSStyleDeclaration);
    const orig = HTMLCanvasElement.prototype.getContext;
    let ctx: CanvasRenderingContext2D | undefined;
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockImplementation(function (this: HTMLCanvasElement, kind: string) {
      return (ctx = orig.call(this, kind as "2d") as CanvasRenderingContext2D);
    } as never);
    expect(celebrate()).toBe(true);
    vi.advanceTimersToNextFrame();
    expect(["#512bd4", "#2da44e", "#e8890c"]).toContain(ctx!.fillStyle);
  });
});
