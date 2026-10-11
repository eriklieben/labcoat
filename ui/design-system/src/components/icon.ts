/**
 * Icons. `icon(name, opts)` returns `<span class="icon" style="width;height"><svg fill="currentColor">…</svg></span>`
 * (the markup the stylesheet expects). SVGs are parsed once per name and cloned afterwards, so icons are cheap in
 * virtualized rows. `<lc-icon name="play" size="16">` is the element form (the host itself is the `.icon` box).
 */
import { define, effect, h, LabcoatElement, read, type MaybeReactive, type ClassValue } from "../core";
import { ICONS } from "./icon-set";

export type IconName = keyof typeof ICONS;
export const iconNames = Object.keys(ICONS) as IconName[];

const templates = new Map<IconName, SVGElement>();

/** A fresh SVG element for `name` (fill=currentColor, no fixed size: it fills its `.icon` box). */
export function iconSvg(name: IconName): SVGElement {
  let t = templates.get(name);
  if (!t) {
    const tpl = document.createElement("template");
    tpl.innerHTML = ICONS[name].replace(
      /<svg([^>]*)>/,
      (_m, attrs: string) => `<svg${attrs.replace(/\s(width|height)="[^"]*"/g, "")} fill="currentColor" aria-hidden="true" focusable="false">`,
    );
    t = tpl.content.firstElementChild as SVGElement;
    templates.set(name, t);
  }
  return t.cloneNode(true) as SVGElement;
}

export interface IconOptions {
  size?: number;
  class?: MaybeReactive<ClassValue>;
  title?: MaybeReactive<string | undefined>;
}

/** The brand mark has a small-size optical variant: `labcoat` at <= 20 px renders `labcoatSmall`. */
const optical = (name: IconName, size: number): IconName => (name === "labcoat" && size <= 20 ? "labcoatSmall" : name);

/** "icon" plus the caller's extra classes, staying reactive when those are. */
function iconClass(extra: MaybeReactive<ClassValue> | undefined): MaybeReactive<ClassValue> {
  if (extra == null) return "icon";
  if (typeof extra === "function") return () => ["icon", read(extra)];
  return ["icon", extra];
}

export function icon(name: MaybeReactive<IconName>, opts: IconOptions = {}): HTMLSpanElement {
  const size = opts.size ?? 16;
  const el = h("span", {
    class: iconClass(opts.class),
    style: { width: size, height: size },
    title: opts.title,
  });
  if (typeof name === "function") {
    let last: IconName | undefined;
    effect(() => {
      const n = optical(name(), size);
      if (n === last) return;
      last = n;
      el.replaceChildren(iconSvg(n));
    });
  } else el.append(iconSvg(optical(name, size)));
  return el;
}

export interface IconProps {
  name: IconName;
  size: number;
}
export interface LcIcon extends IconProps {}
/** `<lc-icon name="play" size="20">`: attributes or properties. */
export class LcIcon extends LabcoatElement<IconProps> {
  static defaults: IconProps = { name: "circle", size: 16 };
  static hostClass = "icon";
  static observedAttributes = ["name", "size"];
  attributeChangedCallback(attr: string, _old: string | null, v: string | null) {
    if (attr === "name" && v) this.name = v as IconName;
    if (attr === "size" && v) this.size = Number(v);
  }
  protected render() {
    effect(() => {
      const s = this.props.size();
      this.style.width = this.style.height = `${s}px`;
    });
    return () => iconSvg(optical(this.props.name(), this.props.size()));
  }
}
define("lc-icon", LcIcon);

declare global {
  interface HTMLElementTagNameMap {
    "lc-icon": LcIcon;
  }
}
