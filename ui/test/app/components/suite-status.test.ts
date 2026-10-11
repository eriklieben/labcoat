import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { computed, root, signal, type Disposer } from "@eriklieben/labcoat-design-system";
import {
  followDocumentStatus,
  formatEta,
  notOnMtpText,
  runAnnouncer,
  runningLabel,
  runSummaryBar,
  suiteBar,
  suiteLabel,
  suiteModel,
  suiteOf,
  type Suite,
  type SuiteModel,
} from "../../../src/components/suite-status";
import { buildTree, onStatusFilterRequest, type StatusFilter } from "../../../src/tree";
import { debugSession } from "../../../src/debug";
import type { Store } from "../../../src/store";
import type { RunSummary, TestNode } from "../../../src/contract";
import { flushFrame, project, runSummary, testNode, text } from "../../helpers/app";
import { flushMicrotasks } from "../../helpers/dom";
import { freshStore } from "../../helpers/app6";

const NOW = new Date("2026-10-09T12:00:00Z").getTime();

const suite = (p: Partial<Suite> = {}): Suite => ({
  total: 0,
  passed: 0,
  failed: 0,
  skipped: 0,
  notRun: 0,
  inRun: 0,
  outdated: 0,
  stale: 0,
  outdatedProjects: [],
  quarantined: 0,
  runTotal: 0,
  runDone: 0,
  ...p,
});

const t = (id: string, outcome: TestNode["outcome"], p: Partial<TestNode> = {}) => testNode({ id: `Ns.C.${id}`, outcome, ...p });

let store: Store;
let transport: Awaited<ReturnType<typeof freshStore>>["transport"];
let disposers: Disposer[] = [];

beforeEach(async () => {
  vi.useFakeTimers({ now: NOW });
  ({ store, transport } = await freshStore());
  debugSession.set(undefined);
});

afterEach(async () => {
  for (const d of disposers) d();
  disposers = [];
  await flushFrame(); // never leave the shared store with a frame scheduled on fake timers
});

/** A suite model over the store's tree, created in a root disposed after the test. */
function model(): SuiteModel {
  return root((dispose) => {
    disposers.push(dispose);
    const roots = computed(() => buildTree(store.$.projects(), store.$.tests(), store.$.pending(), new Set(store.$.quarantined() ?? [])));
    return suiteModel(roots);
  });
}

function inRoot<T>(fn: () => T): T {
  return root((dispose) => {
    disposers.push(dispose);
    return fn();
  });
}

async function load(tests: TestNode[], extra: Parameters<typeof freshStore>[0] = {}) {
  store.apply({ type: "state", state: { projects: [project()], tests, watch: false, coverageEnabled: false, ...extra } });
  await flushFrame();
}

async function startRun(run: RunSummary, ids: string[]) {
  store.apply({ type: "runStarted", run, testIds: ids });
  await flushFrame();
}

async function finishRun(run: RunSummary, results: TestNode[] = []) {
  if (results.length) store.apply({ type: "testsUpdated", tests: results });
  store.apply({ type: "runFinished", run });
  await flushFrame();
  await flushMicrotasks();
}

// ---- pure helpers ---------------------------------------------------------------------------------------------

describe("suiteLabel", () => {
  it("says 'No tests' for an empty suite and 'not run yet' before any result", () => {
    expect(suiteLabel(suite())).toBe("No tests");
    expect(suiteLabel(suite({ total: 1, notRun: 1 }))).toBe("1 test · not run yet");
    expect(suiteLabel(suite({ total: 3, notRun: 3 }))).toBe("3 tests · not run yet");
  });

  it("short form when everything ran green (skipped and quarantined appended)", () => {
    expect(suiteLabel(suite({ total: 5, passed: 5 }))).toBe("5 passed");
    expect(suiteLabel(suite({ total: 6, passed: 5, skipped: 1 }))).toBe("5 passed · 1 skipped");
    expect(suiteLabel(suite({ total: 6, passed: 5, quarantined: 1 }))).toBe("5 passed · 1 quarantined");
  });

  it("long form adds up to the total", () => {
    expect(suiteLabel(suite({ total: 25, passed: 16, failed: 7, skipped: 1, notRun: 1 }))).toBe("7 failed · 16 passed · 1 skipped · 1 not run · 25 tests");
    expect(suiteLabel(suite({ total: 3, passed: 2, notRun: 1 }))).toBe("2 passed · 1 not run · 3 tests");
    expect(suiteLabel(suite({ total: 3, failed: 2, quarantined: 1 }))).toBe("2 failed · 1 quarantined · 3 tests");
  });
});

describe("formatEta", () => {
  it.each([
    [undefined, undefined],
    [Number.NaN, undefined],
    [Infinity, undefined],
    [800, "almost done"],
    [4_400, "~4 s left"],
    [23_000, "~25 s left"],
    [130_000, "~2 min left"],
  ])("%s ms → %s", (ms, out) => expect(formatEta(ms as number | undefined)).toBe(out));
});

describe("suiteOf", () => {
  it("counts every test's current outcome and the projects whose results are outdated", () => {
    const tests = new Map([t("a", "passed"), t("b", "failed"), t("c", "skipped"), t("d", "notRun"), t("e", "passed", { stale: true })].map((x) => [`P1|${x.id}`, x]));
    const projects = [project({ resultsOutdated: true })];
    const s = suiteOf(buildTree(projects, tests), projects, undefined, undefined);
    expect(s).toMatchObject({ total: 5, passed: 2, failed: 1, skipped: 1, notRun: 1, inRun: 0, runTotal: 0, runDone: 0 });
    // outdated: every test with a result; stale is counted apart from the outdated ones
    expect(s.outdated).toBe(4);
    expect(s.stale).toBe(0);
    expect(s.outdatedProjects.map((p) => p.id)).toEqual(["P1"]);
  });

  it("during a run, pending tests leave their old bucket and count as in the run", () => {
    const tests = new Map([t("a", "passed"), t("b", "failed"), t("c", "running"), t("d", "passed")].map((x) => [`P1|${x.id}`, x]));
    const pending = new Set(["P1|Ns.C.a", "P1|Ns.C.b"]);
    const keys = new Set(["P1|Ns.C.a", "P1|Ns.C.b", "P1|Ns.C.c", "P1|Ns.C.d"]);
    const s = suiteOf(buildTree([project()], tests, pending), [project()], runSummary({ total: 3 }), keys);
    expect(s).toMatchObject({ total: 4, passed: 1, failed: 0, inRun: 3, runTotal: 4, runDone: 1, notRun: 0 });
  });

  it("quarantined failures are not failures", () => {
    const tests = new Map([t("a", "failed"), t("b", "passed")].map((x) => [`P1|${x.id}`, x]));
    const s = suiteOf(buildTree([project()], tests, undefined, new Set(["P1|Ns.C.a"])), [project()], undefined, undefined);
    expect(s).toMatchObject({ failed: 0, passed: 1, quarantined: 1, notRun: 0 });
  });
});

// ---- the model ------------------------------------------------------------------------------------------------

describe("suiteModel tone", () => {
  it("empty → notRun → failed → passed", async () => {
    const m = model();
    expect(m.tone()).toBe("empty");
    await load([t("a", "notRun")]);
    expect(m.tone()).toBe("notRun");
    await load([t("a", "failed"), t("b", "passed")]);
    expect(m.tone()).toBe("failed");
    await load([t("a", "passed"), t("b", "skipped")]);
    expect(m.tone()).toBe("passed");
    expect(m.outdated()).toBe(false);
  });

  it("building while a project builds/discovers; running wins", async () => {
    const m = model();
    await load([t("a", "passed")], { projects: [project({ status: "building", name: "Lib.Tests" })] });
    expect(m.tone()).toBe("building");
    expect(m.busy()).toEqual(["Lib.Tests"]);
    await startRun(runSummary({ total: 1 }), ["Ns.C.a"]);
    expect(m.tone()).toBe("running");
    expect(m.running()).toBe(true);
  });

  it("outdated: offline after being connected, a failed build, or only previous-session results", async () => {
    const m = model();
    await load([t("a", "passed")]);
    transport.setStatus("reconnecting", { retryInMs: 1000 });
    await flushFrame();
    expect(m.offline()).toBe(true);
    expect(m.tone()).toBe("outdated");
    expect(m.outdated()).toBe(true);

    transport.setStatus("open");
    await load([t("a", "passed", { stale: true })]);
    expect(m.tone()).toBe("outdated");
    expect(m.outdated()).toBe(true);

    await load([t("a", "passed")], { projects: [project({ resultsOutdated: true })] });
    expect(m.tone()).toBe("outdated");
  });

  it("elapsed ticks while a run is in flight and is undefined otherwise", async () => {
    const m = model();
    await load([t("a", "notRun")]);
    expect(m.elapsed()).toBeUndefined();
    await startRun(runSummary({ startedAt: new Date(NOW - 2000).toISOString(), total: 1 }), ["Ns.C.a"]);
    const e0 = m.elapsed()!;
    expect(e0).toBeGreaterThanOrEqual(2000);
    expect(e0).toBeLessThan(2100);
    vi.advanceTimersByTime(500);
    expect(m.elapsed()).toBe(e0 + 500);
  });

  it("estimates the time left from the tests' history medians", async () => {
    const m = model();
    const hist = (ms: number) => [1, 2, 3].map((i) => ({ outcome: "passed" as const, durationMs: ms, at: `2026-10-0${i}T00:00:00Z` }));
    await load([t("a", "passed", { history: hist(1000) }), t("b", "passed", { history: hist(3000) }), t("c", "passed", { durationMs: 2000 })]);
    await startRun(runSummary({ runId: "r9", startedAt: new Date(NOW - 2000).toISOString(), total: 3 }), ["Ns.C.a", "Ns.C.b", "Ns.C.c"]);
    expect(m.eta()).toBeUndefined(); // nothing done yet
    store.apply({ type: "testsUpdated", tests: [t("a", "passed", { durationMs: 1000 })] });
    await flushFrame();
    vi.advanceTimersByTime(1000);
    // done 1000 ms of expected work in 3 s; 5000 ms left → 15 s
    expect(m.eta()).toBe("~15 s left");
  });
});

// ---- headline --------------------------------------------------------------------------------------------------

describe("runSummaryBar", () => {
  function bar() {
    const m = model();
    const celebrating = signal(false);
    const el = inRoot(() => runSummaryBar(m, celebrating)) as HTMLElement;
    document.body.append(el);
    return { el, celebrating, primary: () => text(el.querySelector(".summary-label")), secondary: () => text(el.querySelector(".summary-secondary")) };
  }

  it("idle suite: tone class, suite label, 'No runs yet'", async () => {
    await load([t("a", "passed"), t("b", "failed")]);
    const { el, primary, secondary } = bar();
    expect(el.classList.contains("run-summary")).toBe(true);
    expect(el.classList.contains("tone-failed")).toBe(true);
    expect(primary()).toBe("1 failed · 1 passed · 2 tests");
    expect(secondary()).toBe("No runs yet");
    expect(el.title).toContain("1 passed, 1 failed, 0 skipped, 0 not run — 2 tests");
    expect(el.querySelector(".summary-status .st")).not.toBeNull();
  });

  it("all passed + celebrating adds the celebrate class", async () => {
    await load([t("a", "passed")]);
    const { el, celebrating } = bar();
    expect(el.classList.contains("all-passed")).toBe(true);
    expect(el.classList.contains("celebrate")).toBe(false);
    celebrating.set(true);
    expect(el.classList.contains("celebrate")).toBe(true);
  });

  it("describes the last run: all tests, a subset, duration, aborted, cancelled", async () => {
    await load([t("a", "passed"), t("b", "passed")], { lastRun: runSummary({ total: 2, passed: 2, durationMs: 1500, finishedAt: new Date(NOW).toISOString() }) });
    const { el, secondary } = bar();
    expect(secondary()).toMatch(/^Last run .* · all tests · 1\.50 s$/);
    await load([t("a", "passed"), t("b", "passed")], { lastRun: runSummary({ total: 1, scope: "subset", passed: 1 }) });
    expect(secondary()).toMatch(/· 1 test$/);
    await load([t("a", "passed"), t("b", "passed")], { lastRun: runSummary({ total: 3, passed: 3 }) });
    expect(secondary()).toMatch(/· 3 tests$/);
    await load([t("a", "passed"), t("b", "passed")], { lastRun: runSummary({ total: 2, aborted: true, notRun: 1, abortReason: "Test host crashed" }) });
    expect(secondary()).toMatch(/^Last run aborted .* · 1 not run$/);
    expect(el.title).toContain("Test host crashed");
    expect(el.title).toContain("Last run: 0 passed, 0 failed, 0 skipped, 1 not run of 2 tests");
    await load([t("a", "passed"), t("b", "passed")], { lastRun: runSummary({ total: 2, cancelled: true }) });
    expect(secondary()).toMatch(/^Last run cancelled /);
  });

  it("offline, outdated build and previous-session texts", async () => {
    await load([t("a", "passed", { stale: true })]);
    const { el, secondary } = bar();
    expect(secondary()).toBe("Results from a previous session");
    expect(el.title).toContain("1 result is from a previous session.");
    expect(el.querySelector(".st-outdated")).not.toBeNull();

    await load([t("a", "passed", { stale: true }), t("b", "passed")]);
    expect(secondary()).toBe("1 result from a previous session");

    await load([t("a", "passed")], { projects: [project({ name: "Lib.Tests", resultsOutdated: true })] });
    expect(secondary()).toBe("Outdated: Lib.Tests failed to build");
    expect(el.title).toContain("results are outdated: Lib.Tests failed to build since.");
    await load([t("a", "passed"), testNode({ id: "X.Y.z", projectId: "P2", outcome: "passed" })], {
      projects: [project({ resultsOutdated: true }), project({ id: "P2", name: "Two", resultsOutdated: true })],
    });
    expect(secondary()).toBe("Outdated: 2 projects failed to build");

    transport.setStatus("reconnecting");
    await flushFrame();
    expect(secondary()).toBe("Offline — last known results");
    expect(el.classList.contains("is-outdated")).toBe(true);
  });

  it("projects on VSTest: the secondary text says so when there are no tests, the tooltip always", async () => {
    const vstest = project({
      name: "SampleTests",
      testPlatform: "vstest",
      diagnostics: [{ level: "warning", message: "SampleTests uses VSTest (Microsoft.NET.Test.Sdk + MSTest.TestAdapter). Labcoat runs Microsoft.Testing.Platform projects." }],
    });
    await load([], { projects: [vstest] });
    const { el, primary, secondary } = bar();
    expect(primary()).toBe("No tests");
    expect(secondary()).toBe("SampleTests uses VSTest: switch it to Microsoft.Testing.Platform");
    expect(el.title).toContain("Not run: SampleTests uses VSTest (Microsoft.NET.Test.Sdk + MSTest.TestAdapter).");
    await load([t("a", "passed")], { projects: [project(), { ...vstest, id: "P2" }] });
    expect(secondary()).toBe("No runs yet");
    expect(el.title).toContain("Not run: SampleTests uses VSTest");
  });

  it("notOnMtpText: one VSTest project, a project without a test framework, several", () => {
    expect(notOnMtpText([project({ name: "A", testPlatform: "vstest" })])).toBe("A uses VSTest: switch it to Microsoft.Testing.Platform");
    expect(notOnMtpText([project({ name: "B", testPlatform: "none" })])).toBe("B is not a Microsoft.Testing.Platform test app");
    expect(notOnMtpText([project({ testPlatform: "vstest" }), project({ id: "P2", testPlatform: "vstest" })])).toBe(
      "2 projects use VSTest: switch them to Microsoft.Testing.Platform",
    );
  });

  it("building: 'Building X…' with the suite as secondary text", async () => {
    await load([t("a", "passed")], { projects: [project({ name: "Lib.Tests", status: "discovering" })] });
    const { primary, secondary } = bar();
    expect(primary()).toBe("Building Lib.Tests…");
    expect(secondary()).toBe("1 passed");
    await load([], { projects: [project({ name: "Lib.Tests", status: "building" })] });
    expect(document.querySelector(".summary-secondary")).toBeNull();
  });

  it("a snapshot of a past run says so", async () => {
    await load([t("a", "passed")]);
    const { secondary } = bar();
    store.viewRun("old", { startedAt: new Date(NOW).toISOString() });
    await flushFrame();
    expect(secondary()).toMatch(/^Run of .* \(snapshot\)$/);
  });

  it("while running: progress, 'starting…', failures so far, queued request, debugger wait, elapsed", async () => {
    await load([t("a", "passed"), t("b", "passed"), t("c", "passed")]);
    const { el, primary, secondary } = bar();
    await startRun(runSummary({ runId: "r2", startedAt: new Date(NOW).toISOString(), total: 3 }), ["Ns.C.a", "Ns.C.b", "Ns.C.c"]);
    expect(primary()).toBe("Running 0/3");
    expect(secondary()).toBe("starting…");
    expect(el.classList.contains("tone-running")).toBe(true);
    expect(el.querySelector(".sum-time")).not.toBeNull();

    store.apply({ type: "testsUpdated", tests: [t("a", "failed")] });
    await flushFrame();
    expect(primary()).toBe("Running 1/3 · 1 failed so far");
    expect(secondary()).toBe("estimating…");

    store.apply({ type: "pendingRun", pending: { runId: "q", requestedAt: "", scope: "subset", testCount: 2, requests: 3 } });
    await flushFrame();
    expect(secondary()).toBe("estimating… · next: 2 tests queued (3 requests)");
    store.apply({ type: "pendingRun", pending: { runId: "q", requestedAt: "", scope: "all", requests: 1 } });
    await flushFrame();
    expect(secondary()).toBe("estimating… · next: all tests queued");

    debugSession.set({ label: "x", projectId: "P1", testIds: [], status: "waiting", pid: 4242 });
    await flushFrame();
    expect(secondary()).toMatch(/^waiting for the debugger \(PID 4242\)/);
  });

  it("repeat runs show the repetition from the host's log lines", async () => {
    await load([t("a", "passed")]);
    const { primary } = bar();
    const startedAt = new Date(NOW - 1000).toISOString();
    await startRun(runSummary({ runId: "rep", startedAt, total: 1, repeat: { count: 20, completed: 0, perTest: [] } }), ["Ns.C.a"]);
    expect(primary()).toBe("Running 20× · repetition 1/20");
    store.apply({ type: "log", level: "info", message: "Repeat 12/20", timestamp: new Date(NOW).toISOString() });
    store.apply({ type: "log", level: "info", message: "something else", timestamp: new Date(NOW).toISOString() });
    await flushFrame();
    expect(primary()).toBe("Running 20× · repetition 12/20");
    expect(runningLabel()).toBe("Running 20× · repetition 12/20");
  });

  it("on run finish the changed counts tick from the pre-run values to the final ones", async () => {
    await load([t("a", "failed"), t("b", "failed")]);
    const { primary } = bar();
    expect(primary()).toBe("2 failed · 2 tests");
    const run = runSummary({ runId: "r3", startedAt: new Date(NOW).toISOString(), total: 2 });
    await startRun(run, ["Ns.C.a", "Ns.C.b"]);
    await finishRun({ ...run, passed: 2 }, [t("a", "passed"), t("b", "passed")]);
    // the tween starts at the old values …
    expect(primary()).toBe("2 failed · 2 tests");
    for (let i = 0; i < 30; i++) vi.advanceTimersToNextFrame();
    // … and lands on the settled suite
    expect(primary()).toBe("2 passed");
  });
});

describe("runningLabel", () => {
  it("is plain 'Running' without a current run", () => {
    expect(inRoot(() => runningLabel())).toBe("Running");
  });
});

// ---- composition bar --------------------------------------------------------------------------------------------

describe("suiteBar", () => {
  function mountBar() {
    const m = model();
    const el = inRoot(() => suiteBar(m));
    document.body.append(el);
    const track = el.querySelector<HTMLElement>(".rp-track")!;
    const layer = (n: string) => el.querySelector<HTMLElement>(`.layer.${n}`)!.style.transform;
    return { el, track, layer };
  }

  it("is an accessible progress bar whose cumulative layers scale with the suite", async () => {
    await load([t("a", "passed"), t("b", "failed"), t("c", "skipped"), t("d", "notRun")]);
    const { el, track, layer } = mountBar();
    expect(track.getAttribute("role")).toBe("progressbar");
    expect(track.getAttribute("aria-label")).toBe("Suite: 1 failed, 1 passed, 1 skipped, 1 not run of 4 tests");
    expect(track.getAttribute("aria-valuemin")).toBe("0");
    expect(track.getAttribute("aria-valuemax")).toBe("4");
    expect(track.getAttribute("aria-valuenow")).toBe("3");
    expect(layer("passed")).toBe("scaleX(0.25)");
    expect(layer("failed")).toBe("scaleX(0.5)");
    expect(layer("skipped")).toBe("scaleX(0.75)");
    expect(layer("inrun")).toBe("scaleX(0.75)");
    expect(el.classList.contains("empty")).toBe(false);
  });

  it("band buttons (out of the tab order) request the tree's status filter", async () => {
    await load([t("a", "passed"), t("b", "failed")]);
    const { el } = mountBar();
    const bands = [...el.querySelectorAll<HTMLButtonElement>(".band-hit")];
    expect(bands.map((b) => b.className)).toEqual(["band-hit passed", "band-hit failed"]);
    expect(bands[0].tabIndex).toBe(-1);
    expect(bands[1].style.left).toBe("50%");
    expect(bands[1].style.width).toBe("50%");
    expect(bands[1].getAttribute("aria-label")).toBe("Show 1 failed tests");
    expect(bands[1].title).toBe("Show 1 failed test");
    const got: StatusFilter[] = [];
    const off = onStatusFilterRequest((f) => got.push(f));
    bands[1].click();
    off();
    expect(got).toEqual(["failed"]);
  });

  it("empty suite, building (indeterminate) and live run states", async () => {
    const { el, track } = mountBar();
    expect(el.classList.contains("empty")).toBe(true);
    expect(el.querySelector(".layer.passed")!.getAttribute("style")).toContain("scaleX(0)");
    await load([t("a", "passed"), t("b", "passed")], { projects: [project({ status: "building" })] });
    expect(el.querySelector(".indeterminate")).not.toBeNull();
    await load([t("a", "passed"), t("b", "passed")]);
    expect(el.querySelector(".indeterminate")).toBeNull();
    await startRun(runSummary({ runId: "r4", total: 2 }), ["Ns.C.a", "Ns.C.b"]);
    expect(el.classList.contains("live")).toBe(true);
    expect(track.getAttribute("aria-label")).toBe("Run progress: 0 of 2 tests done");
    store.apply({ type: "testsUpdated", tests: [t("a", "failed")] });
    await flushFrame();
    expect(track.getAttribute("aria-label")).toBe("Run progress: 1 of 2 tests done, 1 failed");
    expect(track.getAttribute("aria-valuemax")).toBe("2");
    expect(track.getAttribute("aria-valuenow")).toBe("1");
  });

  it("a run that turns the suite red → green replays the sheen once", async () => {
    await load([t("a", "failed")]);
    const { track } = mountBar();
    const run = runSummary({ runId: "r5", total: 1 });
    await startRun(run, ["Ns.C.a"]);
    await finishRun({ ...run, passed: 1 }, [t("a", "passed")]);
    expect(track.classList.contains("lc-sheen")).toBe(false);
    vi.advanceTimersByTime(260);
    expect(track.classList.contains("lc-sheen")).toBe(true);
    vi.advanceTimersByTime(700);
    expect(track.classList.contains("lc-sheen")).toBe(false);

    // green → green: nothing
    const run2 = runSummary({ runId: "r6", total: 1 });
    await startRun(run2, ["Ns.C.a"]);
    await finishRun({ ...run2, passed: 1 }, [t("a", "passed")]);
    vi.advanceTimersByTime(260);
    expect(track.classList.contains("lc-sheen")).toBe(false);
  });
});

// ---- announcer -----------------------------------------------------------------------------------------------

describe("runAnnouncer", () => {
  it("announces run start and finish (and ignores runs it did not see start)", async () => {
    await load([t("a", "passed"), t("b", "passed")], { lastRun: runSummary({ runId: "before", total: 2, passed: 2 }) });
    const el = inRoot(() => runAnnouncer());
    expect(el.getAttribute("role")).toBe("status");
    expect(el.getAttribute("aria-live")).toBe("polite");
    expect(el.textContent).toBe("");
    const run = runSummary({ runId: "r7", total: 2, trigger: "watch" });
    await startRun(run, ["Ns.C.a", "Ns.C.b"]);
    expect(el.textContent).toBe("Run started: 2 tests (watch).");
    await finishRun({ ...run, passed: 1, failed: 1, durationMs: 1200 });
    expect(el.textContent).toBe("Run finished: 1 failed, 1 passed of 2 tests in 1.20 s.");
  });

  it("aborted and cancelled runs", async () => {
    await load([t("a", "passed")]);
    const el = inRoot(() => runAnnouncer());
    const run = runSummary({ runId: "r8", total: 1 });
    await startRun(run, ["Ns.C.a"]);
    expect(el.textContent).toBe("Run started: 1 test.");
    await finishRun({ ...run, aborted: true, notRun: 1, skipped: 1, abortReason: "Host exited." });
    expect(el.textContent).toBe("Run aborted: 0 passed, 1 skipped, 1 not run of 1 test. Host exited.");
    const run2 = runSummary({ runId: "r9", total: 1 });
    await startRun(run2, ["Ns.C.a"]);
    await finishRun({ ...run2, cancelled: true });
    expect(el.textContent).toBe("Run cancelled: 0 passed of 1 test.");
  });
});

// ---- document title + favicon -----------------------------------------------------------------------------

describe("followDocumentStatus", () => {
  const svg = '<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0"/></svg>';

  it("keeps the title in sync with the suite and restores it on dispose", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: false, text: async () => "" })));
    await load([], { workspaceName: "Shop" });
    let dispose!: Disposer;
    root((d) => {
      dispose = d;
      followDocumentStatus(model());
    });
    expect(document.title).toBe("Shop · Labcoat");
    await load([t("a", "failed")], { workspaceName: "Shop" });
    expect(document.title).toBe("✕ 1 failed · Shop");
    await load([t("a", "passed")], { workspaceName: "Shop" });
    expect(document.title).toBe("✓ 1 passed · Shop");
    await load([t("a", "notRun")], { workspaceName: "Shop" });
    expect(document.title).toBe("1 tests · Shop");
    await load([t("a", "passed")], { workspaceName: "Shop", projects: [project({ status: "building" })] });
    expect(document.title).toBe("▶ Building… · Shop");
    await load([t("a", "passed", { stale: true })], { workspaceName: "Shop" });
    expect(document.title).toBe("⚠ 1 passed · Shop");
    dispose();
    expect(document.title).toBe("Labcoat");
  });

  it("throttles title writes while a run streams results", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: false, text: async () => "" })));
    await load([t("a", "passed"), t("b", "passed")], { workspaceName: "WS" });
    root((d) => {
      disposers.push(d);
      followDocumentStatus(model());
    });
    expect(document.title).toBe("✓ 2 passed · WS");
    await startRun(runSummary({ runId: "r10", total: 2 }), ["Ns.C.a", "Ns.C.b"]);
    // within 500 ms of the last write: deferred
    expect(document.title).toBe("✓ 2 passed · WS");
    vi.advanceTimersByTime(600);
    expect(document.title).toBe("▶ Running 0/2 · WS");
    store.apply({ type: "testsUpdated", tests: [t("a", "passed")] });
    await flushFrame();
    expect(document.title).toBe("▶ Running 0/2 · WS");
    vi.advanceTimersByTime(600);
    expect(document.title).toBe("▶ Running 1/2 · WS");
  });

  it("draws the status dot onto the fetched favicon and restores the original link", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, text: async () => svg })));
    for (const l of document.head.querySelectorAll('link[rel="icon"]')) l.remove();
    const link = Object.assign(document.createElement("link"), { rel: "icon", href: "http://localhost:3000/favicon.svg" });
    document.head.append(link);
    try {
      await load([t("a", "failed")]);
      let dispose!: Disposer;
      root((d) => {
        dispose = d;
        followDocumentStatus(model());
      });
      await flushMicrotasks();
      expect(link.type).toBe("image/svg+xml");
      expect(decodeURIComponent(link.href)).toContain('fill="#d13438"');
      await load([t("a", "notRun")]);
      expect(decodeURIComponent(link.href)).not.toContain("<circle");
      dispose();
      expect(link.href).toBe("http://localhost:3000/favicon.svg");
    } finally {
      link.remove();
    }
  });
});
