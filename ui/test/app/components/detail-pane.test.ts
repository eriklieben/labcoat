import { beforeEach, describe, expect, it, vi } from "vitest";
import { h, signal } from "@eriklieben/labcoat-design-system";
import type { ExplorerState, RunSummary, TestNode } from "../../../src/contract";
import { REPEAT_COUNT, shortTypeName, slowFactor, soft, trimOutput } from "../../../src/components/detail-pane";
import { requestFailureNav } from "../../../src/components/run-overview";
import { setSetting } from "../../../src/settings";
import { getStore, testKey } from "../../../src/store";
import { buildTree, findNode, type TreeNode } from "../../../src/tree";
import { click, coverageReport, fileCoverage, flushFrame, key, mount, project, runSummary, testNode, text } from "../../helpers/app";
import { jsonResponse, resetStore } from "../../helpers/app3";
import { clipboard, flushMicrotasks } from "../../helpers/dom";

const T1 = "Ns.ClassA.Test1";
const tKey = (t: TestNode) => `t:${testKey(t)}`;

function nodeOf(k: string): TreeNode {
  const s = getStore().get();
  const n = findNode(buildTree(s.projects, s.tests), k);
  if (!n) throw new Error(`no node ${k}`);
  return n;
}

async function pane(nodeKey: string | undefined, state?: Partial<ExplorerState>, opts?: { kind?: "ws" | "mock"; open?: boolean }) {
  if (state) await resetStore(state, opts);
  const node = signal<TreeNode | undefined>(nodeKey ? nodeOf(nodeKey) : undefined);
  const onRun = vi.fn();
  const onSelect = vi.fn();
  const el = mount(h("lc-detail-pane", { node, onRun, onSelect }));
  await flushMicrotasks();
  /** re-derive the node from the store (after store updates) */
  const refresh = async () => {
    await flushFrame();
    if (nodeKey) node.set(nodeOf(nodeKey));
    await flushMicrotasks();
  };
  return { el, node, onRun, onSelect, refresh };
}

const buttonByText = (el: Element, t: string) => [...el.querySelectorAll<HTMLButtonElement>("button")].find((b) => text(b) === t);
const sectionByTitle = (el: Element, t: string) => [...el.querySelectorAll(".detail-section")].find((s) => text(s.querySelector("h3")) === t);

describe("<lc-detail-pane> — nothing selected", () => {
  it("shows the run overview", async () => {
    const { el } = await pane(undefined, {});
    expect(el.classList.contains("detail-pane")).toBe(true);
    expect(el.querySelector("lc-run-overview")).not.toBeNull();
  });
});

describe("<lc-detail-pane> — a test", () => {
  beforeEach(async () => {
    await resetStore();
  });

  it("header: breadcrumb, eyebrow, title, fully qualified name, actions", async () => {
    const t = testNode({ id: T1, outcome: "passed", durationMs: 12, filePath: "/src/Tests/ClassA.cs", line: 5, traits: [{ key: "Category", value: "Fast" }] });
    const { el, onRun, onSelect } = await pane(tKey(t), { tests: [t] });
    const crumbs = [...el.querySelectorAll<HTMLButtonElement>(".breadcrumb .crumb-btn")];
    expect(el.querySelector(".breadcrumb")!.getAttribute("aria-label")).toBe("Location");
    expect(crumbs.map((c) => text(c))).toEqual(["Project.One", "Ns", "ClassA"]);
    crumbs[2].click();
    expect(onSelect).toHaveBeenCalledWith(nodeOf(tKey(t)).parent!.key);
    expect(text(el.querySelector(".kind-label"))).toBe("Test");
    expect(text(el.querySelector(".detail-title"))).toBe("Test1");
    expect(text(el.querySelector(".fqn code"))).toBe("Ns.ClassA.Test1");
    expect(el.querySelector(".fqn .lc-copy")!.getAttribute("aria-label")).toBe("Copy fully qualified name");

    const run = buttonByText(el, "Run this test")!;
    expect(run.getAttribute("aria-disabled")).toBe("false");
    run.click();
    expect(onRun.mock.calls[0][0].key).toBe(tKey(t));
    expect(buttonByText(el, "Debug")).toBeDefined();
    expect(buttonByText(el, `Run ${REPEAT_COUNT}×`)).toBeDefined();
    expect(text(el.querySelector(".detail-actions a.btn"))).toBe("VS Code");

    // meta line
    const meta = el.querySelector(".meta-line")!;
    expect(text(meta)).toContain("12 ms");
    expect(text(meta)).toContain("Never run");
    expect(text(meta.querySelector(".project-tag"))).toBe("P1");
    expect(text(meta.querySelector("a.meta-item"))).toBe("ClassA.cs:5");
    expect(meta.querySelector("a.meta-item")!.getAttribute("title")).toBe("/src/Tests/ClassA.cs — open in VS Code");
    expect(text(meta.querySelector(".trait"))).toBe("CategoryFast");
    expect(text(el.querySelector(".detail-note"))).toBe("Passed with no output.");
    // a source section with the inline code view
    expect(el.querySelector("lc-code-view")).not.toBeNull();
  });

  it("run actions are soft-disabled while a run is in flight or offline", async () => {
    const t = testNode({ id: T1, outcome: "notRun" });
    const { el, onRun } = await pane(tKey(t), { tests: [t] }, { open: false });
    const run = buttonByText(el, "Run this test")!;
    expect(run.getAttribute("aria-disabled")).toBe("true");
    run.click();
    expect(onRun).not.toHaveBeenCalled();
    // no debug while disconnected
    expect(buttonByText(el, "Debug")).toBeUndefined();
    expect(text(el.querySelector(".detail-note"))).toBe("This test hasn't produced a result yet. Run it to see output here.");
    expect(text(el.querySelector(".meta-line"))).toContain("—");
  });

  it("Debug starts a debug session for this test", async () => {
    const t = testNode({ id: T1, outcome: "passed" });
    const { el } = await pane(tKey(t), { tests: [t] });
    buttonByText(el, "Debug")!.click();
    expect((getStore().transport as unknown as { sent: unknown[] }).sent).toEqual([{ type: "debug", projectId: "P1", testIds: [T1] }]);
  });

  it(`Run ${REPEAT_COUNT}× sends a repeat run; Quarantine toggles the quarantine`, async () => {
    const t = testNode({ id: T1, outcome: "passed" });
    const { el, refresh } = await pane(tKey(t), { tests: [t] });
    const sent = (getStore().transport as unknown as { sent: unknown[] }).sent;
    buttonByText(el, `Run ${REPEAT_COUNT}×`)!.click();
    expect(sent).toEqual([{ type: "run", projectId: "P1", testIds: [T1], repeat: REPEAT_COUNT }]);
    const q = el.querySelector<HTMLButtonElement>(".btn-quarantine")!;
    expect(text(q)).toBe("Quarantine");
    expect(q.getAttribute("aria-pressed")).toBe("false");
    q.click();
    expect(sent[1]).toEqual({ type: "setQuarantine", projectId: "P1", id: T1, quarantined: true });
    getStore().apply({ type: "settingsChanged", settings: { watch: false, coverageEnabled: false, tracingEnabled: false, quarantined: [testKey(t)] } });
    await refresh();
    expect(text(q)).toBe("Release");
    expect(q.getAttribute("aria-pressed")).toBe("true");
    expect(el.querySelector(".badge-quarantined")).not.toBeNull();
    q.click();
    expect(sent[2]).toEqual({ type: "setQuarantine", projectId: "P1", id: T1, quarantined: false });
  });

  it("flaky and slow badges", async () => {
    const history = [10, 12, 11].map((d, i) => ({ outcome: "passed" as const, durationMs: d, at: `2026-10-0${i + 1}T10:00:00Z`, runId: `r${i}` }));
    const t = testNode({ id: T1, outcome: "passed", flaky: true, durationMs: 50, history });
    expect(slowFactor(t)).toBeCloseTo(50 / 11);
    const { el } = await pane(tKey(t), { tests: [t] });
    expect(text(el.querySelector(".badge-flaky"))).toBe("Flaky");
    expect(text(el.querySelector(".badge-slow"))).toBe("Slow · 4.5×");
    // the history strip
    const strip = el.querySelector("lc-history-strip")!;
    expect(strip.querySelectorAll(".hsq")).toHaveLength(3);
  });

  it("picking a run in the history strip shows that run", async () => {
    const history = [0, 1].map((i) => ({ outcome: "passed" as const, durationMs: 5, at: `2026-10-0${i + 1}T10:00:00Z`, runId: `r${i}` }));
    const t = testNode({ id: T1, outcome: "passed", history });
    const { el } = await pane(tKey(t), { tests: [t] });
    const spy = vi.spyOn(getStore(), "viewRun");
    el.querySelectorAll<HTMLButtonElement>(".hsq")[0].click();
    expect(spy).toHaveBeenCalledWith("r0", { startedAt: history[0].at });
  });

  it("partial runs are left out of the history unless the setting includes them", async () => {
    const history = [0, 1, 2].map((i) => ({ outcome: "passed" as const, durationMs: 5, at: `2026-10-0${i + 1}T10:00:00Z`, runId: `r${i}` }));
    const t = testNode({ id: T1, outcome: "passed", history });
    const { el } = await pane(tKey(t), { tests: [t], runs: [runSummary({ runId: "r1", scope: "subset" }), runSummary({ runId: "r0" }), runSummary({ runId: "r2" })] });
    expect(el.querySelectorAll(".hsq")).toHaveLength(2);
    setSetting("partialRunsInHistory", true);
    await flushMicrotasks();
    expect(el.querySelectorAll(".hsq")).toHaveLength(3);
    setSetting("partialRunsInHistory", false);
  });

  it("a failure: error box with the short exception name, message diff, copy error", async () => {
    const t = testNode({
      id: T1,
      outcome: "failed",
      errorType: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
      errorMessage: "Assert.AreEqual failed. Expected:<1>. Actual:<2>.",
      stackTrace: "   at Ns.ClassA.Test1() in /src/Tests/ClassA.cs:line 7",
    });
    const { el } = await pane(tKey(t), { tests: [t] });
    const box = el.querySelector(".detail-error")!;
    expect(box.classList.contains("mb-error")).toBe(true);
    expect(box.getAttribute("role")).toBe("group");
    expect(box.getAttribute("aria-label")).toBe("Failure");
    expect(text(box.querySelector(".mb-title"))).toBe("AssertFailedException");
    expect(box.querySelector(".mb-title")!.getAttribute("title")).toBe(t.errorType);
    expect(box.querySelector(".diff-block")).not.toBeNull();
    box.querySelector<HTMLButtonElement>(".lc-copy[aria-label='Copy error']")!.click();
    await flushMicrotasks();
    expect(clipboard.text).toBe([t.errorType, t.errorMessage, t.stackTrace].join("\n"));
    // no "no output" note when there is an error
    expect(el.querySelector(".detail-note")).toBeNull();
  });

  it("a failure without an exception type is titled by its outcome", async () => {
    const t = testNode({ id: T1, outcome: "timedOut", errorMessage: "took too long" });
    const { el } = await pane(tKey(t), { tests: [t] });
    expect(text(el.querySelector(".detail-error .mb-title"))).toBe("Timed out");
  });

  it("skipped and informational messages", async () => {
    const s = testNode({ id: T1, outcome: "skipped", errorMessage: "Ignored: flaky infra" });
    const p = testNode({ id: "Ns.ClassA.Test2", outcome: "passed", errorMessage: "Inconclusive-ish note" });
    const { el, node } = await pane(tKey(s), { tests: [s, p] });
    const box = el.querySelector(".detail-error")!;
    expect(box.classList.contains("mb-warning")).toBe(true);
    expect(box.getAttribute("aria-label")).toBe("Skipped");
    expect(text(box.querySelector("pre"))).toBe("Ignored: flaky infra");
    node.set(nodeOf(tKey(p)));
    await flushMicrotasks();
    const info = el.querySelector(".detail-error")!;
    expect(info.classList.contains("mb-info")).toBe(true);
    expect(text(info.querySelector(".mb-title"))).toBe("Message");
  });

  it("stack trace: my code frames, framework frames folded, show all, open a frame in the code view", async () => {
    const stack = [
      "   at System.Assert.Fail() in /_/src/Assert.cs:line 10",
      "   at System.Other()",
      "   at Ns.ClassA.Helper() in /repo/src/Helper.cs:line 20",
      "   at Ns.ClassA.Test1() in /repo/tests/ClassA.cs:line 7",
      "--- End of stack trace ---",
    ].join("\n");
    const t = testNode({ id: T1, outcome: "failed", errorMessage: "x", stackTrace: stack, filePath: "/repo/tests/ClassA.cs", line: 5, lineEnd: 8 });
    const { el } = await pane(tKey(t), { tests: [t], sourceRoots: ["/repo"] });
    const sec = sectionByTitle(el, "Stack trace")!;
    expect(text(sec.querySelector(".trace-fold"))).toBe("… 2 framework frames");
    const frames = [...sec.querySelectorAll<HTMLButtonElement>(".frame-btn")];
    expect(frames.map((f) => text(f))).toEqual(["Helper.cs:20", "ClassA.cs:7"]);
    const all = sec.querySelector<HTMLButtonElement>(".trace-all")!;
    expect(text(all)).toBe("Show all 4 frames (2 hidden)");
    expect(all.getAttribute("aria-expanded")).toBe("false");
    all.click();
    expect(text(all)).toBe("Only my code");
    expect(sec.querySelector(".trace-fold")).toBeNull();
    // the pathmapped frame is not a link
    expect(sec.querySelector(".trace-external [title^='Path from a deterministic build']")).not.toBeNull();
    expect(sec.querySelectorAll(".frame-vscode")).toHaveLength(2);

    // the code view starts on the test file with the failure annotated at its frame
    const cv = el.querySelector("lc-code-view")!;
    expect(cv.path).toBe("/repo/tests/ClassA.cs");
    expect(cv.annotations).toEqual([{ line: 7, title: "Failed" }]);
    expect(cv.markers).toEqual([7]);
    expect(cv.range).toEqual({ from: 5, to: 8 });

    // open another file's frame → the code view follows; "Test source" goes back
    sec.querySelectorAll<HTMLButtonElement>(".frame-btn")[0].click();
    await flushMicrotasks();
    expect(cv.path).toBe("/repo/src/Helper.cs");
    expect(cv.focusLine).toBe(20);
    expect(cv.range).toBeUndefined();
    expect(sec.querySelectorAll(".frame-btn")[0].classList.contains("active")).toBe(true);
    const back = buttonByText(el, "Test source")!;
    back.click();
    await flushMicrotasks();
    expect(cv.path).toBe("/repo/tests/ClassA.cs");
    expect(buttonByText(el, "Test source")).toBeUndefined();
  });

  it("a stack without my code shows every line; copy copies the trace", async () => {
    const stack = "   at System.X() in /_/src/X.cs:line 1\n   at System.Y()";
    const t = testNode({ id: T1, outcome: "failed", errorMessage: "x", stackTrace: stack });
    const { el } = await pane(tKey(t), { tests: [t], sourceRoots: ["/repo"] });
    const sec = sectionByTitle(el, "Stack trace")!;
    expect(sec.querySelectorAll(".trace-line")).toHaveLength(2);
    expect(sec.querySelector(".trace-all")).toBeNull();
    sec.querySelector<HTMLButtonElement>(".lc-copy")!.click();
    await flushMicrotasks();
    expect(clipboard.text).toBe(stack);
  });

  it("a timeout without frames in the file annotates the method's last line", async () => {
    const t = testNode({ id: T1, outcome: "timedOut", errorMessage: "Timeout", filePath: "/repo/tests/ClassA.cs", line: 5, lineEnd: 9 });
    const { el } = await pane(tKey(t), { tests: [t] });
    expect(el.querySelector("lc-code-view")!.annotations).toEqual([{ line: 9, title: "Timed out" }]);
  });

  it("sections collapse and expand", async () => {
    const t = testNode({ id: T1, outcome: "failed", errorMessage: "x", stackTrace: "   at A.B() in /a.cs:line 1" });
    const { el } = await pane(tKey(t), { tests: [t] });
    const toggle = sectionByTitle(el, "Stack trace")!.querySelector<HTMLButtonElement>(".section-toggle")!;
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    toggle.click();
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(sectionByTitle(el, "Stack trace")!.querySelector(".lc-collapsible")!.classList.contains("open")).toBe(false);
  });

  it("source: a coverage chip toggles the overlay when the last report covers the file", async () => {
    const path = "/src/Lib/Calc.cs";
    const t = testNode({ id: T1, outcome: "passed", filePath: path, line: 1 });
    const { el } = await pane(tKey(t), { tests: [t], coverage: coverageReport({ files: [fileCoverage({ path })] }) });
    const chip = el.querySelector<HTMLButtonElement>(".toggle-chip")!;
    expect(text(chip)).toBe("Coverage 75%");
    expect(chip.getAttribute("aria-pressed")).toBe("false");
    const cv = el.querySelector("lc-code-view")!;
    expect(cv.showCoverage).toBe(false);
    chip.click();
    expect(chip.getAttribute("aria-pressed")).toBe("true");
    expect(cv.showCoverage).toBe(true);
    expect(cv.coverage?.path).toBe(path);
  });

  it("console output: headers trimmed, stderr marked, line numbers, copy", async () => {
    const t = testNode({ id: T1, outcome: "passed", stdOut: "Standard Output Messages:\n\nhello\n\n\nworld\n\n", stdErr: "oops" });
    const { el } = await pane(tKey(t), { tests: [t] });
    const sec = sectionByTitle(el, "Console output")!;
    const lines = [...sec.querySelectorAll(".console-line")];
    expect(lines.map((l) => text(l.querySelector(".console-text")))).toEqual(["hello", "", "world", "oops"]);
    expect(text(lines[0].querySelector(".console-ln"))).toBe("1");
    expect(lines[3].classList.contains("stderr")).toBe(true);
    expect(text(lines[3].querySelector(".console-stream"))).toBe("stderr");
    sec.querySelector<HTMLButtonElement>(".lc-copy")!.click();
    await flushMicrotasks();
    expect(clipboard.text).toBe("hello\n\nworld\noops");
    expect(el.querySelector(".detail-note")).toBeNull();
  });

  it("truncated output: 'Show full output' loads the untruncated text from the host", async () => {
    const t = testNode({ id: T1, outcome: "passed", stdOut: "cut", outputTruncated: true, lastRunAt: "2026-10-01T10:00:00Z" });
    const { el } = await pane(tKey(t), { tests: [t] });
    const fetchMock = vi.fn(async () => jsonResponse({ stdOut: "the whole thing", truncated: false }));
    vi.stubGlobal("fetch", fetchMock);
    expect(text(el.querySelector(".output-truncated"))).toMatch(/Output truncated/);
    click(el.querySelector<HTMLButtonElement>(".output-truncated button")!);
    await flushMicrotasks();
    expect(String((fetchMock.mock.calls[0] as unknown[])[0])).toContain("/api/tests/output?projectId=P1&id=Ns.ClassA.Test1");
    const sec = sectionByTitle(el, "Console output")!;
    expect(text(sec.querySelector(".console-text"))).toBe("the whole thing");
    expect(el.querySelector(".output-truncated")).toBeNull();
  });

  it("truncated output the host no longer keeps says so", async () => {
    const t = testNode({ id: T1, outcome: "passed", stdOut: "cut", outputTruncated: true });
    const { el } = await pane(tKey(t), { tests: [t] });
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({}, { status: 404 })));
    const btn = el.querySelector<HTMLButtonElement>(".output-truncated button")!;
    click(btn);
    await flushMicrotasks();
    expect(btn.disabled).toBe(false);
    expect(text(btn)).toBe("The full output is no longer kept by the host");
  });

  it("lean mode: the selected test's output is fetched on demand", async () => {
    const t = testNode({ id: T1, outcome: "passed", hasOutput: true, lastRunAt: "2026-10-01T10:00:00Z" });
    const fetchMock = vi.fn(async (url: unknown) => {
      if (String(url).includes("/api/tests/history")) return jsonResponse({ tests: [] });
      return jsonResponse({ ...t, stdOut: "lazy output" });
    });
    vi.stubGlobal("fetch", fetchMock);
    const { el, refresh } = await pane(tKey(t), { tests: [t], lean: true });
    expect(text(el.querySelector(".detail-note"))).toBe("Loading console output…");
    await flushMicrotasks();
    await refresh();
    expect(fetchMock.mock.calls.some((c) => String(c[0]).includes("/api/tests/details?projectId=P1&id=Ns.ClassA.Test1"))).toBe(true);
    expect(text(sectionByTitle(el, "Console output")!.querySelector(".console-text"))).toBe("lazy output");
  });

  it("state bars: previous session (with Run now), outdated build", async () => {
    const t = testNode({ id: T1, outcome: "passed", stale: true, lastRunAt: "2026-10-01T10:00:00Z" });
    const { el, onRun } = await pane(tKey(t), { tests: [t] });
    const bar = el.querySelector(".mb-stale")!;
    expect(bar.getAttribute("role")).toBe("status");
    expect(text(bar.querySelector(".mb-title"))).toBe("Result from a previous session");
    expect(text(bar.querySelector(".mb-stale-text"))).toMatch(/— run the test to refresh it\.$/);
    buttonByText(bar, "Run now")!.click();
    expect(onRun).toHaveBeenCalled();

    const o = testNode({ id: T1, outcome: "passed" });
    const { el: el2 } = await pane(tKey(o), { tests: [o], projects: [project({ resultsOutdated: true })] });
    expect(text(el2.querySelector(".mb-warning .mb-title"))).toBe("Outdated result");
  });

  it("viewing a past run: 'Not in this run' for a test that was not part of it", async () => {
    const t = testNode({ id: T1, outcome: "passed" });
    await resetStore({ tests: [t] });
    const store = getStore();
    const run = runSummary({ runId: "old", startedAt: "2026-10-01T08:00:00Z" });
    (store.transport.fetchRun as ReturnType<typeof vi.fn>).mockResolvedValue({ run, results: [] });
    store.viewRun("old");
    await flushMicrotasks();
    await flushFrame();
    const { el } = await pane(tKey(t));
    // the viewed snapshot's test: notInRun (the shell sets it); emulate with a node whose test is flagged
    const n = nodeOf(tKey(t));
    const flagged: TreeNode = { ...n, test: { ...n.test!, notInRun: true, lastRunAt: undefined } };
    (el as unknown as { node: TreeNode }).node = flagged;
    await flushMicrotasks();
    const bar = el.querySelector(".mb-snapshot")!;
    expect(text(bar.querySelector(".mb-title"))).toBe("Not in this run");
    expect(text(el.querySelector(".meta-line"))).toContain("Not in this run");
    store.viewRun(undefined);
  });

  it("repeat results of the latest repeat run (pass rate, spread, flaky)", async () => {
    const t = testNode({ id: T1, outcome: "failed", errorMessage: "x", history: [{ outcome: "failed", at: "2026-10-01T10:00:00Z", runId: "rep", flaky: true }] });
    const run: RunSummary = runSummary({
      runId: "rep",
      repeat: { count: 4, completed: 4, perTest: [{ projectId: "P1", id: T1, passed: 3, failed: 1, skipped: 1, durationsMs: [10, 40, 20, 30] }] },
    });
    const { el } = await pane(tKey(t), { tests: [t], lastRun: run });
    const card = el.querySelector(".repeat-card")!;
    expect(card.classList.contains("repeat-flaky")).toBe(true);
    expect(card.getAttribute("aria-label")).toBe("Repeat run results");
    expect(text(card.querySelector(".repeat-title"))).toBe("Ran 4×");
    expect(card.querySelector(".badge-flaky")).not.toBeNull();
    expect([...card.querySelectorAll(".repeat-stat .stat-value")].map((v) => text(v))).toEqual(["75%", "3", "1", "1"]);
    expect(card.querySelector(".repeat-rate")!.getAttribute("aria-label")).toBe("3 passed, 1 failed");
    expect(card.querySelectorAll(".repeat-dot")).toHaveLength(4);
    expect([...card.querySelectorAll(".repeat-quantiles span")].map((x) => text(x))).toEqual(["min 10 ms", "median 25 ms", "p95 39 ms", "max 40 ms"]);
  });

  it("a cancelled repeat run says how many repetitions ran; all-passed and all-failed tones", async () => {
    const mk = (passed: number, failed: number, completed: number): RunSummary =>
      runSummary({ runId: "rep", repeat: { count: 5, completed, perTest: [{ projectId: "P1", id: T1, passed, failed, skipped: 0, durationsMs: [7, 7] }] } });
    const t = testNode({ id: T1, outcome: "passed" });
    const { el } = await pane(tKey(t), { tests: [t], lastRun: mk(2, 0, 2) });
    expect(text(el.querySelector(".repeat-title"))).toBe("Ran 2 of 5 repetitions (cancelled)");
    expect(el.querySelector(".repeat-card")!.classList.contains("repeat-passed")).toBe(true);
    const { el: el2 } = await pane(tKey(t), { tests: [t], runs: [mk(0, 0, 5)] });
    expect(el2.querySelector(".repeat-card")!.classList.contains("repeat-passed")).toBe(true);
    expect(text(el2.querySelector(".repeat-stat .stat-value"))).toBe("0%");
    const { el: el3 } = await pane(tKey(t), { tests: [t], runs: [{ ...mk(0, 2, 5), repeat: { count: 5, completed: 5, perTest: [{ projectId: "P1", id: T1, passed: 0, failed: 2, skipped: 0, durationsMs: [] }] } }] });
    expect(el3.querySelector(".repeat-card")!.classList.contains("repeat-failed")).toBe(true);
    expect(el3.querySelector(".repeat-spread")).toBeNull();
  });

  it("an older repeat run is not shown once the test has a newer result", async () => {
    const t = testNode({ id: T1, outcome: "passed", history: [{ outcome: "passed", at: "x", runId: "newer" }] });
    const run = runSummary({ runId: "rep", repeat: { count: 2, completed: 2, perTest: [{ projectId: "P1", id: T1, passed: 2, failed: 0, skipped: 0, durationsMs: [1] }] } });
    const { el } = await pane(tKey(t), { tests: [t], lastRun: run });
    expect(el.querySelector(".repeat-card")).toBeNull();
  });

  it("a repeat run in progress shows its repetition", async () => {
    const t = testNode({ id: T1, outcome: "passed" });
    const { el } = await pane(tKey(t), { tests: [t] });
    const store = getStore();
    const run = runSummary({ runId: "rep", startedAt: "2026-10-01T10:00:00Z", repeat: { count: 20, completed: 0, perTest: [] } });
    store.apply({ type: "runStarted", run, testIds: [T1] });
    store.apply({ type: "log", level: "info", message: "Repeat 3/20", timestamp: "2026-10-01T10:00:01Z" });
    await flushFrame();
    const card = el.querySelector(".repeat-card.running")!;
    expect(card.getAttribute("role")).toBe("status");
    expect(text(card)).toBe("Running 20× · repetition 3/20");
    store.apply({ type: "runFinished", run: { ...run, finishedAt: "x" } });
    await flushFrame();
  });

  it("a running test waits for its result", async () => {
    const t = testNode({ id: T1, outcome: "running" });
    const { el } = await pane(tKey(t), { tests: [t] });
    // LIVE OUTPUT: the live console takes over the note while the test runs
    expect(el.querySelector(".detail-note")).toBeNull();
    expect(text(el.querySelector(".live-section .console-empty"))).toBe("Waiting for output…");
  });

  it("a data-row test case is labelled 'Test case'", async () => {
    const a = testNode({ id: "Ns.ClassA.Rows#0", methodName: "Rows", displayName: "Rows (1)", outcome: "skipped" });
    const b = testNode({ id: "Ns.ClassA.Rows#1", methodName: "Rows", displayName: "Rows (2)", outcome: "skipped" });
    const { el } = await pane(tKey(a), { tests: [a, b] });
    expect(text(el.querySelector(".kind-label"))).toBe("Test case");
    expect(text(el.querySelector(".detail-note"))).toBe("No additional output.");
  });

  it("a new selection fades in (not on first paint, not while scrubbing)", async () => {
    vi.useFakeTimers({ toFake: ["performance"] });
    const a = testNode({ id: T1, outcome: "passed" });
    const b = testNode({ id: "Ns.ClassA.Test2", outcome: "passed" });
    const { el, node } = await pane(tKey(a), { tests: [a, b] });
    expect(el.querySelector(".detail-scroll")!.classList.contains("lc-enter")).toBe(false);
    vi.advanceTimersByTime(500);
    node.set(nodeOf(tKey(b)));
    expect(el.querySelector(".detail-scroll")!.classList.contains("lc-enter")).toBe(true);
    // quick re-selection (scrubbing): instant
    vi.advanceTimersByTime(50);
    node.set(nodeOf(tKey(a)));
    expect(el.querySelector(".detail-scroll")!.classList.contains("lc-enter")).toBe(false);
    vi.advanceTimersByTime(500);
    node.set(undefined);
    await flushMicrotasks();
    // the overview host is display: contents: its root element animates
    expect(el.querySelector("lc-run-overview > .lc-enter")).not.toBeNull();
  });

  it("F8 / Shift+F8 (and requestFailureNav) select the next / previous failure", async () => {
    const f1 = testNode({ id: "Ns.ClassA.F1", outcome: "failed" });
    const f2 = testNode({ id: "Ns.ClassA.F2", outcome: "failed" });
    const { onSelect } = await pane(tKey(f1), { tests: [f1, f2] });
    key(window, "F8");
    expect(onSelect).toHaveBeenLastCalledWith(tKey(f2));
    key(window, "F8", { shiftKey: true });
    expect(onSelect).toHaveBeenLastCalledWith(tKey(f2));
    requestFailureNav(1);
    expect(onSelect).toHaveBeenCalledTimes(3);
    // modified / already handled F8 is ignored
    key(window, "F8", { ctrlKey: true });
    const handled = new KeyboardEvent("keydown", { key: "F8", cancelable: true });
    handled.preventDefault();
    window.dispatchEvent(handled);
    key(window, "F7");
    expect(onSelect).toHaveBeenCalledTimes(3);
  });

  it("aspire: trace links in the header and the failure context under the error", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ traceId: "tr", spans: [], logs: [] })));
    const t = testNode({ id: T1, outcome: "failed", errorMessage: "boom", traceId: "tr" });
    const { el } = await pane(tKey(t), { tests: [t], tracingEnabled: true, aspireDashboardUrl: "http://dash" });
    expect([...el.querySelectorAll(".detail-actions a.btn")].map((a) => text(a))).toEqual(["View trace", "Logs"]);
    expect(el.querySelector(".detail-error .aspire-context")).not.toBeNull();
  });
});

describe("<lc-detail-pane> — a group", () => {
  const fail = testNode({ id: "Ns.ClassA.Fail", outcome: "failed", errorMessage: "bad\nmore", durationMs: 40 });
  const pass = testNode({ id: "Ns.ClassA.Pass", outcome: "passed", durationMs: 10 });
  const skip = testNode({ id: "Ns.ClassA.Skip", outcome: "skipped" });
  const never = testNode({ id: "Ns.ClassA.Never" });

  beforeEach(async () => {
    await resetStore();
  });

  it("a class: stat cards, distribution, failures and slowest tests; run actions", async () => {
    await resetStore({ tests: [fail, pass, skip, never] });
    const classKey = nodeOf(tKey(fail)).parent!.key;
    const { el, onRun, onSelect } = await pane(classKey);
    expect(text(el.querySelector(".kind-label"))).toBe("Test class");
    expect(text(el.querySelector(".detail-title"))).toBe("ClassA");
    const cards = [...el.querySelectorAll(".stat-card")].map((c) => text(c.querySelector(".stat-label")));
    expect(cards).toEqual(["Total", "Passed", "Failed", "Skipped", "Not run", "Duration"]);
    expect(el.querySelector(".dist-bar")).not.toBeNull();
    const failures = sectionByTitle(el, "Failures (1)")!;
    const row = failures.querySelector<HTMLButtonElement>(".failure-item")!;
    expect(text(row.querySelector(".failure-msg"))).toBe("bad");
    row.click();
    expect(onSelect).toHaveBeenCalledWith(tKey(fail));
    const slowest = sectionByTitle(el, "Slowest tests")!;
    expect([...slowest.querySelectorAll(".failure-name")].map((n) => text(n))).toEqual(["ClassA.Fail", "ClassA.Pass"]);

    buttonByText(el, "Run 4 tests")!.click();
    expect(onRun.mock.calls[0][0].key).toBe(classKey);
    expect(onRun.mock.calls[0][1]).toBeUndefined();
    buttonByText(el, "Run failed (1)")!.click();
    expect(onRun.mock.calls[1][0].key).toBe(classKey);
    expect(onRun.mock.calls[1][1].map((t: TestNode) => t.id)).toEqual([fail.id]);
    // no project-level actions for a class
    expect(buttonByText(el, "Rediscover")).toBeUndefined();
  });

  it("a single-test group says 'Run 1 test'; durations show on the Duration card", async () => {
    await resetStore({ tests: [pass] });
    const { el } = await pane(nodeOf(tKey(pass)).parent!.key);
    expect(buttonByText(el, "Run 1 test")).toBeDefined();
    const cards = [...el.querySelectorAll(".stat-card")];
    expect(text(cards[cards.length - 1].querySelector(".stat-value"))).toBe("10 ms");
  });

  it("a project: path + copy, rediscover, build failure bar and build output", async () => {
    const p = project({ status: "error", lastError: "CS1002: ; expected", buildOutput: "Build started\nerror CS1002: ; expected\nwarning CS0168: unused" });
    await resetStore({ projects: [p], tests: [pass] });
    const projKey = nodeOf(tKey(pass)).parent!.parent!.parent!.key;
    const { el } = await pane(projKey);
    expect(text(el.querySelector(".kind-label"))).toBe("Project");
    expect(text(el.querySelector(".fqn code"))).toBe(p.path);
    const bar = el.querySelector(".messagebar.mb-error")!;
    expect(bar.getAttribute("aria-label")).toBe("Build failed");
    expect(text(bar.querySelector(".mb-message"))).toBe("CS1002: ; expected");
    const out = sectionByTitle(el, "Build output")!;
    const lines = [...out.querySelectorAll(".console-line")];
    expect(lines.map((l) => l.className)).toEqual(["console-line", "console-line stderr", "console-line warn"]);
    // project in error: Run is soft-disabled
    expect(buttonByText(el, "Run 1 test")!.getAttribute("aria-disabled")).toBe("true");
    buttonByText(el, "Rediscover")!.click();
    expect((getStore().transport as unknown as { sent: unknown[] }).sent).toEqual([{ type: "discover", projectId: "P1" }]);
  });

  it("a failed build without a message falls back to a generic one", async () => {
    await resetStore({ projects: [project({ status: "error" })], tests: [pass] });
    const { el } = await pane(nodeOf(tKey(pass)).parent!.parent!.parent!.key);
    expect(text(el.querySelector(".mb-error .mb-message"))).toMatch(/could not be built/);
  });

  it("target framework: a badge for one, a picker for several", async () => {
    await resetStore({ projects: [project({ targetFramework: "net10.0" })], tests: [pass] });
    const projKey = nodeOf(tKey(pass)).parent!.parent!.parent!.key;
    const { el } = await pane(projKey);
    expect(text(el.querySelector(".tfm-badge"))).toBe("net10.0");

    await resetStore({ projects: [project({ targetFramework: "net9.0", availableFrameworks: ["net8.0", "net9.0"] })], tests: [pass] });
    const { el: el2 } = await pane(projKey);
    const select = el2.querySelector<HTMLSelectElement>(".tfm-select")!;
    expect([...select.options].map((o) => o.value)).toEqual(["net8.0", "net9.0"]);
    expect(select.disabled).toBe(false);
    select.value = "net8.0";
    select.dispatchEvent(new Event("change"));
    expect((getStore().transport as unknown as { sent: unknown[] }).sent).toEqual([{ type: "setTargetFramework", projectId: "P1", targetFramework: "net8.0" }]);
  });

  it("a project without tests shows the discovery diagnostics instead of a Run button", async () => {
    await resetStore({ projects: [project({ status: "idle", diagnostics: [{ level: "warning", message: "No tests" }] })], tests: [] });
    const roots = buildTree(getStore().get().projects, getStore().get().tests);
    const { el } = await pane(roots[0].key);
    expect(el.querySelector("lc-project-diagnostics")).not.toBeNull();
    expect(buttonByText(el, "Run 0 tests")).toBeUndefined();
  });

  it("a project still building with no tests says none were discovered", async () => {
    await resetStore({ projects: [project({ status: "building" })], tests: [] });
    const roots = buildTree(getStore().get().projects, getStore().get().tests);
    const { el } = await pane(roots[0].key);
    expect(text(el.querySelector(".detail-note"))).toBe("No tests discovered in this project.");
    expect(buttonByText(el, "Rediscover")!.disabled).toBe(false);
  });

  it("a data-driven method: no 'Run failed', no slowest list", async () => {
    const a = testNode({ id: "Ns.ClassA.Rows#0", methodName: "Rows", displayName: "Rows (1)", outcome: "failed", durationMs: 3 });
    const b = testNode({ id: "Ns.ClassA.Rows#1", methodName: "Rows", displayName: "Rows (2)", outcome: "passed", durationMs: 2 });
    await resetStore({ tests: [a, b] });
    const { el } = await pane(nodeOf(tKey(a)).parent!.key);
    expect(text(el.querySelector(".kind-label"))).toBe("Data-driven test");
    expect(buttonByText(el, "Run failed (1)")).toBeUndefined();
    expect(sectionByTitle(el, "Slowest tests")).toBeUndefined();
    expect(sectionByTitle(el, "Failures (1)")).toBeDefined();
  });
});

describe("pure helpers", () => {
  it("shortTypeName drops the namespace (generic arguments kept)", () => {
    expect(shortTypeName(undefined)).toBe("");
    expect(shortTypeName("Plain")).toBe("Plain");
    expect(shortTypeName("A.B.AssertFailedException")).toBe("AssertFailedException");
    expect(shortTypeName("Sys.Wrapper`1[Sys.Inner]")).toBe("Wrapper`1[Sys.Inner]");
  });

  it("trimOutput drops MSTest headers and extra blank lines", () => {
    expect(trimOutput(undefined)).toBe("");
    expect(trimOutput("\r\nTestContext Messages:\r\n\r\na\r\n\r\n\r\nb\r\n\r\n")).toBe("a\n\nb");
  });

  it("soft() guards the click while disabled", () => {
    const on = vi.fn();
    const off = signal(true);
    const p = soft(off, on);
    expect(p["aria-disabled"]()).toBe(true);
    p.onclick();
    expect(on).not.toHaveBeenCalled();
    off.set(false);
    p.onclick();
    expect(on).toHaveBeenCalledTimes(1);
  });
});
