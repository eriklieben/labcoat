/**
 * Collapsible section (P14): `grid-template-rows` 0fr ⇄ 1fr over 200 ms (emphasized), content fades in 40 ms later.
 * Instant when the content is taller than 600 px (a long reveal reads as lag) and with reduced motion. Closed content
 * is `visibility: hidden` (out of the tab order and the accessibility tree) once the collapse finished.
 *
 *   const open = signal(true);
 *   h("button", { "aria-expanded": open, "aria-controls": id, onclick: () => open.update((v) => !v) }, caret(), "Source")
 *   collapsible({ open, id, content: () => sourceView() })        // content is created on first open and kept
 *
 *   disclosure({ summary: "Raw message", content: () => pre(msg) }) // button + caret + collapsible in one
 *
 * Markup: `div.lc-collapsible[.open] > div.lc-collapsible-inner > content`; caret: `span.icon.lc-caret` (rotates 90°
 * when its button has aria-expanded="true").
 */
import { effect, h, read, signal, untracked, type Child, type ClassValue, type MaybeReactive } from "../core";
import { icon } from "./icon";
import { MOTION } from "./motion";
import { uid } from "./util";

export interface CollapsibleOptions {
  open: MaybeReactive<boolean>;
  content: () => Child;
  id?: string;
  class?: ClassValue;
  /** create the content only when first opened (default true); it is kept afterwards so it can animate closed */
  lazy?: boolean;
  /** above this content height (px) the toggle is instant (default 600) */
  maxAnimatedHeight?: number;
}

export function collapsible(opts: CollapsibleOptions): HTMLDivElement {
  const { lazy = true, maxAnimatedHeight = 600 } = opts;
  const mounted = signal(!lazy || !!untracked(() => read(opts.open)));
  const inner = h("div", { class: "lc-collapsible-inner" }, () => (mounted() ? untracked(opts.content) : null));
  const el = h("div", { class: ["lc-collapsible", opts.class], id: opts.id }, inner);
  let first = true;
  let timer = 0;
  effect(() => {
    const open = !!read(opts.open);
    untracked(() => {
      if (open && !mounted.peek()) mounted.set(true);
      if (first) {
        first = false;
        el.classList.toggle("open", open);
        return;
      }
      // measure once (content height does not depend on the row size) and pick instant vs animated
      const tall = inner.scrollHeight > maxAnimatedHeight;
      el.classList.toggle("lc-instant", tall);
      el.classList.toggle("lc-animating", !tall);
      el.classList.toggle("open", open);
      window.clearTimeout(timer);
      timer = window.setTimeout(() => el.classList.remove("lc-animating", "lc-instant"), MOTION.base + 60);
    });
  });
  return el;
}

/** The rotating disclosure caret (chevron right → down). Put it inside the button that carries aria-expanded. */
export const caret = (size = 14) => icon("chevronRight", { size, class: "lc-caret" });

export interface DisclosureOptions {
  summary: Child;
  content: () => Child;
  open?: MaybeReactive<boolean>;
  onToggle?: (open: boolean) => void;
  class?: ClassValue;
  buttonClass?: ClassValue;
}

/** `button[aria-expanded] (caret + summary)` + collapsible content. Uncontrolled unless `open` is a signal accessor. */
export function disclosure(opts: DisclosureOptions): HTMLDivElement {
  const local = signal(!!untracked(() => read(opts.open ?? false)));
  const open = typeof opts.open === "function" ? opts.open : local;
  const id = uid("disclosure");
  return h(
    "div",
    { class: ["lc-disclosure", opts.class] },
    h(
      "button",
      {
        type: "button",
        class: ["lc-disclosure-btn", opts.buttonClass],
        "aria-expanded": () => !!open(),
        "aria-controls": id,
        onclick: () => {
          const next = !untracked(open);
          local.set(next);
          opts.onToggle?.(next);
        },
      },
      caret(),
      opts.summary,
    ),
    collapsible({ open, id, content: opts.content }),
  );
}
