/**
 * Empty state: an illustration (icon tile or spinner), a title, a sentence, and actions.
 *
 *   emptyState({ icon: "shieldCheck", title: "No coverage report yet", body: "Run all tests with coverage…",
 *     actions: button({ variant: "accent", icon: "play", onclick: run }, "Run all with coverage") })
 *   emptyState({ spinner: true, title: "Connecting to the explorer host…", size: "small" })
 *
 * Markup (same as the React app): `<div class="empty-state [small]"><div class="empty-illustration">icon</div>
 * <div class="empty-title"/><div class="empty-sub"/><div class="empty-actions"/></div>`. A spinner replaces the
 * illustration tile (`<span class="spinner large">`).
 */
import { h, type Child, type ClassValue } from "../core";
import { icon, type IconName } from "./icon";

export interface EmptyStateOptions {
  title: Child;
  body?: Child;
  icon?: IconName;
  spinner?: boolean;
  actions?: Child;
  size?: "default" | "small";
  /** heading level for the title (default: a plain div, like the app); e.g. "h2" */
  as?: "div" | "h2" | "h3";
  /** role "status" for loading states so screen readers hear the title */
  role?: "status" | null;
  class?: ClassValue;
}

/** The illustration tile: a large spinner, the icon, or nothing. */
function illustration(opts: EmptyStateOptions, small: boolean): HTMLElement | null {
  if (opts.spinner) return h("span", { class: ["spinner", !small && "large"], "aria-hidden": "true" });
  if (opts.icon) return h("div", { class: "empty-illustration", "aria-hidden": "true" }, icon(opts.icon, { size: 32 }));
  return null;
}

export function emptyState(opts: EmptyStateOptions): HTMLDivElement {
  const { title, body, actions, size = "default", as = "div" } = opts;
  const small = size === "small";
  return h(
    "div",
    { class: ["empty-state", small && "small", opts.class], role: opts.role ?? (opts.spinner ? "status" : undefined) },
    illustration(opts, small),
    h(as, { class: "empty-title" }, title),
    body != null ? h("div", { class: "empty-sub" }, body) : null,
    actions != null ? h("div", { class: "empty-actions" }, actions) : null,
  );
}
