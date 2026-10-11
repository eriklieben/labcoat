// run-diff.ts keeps module-level computeds over getStore() (as the app has one store), so this file uses one store.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h } from "@eriklieben/labcoat-design-system";
import {
  closeCompare,
  comparison,
  comparisonOpen,
  comparisonResult,
  diffRuns,
  isNewFailure,
  isNewFailureAt,
  isNewlySlow,
  isRunStored,
  LIVE,
  newFailureKeys,
  openCompare,
  runDeltas,
  runRefLabel,
} from "../../../src/components/run-diff";
import type { HistoryEntry, RunDetails, RunResult, RunSummary } from "../../../src/contract";
import { click, explorerState, flushFrame, key, makeStore, mount, runSummary, testNode, text } from "../../helpers/app";
import { flushMicrotasks } from "../../helpers/dom";

const { store, transport } = makeStore();

const runs: RunSummary[] = [
  runSummary({ runId: "r3", startedAt: "2026-10-03T10:00:00Z", passed: 2, failed: 1, total: 3 }),
  runSummary({ runId: "s2", startedAt: "2026-10-02T10:00:00Z", scope: "subset", total: 1, passed: 1 }),
  runSummary({ runId: "r1", startedAt: "2026-10-01T10:00:00Z", passed: 3, total: 3 }),
];

async function setState(p: Parameters<typeof explorerState>[0] = {}) {
  store.apply({ type: "state", state: explorerState({ runs, ...p }) });
  await flushFrame();
}

const he = (outcome: HistoryEntry["outcome"], runId?: string, at = "2026-10-01T10:00:00Z"): HistoryEntry => ({ outcome, runId, at });
const rr = (id: string, outcome: RunResult["outcome"], durationMs?: number, errorMessage?: string): RunResult => ({ projectId: "P1", id, outcome, durationMs, errorMessage });

beforeEach(async () => {
  transport.fetchRun.mockReset();
  await setState();
});
afterEach(() => {
  comparison.set(undefined);
  comparisonOpen.set(false);
});

describe("history helpers", () => {
  it("isRunStored: only runs in the persisted list", () => {
    expect(isRunStored("r1")).toBe(true);
    expect(isRunStored("pruned")).toBe(false);
    expect(isRunStored(undefined)).toBe(false);
  });

  it("isNewFailureAt: failing now, previous full-run result passed (or no earlier result)", () => {
    expect(isNewFailureAt([he("passed", "r1")], 0)).toBe(false);
    expect(isNewFailureAt([he("failed", "r1")], 0)).toBe(true); // a new test
    expect(isNewFailureAt([he("passed", "r1"), he("timedOut", "r3")], 1)).toBe(true);
    expect(isNewFailureAt([he("failed", "r1"), he("failed", "r3")], 1)).toBe(false);
    // the entry of a partial run (s2) is skipped: the previous full run passed
    expect(isNewFailureAt([he("passed", "r1"), he("failed", "s2"), he("failed", "r3")], 2)).toBe(true);
    // entries without a runId count as full runs
    expect(isNewFailureAt([he("passed", "r1"), he("failed"), he("failed", "r3")], 2)).toBe(false);
    // only partial-run entries before: the last one is used
    expect(isNewFailureAt([he("failed", "s2"), he("failed", "r3")], 1)).toBe(false);
    expect(isNewFailureAt([], 0)).toBe(false);
  });

  it("isNewFailure: settled results and a run in progress", () => {
    const at = "2026-10-03T10:00:05Z";
    const t = testNode({ outcome: "failed", lastRunAt: at, history: [he("passed", "r1"), he("failed", "r3", at)] });
    expect(isNewFailure(t)).toBe(true);
    expect(isNewFailure({ ...t, outcome: "passed" })).toBe(false);
    expect(isNewFailure({ ...t, history: [he("failed", "r1"), he("failed", "r3", at)] })).toBe(false);
    // the current run's result is not in history yet: compare with the whole history
    const running = runSummary({ runId: "r4", startedAt: "2026-10-04T10:00:00Z" });
    const live = testNode({ outcome: "failed", lastRunAt: "2026-10-04T10:00:01Z", history: [he("failed", "r1"), he("passed", "r3")] });
    expect(isNewFailure(live, running)).toBe(true);
    expect(isNewFailure(live)).toBe(false);
    expect(isNewFailure(testNode({ outcome: "failed" }))).toBe(true);
  });

  it("newFailureKeys: keys of live new failures", async () => {
    await setState({
      tests: [
        testNode({ id: "A.B.New", outcome: "failed", lastRunAt: "x", history: [he("passed", "r1"), he("failed", "r3", "x")] }),
        testNode({ id: "A.B.Old", outcome: "failed", lastRunAt: "x", history: [he("failed", "r1"), he("failed", "r3", "x")] }),
        testNode({ id: "A.B.Ok", outcome: "passed" }),
      ],
    });
    expect([...newFailureKeys()]).toEqual(["P1|A.B.New"]);
  });

  it("runDeltas: per run, new failures and fixes (the oldest entry has no predecessor)", async () => {
    await setState({
      tests: [
        testNode({ id: "A.B.One", history: [he("passed", "r1"), he("failed", "r3")] }),
        testNode({ id: "A.B.Two", history: [he("failed", "r1"), he("passed", "r3"), he("passed")] }),
        testNode({ id: "A.B.Three" }),
      ],
    });
    const d = runDeltas();
    expect(d.get("r1")).toEqual({ newFailures: 0, fixed: 0 });
    expect(d.get("r3")).toEqual({ newFailures: 1, fixed: 1 });
    expect(d.size).toBe(2);
  });

  it("isNewlySlow: > 2× and > 50 ms slower", () => {
    expect(isNewlySlow(100, 250)).toBe(true);
    expect(isNewlySlow(10, 30)).toBe(false); // 3× but only 20 ms
    expect(isNewlySlow(100, 180)).toBe(false);
    expect(isNewlySlow(undefined, 500)).toBe(false);
    expect(isNewlySlow(100, undefined)).toBe(false);
  });

  it("runRefLabel: Live, a stored run's time, a pruned run", () => {
    expect(runRefLabel(LIVE, runs)).toBe("Live");
    expect(runRefLabel("r1", runs)).not.toBe("");
    expect(runRefLabel("gone", runs)).toBe("a pruned run");
  });
});

describe("diffRuns", () => {
  const base = [rr("fixed", "failed"), rr("still", "failed"), rr("newfail", "passed"), rr("slow", "passed", 100), rr("slower", "passed", 100), rr("gone", "passed"), rr("skipgone", "skipped"), rr("queued", "running")];
  const target = [
    rr("fixed", "passed", 5),
    rr("still", "timedOut", 1, "again"),
    rr("newfail", "failed", 1, "boom\nstack"),
    rr("slow", "passed", 300),
    rr("slower", "passed", 900),
    rr("brandnew", "failed"),
    rr("skip", "skipped"),
  ];

  it("classifies every test", () => {
    const d = diffRuns(base, target);
    const keys = (xs: { key: string }[]) => xs.map((x) => x.key.split("|")[1]);
    expect(keys(d.newFailures)).toEqual(["newfail", "brandnew"]);
    expect(keys(d.fixed)).toEqual(["fixed"]);
    expect(keys(d.stillFailing)).toEqual(["still"]);
    expect(keys(d.newlySlow)).toEqual(["slower", "slow"]); // biggest factor first
    expect(keys(d.notRun)).toEqual(["gone", "skipgone"]);
  });

  it("after a partial base, a failure without a base result is unknown (not new)", () => {
    const d = diffRuns(base, target, false);
    expect(d.newFailures.map((x) => x.key)).toEqual(["P1|newfail"]);
  });
});

describe("comparison state", () => {
  const details = (run: RunSummary, results: RunResult[]): RunDetails => ({ run, results });

  it("openCompare: the older run becomes the base; details are fetched once; LIVE uses the live tests", async () => {
    await setState({ tests: [testNode({ id: "A.B.T", outcome: "failed" }), testNode({ id: "A.B.Q", outcome: "queued" })] });
    let resolve!: (d: RunDetails | null) => void;
    transport.fetchRun.mockReturnValue(new Promise((r) => (resolve = r)));
    openCompare(LIVE, "r1");
    expect(comparison()).toEqual({ base: "r1", target: LIVE });
    expect(comparisonOpen()).toBe(true);
    expect(comparisonResult()).toEqual({ status: "loading" });
    openCompare("r1", LIVE); // still loading: no second fetch
    expect(transport.fetchRun).toHaveBeenCalledTimes(1);
    resolve(details(runs[2], [rr("A.B.T", "passed")]));
    await flushMicrotasks();
    const r = comparisonResult();
    expect(r?.status).toBe("ready");
    expect(r?.status === "ready" && r.diff.newFailures.map((x) => x.key)).toEqual(["P1|A.B.T"]);
    closeCompare();
    expect(comparisonOpen()).toBe(false);
    expect(comparison()).toEqual({ base: "r1", target: LIVE });
  });

  it("missing runs (null / fetch error) are reported", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    transport.fetchRun.mockImplementation(async (id: string) => {
      if (id === "r3") throw new Error("offline");
      return null;
    });
    openCompare("r3", "pruned-x");
    await flushMicrotasks();
    // "pruned-x" is not in the run list: start 0 → the base
    expect(comparison()).toEqual({ base: "pruned-x", target: "r3" });
    expect(comparisonResult()).toEqual({ status: "missing", which: ["pruned-x", "r3"] });
    expect(warn).toHaveBeenCalled();
  });
});

describe("<lc-run-diff>", () => {
  const tests = [testNode({ id: "Ns.C.Fixed" }), testNode({ id: "Ns.C.Broke" }), testNode({ id: "Ns.C.Slow" }), testNode({ id: "Ns.C.Still" })];
  // fetched run details are cached for the module's lifetime (runs never change): fresh run ids per test
  let n = 0;

  async function open(baseResults: RunResult[], targetResults: RunResult[]) {
    n++;
    const baseRun = runSummary({ runId: `b${n}`, startedAt: "2026-09-01T10:00:00Z", passed: 3, failed: 2, total: 5 });
    const targetRun = runSummary({ runId: `t${n}`, startedAt: "2026-09-02T10:00:00Z", passed: 3, failed: 2, total: 5 });
    await setState({ runs: [targetRun, baseRun, ...runs], tests });
    transport.fetchRun.mockImplementation(async (id: string) =>
      id === baseRun.runId ? { run: baseRun, results: baseResults } : id === targetRun.runId ? { run: targetRun, results: targetResults } : null,
    );
    const before = mount(h("button", null, "before"));
    before.focus();
    openCompare(targetRun.runId, baseRun.runId);
    const onJump = vi.fn();
    const onClose = vi.fn();
    const el = mount(h("lc-run-diff", { onJump, onClose }));
    await vi.waitFor(() => expect(comparisonResult()?.status).toBe("ready"));
    await flushMicrotasks();
    return { el, onJump, onClose, before, base: baseRun.runId, target: targetRun.runId };
  }

  it("a dialog with the pair, summary chips and sections with rows", async () => {
    const { el, onJump } = await open(
      [rr("Ns.C.Fixed", "failed"), rr("Ns.C.Broke", "passed"), rr("Ns.C.Slow", "passed", 100), rr("Ns.C.Still", "failed"), rr("Ns.C.Removed", "passed")],
      [rr("Ns.C.Fixed", "passed", 12), rr("Ns.C.Broke", "failed", 3, "Expected 1\nbut was 2"), rr("Ns.C.Slow", "passed", 400), rr("Ns.C.Still", "failed")],
    );
    expect(el.getAttribute("role")).toBe("dialog");
    expect(el.getAttribute("aria-label")).toBe("Compare runs");
    expect(text(el.querySelector(".run-diff-title"))).toContain("→");
    expect(document.activeElement).toBe(el.querySelector(".run-diff-close"));
    const chips = [...el.querySelectorAll<HTMLButtonElement>(".run-diff-chip")];
    expect(chips.map((c) => text(c))).toEqual(["1 new failure", "1 fixed", "1 still failing", "1 newly slow", "1 not run"]);
    expect(chips.every((c) => !c.disabled && c.classList.contains("nonzero"))).toBe(true);
    const sections = [...el.querySelectorAll<HTMLDetailsElement>(".run-diff-section")];
    expect(sections.map((s) => s.open)).toEqual([true, true, true, true, false]);
    const broke = sections[0].querySelector(".run-diff-row")!;
    expect(text(broke.querySelector(".run-diff-test"))).toBe("Broke");
    expect(text(broke.querySelector(".run-diff-ctx"))).toBe("Ns.C");
    expect(text(broke.querySelector(".run-diff-error"))).toBe("Expected 1");
    expect(text(sections[1].querySelector(".run-diff-detail"))).toBe("12 ms");
    expect(text(sections[3].querySelector(".run-diff-detail"))).toBe("100 ms → 400 ms 4.0×");
    // a test that is no longer discovered
    expect(text(sections[4].querySelector(".run-diff-ctx"))).toBe("no longer discovered");
    expect(text(sections[4].querySelector(".run-diff-test"))).toBe("Removed");
    click(broke);
    expect(onJump).toHaveBeenCalledWith("P1|Ns.C.Broke");
    // a chip opens and focuses its section
    sections[4].open = false;
    click(chips[4]);
    expect(sections[4].open).toBe(true);
    expect(document.activeElement).toBe(sections[4].querySelector("summary"));
  });

  it("empty sections say so; swap and the pickers change the pair", async () => {
    const { el, base, target } = await open([rr("Ns.C.Fixed", "passed")], [rr("Ns.C.Fixed", "passed")]);
    const empties = [...el.querySelectorAll(".run-diff-section")].map((s) => text(s.querySelector(".run-diff-empty")));
    expect(empties[0]).toBe("No new failures.");
    expect(empties[1]).toBe("Nothing was fixed.");
    const selects = el.querySelectorAll<HTMLSelectElement>(".run-diff-select");
    expect(selects[0].getAttribute("aria-label")).toBe("Base");
    expect(selects[0].value).toBe(base);
    expect(selects[1].value).toBe(target);
    expect(selects[0].querySelectorAll("option")).toHaveLength(1 + 5);
    click(el.querySelector(".run-diff-swap")!);
    expect(comparison()).toEqual({ base: target, target: base });
    expect(selects[0].value).toBe(target);
    selects[1].value = LIVE;
    selects[1].dispatchEvent(new Event("change"));
    expect(comparison()).toEqual({ base: target, target: LIVE });
    selects[0].value = "r1";
    selects[0].dispatchEvent(new Event("change"));
    expect(comparison()).toEqual({ base: "r1", target: LIVE });
  });

  it("caps long sections at 200 rows", async () => {
    const many = Array.from({ length: 205 }, (_, i) => `Ns.C.T${i}`);
    const { el } = await open(
      many.map((id) => rr(id, "passed")),
      many.map((id) => rr(id, "failed")),
    );
    const first = el.querySelector(".run-diff-section")!;
    expect(first.querySelectorAll(".run-diff-row")).toHaveLength(200);
    expect(text(first.querySelectorAll(".run-diff-empty")[0])).toBe("+5 more");
  });

  it("Escape and the close button close it (Escape doesn't reach the app); focus goes back", async () => {
    const { el, onClose, before } = await open([], []);
    const outer = vi.fn();
    window.addEventListener("keydown", outer);
    key(el.querySelector(".run-diff-close")!, "Escape");
    key(el, "Enter");
    window.removeEventListener("keydown", outer);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(outer).toHaveBeenCalledTimes(1); // only Enter
    click(el.querySelector(".run-diff-close")!);
    expect(onClose).toHaveBeenCalledTimes(2);
    el.remove();
    await flushMicrotasks();
    expect(document.activeElement).toBe(before);
  });

  it("loading and missing states", async () => {
    const pending: ((d: RunDetails | null) => void)[] = [];
    transport.fetchRun.mockImplementation(() => new Promise((r) => pending.push(r)));
    openCompare("x-old", "x-new");
    const el = mount(h("lc-run-diff", {}));
    expect(text(el.querySelector(".run-diff-status"))).toBe("Loading runs…");
    expect(el.querySelector<HTMLElement>(".run-diff-sections")!.hidden).toBe(true);
    expect(el.querySelector<HTMLButtonElement>(".run-diff-chip")!.disabled).toBe(true);
    expect(text(el.querySelector(".run-diff-chip-count"))).toBe("–");
    expect(pending).toHaveLength(2);
    for (const r of pending) r(null);
    await flushMicrotasks();
    await vi.waitFor(() => expect(el.querySelector(".run-diff-status.warn")).toBeTruthy());
    expect(text(el.querySelector(".run-diff-status.warn"))).toContain("no longer stored. Pick another run.");
  });

  it("without a comparison the title is empty and the chips are disabled", async () => {
    const el = mount(h("lc-run-diff", {}));
    expect(text(el.querySelector(".run-diff-title"))).toBe("");
    click(el.querySelector(".run-diff-swap")!);
    const sel = el.querySelector<HTMLSelectElement>(".run-diff-select")!;
    sel.dispatchEvent(new Event("change"));
    expect(comparison()).toBeUndefined();
  });
});
