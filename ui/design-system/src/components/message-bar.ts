/**
 * Message bars (Fluent / Aspire MessageBar): info, success, warning, error (+ "snapshot" for the past-run banner).
 *
 *   messageBar({ intent: "error", title: "Build failed", body: "MyTests.csproj(12,5): error CS1002", actions: [
 *     messageBarAction("View details", openDetails)], onDismiss: () => hide() })
 *   messageBar({ intent: "warning", top: true, title: "Connection lost.", body: "Reconnecting…" })   // single-line shell bar
 *
 * Markup (same as the React app):
 *   <div class="messagebar mb-<intent> [top]" role="alert|status">
 *     <span class="icon mb-icon"/><div class="mb-body [inline]"><div class="mb-title"/><div class="mb-message plain"/></div>
 *     actions (button.btn.btn-small.mb-action) · dismiss (button.icon-btn.small.mb-dismiss)
 *
 * Enter / exit with a height reserve (P3): wrap the conditional bar in `reveal(when, () => messageBar(…))` (motion.ts)
 * instead of `show()`: the space grows 0fr → 1fr while the bar fades in, and shrinks before it is removed.
 *
 * `role`: errors are `alert` (announced immediately), the rest `status` (polite). Pass `role: null` for static
 * content that should not be announced when it is rendered.
 */
import { define, h, LabcoatElement, read, type Child, type ClassValue, type MaybeReactive } from "../core";
import { iconButton } from "./button";
import { icon, type IconName } from "./icon";
import { animateExit } from "./motion";

export type MessageIntent = "info" | "success" | "warning" | "error" | "snapshot";

const ICON: Record<MessageIntent, IconName> = { info: "info", success: "checkCircle", warning: "warning", error: "errorCircle", snapshot: "history" };

export interface MessageBarOptions {
  intent?: MessageIntent;
  title?: Child;
  body?: Child;
  /** one-line layout for the shell (title and body inline, ellipsis) */
  top?: boolean;
  /** body in the mono font (error messages); default false */
  mono?: boolean;
  /** custom leading icon, an element (e.g. a spinner) or false for none */
  icon?: IconName | Node | false;
  actions?: Child;
  onDismiss?: () => void;
  dismissLabel?: string;
  role?: "alert" | "status" | null;
  class?: MaybeReactive<ClassValue>;
}

/** Errors interrupt (alert); everything else is announced politely (status). */
const defaultRole = (intent: MessageIntent) => (intent === "error" ? "alert" : "status");

/** The leading icon: none (false), a given element, or the icon name (default: the intent's icon). */
function leadIcon(custom: MessageBarOptions["icon"], intent: MessageIntent): Node | null {
  if (custom === false) return null;
  if (custom instanceof Node) return custom;
  return icon(custom ?? ICON[intent], { size: 18, class: "mb-icon" });
}

/** The message: one ellipsized line in the top layout, else a block (in the plain font unless `mono`). */
function messageBody(body: Child, top: boolean, mono: boolean): Child {
  if (body == null) return null;
  if (top) return h("span", { class: "mb-ellipsis" }, body);
  return h("div", { class: ["mb-message", !mono && "plain"] }, body);
}

export function messageBar(opts: MessageBarOptions): HTMLDivElement {
  const { intent = "info", title, body, top = false, mono = false, actions, onDismiss, dismissLabel = "Dismiss" } = opts;
  const role = opts.role === undefined ? defaultRole(intent) : opts.role;
  const T = top ? "span" : "div";
  return h(
    "div",
    { class: () => ["messagebar", `mb-${intent}`, top && "top", read(opts.class)], role: role ?? undefined },
    leadIcon(opts.icon, intent),
    h(
      "div",
      { class: ["mb-body", top && "inline"] },
      title != null ? h(T, { class: "mb-title" }, title) : null,
      messageBody(body, top, mono),
    ),
    actions != null ? h("div", { class: "mb-actions" }, actions) : null,
    onDismiss ? iconButton({ icon: "dismiss", label: dismissLabel, size: "small", iconSize: 14, class: "mb-dismiss", onclick: onDismiss }) : null,
  );
}

/** `<button class="btn btn-small mb-action">` for message bar actions. */
export function messageBarAction(label: Child, onclick: (e: MouseEvent) => void, title?: string): HTMLButtonElement {
  return h("button", { type: "button", class: "btn btn-small mb-action", onclick, title }, label);
}

export interface MessageBarProps {
  intent: MessageIntent;
  heading: string;
  body: string;
  top: boolean;
  dismissible: boolean;
  onDismiss: () => void;
}
export interface LcMessageBar extends MessageBarProps {}
/**
 * `<lc-message-bar intent="warning" heading="…" body="…" dismissible>`: dismissing hides the element (and calls
 * `onDismiss`). Host is `display: contents`.
 */
export class LcMessageBar extends LabcoatElement<MessageBarProps> {
  static defaults: MessageBarProps = { intent: "info", heading: "", body: "", top: false, dismissible: false, onDismiss: () => {} };
  static observedAttributes = ["intent", "heading", "body", "top", "dismissible"];
  attributeChangedCallback(attr: string, _o: string | null, v: string | null) {
    if (attr === "intent") this.intent = (v ?? "info") as MessageIntent;
    else if (attr === "heading") this.heading = v ?? "";
    else if (attr === "body") this.body = v ?? "";
    else if (attr === "top") this.top = v != null;
    else if (attr === "dismissible") this.dismissible = v != null;
  }
  protected render() {
    this.style.display = "contents";
    const p = this.props;
    return () =>
      messageBar({
        intent: p.intent(),
        top: p.top(),
        title: p.heading() || null,
        body: p.body() || null,
        onDismiss: p.dismissible()
          ? () => {
              const bar = this.querySelector<HTMLElement>(".messagebar");
              const hide = () => {
                this.style.display = "none";
                if (bar) {
                  bar.getAnimations().forEach((a) => a.cancel());
                  bar.inert = false;
                  bar.style.pointerEvents = "";
                }
              };
              if (bar) animateExit(bar, hide, { duration: 160, keyframes: [{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(-6px)" }] });
              else hide();
              p.onDismiss.peek()();
            }
          : undefined,
      });
  }
}
define("lc-message-bar", LcMessageBar);

declare global {
  interface HTMLElementTagNameMap {
    "lc-message-bar": LcMessageBar;
  }
}
