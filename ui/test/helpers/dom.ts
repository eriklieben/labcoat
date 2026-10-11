/**
 * Deterministic browser API stubs for happy-dom + the controls tests use to drive them. Installed once by
 * test/setup.ts; state is reset after every test (resetDomStubs).
 */
import { vi } from "vitest";

// ---- WAAPI --------------------------------------------------------------------------------------------------------

export class FakeAnimation {
  playState: "running" | "finished" | "idle" | "paused" = "running";
  currentTime: number | null = 0;
  onfinish: ((e: Event) => void) | null = null;
  oncancel: ((e: Event) => void) | null = null;
  readonly finished: Promise<FakeAnimation>;
  readonly effect: { target: Element; getKeyframes: () => Keyframe[] };
  animationName?: string;
  private settle!: { resolve: (a: FakeAnimation) => void; reject: (e: unknown) => void };
  constructor(
    public target: Element,
    public keyframes: Keyframe[] | PropertyIndexedKeyframes | null,
    public options: number | KeyframeAnimationOptions | undefined,
  ) {
    this.finished = new Promise<FakeAnimation>((resolve, reject) => (this.settle = { resolve, reject }));
    this.finished.catch(() => {}); // a cancelled animation's rejection is expected
    this.effect = { target, getKeyframes: () => (Array.isArray(keyframes) ? keyframes : []) };
  }
  finish() {
    if (this.playState === "finished" || this.playState === "idle") return;
    this.playState = "finished";
    this.settle.resolve(this);
    this.onfinish?.(new Event("finish"));
  }
  cancel() {
    if (this.playState === "idle") return;
    this.playState = "idle";
    this.settle.reject(new DOMException("The animation was cancelled", "AbortError"));
    this.oncancel?.(new Event("cancel"));
  }
  play() {
    this.playState = "running";
  }
  pause() {
    this.playState = "paused";
  }
  reverse() {}
  commitStyles() {}
  persist() {}
  addEventListener() {}
  removeEventListener() {}
}

/** Every animation created with Element.animate since the last reset (also CSS ones registered with addCssAnimation). */
export const animations: FakeAnimation[] = [];

/** Finish all running animations and let their `finished` handlers run. */
export async function finishAnimations() {
  for (const a of [...animations]) a.finish();
  await flushMicrotasks();
}

/** Register a fake running CSS animation (getAnimations() returns it) on `el`. */
export function addCssAnimation(el: Element, animationName: string): FakeAnimation {
  const a = new FakeAnimation(el, null, undefined);
  a.animationName = animationName;
  animations.push(a);
  return a;
}

// ---- matchMedia ---------------------------------------------------------------------------------------------------

type MqlListener = (e: MediaQueryListEvent) => void;
const media = new Map<string, boolean>();
const mqlListeners = new Map<string, Set<MqlListener>>();

/** Set whether `query` matches; fires "change" on every MediaQueryList for it. */
export function setMedia(query: string, matches: boolean) {
  media.set(query, matches);
  for (const l of mqlListeners.get(query) ?? []) l({ matches, media: query } as MediaQueryListEvent);
}

function fakeMatchMedia(query: string): MediaQueryList {
  const listeners = mqlListeners.get(query) ?? new Set<MqlListener>();
  mqlListeners.set(query, listeners);
  return {
    media: query,
    get matches() {
      return media.get(query) ?? false;
    },
    onchange: null,
    addEventListener: (_t: string, l: MqlListener) => listeners.add(l),
    removeEventListener: (_t: string, l: MqlListener) => listeners.delete(l),
    addListener: (l: MqlListener) => listeners.add(l),
    removeListener: (l: MqlListener) => listeners.delete(l),
    dispatchEvent: () => true,
  } as unknown as MediaQueryList;
}

// ---- observers ----------------------------------------------------------------------------------------------------

export class FakeResizeObserver {
  static instances: FakeResizeObserver[] = [];
  targets = new Set<Element>();
  disconnected = false;
  constructor(public cb: ResizeObserverCallback) {
    FakeResizeObserver.instances.push(this);
  }
  observe(el: Element) {
    this.targets.add(el);
  }
  unobserve(el: Element) {
    this.targets.delete(el);
  }
  disconnect() {
    this.disconnected = true;
    this.targets.clear();
  }
}

/** Call every live ResizeObserver that observes `el` (or every one when `el` is omitted). */
export function triggerResize(el?: Element) {
  for (const ro of FakeResizeObserver.instances) {
    if (ro.disconnected) continue;
    const targets = el ? (ro.targets.has(el) ? [el] : []) : [...ro.targets];
    if (targets.length)
      ro.cb(
        targets.map((t) => ({ target: t, contentRect: t.getBoundingClientRect() }) as unknown as ResizeObserverEntry),
        ro as unknown as ResizeObserver,
      );
  }
}

export class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = [];
  targets = new Set<Element>();
  root = null;
  rootMargin = "";
  thresholds = [0];
  constructor(public cb: IntersectionObserverCallback) {
    FakeIntersectionObserver.instances.push(this);
  }
  observe(el: Element) {
    this.targets.add(el);
  }
  unobserve(el: Element) {
    this.targets.delete(el);
  }
  disconnect() {
    this.targets.clear();
  }
  takeRecords() {
    return [];
  }
}

export function triggerIntersection(el: Element, isIntersecting: boolean) {
  for (const io of FakeIntersectionObserver.instances)
    if (io.targets.has(el))
      io.cb([{ target: el, isIntersecting, intersectionRatio: isIntersecting ? 1 : 0 } as unknown as IntersectionObserverEntry], io as unknown as IntersectionObserver);
}

// ---- canvas -------------------------------------------------------------------------------------------------------

/** Calls made on fake 2D contexts since the last reset: [method, ...args]. */
export const canvasCalls: unknown[][] = [];

function fakeContext2d(canvas: HTMLCanvasElement): CanvasRenderingContext2D {
  const state: Record<string | symbol, unknown> = { canvas };
  return new Proxy(state, {
    get(t, k) {
      if (k in t) return t[k];
      return (...args: unknown[]) => {
        canvasCalls.push([k, ...args]);
      };
    },
    set(t, k, v) {
      t[k] = v;
      return true;
    },
  }) as unknown as CanvasRenderingContext2D;
}

// ---- WebSocket ----------------------------------------------------------------------------------------------------

export class FakeWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static instances: FakeWebSocket[] = [];
  /** set to make the next constructor call throw (bad URL etc.) */
  static throwOnCreate = false;
  readonly CONNECTING = 0;
  readonly OPEN = 1;
  readonly CLOSING = 2;
  readonly CLOSED = 3;
  readyState = 0;
  sent: string[] = [];
  onopen: ((e: Event) => void) | null = null;
  onmessage: ((e: MessageEvent) => void) | null = null;
  onclose: ((e: CloseEvent) => void) | null = null;
  onerror: ((e: Event) => void) | null = null;
  constructor(public url: string) {
    if (FakeWebSocket.throwOnCreate) {
      FakeWebSocket.throwOnCreate = false;
      throw new SyntaxError("bad url");
    }
    FakeWebSocket.instances.push(this);
  }
  static get last(): FakeWebSocket | undefined {
    return FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
  }
  send(data: string) {
    if (this.readyState !== 1) throw new Error("not open");
    this.sent.push(data);
  }
  close() {
    if (this.readyState === 3) return;
    this.readyState = 3;
    this.onclose?.({ code: 1000 } as CloseEvent);
  }
  // test controls
  open() {
    this.readyState = 1;
    this.onopen?.(new Event("open"));
  }
  message(data: unknown) {
    this.onmessage?.({ data: typeof data === "string" ? data : JSON.stringify(data) } as MessageEvent);
  }
  error() {
    this.onerror?.(new Event("error"));
  }
  /** the server dropped the connection */
  drop() {
    this.readyState = 3;
    this.onclose?.({ code: 1006 } as CloseEvent);
  }
}

// ---- clipboard ----------------------------------------------------------------------------------------------------

export const clipboard = { text: "" as string, fail: false };

// ---- misc ---------------------------------------------------------------------------------------------------------

/** Let pending promise callbacks run (several hops). */
export async function flushMicrotasks(n = 10) {
  for (let i = 0; i < n; i++) await Promise.resolve();
}

export function installDomStubs() {
  const proto = Element.prototype as unknown as Record<string, unknown>;
  proto.animate = function (this: Element, keyframes: Keyframe[] | null, options?: number | KeyframeAnimationOptions) {
    const a = new FakeAnimation(this, keyframes, options);
    animations.push(a);
    return a;
  };
  proto.getAnimations = function (this: Element, opts?: { subtree?: boolean }) {
    return animations.filter((a) => (a.playState === "running" || a.playState === "paused") && (a.target === this || (opts?.subtree && this.contains(a.target))));
  };
  (Document.prototype as unknown as Record<string, unknown>).getAnimations = () => animations.filter((a) => a.playState === "running");

  const mm = (q: string) => fakeMatchMedia(q);
  globalThis.matchMedia = mm;
  window.matchMedia = mm;

  (document as unknown as Record<string, unknown>).startViewTransition = (cb?: () => void) => {
    cb?.();
    const done = Promise.resolve();
    return { finished: done, ready: done, updateCallbackDone: done, skipTransition() {} };
  };

  for (const [name, cls] of [
    ["ResizeObserver", FakeResizeObserver],
    ["IntersectionObserver", FakeIntersectionObserver],
    ["WebSocket", FakeWebSocket],
  ] as const) {
    (globalThis as Record<string, unknown>)[name] = cls;
    (window as unknown as Record<string, unknown>)[name] = cls;
  }

  HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement, kind: string) {
    return kind === "2d" ? fakeContext2d(this) : null;
  } as HTMLCanvasElement["getContext"];

  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: {
      writeText: vi.fn(async (t: string) => {
        if (clipboard.fail) throw new Error("denied");
        clipboard.text = t;
      }),
      readText: async () => clipboard.text,
    },
  });
}

export function resetDomStubs() {
  for (const a of animations) a.cancel();
  animations.length = 0;
  media.clear();
  mqlListeners.clear();
  FakeResizeObserver.instances = [];
  FakeIntersectionObserver.instances = [];
  FakeWebSocket.instances = [];
  FakeWebSocket.throwOnCreate = false;
  canvasCalls.length = 0;
  clipboard.text = "";
  clipboard.fail = false;
  installDomStubs();
}
