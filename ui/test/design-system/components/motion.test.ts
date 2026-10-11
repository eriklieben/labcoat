import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h, root, signal } from "../../../design-system/src/core";
import {
  after,
  animateEnter,
  animateExit,
  canAnimate,
  countUp,
  dur,
  EASE,
  flash,
  flip,
  isBusy,
  locate,
  MOTION,
  motionLevel,
  presence,
  replayClass,
  reveal,
  scrollToTop,
  scrubGuard,
  setMotionLevel,
  tween,
} from "../../../design-system/src/components/motion";
import { addCssAnimation, animations, FakeAnimation, finishAnimations, flushMicrotasks, setMedia } from "../../helpers/dom";

const REDUCED = "(prefers-reduced-motion: reduce)";
const html = () => document.documentElement;

function setVisibility(state: "visible" | "hidden") {
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => state });
}

afterEach(() => {
  setVisibility("visible");
});

describe("motion policy", () => {
  it("motionLevel follows prefers-reduced-motion unless html[data-motion] overrides it", () => {
    expect(motionLevel()).toBe("full");
    setMedia(REDUCED, true);
    expect(motionLevel()).toBe("reduced");
    setMotionLevel("full");
    expect(html().dataset.motion).toBe("full");
    expect(motionLevel()).toBe("full");
    setMotionLevel("off");
    expect(motionLevel()).toBe("off");
    setMotionLevel("system");
    expect(html().hasAttribute("data-motion")).toBe(false);
    expect(motionLevel()).toBe("reduced");
    html().dataset.motion = "bogus";
    expect(motionLevel()).toBe("reduced");
  });

  it("canAnimate: off blocks everything, reduced keeps fades only", () => {
    setMotionLevel("off");
    expect([canAnimate("move"), canAnimate("fade"), canAnimate("decorative")]).toEqual([false, false, false]);
    setMotionLevel("reduced");
    expect([canAnimate(), canAnimate("fade"), canAnimate("decorative")]).toEqual([false, true, false]);
    setMotionLevel("full");
    expect([canAnimate(), canAnimate("fade"), canAnimate("decorative")]).toEqual([true, true, true]);
  });

  it("decorative motion stops while busy (html.lc-busy) or when the tab is hidden", () => {
    expect(isBusy()).toBe(false);
    html().classList.add("lc-busy");
    expect(isBusy()).toBe(true);
    expect(canAnimate("decorative")).toBe(false);
    expect(canAnimate("move")).toBe(true);
    html().classList.remove("lc-busy");
    setVisibility("hidden");
    expect(canAnimate("decorative")).toBe(false);
  });

  it("dur caps durations at MOTION.fast with reduced motion", () => {
    expect(dur(MOTION.slow)).toBe(320);
    setMotionLevel("reduced");
    expect(dur(MOTION.slow)).toBe(MOTION.fast);
    expect(dur(50)).toBe(50);
  });

  it("exposes the token values", () => {
    expect(MOTION).toMatchObject({ instant: 80, fast: 120, base: 200, slow: 320, xslow: 600, exit: 100 });
    expect(EASE.decelerate).toContain("cubic-bezier");
  });
});

describe("animateEnter", () => {
  it("adds the kind's class and removes it on its own animationend only", () => {
    const el = h("div", null, h("span"));
    expect(animateEnter(el)).toBe(el);
    expect(el.classList.contains("lc-enter")).toBe(true);
    el.firstElementChild!.dispatchEvent(new Event("animationend", { bubbles: true }));
    expect(el.classList.contains("lc-enter")).toBe(true);
    el.dispatchEvent(new Event("animationend"));
    expect(el.classList.contains("lc-enter")).toBe(false);
  });

  it("sets the stagger index and delay; animationcancel also cleans up", () => {
    const el = h("div");
    animateEnter(el, "row", { index: 3 });
    expect(el.className).toBe("lc-row-enter");
    expect(el.style.getPropertyValue("--lc-i")).toBe("3");
    el.dispatchEvent(new Event("animationcancel"));
    expect(el.className).toBe("");
    const x = animateEnter(h("div"), "x", { delay: 120 });
    expect(x.className).toBe("lc-enter-x");
    expect(x.style.getPropertyValue("--lc-delay")).toBe("120ms");
    expect(animateEnter(h("div"), "fade").className).toBe("lc-enter-fade");
  });

  it("works on SVG elements and ignores other elements", () => {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    animateEnter(svg, "fade");
    expect(svg.classList.contains("lc-enter-fade")).toBe(true);
    const math = document.createElementNS("http://www.w3.org/1998/Math/MathML", "math");
    animateEnter(math);
    expect(math.classList).toHaveLength(0);
  });

  it("does nothing with motion off, and skips rows / x-steps while busy", () => {
    setMotionLevel("off");
    expect(animateEnter(h("div")).className).toBe("");
    setMotionLevel("system");
    html().classList.add("lc-busy");
    expect(animateEnter(h("div"), "row").className).toBe("");
    expect(animateEnter(h("div"), "x").className).toBe("");
    expect(animateEnter(h("div"), "rise").className).toBe("lc-enter");
  });
});

describe("animateExit", () => {
  it("removes a detached element at once", () => {
    const parent = h("div");
    const el = h("span");
    parent.append(el);
    animateExit(el);
    expect(el.parentNode).toBeNull();
    const then = vi.fn();
    animateExit(h("span"), then);
    expect(then).toHaveBeenCalledTimes(1);
  });

  it("fades a connected element out (inert while leaving), then calls `then` exactly once", async () => {
    vi.useFakeTimers();
    const el = h("div");
    document.body.append(el);
    const then = vi.fn();
    animateExit(el, then);
    expect(el.inert).toBe(true);
    expect(el.style.pointerEvents).toBe("none");
    const a = animations.at(-1)!;
    expect(a.target).toBe(el);
    expect(a.keyframes).toEqual([{ opacity: 1 }, { opacity: 0 }]);
    expect(a.options).toMatchObject({ duration: MOTION.instant, easing: EASE.accelerate, fill: "forwards" });
    expect(then).not.toHaveBeenCalled();
    a.finish();
    await flushMicrotasks();
    expect(then).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1000); // the safety timeout does not call it again
    expect(then).toHaveBeenCalledTimes(1);
    expect(el.isConnected).toBe(true); // `then` replaced the default removal
  });

  it("uses custom keyframes only when movement is allowed; the safety timeout finishes a stuck animation", async () => {
    vi.useFakeTimers();
    const kf = [{ transform: "none" }, { transform: "translateY(-6px)" }];
    const a1 = h("div");
    document.body.append(a1);
    animateExit(a1, undefined, { keyframes: kf, duration: 300, easing: "linear" });
    expect(animations.at(-1)!.keyframes).toBe(kf);
    expect(animations.at(-1)!.options).toMatchObject({ duration: 300, easing: "linear" });
    vi.advanceTimersByTime(300 + 120);
    expect(a1.isConnected).toBe(false);

    setMotionLevel("reduced");
    const a2 = h("div");
    document.body.append(a2);
    animateExit(a2, undefined, { keyframes: kf, duration: 300 });
    expect(animations.at(-1)!.keyframes).toEqual([{ opacity: 1 }, { opacity: 0 }]);
    expect(animations.at(-1)!.options).toMatchObject({ duration: MOTION.fast });
  });

  it("a cancelled animation still finishes; motion off or no WAAPI → immediate", async () => {
    const el = h("div");
    document.body.append(el);
    animateExit(el);
    animations.at(-1)!.cancel();
    await flushMicrotasks();
    expect(el.isConnected).toBe(false);

    setMotionLevel("off");
    const el2 = h("div");
    document.body.append(el2);
    animateExit(el2);
    expect(el2.isConnected).toBe(false);

    setMotionLevel("system");
    const el3 = h("div");
    document.body.append(el3);
    (el3 as unknown as { animate: unknown }).animate = undefined;
    animateExit(el3);
    expect(el3.isConnected).toBe(false);
  });
});

describe("flip", () => {
  const rect = (left: number, top: number) => ({ left, top, right: left + 10, bottom: top + 10, width: 10, height: 10, x: left, y: top, toJSON() {} }) as DOMRect;

  function positioned(before: [number, number], afterPos: [number, number]) {
    const el = h("div");
    document.body.append(el);
    let moved = false;
    el.getBoundingClientRect = () => (moved ? rect(...afterPos) : rect(...before));
    return { el, move: () => (moved = true) };
  }

  it("animates each moved element from its old position (translate, composite add)", () => {
    const a = positioned([0, 0], [0, 30]);
    const b = positioned([5, 5], [5.2, 5.1]); // below threshold
    const c = positioned([0, 0], [0, 500]); // beyond maxDistance
    const d = positioned([0, 0], [0, 10]); // removed by mutate
    const out = flip([a.el, b.el, c.el, d.el], () => {
      a.move();
      b.move();
      c.move();
      d.el.remove();
    }, { maxDistance: 100, duration: 180 });
    expect(out).toHaveLength(1);
    const anim = out[0] as unknown as FakeAnimation;
    expect(anim.target).toBe(a.el);
    expect(anim.keyframes).toEqual([{ translate: "0px -30px" }, { translate: "0 0" }]);
    expect(anim.options).toMatchObject({ duration: 180, easing: EASE.emphasized, composite: "add" });
  });

  it("only runs mutate when movement is not allowed", () => {
    setMotionLevel("reduced");
    const a = positioned([0, 0], [0, 30]);
    const mutate = vi.fn(a.move);
    expect(flip([a.el], mutate)).toEqual([]);
    expect(mutate).toHaveBeenCalledTimes(1);
  });
});

describe("tween / countUp", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  it("calls onFrame from `from` to `to` over the duration, then onDone", () => {
    const frames: number[] = [];
    const onDone = vi.fn();
    tween(0, 100, (v) => frames.push(v), { onDone, duration: 100 });
    expect(frames).toEqual([0]);
    vi.advanceTimersByTime(50);
    expect(frames.length).toBeGreaterThan(1);
    const mid = frames.at(-1)!;
    expect(mid).toBeGreaterThan(0);
    expect(mid).toBeLessThan(100);
    vi.advanceTimersByTime(200);
    expect(frames.at(-1)).toBe(100);
    expect(onDone).toHaveBeenCalledTimes(1);
  });

  it("jumps to the end when equal, not finite, or decorative motion is not allowed", () => {
    for (const [from, to] of [
      [5, 5],
      [NaN, 3],
      [0, Infinity],
    ] as const) {
      const frames: number[] = [];
      const onDone = vi.fn();
      tween(from, to, (v) => frames.push(v), { onDone });
      expect(frames).toEqual([to]);
      expect(onDone).toHaveBeenCalled();
    }
    html().classList.add("lc-busy");
    const frames: number[] = [];
    tween(0, 9, (v) => frames.push(v));
    expect(frames).toEqual([9]);
  });

  it("a new tween with the same key cancels the previous one; cancel() stops it", () => {
    const key = {};
    const first: number[] = [];
    const done1 = vi.fn();
    tween(0, 10, (v) => first.push(v), { key, onDone: done1 });
    const second: number[] = [];
    const cancel = tween(0, 20, (v) => second.push(v), { key });
    vi.advanceTimersByTime(1000);
    expect(first).toEqual([0]);
    expect(done1).not.toHaveBeenCalled();
    expect(second.at(-1)).toBe(20);
    cancel(); // already done: harmless

    const third: number[] = [];
    const stop = tween(0, 30, (v) => third.push(v));
    stop();
    vi.advanceTimersByTime(1000);
    expect(third).toEqual([0]);
  });

  it("countUp writes formatted numbers into the element (tabular nums)", () => {
    const el = h("span");
    countUp(el, 0, 42, { format: (n) => `${Math.round(n)} tests` });
    expect(el.classList.contains("lc-tabular")).toBe(true);
    expect(el.textContent).toBe("0 tests");
    vi.advanceTimersByTime(1000);
    expect(el.textContent).toBe("42 tests");
    countUp(el, 1, 7);
    vi.advanceTimersByTime(1000);
    expect(el.textContent).toBe("7");
  });

  it("countUp shows the final value at once with reduced motion", () => {
    setMotionLevel("reduced");
    const el = h("span");
    countUp(el, 0, 12);
    expect(el.textContent).toBe("12");
  });
});

describe("flash / locate / replayClass", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.advanceTimersByTime(5000); // drain the module's concurrent-flash counter
  });

  it("flash adds one tone class for 950 ms and replaces other tones", () => {
    const el = h("div", { class: "lc-flash-good" });
    flash(el, "bad");
    expect(el.classList.contains("lc-flash-bad")).toBe(true);
    expect(el.classList.contains("lc-flash-good")).toBe(false);
    vi.advanceTimersByTime(949);
    expect(el.classList.contains("lc-flash-bad")).toBe(true);
    vi.advanceTimersByTime(1);
    expect(el.classList.contains("lc-flash-bad")).toBe(false);
  });

  it("flash restarts a running flash instead of stacking", () => {
    const el = h("div");
    document.body.append(el);
    flash(el, "accent");
    const a = addCssAnimation(el, "lc-flash");
    const other = addCssAnimation(el, "lc-other");
    a.currentTime = 500;
    other.currentTime = 500;
    flash(el, "accent");
    expect(a.currentTime).toBe(0);
    expect(other.currentTime).toBe(500);
  });

  it("at most 10 flashes run at the same time", () => {
    const els = Array.from({ length: 11 }, () => h("div"));
    els.forEach((el) => flash(el, "good"));
    expect(els.filter((el) => el.classList.contains("lc-flash-good"))).toHaveLength(10);
    vi.advanceTimersByTime(1000);
    flash(els[10], "good");
    expect(els[10].classList.contains("lc-flash-good")).toBe(true);
  });

  it("flash and locate do nothing with motion off", () => {
    setMotionLevel("off");
    const el = h("div");
    flash(el, "bad");
    locate(el);
    expect(el.className).toBe("");
  });

  it("locate shows the ring for 650 ms and cancels a running flash", () => {
    const el = h("div", { class: "lc-locator" });
    document.body.append(el);
    const f = addCssAnimation(el, "lc-flash");
    locate(el);
    expect(f.playState).toBe("idle");
    expect(el.classList.contains("lc-locator")).toBe(true);
    vi.advanceTimersByTime(MOTION.xslow + 50);
    expect(el.classList.contains("lc-locator")).toBe(false);
  });

  it("replayClass restarts CSS animations; lc-sheen is decorative (skipped while busy)", () => {
    const el = h("div");
    document.body.append(el);
    const a = addCssAnimation(el, "lc-pop");
    a.currentTime = 99;
    replayClass(el, "pop", 200);
    expect(a.currentTime).toBe(0);
    expect(el.classList.contains("pop")).toBe(true);
    vi.advanceTimersByTime(250);
    expect(el.classList.contains("pop")).toBe(false);

    html().classList.add("lc-busy");
    replayClass(el, "lc-sheen", 600);
    expect(el.classList.contains("lc-sheen")).toBe(false);
    replayClass(el, "pop", 200);
    expect(el.classList.contains("pop")).toBe(true);
  });
});

describe("presence / reveal", () => {
  it("renders while truthy; enter runs on later appearances (not the first paint); exit animates the leaving element", async () => {
    const on = signal(true);
    const enter = vi.fn();
    const exits: HTMLElement[] = [];
    let finish!: () => void;
    root((dispose) => {
      const host = h(
        "div",
        null,
        presence(on, () => h("p", null, "hi"), {
          enter,
          exit: (el, done) => {
            exits.push(el);
            finish = done;
          },
        }),
      );
      document.body.append(host);
      expect(host.querySelector("p")?.textContent).toBe("hi");
      expect(enter).not.toHaveBeenCalled();
      on.set(false);
      // moved out of the region, still in the DOM until done()
      expect(exits).toHaveLength(1);
      expect(host.querySelector("p")).toBe(exits[0]);
      finish();
      expect(host.querySelector("p")).toBeNull();
      on.set(true);
      expect(enter).toHaveBeenCalledTimes(1);
      expect(enter.mock.calls[0][0]).toBe(host.querySelector("p"));
      dispose();
    });
    await flushMicrotasks();
  });

  it("appear: runs enter on the first render; default exit is an 80 ms fade", async () => {
    const on = signal<string | null>("x");
    const enter = vi.fn();
    await root(async (dispose) => {
      const host = h("div", null, presence(on, (v) => h("b", null, () => v()), { enter, appear: true }));
      document.body.append(host);
      expect(enter).toHaveBeenCalledTimes(1);
      expect(host.querySelector("b")?.textContent).toBe("x");
      on.set("y");
      expect(host.querySelector("b")?.textContent).toBe("y");
      on.set(null);
      expect(host.querySelector("b")).not.toBeNull();
      await finishAnimations();
      expect(host.querySelector("b")).toBeNull();
      dispose();
    });
  });

  it("removes instantly without fade motion, when detached, or for non-element content", () => {
    const on = signal(true);
    root((dispose) => {
      const host = h("div", null, presence(on, () => h("i")), presence(on, () => "text"));
      document.body.append(host);
      setMotionLevel("off");
      on.set(false);
      expect(host.querySelector("i")).toBeNull();
      expect(host.textContent).toBe("");
      setMotionLevel("system");
      on.set(true);
      host.remove();
      on.set(false);
      expect(host.querySelector("i")).toBeNull();
      dispose();
    });
  });

  it("reveal wraps content in a height reserve that animates in and out", async () => {
    vi.useFakeTimers();
    const on = signal(false);
    root((dispose) => {
      const host = h("div", null, reveal(on, () => h("span", { class: "bar" }, "bar"), { class: "extra" }));
      document.body.append(host);
      on.set(true);
      const wrap = host.querySelector(".lc-reveal") as HTMLElement;
      expect(wrap.classList.contains("extra")).toBe(true);
      expect(wrap.querySelector(".lc-reveal-inner > .bar")).not.toBeNull();
      expect(wrap.classList.contains("lc-entering")).toBe(true);
      vi.advanceTimersByTime(MOTION.base + 60);
      expect(wrap.classList.contains("lc-entering")).toBe(false);
      on.set(false);
      expect(wrap.classList.contains("lc-animating")).toBe(true);
      // inner content fades/rises, the wrapper collapses
      const targets = animations.map((a) => a.target);
      expect(targets).toContain(wrap.firstElementChild!.firstElementChild);
      expect(targets).toContain(wrap);
      expect(animations.find((a) => a.target === wrap)!.keyframes).toEqual([{ gridTemplateRows: "1fr" }, { gridTemplateRows: "0fr" }]);
      vi.advanceTimersByTime(400);
      expect(host.querySelector(".lc-reveal")).toBeNull();
      dispose();
    });
  });

  it("reveal with reduced motion: no movement of the inner content", () => {
    vi.useFakeTimers();
    setMotionLevel("reduced");
    const on = signal(true);
    root((dispose) => {
      const host = h("div", null, reveal(on, () => h("span", null, "bar"), { appear: true }));
      document.body.append(host);
      const wrap = host.querySelector(".lc-reveal") as HTMLElement;
      expect(wrap.classList.contains("lc-entering")).toBe(true);
      on.set(false);
      expect(animations.filter((a) => a.target !== wrap)).toHaveLength(0);
      vi.advanceTimersByTime(400);
      expect(host.querySelector(".lc-reveal")).toBeNull();
      dispose();
    });
  });

  it("reveal's own enter/exit hooks bail out when fades are not allowed", () => {
    // presence() itself skips exit with motion off; reveal's hooks also guard (enter with appear while off)
    setMotionLevel("off");
    const on = signal(true);
    root((dispose) => {
      const host = h("div", null, reveal(on, () => h("span"), { appear: true }));
      document.body.append(host);
      expect(host.querySelector(".lc-reveal")!.classList.contains("lc-entering")).toBe(false);
      on.set(false);
      expect(host.querySelector(".lc-reveal")).toBeNull();
      dispose();
    });
  });
});

describe("timing helpers", () => {
  it("after runs once after the delay, unless its owner is disposed first", () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    after(100, fn); // no owner: still runs
    vi.advanceTimersByTime(100);
    expect(fn).toHaveBeenCalledTimes(1);
    const fn2 = vi.fn();
    root((dispose) => {
      after(100, fn2);
      dispose();
    });
    vi.advanceTimersByTime(200);
    expect(fn2).not.toHaveBeenCalled();
  });

  it("scrubGuard reports quick repeats as scrubbing", () => {
    vi.useFakeTimers();
    const ok = scrubGuard(150);
    expect(ok()).toBe(true);
    vi.advanceTimersByTime(100);
    expect(ok()).toBe(false);
    vi.advanceTimersByTime(200);
    expect(ok()).toBe(true);
    expect(scrubGuard()()).toBe(true);
  });

  it("scrollToTop scrolls smoothly only for short distances with movement allowed", () => {
    const scroller = h("div");
    const scrollTo = vi.fn();
    scroller.scrollTo = scrollTo as unknown as HTMLElement["scrollTo"];
    Object.defineProperty(scroller, "clientHeight", { value: 100 });
    scrollToTop(scroller, 150);
    expect(scrollTo).toHaveBeenLastCalledWith({ top: 150, behavior: "smooth" });
    scrollToTop(scroller, 500);
    expect(scrollTo).toHaveBeenLastCalledWith({ top: 500, behavior: "auto" });
    scrollToTop(scroller, 500, 1000);
    expect(scrollTo).toHaveBeenLastCalledWith({ top: 500, behavior: "smooth" });
    setMotionLevel("reduced");
    scrollToTop(scroller, 10);
    expect(scrollTo).toHaveBeenLastCalledWith({ top: 10, behavior: "auto" });
  });
});
