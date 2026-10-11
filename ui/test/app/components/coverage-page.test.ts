import { describe, expect, it, vi } from "vitest";
import { h } from "@eriklieben/labcoat-design-system";
import { coverageNotes, coverageRunLabel } from "../../../src/components/coverage-page";
import { formatRunTime } from "../../../src/util";
import { asFull, click, coverageReport, fileCoverage, flushFrame, makeStore, mount, runSummary, stateEvent, text } from "../../helpers/app";

const report = () =>
  coverageReport({
    runId: "full-42abcdef99",
    files: [fileCoverage({ path: "/src/Lib/Calc.cs", relativePath: "Lib/Calc.cs" }), fileCoverage({ path: "/src/Lib/Other.cs", relativePath: "Lib/Other.cs" })],
  });

describe("<lc-coverage-page>", () => {
  it("is a labelled region with an empty state that runs all tests with coverage", async () => {
    const { store, transport } = makeStore({ state: {} });
    await flushFrame();
    const el = mount(h("lc-coverage-page"));
    expect(el.getAttribute("role")).toBe("region");
    expect(el.getAttribute("aria-label")).toBe("Coverage report");
    expect(el.classList.contains("coverage-page")).toBe(true);
    expect(text(el.querySelector(".empty-title"))).toBe("No coverage report yet");
    const btn = el.querySelector<HTMLButtonElement>(".btn-accent")!;
    expect(btn.disabled).toBe(false);
    expect(btn.title).toContain("Run all tests once with coverage");
    click(btn);
    expect(transport.sent).toEqual([{ type: "run", coverage: true }]);
    store.apply({ type: "runStarted", run: runSummary({ runId: "rx" }), testIds: [] });
    await flushFrame();
    expect(btn.disabled).toBe(true);
    expect(btn.title).toBe("A run is in progress");
    expect(text(el.querySelector(".cov-hint"))).toBe("A run is in progress…");
  });

  it("shows the last full report (never a subset) with the run label", async () => {
    const { store } = makeStore({ state: { ...asFull(report()), lastRun: runSummary({ runId: "full-42abcdef99", startedAt: "2026-10-01T10:00:00Z" }) } });
    store.apply({ type: "coverageUpdated", coverage: coverageReport({ runId: "sub", scope: "subset", testIds: ["x"] }) });
    await flushFrame();
    const el = mount(h("lc-coverage-page"));
    const ov = el.querySelector("lc-coverage-overview")!;
    expect(text(ov.querySelector(".kind-label"))).toBe("All tests · last full run");
    expect(text(ov.querySelector(".detail-title"))).toBe("Coverage report");
    const meta = text(ov.querySelector(".cov-meta"));
    expect(meta).toContain("from the last run (");
    expect(meta).toContain("full-42a");
    // no "Collect coverage is off" pill on the page
    expect(ov.querySelector(".row-pill.err")).toBeNull();
    // another last run: "from the last full run"
    store.apply({ type: "runFinished", run: runSummary({ runId: "other" }) });
    await flushFrame();
    expect(text(ov.querySelector(".cov-meta"))).toContain("from the last full run");
  });

  it("opens a file locally and goes back to the table (All files)", async () => {
    makeStore({ state: { ...asFull(report()) } });
    await flushFrame();
    const el = mount(h("lc-coverage-page"));
    click(el.querySelectorAll("tr.cov-file")[1]);
    const src = el.querySelector("lc-coverage-source")!;
    expect(src).toBeTruthy();
    expect(text(src.querySelector(".crumb-btn"))).toBe("All files");
    expect(text(src.querySelector(".detail-title"))).toBe("Other.cs");
    click(src.querySelector(".crumb-btn")!);
    expect(el.querySelector("lc-coverage-source")).toBeNull();
    expect(el.querySelector("lc-coverage-overview")).toBeTruthy();
  });

  it("uses the shell's open file when onOpen is given", async () => {
    const { store } = makeStore({ state: { ...asFull(report()) } });
    await flushFrame();
    const onOpen = vi.fn();
    const el = mount(h("lc-coverage-page", { onOpen, openPath: "/src/Lib/Calc.cs" }));
    expect(text(el.querySelector("lc-coverage-source .detail-title"))).toBe("Calc.cs");
    click(el.querySelector(".crumb-btn")!);
    expect(onOpen).toHaveBeenCalledWith(undefined);
    // still open: the shell owns the state
    expect(el.querySelector("lc-coverage-source")).toBeTruthy();
    el.openPath = undefined;
    click(el.querySelector("tr.cov-file")!);
    expect(onOpen).toHaveBeenLastCalledWith("/src/Lib/Calc.cs");
    // a new report re-keys the view
    store.apply(stateEvent({ ...asFull(coverageReport({ runId: "full-2", files: [] })) }));
    await flushFrame();
    expect(text(el.querySelector(".cov-empty"))).toBe("No product code was covered by this run.");
  });

  it("a subset run (e.g. a 4-test watch rerun) keeps the full report and shows its own numbers as a note", async () => {
    const full = coverageReport({ runId: "full-1", linesCovered: 96, linesValid: 100, lineRate: 0.96, files: [fileCoverage()] });
    const { store } = makeStore({ state: { ...asFull(full), lastRun: runSummary({ runId: "full-1" }) } });
    await flushFrame();
    const el = mount(h("lc-coverage-page"));
    const card = () => text(el.querySelector(".cov-card .cov-card-fraction"));
    expect(card()).toBe("96 / 100 lines");
    expect(el.querySelector(".cov-page-notes")).toBeNull();

    const subset = coverageReport({ runId: "watch-1", scope: "subset", testIds: ["a", "b", "c", "d"], linesCovered: 7, linesValid: 97, lineRate: 0.072 });
    store.apply({ type: "runStarted", run: runSummary({ runId: "watch-1" }), testIds: ["a", "b", "c", "d"] });
    store.apply({ type: "coverageUpdated", coverage: subset });
    store.apply({ type: "runFinished", run: runSummary({ runId: "watch-1" }) });
    await flushFrame();
    expect(card()).toBe("96 / 100 lines");
    expect(text(el.querySelector(".cov-meta"))).toContain("from the last full run");
    expect(text(el.querySelector(".cov-page-notes"))).toBe(
      "Last coverage run: 4 tests · 7.2% of lines (only those tests; this report is unchanged). Per-test coverage: the Coverage tab of the Tests page.",
    );
  });

  it("files changed since the report: an out-of-date banner with Re-run with coverage, and marked rows", async () => {
    const full = coverageReport({ runId: "full-1", files: [fileCoverage({ path: "/src/Lib/Calc.cs", relativePath: "Lib/Calc.cs" }), fileCoverage({ path: "/src/Lib/Other.cs", relativePath: "Lib/Other.cs" })] });
    const { store, transport } = makeStore({ state: asFull(full) });
    await flushFrame();
    const el = mount(h("lc-coverage-page"));
    expect(el.querySelector(".cov-stale-banner")).toBeNull();

    // the host re-checks the files after a run: same run id, staleness only
    const stale = { ...full, staleFiles: ["/src/Lib/Calc.cs", "/src/Lib/New.cs"], files: [{ ...full.files[0], stale: true }, full.files[1]] };
    store.apply({ type: "fullCoverageUpdated", coverage: stale });
    await flushFrame();
    const banner = el.querySelector(".cov-stale-banner")!;
    expect(text(banner)).toContain("Out of date: Calc.cs and 1 more file changed since this report. Their numbers and line marks are from before the change.");
    const rows = el.querySelectorAll("tr.cov-file");
    expect(text(rows[0].querySelector(".cov-stale-chip"))).toBe("Changed since");
    expect(rows[1].querySelector(".cov-stale-chip")).toBeNull();
    click(banner.querySelector<HTMLButtonElement>(".mb-action")!);
    expect(transport.sent).toEqual([{ type: "run", coverage: true }]);
    store.apply({ type: "runStarted", run: runSummary({ runId: "r2" }), testIds: [] });
    await flushFrame();
    expect(banner.querySelector<HTMLButtonElement>(".mb-action")!.disabled).toBe(true);

    // the source view of a stale file says so too
    click(rows[0]);
    expect(text(el.querySelector("lc-coverage-source .cov-stale-chip"))).toBe("Changed since this report");
  });

  it("notes: a merged subset run, other merged runs, a one-project run", () => {
    const merged = [
      { runId: "s1", generatedAt: "t", projectId: "P1", testCount: 2, filesChanged: 1 },
      { runId: "s0", generatedAt: "t", projectId: "P1", testCount: 1, filesChanged: 1 },
    ];
    const full = coverageReport({ runId: "s1", merged });
    expect(coverageNotes(full, coverageReport({ runId: "s1", scope: "subset", testIds: ["a"], lineRate: 0.5, linesValid: 10 }))).toEqual([
      "Last coverage run: 1 test · 50.0% of lines (only those tests; the lines they newly covered are merged into this report). Per-test coverage: the Coverage tab of the Tests page.",
      "Includes 1 later run of selected tests, merged into files unchanged since the full run.",
    ]);
    expect(coverageNotes(coverageReport({ runId: "full" }), coverageReport({ runId: "p2", projectIds: ["P2"], linesValid: 0, noCoverableLines: true }))).toEqual([
      "Last coverage run: all tests of P2 · no coverable lines (only those tests; this report is unchanged). Per-test coverage: the Coverage tab of the Tests page.",
    ]);
    expect(coverageNotes(coverageReport({ runId: "full" }), coverageReport({ runId: "full" }))).toEqual([]);
    expect(coverageNotes(coverageReport({ runId: "full" }), undefined)).toEqual([]);
  });

  it("a report restored after a restart says so, with its time, and keeps the stale marks", async () => {
    const at = "2025-01-06T20:54:00Z"; // never today
    const restored = coverageReport({
      runId: "full-1",
      generatedAt: at,
      restoredAt: at,
      staleFiles: ["/src/Lib/Calc.cs"],
      files: [fileCoverage({ path: "/src/Lib/Calc.cs", relativePath: "Lib/Calc.cs", stale: true }), fileCoverage({ path: "/src/Lib/Other.cs", relativePath: "Lib/Other.cs" })],
    });
    const { store } = makeStore({ state: asFull(restored) });
    await flushFrame();
    const el = mount(h("lc-coverage-page"));
    expect(text(el.querySelector(".cov-meta"))).toContain(`from a full run before the restart (${formatRunTime(at)})`);
    expect(text(el.querySelector(".cov-stale-banner"))).toContain("Out of date: Calc.cs changed since this report.");
    expect(text(el.querySelectorAll("tr.cov-file")[0].querySelector(".cov-stale-chip"))).toBe("Changed since");

    // one project ran all its tests since: the rest is still from before the restart
    store.apply({ type: "fullCoverageUpdated", coverage: { ...restored, runId: "full-2", generatedAt: "2026-10-10T08:00:00Z", staleFiles: undefined, files: [] } });
    await flushFrame();
    expect(text(el.querySelector(".cov-meta"))).toContain("from the last full run");
    expect(text(el.querySelector(".cov-page-notes"))).toContain(
      `Part of this report is from a full run before the restart (${formatRunTime(at)}); run all with coverage to refresh it.`,
    );
  });

  it("run label: restored, the last run, or the last full run", () => {
    const at = "2025-01-06T20:54:00Z"; // never today
    expect(coverageRunLabel(coverageReport({ runId: "r", generatedAt: at, restoredAt: at }), { runId: "r" })).toBe(`from a full run before the restart (${formatRunTime(at)})`);
    expect(coverageRunLabel(coverageReport({ runId: "r", generatedAt: "2026-10-10T08:00:00Z", restoredAt: at }), undefined)).toBe("from the last full run");
    // today: "Generated <clock>" already says when
    const now = new Date().toISOString();
    expect(coverageRunLabel(coverageReport({ runId: "r", generatedAt: now, restoredAt: now }), undefined)).toBe("from a full run before the restart");
    expect(coverageRunLabel(coverageReport({ runId: "r" }), { runId: "r", startedAt: "2026-10-10T08:00:00Z" })).toMatch(/^from the last run \(/);
    expect(coverageRunLabel(coverageReport({ runId: "r", merged: [{ runId: "s", generatedAt: "t", projectId: "P", testCount: 1, filesChanged: 1 }] }), { runId: "r" })).toBe(
      "from the last full run",
    );
  });
});
