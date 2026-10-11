import { afterEach, describe, expect, it } from "vitest";
import { root } from "@eriklieben/labcoat-design-system";
import type { RunningNowInfo } from "../../src/contract";
import { applyRunningNowEvent, formatElapsed, runningNow, runningNowAt } from "../../src/running-now";
import { runningNowChip } from "../../src/components/running-now";
import { serverEventListeners } from "../../src/server-events";

const info = (p: Partial<RunningNowInfo> = {}): RunningNowInfo => ({
  count: 2,
  tests: [
    { projectId: "P1", id: "a", displayName: "Hangs", elapsedMs: 59_000 },
    { projectId: "P2", id: "b", displayName: "Quick", elapsedMs: 1_000 },
  ],
  hangThresholdMs: 60_000,
  ...p,
});

afterEach(() => applyRunningNowEvent({ type: "runFinished" }));

describe("running now", () => {
  it("advances elapsed times from when the event arrived and marks tests slow past the threshold", () => {
    const v = runningNowAt({ value: info(), at: 1_000 }, 3_500)!;
    expect(v.tests.map((t) => [t.displayName, t.elapsedMs, t.slow])).toEqual([
      ["Hangs", 61_500, true],
      ["Quick", 3_500, false],
    ]);
    expect(v.count).toBe(2);
    // a host-side slow flag counts too; no threshold = never slow by time
    const host = runningNowAt({ value: info({ hangThresholdMs: undefined, tests: [{ projectId: "P", id: "x", displayName: "X", elapsedMs: 5, slow: true }] }), at: 0 }, 0)!;
    expect(host.tests[0].slow).toBe(true);
    expect(runningNowAt({ value: info({ tests: [] }), at: 0 }, 0)).toBeUndefined();
    expect(runningNowAt(undefined, 0)).toBeUndefined();
  });

  it("follows runningNow, state (only during a run) and runFinished events, also through the server event listeners", () => {
    applyRunningNowEvent({ type: "runningNow", runningNow: info() });
    expect(runningNow()?.tests[0].displayName).toBe("Hangs");
    applyRunningNowEvent({ type: "runningNow", runningNow: null });
    expect(runningNow()).toBeUndefined();
    applyRunningNowEvent({ type: "state", state: { runningNow: info() } }); // no current run: ignored
    expect(runningNow()).toBeUndefined();
    for (const l of serverEventListeners) l({ type: "state", state: { runningNow: info(), currentRun: { runId: "r" } } });
    expect(runningNow()?.count).toBe(2);
    applyRunningNowEvent({ type: "runFinished" });
    expect(runningNow()).toBeUndefined();
  });

  it("formats elapsed times", () => {
    expect(formatElapsed(999)).toBe("0 s");
    expect(formatElapsed(12_400)).toBe("12 s");
    expect(formatElapsed(65_000)).toBe("1:05");
    expect(formatElapsed(3_725_000)).toBe("1:02:05");
  });

  it("the chip shows the longest-running test, 'possibly hanging', and opens the list", () => {
    applyRunningNowEvent({ type: "runningNow", runningNow: info({ tests: [{ projectId: "P1", id: "a", displayName: "Hangs", elapsedMs: 75_000, slow: true }, { projectId: "P2", id: "b", displayName: "Quick", elapsedMs: 1_000 }], count: 3, testTimeoutMs: 120_000 }) });
    let el!: HTMLElement;
    root(() => {
      el = document.createElement("div");
      el.append(runningNowChip() as Node);
    });
    document.body.append(el);
    const button = el.querySelector<HTMLButtonElement>("button.running-now")!;
    expect(button.classList.contains("is-slow")).toBe(true);
    expect(button.textContent).toContain("Hangs");
    expect(button.textContent).toContain("1:15");
    expect(button.textContent).toContain("possibly hanging");
    expect(button.textContent).toContain("+2");
    expect(button.title).toContain("Hangs: running for 1:15 · possibly hanging · times out after 2:00");
    expect(button.getAttribute("aria-expanded")).toBe("false");
    button.click();
    expect(button.getAttribute("aria-expanded")).toBe("true");
    const rows = [...el.querySelectorAll(".running-now-list .rn-row")].map((r) => r.textContent);
    expect(rows[0]).toContain("Hangs");
    expect(rows[1]).toContain("Quick");
    expect(rows.join("|")).toContain("+1 more running");
    expect(rows.join("|")).toContain("Tests time out after 2:00 (testTimeout).");
    button.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(button.getAttribute("aria-expanded")).toBe("false");
    el.remove();
  });
});
