/**
 * Meter (a value in a known range, e.g. coverage %) and progress bar (task progress, determinate or not).
 *
 *   meter({ value: () => report().lineRate, label: "Line coverage" })          // tone from thresholds 0.8 / 0.5
 *   meter({ value: 0.42, size: "sm", label: "Branch coverage", showValue: true })
 *   progressBar({ value: () => done() / total(), label: "Run progress" })      // value undefined → indeterminate
 *
 * Meter markup (same as the React coverage pane): `<span class="meter meter-<size> tone-<tone>" role="meter"
 * aria-valuenow aria-valuemin="0" aria-valuemax="100"><span class="meter-fill" style="transform: scaleX(v)"/></span>`.
 * Progress: `<div class="lc-progress [indeterminate]" role="progressbar" aria-label aria-valuenow?><div class="lc-progress-fill"/></div>`.
 */
import { h, read, type MaybeReactive } from "../core";
import { canAnimate, EASE, MOTION } from "./motion";
import { rateTone, type Tone } from "./tabs";

/** "82.4%" (no decimals for 100 % / 0 %). */
export const formatPercent = (rate: number) => `${(rate * 100).toFixed(rate === 1 || rate === 0 ? 0 : 1)}%`;

const clamp01 = (v: number) => (Number.isFinite(v) ? Math.max(0, Math.min(1, v)) : 0);

export interface MeterOptions {
  /** 0..1 */
  value: MaybeReactive<number>;
  label: string;
  size?: "md" | "sm";
  /** tone thresholds (default good ≥ 0.8, warn ≥ 0.5); `tone` overrides */
  thresholds?: { good?: number; warn?: number };
  tone?: MaybeReactive<Tone | "accent">;
  /** render the percentage next to the bar (`.meter-wrap > .meter + .meter-value`) */
  showValue?: boolean;
  class?: string;
  /** P10: fill from 0 when first shown (320 ms emphasized); a number is the stagger index (20 ms each, ≤ 12) */
  appear?: boolean | number;
}

export function meter(opts: MeterOptions): HTMLSpanElement {
  const { value, label, size = "md", thresholds } = opts;
  const v = () => clamp01(read(value));
  const tone = () => (opts.tone ? read(opts.tone) : rateTone(v(), thresholds));
  const bar = h(
    "span",
    {
      class: () => ["meter", `meter-${size}`, tone() !== "accent" && `tone-${tone()}`, !opts.showValue && opts.class],
      role: "meter",
      "aria-label": label,
      "aria-valuemin": 0,
      "aria-valuemax": 100,
      "aria-valuenow": () => Math.round(v() * 1000) / 10,
      "aria-valuetext": () => formatPercent(v()),
    },
    h("span", { class: "meter-fill", style: { transform: () => `scaleX(${v()})` } }),
  );
  if (opts.appear !== undefined && opts.appear !== false) fillIn([bar.firstElementChild as HTMLElement], typeof opts.appear === "number" ? opts.appear : 0);
  if (!opts.showValue) return bar;
  return h(
    "span",
    { class: () => ["meter-wrap", `meter-wrap-${size}`, tone() !== "accent" && `tone-${tone()}`, opts.class] },
    bar,
    h("span", { class: "meter-value", "aria-hidden": "true" }, () => formatPercent(v())),
  );
}

/**
 * P10: grow meter fills (`.meter-fill`, `.lc-progress-fill`, or any element whose transform is a scaleX) from 0 to
 * their value, 320 ms emphasized, staggered 20 ms from `startIndex`, at most 12 animated (the rest show at once). For
 * the first display of a report only: not on sort, filter or re-render. Pass a root to animate the fills inside it.
 */
export function fillIn(fills: Iterable<Element> | ParentNode, startIndex = 0): void {
  if (!canAnimate("move")) return;
  const els = "querySelectorAll" in fills && !(Symbol.iterator in fills) ? [...(fills as ParentNode).querySelectorAll<HTMLElement>(".meter-fill, .lc-progress-fill")] : [...(fills as Iterable<HTMLElement>)];
  els.slice(0, Math.max(0, 12 - startIndex)).forEach((el, i) =>
    el.animate([{ transform: "scaleX(0)", offset: 0 }], { duration: MOTION.slow, easing: EASE.emphasized, delay: (startIndex + i) * 20, fill: "backwards" }),
  );
}

export interface ProgressOptions {
  /** 0..1, or undefined for indeterminate */
  value?: MaybeReactive<number | undefined>;
  label: string;
  tone?: MaybeReactive<"accent" | "passed" | "failed" | "running">;
  size?: "md" | "sm";
  class?: string;
}

export function progressBar(opts: ProgressOptions): HTMLDivElement {
  const v = () => read(opts.value);
  const indeterminate = () => v() === undefined;
  return h(
    "div",
    {
      class: () => ["lc-progress", opts.size === "sm" && "lc-progress-sm", indeterminate() && "indeterminate", `lc-progress-${read(opts.tone) ?? "accent"}`, opts.class],
      role: "progressbar",
      "aria-label": opts.label,
      "aria-valuemin": () => (indeterminate() ? undefined : 0),
      "aria-valuemax": () => (indeterminate() ? undefined : 100),
      "aria-valuenow": () => (indeterminate() ? undefined : Math.round(clamp01(v()!) * 100)),
    },
    h("div", { class: "lc-progress-fill", style: { transform: () => (indeterminate() ? "" : `scaleX(${clamp01(v()!)})`) } }),
  );
}
