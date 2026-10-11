/**
 * Anchored popups. `popover()` renders `content` into the anchor's nearest landmark / dialog, else document.body
 * (position: fixed), so `overflow: hidden` headers and panes never clip it, places it next to `anchor` (flipping/clamping to stay in the viewport, following
 * scroll and resize), closes on Escape / outside pointerdown, and returns focus to the anchor when it closes with
 * focus inside.
 *
 *   const open = signal(false);
 *   let btn!: HTMLButtonElement;
 *   h("button", { ref: (b) => (btn = b), "aria-expanded": open, onclick: () => open.update((v) => !v) }, "Runs",
 *     popover({ open, anchor: () => btn, onClose: () => open.set(false), label: "Runs", class: "run-menu",
 *       content: () => h("div", …) }))
 *
 * `popover()` returns a region (insert it anywhere, e.g. inside the anchor button: only comment markers stay there).
 * `placePopup(el, anchor, placement)` is the positioning function on its own.
 */
import { h, onCleanup, read, show, type Child, type ClassValue, type MaybeReactive } from "../core";
import { animateExit, MOTION } from "./motion";
import { focusables, listen, portal } from "./util";

export type Placement = "bottom-start" | "bottom-end" | "bottom" | "top-start" | "top-end" | "top" | "right-start" | "right-end" | "left-start" | "left-end";

export type CloseReason = "escape" | "outside" | "tab" | "select";

const MARGIN = 8;

/**
 * Position a `position: fixed` element next to `anchor`. Flips to the opposite side when the preferred side has no
 * room, clamps into the viewport, and caps the height to the available space. Sets `data-side` / `data-align` and the transform-origin (the anchor side) for the entrance animation.
 */
export function placePopup(el: HTMLElement, anchor: Element | DOMRect, placement: Placement = "bottom-start", offset = 4) {
  const a = anchor instanceof Element ? anchor.getBoundingClientRect() : anchor;
  const vw = document.documentElement.clientWidth;
  const vh = document.documentElement.clientHeight;
  el.style.maxHeight = "";
  const [side, align = "center"] = placement.split("-") as [string, string?];
  const fit: Fit = { a, side, align, offset, w: el.offsetWidth, hgt: el.offsetHeight };
  const { top, left, hgt, w } = side === "bottom" || side === "top" ? placeVertically(el, fit, vh) : placeHorizontally(fit, vw);
  el.style.left = `${Math.round(Math.max(MARGIN, Math.min(left, vw - w - MARGIN)))}px`;
  el.style.top = `${Math.round(Math.max(MARGIN, Math.min(top, vh - hgt - MARGIN)))}px`;
  el.dataset.side = fit.side;
  el.dataset.align = align;
  // scale from the anchor: the popup grows out of the control that opened it (P4)
  el.style.transformOrigin = transformOrigin(fit.side, align);
}

/** The anchor box, the requested side (flipped in place) and alignment, and the popup size. */
interface Fit {
  a: DOMRect;
  side: string;
  align: string;
  offset: number;
  w: number;
  hgt: number;
}

/** Above or below the anchor: flip when the other side has more room, cap the height to the room left. */
function placeVertically(el: HTMLElement, fit: Fit, vh: number) {
  const { a, align, offset, w } = fit;
  const below = vh - a.bottom - offset - MARGIN;
  const above = a.top - offset - MARGIN;
  if (fit.side === "bottom" && fit.hgt > below && above > below) fit.side = "top";
  else if (fit.side === "top" && fit.hgt > above && below > above) fit.side = "bottom";
  const room = fit.side === "bottom" ? below : above;
  if (fit.hgt > room) {
    el.style.maxHeight = `${Math.max(80, room)}px`;
    fit.hgt = el.offsetHeight;
  }
  const top = fit.side === "bottom" ? a.bottom + offset : a.top - offset - fit.hgt;
  return { top, left: alignTo(align, a.left, a.right, a.width, w), w, hgt: fit.hgt };
}

/** Left or right of the anchor: flip when the other side has more room. */
function placeHorizontally(fit: Fit, vw: number) {
  const { a, align, offset, w, hgt } = fit;
  const right = vw - a.right - offset - MARGIN;
  const leftRoom = a.left - offset - MARGIN;
  if (fit.side === "right" && w > right && leftRoom > right) fit.side = "left";
  else if (fit.side === "left" && w > leftRoom && right > leftRoom) fit.side = "right";
  const left = fit.side === "right" ? a.right + offset : a.left - offset - w;
  return { top: alignTo(align, a.top, a.bottom, a.height, hgt), left, w, hgt };
}

/** Position along the anchor's edge: flush with its start, flush with its end, or centered. */
function alignTo(align: string, start: number, end: number, anchorSize: number, size: number): number {
  if (align === "start") return start;
  if (align === "end") return end - size;
  return start + (anchorSize - size) / 2;
}

const OPPOSITE: Record<string, string> = { top: "bottom", bottom: "top", left: "right", right: "left" };
const ALIGN_X: Record<string, string> = { start: "left", end: "right" };
const ALIGN_Y: Record<string, string> = { start: "top", end: "bottom" };

/** The anchor side of the popup, and along that side its aligned edge (or the center). */
function transformOrigin(side: string, align: string): string {
  if (side === "top" || side === "bottom") return `${ALIGN_X[align] ?? "center"} ${OPPOSITE[side]}`;
  return `${OPPOSITE[side]} ${ALIGN_Y[align] ?? "center"}`;
}

/** Exit for popups: 80 ms fade (accelerate), then removal. */
export const popExit = (el: HTMLElement, done: () => void) => animateExit(el, done, { duration: MOTION.instant });

export interface PopoverOptions {
  open: MaybeReactive<boolean>;
  anchor: () => HTMLElement | null | undefined;
  onClose: (reason: CloseReason) => void;
  content: () => Child;
  placement?: Placement;
  offset?: number;
  /** role of the popup element (default "dialog"; menus use "menu" on an inner list) */
  role?: string | null;
  /** accessible name of the popup */
  label?: string;
  class?: ClassValue;
  /** focus on open: the first focusable element (default), the popup itself, nothing, or a custom element */
  initialFocus?: "first" | "container" | "none" | ((popup: HTMLElement) => HTMLElement | null | undefined);
  /** at least as wide as the anchor */
  matchWidth?: boolean;
  /** close when focus leaves the popup (Tab out); default true */
  closeOnFocusOut?: boolean;
  /** called with the popup element once it is in the DOM */
  ref?: (popup: HTMLElement) => void;
}

export function popover(opts: PopoverOptions): Child {
  const { placement = "bottom-start", offset = 4, role = "dialog", initialFocus = "first", closeOnFocusOut = true } = opts;
  return show(
    () => read(opts.open),
    () => {
      const anchor = opts.anchor();
      const el = h(
        "div",
        {
          class: ["lc-popover", opts.class],
          role: role ?? undefined,
          "aria-label": opts.label,
          tabIndex: -1,
          style: { position: "fixed", top: 0, left: 0 },
          onkeydown: (e: KeyboardEvent) => {
            if (e.key === "Escape") {
              e.preventDefault();
              e.stopPropagation();
              opts.onClose("escape");
            }
          },
        },
        opts.content(),
      );
      el.addEventListener("focusout", (e) => {
        const to = e.relatedTarget as Node | null;
        if (closeOnFocusOut && to && !el.contains(to) && !anchor?.contains(to)) opts.onClose("tab");
      });
      portal(el, anchor, { exit: popExit });
      const place = () => anchor?.isConnected && placePopup(el, anchor, placement, offset);
      if (opts.matchWidth && anchor) el.style.minWidth = `${anchor.offsetWidth}px`;
      place();
      listen(window, "resize", place);
      listen(window, "scroll", place, true);
      const ro = new ResizeObserver(place);
      ro.observe(el);
      onCleanup(() => ro.disconnect());
      listen(
        document,
        "pointerdown",
        (e) => {
          const t = e.target as Node;
          if (!el.contains(t) && !anchor?.contains(t)) opts.onClose("outside");
        },
        true,
      );
      // Registered after portal(): runs before the element is removed, while focus is still inside.
      onCleanup(() => {
        if (el.contains(document.activeElement) && anchor?.isConnected) anchor.focus();
      });
      opts.ref?.(el);
      focusTarget(el, initialFocus)?.focus({ preventScroll: true });
      return null;
    },
  );
}

/** The element to focus on open (see `PopoverOptions.initialFocus`). */
function focusTarget(el: HTMLElement, initialFocus: NonNullable<PopoverOptions["initialFocus"]>): HTMLElement | null | undefined {
  if (initialFocus === "none") return null;
  if (initialFocus === "container") return el;
  if (typeof initialFocus === "function") return initialFocus(el);
  return focusables(el)[0] ?? el;
}
