/**
 * Small shared helpers for primitives: unique ids, roving-focus keyboard handling, portals, reduced motion.
 */
import { onCleanup } from "../core";

let seq = 0;
/** A document-unique id (`lc-<prefix>-<n>`) for aria-controls / aria-labelledby / aria-describedby wiring. */
export const uid = (prefix = "id") => `lc-${prefix}-${++seq}`;

export type Orientation = "horizontal" | "vertical" | "both";

interface ArrowKeys {
  prev: readonly string[];
  next: readonly string[];
}

const ARROW_KEYS: Record<Orientation, ArrowKeys> = {
  horizontal: { prev: ["ArrowLeft"], next: ["ArrowRight"] },
  vertical: { prev: ["ArrowUp"], next: ["ArrowDown"] },
  both: { prev: ["ArrowLeft", "ArrowUp"], next: ["ArrowRight", "ArrowDown"] },
};

/** The arrow keys that move back / forward; anything other than horizontal or vertical moves both ways. */
function arrowKeys(orientation: Orientation): ArrowKeys {
  if (orientation === "horizontal" || orientation === "vertical") return ARROW_KEYS[orientation];
  return ARROW_KEYS.both;
}

/**
 * The index to move focus to for a roving-tabindex keydown (arrows, Home, End; wraps), or undefined when the key is
 * not a navigation key. Skips indices for which `disabled(i)` is true.
 */
export function rovingIndex(e: KeyboardEvent, count: number, current: number, orientation: Orientation = "horizontal", disabled?: (i: number) => boolean): number | undefined {
  const keys = arrowKeys(orientation);
  let dir: number;
  let start = current;
  if (keys.prev.includes(e.key)) dir = -1;
  else if (keys.next.includes(e.key)) dir = 1;
  else if (e.key === "Home") {
    dir = 1;
    start = -1;
  } else if (e.key === "End") {
    dir = -1;
    start = count;
  } else return undefined;
  if (count === 0) return undefined;
  let i = start;
  for (let n = 0; n < count; n++) {
    i = (i + dir + count) % count;
    if (!disabled?.(i)) return i;
  }
  return undefined;
}

const CONTAINERS =
  "dialog, [role=dialog], [role=alertdialog], main, nav, header, aside, footer, [role=main], [role=navigation], [role=banner], [role=complementary], [role=contentinfo], [role=region][aria-label], [role=region][aria-labelledby]";

/**
 * Append `el` for the lifetime of the current owner (an effect run, a `show` branch, an element) to the closest
 * dialog or landmark around `near` (else document.body). Popups are `position: fixed`, so `overflow: hidden` on
 * any ancestor never clips them; staying inside the landmark keeps them in the accessibility tree next to their
 * trigger, and inside a modal dialog keeps them out of the inert page.
 */
export function portal<T extends HTMLElement>(el: T, near?: Element | null, opts: { exit?: (el: T, done: () => void) => void } = {}): T {
  (near?.closest(CONTAINERS) ?? document.body).append(el);
  onCleanup(() => (opts.exit && el.isConnected ? opts.exit(el, () => el.remove()) : el.remove()));
  return el;
}

/** Listen on a target for the lifetime of the current owner. */
export function listen<K extends keyof WindowEventMap>(target: Window, type: K, fn: (e: WindowEventMap[K]) => void, opts?: AddEventListenerOptions | boolean): void;
export function listen<K extends keyof DocumentEventMap>(target: Document, type: K, fn: (e: DocumentEventMap[K]) => void, opts?: AddEventListenerOptions | boolean): void;
export function listen(target: EventTarget, type: string, fn: (e: Event) => void, opts?: AddEventListenerOptions | boolean) {
  target.addEventListener(type, fn, opts);
  onCleanup(() => target.removeEventListener(type, fn, opts));
}

/** True when movement should be avoided: the system setting or html[data-motion="reduced"|"off"] (see motion.ts). */
export const prefersReducedMotion = () => {
  const a = document.documentElement.dataset.motion;
  if (a === "reduced" || a === "off") return true;
  if (a === "full") return false;
  return typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
};

let live: HTMLElement | undefined;
/** Announce `text` politely to screen readers (one shared visually hidden live region). */
export function announce(text: string): void {
  if (!live?.isConnected) {
    live = document.createElement("div");
    live.className = "visually-hidden";
    live.setAttribute("role", "status");
    live.setAttribute("aria-live", "polite");
    document.body.append(live);
  }
  const el = live;
  el.textContent = "";
  // a fresh text node after a tick so repeated identical messages are announced again
  window.setTimeout(() => (el.textContent = text), 30);
}

/** Focusable descendants (visible, enabled, not tabindex=-1). */
export function focusables(root: HTMLElement): HTMLElement[] {
  const sel = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"]), [contenteditable="true"]';
  return [...root.querySelectorAll<HTMLElement>(sel)].filter((el) => el.offsetParent !== null || el.getClientRects().length > 0);
}
