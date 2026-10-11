import { describe, expect, it } from "vitest";
import { h } from "../../../design-system/src/core";
import { delayed, skeleton } from "../../../design-system/src/components/skeleton";

describe("skeleton()", () => {
  it("is a busy status region with varied line widths", () => {
    const s = skeleton();
    expect(s.className).toBe("lc-skeleton");
    expect(s.getAttribute("role")).toBe("status");
    expect(s.getAttribute("aria-busy")).toBe("true");
    expect(s.getAttribute("aria-label")).toBe("Loading");
    const ls = s.querySelectorAll<HTMLElement>(".lc-skeleton-line");
    expect(ls).toHaveLength(3);
    expect([...ls].map((l) => l.style.width)).toEqual(["92%", "78%", "85%"]);
  });

  it("heading line first; widths cycle; label and class", () => {
    const s = skeleton({ lines: 8, heading: true, label: "Loading coverage", class: "x" });
    expect(s.className).toBe("lc-skeleton x");
    expect(s.getAttribute("aria-label")).toBe("Loading coverage");
    const ls = s.querySelectorAll<HTMLElement>(".lc-skeleton-line");
    expect(ls).toHaveLength(9);
    expect(ls[0].className).toBe("lc-skeleton-line lg");
    expect(ls[0].style.width).toBe("45%");
    expect(ls[7].style.width).toBe("92%");
    expect(skeleton({ lines: 0 }).children).toHaveLength(0);
  });
});

describe("delayed()", () => {
  it("hides the element for the delay via CSS (no timer)", () => {
    const el = h("span");
    expect(delayed(el)).toBe(el);
    expect(el.classList.contains("lc-delayed")).toBe(true);
    expect(el.style.getPropertyValue("--lc-delay")).toBe("300ms");
    delayed(el, 450);
    expect(el.style.getPropertyValue("--lc-delay")).toBe("450ms");
  });
});
