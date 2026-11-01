/**
 * Coverage views (port of CoveragePane.tsx):
 *
 *   <lc-coverage-pane>      the Coverage tab of the Tests page: the cached / collected coverage of the tree selection
 *                           (scope banner, "collecting", "no coverage collected for <test> yet", stale chip) or the full
 *                           report, with the file table and the source viewer.
 *   <lc-coverage-overview>  report header, summary cards (meters) and the module-grouped, sortable, filterable file table
 *   <lc-coverage-source>    one file: stats, legend, tint mode, next uncovered, source with hit counts / branch badges;
 *                           clicking a line's hit count lists the tests that cover it (<lc-coverage-line-tests>)
 *
 * The pane reads the coverage state from the store; only the shell's view state (open file, focus line) comes in as
 * props. The Coverage page (coverage-page.ts) reuses the overview and the source viewer.
 */
import {
  after,
  canAnimate,
  computed,
  fillIn,
  onCleanup,
  tween,
  delayed,
  cx,
  effect,
  define,
  h,
  icon,
  keyed,
  LabcoatElement,
  list,
  show,
  signal,
  untracked,
  type Child,
  popover,
  segmented,
  kbd,
  meter as dsMeter,
  menuButton,
  type MaybeReactive,
  type ReadSignal,
} from "@eriklieben/labcoat-design-system";
import { COVERAGE_EXPORTS, exportCoverage } from "../export";
import type { CoverageReport, CoverageSettingsInfo, FileCoverage } from "../contract";
import { getStore, type CoverageLabel, type CoverageRequest, type CoverageSelection, type CoverageTarget } from "../store";
import { fileName, formatClock, formatRelative, pct, projectAccent, rateTone } from "../util";
import { lockedHint, setSetting, settings } from "../settings";
import "./coverage-line-tests";
import { editorAnchor, editorName } from "./code-view";
import "./coverage.css";

export { pct, rateTone };

type SortKey = "file" | "lines" | "branches" | "covered";

export const isSubset = (r?: CoverageReport) => !!r && (r.scope ?? "all") === "subset";
const labelText = (l?: CoverageLabel) => l?.title ?? "selected tests";

/** Coverage meter bar (`.meter.tone-*`, the design system's meter). */
export function meter(rate: MaybeReactive<number>, size: "sm" | "md" = "md", label = "Coverage"): HTMLElement {
  return dsMeter({ value: rate, size, label });
}

/** `src()` while it is defined, else the last defined value (bindings of a region may re-run once before it is rebuilt). */
function lastDefined<T>(src: () => T | undefined): () => T {
  let last = untracked(src) as T;
  return computed(() => {
    const v = src();
    if (v !== undefined) last = v;
    return last;
  });
}

// ---- <lc-coverage-pane> ---------------------------------------------------------------------------------------

export interface CoveragePaneProps {
  /** file open in the source viewer (shell state, so the auto-open of per-test reports can set it) */
  openPath: string | undefined;
  /** line to scroll to when the source viewer opens (e.g. the first line covered by the selected test) */
  focusLine: number | undefined;
  onOpen: (path: string | undefined) => void;
}
export interface CoveragePane extends CoveragePaneProps {}

export class CoveragePane extends LabcoatElement<CoveragePaneProps> {
  static defaults: CoveragePaneProps = { openPath: undefined, focusLine: undefined, onOpen: () => {} };
  static hostClass = "detail-pane coverage-pane";

  protected render(): Child {
    const store = getStore();
    const $ = store.$;
    const running = computed(() => !!$.currentRun());
    const request = $.coverageRequest;
    const selection = $.coverageSelection;

    // A lookup that takes longer than 300 ms shows "Looking up coverage for …"; faster ones keep what was shown (no flash).
    const slowKey = signal<string | undefined>(undefined);
    effect(() => {
      const s = selection();
      if (s?.status !== "loading") return;
      const key = s.key;
      after(300, () => slowKey.set(key));
    });
    /**
     * The selection the pane is about. Precedence (user-visible scope rules):
     *   a tree selection (test, class, namespace) always wins over the global report / empty states;
     *   while its lookup runs, the previous selection's view stays for up to 300 ms, then "Looking up…";
     *   only with nothing (or a project) selected the overall report or the generic empty states show.
     */
    const sel = computed<CoverageSelection | undefined>(() => {
      const s = selection();
      if (s?.status !== "loading") return s;
      return s.prev && s.prev.status !== "loading" && slowKey() !== s.key ? s.prev : s;
    });
    const missing = computed(() => (sel()?.status === "missing" ? sel() : undefined));
    // What the overview shows: the selection's own report; else the full report; with nothing selected never an
    // unlabelled leftover subset report (it would read as "all tests").
    const shown = computed<CoverageReport | undefined>(() => {
      const s = sel();
      const r = $.coverage();
      if (s?.status === "found") return r;
      if (s) return $.fullCoverage() ?? (isSubset(r) ? undefined : r);
      if (!r || !isSubset(r)) return $.fullCoverage() ?? r;
      return $.coverageLabel() ? r : ($.fullCoverage() ?? undefined);
    });
    /** the label of what `shown` covers (undefined = all tests) */
    const scope = computed<CoverageLabel | undefined>(() => {
      if (sel()?.status === "found") return sel()!.label;
      return isSubset(shown()) ? ($.coverageLabel() ?? { title: "selected tests" }) : undefined;
    });
    // Full-pane progress when the pending request is for what's selected (or there is nothing else to show).
    const collecting = computed(() => {
      const req = request();
      if (!req) return false;
      const s = sel();
      const forSel = !!s && s.status !== "found" && req.testIds.length === s.testIds.length && req.testIds.every((id) => s.testIds.includes(id));
      return forSel || (!shown() && !s);
    });
    const openPath = this.props.openPath;
    const open = (p: string | undefined) => this.onOpen(p);
    const file = computed(() => {
      const r = shown();
      const p = openPath();
      return r && p ? r.files.find((f) => f.path === p) : undefined;
    });
    const hasFull = computed(() => !!$.fullCoverage() && $.fullCoverage() !== shown());
    const showFull = () => {
      store.showFullCoverage();
      open(undefined);
    };
    const collect = (t: CoverageTarget) => store.requestCoverage(t.label, t.testIds, t.projectId);

    const mode = computed(() => {
      if (collecting()) return "collecting";
      const s = sel();
      if (s?.status === "loading") return `lookup|${s.key}`;
      const r = shown();
      if (s?.status === "missing" && (s.isTest || !r)) return `missing|${s.key}`;
      if (!r) return "empty";
      const f = file();
      const tag = s ? `${s.status}:${s.key}` : "all";
      return f ? `file|${r.runId}|${f.path}` : `overview|${r.runId}|${tag}`;
    });

    return [
      show(
        () => request() && !collecting(),
        () => requestBar(lastDefined(request), () => store.dismissCoverageRequest()),
      ),
      // the per-selection report: "Coverage for <test>" (+ stale chip)
      show(
        () => isSubset(shown()) && !request() && !missing() && sel()?.status !== "loading",
        () =>
          scopeBanner({
            report: lastDefined(shown),
            label: () => scope(),
            selectionKey: () => $.coverageSelection()?.key,
            running,
            hasFull,
            onShowFull: showFull,
            onCollect: collect,
            onRunAllWithCoverage: () => store.send({ type: "run", coverage: true }),
          }),
      ),
      // a class / namespace without coverage of its own: the full report, clearly labelled as such
      show(
        () => {
          const m = missing();
          return m && !m.isTest && !!shown() && !request() ? m : undefined;
        },
        (m) => fallbackBanner(m, running, () => collect(untracked(m))),
      ),
      keyed(mode, (m) => {
        if (m === "collecting") return collectingState(lastDefined(request), () => store.dismissCoverageRequest());
        if (m.startsWith("lookup|")) return lookupState(lastDefined(() => sel()));
        if (m.startsWith("missing|")) {
          const target = lastDefined(missing);
          return missingState(target, running, () => collect(untracked(target)), () => !!$.fullCoverage(), showFull);
        }
        if (m === "empty") return emptyState(running);
        const r = shown.peek()!;
        if (m.startsWith("file|"))
          return h("lc-coverage-source", {
            file: file.peek()!,
            report: r,
            initialFocus: untracked(() => this.props.focusLine()),
            onBack: () => open(undefined),
          });
        return h("lc-coverage-overview", {
          report: r,
          scope: untracked(scope),
          enabled: $.coverageEnabled,
          running: () => running() && !request(),
          onOpen: (p: string) => open(p),
        });
      }),
    ];
  }
}
define("lc-coverage-pane", CoveragePane);

/** Subtle progress line while a per-test coverage run is in flight (or produced nothing). */
function requestBar(request: () => CoverageRequest, onDismiss: () => void) {
  const failed = () => request().status === "noResult";
  return h(
    "div",
    { class: () => cx("messagebar cov-scope", failed() && "mb-warning"), role: "status", "aria-live": "polite" },
    () => (failed() ? icon("warning", { size: 18, class: "mb-icon" }) : h("span", { class: "spinner cov-scope-spinner" })),
    h(
      "div",
      { class: "mb-body inline" },
      h(
        "span",
        { class: "mb-ellipsis" },
        () => (failed() ? "No coverage was collected for " : "Collecting coverage for "),
        h("b", { title: () => request().label.context ?? "" }, () => labelText(request().label)),
        () => (failed() ? ". See the output panel for details." : "…"),
      ),
    ),
    show(failed, () =>
      h("button", { class: "icon-btn small", onclick: onDismiss, title: "Dismiss", "aria-label": "Dismiss" }, icon("dismiss", { size: 14 })),
    ),
  );
}

function scopeBanner(p: {
  report: () => CoverageReport;
  label: () => CoverageLabel | undefined;
  selectionKey: () => string | undefined;
  running: () => boolean;
  hasFull: () => boolean;
  onShowFull: () => void;
  onCollect: (t: CoverageTarget) => void;
  onRunAllWithCoverage: () => void;
}) {
  const recollect = () => {
    const r = p.report();
    p.onCollect({
      key: p.selectionKey() ?? "",
      label: p.label() ?? { title: labelText(p.label()) },
      testIds: r.testIds ?? [],
      projectId: r.projectIds.length === 1 ? r.projectIds[0] : undefined,
      isTest: (r.testIds?.length ?? 0) === 1,
    });
  };
  return h(
    "div",
    { class: "messagebar cov-scope", role: "status" },
    icon("info", { size: 18, class: "mb-icon" }),
    h(
      "div",
      { class: "mb-body inline" },
      h(
        "span",
        { class: "mb-title mb-ellipsis" },
        "Coverage for ",
        h("span", { class: "cov-scope-name", title: () => labelText(p.label()) }, () => labelText(p.label())),
      ),
      show(
        () => p.label()?.context,
        (ctx) => h("span", { class: "mb-ellipsis dim", title: ctx }, ctx),
      ),
      show(
        () => p.report().stale,
        () =>
          h(
            "span",
            { class: "cov-stale-chip", title: "The test project was rebuilt after this coverage was collected; it may be out of date." },
            icon("warning", { size: 12 }),
            "Code changed since —",
            h("button", { class: "cov-stale-action", onclick: recollect, disabled: p.running }, "Recollect"),
          ),
      ),
    ),
    () =>
      p.hasFull()
        ? h("button", { class: "btn btn-small mb-action", onclick: p.onShowFull, title: "Switch back to the coverage of the last full run" }, "Show full coverage")
        : h(
            "button",
            {
              class: "btn btn-small mb-action",
              onclick: p.onRunAllWithCoverage,
              disabled: p.running,
              title: "Run all tests with coverage (the Collect coverage toggle stays as it is)",
            },
            icon("playFilled", { size: 14 }),
            " Run all with coverage",
          ),
  );
}

function collectingState(request: () => CoverageRequest, onDismiss: () => void) {
  const failed = () => request().status === "noResult";
  return h(
    "div",
    { class: "empty-state" },
    () => (failed() ? h("div", { class: "empty-illustration" }, icon("warning", { size: 32 })) : h("span", { class: "spinner large" })),
    h(
      "div",
      { class: "empty-title" },
      () => (failed() ? "No coverage collected for " : "Collecting coverage for "),
      h("span", { class: "cov-scope-name" }, () => labelText(request().label)),
      () => (failed() ? "" : "…"),
    ),
    h("div", { class: "empty-sub" }, () =>
      failed()
        ? "The run finished without a coverage report. The test project may lack Microsoft.Testing.Extensions.CodeCoverage — see the output panel."
        : "Running just this selection with code coverage. MTP coverage has no per-test attribution, so a focused run is how a single test's coverage is measured.",
    ),
    show(failed, () => h("button", { class: "btn", onclick: onDismiss }, "Dismiss")),
  );
}

/** The selection's cached coverage is being looked up (only shown when that takes longer than 300 ms). */
function lookupState(sel: () => CoverageSelection) {
  // hidden for the first 300 ms (fast lookups never flash it), then fades in
  return delayed(h(
    "div",
    { class: "empty-state cov-lookup", role: "status" },
    h("span", { class: "spinner large" }),
    h("div", { class: "empty-title" }, "Looking up coverage for ", h("span", { class: "cov-scope-name" }, () => sel().label.title), "…"),
    show(
      () => sel().label.context,
      (ctx) => h("div", { class: "empty-sub dim" }, ctx),
    ),
  ));
}

/** A class / namespace without its own cached coverage, while the full report is shown below. */
function fallbackBanner(target: () => CoverageTarget, running: () => boolean, onCollect: () => void) {
  return h(
    "div",
    { class: "messagebar cov-scope cov-fallback", role: "status" },
    icon("info", { size: 18, class: "mb-icon" }),
    h(
      "div",
      { class: "mb-body inline" },
      h(
        "span",
        { class: "mb-ellipsis" },
        h("span", { class: "mb-title" }, "Showing coverage for all tests"),
        " — ",
        h("span", { class: "cov-scope-name" }, () => target().label.title),
        " has no coverage of its own yet",
      ),
    ),
    h(
      "button",
      { class: "btn btn-small mb-action", onclick: onCollect, disabled: running, title: () => `Run the ${target().testIds.length} tests of ${target().label.title} with coverage` },
      icon("shieldCheck", { size: 14 }),
      () => ` Collect coverage for ${target().label.title}`,
    ),
  );
}

/** A test (or a class/namespace, without a full report) without cached coverage: invite to collect it (never automatic). */
function missingState(target: () => CoverageTarget, running: () => boolean, onCollect: () => void, hasFull: () => boolean, onShowFull: () => void) {
  const targetNoun = () => (target().isTest ? "this test" : "these tests");
  return h(
    "div",
    { class: "empty-state cov-missing" },
    h("div", { class: "empty-illustration" }, icon("shieldCheck", { size: 32 })),
    h(
      "div",
      { class: "empty-title" },
      "No coverage collected for ",
      h("span", { class: "cov-scope-name" }, () => target().label.title),
      " yet",
    ),
    show(
      () => target().label.context,
      (ctx) => h("div", { class: "empty-sub dim" }, ctx),
    ),
    h("div", { class: "empty-sub" }, () =>
      target().isTest
        ? "Collecting runs just this test with code coverage (whether or not Collect coverage is on) and shows the product code it executed."
        : `Collecting runs these ${target().testIds.length} tests with code coverage (whether or not Collect coverage is on) and shows the product code they executed.`,
    ),
    h(
      "div",
      { class: "detail-actions", style: { justifyContent: "center" } },
      h(
        "button",
        {
          class: "btn btn-accent",
          onclick: onCollect,
          disabled: running,
          title: () => (running() ? "A run is in progress" : `Run ${targetNoun()} with coverage (C)`),
        },
        icon("shieldCheck"),
        () => (target().isTest ? " Collect coverage for this test" : ` Collect coverage for ${target().label.title}`),
      ),
      show(hasFull, () => h("button", { class: "btn", onclick: onShowFull }, "Show full coverage")),
    ),
    h(
      "div",
      { class: "cov-hint" },
      icon("info", { size: 14 }),
      h("span", null, "Tip: press ", kbd("C"), " on a test in the tree (or use its ", icon("shieldCheck", { size: 14 }), " action) to collect its coverage."),
    ),
  );
}

function emptyState(running: () => boolean) {
  const store = getStore();
  const enabled = store.$.coverageEnabled;
  return h(
    "div",
    { class: "empty-state" },
    h("div", { class: "empty-illustration" }, icon("shieldCheck", { size: 32 })),
    h("div", { class: "empty-title" }, () => (enabled() ? "No coverage collected yet" : "Coverage is not being collected")),
    h("div", { class: "empty-sub" }, () =>
      enabled()
        ? "Run tests to collect line and branch coverage with Microsoft.Testing.Extensions.CodeCoverage. Selecting a test here shows the coverage collected for just that test."
        : "Turn on Collect coverage to record Cobertura line and branch coverage on every run, or collect it once for a single test. Test assemblies are excluded, so you see product code only.",
    ),
    h("div", { class: "detail-actions", style: { justifyContent: "center" } }, () =>
      enabled()
        ? h("button", { class: "btn btn-accent", onclick: () => store.send({ type: "run" }), disabled: running }, icon("playFilled"), " Run all with coverage")
        : h("button", { class: "btn btn-accent", onclick: () => store.send({ type: "setCoverage", enabled: true }) }, icon("shieldCheck"), " Collect coverage on runs"),
    ),
    h(
      "div",
      { class: "cov-hint" },
      icon("info", { size: 14 }),
      h(
        "span",
        null,
        "Tip: select a test while this tab is open to see its coverage. Press ",
        kbd("C"),
        " (or use the ",
        icon("shieldCheck", { size: 14 }),
        " action) to collect coverage for just that test.",
      ),
    ),
  );
}

// ---- <lc-coverage-overview> -----------------------------------------------------------------------------------

export interface CoverageOverviewProps {
  report: CoverageReport | undefined;
  /** what the report covers (a selected test / class); undefined = all tests */
  scope: CoverageLabel | undefined;
  enabled: boolean;
  running: boolean;
  /** rendered as the full-width Coverage page (overall report) instead of the Tests page tab */
  page: { runLabel?: string; notes?: string[] } | undefined;
  onOpen: (path: string) => void;
}
export interface CoverageOverview extends CoverageOverviewProps {}

interface Group {
  module: string;
  files: FileCoverage[];
  lc: number;
  lv: number;
  bc: number;
  bv: number;
}

/** Local state of the overview (filter, sort, collapsed modules); survives re-keying by run within one pane. */
export class CoverageOverview extends LabcoatElement<CoverageOverviewProps> {
  static defaults: CoverageOverviewProps = { report: undefined, scope: undefined, enabled: false, running: false, page: undefined, onOpen: () => {} };
  static hostClass = "detail-scroll";

  protected render(): Child {
    const report = () => this.props.report()!;
    const page = this.props.page;
    const subset = computed(() => isSubset(this.props.report()));
    const filter = signal("");
    const sort = signal<{ key: SortKey; desc: boolean }>({ key: "file", desc: false });
    const collapsed = signal(new Set<string>());
    // A per-test report shows only the files that test touched unless asked otherwise.
    const onlyCovered = signal(untracked(subset));
    const hiddenCount = computed(() => (onlyCovered() ? report().files.filter((f) => f.linesCovered === 0).length : 0));

    const groups = computed<Group[]>(() => {
      const q = filter().trim().toLowerCase();
      const only = onlyCovered();
      const s = sort();
      const files = report().files.filter(
        (f) => (!only || f.linesCovered > 0) && (!q || f.relativePath.toLowerCase().includes(q) || f.module.toLowerCase().includes(q)),
      );
      const cmp = (a: FileCoverage, b: FileCoverage) => {
        let d = 0;
        switch (s.key) {
          case "file":
            d = a.relativePath.localeCompare(b.relativePath);
            break;
          case "lines":
            d = a.lineRate - b.lineRate;
            break;
          case "branches":
            d = (a.branchesValid ? a.branchRate : 2) - (b.branchesValid ? b.branchRate : 2);
            break;
          case "covered":
            d = a.linesValid - a.linesCovered - (b.linesValid - b.linesCovered);
            break;
        }
        return s.desc ? -d : d;
      };
      const m = new Map<string, FileCoverage[]>();
      for (const f of files) {
        const g = m.get(f.module);
        if (g) g.push(f);
        else m.set(f.module, [f]);
      }
      return [...m.entries()]
        .map(([module, fs]) => ({
          module,
          files: fs.toSorted(cmp),
          lc: fs.reduce((a, f) => a + f.linesCovered, 0),
          lv: fs.reduce((a, f) => a + f.linesValid, 0),
          bc: fs.reduce((a, f) => a + f.branchesCovered, 0),
          bv: fs.reduce((a, f) => a + f.branchesValid, 0),
        }))
        .sort((a, b) => a.module.localeCompare(b.module));
    });

    const th = (key: SortKey, label: string, cls: string) =>
      h(
        "th",
        { class: cls, "aria-sort": () => (sort().key === key ? sortDirection(sort().desc) : "none") },
        h(
          "button",
          {
            class: () => cx("th-btn", sort().key === key && "sorted"),
            onclick: () => sort.update((s) => ({ key, desc: s.key === key ? !s.desc : false })),
          },
          label,
          () => (sort().key === key ? sortIcon(sort().desc) : null),
        ),
      );

    const toggleModule = (module: string) =>
      collapsed.update((s) => {
        const n = new Set(s);
        if (n.has(module)) n.delete(module);
        else n.add(module);
        return n;
      });

    const fileRow = (f: () => FileCoverage) => {
      const open = () => this.onOpen(f().path);
      return h(
        "tr",
        {
          class: "cov-file",
          tabIndex: 0,
          onclick: open,
          onkeydown: (e) => {
            if (e.key === "Enter") open();
          },
        },
        h(
          "td",
          { class: "col-file", title: () => f().path },
          icon("document", { size: 16 }),
          h("span", { class: "cov-path" }, () => {
            const rel = f().relativePath;
            const slash = rel.lastIndexOf("/");
            return [slash > 0 ? h("span", { class: "dim" }, rel.slice(0, slash + 1)) : null, h("span", { class: "cov-fname" }, rel.slice(slash + 1))];
          }),
          show(
            () => f().stale,
            () => h("span", { class: "cov-stale-chip", title: "This file changed after its coverage was collected: the numbers are from before the change." }, icon("warning", { size: 12 }), "Changed since"),
          ),
        ),
        rateCell(() => f().linesCovered, () => f().linesValid),
        rateCell(() => f().branchesCovered, () => f().branchesValid),
        h("td", { class: "col-num mono" }, () => uncoveredCell(f().linesCovered, f().linesValid)),
      );
    };

    const groupRows = (g: ReadSignal<Group>) => {
      const open = () => !collapsed().has(g().module);
      return [
        h(
          "tr",
          {
            class: "cov-module",
            onclick: () => toggleModule(g.peek().module),
            "aria-expanded": open,
          },
          h(
            "td",
            { class: "col-file" },
            h("span", { class: () => cx("twisty has", open() && "open") }, icon("chevronRight", { size: 12 })),
            h("span", { class: "cov-module-name" }, () => g().module),
            h("span", { class: "cnt-total" }, () => g().files.length),
          ),
          rateCell(() => g().lc, () => g().lv),
          rateCell(() => g().bc, () => g().bv),
          // the same "uncovered / lines" as the file rows (#11)
          h("td", { class: "col-num mono" }, () => uncoveredCell(g().lc, g().lv)),
        ),
        show(open, () =>
          list(
            () => g().files,
            (f) => f.path,
            (f) => fileRow(f),
          ),
        ),
      ];
    };

    // P10: the report's meters fill once when it is first shown (not on sort / filter / re-render)
    queueMicrotask(() => this.isConnected && fillIn(this));
    return [
      h(
        "header",
        { class: "detail-header" },
        h("div", { class: "kind-label" }, () => {
          if (page()) return "All tests · last full run";
          const scope = this.props.scope();
          return scope ? `Code coverage · ${scope.context ?? "selected tests"}` : "Code coverage · all tests";
        }),
        h(
          "div",
          { class: "detail-title-row" },
          h("h2", { class: "detail-title", title: () => this.props.scope()?.title ?? "" }, () =>
            page() ? "Coverage report" : (this.props.scope()?.title ?? testsTitle(subset())),
          ),
          show(
            () => this.props.running() && this.props.enabled(),
            () =>
              h(
                "span",
                { class: "row-pill busy" },
                h("span", { class: "spinner", style: { width: 10, height: 10, borderWidth: "1.5px", marginRight: 4, verticalAlign: "-1px" } }),
                "Updating after run",
              ),
          ),
          show(
            () => !this.props.enabled() && !subset() && !page(),
            () => h("span", { class: "row-pill err" }, "Collect coverage is off — showing the last report"),
          ),
          // the Coverage page: download the report (export.ts)
          show(
            () => !!page(),
            () =>
              menuButton({
                label: "Export",
                menuLabel: "Export coverage",
                icon: "arrowDownload",
                size: "small",
                class: "cov-export",
                items: COVERAGE_EXPORTS.map((x) => ({
                  label: `Export as ${x.label}`,
                  description: x.detail,
                  icon: "arrowDownload" as const,
                  onSelect: () => void exportCoverage(x.format),
                })),
              }),
          ),
        ),
        h(
          "div",
          { class: "cov-meta" },
          () => {
            const r = report();
            const pg = page();
            return pg
              ? h(
                  "span",
                  { title: `Run ${r.runId}\nGenerated ${r.generatedAt}` },
                  `Generated ${formatClock(r.generatedAt)} (${formatRelative(r.generatedAt)}) · ${pg.runLabel ?? "last full run"} `,
                  h("span", { class: "mono dim" }, r.runId.slice(0, 8)),
                )
              : h("span", { title: r.generatedAt }, `${r.runId} · ${formatClock(r.generatedAt)} (${formatRelative(r.generatedAt)})`);
          },
          () => report().projectIds.map((id) => h("span", { class: "project-tag", style: { "--tag": projectAccent(id) } }, id)),
        ),
        // the Coverage page: what the latest coverage run measured, when that is not this report (e.g. a watch rerun)
        show(
          () => (page()?.notes?.length ?? 0) > 0,
          () => h("ul", { class: "cov-page-notes", "aria-label": "About this report" }, () => page()!.notes!.map((n) => h("li", null, icon("info", { size: 14 }), h("span", null, n)))),
        ),
        // which coverage settings applied, per project: never a mystery (CoverageReport.settings)
        show(
          () => (report().settings?.length ?? 0) > 0,
          () =>
            h("ul", { class: "cov-settings", "aria-label": "Coverage settings" }, () =>
              report().settings!.map((s) =>
                h(
                  "li",
                  { class: "cov-setting", title: coverageSettingsTitle(s) },
                  h("span", { class: "project-tag", style: { "--tag": projectAccent(s.projectId) } }, s.projectId),
                  h("span", null, coverageSettingsLabel(s)),
                ),
              ),
            ),
        ),
      ),

      h(
        "div",
        { class: "cov-cards" },
        summaryCard("Lines", () => report().lineRate, () => report().linesCovered, () => report().linesValid, "lines"),
        summaryCard("Branches", () => report().branchRate, () => report().branchesCovered, () => report().branchesValid, "branches"),
        h(
          "div",
          { class: "cov-card cov-card-plain" },
          h(
            "div",
            { class: "cov-card-head" },
            h("span", { class: "stat-label" }, "Files"),
            h("span", { class: "cov-card-fraction" }, () => `${new Set(report().files.map((f) => f.module)).size} modules`),
          ),
          h("div", { class: "cov-card-value" }, () => report().files.length),
          h(
            "div",
            { class: "cov-legend", "aria-label": "Rate colors" },
            h("span", null, h("i", { class: "tone-good" }), "≥ 80%"),
            h("span", null, h("i", { class: "tone-warn" }), "≥ 50%"),
            h("span", null, h("i", { class: "tone-bad" }), "< 50%"),
          ),
        ),
      ),

      h(
        "div",
        { class: "cov-table-toolbar" },
        h(
          "label",
          { class: "search-box" },
          icon("search", { size: 16 }),
          h("input", {
            type: "search",
            placeholder: "Filter files",
            value: filter,
            oninput: (e) => filter.set((e.currentTarget as HTMLInputElement).value),
            onkeydown: (e) => {
              if (e.key === "Escape") filter.set("");
            },
            spellcheck: false,
            "aria-label": "Filter files",
          }),
          show(filter, () =>
            h("button", { class: "search-clear", onclick: () => filter.set(""), "aria-label": "Clear filter" }, icon("dismiss", { size: 14 })),
          ),
        ),
        show(subset, () =>
          h(
            "label",
            { class: "cov-only-toggle", title: "Show only files with at least one line covered by this run" },
            h("input", { type: "checkbox", checked: () => !onlyCovered(), onchange: (e) => onlyCovered.set(!(e.currentTarget as HTMLInputElement).checked) }),
            "Show all files",
            show(
              () => hiddenCount() > 0,
              () => h("span", { class: "dim" }, () => `(${hiddenCount()} not touched)`),
            ),
          ),
        ),
      ),

      h(
        "div",
        { class: "cov-table-wrap" },
        h(
          "table",
          { class: "cov-table" },
          h("thead", null, h("tr", null, th("file", "File", "col-file"), th("lines", "Lines", "col-rate"), th("branches", "Branches", "col-rate"), th("covered", "Uncovered", "col-num"))),
          h(
            "tbody",
            null,
            show(
              () => groups().length === 0,
              () =>
                h("tr", null, h("td", { colSpan: 4, class: "cov-empty" }, () => (filter() ? `No files match “${filter()}”.` : "No product code was covered by this run."))),
            ),
            list(groups, (g) => g.module, (g) => groupRows(g)),
          ),
        ),
      ),
    ];
  }
}
define("lc-coverage-overview", CoverageOverview);

/** One line per project: "coverage.settings.xml (found by name) · dynamic" / "Labcoat defaults: Api, Lib · dynamic". */
export function coverageSettingsLabel(s: CoverageSettingsInfo): string {
  const what = settingsSource(s);
  const filters = [s.include?.length ? `+ ${s.include.join(", ")}` : "", s.exclude?.length ? `− ${s.exclude.join(", ")}` : ""].filter(Boolean);
  const instrumentation = s.source === "runner" ? [] : [`${s.instrumentation} instrumentation`];
  return [what, ...filters, ...instrumentation].join(" · ");
}

const sortDirection = (desc: boolean) => (desc ? "descending" : "ascending");
const sortIcon = (desc: boolean) => icon(desc ? "sortDown" : "sortUp", { size: 14 });
const testsTitle = (subset: boolean) => (subset ? "Selected tests" : "All tests");

/** Where the settings come from: the file and how it was found, testconfig.json, or Labcoat's defaults. */
function settingsSource(s: CoverageSettingsInfo): string {
  if (s.source === "file") return `${fileName(s.path ?? "")} (${settingsOrigin(s.origin)})`;
  if (s.source === "testconfig") return `${fileName(s.path ?? "testconfig.json")} (codeCoverage)`;
  if (s.source === "runner") return `${fileName(s.path ?? "")} (${s.origin ?? "runner"} settings)`;
  const modules = s.modules?.length ? s.modules.join(", ") : "all but tests, test frameworks and Labcoat";
  return `Labcoat defaults: ${modules}`;
}

function settingsOrigin(origin: CoverageSettingsInfo["origin"]): string {
  if (origin === "option") return "Coverage.Settings";
  return origin === "msbuild" ? "TestingPlatformCommandLineArguments" : "found by name";
}

/** Tooltip: the full path and what the defaults leave out. */
export function coverageSettingsTitle(s: CoverageSettingsInfo): string {
  if (s.source !== "defaults") return s.path ?? "";
  return "No coverage settings file found next to the test project or up to the solution root.\n" +
    "Counted: the projects it references. Left out: Labcoat's assemblies, test projects, test frameworks, generated code and obj/.\n" +
    "Add a coverage.settings.xml next to the test project, or set coverage.settings / include / exclude in .labcoat.json.";
}

function summaryCard(title: string, rate: () => number, covered: () => number, valid: () => number, unit: string) {
  // P10: on first display the percentage counts up in sync with its bar filling (decorative: skipped when busy/reduced)
  const counting = signal<number | undefined>(undefined);
  const to = untracked(rate);
  if (untracked(valid) && canAnimate("decorative")) {
    counting.set(0);
    const stop = tween(0, to, (v) => counting.set(v), { onDone: () => counting.set(undefined) });
    onCleanup(stop);
  }
  return h(
    "div",
    { class: () => cx("cov-card", valid() ? `tone-${rateTone(rate())}` : "tone-none") },
    h(
      "div",
      { class: "cov-card-head" },
      h("span", { class: "stat-label" }, title),
      h("span", { class: "cov-card-fraction" }, () => `${covered().toLocaleString()} / ${valid().toLocaleString()} ${unit}`),
    ),
    // nothing to cover (Round 4 noCoverableLines; branches: branchesValid 0): say so instead of 0% / 100%
    h("div", { class: () => cx("cov-card-value lc-tabular", !valid() && "cov-card-none") }, () => (valid() ? pct(counting() ?? rate()) : `No coverable ${unit}`)),
    meter(() => (valid() ? rate() : 0), "md", `${title} coverage`),
  );
}

function rateCell(covered: () => number, valid: () => number) {
  return h("td", { class: "col-rate" }, () => {
    const v = valid();
    if (!v) return h("span", { class: "dim" }, "—");
    const rate = covered() / v;
    return h("span", { class: "rate-cell" }, h("span", { class: cx("rate-num mono", `tone-${rateTone(rate)}`) }, pct(rate)), meter(rate, "sm"));
  });
}

// ---- <lc-coverage-source> -------------------------------------------------------------------------------------

export interface CoverageSourceProps {
  file: FileCoverage | undefined;
  report: CoverageReport | undefined;
  /** first breadcrumb ("Coverage" in the tab, "All files" on the page) */
  rootLabel: string;
  initialFocus: number | undefined;
  onBack: () => void;
}
export interface CoverageSource extends CoverageSourceProps {}

export class CoverageSource extends LabcoatElement<CoverageSourceProps> {
  static defaults: CoverageSourceProps = { file: undefined, report: undefined, rootLabel: "Coverage", initialFocus: undefined, onBack: () => {} };
  static hostClass = "source-view";

  protected render(): Child {
    queueMicrotask(() => this.isConnected && fillIn(this)); // P10
    const file = () => this.props.file()!;
    const uncovered = computed(() => file().lines.filter((l) => l.hits === 0).map((l) => l.line));
    const focus = signal<{ line?: number; nonce: number }>({ line: untracked(() => this.props.initialFocus()), nonce: 0 });
    const nextUncovered = () => {
      const u = uncovered.peek();
      const cur = focus.peek().line ?? 0;
      const after = u.find((l) => l > cur) ?? u[0];
      focus.update((f) => ({ line: after, nonce: f.nonce + 1 }));
    };
    const gutterOnly = computed(() => settings().coverageTint === "gutter");

    // F1 "Which tests cover this line?": clicking a line's hit count opens the popover next to it.
    const lineTests = signal<{ line: number; anchor: HTMLElement } | undefined>(undefined);

    return [
      h(
        "header",
        { class: "source-header" },
        h(
          "div",
          { class: "source-title-row" },
          h("button", { class: "icon-btn", onclick: () => this.onBack(), title: "Back to all files", "aria-label": "Back to all files" }, icon("arrowLeft", { size: 18 })),
          h(
            "div",
            { class: "source-title" },
            h(
              "nav",
              { class: "breadcrumb", "aria-label": "Breadcrumb" },
              h("button", { class: "crumb-btn", onclick: () => this.onBack() }, () => this.props.rootLabel()),
              icon("chevronRight", { size: 12 }),
              h("span", null, () => file().module),
            ),
            h("h2", { class: "detail-title", title: () => file().path }, () => fileName(file().relativePath)),
          ),
          show(
            () => file().stale,
            () => h("span", { class: "cov-stale-chip", title: "This file changed after its coverage was collected: the line marks are from before the change and may be off." }, icon("warning", { size: 12 }), "Changed since this report"),
          ),
          h("span", { class: "flex-filler" }),
          show(
            () => uncovered().length > 0,
            () =>
              h(
                "button",
                { class: "btn btn-small", onclick: nextUncovered, title: "Jump to the next uncovered line" },
                () => `Next uncovered (${uncovered().length})`,
              ),
          ),
          editorAnchor(
            {
              class: "btn btn-small cov-open-editor",
              path: () => file().path,
              line: () => focus().line ?? uncovered()[0],
              title: (name) => `Open this file in ${name}`,
            },
            icon("code", { size: 16 }),
            () => ` Open in ${editorName()}`,
          ),
        ),
        h(
          "div",
          { class: "source-stats" },
          h(
            "span",
            { class: "source-stat" },
            "Lines ",
            h("b", { class: () => `tone-${rateTone(file().lineRate)}` }, () => pct(file().lineRate)),
            h("span", { class: "dim" }, () => `${file().linesCovered}/${file().linesValid}`),
            meter(() => file().lineRate, "sm", "Line coverage"),
          ),
          show(
            () => file().branchesValid > 0,
            () =>
              h(
                "span",
                { class: "source-stat" },
                "Branches ",
                h("b", { class: () => `tone-${rateTone(file().branchRate)}` }, () => pct(file().branchRate)),
                h("span", { class: "dim" }, () => `${file().branchesCovered}/${file().branchesValid}`),
                meter(() => file().branchRate, "sm", "Branch coverage"),
              ),
          ),
          h("span", { class: "flex-filler" }),
          // locked by the host (ui-defaults, Round 5): disabled, with the same "Set by …" hint as the Settings page
          show(
            () => lockedHint("coverageTint"),
            (hint) => h("span", { class: "setting-origin setting-origin-locked cov-tint-lock", title: "This setting is locked by the Labcoat host's configuration, so it can't be changed here." }, icon("shieldCheck", { size: 14 }), () => ` ${hint()}`),
          ),
          segmented({
            label: "Coverage tint",
            size: "small",
            value: () => settings().coverageTint ?? "line",
            onChange: (v) => setSetting("coverageTint", v),
            disabled: () => !!lockedHint("coverageTint"),
            options: [
              { value: "line", label: "Tint lines", title: "Tint whole lines" },
              { value: "gutter", label: "Gutter only", title: "Tint only the hit-count gutter" },
            ],
          }),
        ),
        h(
          "div",
          { class: "src-legend-row" },
          h(
            "span",
            { class: "src-legend", "aria-label": "Legend" },
            h("span", null, h("i", { class: "lg-cov" }), "Covered"),
            h("span", null, h("i", { class: "lg-partial" }), "Partial (some branches)"),
            h("span", null, h("i", { class: "lg-uncov" }), "Not covered"),
            h("span", { class: "lg-hint" }, "Click a hit count to list the tests that cover it"),
          ),
        ),
        h("div", { class: "fqn" }, h("code", null, () => file().path)),
      ),
      h("lc-code-view", {
        path: () => file().path,
        coverage: file,
        showCoverage: true,
        focusLine: () => focus().line,
        focusNonce: () => focus().nonce,
        viewClass: () => cx("cov-source-code", gutterOnly() && "cov-gutter-only"),
        onLineHits: (line: number, anchor: HTMLElement) =>
          lineTests.set(lineTests.peek()?.anchor === anchor ? undefined : { line, anchor }),
      }),
      popover({
        open: () => !!lineTests(),
        anchor: () => lineTests.peek()?.anchor,
        onClose: () => lineTests.set(undefined),
        placement: "right-start",
        label: "Tests covering this line",
        class: "cov-lt-popover",
        content: () => {
          const lt = lineTests.peek()!;
          return h("lc-coverage-line-tests", {
            path: file().path,
            line: lt.line,
            hits: file().lines.find((l) => l.line === lt.line)?.hits,
            onClose: () => lineTests.set(undefined),
          });
        },
      }),
    ];
  }
}
define("lc-coverage-source", CoverageSource);

declare global {
  interface HTMLElementTagNameMap {
    "lc-coverage-pane": CoveragePane;
    "lc-coverage-overview": CoverageOverview;
    "lc-coverage-source": CoverageSource;
  }
}

/** "5 / 13" (uncovered / coverable lines) for file and module rows alike. */
function uncoveredCell(covered: number, valid: number): Child {
  const un = valid - covered;
  return [un > 0 ? h("span", { class: "uncov-count" }, un.toLocaleString()) : h("span", { class: "dim" }, "0"), h("span", { class: "dim" }, ` / ${valid.toLocaleString()}`)];
}
