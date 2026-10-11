/**
 * <lc-coverage-page>: the Coverage report page (nav rail; port of CoveragePage.tsx). The overall report
 * (state.fullCoverage): per project its last full run, like Vitest's / Istanbul's coverage report — never replaced by a
 * run of selected tests (a watch rerun, the Tests page's Coverage tab). Such a run's own numbers show as a note; its
 * newly covered lines in unchanged files are merged in by the host. Files changed since the report are marked stale,
 * with "Re-run with coverage". The host keeps the report across restarts: a restored one says it is from before the
 * restart (`restoredAt`), and files edited meanwhile come back stale. Reads the store directly; the open file is local
 * to the page.
 */
import { computed, define, h, icon, keyed, LabcoatElement, show, signal, type Child } from "@eriklieben/labcoat-design-system";
import type { CoverageReport } from "../contract";
import { getStore } from "../store";
import { fileName, formatClock, formatRunTime, pct } from "../util";
import "./coverage-pane";

const plural = (n: number, one: string) => `${n} ${one}${n === 1 ? "" : "s"}`;

/** " (Yesterday 10:54 PM)" / " (Oct 6, 10:54 PM)"; "" for today. */
const restoredDay = (iso: string) => {
  const when = formatRunTime(iso);
  return when && when !== formatClock(iso) ? ` (${when})` : "";
};

/** "from the last full run" etc. after "Generated <time> · " in the page's header. */
export function coverageRunLabel(full: CoverageReport, lastRun: { runId: string; startedAt?: string } | undefined): string {
  // restored at startup and no project ran all its tests since: the whole report is from before the restart
  // ("Generated <clock>" precedes it: the day only when it is not today)
  if (full.restoredAt && full.restoredAt === full.generatedAt) return `from a full run before the restart${restoredDay(full.restoredAt)}`;
  return lastRun?.runId === full.runId && !full.merged?.length ? `from the last run (${formatClock(lastRun.startedAt)})` : "from the last full run";
}

/** Notes under the page's header: the latest coverage run when it is not this report, and runs merged in. */
export function coverageNotes(full: CoverageReport, last: CoverageReport | undefined): string[] {
  const notes: string[] = [];
  if (full.restoredAt && full.restoredAt !== full.generatedAt)
    notes.push(`Part of this report is from a full run before the restart (${formatRunTime(full.restoredAt)}); run all with coverage to refresh it.`);
  const merged = full.merged ?? [];
  const lastIsSubset = !!last && (last.scope ?? "all") === "subset";
  if (last && (lastIsSubset || last.runId !== full.runId)) {
    const what = lastIsSubset ? plural(last.testIds?.length ?? 0, "test") : `all tests of ${last.projectIds.join(", ")}`;
    const rate = last.noCoverableLines || !last.linesValid ? "no coverable lines" : `${pct(last.lineRate)} of lines`;
    const how = merged.some((m) => m.runId === last.runId)
      ? "only those tests; the lines they newly covered are merged into this report"
      : "only those tests; this report is unchanged";
    notes.push(`Last coverage run: ${what} · ${rate} (${how}). Per-test coverage: the Coverage tab of the Tests page.`);
  }
  const others = merged.filter((m) => m.runId !== last?.runId);
  if (others.length)
    notes.push(`Includes ${plural(others.length, "later run")} of selected tests, merged into files unchanged since the full run.`);
  return notes;
}

export interface CoveragePageProps {
  /** file open in the source viewer (undefined = the file table); omit to keep it local to the page */
  openPath: string | undefined;
  onOpen: ((path: string | undefined) => void) | undefined;
}
export interface CoveragePage extends CoveragePageProps {}

export class CoveragePage extends LabcoatElement<CoveragePageProps> {
  static defaults: CoveragePageProps = { openPath: undefined, onOpen: undefined };
  static hostClass = "detail-pane coverage-pane page-view coverage-page";

  protected render(): Child {
    this.setAttribute("role", "region");
    this.setAttribute("aria-label", "Coverage report");
    const store = getStore();
    const $ = store.$;
    const running = computed(() => !!$.currentRun());
    const report = $.fullCoverage;
    // Open file: the shell's state when it passes onOpen, otherwise local.
    const localPath = signal<string | undefined>(undefined);
    const openPath = () => (this.props.onOpen() ? this.props.openPath() : localPath());
    const open = (p: string | undefined) => {
      const cb = this.props.onOpen.peek();
      if (cb) cb(p);
      else localPath.set(p);
    };
    const file = computed(() => {
      const r = report();
      const p = openPath();
      return r && p ? r.files.find((f) => f.path === p) : undefined;
    });
    const runLabel = computed(() => {
      const r = report();
      return r ? coverageRunLabel(r, $.lastRun()) : "";
    });
    const notes = computed(() => {
      const r = report();
      return r ? coverageNotes(r, $.coverage()) : [];
    });
    const stale = computed(() => report()?.staleFiles ?? []);
    const mode = computed(() => {
      const r = report();
      if (!r) return "empty";
      const f = file();
      // a staleness update keeps the run id: re-key so the table shows the marks
      const tag = `${r.runId}|${r.staleFiles?.length ?? 0}`;
      return f ? `file|${tag}|${f.path}` : `overview|${tag}`;
    });

    const staleBanner = () =>
      h(
        "div",
        { class: "messagebar mb-warning cov-scope cov-stale-banner", role: "status" },
        icon("warning", { size: 18, class: "mb-icon" }),
        h(
          "div",
          { class: "mb-body inline" },
          h(
            "span",
            { class: "mb-ellipsis", title: () => stale().join("\n") },
            h("span", { class: "mb-title" }, "Out of date: "),
            h("span", { class: "cov-scope-name" }, () => fileName(stale()[0] ?? "")),
            () => (stale().length > 1 ? ` and ${plural(stale().length - 1, "more file")}` : ""),
            () => ` changed since this report. ${stale().length > 1 ? "Their" : "Its"} numbers and line marks are from before the change.`,
          ),
        ),
        h(
          "button",
          {
            class: "btn btn-small mb-action",
            onclick: () => store.send({ type: "run", coverage: true }),
            disabled: running,
            title: () => (running() ? "A run is in progress" : "Run all tests once with coverage (the Collect coverage toggle stays as it is)"),
          },
          icon("playFilled", { size: 14 }),
          " Re-run with coverage",
        ),
      );

    return [show(() => stale().length > 0, staleBanner), keyed(mode, (m) => {
      if (m === "empty")
        return h(
          "div",
          { class: "empty-state" },
          h("div", { class: "empty-illustration" }, icon("dataPie", { size: 32 })),
          h("h2", { class: "empty-title" }, "No coverage report yet"),
          h(
            "div",
            { class: "empty-sub" },
            "This page shows the overall line and branch coverage of a full run (all tests), collected with Microsoft.Testing.Extensions.CodeCoverage. Test assemblies are excluded, so you see product code only. Per-test coverage lives in the Coverage tab of the Tests page.",
          ),
          h(
            "div",
            { class: "detail-actions", style: { justifyContent: "center" } },
            h(
              "button",
              {
                class: "btn btn-accent",
                onclick: () => store.send({ type: "run", coverage: true }),
                disabled: running,
                title: () => (running() ? "A run is in progress" : "Run all tests once with coverage (the Collect coverage toggle stays as it is)"),
              },
              icon("playFilled"),
              " Run all with coverage",
            ),
          ),
          show(running, () => h("div", { class: "cov-hint" }, h("span", { class: "spinner" }), h("span", null, "A run is in progress…"))),
        );
      const r = report.peek()!;
      if (m.startsWith("file|"))
        return h("lc-coverage-source", { file: file.peek()!, report: r, rootLabel: "All files", onBack: () => open(undefined) });
      return h("lc-coverage-overview", {
        report: r,
        enabled: $.coverageEnabled,
        running,
        page: () => ({ runLabel: runLabel(), notes: notes() }),
        onOpen: (p: string) => open(p),
      });
    })];
  }
}
define("lc-coverage-page", CoveragePage);

declare global {
  interface HTMLElementTagNameMap {
    "lc-coverage-page": CoveragePage;
  }
}
