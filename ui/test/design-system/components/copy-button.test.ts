import { beforeEach, describe, expect, it, vi } from "vitest";
import { root, signal } from "@eriklieben/labcoat-design-system/core";
import { copyButton } from "@eriklieben/labcoat-design-system/components";
import { clipboard, flushMicrotasks } from "../../helpers/dom";

const live = () => document.querySelector("[role=status][aria-live=polite]");

describe("copyButton()", () => {
  beforeEach(() => vi.useFakeTimers());

  it("renders an icon button labelled by `label` with copy + check icons", () => {
    const b = root(() => copyButton({ text: "x", label: "Copy error", class: "extra" }));
    expect(b.getAttribute("aria-label")).toBe("Copy error");
    expect(b.title).toBe("Copy error");
    expect(b.className).toBe("icon-btn small lc-copy extra");
    expect(b.querySelector(".lc-copy-icon")).not.toBeNull();
    expect(b.querySelector(".lc-copy-check")).not.toBeNull();
  });

  it("copies the (reactive) text, morphs to done + 'Copied', announces, then resets after 1.2 s", async () => {
    const text = signal("first");
    const onCopied = vi.fn();
    const outer = vi.fn();
    const b = root(() => copyButton({ text, label: "Copy", onCopied }));
    document.body.append(b);
    document.body.addEventListener("click", outer);
    text.set("second");
    b.click();
    expect(outer).not.toHaveBeenCalled(); // stopPropagation
    await flushMicrotasks();
    expect(clipboard.text).toBe("second");
    expect(b.classList.contains("done")).toBe(true);
    expect(b.title).toBe("Copied");
    expect(onCopied).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(30);
    expect(live()!.textContent).toBe("Copied");
    vi.advanceTimersByTime(1200);
    expect(b.classList.contains("done")).toBe(false);
    expect(b.title).toBe("Copy");
  });

  it("falls back to execCommand when the clipboard API fails", async () => {
    clipboard.fail = true;
    const exec = vi.fn(() => true);
    (document as unknown as { execCommand: unknown }).execCommand = exec;
    const b = root(() => copyButton({ text: "fallback", label: "Copy" }));
    document.body.append(b);
    b.click();
    await flushMicrotasks();
    expect(exec).toHaveBeenCalledWith("copy");
    expect(document.querySelector("textarea")).toBeNull();
    expect(b.classList.contains("done")).toBe(true);
  });

  it("announces 'Copy failed' when both ways fail", async () => {
    clipboard.fail = true;
    (document as unknown as { execCommand: unknown }).execCommand = () => false;
    const b = root(() => copyButton({ text: "x", label: "Copy" }));
    document.body.append(b);
    b.click();
    await flushMicrotasks();
    vi.advanceTimersByTime(30);
    expect(b.classList.contains("done")).toBe(false);
    expect(live()!.textContent).toBe("Copy failed");
  });

  it("disposing the owner clears the reset timer", async () => {
    let dispose!: () => void;
    const b = root((d) => ((dispose = d), copyButton({ text: "x", label: "Copy" })));
    document.body.append(b);
    b.click();
    await flushMicrotasks();
    dispose();
    vi.advanceTimersByTime(2000);
    expect(b.classList.contains("done")).toBe(true); // timer cleared: stays as it was
  });
});
