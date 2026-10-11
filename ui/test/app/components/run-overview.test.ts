import { beforeEach, describe, expect, it, vi } from "vitest";
import { h, root } from "@eriklieben/labcoat-design-system";
import type { HistoryEntry, TestNode } from "../../../src/contract";
import { distBar, nextFailureKey, noSelection, onFailureNavRequest, requestFailureNav } from "../../../src/components/run-overview";
import "../../../src/components/run-overview";
import { testKey } from "../../../src/store";
import { buildTree } from "../../../src/tree";
import { click, flushFrame, mount, project, runSummary, testNode, text } from "../../helpers/app";
import { resetStore } from "../../helpers/app3";
import { flushMicrotasks } from "../../helpers/dom";

const passedBefore: HistoryEntry[] = [{ outcome: "passed", at: "2026-10-01T09:00:00Z", runId: "r0" }, { outcome: "failed", at: "2026-10-01T10:00:00Z", runId: "r1" }];
const failedBefore: HistoryEntry[] = [{ outcome: "failed", at: "2026-10-01T09:00:00Z", runId: "r0" }, { outcome: "failed", at: "2026-10-01T10:00:00Z", runId: "r1" }];

const tPass = (id: string, ms: number, p: Partial<TestNode> = {}) => testNode({ id, outcome: "passed", durationMs: ms, ...p });
const tNew = testNode({ id: "Ns.A.NewFail", outcome: "failed", errorMessage: "first line\nsecond", durationMs: 30, history: passedBefore });
const tOld = testNode({ id: "Ns.A.OldFail", outcome: "timedOut", durationMs: 20, history: failedBefore });

function overview(onSelect = vi.fn()) {
  const el = mount(h("lc-run-overview", { onSelect }));
  return { el, onSelect };
}

describe("<lc-run-overview>", () => {
  beforeEach(async () => {
    await resetStore();
  });

  it("without tests: the 'No test selected' empty state", () => {
    const { el } = overview();
    expect(el.style.display).toBe("contents");
    expect(text(el.querySelector(".empty-title"))).toBe("No test selected");
    expect(el.querySelectorAll(".empty-sub kbd").length).toBeGreaterThan(0);
  });

  it("tests that never ran: headline, 'Run all' and the plain hint", async () => {
    const { transport } = await resetStore({ tests: [testNode({ id: "Ns.A.T1" }), testNode({ id: "Ns.A.T2" })] });
    const { el } = overview();
    expect(text(el.querySelector(".kind-label"))).toBe("Test suite");
    expect(text(el.querySelector(".detail-title"))).toBe("2 tests, none run yet");
    expect(text(el.querySelector(".overview-run"))).toBe("No run in this session yet");
    const run = [...el.querySelectorAll<HTMLButtonElement>(".detail-actions button")].find((b) => text(b) === "Run all")!;
    expect(run.disabled).toBe(false);
    run.click();
    expect(transport.sent).toEqual([{ type: "run" }]);
    expect(text(el.querySelector(".overview-hint"))).toMatch(/^Select a test in the tree/);
  });

  it("'Run all' is disabled while disconnected", async () => {
    await resetStore({ tests: [testNode()] }, { open: false });
    const { el } = overview();
    const run = [...el.querySelectorAll<HTMLButtonElement>(".detail-actions button")].find((b) => text(b) === "Run all")!;
    expect(run.disabled).toBe(true);
  });

  it("all passed: headline, last run line, slowest tests (top 5, no stale) and the distribution bar", async () => {
    const tests = [1, 2, 3, 4, 5, 6].map((i) => tPass(`Ns.A.T${i}`, i * 10));
    tests.push(tPass("Ns.A.Stale", 999, { stale: true }));
    tests.push(tPass("Ns.A.Zero", 0));
    await resetStore({ tests, lastRun: runSummary({ runId: "r9", total: 8, passed: 8, durationMs: 1500, finishedAt: "2026-10-01T10:01:00Z", scope: "subset", repeat: { count: 3, completed: 3, perTest: [] }, quarantined: 2 }) });
    const { el } = overview();
    expect(text(el.querySelector(".detail-title"))).toBe("All 8 passed");
    const runLine = text(el.querySelector(".overview-run"));
    expect(runLine).toMatch(/^Last run /);
    expect(runLine).toContain("1.50 s");
    expect(runLine).toContain("8 selected tests");
    expect(runLine).toContain("3× repeat");
    expect(runLine).toContain("8 passed");
    expect(runLine).toContain("2 quarantined");
    const sections = [...el.querySelectorAll(".overview-section")];
    expect(sections).toHaveLength(1);
    expect(text(sections[0].querySelector("h3"))).toBe("Slowest tests");
    const names = [...sections[0].querySelectorAll(".failure-name")].map((n) => text(n));
    expect(names).toEqual(["A.T6", "A.T5", "A.T4", "A.T3", "A.T2"]);
    // no "run failed" / "open first failure"
    expect(el.querySelectorAll(".detail-actions button")).toHaveLength(0);
    const bar = el.querySelector<HTMLElement>(".dist-bar")!;
    expect(bar.title).toBe("8 of 8 have results");
    expect(bar.querySelector<HTMLElement>(".seg.passed")!.style.transform).toBe("translateX(0.000%) scaleX(1.0000)");
  });

  it("some results missing: 'x of y passed'; stale results are mentioned when there is no run", async () => {
    await resetStore({ tests: [tPass("Ns.A.T1", 5, { stale: true }), testNode({ id: "Ns.A.T2" })] });
    const { el } = overview();
    expect(text(el.querySelector(".detail-title"))).toBe("1 of 2 passed");
    expect(text(el.querySelector(".overview-run"))).toBe("No run in this session yet · 1 results from a previous session");
  });

  it("failures: new failures first (marked next, F8 hint), then still failing; quarantined ones left out", async () => {
    const q = testNode({ id: "Ns.A.Quarantined", outcome: "failed", history: passedBefore });
    const { transport } = await resetStore({ tests: [tNew, tOld, q, tPass("Ns.A.Ok", 1)], quarantined: [testKey(q)], lastRun: runSummary() });
    const { el, onSelect } = overview();
    expect(text(el.querySelector(".detail-title"))).toBe("2 failed · 3 tests");
    const sections = [...el.querySelectorAll(".overview-section")];
    expect(sections.map((s) => text(s.querySelector("h3")))).toEqual(["New failures (1)", "Still failing (1)", "Slowest tests"]);
    expect(sections[0].classList.contains("tone-new")).toBe(true);
    const next = el.querySelector<HTMLButtonElement>(".failure-item.is-next")!;
    expect(text(next.querySelector(".failure-name"))).toBe("A.NewFail");
    expect(text(next.querySelector(".failure-msg"))).toBe("first line");
    expect(next.getAttribute("aria-describedby")).toBe("run-overview-next");
    expect(next.querySelector("#run-overview-next kbd")).not.toBeNull();
    expect(el.querySelector(".overview-hint")!.querySelectorAll(".kbd-combo")).toHaveLength(2);

    next.click();
    expect(onSelect).toHaveBeenLastCalledWith(`t:${testKey(tNew)}`);

    const actions = [...el.querySelectorAll<HTMLButtonElement>(".detail-actions button")];
    expect(actions.map((b) => text(b))).toEqual(["Run failed (2)", "Open first new failure"]);
    actions[1].click();
    expect(onSelect).toHaveBeenLastCalledWith(`t:${testKey(tNew)}`);
    actions[0].click();
    expect(transport.sent).toEqual([{ type: "run", projectId: "P1", testIds: [tNew.id, tOld.id] }]);
  });

  it("only old failures: a 'Failures' section and 'Open first failure'", async () => {
    await resetStore({ tests: [tOld, testNode({ id: "Ns.B.Other", projectId: "P2", outcome: "failed", history: failedBefore })], projects: [project(), project({ id: "P2", name: "Two" })] });
    const { el } = overview();
    expect(text(el.querySelector(".overview-section h3"))).toBe("Failures (2)");
    const actions = [...el.querySelectorAll<HTMLButtonElement>(".detail-actions button")];
    expect(text(actions[1])).toBe("Open first failure");
  });

  it("'Run failed' across projects sends no projectId", async () => {
    const other = testNode({ id: "Ns.B.Other", projectId: "P2", outcome: "failed" });
    const { transport } = await resetStore({ tests: [tOld, other], projects: [project(), project({ id: "P2" })] });
    const { el } = overview();
    click(el.querySelector(".detail-actions button")!);
    expect(transport.sent).toEqual([{ type: "run", projectId: undefined, testIds: [tOld.id, other.id] }]);
  });

  it("during a run: live headline, run failed is soft-disabled, the lists follow at most every 500 ms", async () => {
    vi.useFakeTimers();
    const { store, transport } = await resetStore({ tests: [tOld, tPass("Ns.A.Ok", 1)], lastRun: runSummary() });
    const { el } = overview();
    const failureRows = () => [...el.querySelectorAll(".overview-section")].filter((s) => !text(s.querySelector("h3")).startsWith("Slowest")).flatMap((s) => [...s.querySelectorAll(".failure-item")]);
    store.apply({ type: "runStarted", run: runSummary({ runId: "r2", startedAt: "2026-10-01T11:00:00Z", total: 2 }), testIds: [tOld.id, "Ns.A.Ok"] });
    await flushFrame();
    expect(text(el.querySelector(".kind-label"))).toBe("Run in progress");
    expect(text(el.querySelector(".detail-title"))).toBe("Running 0/2");
    expect(text(el.querySelector(".overview-run"))).toMatch(/^Started /);
    const runFailed = el.querySelector<HTMLButtonElement>(".detail-actions button")!;
    expect(runFailed.getAttribute("aria-disabled")).toBe("true");
    runFailed.click();
    expect(transport.sent).toEqual([]);

    // a new failure arrives: the overview's lists update only after the snapshot timer
    const fresh = testNode({ id: "Ns.A.Ok", outcome: "failed", history: passedBefore, lastRunAt: "2026-10-01T11:00:01Z" });
    store.apply({ type: "testsUpdated", tests: [fresh] });
    await flushFrame();
    expect(text(el.querySelector(".detail-title"))).toBe("Running 1/2 · 1 failed so far");
    expect(failureRows()).toHaveLength(1);
    vi.advanceTimersByTime(500);
    await flushMicrotasks();
    expect(failureRows()).toHaveLength(2);
    // arriving failures slide in (≤ 5 at once)
    expect(el.querySelector(".lc-row-enter")).not.toBeNull();

    store.apply({ type: "runFinished", run: runSummary({ runId: "r2", total: 2, failed: 2 }) });
    await flushFrame();
    expect(text(el.querySelector(".kind-label"))).toBe("Test suite");
  });

  it("an aborted or cancelled run says so", async () => {
    const { store } = await resetStore({ tests: [tPass("Ns.A.T", 1)], lastRun: runSummary({ aborted: true, abortReason: "Host crashed" }) });
    const { el } = overview();
    const bar = el.querySelector(".messagebar")!;
    expect(bar.classList.contains("mb-error")).toBe(true);
    expect(bar.getAttribute("aria-label")).toBe("Run aborted");
    expect(text(bar.querySelector(".mb-title"))).toBe("Run aborted");
    expect(text(bar.querySelector(".mb-body"))).toContain("Host crashed");
    store.apply({ type: "runFinished", run: runSummary({ runId: "r3", cancelled: true, notRun: 1 }) });
    await flushFrame();
    const bar2 = el.querySelector(".messagebar")!;
    expect(bar2.classList.contains("mb-warning")).toBe(true);
    expect(text(bar2)).toContain("1 test did not run.");
    store.apply({ type: "runFinished", run: runSummary({ runId: "r4", cancelled: true, notRun: 3 }) });
    await flushFrame();
    expect(text(el.querySelector(".messagebar"))).toContain("3 tests did not run.");
    store.apply({ type: "runFinished", run: runSummary({ runId: "r5", cancelled: true }) });
    await flushFrame();
    expect(text(el.querySelector(".messagebar"))).toContain("Not every test reported a result.");
  });
});

describe("failure navigation", () => {
  beforeEach(async () => {
    await resetStore();
  });

  it("nextFailureKey walks failures in tree order and wraps; from nothing it starts at the first new failure", async () => {
    const { store } = await resetStore({ tests: [tOld, tNew, tPass("Ns.A.Ok", 1)] });
    const roots = buildTree(store.get().projects, store.get().tests);
    const kOld = `t:${testKey(tOld)}`;
    const kNew = `t:${testKey(tNew)}`;
    expect(nextFailureKey(roots, undefined, 1)).toBe(kNew);
    const forward = nextFailureKey(roots, kNew, 1)!;
    expect([kOld, kNew]).toContain(forward);
    expect(nextFailureKey(roots, forward, 1)).not.toBe(forward);
    // backwards from nothing: the last failure
    expect(nextFailureKey(roots, "p:P1", -1)).toBeDefined();
    expect(nextFailureKey(buildTree([], new Map()), undefined, 1)).toBeUndefined();
  });

  it("from nothing without new failures: the first failure", async () => {
    const { store } = await resetStore({ tests: [tOld] });
    const roots = buildTree(store.get().projects, store.get().tests);
    expect(nextFailureKey(roots, undefined, 1)).toBe(`t:${testKey(tOld)}`);
  });

  it("requestFailureNav reaches onFailureNavRequest listeners until disposed", () => {
    const seen: number[] = [];
    const off = onFailureNavRequest((d) => seen.push(d));
    requestFailureNav(1);
    requestFailureNav(-1);
    off();
    requestFailureNav(1);
    expect(seen).toEqual([1, -1]);
  });
});

describe("distBar() / noSelection()", () => {
  it("places the segments by share; empty totals give zero widths", () => {
    const bar = root(() => distBar(() => ({ total: 4, passed: 2, failed: 1, skipped: 1 })));
    expect(bar.querySelector<HTMLElement>(".seg.failed")!.style.transform).toBe("translateX(50.000%) scaleX(0.2500)");
    expect(bar.querySelector<HTMLElement>(".seg.skipped")!.style.transform).toBe("translateX(75.000%) scaleX(0.2500)");
    const empty = root(() => distBar(() => ({ total: 0, passed: 0, failed: 0, skipped: 0 })));
    expect(empty.querySelector<HTMLElement>(".seg.passed")!.style.transform).toBe("translateX(0.000%) scaleX(0.0000)");
  });

  it("noSelection is an empty state with keyboard hints", () => {
    const el = root(() => noSelection());
    expect(el.classList.contains("empty-state")).toBe(true);
    expect(text(el.querySelector(".empty-title"))).toBe("No test selected");
  });
});
