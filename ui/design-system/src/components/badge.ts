/**
 * Badges: `badge({ class: "trait" }, "Category", ": ", "Slow")` → `<span class="badge trait">…</span>`;
 * `statusBadge(status)` → `<span class="badge badge-status badge-<status>"><status icon/> Label</span>`.
 * `<lc-badge status="failed">` is the element form (the host is the badge box).
 */
import { define, effect, h, LabcoatElement, read, type Child, type ClassValue, type MaybeReactive, type Props } from "../core";
import { STATUS_LABEL, statusIcon, type Status } from "./status-icon";

export function badge(opts: Omit<Props<HTMLSpanElement>, "class"> & { class?: MaybeReactive<ClassValue> }, ...children: Child[]): HTMLSpanElement {
  const { class: extra, ...rest } = opts;
  const props = rest as Props<HTMLSpanElement>;
  props.class = typeof extra === "function" ? () => ["badge", read(extra)] : ["badge", extra];
  return h("span", props, ...children);
}

export function statusBadge(status: MaybeReactive<Status>): HTMLSpanElement {
  return h(
    "span",
    { class: () => `badge badge-status badge-${read(status)}` },
    statusIcon(status, 14),
    () => STATUS_LABEL[read(status)],
  );
}

export interface BadgeProps {
  status: Status | undefined;
}
export interface LcBadge extends BadgeProps {}
/** `<lc-badge>text</lc-badge>` or `<lc-badge status="passed">` (status icon + label). */
export class LcBadge extends LabcoatElement<BadgeProps> {
  static defaults: BadgeProps = { status: undefined };
  static hostClass = "badge";
  static observedAttributes = ["status"];
  attributeChangedCallback(_a: string, _o: string | null, v: string | null) {
    this.status = (v ?? undefined) as Status | undefined;
  }
  /** light-DOM children given by the author (captured once: re-renders after a reconnect reuse them) */
  #slotted?: Node[];
  protected render() {
    const slotted = (this.#slotted ??= [...this.childNodes]);
    effect(() => {
      const s = this.props.status();
      this.className = s ? `badge badge-status badge-${s}` : "badge";
    });
    return () => {
      const s = this.props.status();
      return s ? [statusIcon(s, 14), STATUS_LABEL[s]] : slotted;
    };
  }
}
define("lc-badge", LcBadge);

declare global {
  interface HTMLElementTagNameMap {
    "lc-badge": LcBadge;
  }
}
