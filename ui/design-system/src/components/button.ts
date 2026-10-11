/**
 * Buttons. Factories return the exact markup the stylesheet styles (`<button class="btn btn-accent">`):
 *
 *   button({ variant: "accent", icon: "playFilled", onclick: runAll }, "Run all")
 *   button({ variant: "toggle", pressed: () => watch(), icon: () => watch() ? "eyeFilled" : "eye" }, "Watch")
 *   iconButton({ icon: "expandAll", label: "Expand all", onclick })
 *
 * Any other h() prop (title, disabled, aria-*, …, reactive or not) is passed through to the <button>.
 * `<lc-button>` / `<lc-icon-button>` are the element forms (host is `display: contents` around the real <button>).
 */
import { define, h, LabcoatElement, read, type Child, type ClassValue, type MaybeReactive, type Props } from "../core";
import { icon, type IconName } from "./icon";

export type ButtonVariant = "default" | "accent" | "danger" | "toggle" | "link";

export interface ButtonOptions extends Omit<Props<HTMLButtonElement>, "class"> {
  variant?: ButtonVariant;
  size?: "default" | "small";
  /** leading icon */
  icon?: MaybeReactive<IconName>;
  /** extra classes for the leading icon (e.g. a reactive "spin") */
  iconClass?: MaybeReactive<ClassValue>;
  /** toggle buttons: adds `.on` + aria-pressed */
  pressed?: MaybeReactive<boolean>;
  class?: MaybeReactive<ClassValue>;
}

export function button(opts: ButtonOptions, ...children: Child[]): HTMLButtonElement {
  const { variant = "default", size = "default", icon: iconName, iconClass, pressed, class: extra, ...rest } = opts;
  const base = ["btn", variant !== "default" && `btn-${variant}`, size === "small" && "btn-small"];
  const props = rest as Props<HTMLButtonElement>;
  if (pressed !== undefined) props["aria-pressed"] = typeof pressed === "function" ? () => pressed() : pressed;
  const dynamic = typeof extra === "function" || typeof pressed === "function";
  props.class = dynamic ? () => [base, read(pressed) && "on", read(extra)] : [base, pressed && "on", extra];
  return h("button", props, iconName ? icon(iconName, { class: iconClass }) : null, ...children);
}

export interface IconButtonOptions extends Omit<Props<HTMLButtonElement>, "class"> {
  icon: MaybeReactive<IconName>;
  /** tooltip + accessible name */
  label: MaybeReactive<string>;
  size?: "default" | "small";
  iconSize?: number;
  toggled?: MaybeReactive<boolean>;
  class?: MaybeReactive<ClassValue>;
}

/** `<button class="icon-btn" title aria-label>` with an 18px icon (the tree toolbar style). */
export function iconButton(opts: IconButtonOptions): HTMLButtonElement {
  const { icon: iconName, label, size = "default", iconSize = 18, toggled, class: extra, ...rest } = opts;
  const props = rest as Props<HTMLButtonElement>;
  props.title = label;
  props["aria-label"] = label;
  props.class = () => ["icon-btn", size === "small" && "small", read(toggled) && "toggled", read(extra)];
  return h("button", props, icon(iconName, { size: iconSize }));
}

// ---- element forms ----------------------------------------------------------------------------------

export interface ButtonProps {
  variant: ButtonVariant;
  size: "default" | "small";
  icon: IconName | undefined;
  disabled: boolean;
  pressed: boolean | undefined;
  label: string;
}
export interface LcButton extends ButtonProps {}
/** `<lc-button variant="accent" icon="playFilled" label="Run all">`; listen for `click` on the host. */
export class LcButton extends LabcoatElement<ButtonProps> {
  static defaults: ButtonProps = { variant: "default", size: "default", icon: undefined, disabled: false, pressed: undefined, label: "" };
  static observedAttributes = ["variant", "size", "icon", "label"];
  attributeChangedCallback(attr: "variant" | "size" | "icon" | "label", _o: string | null, v: string | null) {
    (this as unknown as Record<string, unknown>)[attr] = v ?? undefined;
  }
  protected render() {
    this.style.display = "contents";
    const p = this.props;
    // Rebuilt when variant/size/icon change (rare); label/disabled/pressed are bound in place.
    return () =>
      button(
        { variant: p.variant(), size: p.size(), icon: p.icon(), disabled: () => p.disabled(), pressed: p.pressed() === undefined ? undefined : () => !!p.pressed() },
        () => p.label(),
      );
  }
}
define("lc-button", LcButton);

declare global {
  interface HTMLElementTagNameMap {
    "lc-button": LcButton;
  }
}
