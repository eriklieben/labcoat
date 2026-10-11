/**
 * Segmented control: a single-choice group of buttons (Settings theme/mode). WAI-ARIA APG "Radio Group":
 *
 *   segmented({ label: "Mode", value: themePref, onChange: setThemePref,
 *     options: [{ value: "system", label: "System", icon: "darkTheme" }, { value: "light", label: "Light", icon: "sun" }] })
 *
 * Markup (same as the React Settings page):
 *   <div class="segmented" role="radiogroup" aria-label><button role="radio" aria-checked class="segment [on]">icon Label</button>…</div>
 *
 * Keyboard: Tab enters the group at the checked option (roving tabindex); Arrow keys / Home / End move and select.
 */
import { define, h, LabcoatElement, read, type Child, type MaybeReactive } from "../core";
import { icon, type IconName } from "./icon";
import { rovingIndex } from "./util";

export interface SegmentOption<T extends string = string> {
  value: T;
  label: Child;
  icon?: IconName;
  /** tooltip; also the accessible name for icon-only options (`label: ""`) */
  title?: string;
  disabled?: boolean;
}

export interface SegmentedOptions<T extends string = string> {
  options: readonly SegmentOption<T>[];
  value: MaybeReactive<T>;
  onChange: (value: T) => void;
  /** accessible name of the group */
  label: string;
  size?: "default" | "small";
  disabled?: MaybeReactive<boolean>;
  class?: string;
}

export function segmented<T extends string>(opts: SegmentedOptions<T>): HTMLDivElement {
  const { options, value, onChange, label, size = "default", disabled } = opts;
  const buttons: HTMLButtonElement[] = [];
  const selectedIndex = () => Math.max(0, options.findIndex((o) => o.value === read(value)));
  const onkeydown = (e: KeyboardEvent) => {
    const i = rovingIndex(e, options.length, buttons.indexOf(e.target as HTMLButtonElement), "both", (j) => !!options[j].disabled);
    if (i === undefined) return;
    e.preventDefault();
    buttons[i].focus();
    onChange(options[i].value);
  };
  return h(
    "div",
    {
      class: ["segmented", size === "small" && "segmented-small", opts.class],
      role: "radiogroup",
      "aria-label": label,
      "aria-disabled": disabled === undefined ? undefined : () => !!read(disabled),
      onkeydown,
    },
    options.map((o, i) =>
      h(
        "button",
        {
          type: "button",
          role: "radio",
          class: () => ["segment", read(value) === o.value && "on"],
          "aria-checked": () => read(value) === o.value,
          tabIndex: () => (i === selectedIndex() ? 0 : -1),
          title: o.title,
          "aria-label": o.label === "" ? o.title : undefined,
          disabled: () => !!o.disabled || !!read(disabled),
          onclick: () => onChange(o.value),
          ref: (b) => (buttons[i] = b),
        },
        o.icon ? icon(o.icon, { size: 16 }) : null,
        o.icon && o.label !== "" ? " " : null,
        o.label,
      ),
    ),
  );
}

export interface SegmentedProps {
  options: readonly SegmentOption[];
  value: string;
  label: string;
  onChange: (value: string) => void;
}
export interface LcSegmented extends SegmentedProps {}
/** `<lc-segmented>`: self-contained (keeps the selected value; `onChange` reports it). Host is `display: contents`. */
export class LcSegmented extends LabcoatElement<SegmentedProps> {
  static defaults: SegmentedProps = { options: [], value: "", label: "", onChange: () => {} };
  protected render() {
    this.style.display = "contents";
    const p = this.props;
    return () =>
      segmented({
        options: p.options(),
        label: p.label.peek(),
        value: p.value,
        onChange: (v) => {
          p.value.set(v);
          p.onChange.peek()(v);
        },
      });
  }
}
define("lc-segmented", LcSegmented);

declare global {
  interface HTMLElementTagNameMap {
    "lc-segmented": LcSegmented;
  }
}
