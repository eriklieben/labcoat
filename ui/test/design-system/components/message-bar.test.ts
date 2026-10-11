import { describe, expect, it, vi } from "vitest";
import { h, root, signal } from "../../../design-system/src/core";
import { messageBar, messageBarAction } from "../../../design-system/src/components/message-bar";
import { iconSvg } from "../../../design-system/src/components/icon";
import { animations, finishAnimations } from "../../helpers/dom";

describe("messageBar()", () => {
  it("renders intent, icon, title, plain body, actions and a dismiss button", () => {
    const onDismiss = vi.fn();
    const act = vi.fn();
    const bar = messageBar({ intent: "warning", title: "Heads up", body: "Something", actions: messageBarAction("Fix", act, "Fix it"), onDismiss, dismissLabel: "Close" });
    expect(bar.className).toBe("messagebar mb-warning");
    expect(bar.getAttribute("role")).toBe("status");
    expect(bar.querySelector(".mb-icon")!.innerHTML).toBe(iconSvg("warning").outerHTML);
    expect(bar.querySelector("div.mb-title")!.textContent).toBe("Heads up");
    expect(bar.querySelector(".mb-message")!.className).toBe("mb-message plain");
    const action = bar.querySelector<HTMLButtonElement>(".mb-actions > button.mb-action")!;
    expect(action.className).toBe("btn btn-small mb-action");
    expect(action.type).toBe("button");
    expect(action.title).toBe("Fix it");
    action.click();
    expect(act).toHaveBeenCalled();
    const dismiss = bar.querySelector<HTMLButtonElement>(".mb-dismiss")!;
    expect(dismiss.getAttribute("aria-label")).toBe("Close");
    dismiss.click();
    expect(onDismiss).toHaveBeenCalled();
  });

  it("errors are alerts; role can be overridden or removed", () => {
    expect(messageBar({ intent: "error", title: "x" }).getAttribute("role")).toBe("alert");
    expect(messageBar({ intent: "error", role: "status" }).getAttribute("role")).toBe("status");
    expect(messageBar({ role: null }).hasAttribute("role")).toBe(false);
    expect(messageBar({}).className).toBe("messagebar mb-info");
  });

  it("top layout: inline title + ellipsis body; mono body; custom / no icon; reactive class", () => {
    const top = messageBar({ intent: "snapshot", top: true, title: "Viewing", body: "run 3" });
    expect(top.className).toBe("messagebar mb-snapshot top");
    expect(top.querySelector(".mb-body")!.className).toBe("mb-body inline");
    expect(top.querySelector("span.mb-title")!.textContent).toBe("Viewing");
    expect(top.querySelector(".mb-ellipsis")!.textContent).toBe("run 3");
    expect(top.querySelector(".mb-icon")!.innerHTML).toBe(iconSvg("history").outerHTML);
    expect(messageBar({ body: "err", mono: true }).querySelector(".mb-message")!.className).toBe("mb-message");
    const spinner = h("span", { class: "spinner" });
    expect(messageBar({ icon: spinner }).firstChild).toBe(spinner);
    expect(messageBar({ icon: false }).querySelector(".mb-icon")).toBeNull();
    expect(messageBar({ icon: "info", intent: "success" }).querySelector(".mb-icon")!.innerHTML).toBe(iconSvg("info").outerHTML);
    expect(messageBar({}).querySelector(".mb-title, .mb-message, .mb-actions, .mb-dismiss")).toBeNull();
    root((dispose) => {
      const c = signal("a");
      const bar = messageBar({ class: () => c() });
      c.set("b");
      expect(bar.className).toBe("messagebar mb-info b");
      dispose();
    });
  });
});

describe("<lc-message-bar>", () => {
  it("renders from attributes; dismiss animates out, hides the host and calls onDismiss", async () => {
    const el = document.createElement("lc-message-bar");
    el.setAttribute("intent", "error");
    el.setAttribute("heading", "Build failed");
    el.setAttribute("body", "CS1002");
    el.setAttribute("top", "");
    el.setAttribute("dismissible", "");
    const onDismiss = vi.fn();
    el.onDismiss = onDismiss;
    document.body.append(el);
    expect(el.style.display).toBe("contents");
    const bar = el.querySelector<HTMLElement>(".messagebar")!;
    expect(bar.className).toBe("messagebar mb-error top");
    expect(bar.textContent).toContain("Build failed");
    el.querySelector<HTMLButtonElement>(".mb-dismiss")!.click();
    expect(onDismiss).toHaveBeenCalledTimes(1);
    expect(bar.inert).toBe(true);
    expect(animations.some((a) => a.target === bar)).toBe(true);
    await finishAnimations();
    expect(el.style.display).toBe("none");
    expect(bar.inert).toBe(false);
    expect(bar.style.pointerEvents).toBe("");
  });

  it("attribute removal resets to defaults; not dismissible → no button", () => {
    const el = document.createElement("lc-message-bar");
    el.setAttribute("intent", "success");
    el.setAttribute("heading", "Done");
    document.body.append(el);
    expect(el.querySelector(".mb-dismiss")).toBeNull();
    expect(el.querySelector(".mb-message")).toBeNull();
    el.removeAttribute("intent");
    el.removeAttribute("heading");
    el.removeAttribute("body");
    el.removeAttribute("top");
    expect(el.querySelector(".messagebar")!.className).toBe("messagebar mb-info");
    expect(el.querySelector(".mb-title")).toBeNull();
  });

  it("dismiss with motion off hides at once", () => {
    document.documentElement.dataset.motion = "off";
    const el = h("lc-message-bar", { heading: "x", dismissible: true });
    document.body.append(el);
    el.querySelector<HTMLButtonElement>(".mb-dismiss")!.click();
    expect(el.style.display).toBe("none");
  });
});
