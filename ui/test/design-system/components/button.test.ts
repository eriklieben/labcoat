import { describe, expect, it, vi } from "vitest";
import { h, root, signal } from "../../../design-system/src/core";
import { button, iconButton } from "../../../design-system/src/components/button";
import { iconSvg, type IconName } from "../../../design-system/src/components/icon";

describe("button()", () => {
  it("renders the variant / size classes, a leading icon, children and passes props through", () => {
    const onclick = vi.fn();
    const b = button({ variant: "accent", size: "small", icon: "playFilled", title: "Run", onclick }, " Run all");
    expect(b.className).toBe("btn btn-accent btn-small");
    expect(b.title).toBe("Run");
    expect(b.querySelector(".icon")!.innerHTML).toBe(iconSvg("playFilled").outerHTML);
    expect(b.textContent).toBe(" Run all");
    b.click();
    expect(onclick).toHaveBeenCalled();
    expect(button({}, "x").className).toBe("btn");
    expect(button({ variant: "link", class: "more" }).className).toBe("btn btn-link more");
  });

  it("static pressed → .on + aria-pressed", () => {
    const b = button({ variant: "toggle", pressed: true }, "Watch");
    expect(b.className).toBe("btn btn-toggle on");
    expect(b.getAttribute("aria-pressed")).toBe("true");
    const off = button({ variant: "toggle", pressed: false });
    expect(off.className).toBe("btn btn-toggle");
    expect(off.getAttribute("aria-pressed")).toBe("false");
  });

  it("reactive pressed / class / icon / iconClass", () => {
    root((dispose) => {
      const on = signal(false);
      const busy = signal(false);
      const name = signal<IconName>("eye");
      const b = button({ variant: "toggle", pressed: on, class: () => busy() && "busy", icon: name, iconClass: () => (busy() ? "spin" : null) }, "Watch");
      expect(b.className).toBe("btn btn-toggle");
      on.set(true);
      expect(b.className).toBe("btn btn-toggle on");
      expect(b.getAttribute("aria-pressed")).toBe("true");
      busy.set(true);
      expect(b.className).toBe("btn btn-toggle on busy");
      expect(b.querySelector(".icon")!.className).toBe("icon spin");
      name.set("eyeFilled");
      expect(b.querySelector(".icon")!.innerHTML).toBe(iconSvg("eyeFilled").outerHTML);
      dispose();
    });
  });
});

describe("iconButton()", () => {
  it("label is the tooltip and the accessible name; size, toggled and class", () => {
    root((dispose) => {
      const t = signal(false);
      const b = iconButton({ icon: "expandAll", label: "Expand all", size: "small", iconSize: 14, toggled: t, class: "x" });
      expect(b.title).toBe("Expand all");
      expect(b.getAttribute("aria-label")).toBe("Expand all");
      expect(b.className).toBe("icon-btn small x");
      expect((b.querySelector(".icon") as HTMLElement).style.width).toBe("14px");
      t.set(true);
      expect(b.className).toBe("icon-btn small toggled x");
      const d = iconButton({ icon: "copy", label: () => "Copy" });
      expect(d.className).toBe("icon-btn");
      expect((d.querySelector(".icon") as HTMLElement).style.width).toBe("18px");
      expect(d.title).toBe("Copy");
      dispose();
    });
  });
});

describe("<lc-button>", () => {
  it("renders a real button from attributes; label, disabled and pressed are bound", () => {
    const el = document.createElement("lc-button");
    el.setAttribute("variant", "accent");
    el.setAttribute("icon", "play");
    el.setAttribute("label", "Run");
    document.body.append(el);
    expect(el.style.display).toBe("contents");
    const b = () => el.querySelector("button")!;
    expect(b().className).toBe("btn btn-accent");
    expect(b().textContent).toBe("Run");
    expect(b().hasAttribute("aria-pressed")).toBe(false);
    el.label = "Run all";
    expect(b().textContent).toBe("Run all");
    el.disabled = true;
    expect(b().disabled).toBe(true);
    el.pressed = true;
    expect(b().getAttribute("aria-pressed")).toBe("true");
    el.setAttribute("size", "small");
    expect(b().className).toContain("btn-small");
    el.removeAttribute("icon");
    expect(el.icon).toBeUndefined();
    expect(b().querySelector(".icon")).toBeNull();
    const viaH = h("lc-button", { variant: "danger", label: "Stop" });
    document.body.append(viaH);
    expect(viaH.querySelector("button")!.className).toBe("btn btn-danger");
  });
});
