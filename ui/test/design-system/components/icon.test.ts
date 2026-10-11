import { describe, expect, it } from "vitest";
import { h, root, signal } from "../../../design-system/src/core";
import { icon, iconNames, iconSvg, type IconName } from "../../../design-system/src/components/icon";
import { ICONS } from "../../../design-system/src/components/icon-set";
import { flushMicrotasks } from "../../helpers/dom";

describe("icon set", () => {
  it("every icon is an <svg> string and iconNames lists them all", () => {
    expect(iconNames.length).toBeGreaterThan(40);
    expect(iconNames).toEqual(Object.keys(ICONS));
    for (const n of iconNames) expect(ICONS[n], n).toMatch(/^\s*(<\?xml[^>]*>\s*)?<svg[\s>]/);
    for (const n of ["play", "stop", "checkCircle", "dismissCircle", "labcoat", "labcoatSmall", "document", "dismiss"]) expect(iconNames).toContain(n);
  });
});

describe("iconSvg", () => {
  it("returns a fresh decorative svg without a fixed size", () => {
    const a = iconSvg("play");
    const b = iconSvg("play");
    expect(a).not.toBe(b);
    expect(a.tagName.toLowerCase()).toBe("svg");
    expect(a.getAttribute("fill")).toBe("currentColor");
    expect(a.getAttribute("aria-hidden")).toBe("true");
    expect(a.getAttribute("focusable")).toBe("false");
    expect(a.hasAttribute("width")).toBe(false);
    expect(a.hasAttribute("height")).toBe(false);
    expect(a.outerHTML).toBe(b.outerHTML);
  });
});

describe("icon()", () => {
  it("renders span.icon sized in px with optional class and title", () => {
    const el = icon("search", { size: 20, class: "extra", title: "Find" });
    expect(el.tagName).toBe("SPAN");
    expect(el.className).toBe("icon extra");
    expect(el.style.width).toBe("20px");
    expect(el.style.height).toBe("20px");
    expect(el.title).toBe("Find");
    expect(el.querySelector("svg")).not.toBeNull();
    const d = icon("search");
    expect(d.className).toBe("icon");
    expect(d.style.width).toBe("16px");
  });

  it("uses the small optical variant of the brand mark at <= 20 px", () => {
    expect(icon("labcoat", { size: 16 }).innerHTML).toBe(iconSvg("labcoatSmall").outerHTML);
    expect(icon("labcoat", { size: 32 }).innerHTML).toBe(iconSvg("labcoat").outerHTML);
  });

  it("follows a reactive name and class", () => {
    root((dispose) => {
      const name = signal<IconName>("eye");
      const spin = signal(false);
      const el = icon(name, { class: () => (spin() ? "spin" : null) });
      expect(el.innerHTML).toBe(iconSvg("eye").outerHTML);
      const svg = el.firstChild;
      name.set("eye");
      expect(el.firstChild).toBe(svg);
      name.set("eyeFilled");
      expect(el.innerHTML).toBe(iconSvg("eyeFilled").outerHTML);
      spin.set(true);
      expect(el.className).toBe("icon spin");
      dispose();
    });
  });
});

describe("<lc-icon>", () => {
  it("renders from attributes and props (host is the .icon box)", async () => {
    const el = document.createElement("lc-icon");
    el.setAttribute("name", "play");
    el.setAttribute("size", "24");
    document.body.append(el);
    expect(el.classList.contains("icon")).toBe(true);
    expect(el.style.width).toBe("24px");
    expect(el.innerHTML).toContain(iconSvg("play").outerHTML);
    el.name = "stop";
    el.size = 12;
    expect(el.style.height).toBe("12px");
    expect(el.innerHTML).toContain(iconSvg("stop").outerHTML);
    el.setAttribute("name", "");
    expect(el.name).toBe("stop");
    el.remove();
    await flushMicrotasks();
    expect(el.childNodes).toHaveLength(0);
    const viaH = h("lc-icon", { name: "labcoat", size: 18 });
    document.body.append(viaH);
    expect(viaH.innerHTML).toContain(iconSvg("labcoatSmall").outerHTML);
  });
});
