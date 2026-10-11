/**
 * Status icons for test/run states. `statusIcon(status)` renders the icon for a fixed status;
 * `statusIcon(() => status())` follows a signal and pops (`.st-anim`) only when the status *changes* — never when a
 * row is first rendered (e.g. a virtualized row scrolling into view).
 */
import { computed, define, h, LabcoatElement, untracked, type Child, type MaybeReactive } from "../core";
import { icon } from "./icon";

export type Status =
  | "notRun"
  | "queued"
  | "running"
  | "passed"
  | "failed"
  | "skipped"
  | "timedOut"
  | "cancelled"
  | "building"
  | "discovering"
  | "error";

export const STATUS_LABEL: Record<Status, string> = {
  notRun: "Not run",
  queued: "Queued",
  running: "Running",
  passed: "Passed",
  failed: "Failed",
  skipped: "Skipped",
  timedOut: "Timed out",
  cancelled: "Cancelled",
  building: "Building",
  discovering: "Discovering",
  error: "Build error",
};

/** The static icon element for `status`. */
export function renderStatus(status: Status, size = 16): HTMLElement {
  const title = STATUS_LABEL[status];
  const i = (name: Parameters<typeof icon>[0], cls: string) => icon(name, { size, class: `st ${cls}`, title });
  switch (status) {
    case "passed":
      return i("checkCircle", "st-passed");
    case "failed":
      return i("dismissCircle", "st-failed");
    case "timedOut":
      return i("timer", "st-failed");
    case "error":
      return i("errorCircle", "st-failed");
    case "skipped":
      return i("subtractCircle", "st-skipped");
    case "cancelled":
      return i("subtractCircle", "st-muted");
    case "running":
    case "building":
    case "discovering":
      return h("span", { class: "st spinner-wrap", style: { width: size, height: size }, title }, h("span", { class: "spinner" }));
    case "queued":
      return h("span", { class: "st queued-dot-wrap", style: { width: size, height: size }, title }, h("span", { class: "queued-dot" }));
    default:
      return i("circle", "st-muted");
  }
}

export function statusIcon(status: MaybeReactive<Status>, size = 16): Child {
  if (typeof status !== "function") return renderStatus(status, size);
  const s = computed(status);
  let rendered = false;
  return () => {
    const v = s();
    const el = untracked(() => renderStatus(v, size));
    if (!rendered) {
      rendered = true;
      return el;
    }
    return h("span", { class: "st-anim" }, el);
  };
}

export interface StatusIconProps {
  status: Status;
  size: number;
}
export interface LcStatusIcon extends StatusIconProps {}
/** `<lc-status-icon status="passed">`: host is `display: contents`; pops on status changes. */
export class LcStatusIcon extends LabcoatElement<StatusIconProps> {
  static defaults: StatusIconProps = { status: "notRun", size: 16 };
  static observedAttributes = ["status", "size"];
  attributeChangedCallback(attr: string, _old: string | null, v: string | null) {
    if (attr === "status" && v) this.status = v as Status;
    if (attr === "size" && v) this.size = Number(v);
  }
  protected render() {
    this.style.display = "contents";
    return statusIcon(() => this.props.status(), this.props.size.peek());
  }
}
define("lc-status-icon", LcStatusIcon);

declare global {
  interface HTMLElementTagNameMap {
    "lc-status-icon": LcStatusIcon;
  }
}
