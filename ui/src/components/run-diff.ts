/**
 * Run diff (F2) and run-history helpers.
 *
 * - `<lc-run-diff>`: "Compare runs" sheet: pick two runs (or Live) → new failures, fixed, still failing, newly slow
 *   (> 2× and > 50 ms slower) and not run, each row jumps to the test. Opened from the run switcher ("Compare with…").
 * - helpers for other components (no element needed):
 *     isRunStored(runId)    the run is still in the persisted run list (history squares of pruned runs: not clickable)
 *     newFailureKeys()      testKeys failing now whose previous full-run result passed ("New failures" quick filter)
 *     isNewFailure(test)    the same for one test; isNewFailureAt(history, i) for a history entry (watch strip)
 *     runDeltas()           per runId: tests that started failing / got fixed in that run (from TestNode.history)
 *     openCompare(a, b)     open the comparison of two runs (runId or LIVE), older one becomes the base
 *     comparison()          the open comparison pair, comparisonOpen() whether the sheet is visible
 */
import {
  computed,
  cx,
  define,
  effect,
  h,
  icon,
  iconButton,
  LabcoatElement,
  list,
  onCleanup,
  show,
  signal,
  statusIcon,
  type Child,
} from "@eriklieben/labcoat-design-system";
import type { HistoryEntry, Outcome, RunDetails, RunResult, RunSummary, TestNode } from "../contract";
import { getStore, testKey } from "../store";
import { formatDuration, formatRunTime } from "../util";
import "./run-diff.css";

/** Pseudo run id for "the current (live) results". */
export const LIVE = "live";

const FINAL = new Set<Outcome>(["passed", "failed", "skipped", "timedOut"]);
const isFail = (o?: Outcome) => o === "failed" || o === "timedOut";

// ---- helpers for other components --------------------------------------------------------------

/**
 * The run is still persisted (in `state.runs`): only then can it be viewed. Reactive when called inside a binding /
 * computed (reads `store.$.runs()`), plain otherwise. For DETAIL's history strip: pruned squares aren't clickable.
 */
export function isRunStored(runId: string | undefined): boolean {
  return !!runId && getStore().$.runs().some((r) => r.runId === runId);
}

/** runIds of stored partial runs (a single test, a class, …): "the previous full run" skips their history entries. */
const subsetRunIds = computed(() => new Set(getStore().$.runs().filter((r) => r.scope === "subset").map((r) => r.runId)));

/**
 * THE "new failure" rule (tree chip and filter, run overview, watch strip, run switcher deltas, F8): the result at
 * `hist[i]` failed, and the test's previous FULL-run result passed — or it has no earlier result at all (a new test).
 * Entries of stored partial runs are skipped when an earlier full-run entry exists. Reactive inside computeds (reads
 * the run list).
 */
export function isNewFailureAt(hist: readonly HistoryEntry[], i: number): boolean {
  if (!isFail(hist[i]?.outcome)) return false;
  return isNewAfter(hist.slice(0, i));
}

function isNewAfter(before: readonly HistoryEntry[]): boolean {
  if (!before.length) return true;
  const subset = subsetRunIds();
  let prev = before.at(-1)!;
  for (let k = before.length - 1; k >= 0; k--)
    if (!before[k].runId || !subset.has(before[k].runId!)) {
      prev = before[k];
      break;
    }
  return prev.outcome === "passed";
}

/**
 * The test fails now and its previous full-run result passed (or it has no earlier result): a "new failure".
 * `running`: the current run, if any (results of a run in progress are not in `history` yet).
 */
export function isNewFailure(t: TestNode, running?: RunSummary): boolean {
  if (!isFail(t.outcome)) return false;
  const h = t.history ?? [];
  const last = h.at(-1);
  const inProgress = !!running && !!t.lastRunAt && t.lastRunAt >= running.startedAt && last?.at !== t.lastRunAt;
  return isNewAfter(inProgress ? h : h.slice(0, -1));
}

/** testKeys of the live tests that are new failures (for a "New failures" tree filter / chip). */
export const newFailureKeys = computed(() => {
  const s = getStore();
  const running = s.$.currentRun();
  const out = new Set<string>();
  for (const [k, t] of s.$.tests()) if (isNewFailure(t, running)) out.add(k);
  return out;
});

export interface RunDelta {
  /** tests whose previous result passed and that failed in this run */
  newFailures: number;
  /** tests whose previous result failed and that passed in this run */
  fixed: number;
}

/**
 * Per runId: what changed in that run, from each test's `history` (max 20 entries per test, so very old runs have no
 * entry: callers fall back to comparing the run summaries).
 */
export const runDeltas = computed(() => {
  const m = new Map<string, RunDelta>();
  for (const t of getStore().$.tests().values()) if (t.history) addRunDeltas(m, t.history);
  return m;
});

/** Counts one test's history into the per-run deltas. */
function addRunDeltas(m: Map<string, RunDelta>, hist: NonNullable<TestNode["history"]>) {
  for (let i = 0; i < hist.length; i++) {
    const id = hist[i].runId;
    if (!id) continue;
    let d = m.get(id);
    if (!d) {
      d = { newFailures: 0, fixed: 0 };
      m.set(id, d);
    }
    if (i === 0) continue; // the oldest kept entry: its predecessor may have been pruned
    const a = hist[i - 1].outcome;
    const b = hist[i].outcome;
    if (isNewFailureAt(hist, i)) d.newFailures++;
    else if (b === "passed" && isFail(a)) d.fixed++;
  }
}

// ---- diff ---------------------------------------------------------------------------------------

export interface DiffEntry {
  key: string;
  before?: RunResult;
  after?: RunResult;
}

export interface RunDiff {
  newFailures: DiffEntry[];
  fixed: DiffEntry[];
  stillFailing: DiffEntry[];
  newlySlow: DiffEntry[];
  notRun: DiffEntry[];
}

/** Newly slow: more than twice as slow and more than 50 ms slower. */
export const isNewlySlow = (before?: number, after?: number) => before != null && after != null && after > 2 * before && after - before > 50;

/**
 * What changed from `base` to `target` (both: per-test results of a run). A failing test without a result in the base
 * is a new failure only when the base was a full run (then the test is new); after a partial base it is unknown.
 */
export function diffRuns(base: readonly RunResult[], target: readonly RunResult[], baseFull = true): RunDiff {
  const b = new Map(base.map((r) => [testKey(r), r]));
  const t = new Map(target.map((r) => [testKey(r), r]));
  const d: RunDiff = { newFailures: [], fixed: [], stillFailing: [], newlySlow: [], notRun: [] };
  for (const [key, after] of t) {
    const before = b.get(key);
    const section = diffSection(before, after, baseFull);
    if (section) d[section].push({ key, before, after });
  }
  for (const [key, before] of b) if (FINAL.has(before.outcome) && !t.has(key)) d.notRun.push({ key, before });
  d.newlySlow.sort((x, y) => (y.after!.durationMs ?? 0) / (y.before!.durationMs || 1) - (x.after!.durationMs ?? 0) / (x.before!.durationMs || 1));
  return d;
}

/** The section of a run diff a test with a result in the target belongs to, if any. */
function diffSection(before: RunResult | undefined, after: RunResult, baseFull: boolean): keyof RunDiff | undefined {
  if (isFail(after.outcome)) {
    if (isFail(before?.outcome)) return "stillFailing";
    return before || baseFull ? "newFailures" : undefined;
  }
  if (after.outcome !== "passed" || !before) return undefined;
  if (isFail(before.outcome)) return "fixed";
  return before.outcome === "passed" && isNewlySlow(before.durationMs, after.durationMs) ? "newlySlow" : undefined;
}

/** The live results as a run's result list (tests with a final outcome). */
function liveResults(tests: Map<string, TestNode>): RunResult[] {
  const out: RunResult[] = [];
  for (const t of tests.values())
    if (FINAL.has(t.outcome)) out.push({ projectId: t.projectId, id: t.id, outcome: t.outcome, durationMs: t.durationMs, errorMessage: t.errorMessage });
  return out;
}

// ---- comparison state (module level: survives closing the sheet) -----------------------------

export interface Comparison {
  /** older side: runId or LIVE */
  base: string;
  /** newer side: runId or LIVE */
  target: string;
}

export const comparison = signal<Comparison | undefined>(undefined);
export const comparisonOpen = signal(false);

/** Fetched run details: undefined = not requested, "loading", null = not stored any more. Runs never change. */
const details = signal<ReadonlyMap<string, RunDetails | null | "loading">>(new Map());

function setDetails(runId: string, v: RunDetails | null | "loading") {
  const m = new Map(details.peek());
  m.set(runId, v);
  details.set(m);
}

function ensureDetails(runId: string) {
  const cur = details.peek().get(runId);
  if (runId === LIVE || cur === "loading" || cur) return;
  setDetails(runId, "loading");
  getStore()
    .transport.fetchRun(runId)
    .then(
      (d) => setDetails(runId, d),
      (err) => {
        console.warn("[runs] fetch failed", err);
        setDetails(runId, null);
      },
    );
}

function startOf(ref: string) {
  if (ref === LIVE) return Number.POSITIVE_INFINITY;
  const r = getStore()
    .get()
    .runs.find((x) => x.runId === ref);
  return r ? Date.parse(r.startedAt) : 0;
}

/** Compare two runs (runIds or LIVE); the older one becomes the base. Opens the sheet. */
export function openCompare(a: string, b: string) {
  const [base, target] = startOf(a) <= startOf(b) ? [a, b] : [b, a];
  ensureDetails(base);
  ensureDetails(target);
  comparison.set({ base, target });
  comparisonOpen.set(true);
}

export function closeCompare() {
  comparisonOpen.set(false);
}

type ComparisonResult = { status: "loading" } | { status: "missing"; which: string[] } | { status: "ready"; diff: RunDiff };

export const comparisonResult = computed<ComparisonResult | undefined>(() => {
  const c = comparison();
  if (!c) return undefined;
  const of = (ref: string) => {
    if (ref === LIVE) return liveResults(getStore().$.tests());
    const d = details().get(ref);
    if (d === undefined || d === "loading") return "loading";
    return d ? d.results : null;
  };
  const baseDetails = details().get(c.base);
  const baseFull = c.base === LIVE || (typeof baseDetails === "object" && baseDetails?.run.scope !== "subset");
  const a = of(c.base);
  const b = of(c.target);
  if (a === "loading" || b === "loading") return { status: "loading" };
  if (!a || !b) return { status: "missing", which: [!a && c.base, !b && c.target].filter((x): x is string => !!x) };
  return { status: "ready", diff: diffRuns(a, b, baseFull) };
});

// ---- labels ---------------------------------------------------------------------------------------

/** "11:33:47 PM" / "Yesterday 10:02" for a runId, "Live" for LIVE. */
export function runRefLabel(ref: string, runs: readonly RunSummary[]) {
  if (ref === LIVE) return "Live";
  const r = runs.find((x) => x.runId === ref);
  return r ? formatRunTime(r.startedAt) : "a pruned run";
}

const testCount = (n: number) => `${n} test${n === 1 ? "" : "s"}`;
const scopeLabel = (r: RunSummary) => (r.scope === "subset" ? testCount(r.total) : "all tests");

function optionLabel(r: RunSummary) {
  const counts = [`${r.passed} passed`, r.failed && `${r.failed} failed`].filter(Boolean).join(", ");
  return `${formatRunTime(r.startedAt)} · ${counts} · ${scopeLabel(r)}`;
}

// ---- element ------------------------------------------------------------------------------------

type SectionId = keyof RunDiff;
const SECTIONS: { id: SectionId; title: string; one?: string; empty: string; tone: string }[] = [
  { id: "newFailures", title: "New failures", one: "New failure", empty: "No new failures.", tone: "bad" },
  { id: "fixed", title: "Fixed", empty: "Nothing was fixed.", tone: "good" },
  { id: "stillFailing", title: "Still failing", empty: "Nothing failed in both runs.", tone: "bad" },
  { id: "newlySlow", title: "Newly slow", empty: "No test got more than twice as slow (and > 50 ms slower).", tone: "warn" },
  { id: "notRun", title: "Not run", empty: "Every test of the base run ran again.", tone: "muted" },
];
const MAX_ROWS = 200;

export interface RunDiffProps {
  /** select a test in the tree (closes the sheet) */
  onJump: (key: string) => void;
  onClose: () => void;
}
export interface RunDiffSheet extends RunDiffProps {}
export class RunDiffSheet extends LabcoatElement<RunDiffProps> {
  static defaults: RunDiffProps = { onJump: () => {}, onClose: () => {} };
  static hostClass = "run-diff";

  protected render(): Child {
    this.setAttribute("role", "dialog");
    this.setAttribute("aria-label", "Compare runs");
    this.tabIndex = -1;
    const store = getStore();
    const $ = store.$;
    const close = () => this.onClose();

    this.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      // Don't let the app's global Escape (back to live) see it.
      e.stopPropagation();
      close();
    });
    // Focus the sheet when it opens; give focus back to where it was on close.
    const prevFocus = document.activeElement as HTMLElement | null;
    queueMicrotask(() => this.querySelector<HTMLElement>(".run-diff-close")?.focus({ preventScroll: true }));
    onCleanup(() => {
      if ((this.contains(document.activeElement) || document.activeElement === document.body) && prevFocus?.isConnected) prevFocus.focus({ preventScroll: true });
    });

    const nameOf = (key: string) => {
      const t = store.get().tests.get(key);
      if (t) return { name: t.displayName, ctx: [t.namespace, t.className].filter(Boolean).join(".") };
      const id = key.slice(key.indexOf("|") + 1);
      return { name: id.split(".").pop() ?? id, ctx: "no longer discovered" };
    };

    const ready = computed(() => {
      const r = comparisonResult();
      return r?.status === "ready" ? r.diff : undefined;
    });

    const picker = (side: "base" | "target", label: string) => {
      const sel = h(
        "select",
        {
          class: "run-diff-select",
          "aria-label": label,
          onchange: (e) => {
            const c = comparison.peek();
            if (!c) return;
            const v = (e.currentTarget as HTMLSelectElement).value;
            openCompare(side === "base" ? v : c.base, side === "base" ? c.target : v);
          },
        },
        h("option", { value: LIVE }, "Live (current results)"),
        list(
          $.runs,
          (r) => r.runId,
          (r) => h("option", { value: r.peek().runId }, () => optionLabel(r())),
        ),
      );
      // value after the options exist (and again when the list changes)
      effect(() => {
        $.runs();
        const v = comparison()?.[side];
        if (v) sel.value = v;
      });
      return h("label", { class: "run-diff-pick" }, h("span", { class: "run-diff-pick-label" }, label), sel);
    };

    const row = (e: DiffEntry, section: SectionId) => {
      const { name, ctx } = nameOf(e.key);
      const b = e.before;
      const a = e.after;
      let detail: Child = null;
      if (section === "newlySlow")
        detail = h(
          "span",
          { class: "run-diff-detail mono" },
          `${formatDuration(b?.durationMs)} → ${formatDuration(a?.durationMs)}`,
          h("span", { class: "run-diff-factor" }, ` ${((a?.durationMs ?? 0) / Math.max(b?.durationMs ?? 1, 0.1)).toFixed(1)}×`),
        );
      else if (section === "newFailures" || section === "stillFailing") {
        const msg = a?.errorMessage?.split("\n")[0];
        if (msg) detail = h("span", { class: "run-diff-detail run-diff-error", title: a?.errorMessage }, msg);
      } else if (section === "fixed") detail = h("span", { class: "run-diff-detail mono" }, formatDuration(a?.durationMs));
      return h(
        "button",
        { class: "run-diff-row", title: `Show ${name} in the tree`, onclick: () => this.onJump(e.key) },
        h(
          "span",
          { class: "run-diff-states", "aria-hidden": "true" },
          statusIcon(b?.outcome ?? "notRun", 14),
          icon("chevronRight", { size: 12, class: "run-diff-arrow" }),
          statusIcon(a?.outcome ?? "notRun", 14),
        ),
        h("span", { class: "run-diff-name" }, h("span", { class: "run-diff-test" }, name), ctx ? h("span", { class: "run-diff-ctx" }, ctx) : null),
        detail,
      );
    };

    const sectionEls = new Map<SectionId, HTMLDetailsElement>();
    const section = (s: (typeof SECTIONS)[number]) => {
      const entries = computed(() => ready()?.[s.id] ?? []);
      const shown = computed(() => entries().slice(0, MAX_ROWS));
      return h(
        "details",
        {
          class: cx("run-diff-section", `tone-${s.tone}`),
          open: s.id !== "notRun",
          ref: (el) => sectionEls.set(s.id, el),
        },
        h(
          "summary",
          { class: "run-diff-section-head" },
          icon("chevronRight", { size: 14, class: "run-diff-caret" }),
          h("span", { class: "run-diff-section-title" }, s.title),
          h("span", { class: () => cx("run-diff-count", entries().length > 0 && "nonzero") }, () => entries().length),
        ),
        show(
          () => entries().length === 0,
          () => h("div", { class: "run-diff-empty" }, s.empty),
        ),
        h(
          "div",
          { class: "run-diff-rows" },
          list(
            shown,
            (e) => e.key,
            (e) => row(e.peek(), s.id),
          ),
        ),
        show(
          () => entries().length > MAX_ROWS,
          () => h("div", { class: "run-diff-empty" }, () => `+${entries().length - MAX_ROWS} more`),
        ),
      );
    };

    const goTo = (id: SectionId) => {
      const el = sectionEls.get(id);
      if (!el) return;
      el.open = true;
      el.scrollIntoView({ block: "start", behavior: "smooth" });
      el.querySelector<HTMLElement>("summary")?.focus({ preventScroll: true });
    };

    return [
      h(
        "header",
        { class: "run-diff-head" },
        h(
          "div",
          { class: "run-diff-title-row" },
          h(
            "div",
            { class: "run-diff-titles" },
            h("div", { class: "kind-label" }, "Compare runs"),
            h("h2", { class: "run-diff-title" }, () => {
              const c = comparison();
              const runs = $.runs();
              return c ? `${runRefLabel(c.base, runs)} → ${runRefLabel(c.target, runs)}` : "";
            }),
          ),
          iconButton({ icon: "dismiss", label: "Close (Esc)", class: "run-diff-close", onclick: close }),
        ),
        h(
          "div",
          { class: "run-diff-pickers" },
          picker("base", "Base"),
          iconButton({
            icon: "arrowSync",
            label: "Swap",
            class: "run-diff-swap",
            onclick: () => {
              const c = comparison.peek();
              if (c) comparison.set({ base: c.target, target: c.base });
            },
          }),
          picker("target", "Compared with"),
        ),
        h(
          "div",
          { class: "run-diff-summary", role: "group", "aria-label": "Summary" },
          SECTIONS.map((s) =>
            h(
              "button",
              {
                class: () => cx("run-diff-chip", `tone-${s.tone}`, (ready()?.[s.id].length ?? 0) > 0 && "nonzero"),
                disabled: () => !ready(),
                onclick: () => goTo(s.id),
              },
              h("span", { class: "run-diff-chip-count" }, () => ready()?.[s.id].length ?? "–"),
              () => ` ${(ready()?.[s.id].length === 1 && s.one ? s.one : s.title).toLowerCase()}`,
            ),
          ),
        ),
      ),
      h(
        "div",
        { class: "run-diff-body" },
        () => {
          const r = comparisonResult();
          if (r?.status === "loading") return h("div", { class: "run-diff-status" }, statusIcon("running", 16), " Loading runs…");
          if (r?.status === "missing")
            return h(
              "div",
              { class: "run-diff-status warn" },
              icon("warning", { size: 16 }),
              ` ${r.which.map((x) => runRefLabel(x, $.runs.peek())).join(" and ")} ${r.which.length > 1 ? "are" : "is"} no longer stored. Pick another run.`,
            );
          return null;
        },
        h("div", { class: "run-diff-sections", hidden: () => !ready() }, SECTIONS.map(section)),
      ),
    ];
  }
}

define("lc-run-diff", RunDiffSheet);

declare global {
  interface HTMLElementTagNameMap {
    "lc-run-diff": RunDiffSheet;
  }
}
