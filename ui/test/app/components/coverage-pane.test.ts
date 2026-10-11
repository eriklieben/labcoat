import { afterEach, describe, expect, it, vi } from "vitest";
import { h, root } from "@eriklieben/labcoat-design-system";
import "../../../src/components/coverage-pane";
import { coverageSettingsLabel, coverageSettingsTitle, isSubset, meter } from "../../../src/components/coverage-pane";
import type { CoverageReport } from "../../../src/contract";
import { localSettings, settings } from "../../../src/settings";
import type { CoverageTarget } from "../../../src/store";
import { asFull, click, coverageReport, fileCoverage, flushFrame, key, makeStore, mount, runSummary, stateEvent, testNode, text, type } from "../../helpers/app";
import { flushMicrotasks } from "../../helpers/dom";

afterEach(() => localSettings.set({}));

const twoModules = () =>
  coverageReport({
    runId: "full-1",
    projectIds: ["P1"],
    files: [
      fileCoverage({ path: "/src/Lib/Calc.cs", relativePath: "Lib/Calc.cs", module: "Lib", linesCovered: 3, linesValid: 4, lineRate: 0.75, branchRate: 0.5 }),
      fileCoverage({ path: "/src/Lib/Zeta.cs", relativePath: "Lib/Zeta.cs", module: "Lib", linesCovered: 1, linesValid: 10, lineRate: 0.1, branchesCovered: 0, branchesValid: 0, branchRate: 0 }),
      fileCoverage({ path: "/src/Api/Orders.cs", relativePath: "Api/Orders.cs", module: "Api", linesCovered: 8, linesValid: 8, lineRate: 1, branchesCovered: 2, branchesValid: 2, branchRate: 1 }),
    ],
  });

const subsetReport = (p: Partial<CoverageReport> = {}) =>
  coverageReport({
    runId: "sub-1",
    scope: "subset",
    testIds: ["Ns.ClassA.Test1"],
    files: [
      fileCoverage({ path: "/src/Lib/Calc.cs", relativePath: "Lib/Calc.cs", module: "Lib" }),
      fileCoverage({ path: "/src/Lib/Other.cs", relativePath: "Lib/Other.cs", module: "Lib", linesCovered: 0, lineRate: 0 }),
    ],
    ...p,
  });

const target = (p: Partial<CoverageTarget> = {}): CoverageTarget => ({
  key: "t:P1|Ns.ClassA.Test1",
  label: { title: "Test1", context: "Ns.ClassA" },
  testIds: ["Ns.ClassA.Test1"],
  projectId: "P1",
  isTest: true,
  ...p,
});

function pane(props: Record<string, unknown> = {}) {
  const onOpen = vi.fn();
  const el = mount(h("lc-coverage-pane", { onOpen, ...props }));
  return { el, onOpen };
}

const rows = (el: Element, sel = "tr.cov-file") => [...el.querySelectorAll(sel)].map((r) => text(r.querySelector(".col-file")));

describe("helpers", () => {
  it("isSubset reads the scope (missing = all)", () => {
    expect(isSubset(undefined)).toBe(false);
    expect(isSubset(coverageReport())).toBe(false);
    expect(isSubset(coverageReport({ scope: "all" }))).toBe(false);
    expect(isSubset(coverageReport({ scope: "subset" }))).toBe(true);
  });
  it("meter() builds a labelled design-system meter", () => {
    root((dispose) => {
      const m = meter(0.42, "sm", "Lines");
      expect(m.classList.contains("meter")).toBe(true);
      expect(m.outerHTML).toContain("Lines");
      dispose();
    });
  });
});

describe("<lc-coverage-pane> empty states", () => {
  it("coverage off: invites to turn it on (setCoverage)", async () => {
    const { store, transport } = makeStore({ state: {} });
    await flushFrame();
    const { el } = pane();
    expect(el.classList.contains("coverage-pane")).toBe(true);
    expect(text(el.querySelector(".empty-title"))).toBe("Coverage is not being collected");
    click(el.querySelector(".btn-accent")!);
    expect(transport.sent).toContainEqual({ type: "setCoverage", enabled: true });
    expect(store.get().coverageEnabled).toBe(true);
    await flushFrame();
    expect(text(el.querySelector(".empty-title"))).toBe("No coverage collected yet");
  });

  it("coverage on: Run all (disabled while a run is in progress)", async () => {
    const { store, transport } = makeStore({ state: { coverageEnabled: true } });
    await flushFrame();
    const { el } = pane();
    const btn = el.querySelector<HTMLButtonElement>(".btn-accent")!;
    expect(text(btn)).toContain("Run all with coverage");
    expect(btn.disabled).toBe(false);
    click(btn);
    expect(transport.sent).toContainEqual({ type: "run" });
    store.apply({ type: "runStarted", run: runSummary({ runId: "r9" }), testIds: [] });
    await flushFrame();
    expect(el.querySelector<HTMLButtonElement>(".btn-accent")!.disabled).toBe(true);
  });
});

describe("<lc-coverage-pane> with a full report", () => {
  it("shows the overview: header, cards, module-grouped file table", async () => {
    makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    await flushFrame();
    const { el } = pane();
    const ov = el.querySelector("lc-coverage-overview")!;
    expect(ov).toBeTruthy();
    expect(text(ov.querySelector(".kind-label"))).toBe("Code coverage · all tests");
    expect(text(ov.querySelector(".detail-title"))).toBe("All tests");
    expect(text(ov.querySelector(".cov-meta"))).toContain("full-1");
    expect(ov.querySelector(".project-tag")?.textContent).toBe("P1");
    // cards: lines / branches / files
    const cards = [...ov.querySelectorAll(".cov-card")];
    expect(cards).toHaveLength(3);
    expect(text(cards[0].querySelector(".cov-card-fraction"))).toBe("3 / 4 lines");
    expect(cards[0].className).toContain("tone-warn");
    expect(text(cards[2].querySelector(".cov-card-value"))).toBe("3");
    expect(text(cards[2].querySelector(".cov-card-fraction"))).toBe("2 modules");
    // modules sorted by name, files below them
    expect([...ov.querySelectorAll(".cov-module-name")].map((x) => x.textContent)).toEqual(["Api", "Lib"]);
    expect(rows(ov)).toEqual(["Api/Orders.cs", "Lib/Calc.cs", "Lib/Zeta.cs"]);
    // module totals: Lib = 4 / 14 lines → 10 uncovered
    const lib = [...ov.querySelectorAll("tr.cov-module")][1];
    expect(text(lib.querySelector(".uncov-count"))).toBe("10");
    expect(text(lib.querySelector(".cnt-total"))).toBe("2");
    // a file without branches shows a dash
    const zeta = [...ov.querySelectorAll("tr.cov-file")][2];
    expect(text(zeta.querySelectorAll(".col-rate")[1])).toBe("—");
    // fully covered file: "0 / 8"
    const orders = ov.querySelector("tr.cov-file")!;
    expect(text(orders.querySelector(".col-num"))).toBe("0 / 8");
  });

  it("says when Collect coverage is off and when it is updating after a run", async () => {
    const { store } = makeStore({ state: { coverageEnabled: false, ...asFull(twoModules()) } });
    await flushFrame();
    const { el } = pane();
    expect(text(el.querySelector(".row-pill.err"))).toBe("Collect coverage is off — showing the last report");
    store.apply(stateEvent({ coverageEnabled: true, ...asFull(twoModules()), currentRun: runSummary({ runId: "r2" }) }));
    await flushFrame();
    expect(el.querySelector(".row-pill.err")).toBeNull();
    expect(text(el.querySelector(".row-pill.busy"))).toBe("Updating after run");
  });

  it("sorts by column (aria-sort, toggling direction)", async () => {
    makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    await flushFrame();
    const { el } = pane();
    const ths = [...el.querySelectorAll("th")];
    expect(ths[0].getAttribute("aria-sort")).toBe("ascending");
    expect(ths[1].getAttribute("aria-sort")).toBe("none");
    click(ths[1].querySelector("button")!); // lines ascending
    expect(ths[1].getAttribute("aria-sort")).toBe("ascending");
    expect(ths[1].querySelector("button")!.classList.contains("sorted")).toBe(true);
    expect(rows(el)).toEqual(["Api/Orders.cs", "Lib/Zeta.cs", "Lib/Calc.cs"]);
    click(ths[1].querySelector("button")!); // lines descending
    expect(ths[1].getAttribute("aria-sort")).toBe("descending");
    expect(rows(el)).toEqual(["Api/Orders.cs", "Lib/Calc.cs", "Lib/Zeta.cs"]);
    click(ths[2].querySelector("button")!); // branches: files without branches last
    expect(rows(el)).toEqual(["Api/Orders.cs", "Lib/Calc.cs", "Lib/Zeta.cs"]);
    click(ths[3].querySelector("button")!); // uncovered ascending
    expect(rows(el)).toEqual(["Api/Orders.cs", "Lib/Calc.cs", "Lib/Zeta.cs"]);
    click(ths[3].querySelector("button")!); // uncovered descending
    expect(rows(el)).toEqual(["Api/Orders.cs", "Lib/Zeta.cs", "Lib/Calc.cs"]);
    click(ths[0].querySelector("button")!); // file ascending again
    expect(ths[0].getAttribute("aria-sort")).toBe("ascending");
  });

  it("filters by path or module; Escape and the clear button reset", async () => {
    makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    await flushFrame();
    const { el } = pane();
    const input = el.querySelector<HTMLInputElement>('input[type="search"]')!;
    expect(input.getAttribute("aria-label")).toBe("Filter files");
    type(input, "zeta");
    expect(rows(el)).toEqual(["Lib/Zeta.cs"]);
    type(input, "api");
    expect(rows(el)).toEqual(["Api/Orders.cs"]);
    type(input, "nothing-matches");
    expect(rows(el)).toEqual([]);
    expect(text(el.querySelector(".cov-empty"))).toBe("No files match “nothing-matches”.");
    key(input, "Escape");
    expect(rows(el)).toHaveLength(3);
    type(input, "calc");
    click(el.querySelector(".search-clear")!);
    expect(rows(el)).toHaveLength(3);
    expect(el.querySelector(".search-clear")).toBeNull();
  });

  it("collapses and expands a module", async () => {
    makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    await flushFrame();
    const { el } = pane();
    const api = el.querySelector("tr.cov-module")!;
    expect(api.getAttribute("aria-expanded")).toBe("true");
    click(api);
    expect(api.getAttribute("aria-expanded")).toBe("false");
    expect(rows(el)).toEqual(["Lib/Calc.cs", "Lib/Zeta.cs"]);
    click(api);
    expect(rows(el)).toHaveLength(3);
  });

  it("opens a file by click or Enter (onOpen), then shows the source view for openPath", async () => {
    makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    await flushFrame();
    const { el, onOpen } = pane();
    const fileRows = el.querySelectorAll<HTMLElement>("tr.cov-file");
    click(fileRows[0]);
    expect(onOpen).toHaveBeenLastCalledWith("/src/Api/Orders.cs");
    key(fileRows[1], "Enter");
    expect(onOpen).toHaveBeenLastCalledWith("/src/Lib/Calc.cs");
    key(fileRows[1], "a");
    expect(onOpen).toHaveBeenCalledTimes(2);
    el.openPath = "/src/Lib/Calc.cs";
    const src = el.querySelector("lc-coverage-source")!;
    expect(src).toBeTruthy();
    expect(text(src.querySelector(".detail-title"))).toBe("Calc.cs");
    // back to the table
    click(src.querySelector(".crumb-btn")!);
    expect(onOpen).toHaveBeenLastCalledWith(undefined);
    el.openPath = "/src/unknown.cs";
    expect(el.querySelector("lc-coverage-overview")).toBeTruthy();
  });

  it("shows 'No coverable lines' for an empty report (Round 4 noCoverableLines)", async () => {
    const empty = coverageReport({ linesValid: 0, linesCovered: 0, lineRate: 0, branchesValid: 0, branchesCovered: 0, branchRate: 0, noCoverableLines: true, files: [] });
    makeStore({ state: { coverageEnabled: true, coverage: empty } });
    await flushFrame();
    const { el } = pane();
    const values = [...el.querySelectorAll(".cov-card-value")].map((v) => text(v));
    expect(values.slice(0, 2)).toEqual(["No coverable lines", "No coverable branches"]);
    expect(el.querySelector(".cov-card")!.className).toContain("tone-none");
    expect(text(el.querySelector(".cov-empty"))).toBe("No product code was covered by this run.");
  });

  it("counts the summary percentage up on first display (decorative), immediately when busy", async () => {
    vi.useFakeTimers();
    makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    await flushFrame();
    const { el } = pane();
    const value = () => text(el.querySelector(".cov-card-value"));
    expect(value()).toBe("0.0%");
    for (let i = 0; i < 40; i++) vi.advanceTimersToNextFrame();
    expect(value()).toBe("75.0%");
    el.remove();
    await flushMicrotasks();
    document.documentElement.classList.add("lc-busy");
    const again = pane().el;
    expect(text(again.querySelector(".cov-card-value"))).toBe("75.0%");
  });
});

describe("<lc-coverage-pane> selections", () => {
  it("found: the selection's own report with a scope banner; Show full coverage goes back", async () => {
    const { store, transport } = makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    transport.fetchTestCoverage.mockResolvedValue(subsetReport());
    await flushFrame();
    const { el, onOpen } = pane();
    store.selectCoverage(target());
    await vi.waitFor(() => expect(store.get().coverageSelection?.status).toBe("found"));
    await flushFrame();
    const banner = el.querySelector(".messagebar.cov-scope")!;
    expect(text(banner.querySelector(".mb-title"))).toBe("Coverage for Test1");
    expect(text(banner)).toContain("Ns.ClassA");
    const ov = el.querySelector("lc-coverage-overview")!;
    expect(text(ov.querySelector(".kind-label"))).toBe("Code coverage · Ns.ClassA");
    expect(text(ov.querySelector(".detail-title"))).toBe("Test1");
    // only touched files by default; "Show all files" reveals the rest
    expect(rows(ov)).toEqual(["Lib/Calc.cs"]);
    expect(text(ov.querySelector(".cov-only-toggle"))).toContain("(1 not touched)");
    const toggle = ov.querySelector<HTMLInputElement>(".cov-only-toggle input")!;
    expect(toggle.checked).toBe(false);
    toggle.checked = true;
    toggle.dispatchEvent(new Event("change"));
    expect(rows(ov)).toEqual(["Lib/Calc.cs", "Lib/Other.cs"]);
    // back to the full report
    click(banner.querySelector(".mb-action")!);
    expect(text(banner.querySelector(".mb-action"))).toBe("Show full coverage");
    expect(onOpen).toHaveBeenCalledWith(undefined);
    await flushFrame();
    expect(store.get().coverageSelection).toBeUndefined();
    expect(el.querySelector(".messagebar.cov-scope")).toBeNull();
  });

  it("found but stale: a Recollect chip requests coverage for the same tests", async () => {
    const { store, transport } = makeStore({ state: { coverageEnabled: true } });
    transport.fetchTestCoverage.mockResolvedValue(subsetReport({ stale: true }));
    await flushFrame();
    const { el } = pane();
    store.selectCoverage(target());
    await vi.waitFor(() => expect(store.get().coverageSelection?.status).toBe("found"));
    await flushFrame();
    const chip = el.querySelector(".cov-stale-chip")!;
    expect(text(chip)).toContain("Code changed since");
    // no full report: the banner offers "Run all with coverage"
    const action = el.querySelector<HTMLButtonElement>(".messagebar .mb-action")!;
    expect(text(action)).toBe("Run all with coverage");
    click(action);
    expect(transport.sent).toContainEqual({ type: "run", coverage: true });
    click(chip.querySelector(".cov-stale-action")!);
    expect(transport.sent).toContainEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test1"], coverage: true });
    expect(store.get().coverageRequest?.label).toEqual({ title: "Test1", context: "Ns.ClassA" });
  });

  it("missing test: invites to collect (never automatic); Show full coverage when a full report exists", async () => {
    const { store, transport } = makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    await flushFrame();
    const { el } = pane();
    store.selectCoverage(target());
    await vi.waitFor(() => expect(store.get().coverageSelection?.status).toBe("missing"));
    await flushFrame();
    const m = el.querySelector(".cov-missing")!;
    expect(text(m.querySelector(".empty-title"))).toBe("No coverage collected for Test1 yet");
    expect(text(m)).toContain("Collecting runs just this test");
    const collect = m.querySelector<HTMLButtonElement>(".btn-accent")!;
    expect(text(collect)).toBe("Collect coverage for this test");
    expect(collect.title).toBe("Run this test with coverage (C)");
    expect(transport.sent).toEqual([]);
    const full = [...m.querySelectorAll<HTMLButtonElement>(".btn")].find((b) => text(b) === "Show full coverage")!;
    click(collect);
    expect(transport.sent).toContainEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test1"], coverage: true });
    store.dismissCoverageRequest();
    click(full);
    await flushFrame();
    expect(el.querySelector("lc-coverage-overview")).toBeTruthy();
  });

  it("missing class without a full report: the missing state names the tests", async () => {
    const { store } = makeStore({ state: { coverageEnabled: true } });
    await flushFrame();
    const { el } = pane();
    store.selectCoverage(target({ key: "c:Ns.ClassA", label: { title: "ClassA" }, testIds: ["a", "b"], isTest: false }));
    await vi.waitFor(() => expect(store.get().coverageSelection?.status).toBe("missing"));
    await flushFrame();
    expect(text(el.querySelector(".cov-missing .empty-sub"))).toContain("Collecting runs these 2 tests");
    expect(text(el.querySelector(".cov-missing .btn-accent"))).toBe("Collect coverage for ClassA");
  });

  it("missing class with a full report: a fallback banner above the full overview", async () => {
    const { store, transport } = makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    await flushFrame();
    const { el } = pane();
    store.selectCoverage(target({ key: "c:Ns.ClassA", label: { title: "ClassA" }, testIds: ["a", "b"], isTest: false }));
    await vi.waitFor(() => expect(store.get().coverageSelection?.status).toBe("missing"));
    await flushFrame();
    const fb = el.querySelector(".cov-fallback")!;
    expect(text(fb)).toContain("Showing coverage for all tests — ClassA has no coverage of its own yet");
    const btn = fb.querySelector<HTMLButtonElement>(".mb-action")!;
    expect(btn.title).toBe("Run the 2 tests of ClassA with coverage");
    expect(el.querySelector("lc-coverage-overview")).toBeTruthy();
    click(btn);
    expect(transport.sent).toContainEqual({ type: "run", projectId: "P1", testIds: ["a", "b"], coverage: true });
  });

  it("a slow lookup keeps the previous view for 300 ms, then says 'Looking up…'", async () => {
    vi.useFakeTimers();
    const { store, transport } = makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    transport.fetchTestCoverage.mockReturnValue(new Promise(() => {}));
    vi.advanceTimersToNextFrame();
    const { el } = pane();
    store.selectCoverage(target(), 50);
    vi.advanceTimersToNextFrame();
    await flushMicrotasks();
    // first lookup without a previous selection: the lookup state at once
    expect(el.querySelector(".cov-lookup")).toBeTruthy();
    expect(text(el.querySelector(".cov-lookup .empty-title"))).toBe("Looking up coverage for Test1…");
    expect(text(el.querySelector(".cov-lookup .empty-sub"))).toBe("Ns.ClassA");
  });

  it("a lookup after a known selection keeps showing it until 300 ms passed", async () => {
    vi.useFakeTimers();
    const { store, transport } = makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()) } });
    vi.advanceTimersToNextFrame();
    const { el } = pane();
    store.selectCoverage(target()); // → missing
    vi.advanceTimersByTime(1);
    await flushMicrotasks();
    vi.advanceTimersToNextFrame();
    expect(el.querySelector(".cov-missing")).toBeTruthy();
    transport.fetchTestCoverage.mockReturnValue(new Promise(() => {}));
    store.selectCoverage(target({ key: "t:P1|x", testIds: ["x"], label: { title: "X" } }), 100);
    vi.advanceTimersToNextFrame();
    expect(el.querySelector(".cov-missing")).toBeTruthy();
    expect(el.querySelector(".cov-lookup")).toBeNull();
    vi.advanceTimersByTime(300);
    await flushMicrotasks();
    expect(el.querySelector(".cov-lookup")).toBeTruthy();
  });
});

describe("<lc-coverage-pane> coverage requests", () => {
  it("collecting for the selection, then 'No coverage collected' with Dismiss", async () => {
    const { store } = makeStore({ state: { coverageEnabled: true, tests: [testNode()] } });
    await flushFrame();
    const { el } = pane();
    store.requestCoverage({ title: "Test1" }, ["Ns.ClassA.Test1"], "P1");
    await flushFrame();
    expect(text(el.querySelector(".empty-title"))).toBe("Collecting coverage for Test1…");
    expect(el.querySelector(".spinner.large")).toBeTruthy();
    store.apply({ type: "runStarted", run: runSummary({ runId: "cov-run" }), testIds: ["Ns.ClassA.Test1"] });
    store.apply({ type: "runFinished", run: runSummary({ runId: "cov-run" }) });
    await flushFrame();
    expect(store.get().coverageRequest?.status).toBe("noResult");
    expect(text(el.querySelector(".empty-title"))).toBe("No coverage collected for Test1");
    expect(text(el.querySelector(".empty-sub"))).toContain("The run finished without a coverage report");
    click([...el.querySelectorAll<HTMLButtonElement>(".btn")].find((b) => text(b) === "Dismiss")!);
    await flushFrame();
    expect(store.get().coverageRequest).toBeUndefined();
    expect(text(el.querySelector(".empty-title"))).toBe("No coverage collected yet");
  });

  it("a request while another report shows: a progress bar above it", async () => {
    const { store } = makeStore({ state: { coverageEnabled: true, ...asFull(twoModules()), tests: [testNode()] } });
    await flushFrame();
    const { el } = pane();
    store.requestCoverage({ title: "ClassB", context: "Ns" }, ["b1"], "P1");
    await flushFrame();
    const bar = el.querySelector(".messagebar.cov-scope")!;
    expect(bar.getAttribute("role")).toBe("status");
    expect(text(bar)).toBe("Collecting coverage for ClassB…");
    expect(bar.querySelector("b")!.title).toBe("Ns");
    expect(bar.querySelector(".cov-scope-spinner")).toBeTruthy();
    expect(el.querySelector("lc-coverage-overview")).toBeTruthy();
    store.apply({ type: "runStarted", run: runSummary({ runId: "rb" }), testIds: ["b1"] });
    store.apply({ type: "runFinished", run: runSummary({ runId: "rb" }) });
    await flushFrame();
    expect(bar.classList.contains("mb-warning")).toBe(true);
    expect(text(bar)).toBe("No coverage was collected for ClassB. See the output panel for details.");
    click(bar.querySelector('[aria-label="Dismiss"]')!);
    await flushFrame();
    expect(el.querySelector(".messagebar.cov-scope")).toBeNull();
  });

  it("an unlabelled leftover subset report is not shown as 'all tests'", async () => {
    const { store } = makeStore({ state: { coverageEnabled: true } });
    store.apply({ type: "coverageUpdated", coverage: subsetReport({ testIds: ["a", "b"] }) });
    await flushFrame();
    // labelled by the store ("2 tests"): shown with that label
    const { el } = pane();
    expect(text(el.querySelector(".messagebar .mb-title"))).toBe("Coverage for 2 tests");
    expect(text(el.querySelector(".detail-title"))).toBe("2 tests");
  });
});

describe("<lc-coverage-source>", () => {
  const file = () =>
    fileCoverage({
      path: "/src/Lib/Source1.cs",
      relativePath: "Lib/Source1.cs",
      lines: [
        { line: 1, hits: 1 },
        { line: 2, hits: 0 },
        { line: 3, hits: 4 },
        { line: 4, hits: 0, branchesCovered: 0, branchesTotal: 2 },
      ],
    });

  async function source(f = file(), extra: Record<string, unknown> = {}) {
    const ctx = makeStore({ state: { coverageEnabled: true, ...asFull(coverageReport({ files: [f] })), tests: [testNode()] } });
    await flushFrame();
    const onBack = vi.fn();
    const el = mount(h("lc-coverage-source", { file: f, report: coverageReport({ files: [f] }), onBack, ...extra }));
    await vi.waitFor(() => expect(el.querySelector("[data-line]")).toBeTruthy());
    return { el, onBack, ...ctx };
  }

  it("header: breadcrumb, stats, branch stats, next uncovered, back", async () => {
    const { el, onBack } = await source(undefined, { rootLabel: "All files" });
    expect(el.classList.contains("source-view")).toBe(true);
    expect(text(el.querySelector(".crumb-btn"))).toBe("All files");
    expect(text(el.querySelector(".breadcrumb"))).toContain("Lib");
    expect(text(el.querySelector(".detail-title"))).toBe("Source1.cs");
    expect(text(el.querySelector(".fqn"))).toBe("/src/Lib/Source1.cs");
    const stats = [...el.querySelectorAll(".source-stat")].map((s) => text(s));
    expect(stats[0]).toContain("Lines 75.0%3/4");
    expect(stats[1]).toContain("Branches 50.0%1/2");
    const next = [...el.querySelectorAll<HTMLButtonElement>(".btn")].find((b) => text(b).startsWith("Next uncovered"))!;
    expect(text(next)).toBe("Next uncovered (2)");
    click(next);
    click(next);
    click(next); // wraps to the first one
    click(el.querySelector('[aria-label="Back to all files"]')!);
    click(el.querySelector(".crumb-btn")!);
    expect(onBack).toHaveBeenCalledTimes(2);
  });

  it("no branch stats / next-uncovered for a fully covered file without branches", async () => {
    const f = fileCoverage({ path: "/src/Lib/Full.cs", branchesValid: 0, branchesCovered: 0, lines: [{ line: 1, hits: 2 }] });
    const { el } = await source(f);
    expect(el.querySelectorAll(".source-stat")).toHaveLength(1);
    expect([...el.querySelectorAll(".btn")].some((b) => text(b).startsWith("Next uncovered"))).toBe(false);
  });

  it("tint mode: Gutter only is stored in settings and styles the code view", async () => {
    const { el } = await source();
    const cv = el.querySelector("lc-code-view")!;
    expect(cv.classList.contains("cov-source-code")).toBe(true);
    expect(cv.classList.contains("cov-gutter-only")).toBe(false);
    const seg = el.querySelector('[role="radiogroup"][aria-label="Coverage tint"]')!;
    const radios = [...seg.querySelectorAll<HTMLButtonElement>('[role="radio"]')];
    expect(radios.map((r) => r.getAttribute("aria-checked"))).toEqual(["true", "false"]);
    click(radios[1]);
    expect(settings().coverageTint).toBe("gutter");
    expect(cv.classList.contains("cov-gutter-only")).toBe(true);
  });

  it("clicking a hit count opens the 'tests covering this line' popover; again closes it", async () => {
    const { el, transport } = await source();
    (transport as unknown as { fetchCoverageLine: unknown }).fetchCoverageLine = vi.fn(async () => ({
      tests: [{ projectId: "P1", id: "Ns.ClassA.Test1", source: "perTest", hits: 4 }],
      partial: false,
    }));
    const hitsBtn = el.querySelector<HTMLElement>('[data-line="3"] .src-hits-btn')!;
    expect(hitsBtn).toBeTruthy();
    click(hitsBtn);
    const lt = document.querySelector("lc-coverage-line-tests")!;
    expect(lt).toBeTruthy();
    expect(text(lt.querySelector(".cov-lt-heading-title"))).toBe("Tests covering line 3");
    expect(text(lt.querySelector(".cov-lt-sub"))).toBe("Source1.cs · 4 hits in this report");
    await vi.waitFor(() => expect(lt.querySelector(".cov-lt-row")).toBeTruthy());
    click(hitsBtn);
    const pop = document.querySelector<HTMLElement>(".cov-lt-popover");
    expect(pop === null || pop.inert).toBe(true);
  });

  it("closes the popover from inside (Close button)", async () => {
    const { el, transport } = await source();
    (transport as unknown as { fetchCoverageLine: unknown }).fetchCoverageLine = vi.fn(async () => ({ tests: [], partial: false }));
    click(el.querySelector<HTMLElement>('[data-line="1"] .src-hits-btn')!);
    const lt = document.querySelector("lc-coverage-line-tests")!;
    expect(text(lt.querySelector(".cov-lt-sub"))).toBe("Source1.cs · 1 hit in this report");
    click(lt.querySelector(".cov-lt-close")!);
    const pop = document.querySelector<HTMLElement>(".cov-lt-popover");
    expect(pop === null || pop.inert).toBe(true);
  });
});

describe("coverage settings per project", () => {
  it("labels a settings file, testconfig.json and the defaults", () => {
    expect(
      coverageSettingsLabel({ projectId: "P1", source: "file", path: "/r/P1/coverage.settings.xml", origin: "convention", instrumentation: "dynamic" }),
    ).toBe("coverage.settings.xml (found by name) · dynamic instrumentation");
    expect(coverageSettingsLabel({ projectId: "P1", source: "file", path: "C:\\r\\cov.xml", origin: "option", instrumentation: "static" })).toBe(
      "cov.xml (Coverage.Settings) · static instrumentation",
    );
    expect(coverageSettingsLabel({ projectId: "P1", source: "file", path: "/r/a.runsettings", origin: "msbuild", instrumentation: "dynamic" })).toContain(
      "TestingPlatformCommandLineArguments",
    );
    expect(coverageSettingsLabel({ projectId: "P1", source: "testconfig", path: "/o/P1.testconfig.json", instrumentation: "static" })).toBe(
      "P1.testconfig.json (codeCoverage) · static instrumentation",
    );
    expect(
      coverageSettingsLabel({ projectId: "P1", source: "defaults", modules: ["Api", "Lib"], include: ["Labcoat.*"], exclude: ["Lib"], instrumentation: "dynamic" }),
    ).toBe("Labcoat defaults: Api, Lib · + Labcoat.* · − Lib · dynamic instrumentation");
    expect(coverageSettingsLabel({ projectId: "P1", source: "defaults", instrumentation: "dynamic" })).toContain("all but tests");
    expect(coverageSettingsTitle({ projectId: "P1", source: "file", path: "/r/x.xml", instrumentation: "dynamic" })).toBe("/r/x.xml");
    expect(coverageSettingsTitle({ projectId: "P1", source: "defaults", instrumentation: "dynamic" })).toContain("coverage.settings.xml");
  });

  it("the overview lists them under the report header", async () => {
    makeStore({
      state: {
        coverageEnabled: true,
        coverage: coverageReport({
          runId: "full-1",
          projectIds: ["P1", "P2"],
          settings: [
            { projectId: "P1", source: "file", path: "/r/P1/coverage.settings.xml", origin: "convention", instrumentation: "dynamic" },
            { projectId: "P2", source: "defaults", modules: ["Lib"], instrumentation: "dynamic" },
          ],
        }),
      },
    });
    await flushFrame();
    const { el } = pane();
    const items = [...el.querySelectorAll(".cov-settings .cov-setting")];
    expect(items.map((i) => text(i))).toEqual([
      "P1coverage.settings.xml (found by name) · dynamic instrumentation",
      "P2Labcoat defaults: Lib · dynamic instrumentation",
    ]);
    expect(items[0].getAttribute("title")).toBe("/r/P1/coverage.settings.xml");
  });

  it("no settings (an older report): no list", async () => {
    makeStore({ state: { coverageEnabled: true, ...asFull(coverageReport({ runId: "full-1" })) } });
    await flushFrame();
    const { el } = pane();
    expect(el.querySelector(".cov-settings")).toBeNull();
  });
});
