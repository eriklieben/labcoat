import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { historyDiff, type WatchStrip } from "../../../src/components/watch-strip";
import { onStatusFilterRequest, type StatusFilter } from "../../../src/tree";
import type { Store } from "../../../src/store";
import type { HistoryEntry, RunSummary, TestNode } from "../../../src/contract";
import { flushFrame, mount, project, runSummary, testNode, text } from "../../helpers/app";
import { flushMicrotasks } from "../../helpers/dom";
import { freshStore } from "../../helpers/app6";

let store: Store;

beforeEach(async () => {
  ({ store } = await freshStore());
});
afterEach(async () => {
  await flushFrame();
});

const he = (outcome: HistoryEntry["outcome"], runId: string): HistoryEntry => ({ outcome, runId, at: `2026-10-09T10:0${runId.slice(-1)}:00Z` });
const t = (id: string, outcome: TestNode["outcome"], history: HistoryEntry[] = []) => testNode({ id: `Ns.C.${id}`, displayName: id, outcome, history });

const watchRun = (p: Partial<RunSummary> = {}) =>
  runSummary({ runId: "w2", trigger: "watch", total: 3, watch: { changedFiles: ["/src/Orders.cs", "/src/Other.cs"], buildMs: 1200, strategy: "impactMap" }, ...p });

function strip() {
  const el = mount(document.createElement("lc-watch-strip") as WatchStrip);
  return { el, steps: () => [...el.querySelectorAll(".ws-step")].map((s) => text(s)) };
}

describe("historyDiff", () => {
  it("splits a run's tests into fixed, new failures and still failing", () => {
    const tests = new Map(
      [
        t("fixed", "passed", [he("failed", "w1"), he("passed", "w2")]),
        t("new", "failed", [he("passed", "w1"), he("failed", "w2")]),
        t("brandNew", "failed", [he("failed", "w2")]),
        t("still", "failed", [he("failed", "w1"), he("failed", "w2")]),
        t("green", "passed", [he("passed", "w1"), he("passed", "w2")]),
        t("notInRun", "failed", [he("failed", "w1")]),
        t("noHistory", "failed"),
      ].map((x) => [`P1|${x.id}`, x]),
    );
    const d = historyDiff(tests, "w2");
    expect(d.fixed.map((x) => x.displayName)).toEqual(["fixed"]);
    expect(d.newFailures.map((x) => x.displayName)).toEqual(["new", "brandNew"]);
    expect(d.stillFailing).toBe(1);
  });
});

describe("<lc-watch-strip>", () => {
  it("is a labelled group; idle text while watching", () => {
    const { el, steps } = strip();
    expect(el.classList.contains("watch-strip")).toBe(true);
    expect(el.getAttribute("role")).toBe("group");
    expect(el.getAttribute("aria-label")).toBe("Watch mode");
    expect(el.querySelector(".ws-icon")).not.toBeNull();
    expect(steps()).toEqual(["Watching for changes — save a file to re-run the affected tests"]);
  });

  it("says when a rebuild is running and when it failed", async () => {
    const { el, steps } = strip();
    store.apply({ type: "projectStatus", project: project({ status: "building" }) });
    await flushFrame();
    expect(steps()).toEqual(["Change detected → building…"]);
    expect(el.querySelector(".ws-step .spinner")).not.toBeNull();
    store.apply({ type: "projectStatus", project: project({ status: "error" }) });
    await flushFrame();
    expect(steps()).toEqual(["Build failed (Project.One) — fix and save to re-run"]);
    expect(el.querySelector(".ws-bad")).not.toBeNull();
  });

  it("follows a watch run: file(s) saved → build → affected → running n/N", async () => {
    store.apply({ type: "state", state: { projects: [project()], tests: [t("a", "passed"), t("b", "passed"), t("c", "passed")], watch: true, coverageEnabled: false } });
    await flushFrame();
    const { el, steps } = strip();
    await flushMicrotasks();
    store.apply({ type: "runStarted", run: watchRun(), testIds: ["Ns.C.a", "Ns.C.b", "Ns.C.c"] });
    await flushFrame();
    expect(steps()).toEqual(["Orders.cs +1 saved", "build 1.20 s", "3 affected", "running 0/3"]);
    expect(el.querySelector<HTMLElement>(".ws-file")!.title).toBe("/src/Orders.cs\n/src/Other.cs");
    expect(el.querySelector<HTMLElement>(".ws-step span[title]")!.title).toBe("Ran tests whose covered code changed (impact map)");
    expect(el.querySelectorAll(".ws-arrow")).toHaveLength(3);
    // steps that arrive after the first paint slide in
    expect(el.querySelector(".ws-file")!.classList.contains("lc-enter-x")).toBe(true);
    store.apply({ type: "testsUpdated", tests: [t("a", "passed")] });
    await flushFrame();
    expect(steps()[3]).toBe("running 1/3");
  });

  it("a finished run shows fixed / new counts that filter the tree, still failing and the time", async () => {
    store.apply({
      type: "state",
      state: { projects: [project()], tests: [t("a", "failed", [he("failed", "w1")]), t("b", "passed", [he("passed", "w1")]), t("c", "failed", [he("failed", "w1")])], watch: true, coverageEnabled: false },
    });
    await flushFrame();
    const { el, steps } = strip();
    await flushMicrotasks();
    const run = watchRun({ watch: undefined, affected: { strategy: "testFile", changedFiles: ["/src/OrdersTests.cs"] } });
    store.apply({ type: "runStarted", run, testIds: ["Ns.C.a", "Ns.C.b", "Ns.C.c"] });
    store.apply({
      type: "testsUpdated",
      tests: [t("a", "passed", [he("failed", "w1"), he("passed", "w2")]), t("b", "failed", [he("passed", "w1"), he("failed", "w2")]), t("c", "failed", [he("failed", "w1"), he("failed", "w2")])],
    });
    store.apply({ type: "runFinished", run: { ...run, finishedAt: "2026-10-09T10:05:00Z", passed: 1, failed: 2 } });
    await flushFrame();
    expect(steps()).toEqual(["OrdersTests.cs saved", "3 affected", "1 fixed, 1 new · 1 still failing"]);
    expect(el.querySelector(".ws-time")).not.toBeNull();
    const [fixed, fresh] = el.querySelectorAll<HTMLButtonElement>(".ws-count");
    expect(fixed.classList.contains("good")).toBe(true);
    expect(fixed.title).toBe("a");
    expect(fresh.classList.contains("bad")).toBe(true);
    expect(fresh.title).toBe("b");
    expect(fixed.tabIndex).toBe(-1);
    // P13: counts flash once
    expect(fixed.classList.contains("lc-flash-good")).toBe(true);
    expect(fresh.classList.contains("lc-flash-bad")).toBe(true);
    const got: StatusFilter[] = [];
    const off = onStatusFilterRequest((f) => got.push(f));
    fixed.click();
    fresh.click();
    off();
    expect(got).toEqual(["passed", "new"]);
  });

  it("a clean run: '0 fixed, 0 new', the new-count button filters failed; a new cycle fades the strip in", async () => {
    store.apply({ type: "state", state: { projects: [project()], tests: [t("a", "passed", [he("passed", "w1")])], watch: true, coverageEnabled: false } });
    await flushFrame();
    const { el, steps } = strip();
    await flushMicrotasks();
    const r1 = watchRun({ runId: "w2", total: 1, watch: { changedFiles: [], strategy: "all" } });
    store.apply({ type: "runStarted", run: r1, testIds: ["Ns.C.a"] });
    store.apply({ type: "testsUpdated", tests: [t("a", "passed", [he("passed", "w1"), he("passed", "w2")])] });
    store.apply({ type: "runFinished", run: r1 });
    await flushFrame();
    expect(steps()).toEqual(["Change detected", "1 affected", "0 fixed, 0 new"]);
    expect(el.querySelector(".ws-time")).toBeNull(); // no finishedAt
    const [fixed, fresh] = el.querySelectorAll<HTMLButtonElement>(".ws-count");
    expect(fixed.title).toBe("No test went from failing to passing");
    expect(fresh.title).toBe("No new failures");
    expect(fixed.classList.contains("lc-flash-good")).toBe(false);
    const got: StatusFilter[] = [];
    const off = onStatusFilterRequest((f) => got.push(f));
    fresh.click();
    off();
    expect(got).toEqual(["failed"]);

    el.classList.remove("lc-enter-fade");
    store.apply({ type: "runStarted", run: watchRun({ runId: "w3", total: 1 }), testIds: ["Ns.C.a"] });
    await flushFrame();
    expect(el.classList.contains("lc-enter-fade")).toBe(true);
  });

  it("ignores runs that were not started by watch", async () => {
    const { steps } = strip();
    store.apply({ type: "runStarted", run: runSummary({ runId: "u1", trigger: "user", total: 1 }), testIds: [] });
    await flushFrame();
    expect(steps()).toEqual(["Watching for changes — save a file to re-run the affected tests"]);
  });

  it("ignores Run affected (git) runs", async () => {
    const { steps } = strip();
    store.apply({ type: "runStarted", run: runSummary({ runId: "g1", trigger: "user", total: 2, affected: { strategy: "git", base: "working", changedFiles: ["/r/a.cs"] } }), testIds: [] });
    await flushFrame();
    expect(steps()).toEqual(["Watching for changes — save a file to re-run the affected tests"]);
  });
});
