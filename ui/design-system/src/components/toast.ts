/**
 * Toasts (P19): confirmations that have no home in the UI ("Opened in Rider", "Link copied", "Quarantined · Undo").
 * Bottom center, at most 2 stacked (the oldest leaves), enter 200 ms decelerate from 8 px below, exit 150 ms
 * accelerate; auto-dismiss after 4 s, paused while hovered or focused. Announced politely (role=status).
 *
 *   toast("Opened in Rider")
 *   toast("Quarantined OrderTests.PlaceOrder", { action: { label: "Undo", onClick: undo } })
 *
 * Global (not tied to a component owner): timers clean up after themselves.
 */
import { h, type Child } from "../core";
import { icon, type IconName } from "./icon";
import { animateExit, dur } from "./motion";

const INTENT_ICON: Record<NonNullable<ToastOptions["intent"]>, IconName> = { info: "info", success: "checkCircle", error: "errorCircle" };

export interface ToastOptions {
  intent?: "info" | "success" | "error";
  icon?: IconName;
  action?: { label: string; onClick: () => void };
  /** ms before it closes (default 4000; 0 = until dismissed) */
  duration?: number;
}

let region: HTMLElement | undefined;
const MAX = 2;

function host(): HTMLElement {
  if (!region?.isConnected) {
    region = h("div", { class: "lc-toaster", role: "status", "aria-live": "polite" });
    document.body.append(region);
  }
  return region;
}

/** Show a toast; returns a function that closes it. */
export function toast(message: Child, opts: ToastOptions = {}): () => void {
  const { intent = "info", duration = 4000 } = opts;
  const r = host();
  let timer = 0;
  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    window.clearTimeout(timer);
    animateExit(el, undefined, { duration: dur(150), keyframes: [{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(8px)" }] });
  };
  const arm = () => {
    window.clearTimeout(timer);
    if (duration > 0) timer = window.setTimeout(close, duration);
  };
  const ic = opts.icon ?? INTENT_ICON[intent];
  const el = h(
    "div",
    {
      class: ["lc-toast", `lc-toast-${intent}`],
      onmouseenter: () => window.clearTimeout(timer),
      onmouseleave: arm,
    },
    icon(ic, { size: 16, class: "lc-toast-icon" }),
    h("span", { class: "lc-toast-msg" }, message),
    opts.action
      ? h(
          "button",
          {
            type: "button",
            class: "btn btn-small btn-link lc-toast-action",
            onclick: () => {
              opts.action!.onClick();
              close();
            },
          },
          opts.action.label,
        )
      : null,
    h("button", { type: "button", class: "icon-btn small lc-toast-close", "aria-label": "Dismiss", title: "Dismiss", onclick: close }, icon("dismiss", { size: 14 })),
  );
  el.addEventListener("focusin", () => window.clearTimeout(timer));
  el.addEventListener("focusout", arm);
  (el as HTMLElement & { _close?: () => void })._close = close;
  r.append(el);
  const live = [...r.querySelectorAll<HTMLElement & { _close?: () => void }>(".lc-toast:not([inert])")];
  for (const old of live.slice(0, Math.max(0, live.length - MAX))) old._close?.();
  arm();
  return close;
}
