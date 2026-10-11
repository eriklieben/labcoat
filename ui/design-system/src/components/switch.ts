/**
 * Switch (on/off toggle, the Settings toggles). WAI-ARIA APG "Switch" on a native checkbox:
 *
 *   switchControl({ checked: () => settings().confetti, onChange: (v) => setSetting("confetti", v), label: "Confetti" })
 *
 * Markup (same as the React Settings page):
 *   <label class="switch"><input type="checkbox" role="switch" aria-label>
 *     <span class="switch-track" aria-hidden="true"><span class="switch-thumb"></span></span>
 *     <span class="switch-state">On|Off</span></label>
 *
 * Keyboard: Tab focuses, Space toggles (native checkbox). `stateText: false` hides the On/Off text; `text` renders a
 * visible label after the switch (then `label` may be omitted).
 */
import { define, h, LabcoatElement, read, type Child, type MaybeReactive } from "../core";

export interface SwitchOptions {
  checked: MaybeReactive<boolean>;
  onChange: (checked: boolean) => void;
  /** accessible name (required unless `text` is given) */
  label?: MaybeReactive<string>;
  /** visible label text after the switch */
  text?: Child;
  disabled?: MaybeReactive<boolean>;
  /** On/Off text after the track: default ["On", "Off"]; false hides it */
  stateText?: false | [on: string, off: string];
  class?: string;
}

export function switchControl(opts: SwitchOptions): HTMLLabelElement {
  const { checked, onChange, label, text, disabled, stateText = ["On", "Off"] } = opts;
  return h(
    "label",
    { class: () => ["switch", opts.class, read(disabled) && "disabled"] },
    h("input", {
      type: "checkbox",
      role: "switch",
      checked: () => !!read(checked),
      disabled: () => !!read(disabled),
      "aria-label": label,
      onchange: (e) => onChange((e.currentTarget as HTMLInputElement).checked),
    }),
    h("span", { class: "switch-track", "aria-hidden": "true" }, h("span", { class: "switch-thumb" })),
    stateText ? h("span", { class: "switch-state", "aria-hidden": "true" }, () => (read(checked) ? stateText[0] : stateText[1])) : null,
    text != null ? h("span", { class: "switch-label" }, text) : null,
  );
}

export interface SwitchProps {
  checked: boolean;
  disabled: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}
export interface LcSwitch extends SwitchProps {}
/**
 * `<lc-switch label="Confetti" checked>`: self-contained (keeps its own state; `onChange` reports changes).
 * Host is `display: contents`.
 */
export class LcSwitch extends LabcoatElement<SwitchProps> {
  static defaults: SwitchProps = { checked: false, disabled: false, label: "", onChange: () => {} };
  static observedAttributes = ["checked", "disabled", "label"];
  attributeChangedCallback(attr: "checked" | "disabled" | "label", _o: string | null, v: string | null) {
    if (attr === "label") this.label = v ?? "";
    else this[attr] = v != null;
  }
  protected render() {
    this.style.display = "contents";
    const p = this.props;
    return switchControl({
      checked: p.checked,
      disabled: p.disabled,
      label: p.label,
      onChange: (v) => {
        p.checked.set(v);
        p.onChange.peek()(v);
      },
    });
  }
}
define("lc-switch", LcSwitch);

declare global {
  interface HTMLElementTagNameMap {
    "lc-switch": LcSwitch;
  }
}
