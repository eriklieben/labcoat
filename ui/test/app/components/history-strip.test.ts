import { beforeEach, describe, expect, it, vi } from "vitest";
import { h, signal } from "@eriklieben/labcoat-design-system";
import type { HistoryEntry } from "../../../src/contract";
import "../../../src/components/history-strip";
import { flushFrame, key, mount, runSummary, text } from "../../helpers/app";
import { resetStore } from "../../helpers/app3";
import { animations, flushMicrotasks } from "../../helpers/dom";

const entry = (i: number, p: Partial<HistoryEntry> = {}): HistoryEntry => ({
  outcome: "passed",
  durationMs: 10 * (i + 1),
  at: `2026-10-0${i + 1}T10:00:00Z`,
  runId: `r${i}`,
  ...p,
});

const hist3 = () => [entry(0), entry(1, { outcome: "failed" }), entry(2)];

function strip(props: { history?: HistoryEntry[] | (() => HistoryEntry[]); activeRunId?: string | (() => string | undefined); onPick?: (id: string | undefined, f?: { startedAt?: string }) => void }) {
  return mount(h("lc-history-strip", { history: props.history ?? hist3(), activeRunId: props.activeRunId, onPick: props.onPick }));
}
const squares = (el: Element) => [...el.querySelectorAll<HTMLButtonElement>(".hsq")];

describe("<lc-history-strip>", () => {
  beforeEach(async () => {
    await resetStore();
  });

  it("renders one square per result, oldest first, with the latest marked current", () => {
    const el = strip({});
    expect(el.classList.contains("history")).toBe(true);
    const sq = squares(el);
    expect(sq.map((b) => [...b.classList].filter((c) => c.startsWith("hsq-")))).toEqual([["hsq-passed"], ["hsq-failed"], ["hsq-passed"]]);
    expect(sq[2].classList.contains("latest")).toBe(true);
    expect(sq[2].classList.contains("current")).toBe(true);
    expect(sq[2].getAttribute("aria-current")).toBe("true");
    expect(sq[0].hasAttribute("aria-current")).toBe(false);
    expect(sq[2].getAttribute("aria-label")).toMatch(/Passed · 30 ms \(latest\)$/);
    expect(el.querySelector(".history-squares")!.getAttribute("role")).toBe("group");
    expect(text(el.querySelector(".history-readout"))).toBe("3 runs");
  });

  it("says '1 run' for a single result and omits the duration of a skipped one", () => {
    const el = strip({ history: [entry(0, { outcome: "skipped" })] });
    expect(text(el.querySelector(".history-readout"))).toBe("1 run");
    expect(squares(el)[0].getAttribute("aria-label")).not.toMatch(/ms/);
    // a single point: no sparkline
    expect(el.querySelector("svg")).toBeNull();
  });

  it("squares are disabled without onPick", () => {
    const el = strip({});
    expect(squares(el).every((b) => b.disabled)).toBe(true);
    expect(squares(el).every((b) => b.tabIndex === -1)).toBe(true);
  });

  it("clicking an older square picks that run; the latest square while live stays live", () => {
    const onPick = vi.fn();
    const el = strip({ onPick });
    const sq = squares(el);
    expect(sq.every((b) => !b.disabled)).toBe(true);
    expect(sq[1].title).toMatch(/Click to view this run/);
    sq[1].click();
    expect(onPick).toHaveBeenLastCalledWith("r1", { startedAt: entry(1).at });
    sq[2].click();
    expect(onPick).toHaveBeenLastCalledWith(undefined);
  });

  it("the viewed run is current and clicking it again goes back to live", () => {
    const onPick = vi.fn();
    const el = strip({ onPick, activeRunId: "r0" });
    const sq = squares(el);
    expect(sq[0].classList.contains("current")).toBe(true);
    expect(sq[0].getAttribute("aria-label")).toMatch(/\(viewing\)$/);
    expect(sq[0].tabIndex).toBe(0);
    expect(text(el.querySelector(".history-readout"))).toMatch(/Passed · 10 ms/);
    sq[0].click();
    expect(onPick).toHaveBeenLastCalledWith(undefined);
    sq[2].click();
    expect(onPick).toHaveBeenLastCalledWith("r2", { startedAt: entry(2).at });
  });

  it("a square without a runId is not clickable", () => {
    const onPick = vi.fn();
    const el = strip({ onPick, history: [entry(0, { runId: undefined }), entry(1)] });
    const sq = squares(el);
    expect(sq[0].disabled).toBe(true);
    sq[0].click();
    expect(onPick).not.toHaveBeenCalled();
  });

  it("runs pruned from the stored run list are disabled and say so", async () => {
    await resetStore({ runs: [runSummary({ runId: "r1" }), runSummary({ runId: "r2" })] });
    const el = strip({ onPick: vi.fn() });
    const sq = squares(el);
    expect(sq[0].classList.contains("pruned")).toBe(true);
    expect(sq[0].disabled).toBe(true);
    expect(sq[0].title).toMatch(/No longer stored/);
    expect(sq[1].disabled).toBe(false);
    // the latest is still the tab stop
    expect(sq[2].tabIndex).toBe(0);
  });

  it("the tab stop falls back to the latest clickable square when the current one is pruned", async () => {
    await resetStore({ runs: [runSummary({ runId: "r1" })] });
    const el = strip({ onPick: vi.fn(), activeRunId: "r0" });
    const sq = squares(el);
    expect(sq.map((b) => b.tabIndex)).toEqual([-1, 0, -1]);
  });

  it("arrow keys move focus between enabled squares (roving)", () => {
    const el = strip({ onPick: vi.fn() });
    const sq = squares(el);
    sq[2].focus();
    const e = key(sq[2], "ArrowLeft");
    expect(e.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(sq[1]);
    key(sq[1], "Home");
    expect(document.activeElement).toBe(sq[0]);
    const other = key(sq[0], "x");
    expect(other.defaultPrevented).toBe(false);
  });

  it("hover (mouse or focus) highlights a square and shows its readout; leaving resets", () => {
    const el = strip({});
    const sq = squares(el);
    sq[0].dispatchEvent(new MouseEvent("mouseenter"));
    expect(sq[0].classList.contains("hover")).toBe(true);
    expect(text(el.querySelector(".history-readout"))).toMatch(/Passed · 10 ms/);
    // the sparkline crosshair follows the hover
    expect(el.querySelector(".spark-cross")).not.toBeNull();
    el.dispatchEvent(new MouseEvent("mouseleave"));
    expect(sq[0].classList.contains("hover")).toBe(false);
    expect(text(el.querySelector(".history-readout"))).toBe("3 runs");
    sq[1].dispatchEvent(new FocusEvent("focus"));
    expect(sq[1].classList.contains("hover")).toBe(true);
  });

  it("draws a duration sparkline (≥ 2 timed results) with an accessible label", () => {
    const el = strip({ history: [entry(0), entry(1, { outcome: "skipped" }), entry(2)] });
    const svg = el.querySelector("svg.sparkline")!;
    expect(svg.getAttribute("role")).toBe("img");
    expect(svg.getAttribute("aria-label")).toBe("Duration over the last 2 runs: 10 ms to 30 ms");
    expect(svg.querySelector(".spark-dot")!.getAttribute("class")).toContain("hsq-passed");
    expect(el.querySelector(".spark-cross")).toBeNull();
  });

  it("a flat sparkline (equal durations) still renders", () => {
    const el = strip({ history: [entry(0, { durationMs: 5 }), entry(1, { durationMs: 5 })] });
    expect(el.querySelector(".spark-line")!.getAttribute("d")).toMatch(/,14\.0/);
  });

  it("moving the mouse over the sparkline hovers the nearest run", () => {
    const el = strip({});
    const svg = el.querySelector<SVGElement>("svg")!;
    svg.getBoundingClientRect = () => ({ left: 0, top: 0, width: 132, height: 28, right: 132, bottom: 28, x: 0, y: 0, toJSON() {} }) as DOMRect;
    el.querySelector(".sparkline-host")!.dispatchEvent(new MouseEvent("mousemove", { clientX: 4 }));
    expect(squares(el)[0].classList.contains("hover")).toBe(true);
    el.querySelector(".sparkline-host")!.dispatchEvent(new MouseEvent("mousemove", { clientX: 500 }));
    expect(squares(el)[2].classList.contains("hover")).toBe(true);
  });

  it("flaky aggregated entries get a striped square and say so", () => {
    const el = strip({ history: [entry(0, { flaky: true, outcome: "failed" })] });
    const sq = squares(el)[0];
    expect(sq.classList.contains("hsq-flaky")).toBe(true);
    expect(sq.getAttribute("aria-label")).toContain("(flaky)");
  });

  it("a new result animates in (not on first render)", async () => {
    const hist = signal(hist3());
    const el = strip({ history: hist });
    await flushMicrotasks();
    expect(animations).toHaveLength(0);
    hist.set([...hist.peek().slice(1), entry(3)]); // capped: same length → shift
    await flushMicrotasks();
    const targets = animations.map((a) => a.target);
    expect(targets).toContain(el.querySelector(".history-squares"));
    expect(targets).toContain(squares(el)[2]);
    expect(targets).toContain(el.querySelector(".spark-dot"));
  });

  it("no animation when decorative motion is off (busy)", async () => {
    const hist = signal(hist3());
    strip({ history: hist });
    document.documentElement.classList.add("lc-busy");
    hist.set([...hist.peek(), entry(3)]);
    await flushMicrotasks();
    expect(animations).toHaveLength(0);
  });

  it("drops its content when disconnected", async () => {
    const el = strip({});
    el.remove();
    await flushFrame();
    expect(el.childElementCount).toBe(0);
  });
});
