/**
 * Tooltips. WAI-ARIA APG "Tooltip": shown on hover (after a delay) and on keyboard focus, hidden on Escape, blur,
 * pointer leave and press; while shown the target gets `aria-describedby` pointing at the `role="tooltip"` element.
 * The tooltip is hoverable (WCAG 1.4.13): moving the pointer onto it keeps it open.
 *
 *   tooltip(iconButton({ icon: "copy", label: "Copy" }), "Copy the error message", { placement: "top" })
 *   tooltip(el, () => `${count()} tests`)        // reactive text
 *
 * Returns the target. Call inside an owner (render(), an effect, a list row): listeners are removed with it. The
 * tooltip element is created on show and removed on hide, so idle targets cost three listeners and no DOM.
 * Don't also set `title` on the target (the browser would show its own tooltip).
 */
import { effect, h, onCleanup, read, root, type MaybeReactive } from "../core";
import { placePopup, type Placement } from "./popover";
import { uid } from "./util";

export interface TooltipOptions {
  placement?: Placement;
  /** hover delay in ms (default 500); keyboard focus shows immediately */
  delay?: number;
  /** "description" (default) wires aria-describedby; "none" when the text repeats the accessible name */
  aria?: "description" | "none";
}

export function tooltip<T extends HTMLElement>(target: T, text: MaybeReactive<string>, opts: TooltipOptions = {}): T {
  const { placement = "top", delay = 500, aria = "description" } = opts;
  let tip: HTMLDivElement | undefined;
  let disposeTip: (() => void) | undefined;
  let showTimer = 0;
  let hideTimer = 0;
  const clear = () => {
    clearTimeout(showTimer);
    clearTimeout(hideTimer);
  };

  const show = () => {
    clear();
    if (tip || !target.isConnected || !read(text)) return;
    const id = uid("tip");
    disposeTip = root((dispose) => {
      tip = h("div", { class: "lc-tooltip", role: "tooltip", id, onpointerenter: () => clearTimeout(hideTimer), onpointerleave: () => hideSoon() }, () => read(text));
      (target.closest("dialog, [role=dialog], main, nav, header, aside, footer, [role=region][aria-label]") ?? document.body).append(tip);
      placePopup(tip, target, placement, 6);
      effect(() => {
        read(text);
        if (tip) placePopup(tip, target, placement, 6);
      });
      return dispose;
    }, null);
    if (aria === "description") target.setAttribute("aria-describedby", [target.getAttribute("aria-describedby"), id].filter(Boolean).join(" "));
    document.addEventListener("keydown", onDocKey, true);
  };
  const hide = () => {
    clear();
    if (!tip) return;
    const id = tip.id;
    tip.remove();
    tip = undefined;
    disposeTip?.();
    disposeTip = undefined;
    const rest = (target.getAttribute("aria-describedby") ?? "").split(" ").filter((x) => x && x !== id);
    if (rest.length) target.setAttribute("aria-describedby", rest.join(" "));
    else target.removeAttribute("aria-describedby");
    document.removeEventListener("keydown", onDocKey, true);
  };
  const hideSoon = () => {
    clearTimeout(showTimer);
    clearTimeout(hideTimer);
    hideTimer = window.setTimeout(hide, 120);
  };
  const onDocKey = (e: KeyboardEvent) => {
    if (e.key === "Escape" && tip) {
      hide();
      e.stopPropagation();
    }
  };

  const onEnter = (e: PointerEvent) => {
    if (e.pointerType === "touch") return;
    clearTimeout(hideTimer);
    if (tip) return;
    clearTimeout(showTimer);
    showTimer = window.setTimeout(show, delay);
  };
  const onFocus = () => {
    if (target.matches(":focus-visible")) show();
  };
  target.addEventListener("pointerenter", onEnter);
  target.addEventListener("pointerleave", hideSoon);
  target.addEventListener("focus", onFocus);
  target.addEventListener("blur", hide);
  target.addEventListener("pointerdown", hide);
  onCleanup(() => {
    hide();
    target.removeEventListener("pointerenter", onEnter);
    target.removeEventListener("pointerleave", hideSoon);
    target.removeEventListener("focus", onFocus);
    target.removeEventListener("blur", hide);
    target.removeEventListener("pointerdown", hide);
  });
  return target;
}
