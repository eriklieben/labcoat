/**
 * Tabs (pane tabs). WAI-ARIA APG "Tabs" with automatic activation:
 *
 *   const ids = tabIds("detail");
 *   tabs({ ids, label: "Detail views", selected: view, onSelect: setView, tabs: [
 *     { id: "details", label: "Test details", icon: "taskList" },
 *     { id: "coverage", label: "Coverage", icon: "shieldCheck", pill: () => tabPill(pct(rate()), rateTone(rate())) },
 *   ] })
 *   tabPanel(ids, "details", …children)     // role=tabpanel, aria-labelledby the tab, tabindex=0
 *
 * Markup (same as the React app):
 *   <div class="pane-tabs" role="tablist"><button class="pane-tab" role="tab" aria-selected id aria-controls>icon Label pill</button>…</div>
 *
 * Keyboard: Tab enters at the selected tab (roving tabindex); Left/Right (or Up/Down for `orientation: "vertical"`),
 * Home, End move focus and select.
 */
import { define, effect, h, LabcoatElement, read, type Child, type MaybeReactive } from "../core";
import { canAnimate, EASE, MOTION } from "./motion";
import { icon, type IconName } from "./icon";
import { rovingIndex, uid } from "./util";

export interface TabItem<T extends string = string> {
  id: T;
  label: Child;
  icon?: IconName;
  /** trailing content, e.g. tabPill("82%", "good"); may be a function (reactive region) */
  pill?: Child;
  title?: MaybeReactive<string | undefined>;
  disabled?: MaybeReactive<boolean>;
}

/** Id prefix shared by a tablist and its panels (so tabs can point at panels with aria-controls). */
export interface TabIds {
  tab(id: string): string;
  panel(id: string): string;
}
export function tabIds(prefix = uid("tabs")): TabIds {
  return { tab: (id) => `${prefix}-tab-${id}`, panel: (id) => `${prefix}-panel-${id}` };
}

export interface TabsOptions<T extends string = string> {
  tabs: readonly TabItem<T>[];
  selected: MaybeReactive<T>;
  onSelect: (id: T) => void;
  /** accessible name of the tablist */
  label?: string;
  ids?: TabIds;
  /** set when panels are rendered with tabPanel(ids, …): adds aria-controls */
  controls?: boolean;
  orientation?: "horizontal" | "vertical";
  class?: string;
}

export function tabs<T extends string>(opts: TabsOptions<T>): HTMLDivElement {
  const { tabs: items, selected, onSelect, label, ids = tabIds(), controls = !!opts.ids, orientation = "horizontal" } = opts;
  const buttons: HTMLButtonElement[] = [];
  const onkeydown = (e: KeyboardEvent) => {
    const i = rovingIndex(e, items.length, buttons.indexOf(e.target as HTMLButtonElement), orientation, (j) => !!read(items[j].disabled));
    if (i === undefined) return;
    e.preventDefault();
    buttons[i].focus();
    onSelect(items[i].id);
  };
  // P5: the selection underline slides from the old tab to the new one (FLIP on the ::after indicator, 200 ms emphasized)
  if (typeof selected === "function") {
    let prev: T | undefined;
    effect(() => {
      const cur = read(selected);
      const from = prev;
      prev = cur;
      if (from === undefined || from === cur) return;
      const a = buttons[items.findIndex((t) => t.id === from)];
      const b = buttons[items.findIndex((t) => t.id === cur)];
      if (a && b) queueMicrotask(() => slideIndicator(a, b, orientation));
    });
  }
  return h(
    "div",
    { class: ["pane-tabs", orientation === "vertical" && "pane-tabs-vertical", opts.class], role: "tablist", "aria-label": label, "aria-orientation": orientation === "vertical" ? "vertical" : undefined, onkeydown },
    items.map((t, i) =>
      h(
        "button",
        {
          type: "button",
          class: "pane-tab",
          role: "tab",
          id: ids.tab(t.id),
          "aria-controls": controls ? ids.panel(t.id) : undefined,
          "aria-selected": () => read(selected) === t.id,
          tabIndex: () => (read(selected) === t.id ? 0 : -1),
          title: t.title,
          disabled: t.disabled,
          onclick: () => onSelect(t.id),
          ref: (b) => (buttons[i] = b),
        },
        t.icon ? icon(t.icon, { size: 16 }) : null,
        t.icon ? " " : null,
        t.label,
        t.pill ?? null,
      ),
    ),
  );
}

/**
 * Slide the selected-tab indicator (`::after`, or `::before`) from tab `a` to tab `b`: FLIP on the pseudo-element,
 * 200 ms emphasized. The indicator is assumed inset 10 px left/right (horizontal) or 8 px top/bottom (vertical).
 * Exported for tab-like controls (pane tabs built by hand, the nav rail).
 */
export function slideIndicator(a: HTMLElement, b: HTMLElement, orientation: "horizontal" | "vertical" = "horizontal", pseudoElement: "::after" | "::before" = "::after") {
  if (!canAnimate("move") || !a.isConnected || !b.isConnected || typeof b.animate !== "function") return;
  const ra = a.getBoundingClientRect();
  const rb = b.getBoundingClientRect();
  // the indicator is inset 10 px on both sides (horizontal) / 8 px (vertical)
  const transform =
    orientation === "vertical"
      ? `translateY(${ra.top - rb.top}px) scaleY(${Math.max(0.01, ra.height - 16) / Math.max(1, rb.height - 16)})`
      : `translateX(${ra.left - rb.left}px) scaleX(${Math.max(0.01, ra.width - 20) / Math.max(1, rb.width - 20)})`;
  b.animate([{ transform, transformOrigin: orientation === "vertical" ? "center top" : "left center" }, { transform: "none", transformOrigin: orientation === "vertical" ? "center top" : "left center" }], {
    duration: MOTION.base,
    easing: EASE.emphasized,
    pseudoElement,
  });
}

/** `<div role="tabpanel" id aria-labelledby tabindex="0">` for the tab `id` (hide inactive panels yourself, or render only the selected one). */
export function tabPanel(ids: TabIds, id: string, ...children: Child[]): HTMLDivElement {
  return h("div", { class: "pane-tabpanel", role: "tabpanel", id: ids.panel(id), "aria-labelledby": ids.tab(id), tabIndex: 0 }, ...children);
}

export type Tone = "good" | "warn" | "bad";

/** Coverage-style tone for a 0..1 rate: >= good → "good", >= warn → "warn", else "bad" (defaults 0.8 / 0.5). */
export function rateTone(rate: number, thresholds: { good?: number; warn?: number } = {}): Tone {
  const { good = 0.8, warn = 0.5 } = thresholds;
  if (rate >= good) return "good";
  return rate >= warn ? "warn" : "bad";
}

/** `<span class="tab-pill tone-x">` (a small tinted mono pill inside a tab). */
export function tabPill(text: MaybeReactive<string>, tone?: MaybeReactive<Tone | undefined>): HTMLSpanElement {
  return h("span", { class: () => ["tab-pill", read(tone) && `tone-${read(tone)}`] }, typeof text === "function" ? () => text() : text);
}

export interface TabsProps {
  tabs: readonly TabItem[];
  selected: string;
  label: string;
  onSelect: (id: string) => void;
}
export interface LcTabs extends TabsProps {}
/** `<lc-tabs>`: self-contained tablist (keeps the selection; `onSelect` reports it). Host is `display: contents`. */
export class LcTabs extends LabcoatElement<TabsProps> {
  static defaults: TabsProps = { tabs: [], selected: "", label: "", onSelect: () => {} };
  protected render() {
    this.style.display = "contents";
    const p = this.props;
    return () =>
      tabs({
        tabs: p.tabs(),
        label: p.label.peek(),
        selected: p.selected,
        onSelect: (id) => {
          p.selected.set(id);
          p.onSelect.peek()(id);
        },
      });
  }
}
define("lc-tabs", LcTabs);

declare global {
  interface HTMLElementTagNameMap {
    "lc-tabs": LcTabs;
  }
}
