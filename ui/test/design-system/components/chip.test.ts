import { describe, expect, it, vi } from "vitest";
import { root, signal } from "../../../design-system/src/core";
import { chip } from "../../../design-system/src/components/chip";

describe("chip()", () => {
  it("is a radio with a status dot, label and count", () => {
    const onclick = vi.fn();
    const c = chip({ id: "failed", label: "Failed", count: 3, active: true, onclick });
    expect(c.getAttribute("role")).toBe("radio");
    expect(c.getAttribute("aria-checked")).toBe("true");
    expect(c.className).toBe("chip chip-failed chip-active");
    expect(c.querySelector(".chip-dot")).not.toBeNull();
    expect(c.querySelector(".chip-count")!.textContent).toBe("3");
    expect(c.textContent).toBe("Failed3");
    c.click();
    expect(onclick).toHaveBeenCalled();
  });

  it("'all' has no dot by default; no count when omitted; dot can be forced", () => {
    const c = chip({ id: "all", label: "All", active: false });
    expect(c.querySelector(".chip-dot")).toBeNull();
    expect(c.querySelector(".chip-count")).toBeNull();
    expect(c.getAttribute("aria-checked")).toBe("false");
    expect(chip({ id: "all", label: "All", active: false, dot: true }).querySelector(".chip-dot")).not.toBeNull();
    expect(chip({ id: "passed", label: "P", active: false, dot: false }).querySelector(".chip-dot")).toBeNull();
  });

  it("active and count can be reactive", () => {
    root((dispose) => {
      const on = signal(false);
      const n = signal(1);
      const c = chip({ id: "passed", label: "Passed", count: n, active: on });
      on.set(true);
      n.set(7);
      expect(c.className).toContain("chip-active");
      expect(c.getAttribute("aria-checked")).toBe("true");
      expect(c.querySelector(".chip-count")!.textContent).toBe("7");
      dispose();
    });
  });
});

describe("<lc-chip>", () => {
  it("renders from attributes and the active prop", () => {
    const el = document.createElement("lc-chip");
    el.setAttribute("chip-id", "skipped");
    el.setAttribute("label", "Skipped");
    el.setAttribute("count", "2");
    document.body.append(el);
    const b = () => el.querySelector("button")!;
    expect(el.style.display).toBe("contents");
    expect(b().className).toBe("chip chip-skipped");
    expect(b().querySelector(".chip-count")!.textContent).toBe("2");
    el.active = true;
    expect(b().className).toBe("chip chip-skipped chip-active");
    el.removeAttribute("count");
    expect(b().querySelector(".chip-count")!.textContent).toBe("");
    el.removeAttribute("label");
    el.removeAttribute("chip-id");
    expect(b().className).toBe("chip chip-all chip-active");
    expect(b().querySelector(".chip-dot")).toBeNull();
  });
});
