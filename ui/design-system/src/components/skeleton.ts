/**
 * Skeleton placeholder + delayed loading (P16). Nothing is shown for the first 300 ms (CSS animation delay, no timer),
 * so fast loads never flash; then grey blocks with a slow shimmer (static with reduced motion / when busy). Replace it
 * with the content and give the content `animateEnter(el, "fade")` (120 ms).
 *
 *   show(loading, () => skeleton({ lines: 4, label: "Loading coverage" }), () => animateEnter(content(), "fade"))
 *   delayed(spinnerEl, 400)   // any element: hidden for 400 ms, then fades in (spinners, "Connecting…")
 */
import { h } from "../core";

export interface SkeletonOptions {
  /** number of text lines (default 3) */
  lines?: number;
  /** a taller title line first */
  heading?: boolean;
  /** accessible label (role=status, aria-busy) */
  label?: string;
  class?: string;
}

const WIDTHS = ["92%", "78%", "85%", "64%", "88%", "72%"];

export function skeleton(opts: SkeletonOptions = {}): HTMLDivElement {
  const { lines = 3, heading = false, label = "Loading" } = opts;
  return h(
    "div",
    { class: ["lc-skeleton", opts.class], role: "status", "aria-busy": "true", "aria-label": label },
    heading ? h("span", { class: "lc-skeleton-line lg", style: { width: "45%" } }) : null,
    Array.from({ length: lines }, (_, i) => h("span", { class: "lc-skeleton-line", style: { width: WIDTHS[i % WIDTHS.length] } })),
  );
}

/** Hide `el` for `ms` (default 300), then fade it in (`.lc-delayed`). Returns `el`. */
export function delayed<E extends HTMLElement>(el: E, ms = 300): E {
  el.classList.add("lc-delayed");
  el.style.setProperty("--lc-delay", `${ms}ms`);
  return el;
}
