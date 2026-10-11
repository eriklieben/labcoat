import { describe, expect, it, vi } from "vitest";
import { root, signal } from "@eriklieben/labcoat-design-system/core";
import { segmented, type SegmentOption } from "@eriklieben/labcoat-design-system/components";

const key = (t: EventTarget, k: string) => {
  const e = new KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true });
  t.dispatchEvent(e);
  return e;
};

type V = "system" | "light" | "dark";
function make(extra: { disabled?: () => boolean; size?: "small" } = {}) {
  const value = signal<V>("light");
  const onChange = vi.fn((v: V) => value.set(v));
  const options: SegmentOption<V>[] = [
    { value: "system", label: "System", icon: "darkTheme" },
    { value: "light", label: "", icon: "sun", title: "Light" },
    { value: "dark", label: "Dark", disabled: true },
  ];
  const el = root(() => segmented({ options, value, onChange, label: "Mode", class: "c", ...extra }));
  document.body.append(el);
  return { el, value, onChange, btns: [...el.querySelectorAll<HTMLButtonElement>("[role=radio]")] };
}

describe("segmented()", () => {
  it("is a labelled radiogroup with aria-checked, .on and roving tabindex", () => {
    const { el, btns, value } = make({ size: "small" });
    expect(el.getAttribute("role")).toBe("radiogroup");
    expect(el.getAttribute("aria-label")).toBe("Mode");
    expect(el.className).toBe("segmented segmented-small c");
    expect(el.hasAttribute("aria-disabled")).toBe(false);
    expect(btns.map((b) => b.getAttribute("aria-checked"))).toEqual(["false", "true", "false"]);
    expect(btns.map((b) => b.tabIndex)).toEqual([-1, 0, -1]);
    expect(btns[1].classList.contains("on")).toBe(true);
    // icon-only option: title is the accessible name
    expect(btns[1].getAttribute("aria-label")).toBe("Light");
    expect(btns[0].hasAttribute("aria-label")).toBe(false);
    expect(btns[0].textContent).toBe(" System");
    expect(btns[2].disabled).toBe(true);
    value.set("system");
    expect(btns.map((b) => b.tabIndex)).toEqual([0, -1, -1]);
  });

  it("click and arrow keys select (wrapping, skipping disabled options)", () => {
    const { btns, onChange } = make();
    btns[0].click();
    expect(onChange).toHaveBeenLastCalledWith("system");
    expect(key(btns[0], "ArrowRight").defaultPrevented).toBe(true);
    expect(onChange).toHaveBeenLastCalledWith("light");
    expect(document.activeElement).toBe(btns[1]);
    key(btns[1], "ArrowDown"); // dark disabled → wraps to system
    expect(onChange).toHaveBeenLastCalledWith("system");
    key(btns[0], "ArrowUp");
    expect(onChange).toHaveBeenLastCalledWith("light");
    key(btns[1], "Home");
    expect(onChange).toHaveBeenLastCalledWith("system");
    key(btns[0], "End");
    expect(onChange).toHaveBeenLastCalledWith("light");
    expect(key(btns[1], "Enter").defaultPrevented).toBe(false);
  });

  it("an unknown value puts the tab stop on the first option; group disabled", () => {
    const disabled = signal(false);
    const { el, btns, value } = make({ disabled });
    value.set("nope" as V);
    expect(btns.map((b) => b.tabIndex)).toEqual([0, -1, -1]);
    expect(el.getAttribute("aria-disabled")).toBe("false");
    disabled.set(true);
    expect(el.getAttribute("aria-disabled")).toBe("true");
    expect(btns.every((b) => b.disabled)).toBe(true);
  });
});

describe("<lc-segmented>", () => {
  it("keeps the value and reports changes", () => {
    const onChange = vi.fn();
    const el = document.createElement("lc-segmented");
    el.options = [
      { value: "a", label: "A" },
      { value: "b", label: "B" },
    ];
    el.label = "Pick";
    el.value = "a";
    el.onChange = onChange;
    document.body.append(el);
    expect(el.style.display).toBe("contents");
    const btns = [...el.querySelectorAll<HTMLButtonElement>("[role=radio]")];
    btns[1].click();
    expect(onChange).toHaveBeenCalledWith("b");
    expect(el.value).toBe("b");
    expect(btns[1].getAttribute("aria-checked")).toBe("true");
  });
});
