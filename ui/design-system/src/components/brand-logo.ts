/**
 * The Labcoat color logo ("Folded": the plain coat in the Aspire purple ramp, fixed brand fills, transparent
 * background, like Aspire's own header logo). For one-color uses (inherits currentColor) use `icon("labcoat")`.
 *
 *   brandLogo({ size: 24 })                       // decorative (aria-hidden)
 *   brandLogo({ size: 32, label: "Labcoat" })     // role="img" + aria-label
 *   h("lc-logo", { size: 20 })                    // element form
 *
 * At size <= 20 the small optical variant (labcoat-color-16.svg: heavier strokes, fewer details) is used.
 * Markup: `<span class="lc-logo" style="width;height"><svg …/></span>`.
 */
import { define, effect, h, LabcoatElement, type ClassValue } from "../core";
import colorSvg from "../assets/labcoat-color.svg?raw";
import colorSmallSvg from "../assets/labcoat-color-16.svg?raw";

const templates = new Map<string, SVGElement>();
function logoSvg(small: boolean): SVGElement {
  const key = small ? "s" : "l";
  let t = templates.get(key);
  if (!t) {
    const tpl = document.createElement("template");
    tpl.innerHTML = (small ? colorSmallSvg : colorSvg).replace(/<svg([^>]*)>/, (_m, a: string) => `<svg${a.replace(/\s(width|height)="[^"]*"/g, "")} aria-hidden="true" focusable="false">`);
    t = tpl.content.firstElementChild as SVGElement;
    templates.set(key, t);
  }
  return t.cloneNode(true) as SVGElement;
}

export interface BrandLogoOptions {
  size?: number;
  /** accessible name; omit for a decorative logo next to the product name */
  label?: string;
  class?: ClassValue;
}

export function brandLogo(opts: BrandLogoOptions = {}): HTMLSpanElement {
  const size = opts.size ?? 24;
  return h(
    "span",
    { class: ["lc-logo", opts.class], style: { width: size, height: size }, role: opts.label ? "img" : undefined, "aria-label": opts.label, "aria-hidden": opts.label ? undefined : "true" },
    logoSvg(size <= 20),
  );
}

export interface LogoProps {
  size: number;
  label: string;
}
export interface LcLogo extends LogoProps {}
/** `<lc-logo size="24" label="Labcoat">` (the host is the `.lc-logo` box). */
export class LcLogo extends LabcoatElement<LogoProps> {
  static defaults: LogoProps = { size: 24, label: "" };
  static hostClass = "lc-logo";
  static observedAttributes = ["size", "label"];
  attributeChangedCallback(attr: string, _o: string | null, v: string | null) {
    if (attr === "size" && v) this.size = Number(v);
    if (attr === "label") this.label = v ?? "";
  }
  protected render() {
    effect(() => {
      const s = this.props.size();
      this.style.width = this.style.height = `${s}px`;
      const l = this.props.label();
      if (l) {
        this.setAttribute("role", "img");
        this.setAttribute("aria-label", l);
        this.removeAttribute("aria-hidden");
      } else {
        this.removeAttribute("role");
        this.removeAttribute("aria-label");
        this.setAttribute("aria-hidden", "true");
      }
    });
    return () => logoSvg(this.props.size() <= 20);
  }
}
define("lc-logo", LcLogo);

declare global {
  interface HTMLElementTagNameMap {
    "lc-logo": LcLogo;
  }
}
