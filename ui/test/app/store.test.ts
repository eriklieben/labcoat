import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CoverageReport, HistoryEntry, ServerEvent, TestNode } from "../../src/contract";
import { Store, connectionOf, editorNotice, getStore, openInEditor, serverEventListeners, setStore, testKey, type CoverageTarget } from "../../src/store";
import { setSetting, resetLocalSettings } from "../../src/settings";
import { coverageReport, explorerState, flushFrame, makeStore, project, runSummary, stateEvent, testNode } from "../helpers/app";
import { flushMicrotasks } from "../helpers/dom";

const t1 = () => testNode({ id: "Ns.ClassA.Test1" });
const t2 = () => testNode({ id: "Ns.ClassA.Test2" });
const t3 = () => testNode({ id: "Ns.ClassB.Test3", projectId: "P2" });
const k = (t: TestNode) => testKey(t);

function jsonResponse(body: unknown, status = 200) {
  return { ok: status >= 200 && status < 300, status, statusText: status === 200 ? "OK" : "Err", json: async () => body } as unknown as Response;
}

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  serverEventListeners.clear();
  editorNotice.set(undefined);
});

describe("Store basics", () => {
  it("getStore throws until a store is set", async () => {
    vi.resetModules();
    const mod = await import("../../src/store");
    expect(() => mod.getStore()).toThrow(/not initialised/);
    const s = new mod.Store("ws");
    mod.setStore(s);
    expect(mod.getStore()).toBe(s);
  });

  it("starts with an empty, not-loaded state of the given transport kind", () => {
    const s = new Store("ws");
    expect(s.get()).toMatchObject({ transport: "ws", conn: "connecting", loaded: false, everConnected: false, watch: false, logs: [], runs: [] });
    expect(s.get().tests.size).toBe(0);
  });

  it("$ returns one cached computed per field", () => {
    const s = new Store("mock");
    expect(s.$.tests).toBe(s.$.tests);
    expect(s.$.loaded()).toBe(false);
  });

  it("attach() records the transport kind and connection status (everConnected sticks)", async () => {
    const { store, transport } = makeStore({ kind: "ws", open: false });
    expect(store.get().transport).toBe("ws");
    transport.setStatus("open");
    expect(store.get()).toMatchObject({ conn: "open", everConnected: true });
    transport.setStatus("reconnecting", { retryInMs: 1500 });
    expect(store.get()).toMatchObject({ conn: "reconnecting", retryInMs: 1500, everConnected: true });
    await flushFrame();
    expect(store.$.conn()).toBe("reconnecting");
    expect(store.$.retryInMs()).toBe(1500);
  });

  it("publishes a burst of events once per animation frame; get() is ahead", async () => {
    const { store } = makeStore();
    await flushFrame();
    const published: unknown[] = [];
    const before = store.state$();
    store.apply(stateEvent({ tests: [t1()] }));
    store.apply({ type: "log", level: "info", message: "a", timestamp: "t" });
    store.apply({ type: "log", level: "info", message: "b", timestamp: "t" });
    expect(store.state$()).toBe(before);
    expect(store.get().logs).toHaveLength(2);
    published.push(store.state$());
    await flushFrame();
    expect(store.state$()).toBe(store.get());
    expect(store.$.logs()).toHaveLength(2);
    expect(store.$.tests().size).toBe(1);
  });

  it("notifies serverEventListeners after the state update, for every event", () => {
    const { store } = makeStore();
    const seen: string[] = [];
    serverEventListeners.add((e) => seen.push(`${e.type}:${store.get().loaded}`));
    store.apply(stateEvent());
    store.apply({ type: "debuggerAttached", projectId: "P1", pid: 1, runId: "r" });
    expect(seen).toEqual(["state:true", "debuggerAttached:true"]);
  });
});

describe("state snapshot", () => {
  it("loads projects, tests (keyed by project|id), run info and the Round 3/4/5 fields", () => {
    const { store } = makeStore();
    const cov = coverageReport();
    store.apply(
      stateEvent({
        projects: [project(), project({ id: "P2", name: "Two" })],
        tests: [t1(), t2(), t3()],
        lastRun: runSummary({ runId: "r0" }),
        watch: true,
        coverageEnabled: true,
        coverage: cov,
        fullCoverage: cov,
        impactMap: { files: 2, tests: 3 },
        aspireDashboardUrl: "http://dash",
        runs: [runSummary({ runId: "r0" })],
        workspaceName: "Work",
        tracingEnabled: true,
        tracingAvailable: true,
        sourceRoots: ["/src"],
        quarantined: ["P1|x"],
        editors: [{ id: "vscode", name: "VS Code", available: true }],
        telemetryAvailable: true,
        pendingRun: { runId: "q", requestedAt: "t", scope: "all", requests: 1 },
      }),
    );
    const s = store.get();
    expect(s.loaded).toBe(true);
    expect(s.projects.map((p) => p.id)).toEqual(["P1", "P2"]);
    expect([...s.tests.keys()]).toEqual(["P1|Ns.ClassA.Test1", "P1|Ns.ClassA.Test2", "P2|Ns.ClassB.Test3"]);
    expect(s).toMatchObject({
      watch: true,
      coverageEnabled: true,
      coverage: cov,
      fullCoverage: cov,
      impactMap: { files: 2, tests: 3 },
      aspireDashboardUrl: "http://dash",
      workspaceName: "Work",
      tracingEnabled: true,
      tracingAvailable: true,
      sourceRoots: ["/src"],
      quarantined: ["P1|x"],
      telemetryAvailable: true,
      pendingRun: { runId: "q" },
    });
    expect(s.lastRun?.runId).toBe("r0");
    expect(s.runs).toHaveLength(1);
    expect(s.testsVersion).toBe(1);
  });

  it("keeps the previous workspace name / runs / full coverage when a snapshot omits them", () => {
    const { store } = makeStore();
    const full = coverageReport({ runId: "full" });
    store.apply(stateEvent({ workspaceName: "W", runs: [runSummary()], coverage: full, fullCoverage: full }));
    const subset = coverageReport({ runId: "sub", scope: "subset", testIds: ["a"] });
    store.apply({ type: "state", state: { ...explorerState({ coverage: subset }), runs: undefined } });
    expect(store.get().workspaceName).toBe("W");
    expect(store.get().runs).toHaveLength(1);
    expect(store.get().coverage).toBe(subset);
    expect(store.get().fullCoverage).toBe(full);
    expect(store.get().tracingEnabled).toBe(false);
    expect(store.get().tracingAvailable).toBe(false);
  });

  it("maps the on-connect log history (seq, unseen errors) and leaves logs alone without it", () => {
    const { store } = makeStore();
    store.apply(
      stateEvent({
        logs: [
          { level: "info", message: "build", timestamp: "t1", projectId: "P1" },
          { level: "error", message: "boom", timestamp: "t2" },
          { level: "error", message: "boom2", timestamp: "t3" },
        ],
      }),
    );
    expect(store.get().logs.map((l) => [l.seq, l.message, l.projectId])).toEqual([
      [1, "build", "P1"],
      [2, "boom", undefined],
      [3, "boom2", undefined],
    ]);
    expect(store.get().unseenErrors).toBe(2);
    store.apply(stateEvent());
    expect(store.get().logs).toHaveLength(3);
    store.logPanelOpen = true;
    store.apply(stateEvent({ logs: [{ level: "error", message: "x", timestamp: "t" }] }));
    expect(store.get().unseenErrors).toBe(0);
  });

  it("mid-run snapshot: keeps pending tests' previous result instead of the queued one; records the live run", () => {
    const { store } = makeStore({ state: { tests: [{ ...t1(), outcome: "failed" }, t2()] } });
    store.apply({ type: "runStarted", run: runSummary({ runId: "r1" }), testIds: [t1().id, t2().id] });
    expect(store.get().pending?.size).toBe(2);
    store.apply(stateEvent({ tests: [{ ...t1(), outcome: "queued" }, { ...t2(), outcome: "passed" }], currentRun: runSummary({ runId: "r1" }) }));
    expect(store.get().tests.get(k(t1()))?.outcome).toBe("failed");
    expect(store.get().tests.get(k(t2()))?.outcome).toBe("passed");
    expect(store.get().runKeys?.size).toBe(2);
    expect(store.sawRunStart("r1")).toBe(true);
    expect(store.sawRunStart("other")).toBe(false);
    // a snapshot without a current run drops the run bookkeeping
    store.apply(stateEvent({ tests: [t1()] }));
    expect(store.get().pending).toBeUndefined();
    expect(store.get().runKeys).toBeUndefined();
    // a snapshot taken mid-run counts as seeing the run start
    store.apply(stateEvent({ currentRun: runSummary({ runId: "r9" }) }));
    expect(store.sawRunStart("r9")).toBe(true);
  });

  it("keeps the coverage label only while the snapshot's report is the same run", () => {
    const { store } = makeStore();
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "s", scope: "subset", testIds: ["x", "y"] }) });
    expect(store.get().coverageLabel).toEqual({ title: "2 tests" });
    store.apply(stateEvent({ coverage: coverageReport({ runId: "s", scope: "subset", testIds: ["x", "y"] }) }));
    expect(store.get().coverageLabel).toEqual({ title: "2 tests" });
    store.apply(stateEvent({ coverage: coverageReport({ runId: "other" }) }));
    expect(store.get().coverageLabel).toBeUndefined();
  });
});

describe("projectStatus", () => {
  it("replaces a known project and appends a new one", () => {
    const { store } = makeStore({ state: {} });
    store.apply({ type: "projectStatus", project: project({ status: "building" }) });
    expect(store.get().projects).toHaveLength(1);
    expect(store.get().projects[0].status).toBe("building");
    store.apply({ type: "projectStatus", project: project({ id: "P9", status: "error", buildErrors: [{ severity: "error", message: "CS1002" }] }) });
    expect(store.get().projects.map((p) => p.id)).toEqual(["P1", "P9"]);
  });
});

describe("testsUpdated (copy-on-write per frame + delta)", () => {
  it("records the changed keys and the previous map in testsDelta", () => {
    const { store } = makeStore({ state: { tests: [t1(), t2()] } });
    const before = store.get().tests;
    store.apply({ type: "testsUpdated", tests: [{ ...t1(), outcome: "passed" }] });
    const s = store.get();
    expect(s.tests).not.toBe(before);
    expect(s.testsDelta?.from).toBe(before);
    expect([...s.testsDelta!.keys]).toEqual([k(t1())]);
    expect(before.get(k(t1()))?.outcome).toBe("notRun"); // the old map is untouched
  });

  it("mutates the same draft map within a frame, and copies again after a publish", async () => {
    const { store } = makeStore({ state: { tests: [t1(), t2()] } });
    await flushFrame();
    store.apply({ type: "testsUpdated", tests: [{ ...t1(), outcome: "passed" }] });
    const draft = store.get().tests;
    const delta = store.get().testsDelta;
    store.apply({ type: "testsUpdated", tests: [{ ...t2(), outcome: "failed" }] });
    expect(store.get().tests).toBe(draft);
    expect(store.get().testsDelta).toBe(delta);
    expect([...delta!.keys].sort()).toEqual([k(t1()), k(t2())].sort());
    expect(store.get().testsVersion).toBe(3);
    await flushFrame();
    const published = store.$.tests();
    expect(published).toBe(draft);
    store.apply({ type: "testsUpdated", tests: [{ ...t1(), outcome: "failed" }] });
    expect(store.get().tests).not.toBe(published);
    expect(published.get(k(t1()))?.outcome).toBe("passed");
    expect([...store.get().testsDelta!.keys]).toEqual([k(t1())]);
  });

  it("adds tests it did not know", () => {
    const { store } = makeStore({ state: { tests: [t1()] } });
    store.apply({ type: "testsUpdated", tests: [t3()] });
    expect(store.get().tests.size).toBe(2);
  });

  it("while a test is pending, queued/notRun updates are ignored and a real result clears it (pending copied once)", () => {
    const { store } = makeStore({ state: { tests: [{ ...t1(), outcome: "failed" }, { ...t2(), outcome: "passed" }] } });
    store.apply({ type: "runStarted", run: runSummary(), testIds: [t1().id, t2().id] });
    const pending0 = store.get().pending!;
    store.apply({ type: "testsUpdated", tests: [{ ...t1(), outcome: "queued" }, { ...t2(), outcome: "notRun" }] });
    expect(store.get().tests.get(k(t1()))?.outcome).toBe("failed");
    expect(store.get().pending).toBe(pending0);
    store.apply({ type: "testsUpdated", tests: [{ ...t1(), outcome: "passed" }] });
    const p1 = store.get().pending!;
    expect(p1).not.toBe(pending0);
    expect([...p1]).toEqual([k(t2())]);
    expect(pending0.size).toBe(2);
    store.apply({ type: "testsUpdated", tests: [{ ...t2(), outcome: "failed" }] });
    expect(store.get().pending).toBe(p1); // same frame: draft mutated in place
    expect(p1.size).toBe(0);
  });

  it("any other event that replaces tests clears testsDelta", () => {
    const { store } = makeStore({ state: { tests: [{ ...t1(), outcome: "running" }] } });
    store.apply({ type: "runStarted", run: runSummary(), testIds: [t1().id] });
    store.apply({ type: "testsUpdated", tests: [{ ...t1(), outcome: "running" }] });
    expect(store.get().testsDelta).toBeDefined();
    store.apply({ type: "runFinished", run: runSummary() });
    expect(store.get().tests.get(k(t1()))?.outcome).toBe("cancelled");
    expect(store.get().testsDelta).toBeUndefined();
  });
});

describe("runStarted / runFinished", () => {
  it("collects runKeys across projects by id and marks non-running tests pending", () => {
    const { store } = makeStore({ state: { tests: [t1(), { ...t2(), outcome: "running" }, t3()] } });
    store.viewRun("old", { startedAt: "x" });
    store.apply({ type: "runStarted", run: runSummary({ runId: "r2" }), testIds: [t1().id, t2().id] });
    const s = store.get();
    expect(s.currentRun?.runId).toBe("r2");
    expect([...s.runKeys!].sort()).toEqual([k(t1()), k(t2())]);
    expect([...s.pending!]).toEqual([k(t1())]);
    expect(s.viewing).toBeUndefined(); // any run returns to live
  });

  it("runFinished: cancels still queued/running non-pending tests, keeps pending ones' old results", () => {
    const { store } = makeStore({ state: { tests: [{ ...t1(), outcome: "passed" }, t2()] } });
    store.apply({ type: "runStarted", run: runSummary({ runId: "r" }), testIds: [t1().id, t2().id] });
    store.apply({ type: "testsUpdated", tests: [{ ...t2(), outcome: "running" }] });
    const v = store.get().testsVersion;
    store.apply({ type: "runFinished", run: runSummary({ runId: "r", passed: 1 }) });
    const s = store.get();
    expect(s.tests.get(k(t1()))?.outcome).toBe("passed");
    expect(s.tests.get(k(t2()))?.outcome).toBe("cancelled");
    expect(s.currentRun).toBeUndefined();
    expect(s.lastRun?.passed).toBe(1);
    expect(s.pending).toBeUndefined();
    expect(s.testsVersion).toBe(v + 1);
  });

  it("runFinished without changes keeps the tests map and version", () => {
    const { store } = makeStore({ state: { tests: [{ ...t1(), outcome: "passed" }] } });
    const before = store.get();
    store.apply({ type: "runFinished", run: runSummary() });
    expect(store.get().tests).toBe(before.tests);
    expect(store.get().testsVersion).toBe(before.testsVersion);
  });

  it("a run-all started elsewhere returns the coverage view to the full report", () => {
    const full = coverageReport({ runId: "full" });
    const { store } = makeStore({ state: { tests: [t1(), t2()], coverage: full, fullCoverage: full } });
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "sub", scope: "subset", testIds: [t1().id] }) });
    expect(store.get().coverageLabel?.title).toBe("Test1");
    store.apply({ type: "runStarted", run: runSummary({ runId: "all" }), testIds: [t1().id, t2().id] });
    expect(store.get().coverageLabel).toBeUndefined();
    expect(store.get().coverage?.runId).toBe("full");
  });
});

describe("coverage requests", () => {
  it("requestCoverage sends a coverage run and tracks it: runId on runStarted, cleared by the matching report", () => {
    const { store, transport } = makeStore({ state: { tests: [t1(), t2()] } });
    store.requestCoverage({ title: "Test1", context: "Ns.ClassA" }, [t1().id], "P1");
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: [t1().id], coverage: true });
    expect(store.get().coverageRequest).toMatchObject({ status: "pending", testIds: [t1().id] });
    store.apply({ type: "runStarted", run: runSummary({ runId: "cr" }), testIds: [t1().id] });
    expect(store.get().coverageRequest?.runId).toBe("cr");
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "cr", scope: "subset", testIds: [t1().id] }) });
    expect(store.get().coverageRequest).toBeUndefined();
    expect(store.get().coverageLabel).toEqual({ title: "Test1", context: "Ns.ClassA" });
    expect(store.get().fullCoverage).toBeUndefined();
  });

  it("a whole-project request sends no testIds; a run without a report ends as noResult", () => {
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    store.requestCoverage({ title: "Project" }, [], "P1");
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: undefined, coverage: true });
    store.apply({ type: "runStarted", run: runSummary({ runId: "p" }), testIds: [t1().id] });
    store.apply({ type: "runFinished", run: runSummary({ runId: "p" }) });
    expect(store.get().coverageRequest?.status).toBe("noResult");
    store.dismissCoverageRequest();
    expect(store.get().coverageRequest).toBeUndefined();
  });

  it("labels: subset report matched by test ids; one unknown id → 'N tests'", () => {
    const { store } = makeStore({ state: { tests: [t1()] } });
    store.requestCoverage({ title: "Mine" }, ["a", "b"]);
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "zz", scope: "subset", testIds: ["b", "a"] }) });
    expect(store.get().coverageLabel).toEqual({ title: "Mine" });
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "zz2", scope: "subset", testIds: ["unknown"] }) });
    expect(store.get().coverageLabel).toEqual({ title: "1 tests" });
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "zz3", scope: "subset" }) });
    expect(store.get().coverageLabel).toEqual({ title: "0 tests" });
  });

  it("send(run all) resets the coverage scope; setWatch/setCoverage are optimistic", () => {
    const full = coverageReport({ runId: "full" });
    const { store, transport } = makeStore({ state: { tests: [t1()], coverage: full, fullCoverage: full } });
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "s", scope: "subset", testIds: [t1().id] }) });
    store.requestCoverage({ title: "x" }, ["q"]);
    store.send({ type: "run" });
    expect(store.get()).toMatchObject({ coverageLabel: undefined, coverageRequest: undefined, coverageSelection: undefined });
    expect(store.get().coverage?.runId).toBe("full");
    store.send({ type: "setWatch", enabled: true });
    store.send({ type: "setCoverage", enabled: true });
    expect(store.get()).toMatchObject({ watch: true, coverageEnabled: true });
    expect(transport.sent.map((c) => c.type)).toEqual(["run", "run", "setWatch", "setCoverage"]);
    // a subset run does not reset
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "s2", scope: "subset", testIds: [t1().id] }) });
    store.send({ type: "run", testIds: [t1().id] });
    expect(store.get().coverage?.runId).toBe("s2");
  });

  it("the full report only changes with fullCoverageUpdated: a subset run (e.g. a watch rerun) never replaces it", () => {
    const full = coverageReport({ runId: "full", linesCovered: 96, linesValid: 100, lineRate: 0.96 });
    const { store } = makeStore({ state: { tests: [t1()], coverage: full, fullCoverage: full } });
    // a 4-test watch rerun with coverage: its own report is the run's (Tests page tab), the page's stays the full one
    const subset = coverageReport({ runId: "watch", scope: "subset", testIds: ["a", "b", "c", "d"], linesCovered: 7, linesValid: 100, lineRate: 0.072 });
    store.apply({ type: "coverageUpdated", coverage: subset });
    expect(store.get().coverage).toBe(subset);
    expect(store.get().fullCoverage).toBe(full);
    // a run of all tests of only one project is "all" scope but not the page's report either: the host says what that is
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "one-project" }) });
    expect(store.get().fullCoverage).toBe(full);
    const merged = { ...full, runId: "merged", merged: [{ runId: "watch", generatedAt: "t", projectId: "P1", testCount: 4, filesChanged: 1 }] };
    store.apply({ type: "fullCoverageUpdated", coverage: merged });
    expect(store.get().fullCoverage).toBe(merged);
    // a snapshot after a reconnect: state.fullCoverage
    store.apply(stateEvent({ tests: [t1()], coverage: subset, fullCoverage: full }));
    expect(store.get().fullCoverage).toBe(full);
  });

  it("resetCoverageScope keeps a full current report", () => {
    const { store } = makeStore({ state: { coverage: coverageReport({ runId: "f" }) } });
    store.resetCoverageScope();
    expect(store.get().coverage?.runId).toBe("f");
  });

  it("showFullCoverage: back to the cached full report, or just drops the selection when none is cached", () => {
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    transport.fetchTestCoverage.mockResolvedValue(null);
    store.selectCoverage(target());
    expect(store.get().coverageSelection?.status).toBe("loading");
    store.showFullCoverage();
    expect(store.get().coverageSelection).toBeUndefined();
    store.showFullCoverage(); // nothing to do
    const full = coverageReport({ runId: "full" });
    store.apply({ type: "fullCoverageUpdated", coverage: full });
    store.apply({ type: "coverageUpdated", coverage: full });
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "s", scope: "subset", testIds: ["z"] }) });
    store.showFullCoverage();
    expect(store.get().coverage?.runId).toBe("full");
    expect(store.get().coverageLabel).toBeUndefined();
  });
});

const target = (p: Partial<CoverageTarget> = {}): CoverageTarget => ({
  key: "P1|Ns.ClassA.Test1",
  label: { title: "Test1" },
  testIds: ["Ns.ClassA.Test1"],
  projectId: "P1",
  isTest: true,
  ...p,
});

describe("coverage selection (cached lookups)", () => {
  it("debounces the lookup, then shows the found report", async () => {
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    const rep = coverageReport({ runId: "t1", scope: "subset", testIds: [t1().id] });
    transport.fetchTestCoverage.mockResolvedValue(rep);
    store.selectCoverage(target(), 200);
    expect(store.get().coverageSelection?.status).toBe("loading");
    expect(transport.fetchTestCoverage).not.toHaveBeenCalled();
    vi.advanceTimersByTime(200);
    expect(transport.fetchTestCoverage).toHaveBeenCalledWith("P1", [t1().id], expect.any(AbortSignal));
    await flushMicrotasks();
    expect(store.get().coverageSelection).toMatchObject({ status: "found", key: target().key });
    expect(store.get().coverage).toBe(rep);
    expect(store.get().coverageLabel).toEqual({ title: "Test1" });
    // memo: no second fetch
    store.showFullCoverage();
    store.selectCoverage(target());
    expect(transport.fetchTestCoverage).toHaveBeenCalledTimes(1);
    expect(store.get().coverageSelection?.status).toBe("found");
  });

  it("a test without cached coverage → missing (full report shown); a class → missing too", async () => {
    const full = coverageReport({ runId: "full" });
    const { store, transport } = makeStore({ state: { tests: [t1()], coverage: full } });
    transport.fetchTestCoverage.mockResolvedValue(null);
    store.selectCoverage(target());
    vi.advanceTimersByTime(0);
    await flushMicrotasks();
    expect(store.get().coverageSelection?.status).toBe("missing");
    expect(store.get().coverage).toBe(full);
    store.selectCoverage(target({ key: "cls", isTest: false, testIds: ["a", "b"] }));
    vi.advanceTimersByTime(0);
    await flushMicrotasks();
    expect(store.get().coverageSelection).toMatchObject({ key: "cls", status: "missing" });
  });

  it("while loading keeps what was shown before (prev), never chaining loading states", () => {
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    transport.fetchTestCoverage.mockReturnValue(new Promise(() => {}));
    store.selectCoverage(target({ key: "a", testIds: ["a"] }), 100);
    store.selectCoverage(target({ key: "b", testIds: ["b"] }), 100);
    expect(store.get().coverageSelection).toMatchObject({ key: "b", status: "loading", prev: undefined });
  });

  it("a newer selection aborts the stale lookup; its result is ignored", async () => {
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    let resolveA!: (r: CoverageReport | null) => void;
    const signals: AbortSignal[] = [];
    transport.fetchTestCoverage.mockImplementationOnce((_p, _ids, sig) => {
      signals.push(sig!);
      return new Promise((r) => (resolveA = r));
    });
    transport.fetchTestCoverage.mockImplementationOnce(async () => null);
    store.selectCoverage(target({ key: "a", testIds: ["a"] }));
    vi.advanceTimersByTime(0);
    store.selectCoverage(target({ key: "b", testIds: ["b"] }));
    expect(signals[0].aborted).toBe(true);
    vi.advanceTimersByTime(0);
    resolveA(coverageReport({ runId: "a" }));
    await flushMicrotasks();
    expect(store.get().coverageSelection).toMatchObject({ key: "b", status: "missing" });
  });

  it("a failed lookup shows missing; an AbortError is ignored", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    transport.fetchTestCoverage.mockRejectedValueOnce(new Error("500"));
    store.selectCoverage(target());
    vi.advanceTimersByTime(0);
    await flushMicrotasks();
    expect(store.get().coverageSelection?.status).toBe("missing");
    expect(warn).toHaveBeenCalled();
    transport.fetchTestCoverage.mockRejectedValueOnce(Object.assign(new Error("aborted"), { name: "AbortError" }));
    store.selectCoverage(target({ key: "c", testIds: ["c"] }));
    vi.advanceTimersByTime(0);
    await flushMicrotasks();
    expect(store.get().coverageSelection).toMatchObject({ key: "c", status: "loading" });
  });

  it("undefined / empty target → full coverage", () => {
    const { store } = makeStore({ state: { coverage: coverageReport({ runId: "f" }) } });
    store.selectCoverage(undefined);
    store.selectCoverage(target({ testIds: [] }));
    expect(store.get().coverageSelection).toBeUndefined();
  });

  it("a subset report for the selected tests marks the selection found; another report cancels the lookup and drops it", () => {
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    transport.fetchTestCoverage.mockReturnValue(new Promise(() => {}));
    store.selectCoverage(target({ label: { title: "Sel" } }));
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "x", scope: "subset", testIds: [t1().id] }) });
    expect(store.get().coverageSelection?.status).toBe("found");
    expect(store.get().coverageLabel).toEqual({ title: "Sel" });
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "y" }) });
    expect(store.get().coverageSelection).toBeUndefined();
  });

  it("memo filled by a run's coverageUpdated survives that runFinished; a build clears the memo; a finished run re-checks the selection", async () => {
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    const rep = coverageReport({ runId: "r", scope: "subset", testIds: [t1().id] });
    store.apply({ type: "coverageUpdated", coverage: rep });
    store.apply({ type: "runFinished", run: runSummary({ runId: "r" }) });
    await flushMicrotasks();
    store.selectCoverage(target());
    expect(transport.fetchTestCoverage).not.toHaveBeenCalled(); // memo hit
    expect(store.get().coverageSelection?.status).toBe("found");
    // another run finishes: memo entries of other runs are dropped and the selection is looked up again
    transport.fetchTestCoverage.mockResolvedValue(null);
    store.apply({ type: "runFinished", run: runSummary({ runId: "r2" }) });
    await flushMicrotasks();
    expect(transport.fetchTestCoverage).toHaveBeenCalledTimes(1);
    expect(store.get().coverageSelection?.status).toBe("missing");
    // now memoized as a miss: a further runFinished re-looks it up only after the memo is cleared by a build
    store.apply({ type: "projectStatus", project: project({ status: "building" }) });
    store.selectCoverage(target());
    vi.advanceTimersByTime(0);
    expect(transport.fetchTestCoverage).toHaveBeenCalledTimes(2);
  });

  it("refreshSelection does nothing without a settled selection", async () => {
    const { store, transport } = makeStore({ state: { tests: [t1()] } });
    store.apply({ type: "runFinished", run: runSummary() });
    await flushMicrotasks();
    transport.fetchTestCoverage.mockReturnValue(new Promise(() => {}));
    store.selectCoverage(target(), 1000);
    store.apply({ type: "runFinished", run: runSummary() });
    await flushMicrotasks();
    expect(transport.fetchTestCoverage).not.toHaveBeenCalled();
  });
});

describe("viewRun (past runs)", () => {
  it("loads a past run: loading → ready, cached afterwards", async () => {
    const { store, transport } = makeStore({ state: { runs: [runSummary({ runId: "r1", total: 3 })] } });
    const details = { run: runSummary({ runId: "r1", total: 3 }), results: [] };
    transport.fetchRun.mockResolvedValue(details);
    store.viewRun("r1");
    expect(store.get().viewing).toMatchObject({ status: "loading", run: { runId: "r1", total: 3 } });
    store.viewRun("r1"); // same run: no-op
    expect(transport.fetchRun).toHaveBeenCalledTimes(1);
    await flushMicrotasks();
    expect(store.get().viewing).toMatchObject({ status: "ready", details });
    store.viewRun(undefined);
    expect(store.get().viewing).toBeUndefined();
    store.viewRun("r1");
    expect(store.get().viewing?.status).toBe("ready");
    expect(transport.fetchRun).toHaveBeenCalledTimes(1);
  });

  it("unknown run → missing (with the fallback summary); fetch errors → missing", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { store, transport } = makeStore({ state: {} });
    transport.fetchRun.mockResolvedValueOnce(null);
    store.viewRun("gone", { startedAt: "2026-01-01T00:00:00Z", failed: 2 });
    await flushMicrotasks();
    expect(store.get().viewing).toMatchObject({ status: "missing", run: { runId: "gone", startedAt: "2026-01-01T00:00:00Z", failed: 2, total: 0 } });
    transport.fetchRun.mockRejectedValueOnce(new Error("net"));
    store.viewRun("err");
    await flushMicrotasks();
    expect(store.get().viewing).toMatchObject({ status: "missing", run: { runId: "err", startedAt: "" } });
    expect(warn).toHaveBeenCalled();
  });

  it("a late response for a run no longer viewed is cached but not shown", async () => {
    const { store, transport } = makeStore({ state: {} });
    let res!: (d: unknown) => void;
    transport.fetchRun.mockReturnValueOnce(new Promise((r) => (res = r as (d: unknown) => void)));
    transport.fetchRun.mockReturnValueOnce(Promise.reject(new Error("x")));
    store.viewRun("a");
    store.viewRun(undefined);
    res({ run: runSummary({ runId: "a" }), results: [] });
    await flushMicrotasks();
    expect(store.get().viewing).toBeUndefined();
    vi.spyOn(console, "warn").mockImplementation(() => {});
    store.viewRun("b");
    store.viewRun(undefined);
    await flushMicrotasks();
    expect(store.get().viewing).toBeUndefined();
    store.viewRun("a"); // cached from the late response
    expect(store.get().viewing?.status).toBe("ready");
  });

  it("viewRun(undefined) while live does not publish", async () => {
    const { store } = makeStore({ state: {} });
    await flushFrame();
    const s = store.get();
    store.viewRun(undefined);
    expect(store.get()).toBe(s);
  });
});

describe("other events", () => {
  it("runsUpdated, pendingRun, settingsChanged", () => {
    const { store } = makeStore({ state: { quarantined: ["q"], impactMap: { files: 1, tests: 1 } } });
    store.apply({ type: "runsUpdated", runs: [runSummary({ runId: "n" })] });
    expect(store.get().runs.map((r) => r.runId)).toEqual(["n"]);
    store.apply({ type: "pendingRun", pending: { runId: "p", requestedAt: "t", scope: "subset", requests: 2 } });
    expect(store.get().pendingRun?.requests).toBe(2);
    store.apply({ type: "pendingRun" });
    expect(store.get().pendingRun).toBeUndefined();
    store.apply({ type: "settingsChanged", settings: { watch: true, coverageEnabled: true, tracingEnabled: true } });
    expect(store.get()).toMatchObject({ watch: true, coverageEnabled: true, tracingEnabled: true, quarantined: ["q"], impactMap: { files: 1 } });
    store.apply({ type: "settingsChanged", settings: { watch: false, coverageEnabled: false, tracingEnabled: false, quarantined: [], impactMap: { files: 5, tests: 9 } } });
    expect(store.get()).toMatchObject({ watch: false, quarantined: [], impactMap: { files: 5 } });
  });

  it("log: appends with seq, counts unseen errors while the panel is closed, caps at 5000", () => {
    const { store } = makeStore();
    store.apply({ type: "log", level: "error", message: "e1", timestamp: "t", projectId: "P1" });
    store.apply({ type: "log", level: "warning", message: "w", timestamp: "t" });
    expect(store.get().unseenErrors).toBe(1);
    store.logPanelOpen = true;
    store.apply({ type: "log", level: "error", message: "e2", timestamp: "t" });
    expect(store.get().unseenErrors).toBe(1);
    store.markLogsSeen();
    expect(store.get().unseenErrors).toBe(0);
    const s = store.get();
    store.markLogsSeen(); // nothing to do
    expect(store.get()).toBe(s);
    for (let i = 0; i < 5005; i++) store.apply({ type: "log", level: "info", message: `m${i}`, timestamp: "t" });
    expect(store.get().logs).toHaveLength(5000);
    expect(store.get().logs.at(-1)?.message).toBe("m5004");
    expect(store.get().logs[0].seq).toBeLessThan(store.get().logs[1].seq);
    store.clearLogs();
    expect(store.get()).toMatchObject({ logs: [], unseenErrors: 0 });
  });

  it("debugger and commandRejected events reach listeners without changing the store state", () => {
    const { store } = makeStore({ state: {} });
    const s = store.get();
    const seen: string[] = [];
    serverEventListeners.add((e) => seen.push(e.type));
    const evs: ServerEvent[] = [
      { type: "debuggerWaiting", projectId: "P1", pid: 1, testIds: [], runId: "r", hints: [] },
      { type: "debuggerAttached", projectId: "P1", pid: 1, runId: "r" },
      { type: "commandRejected", status: 409, error: "busy" },
    ];
    evs.forEach((e) => store.apply(e));
    expect(seen).toEqual(["debuggerWaiting", "debuggerAttached", "commandRejected"]);
    expect(store.get()).toBe(s);
  });
});

describe("lean mode (Round 4)", () => {
  const hist: HistoryEntry[] = [{ outcome: "passed", at: "t1" }, { outcome: "failed", at: "t2" }];

  it("fetches the history after a lean snapshot and merges it into the tests", async () => {
    const fetch = vi.fn(async () => jsonResponse({ tests: [{ projectId: "P1", id: t1().id, history: hist, flaky: true }] }));
    vi.stubGlobal("fetch", fetch);
    const { store } = makeStore({ kind: "ws" });
    store.apply(stateEvent({ lean: true, tests: [t1(), t2()] }));
    await flushMicrotasks();
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toMatch(/\/api\/tests\/history$/);
    const t = store.get().tests.get(k(t1()))!;
    expect(t.history).toEqual(hist);
    expect(t.flaky).toBe(true);
    expect(store.get().tests.get(k(t2()))?.history).toBeUndefined();
    // new snapshots / updates get the fetched history merged in
    store.apply({ type: "testsUpdated", tests: [{ ...t1(), outcome: "failed" }] });
    expect(store.get().tests.get(k(t1()))?.history).toEqual(hist);
    // after every run the history is fetched again
    store.apply({ type: "runFinished", run: runSummary() });
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("fetchHistory: one request at a time, ignores errors and non-ok responses, never for the mock", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { store } = makeStore({ kind: "ws", state: { tests: [t1()] } });
    const fetch = vi.fn(async () => jsonResponse({}, 500));
    vi.stubGlobal("fetch", fetch);
    const a = store.fetchHistory();
    expect(store.fetchHistory()).toBe(a);
    await a;
    expect(fetch).toHaveBeenCalledTimes(1);
    fetch.mockRejectedValueOnce(new Error("down"));
    await store.fetchHistory();
    expect(warn).toHaveBeenCalled();
    const mock = makeStore({ kind: "mock" }).store;
    await mock.fetchHistory();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("ensureDetails fetches the selected test's output once per result and merges it", async () => {
    const fetch = vi.fn(async (url: URL) => {
      if (String(url).includes("/history")) return jsonResponse({ tests: [] });
      return jsonResponse({ ...t1(), lastRunAt: "L1", stdOut: "out", stdErr: "err", outputTruncated: true, history: hist });
    });
    vi.stubGlobal("fetch", fetch);
    const { store } = makeStore({ kind: "ws" });
    const lean = { ...t1(), hasOutput: true, lastRunAt: "L1" };
    store.apply(stateEvent({ lean: true, tests: [lean] }));
    await flushMicrotasks();
    store.ensureDetails(lean);
    store.ensureDetails(lean); // in flight
    await flushMicrotasks();
    const detailCalls = fetch.mock.calls.filter((c) => String(c[0]).includes("/details"));
    expect(detailCalls).toHaveLength(1);
    expect(String(detailCalls[0][0])).toContain("projectId=P1&id=Ns.ClassA.Test1");
    const t = store.get().tests.get(k(t1()))!;
    expect(t).toMatchObject({ stdOut: "out", stdErr: "err", outputTruncated: true, history: hist });
    store.ensureDetails(lean); // already fetched for this lastRunAt
    expect(fetch.mock.calls.filter((c) => String(c[0]).includes("/details"))).toHaveLength(1);
    // a new result (lastRunAt) without output in the payload: the stale output is not merged
    store.apply({ type: "testsUpdated", tests: [{ ...lean, lastRunAt: "L2" }] });
    expect(store.get().tests.get(k(t1()))?.stdOut).toBeUndefined();
  });

  it("ensureDetails is a no-op outside lean mode, without output or when output is present; failures are logged", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const fetch = vi.fn(async (url: URL) => (String(url).includes("/history") ? jsonResponse({ tests: [] }) : jsonResponse(null, 404)));
    vi.stubGlobal("fetch", fetch);
    const { store } = makeStore({ kind: "ws" });
    store.ensureDetails({ ...t1(), hasOutput: true });
    expect(fetch).not.toHaveBeenCalled();
    store.apply(stateEvent({ lean: true, tests: [t1()] }));
    await flushMicrotasks();
    fetch.mockClear();
    store.ensureDetails(t1());
    store.ensureDetails({ ...t1(), hasOutput: true, stdOut: "x" });
    expect(fetch).not.toHaveBeenCalled();
    store.ensureDetails({ ...t1(), hasOutput: true, lastRunAt: "L" });
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(1); // 404 → nothing merged
    fetch.mockRejectedValueOnce(new Error("x"));
    store.ensureDetails({ ...t1(), hasOutput: true, lastRunAt: "other" });
    await flushMicrotasks();
    expect(warn).toHaveBeenCalledWith("[details] fetch failed", expect.any(Error));
  });

  it("fetchFullOutput: the mock returns the test's own output; ws asks the host", async () => {
    const mock = makeStore({ kind: "mock" }).store;
    expect(await mock.fetchFullOutput({ ...t1(), stdOut: "a", stdErr: "b" })).toEqual({ stdOut: "a", stdErr: "b", truncated: false });
    const { store } = makeStore({ kind: "ws" });
    const fetch = vi.fn(async () => jsonResponse({ stdOut: "full", truncated: false }));
    vi.stubGlobal("fetch", fetch);
    expect(await store.fetchFullOutput(t1())).toEqual({ stdOut: "full", truncated: false });
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/tests/output?projectId=P1");
    fetch.mockResolvedValueOnce(jsonResponse({}, 410));
    expect(await store.fetchFullOutput(t1())).toBeUndefined();
  });
});

describe("connectionOf", () => {
  it("maps conn + everConnected to the header pill", () => {
    expect(connectionOf({ conn: "open", everConnected: true })).toBe("live");
    expect(connectionOf({ conn: "connecting", everConnected: false })).toBe("connecting");
    expect(connectionOf({ conn: "connecting", everConnected: true })).toBe("offline");
    expect(connectionOf({ conn: "reconnecting", everConnected: false })).toBe("offline");
  });
});

describe("openInEditor", () => {
  afterEach(() => resetLocalSettings());

  it("mock: logs instead of opening", async () => {
    const { store } = makeStore({ kind: "mock" });
    await openInEditor("/a/b.cs", 4);
    expect(store.get().logs.at(-1)?.message).toBe("Open in editor (vscode): /a/b.cs:4 (mock)");
    await openInEditor("/a/b.cs");
    expect(store.get().logs.at(-1)?.message).toBe("Open in editor (vscode): /a/b.cs (mock)");
  });

  it("ws: POSTs the editor, path, line and column; success leaves no notice", async () => {
    makeStore({ kind: "ws" });
    const fetch = vi.fn(async () => jsonResponse({ ok: true }));
    vi.stubGlobal("fetch", fetch);
    setSetting("editor", "rider");
    await openInEditor("/x.cs", 3, 7);
    const [url, init] = fetch.mock.calls[0] as unknown as [URL, RequestInit];
    expect(String(url)).toMatch(/\/api\/open-in-editor$/);
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({ editor: "rider", path: "/x.cs", line: 3, column: 7 });
    expect(editorNotice.peek()).toBeUndefined();
  });

  it("ws: a refused request shows a notice (403 for VS Code, any error for others)", async () => {
    makeStore({ kind: "ws" });
    const fetch = vi.fn(async () => jsonResponse({ ok: false, message: "not installed" }, 403));
    vi.stubGlobal("fetch", fetch);
    await openInEditor("/x.cs");
    expect(editorNotice.peek()).toBe("Couldn't open Visual Studio Code: not installed");
    setSetting("editor", "visualstudio");
    fetch.mockResolvedValueOnce({ ok: false, status: 500, statusText: "Server Error", json: async () => Promise.reject(new Error("no json")) } as unknown as Response);
    await openInEditor("/x.cs");
    expect(editorNotice.peek()).toBe("Couldn't open Visual Studio: 500 Server Error");
    fetch.mockRejectedValueOnce(new Error("offline"));
    await openInEditor("/x.cs");
    expect(editorNotice.peek()).toBe("Couldn't open Visual Studio: offline");
  });

  it("ws + VS Code: falls back to a vscode:// link when the host can't launch it", async () => {
    makeStore({ kind: "ws" });
    const hrefs: string[] = [];
    const loc = window.location;
    const fake = { href: loc.href, search: loc.search };
    Object.defineProperty(fake, "href", { get: () => loc.href, set: (v: string) => hrefs.push(v) });
    vi.stubGlobal("location", fake);
    const fetch = vi.fn(async () => jsonResponse({ ok: false }, 500));
    vi.stubGlobal("fetch", fetch);
    await openInEditor("C:\\src\\a b.cs", 12, 3);
    fetch.mockRejectedValueOnce(new Error("offline"));
    await openInEditor("/src/x.cs");
    expect(hrefs).toEqual(["vscode://file/C:/src/a%20b.cs:12:3", "vscode://file/src/x.cs"]);
    expect(editorNotice.peek()).toBeUndefined();
  });
});

describe("store singleton", () => {
  it("setStore/getStore", () => {
    const s = new Store("mock");
    setStore(s);
    expect(getStore()).toBe(s);
  });
});

describe("Round 7: result patches, file table, history stamps", () => {
  const res = (projectId: string, results: import("../../src/contract").TestResultPatch[], runId?: string): ServerEvent => ({
    type: "testResults",
    projects: [{ projectId, results }],
    ...(runId ? { runId } : {}),
  });
  const he = (n: number, runId = `r${n}`): HistoryEntry => ({ outcome: "passed", durationMs: n, at: `t${n}`, runId });

  it("a lean snapshot's fileIndex resolves to the shared filePath, omitted traits become []", () => {
    const { store } = makeStore();
    const wire = (id: string, fileIndex?: number) => ({ id, projectId: "P1", displayName: id, outcome: "notRun", ...(fileIndex !== undefined ? { fileIndex } : {}) }) as unknown as TestNode;
    store.apply(stateEvent({ files: ["/src/A.cs", "/src/B.cs"], tests: [wire("a", 1), wire("b", 1), wire("c")] }));
    const ts = store.get().tests;
    expect(ts.get("P1|a")?.filePath).toBe("/src/B.cs");
    expect(ts.get("P1|b")?.filePath).toBe(ts.get("P1|a")?.filePath);
    expect(ts.get("P1|c")?.filePath).toBeUndefined();
    expect(ts.get("P1|c")?.traits).toEqual([]);
    // full entries in testsUpdated keep an absolute filePath
    store.apply({ type: "testsUpdated", tests: [{ ...wire("d"), filePath: "/src/D.cs" }] });
    expect(store.get().tests.get("P1|d")).toMatchObject({ filePath: "/src/D.cs", traits: [] });
  });

  it("applies patches like full entries: delta keys, one Map copy per frame, unknown tests ignored", () => {
    const { store } = makeStore({ state: { tests: [{ ...t1(), outcome: "failed", errorMessage: "boom" }, t2()] } });
    store.apply(res("P1", [{ id: t1().id, outcome: "passed", durationMs: 3, clear: ["errorMessage"] }, { id: "nope", outcome: "passed" }]));
    const draft = store.get().tests;
    store.apply(res("P1", [{ id: t2().id, outcome: "running" }]));
    expect(store.get().tests).toBe(draft);
    const t = store.get().tests.get(k(t1()))!;
    expect(t).toMatchObject({ outcome: "passed", durationMs: 3, displayName: "Test1" });
    expect(t.errorMessage).toBeUndefined();
    expect(store.get().tests.size).toBe(2);
    expect([...store.get().testsDelta!.keys]).toEqual([k(t1()), "P1|nope", k(t2())].filter((x) => x !== "P1|nope"));
    expect(store.get().tests.get(k(t2()))?.outcome).toBe("running");
  });

  it("pending tests keep their previous result; later patches apply to the host's version (shadow)", () => {
    const { store } = makeStore({ state: { tests: [{ ...t1(), outcome: "failed", errorMessage: "old", durationMs: 9 }] } });
    store.apply({ type: "runStarted", run: runSummary(), testIds: [t1().id] });
    store.apply(res("P1", [{ id: t1().id, outcome: "queued" }]));
    expect(store.get().tests.get(k(t1()))?.outcome).toBe("failed"); // still the previous result
    // the result: applied to "queued + old fields", so the error stays only if the host kept it
    store.apply(res("P1", [{ id: t1().id, outcome: "passed", durationMs: 1, clear: ["errorMessage"] }]));
    const t = store.get().tests.get(k(t1()))!;
    expect(t).toMatchObject({ outcome: "passed", durationMs: 1 });
    expect(t.errorMessage).toBeUndefined();
    expect(store.get().pending?.size).toBe(0);
  });

  it("lean: a new result drops fetched output of the old one; history from patches survives later full entries", async () => {
    const fetch = vi.fn(async (url: URL) =>
      String(url).includes("/history") ? jsonResponse({ tests: [{ projectId: "P1", id: t1().id, history: [he(1)] }] }) : jsonResponse({ ...t1(), lastRunAt: "L1", stdOut: "out" }),
    );
    vi.stubGlobal("fetch", fetch);
    const { store } = makeStore({ kind: "ws" });
    const lean = { ...t1(), hasOutput: true, lastRunAt: "L1", outcome: "passed" as const };
    store.apply(stateEvent({ lean: true, historyStamps: { P1: "a.0" }, tests: [lean] }));
    await flushMicrotasks();
    store.ensureDetails(store.get().tests.get(k(t1()))!);
    await flushMicrotasks();
    expect(store.get().tests.get(k(t1()))).toMatchObject({ stdOut: "out", history: [he(1)] });
    // the run's result + its history entry (short form)
    store.apply(res("P1", [{ id: t1().id, durationMs: 2, lastRunAt: "t2" }]));
    expect(store.get().tests.get(k(t1()))?.stdOut).toBeUndefined();
    store.apply(res("P1", [{ id: t1().id, recorded: true }], "r2"));
    expect(store.get().tests.get(k(t1()))?.history).toEqual([he(1), he(2)]);
    store.apply({ type: "runFinished", run: runSummary() });
    await flushMicrotasks();
    // no refetch after the run: the patches carried the history
    expect(fetch.mock.calls.filter((c) => String(c[0]).includes("/history"))).toHaveLength(1);
    // a later lean full entry (no history) keeps it
    store.apply({ type: "testsUpdated", tests: [{ ...lean, displayName: "renamed", lastRunAt: "t2" }] });
    expect(store.get().tests.get(k(t1()))?.history).toEqual([he(1), he(2)]);
  });

  it("history stamps: everything once, then only projects whose stamp changed; failures are retried", async () => {
    const fetch = vi.fn(async (url: URL) => {
      const p = new URL(url).searchParams.get("projectId");
      if (p === "P2") return jsonResponse({ tests: [{ projectId: "P2", id: t3().id, history: [he(5)] }] });
      return jsonResponse({ tests: [{ projectId: "P1", id: t1().id, history: [he(1)], flaky: true }] });
    });
    vi.stubGlobal("fetch", fetch);
    const { store } = makeStore({ kind: "ws" });
    const projects = [project(), project({ id: "P2", name: "Two" })];
    store.apply(stateEvent({ lean: true, projects, historyStamps: { P1: "i.0", P2: "i.0" }, tests: [t1(), t3()] }));
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(String(fetch.mock.calls[0][0])).toMatch(/\/api\/tests\/history$/);
    expect(store.get().tests.get(k(t1()))).toMatchObject({ history: [he(1)], flaky: true });
    // same stamps: nothing
    store.apply(stateEvent({ lean: true, projects, historyStamps: { P1: "i.0", P2: "i.0" }, tests: [t1(), t3()] }));
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(store.get().tests.get(k(t1()))?.history).toEqual([he(1)]); // kept across snapshots
    // discovery restored P2's history
    store.apply(stateEvent({ lean: true, projects, historyStamps: { P1: "i.0", P2: "i.1" }, tests: [t1(), t3()] }));
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(String(fetch.mock.calls[1][0])).toContain("projectId=P2");
    expect(store.get().tests.get(k(t3()))?.history).toEqual([he(5)]);
    expect(store.get().tests.get(k(t1()))?.history).toEqual([he(1)]);
    // a failed request is retried with the next snapshot
    fetch.mockResolvedValueOnce(jsonResponse(null, 500));
    store.apply(stateEvent({ lean: true, projects, historyStamps: { P1: "i.0", P2: "i.2" }, tests: [t1(), t3()] }));
    await flushMicrotasks();
    store.apply(stateEvent({ lean: true, projects, historyStamps: { P1: "i.0", P2: "i.2" }, tests: [t1(), t3()] }));
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(4);
  });

  it("history recorded while a fetch is on its way is kept; a sync asked for meanwhile runs after it", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const fetch = vi.fn(async (url: URL) => {
      // the full response was taken before r2 was recorded, the later per-project one after it
      if (!new URL(url).searchParams.get("projectId")) await gate;
      else return jsonResponse({ tests: [{ projectId: "P1", id: t1().id, history: [he(1), he(2)] }] });
      return jsonResponse({ tests: [{ projectId: "P1", id: t1().id, history: [he(1)] }] });
    });
    vi.stubGlobal("fetch", fetch);
    const { store } = makeStore({ kind: "ws" });
    const projects = [project(), project({ id: "P2", name: "Two" })];
    store.apply(stateEvent({ lean: true, projects, historyStamps: { P1: "x.0", P2: "x.0" }, tests: [{ ...t1(), lastRunAt: "t1" }] }));
    await flushMicrotasks();
    store.apply(res("P1", [{ id: t1().id, durationMs: 2, lastRunAt: "t2", recorded: true }], "r2"));
    store.apply(stateEvent({ lean: true, projects, historyStamps: { P1: "x.1", P2: "x.0" }, tests: [{ ...t1(), lastRunAt: "t2", durationMs: 2 }] }));
    await flushMicrotasks();
    release();
    await flushMicrotasks(12);
    expect(store.get().tests.get(k(t1()))?.history).toEqual([he(1), he(2)]); // merged into the stale response
    await flushMicrotasks(30);
    expect(store.get().tests.get(k(t1()))?.history).toEqual([he(1), he(2)]);
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(String(fetch.mock.calls[1][0])).toContain("projectId=P1");
  });

  it("a big history response is merged in steps, each an incremental update", async () => {
    const many = Array.from({ length: 7 }, (_, i) => testNode({ id: `Ns.C.T${i}` }));
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ tests: many.map((t) => ({ projectId: "P1", id: t.id, history: [he(1)] })) })));
    // the chunk size is a constant in the app; the test makes it small through a mutable view
    const tunable = Store as unknown as { historyChunk: number };
    const chunk = tunable.historyChunk;
    tunable.historyChunk = 3;
    try {
      const { store } = makeStore({ kind: "ws" });
      store.apply(stateEvent({ lean: true, historyStamps: { P1: "s" }, tests: many }));
      await flushMicrotasks();
      expect([...store.get().tests.values()].filter((t) => t.history)).toHaveLength(3);
      expect(store.get().testsDelta?.keys.size).toBe(3);
      await vi.runAllTimersAsync();
      expect([...store.get().tests.values()].every((t) => t.history?.length === 1)).toBe(true);
    } finally {
      tunable.historyChunk = chunk;
    }
  });

  it("ensureDetails loads the persisted output of a test that never ran in this session (no lastRunAt)", async () => {
    const fetch = vi.fn(async (url: URL) =>
      String(url).includes("/history") ? jsonResponse({ tests: [] }) : jsonResponse({ ...t1(), outcome: "passed", stale: true, stdOut: "from the last session", history: [he(1)] }),
    );
    vi.stubGlobal("fetch", fetch);
    const { store } = makeStore({ kind: "ws" });
    // a previous session's result: stale, with output, but (older history files) no lastRunAt
    const stale = { ...t1(), outcome: "passed" as const, stale: true, hasOutput: true };
    store.apply(stateEvent({ lean: true, tests: [stale] }));
    await flushMicrotasks();
    store.ensureDetails(store.get().tests.get(k(t1()))!);
    await flushMicrotasks();
    expect(fetch.mock.calls.filter((c) => String(c[0]).includes("/details"))).toHaveLength(1);
    expect(store.get().tests.get(k(t1()))).toMatchObject({ stdOut: "from the last session", history: [he(1)] });
    // fetched once for that result
    store.ensureDetails({ ...stale });
    await flushMicrotasks();
    expect(fetch.mock.calls.filter((c) => String(c[0]).includes("/details"))).toHaveLength(1);
  });
});
