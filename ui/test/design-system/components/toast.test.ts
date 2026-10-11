import { beforeEach, describe, expect, it, vi } from "vitest";
import { setMotionLevel, toast } from "@eriklieben/labcoat-design-system/components";
import { finishAnimations } from "../../helpers/dom";

const toasts = () => [...document.querySelectorAll<HTMLElement>(".lc-toast")];
const live = () => toasts().filter((t) => !t.inert);

describe("toast()", () => {
  beforeEach(() => vi.useFakeTimers());

  it("shows in a polite status region at the bottom; info icon by default", () => {
    toast("Opened in Rider");
    const region = document.querySelector(".lc-toaster")!;
    expect(region.getAttribute("role")).toBe("status");
    expect(region.getAttribute("aria-live")).toBe("polite");
    const t = toasts()[0];
    expect(t.className).toBe("lc-toast lc-toast-info");
    expect(t.querySelector(".lc-toast-msg")!.textContent).toBe("Opened in Rider");
    expect(t.querySelector(".lc-toast-icon")).not.toBeNull();
    expect(t.querySelector(".lc-toast-close")!.getAttribute("aria-label")).toBe("Dismiss");
    // one shared region
    toast("again");
    expect(document.querySelectorAll(".lc-toaster")).toHaveLength(1);
  });

  it("auto-dismisses after the duration (exit animation, then removed)", async () => {
    toast("bye", { intent: "success" });
    expect(toasts()[0].classList.contains("lc-toast-success")).toBe(true);
    vi.advanceTimersByTime(3999);
    expect(live()).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(live()).toHaveLength(0); // leaving: inert
    await finishAnimations();
    expect(toasts()).toHaveLength(0);
  });

  it("at most 2 stay: the oldest leaves", () => {
    setMotionLevel("off");
    toast("1");
    toast("2");
    toast("3", { intent: "error" });
    expect(toasts().map((t) => t.textContent)).toEqual(["2", "3"]);
    expect(toasts()[1].classList.contains("lc-toast-error")).toBe(true);
  });

  it("the action runs and closes; dismiss closes; the returned function closes once", () => {
    setMotionLevel("off");
    const undo = vi.fn();
    toast("Quarantined", { action: { label: "Undo", onClick: undo }, icon: "link" });
    const btn = document.querySelector<HTMLButtonElement>(".lc-toast-action")!;
    expect(btn.textContent).toBe("Undo");
    btn.click();
    expect(undo).toHaveBeenCalledTimes(1);
    expect(toasts()).toHaveLength(0);

    toast("x");
    document.querySelector<HTMLButtonElement>(".lc-toast-close")!.click();
    expect(toasts()).toHaveLength(0);

    const close = toast("y", { duration: 0 });
    vi.advanceTimersByTime(60_000);
    expect(toasts()).toHaveLength(1); // duration 0: until dismissed
    close();
    close();
    expect(toasts()).toHaveLength(0);
  });

  it("hover and focus pause the timer; leaving re-arms it", () => {
    setMotionLevel("off");
    toast("paused", { duration: 1000 });
    const t = toasts()[0];
    t.dispatchEvent(new Event("mouseenter"));
    vi.advanceTimersByTime(5000);
    expect(toasts()).toHaveLength(1);
    t.dispatchEvent(new Event("mouseleave"));
    vi.advanceTimersByTime(999);
    expect(toasts()).toHaveLength(1);
    t.dispatchEvent(new Event("focusin"));
    vi.advanceTimersByTime(5000);
    expect(toasts()).toHaveLength(1);
    t.dispatchEvent(new Event("focusout"));
    vi.advanceTimersByTime(1000);
    expect(toasts()).toHaveLength(0);
  });

  it("recreates the region when it was removed from the document", () => {
    toast("a");
    document.body.replaceChildren();
    toast("b");
    expect(document.querySelector(".lc-toaster")!.isConnected).toBe(true);
    expect(toasts().map((x) => x.textContent)).toEqual(["b"]);
  });
});
