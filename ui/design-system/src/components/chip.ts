/**
 * Filter chips (radio-like toggles with a status dot and a count):
 *
 *   h("div", { class: "chips", role: "radiogroup" },
 *     chip({ id: "failed", label: "Failed", count: () => counts().failed, active: () => status() === "failed", onclick }))
 *
 * Markup: `<button role="radio" aria-checked class="chip chip-<id> [chip-active]"><span class="chip-dot"/>Label
 * <span class="chip-count">n</span></button>`. `id: "all"` (or `dot: false`) renders no dot.
 */
import { define, h, LabcoatElement, type MaybeReactive, type Props } from "../core";

export interface ChipOptions extends Omit<Props<HTMLButtonElement>, "class" | "id"> {
  /** status id: drives the dot color (`chip-<id>`) */
  id: string;
  label: string;
  count?: MaybeReactive<number | string>;
  active: MaybeReactive<boolean>;
  dot?: boolean;
}

export function chip(opts: ChipOptions): HTMLButtonElement {
  const { id, label, count, active, dot = id !== "all", ...rest } = opts;
  const props = rest as Props<HTMLButtonElement>;
  const on = () => (typeof active === "function" ? active() : active);
  props.role = "radio";
  props["aria-checked"] = on;
  props.class = () => ["chip", `chip-${id}`, on() && "chip-active"];
  return h(
    "button",
    props,
    dot ? h("span", { class: "chip-dot" }) : null,
    label,
    count !== undefined ? h("span", { class: "chip-count" }, count) : null,
  );
}

export interface ChipProps {
  chipId: string;
  label: string;
  count: number | string | undefined;
  active: boolean;
}
export interface LcChip extends ChipProps {}
/** `<lc-chip chip-id="failed" label="Failed">` (host `display: contents`); listen for `click`. */
export class LcChip extends LabcoatElement<ChipProps> {
  static defaults: ChipProps = { chipId: "all", label: "", count: undefined, active: false };
  static observedAttributes = ["chip-id", "label", "count"];
  attributeChangedCallback(attr: string, _o: string | null, v: string | null) {
    if (attr === "chip-id") this.chipId = v ?? "all";
    if (attr === "label") this.label = v ?? "";
    if (attr === "count") this.count = v ?? undefined;
  }
  protected render() {
    this.style.display = "contents";
    const p = this.props;
    return () => chip({ id: p.chipId(), label: p.label(), count: () => p.count() ?? "", active: () => p.active() });
  }
}
define("lc-chip", LcChip);

declare global {
  interface HTMLElementTagNameMap {
    "lc-chip": LcChip;
  }
}
