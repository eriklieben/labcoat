/**
 * Motion helpers (see ui/MOTION.md). Everything here respects the motion policy:
 *
 *   motionLevel()   "full" | "reduced" | "off"  (html[data-motion] wins over prefers-reduced-motion)
 *   isBusy()        html.lc-busy (set by the shell during runs > 200 tests and while the tab is hidden)
 *   canAnimate(k)   "move" (full only) · "fade" (full + reduced) · "decorative" (full, not busy, tab visible)
 *
 *   animateEnter(el, kind?)       class-based one-shot entrance (.lc-enter / .lc-enter-fade / .lc-row-enter / .lc-enter-x),
 *                                 the class is removed on animationend so moves/re-inserts don't replay it
 *   animateExit(el, then?)        WAAPI fade (+ optional keyframes) then `then()` (default: el.remove()); safe when el is
 *                                 already detached, the animation is cancelled, or motion is off (runs `then` at once)
 *   flip(els, mutate)             FLIP for reorder/position changes: read rects → mutate() → read → animate the delta
 *   countUp(el, from, to)         number tween (tabular nums); final value at once when not allowed
 *   tween(from, to, onFrame)      the number tween behind countUp (drive a signal with it)
 *   flash(el, tone)               one-shot tint (.lc-flash-bad/good/accent), ≤ 10 concurrently
 *   locate(el)                    "you landed here" ring (.lc-locator)
 *   presence(when, render, opts)  like show(), but the leaving element animates out before it is removed
 *   reveal(when, render)          presence() inside a height-reserve wrapper (message bars, P3)
 *
 * JS durations mirror the CSS tokens (tokens.css); with reduced motion movement is dropped and durations are capped
 * at MOTION.fast.
 */
import { computed, getOwner, h, onCleanup, untracked, type Accessor, type Child } from "../core";

export const MOTION = { instant: 80, fast: 120, base: 200, slow: 320, xslow: 600, pop: 140, enter: 160, dialog: 180, exit: 100 } as const;
export const EASE = {
  standard: "cubic-bezier(0.33, 0, 0.67, 1)",
  decelerate: "cubic-bezier(0.1, 0.9, 0.2, 1)",
  accelerate: "cubic-bezier(0.7, 0, 1, 0.5)",
  emphasized: "cubic-bezier(0.22, 1, 0.36, 1)",
} as const;

export type MotionLevel = "full" | "reduced" | "off";
export type MotionKind = "move" | "fade" | "decorative";

const mq = typeof matchMedia === "function" ? matchMedia("(prefers-reduced-motion: reduce)") : undefined;
const rootEl = () => document.documentElement;

export function motionLevel(): MotionLevel {
  const a = rootEl().dataset.motion;
  if (a === "off" || a === "reduced" || a === "full") return a;
  return mq?.matches ? "reduced" : "full";
}
export const isBusy = () => rootEl().classList.contains("lc-busy");

/** Whether an animation of this kind may run right now. */
export function canAnimate(kind: MotionKind = "move"): boolean {
  const level = motionLevel();
  if (level === "off") return false;
  if (kind === "fade") return true;
  if (level === "reduced") return false;
  if (kind === "decorative") return !isBusy() && document.visibilityState !== "hidden";
  return true;
}

/** Duration for a JS animation: capped at `fast` with reduced motion. */
export const dur = (ms: number) => (motionLevel() === "reduced" ? Math.min(ms, MOTION.fast) : ms);

/** Set the motion override on <html>: "system" removes it (follow prefers-reduced-motion). */
export function setMotionLevel(level: MotionLevel | "system") {
  if (level === "system") delete rootEl().dataset.motion;
  else rootEl().dataset.motion = level;
}

// ---- enter / exit ---------------------------------------------------------------------------------------------------

export type EnterKind = "rise" | "fade" | "row" | "x";
const ENTER_CLASS: Record<EnterKind, string> = { rise: "lc-enter", fade: "lc-enter-fade", row: "lc-row-enter", x: "lc-enter-x" };

/**
 * One-shot entrance via a utility class (CSS does the reduced/off/busy gating). `index` staggers row entrances
 * (15 ms each, capped at 8 rows); `delay` (ms) for .lc-enter-x.
 */
export function animateEnter<E extends Element>(el: E, kind: EnterKind = "rise", opts: { index?: number; delay?: number } = {}): E {
  if (motionLevel() === "off" || !(el instanceof HTMLElement || el instanceof SVGElement)) return el;
  if ((kind === "row" || kind === "x") && isBusy()) return el;
  const cls = ENTER_CLASS[kind];
  if (opts.index != null) el.style.setProperty("--lc-i", String(opts.index));
  if (opts.delay != null) el.style.setProperty("--lc-delay", `${opts.delay}ms`);
  el.classList.add(cls);
  const done = (e?: Event) => {
    if (e && e.target !== el) return;
    el.classList.remove(cls);
    el.removeEventListener("animationend", done);
    el.removeEventListener("animationcancel", done);
  };
  el.addEventListener("animationend", done);
  el.addEventListener("animationcancel", done);
  return el;
}

export interface ExitOptions {
  /** keyframes (default: opacity → 0); movement keyframes are reduced to a fade with reduced motion */
  keyframes?: Keyframe[];
  duration?: number;
  easing?: string;
}

/**
 * Animate `el` out, then call `then` (default: remove it). The element is made inert while leaving. Runs `then`
 * immediately when motion is off, the element is detached, or WAAPI is missing; `then` runs exactly once.
 */
export function animateExit(el: Element, then?: () => void, opts: ExitOptions = {}): void {
  let fired = false;
  const finish = () => {
    if (fired) return;
    fired = true;
    if (then) then();
    else el.remove();
  };
  if (!el.isConnected || !canAnimate("fade") || typeof (el as HTMLElement).animate !== "function") return finish();
  const he = el as HTMLElement;
  he.inert = true;
  he.style.pointerEvents = "none";
  const moving = canAnimate("move");
  const duration = dur(opts.duration ?? MOTION.instant);
  const kf = opts.keyframes && moving ? opts.keyframes : [{ opacity: 1 }, { opacity: 0 }];
  const a = he.animate(kf, { duration, easing: opts.easing ?? EASE.accelerate, fill: "forwards" });
  a.finished.then(finish, finish);
  window.setTimeout(finish, duration + 120); // safety: a detached element's animation may never settle
}

// ---- FLIP ----------------------------------------------------------------------------------------------------------

export interface FlipOptions {
  duration?: number;
  easing?: string;
  /** skip elements that moved less than this (px) */
  threshold?: number;
  /** only animate elements whose |dy| ≤ this (px); far moves jump */
  maxDistance?: number;
}

/**
 * FLIP: measure `els` (First), run `mutate()` (the DOM/state change), measure again (Last), animate each element
 * from its old position to the new one with a transform (Invert + Play). One layout read before and one after.
 * Elements created by `mutate` are not animated here (use animateEnter for them). Returns the animations.
 */
export function flip(els: Iterable<Element>, mutate: () => void, opts: FlipOptions = {}): Animation[] {
  const list = [...els] as HTMLElement[];
  if (!canAnimate("move")) {
    mutate();
    return [];
  }
  const first = new Map(list.map((el) => [el, el.getBoundingClientRect()]));
  mutate();
  const out: Animation[] = [];
  const { threshold = 0.5, maxDistance = Infinity, duration = MOTION.base, easing = EASE.emphasized } = opts;
  const moves: [HTMLElement, number, number][] = [];
  for (const el of list) {
    if (!el.isConnected) continue;
    const a = first.get(el)!;
    const b = el.getBoundingClientRect();
    const dx = a.left - b.left;
    const dy = a.top - b.top;
    if (Math.abs(dx) < threshold && Math.abs(dy) < threshold) continue;
    if (Math.abs(dy) > maxDistance || Math.abs(dx) > maxDistance) continue;
    moves.push([el, dx, dy]);
  }
  for (const [el, dx, dy] of moves) out.push(el.animate([{ translate: `${dx}px ${dy}px` }, { translate: "0 0" }], { duration, easing, composite: "add" }));
  return out;
}

// ---- numbers -------------------------------------------------------------------------------------------------------

const counting = new WeakMap<object, () => void>();

/**
 * Tween a number from `from` to `to` (320 ms, ease-out-quint), calling `onFrame` each frame and `onDone` at the end.
 * Decorative: jumps to `to` when not allowed. A new tween with the same `key` cancels the previous one. Returns a
 * cancel function.
 */
export function tween(from: number, to: number, onFrame: (v: number) => void, opts: { duration?: number; key?: object; onDone?: () => void } = {}): () => void {
  if (opts.key) counting.get(opts.key)?.();
  let raf = 0;
  const cancel = () => {
    cancelAnimationFrame(raf);
    if (opts.key && counting.get(opts.key) === cancel) counting.delete(opts.key);
  };
  if (from === to || !canAnimate("decorative") || !Number.isFinite(from) || !Number.isFinite(to)) {
    onFrame(to);
    opts.onDone?.();
    return cancel;
  }
  const d = opts.duration ?? MOTION.slow;
  const t0 = performance.now();
  const ease = (x: number) => 1 - Math.pow(1 - x, 5);
  const step = (t: number) => {
    const k = Math.min(1, (t - t0) / d);
    onFrame(k >= 1 ? to : from + (to - from) * ease(k));
    if (k < 1) raf = requestAnimationFrame(step);
    else {
      cancel();
      opts.onDone?.();
    }
  };
  onFrame(from);
  raf = requestAnimationFrame(step);
  if (opts.key) counting.set(opts.key, cancel);
  return cancel;
}

/**
 * Count `el`'s text from `from` to `to` (320 ms, emphasized, tabular nums). Only for plain text elements (not for a
 * reactive h() region: tween a signal instead). Decorative: shows `to` at once with reduced motion, when busy, or
 * when the tab is hidden.
 */
export function countUp(el: HTMLElement, from: number, to: number, opts: { duration?: number; format?: (n: number) => string } = {}): void {
  const format = opts.format ?? ((n: number) => String(Math.round(n)));
  el.classList.add("lc-tabular");
  tween(from, to, (v) => (el.textContent = format(v)), { duration: opts.duration, key: el });
}

// ---- one-shot feedback ------------------------------------------------------------------------------------------------

let flashing = 0;
export type FlashTone = "bad" | "good" | "accent";

/** One-shot tint on `el` (900 ms). At most 10 run at the same time; restarts if already flashing. */
export function flash(el: HTMLElement, tone: FlashTone): void {
  if (!canAnimate("fade")) return;
  const cls = `lc-flash-${tone}`;
  for (const t of ["bad", "good", "accent"]) if (t !== tone) el.classList.remove(`lc-flash-${t}`);
  if (el.classList.contains(cls)) {
    for (const a of el.getAnimations({ subtree: true })) if ((a as CSSAnimation).animationName === "lc-flash") a.currentTime = 0;
    return;
  }
  if (flashing >= 10) return;
  flashing++;
  el.classList.add(cls);
  const done = () => {
    flashing--;
    el.classList.remove(cls);
  };
  window.setTimeout(done, 950);
}

/** "You landed here": a 600 ms inset accent ring (F8 / frame navigation). */
export function locate(el: HTMLElement): void {
  if (!canAnimate("fade")) return;
  el.classList.remove("lc-locator");
  for (const a of el.getAnimations({ subtree: true })) if ((a as CSSAnimation).animationName === "lc-flash") a.cancel();
  el.classList.add("lc-locator");
  window.setTimeout(() => el.classList.remove("lc-locator"), MOTION.xslow + 50);
}

/** Restart a one-shot CSS class animation on `el` (sheen, pop): removes + re-adds the class. */
export function replayClass(el: HTMLElement, cls: string, ms: number): void {
  if (!canAnimate(cls === "lc-sheen" ? "decorative" : "fade")) return;
  el.classList.remove(cls);
  for (const a of el.getAnimations({ subtree: true })) if (a.effect && (a as CSSAnimation).animationName) a.currentTime = 0;
  el.classList.add(cls);
  window.setTimeout(() => el.classList.remove(cls), ms + 50);
}

// ---- presence ---------------------------------------------------------------------------------------------------------

export interface PresenceOptions {
  /** animate the element in when it appears (not on first render unless `appear`) */
  enter?: (el: HTMLElement) => void;
  /** animate the element out; call `done` when it may be removed (default: an 80 ms fade) */
  exit?: (el: HTMLElement, done: () => void) => void;
  /** also run `enter` when `when()` is already true at creation (default false: no motion on first paint) */
  appear?: boolean;
}

/**
 * `show()` with an exit: when `when()` turns false (or the region is rebuilt) the rendered element is moved out of
 * the region (right before the presence anchor, so it stays in place visually), animated out and then removed.
 * `render` must return one Element for exit to work (anything else is removed instantly).
 */
export function presence<T>(when: () => T, render: (value: Accessor<NonNullable<T>>) => Child, opts: PresenceOptions = {}): Child {
  const on = computed(() => !!when());
  const anchor = document.createComment("presence");
  let first = true;
  const region = () => {
    const visible = on();
    const initial = first;
    first = false;
    if (!visible) return null;
    return untracked(() => {
      const node = render(() => when() as NonNullable<T>);
      if (node instanceof HTMLElement) {
        if (opts.enter && (!initial || opts.appear)) opts.enter(node);
        onCleanup(() => {
          const parent = anchor.parentNode;
          if (!parent || !node.isConnected || node.parentNode !== parent || !canAnimate("fade")) return;
          anchor.before(node); // out of the region: the region's clear no longer touches it
          const done = () => node.remove();
          if (opts.exit) opts.exit(node, done);
          else animateExit(node, done);
        });
      }
      return node;
    });
  };
  return [region, anchor];
}

/**
 * P3: a height-reserving wrapper (`div.lc-reveal > div.lc-reveal-inner > content`) that grows 0fr → 1fr (200 ms,
 * emphasized) while the content fades/rises in, and shrinks back (160 ms, accelerate) before it is removed. Use for
 * bars that push content down (message bars, banners). No motion when the bar is already there on first render.
 */
export function reveal<T>(when: () => T, render: (value: Accessor<NonNullable<T>>) => Child, opts: { appear?: boolean; class?: string } = {}): Child {
  return presence(when, (v) => h("div", { class: ["lc-reveal", opts.class] }, h("div", { class: "lc-reveal-inner" }, render(v))), {
    appear: opts.appear,
    enter: (el) => {
      if (!canAnimate("fade")) return;
      el.classList.add("lc-entering", "lc-animating");
      window.setTimeout(() => el.classList.remove("lc-entering", "lc-animating"), dur(MOTION.base) + 60);
    },
    exit: (el, done) => {
      if (!canAnimate("fade")) return done();
      el.classList.add("lc-animating");
      const inner = el.firstElementChild?.firstElementChild as HTMLElement | null;
      const d = dur(160);
      if (inner && canAnimate("move"))
        inner.animate([{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(-6px)" }], { duration: d, easing: EASE.accelerate, fill: "forwards" });
      animateExit(el, done, { keyframes: [{ gridTemplateRows: "1fr" }, { gridTemplateRows: "0fr" }], duration: d, easing: EASE.accelerate });
    },
  });
}

// ---- small timing helpers -----------------------------------------------------------------------------------------------

/** Runs `fn` after `ms` unless the current owner is disposed first (delayed spinners / "Connecting…"). */
export function after(ms: number, fn: () => void): void {
  const id = window.setTimeout(fn, ms);
  if (getOwner()) onCleanup(() => window.clearTimeout(id));
}

/** Throttle for "skip the animation when it happens again within `ms`" (keyboard scrubbing). */
export function scrubGuard(ms = 150): () => boolean {
  let last = -Infinity;
  return () => {
    const now = performance.now();
    const quick = now - last < ms;
    last = now;
    return !quick;
  };
}

/** Smooth-scroll `scroller` so `top` is reached; instant when far away (> `maxDistance` px) or without motion. */
export function scrollToTop(scroller: HTMLElement, top: number, maxDistance = scroller.clientHeight * 2): void {
  const dist = Math.abs(scroller.scrollTop - top);
  scroller.scrollTo({ top, behavior: dist <= maxDistance && canAnimate("move") ? "smooth" : "auto" });
}
