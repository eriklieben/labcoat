/**
 * Resizable split. WAI-ARIA APG "Window Splitter":
 *
 *   // the whole layout (owns its size; persist with onResizeEnd)
 *   splitPane({ direction: "row", label: "Resize test tree", initialSize: loadPref("treeWidth", 440), min: 280,
 *     onResizeEnd: (px) => savePref("treeWidth", px), first: treePane, second: detailPane })
 *
 *   // just the handle, for a layout you own (e.g. a CSS grid column driven by a signal)
 *   splitter({ orientation: "vertical", label: "Resize test tree", size: treeW, onResize: treeW.set, min: 280,
 *     max: () => main.clientWidth - 320, onResizeEnd: (px) => savePref("treeWidth", px) })
 *
 * Handle markup (same as the React app): `<div class="splitter-v|splitter-h [dragging]" role="separator"
 * aria-orientation aria-valuenow aria-valuemin aria-valuemax aria-label tabindex="0">`. `orientation: "vertical"`
 * is a vertical bar between side-by-side panes (col-resize).
 *
 * Keyboard (focus the handle): Arrow keys resize by `step` (Shift: ×5), Home / End jump to min / max, Enter collapses
 * to min and restores. Pointer: drag (pointer capture; `body.is-dragging` while dragging).
 */
import { h, onCleanup, read, signal, type Child, type ClassValue, type MaybeReactive } from "../core";
import { uid } from "./util";

export interface SplitterOptions {
  orientation: "vertical" | "horizontal";
  /** current size in px of the pane the splitter controls */
  size: MaybeReactive<number>;
  onResize: (px: number) => void;
  /** after a drag / key press: persist the size here */
  onResizeEnd?: (px: number) => void;
  min?: MaybeReactive<number>;
  max?: MaybeReactive<number | undefined>;
  /** the controlled pane is after the handle (right / below): moving toward it shrinks it */
  invert?: boolean;
  step?: number;
  label: string;
  /** id of the controlled pane (aria-controls) */
  controls?: string;
  class?: ClassValue;
}

/** The arrow keys that grow / shrink the controlled pane; `invert`: the pane is after the handle. */
function resizeKeys(vertical: boolean, invert: boolean): { grow: string; shrink: string } {
  const [back, forward] = vertical ? ["ArrowLeft", "ArrowRight"] : ["ArrowUp", "ArrowDown"];
  return invert ? { grow: back, shrink: forward } : { grow: forward, shrink: back };
}

export function splitter(opts: SplitterOptions): HTMLDivElement {
  const { orientation, onResize, invert = false, step = 16, label } = opts;
  const vertical = orientation === "vertical";
  const min = () => read(opts.min) ?? 0;
  const max = () => Math.max(min(), read(opts.max) ?? Number.MAX_SAFE_INTEGER);
  const clamp = (v: number) => Math.round(Math.max(min(), Math.min(max(), v)));
  const dragging = signal(false);
  let restore: number | undefined;
  const set = (v: number, end: boolean) => {
    const c = clamp(v);
    onResize(c);
    if (end) opts.onResizeEnd?.(c);
  };

  const onpointerdown = (e: PointerEvent) => {
    if (e.button !== 0) return;
    e.preventDefault();
    const el = e.currentTarget as HTMLDivElement;
    el.setPointerCapture(e.pointerId);
    el.focus({ preventScroll: true });
    const start = vertical ? e.clientX : e.clientY;
    const startSize = read(opts.size);
    let last = startSize;
    dragging.set(true);
    document.body.classList.add(vertical ? "is-dragging" : "is-dragging-row");
    const move = (ev: PointerEvent) => {
      const d = (vertical ? ev.clientX : ev.clientY) - start;
      last = clamp(startSize + (invert ? -d : d));
      onResize(last);
    };
    const up = () => {
      el.removeEventListener("pointermove", move);
      el.removeEventListener("pointerup", up);
      el.removeEventListener("pointercancel", up);
      dragging.set(false);
      document.body.classList.remove("is-dragging", "is-dragging-row");
      opts.onResizeEnd?.(last);
    };
    el.addEventListener("pointermove", move);
    el.addEventListener("pointerup", up);
    el.addEventListener("pointercancel", up);
  };

  // Enter collapses to the minimum, and a second Enter (still collapsed) restores the size from before.
  const toggleCollapsed = (cur: number) => {
    if (restore !== undefined && cur <= min()) {
      set(restore, true);
      restore = undefined;
    } else {
      restore = cur;
      set(min(), true);
    }
  };

  const { grow, shrink } = resizeKeys(vertical, invert);
  const onkeydown = (e: KeyboardEvent) => {
    const cur = read(opts.size);
    const s = e.shiftKey ? step * 5 : step;
    if (e.key === grow) set(cur + s, true);
    else if (e.key === shrink) set(cur - s, true);
    else if (e.key === "Home") set(min(), true);
    else if (e.key === "End") set(max(), true);
    else if (e.key === "Enter") toggleCollapsed(cur);
    else return;
    e.preventDefault();
  };

  return h("div", {
    class: () => [vertical ? "splitter-v" : "splitter-h", dragging() && "dragging", read(opts.class)],
    role: "separator",
    tabIndex: 0,
    "aria-orientation": orientation,
    "aria-label": label,
    "aria-controls": opts.controls,
    "aria-valuenow": () => Math.round(read(opts.size)),
    "aria-valuemin": () => Math.round(min()),
    "aria-valuemax": () => (read(opts.max) === undefined ? undefined : Math.round(max())),
    onpointerdown,
    onkeydown,
  });
}

export interface SplitPaneOptions {
  /** "row": panes side by side (vertical handle); "column": stacked (horizontal handle) */
  direction?: "row" | "column";
  first: Child;
  second: Child;
  /** which pane has the fixed size (default "first"); the other one flexes */
  sized?: "first" | "second";
  initialSize: number;
  min?: number;
  /** minimum size of the flexing pane (caps the max of the sized one; default 120) */
  minOther?: number;
  label: string;
  onResize?: (px: number) => void;
  onResizeEnd?: (px: number) => void;
  class?: ClassValue;
}

export function splitPane(opts: SplitPaneOptions): HTMLDivElement {
  const { direction = "row", sized = "first", min = 80, minOther = 120 } = opts;
  const size = signal(opts.initialSize);
  const avail = signal(0);
  const id = uid("pane");
  const sizedStyle = { flex: "none", [direction === "row" ? "width" : "height"]: () => `${size()}px` };
  const paneA = h("div", { class: "lc-split-pane", id: sized === "first" ? id : undefined, style: sized === "first" ? sizedStyle : { flex: "1 1 0" } }, opts.first);
  const paneB = h("div", { class: "lc-split-pane", id: sized === "second" ? id : undefined, style: sized === "second" ? sizedStyle : { flex: "1 1 0" } }, opts.second);
  const handle = splitter({
    orientation: direction === "row" ? "vertical" : "horizontal",
    size,
    min,
    max: () => (avail() ? avail() - minOther : undefined),
    invert: sized === "second",
    label: opts.label,
    controls: id,
    onResize: (px) => {
      size.set(px);
      opts.onResize?.(px);
    },
    onResizeEnd: opts.onResizeEnd,
  });
  const root = h("div", { class: ["lc-split", `lc-split-${direction}`, opts.class] }, paneA, handle, paneB);
  const ro = new ResizeObserver(() => avail.set(direction === "row" ? root.clientWidth : root.clientHeight));
  ro.observe(root);
  onCleanup(() => ro.disconnect());
  return root;
}
