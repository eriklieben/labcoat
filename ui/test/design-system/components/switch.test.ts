import { describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system/core";
import { switchControl } from "@eriklieben/labcoat-design-system/components";

const toggle = (input: HTMLInputElement) => {
  input.checked = !input.checked;
  input.dispatchEvent(new Event("change", { bubbles: true }));
};

describe("switchControl()", () => {
  it("renders a native checkbox with role=switch, label and On/Off state text", () => {
    const checked = signal(false);
    const onChange = vi.fn((v: boolean) => checked.set(v));
    const el = root(() => switchControl({ checked, onChange, label: "Confetti", class: "extra" }));
    const input = el.querySelector("input")!;
    expect(el.tagName).toBe("LABEL");
    expect(el.className).toBe("switch extra");
    expect(input.type).toBe("checkbox");
    expect(input.getAttribute("role")).toBe("switch");
    expect(input.getAttribute("aria-label")).toBe("Confetti");
    expect(input.checked).toBe(false);
    expect(el.querySelector(".switch-track")!.getAttribute("aria-hidden")).toBe("true");
    expect(el.querySelector(".switch-state")!.textContent).toBe("Off");
    toggle(input);
    expect(onChange).toHaveBeenCalledWith(true);
    expect(el.querySelector(".switch-state")!.textContent).toBe("On");
    checked.set(false);
    expect(input.checked).toBe(false);
  });

  it("disabled state, custom/hidden state text and a visible text label", () => {
    const disabled = signal(true);
    const el = root(() => switchControl({ checked: true, onChange: () => {}, text: "Watch", disabled, stateText: ["Yes", "No"] }));
    const input = el.querySelector("input")!;
    expect(input.disabled).toBe(true);
    expect(el.classList.contains("disabled")).toBe(true);
    expect(input.hasAttribute("aria-label")).toBe(false);
    expect(el.querySelector(".switch-label")!.textContent).toBe("Watch");
    expect(el.querySelector(".switch-state")!.textContent).toBe("Yes");
    disabled.set(false);
    expect(el.classList.contains("disabled")).toBe(false);
    const none = root(() => switchControl({ checked: false, onChange: () => {}, label: "x", stateText: false }));
    expect(none.querySelector(".switch-state")).toBeNull();
    expect(none.querySelector(".switch-label")).toBeNull();
  });

  it("Space on the focused input toggles (native checkbox click)", () => {
    const onChange = vi.fn();
    const el = root(() => switchControl({ checked: false, onChange, label: "x" }));
    document.body.append(el);
    const input = el.querySelector("input")!;
    input.focus();
    input.click(); // what Space does natively
    expect(onChange).toHaveBeenCalledWith(true);
  });
});

describe("<lc-switch>", () => {
  it("reflects attributes and keeps its own state", () => {
    const onChange = vi.fn();
    const el = document.createElement("lc-switch");
    el.setAttribute("label", "Watch");
    el.setAttribute("checked", "");
    el.onChange = onChange;
    document.body.append(h("div", null, el));
    expect(el.style.display).toBe("contents");
    const input = el.querySelector("input")!;
    expect(input.checked).toBe(true);
    expect(input.getAttribute("aria-label")).toBe("Watch");
    toggle(input);
    expect(onChange).toHaveBeenCalledWith(false);
    expect(el.checked).toBe(false);
    el.setAttribute("disabled", "");
    expect(input.disabled).toBe(true);
    el.removeAttribute("disabled");
    expect(input.disabled).toBe(false);
    el.removeAttribute("label");
    expect(el.label).toBe("");
  });
});
