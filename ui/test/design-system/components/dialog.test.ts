import { describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system/core";
import { dialog, setMotionLevel, type DialogOptions } from "@eriklieben/labcoat-design-system/components";
import { addCssAnimation, flushMicrotasks } from "../../helpers/dom";

const dlg = () => document.querySelector<HTMLDialogElement>("dialog.lc-dialog");

function setup(extra: Partial<DialogOptions> = {}) {
  const open = signal(false);
  const onClose = vi.fn((_r: string) => open.set(false));
  const opener = h("button", null, "Open");
  document.body.append(opener);
  const dispose = root((d) => {
    document.body.append(
      h(
        "div",
        null,
        dialog({
          open,
          onClose,
          title: "Keyboard shortcuts",
          content: () => h("div", null, h("input", { class: "in" }), h("button", { class: "b1" }, "One")),
          footer: () => h("button", { class: "ok" }, "Close"),
          ...extra,
        }),
      ),
    );
    return d;
  });
  return { open, onClose, opener, dispose };
}

describe("dialog()", () => {
  it("opens modally with title/labelledby, header close button, body, footer; focuses the first focusable", () => {
    const { open, opener } = setup({ description: "All keys", size: "lg", class: "extra" });
    opener.focus();
    expect(dlg()).toBeNull();
    open.set(true);
    const d = dlg()!;
    expect(d.open).toBe(true);
    expect(d.parentElement).toBe(document.body);
    expect(d.className).toBe("lc-dialog lc-dialog-lg extra");
    expect(d.getAttribute("aria-modal")).toBe("true");
    const title = d.querySelector(".lc-dialog-title")!;
    expect(title.textContent).toBe("Keyboard shortcuts");
    expect(d.getAttribute("aria-labelledby")).toBe(title.id);
    expect(d.hasAttribute("aria-label")).toBe(false);
    expect(d.getAttribute("aria-describedby")).toBe(d.querySelector(".lc-dialog-desc")!.id);
    expect(d.querySelector(".lc-dialog-close")!.getAttribute("aria-label")).toBe("Close");
    expect(d.querySelector(".lc-dialog-footer .ok")).not.toBeNull();
    expect(document.documentElement.classList.contains("lc-modal-open")).toBe(true);
    expect(document.activeElement).toBe(d.querySelector(".in"));
  });

  it("closing returns focus to the opener and removes the modal class (motion off: removed at once)", async () => {
    setMotionLevel("off");
    const { open, opener } = setup();
    opener.focus();
    open.set(true);
    const d = dlg()!;
    open.set(false);
    expect(d.open).toBe(false);
    expect(d.isConnected).toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(document.documentElement.classList.contains("lc-modal-open")).toBe(false);
  });

  it("with motion: waits for the CSS exit animations (or 300 ms) before removal", async () => {
    vi.useFakeTimers();
    const { open } = setup();
    open.set(true);
    const d = dlg()!;
    const a = addCssAnimation(d, "lc-dialog-out");
    open.set(false);
    expect(d.inert).toBe(true);
    expect(d.isConnected).toBe(true);
    a.finish();
    await flushMicrotasks();
    expect(d.isConnected).toBe(false);

    open.set(true);
    const d2 = dlg()!;
    addCssAnimation(d2, "lc-dialog-out");
    open.set(false);
    vi.advanceTimersByTime(300);
    expect(d2.isConnected).toBe(false);
  });

  it("with motion but no running animations: removed at once", () => {
    const { open } = setup();
    open.set(true);
    const d = dlg()!;
    open.set(false);
    expect(d.isConnected).toBe(false);
  });

  it("Escape (cancel) and the close button call onClose with the reason", () => {
    const { open, onClose } = setup();
    open.set(true);
    const cancel = new Event("cancel", { cancelable: true });
    dlg()!.dispatchEvent(cancel);
    expect(cancel.defaultPrevented).toBe(true);
    expect(onClose).toHaveBeenLastCalledWith("escape");
    open.set(true);
    dlg()!.querySelector<HTMLButtonElement>(".lc-dialog-close")!.click();
    expect(onClose).toHaveBeenLastCalledWith("close-button");
  });

  it("a native close while still open syncs the state", () => {
    const { open, onClose } = setup();
    open.set(true);
    dlg()!.dispatchEvent(new Event("close"));
    expect(onClose).toHaveBeenCalledWith("escape");
  });

  it("backdrop click closes only when the press started on the backdrop; dismissOnBackdrop: false", () => {
    const { open, onClose, dispose } = setup();
    open.set(true);
    const d = dlg()!;
    const inner = d.querySelector(".b1")!;
    inner.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    d.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    expect(onClose).not.toHaveBeenCalled();
    d.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    inner.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    expect(onClose).not.toHaveBeenCalled();
    d.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    d.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    expect(onClose).toHaveBeenCalledWith("backdrop");
    dispose();

    const b = setup({ dismissOnBackdrop: false });
    b.open.set(true);
    const d2 = dlg()!;
    d2.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    d2.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    expect(b.onClose).not.toHaveBeenCalled();
  });

  it("Tab wraps inside the dialog (both directions); other keys pass", () => {
    const { open } = setup();
    open.set(true);
    const d = dlg()!;
    const close = d.querySelector<HTMLElement>(".lc-dialog-close")!;
    const ok = d.querySelector<HTMLElement>(".ok")!;
    ok.focus();
    const t1 = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    ok.dispatchEvent(t1);
    expect(t1.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(close);
    const t2 = new KeyboardEvent("keydown", { key: "Tab", shiftKey: true, bubbles: true, cancelable: true });
    close.dispatchEvent(t2);
    expect(document.activeElement).toBe(ok);
    // in the middle: browser default
    const mid = d.querySelector<HTMLElement>(".in")!;
    mid.focus();
    const t3 = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    mid.dispatchEvent(t3);
    expect(t3.defaultPrevented).toBe(false);
    const k = new KeyboardEvent("keydown", { key: "a", bubbles: true, cancelable: true });
    mid.dispatchEvent(k);
    expect(k.defaultPrevented).toBe(false);
  });

  it("Tab with nothing focusable is swallowed; focus goes to the dialog itself", () => {
    const open = signal(true);
    root(() => document.body.append(h("div", null, dialog({ open, onClose: () => {}, label: "Palette", placement: "top", bare: true, content: () => "just text" }))));
    const d = dlg()!;
    expect(d.getAttribute("aria-label")).toBe("Palette");
    expect(d.hasAttribute("aria-labelledby")).toBe(false);
    expect(d.classList.contains("lc-dialog-top")).toBe(true);
    expect(d.classList.contains("bare")).toBe(true);
    expect(d.querySelector("header")).toBeNull();
    expect(document.activeElement).toBe(d);
    const t = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    d.dispatchEvent(t);
    expect(t.defaultPrevented).toBe(true);
  });

  it("initialFocus as selector or function; description without header is visually hidden", () => {
    const a = setup({ initialFocus: ".ok" });
    a.open.set(true);
    expect(document.activeElement).toBe(dlg()!.querySelector(".ok"));
    a.open.set(false);
    a.dispose();

    const open = signal(true);
    root(() =>
      document.body.append(
        h(
          "div",
          null,
          dialog({
            open,
            onClose: () => {},
            label: "L",
            description: "hidden desc",
            content: () => h("button", { class: "x" }, "x"),
            initialFocus: (el) => el.querySelector<HTMLElement>(".x"),
          }),
        ),
      ),
    );
    const d = dlg()!;
    expect(d.querySelector(".lc-dialog-desc.visually-hidden")!.textContent).toBe("hidden desc");
    expect(document.activeElement).toBe(d.querySelector(".x"));
  });

  it("closeButton: true without a title renders a header with only the close button; false hides it", () => {
    const a = setup({ title: undefined, label: "x", closeButton: true });
    a.open.set(true);
    expect(dlg()!.querySelector(".lc-dialog-header .lc-dialog-close")).not.toBeNull();
    expect(dlg()!.querySelector(".lc-dialog-title")).toBeNull();
    a.dispose();
    setMotionLevel("off");
    a.open.set(false);
    const b = setup({ closeButton: false });
    b.open.set(true);
    expect(dlg()!.querySelector(".lc-dialog-close")).toBeNull();
  });
});
