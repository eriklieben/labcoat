import { describe, expect, it } from "vitest";
import { brandLogo } from "../../../design-system/src/components/brand-logo";

describe("brandLogo()", () => {
  it("decorative by default; labelled logos are images", () => {
    const d = brandLogo();
    expect(d.className).toBe("lc-logo");
    expect(d.style.width).toBe("24px");
    expect(d.getAttribute("aria-hidden")).toBe("true");
    expect(d.hasAttribute("role")).toBe(false);
    const svg = d.querySelector("svg")!;
    expect(svg.getAttribute("aria-hidden")).toBe("true");
    expect(svg.getAttribute("focusable")).toBe("false");
    expect(svg.hasAttribute("width")).toBe(false);
    const l = brandLogo({ size: 32, label: "Labcoat", class: "x" });
    expect(l.className).toBe("lc-logo x");
    expect(l.getAttribute("role")).toBe("img");
    expect(l.getAttribute("aria-label")).toBe("Labcoat");
    expect(l.hasAttribute("aria-hidden")).toBe(false);
  });

  it("uses the small optical variant at <= 20 px", () => {
    const small = brandLogo({ size: 16 }).innerHTML;
    const large = brandLogo({ size: 32 }).innerHTML;
    expect(small).not.toBe(large);
    expect(brandLogo({ size: 20 }).innerHTML).toBe(small);
    expect(brandLogo({ size: 21 }).innerHTML).toBe(large);
  });
});

describe("<lc-logo>", () => {
  it("renders from attributes and toggles its accessible name", () => {
    const el = document.createElement("lc-logo");
    el.setAttribute("size", "16");
    el.setAttribute("label", "Labcoat");
    document.body.append(el);
    expect(el.classList.contains("lc-logo")).toBe(true);
    expect(el.style.width).toBe("16px");
    expect(el.getAttribute("role")).toBe("img");
    expect(el.getAttribute("aria-label")).toBe("Labcoat");
    const small = el.innerHTML;
    el.setAttribute("size", "40");
    expect(el.style.height).toBe("40px");
    expect(el.innerHTML).not.toBe(small);
    el.removeAttribute("label");
    expect(el.hasAttribute("role")).toBe(false);
    expect(el.hasAttribute("aria-label")).toBe(false);
    expect(el.getAttribute("aria-hidden")).toBe("true");
    el.setAttribute("size", "");
    expect(el.size).toBe(40);
  });
});
