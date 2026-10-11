import { computed, signal, type ReadSignal, type Signal } from "@eriklieben/labcoat-design-system/core";
import type { ClientCommand, CoverageReport, EditorId, ExplorerState, HistoryEntry, ImpactMapInfo, LogLevel, RunDetails, RunSummary, ServerEvent, TestNode, TestProject, TestResultPatch } from "./contract";
import { applyResultPatch, normalizeTest, onlyOutcome } from "./test-results";
import type { ConnectionStatus, Transport } from "./transport";
import { getSettings } from "./settings";
import { serverEventListeners } from "./server-events";

export interface LogEntry {
  seq: number;
  level: LogLevel;
  message: string;
  timestamp: string;
  projectId?: string;
}

export interface AppState {
  transport: "ws" | "mock";
  conn: ConnectionStatus;
  retryInMs?: number;
  /** received at least one full state snapshot */
  loaded: boolean;
  /** true after we connected once; used to tell "lost connection" from "never connected" */
  everConnected: boolean;
  projects: TestProject[];
  /** keyed by `${projectId}|${id}` */
  tests: Map<string, TestNode>;
  testsVersion: number;
  currentRun?: RunSummary;
  lastRun?: RunSummary;
  /** keys of tests that belong to the current run (from runStarted) */
  runKeys?: Set<string>;
  /**
   * keys of current-run tests that have not reported a new result yet. Their previous result stays in `tests` (shown
   * dimmed) instead of being reset to "queued", so a watch rebuild doesn't turn the tree grey.
   */
  pending?: Set<string>;
  /** ExplorerState.workspaceName (header, document title) */
  workspaceName?: string;
  /**
   * `tests` / `pending` differ from `from` / `pendingFrom` only in `keys` (set by testsUpdated; lets the tree patch
   * the changed tests without scanning all of them). Undefined after any other change of `tests`.
   */
  testsDelta?: TestsDelta;
  watch: boolean;
  coverageEnabled: boolean;
  impactMap?: ImpactMapInfo;
  coverage?: CoverageReport;
  /** the full report of the Coverage page (state.fullCoverage / fullCoverageUpdated); never a subset run's report */
  fullCoverage?: CoverageReport;
  /** what the current "subset" report represents, e.g. the test's name */
  coverageLabel?: CoverageLabel;
  /** a per-test/subtree coverage run the user asked for and that has not produced a report yet */
  coverageRequest?: CoverageRequest;
  /** tree node selected while the Coverage tab is active: its cached coverage is looked up (never run) */
  coverageSelection?: CoverageSelection;
  aspireDashboardUrl?: string;
  logs: LogEntry[];
  /** count of error logs since the log panel was last opened */
  unseenErrors: number;
  /** persisted past runs, newest first (run switcher) */
  runs: RunSummary[];
  /** a past run shown read-only instead of the live state (undefined = live) */
  viewing?: ViewedRun;
  /** the queued run request (Round 4 run queue), shown next to the run progress */
  pendingRun?: import("./contract").PendingRunInfo;
}

export interface TestsDelta {
  from: Map<string, TestNode>;
  pendingFrom?: Set<string>;
  keys: Set<string>;
}

export interface ViewedRun {
  run: RunSummary;
  status: "loading" | "ready" | "missing";
  details?: RunDetails;
}

export interface CoverageLabel {
  title: string;
  /** dimmed context, e.g. "Namespace.Class" */
  context?: string;
}

export interface CoverageTarget {
  /** tree node key */
  key: string;
  label: CoverageLabel;
  testIds: string[];
  projectId?: string;
  /** a single test leaf (method or data row): a cache miss shows "No coverage collected" instead of the full report */
  isTest: boolean;
}

export interface CoverageSelection extends CoverageTarget {
  /** "loading": lookup in flight (the view keeps what it showed); "found": the cached report is shown; "missing": none cached */
  status: "loading" | "found" | "missing";
  /** while "loading": what was shown before, so the view does not flicker between lookups */
  prev?: CoverageSelection;
}

export interface CoverageRequest {
  label: CoverageLabel;
  /** empty = whole project run */
  testIds: string[];
  /** assigned on the matching runStarted */
  runId?: string;
  status: "pending" | "noResult";
}

const isFull = (c?: CoverageReport) => !!c && (c.scope ?? "all") === "all";
const sameIds = (a: string[], b: string[]) => {
  if (a.length !== b.length) return false;
  const s = new Set(a);
  return b.every((x) => s.has(x));
};

export const testKey = (t: { projectId: string; id: string }) => `${t.projectId}|${t.id}`;

/** `t` without the fetched extras of one kind (history, or the console output), for withExtras to fill in again. */
const withoutExtras = (t: TestNode, history: boolean) => (history ? { ...t, history: undefined } : { ...t, stdOut: undefined, stdErr: undefined }) as TestNode;
const sameExtras = (a: TestNode, b: TestNode) => a.history === b.history && a.stdOut === b.stdOut && a.stdErr === b.stdErr && a.flaky === b.flaky;

type HistoryBody = { tests: { projectId: string; id: string; history: HistoryEntry[]; flaky?: boolean }[] };

/** GET /api/tests/history (all tests, or per project); undefined for a failed response. */
function fetchHistoryBodies(projectIds: string[] | undefined): Promise<(HistoryBody | undefined)[]> {
  const urls = projectIds ? projectIds.map((p) => `./api/tests/history?projectId=${encodeURIComponent(p)}`) : ["./api/tests/history"];
  return Promise.all(
    urls.map(async (u) => {
      const res = await fetch(new URL(u, location.href));
      return res.ok ? ((await res.json()) as HistoryBody) : undefined;
    }),
  );
}

/** Code-unit order, as `.sort()` without a comparator: a stable, locale-independent order for cache keys. */
function byCodeUnit(a: string, b: string): number {
  if (a === b) return 0;
  return a < b ? -1 : 1;
}

const coverageKey = (projectId: string | undefined, ids: string[]) => `${projectId ?? ""}|${[...new Set(ids)].sort(byCodeUnit).join("\u001e")}`;

const MAX_LOGS = 5000;

export type StateSignals = { readonly [K in keyof AppState]-?: ReadSignal<AppState[K]> };

/**
 * App state. Event handlers update `this.state` immutably (same logic as before the web-component port); the UI reads
 * it through signals that are published at most once per animation frame (bursts of events → one update):
 *
 *   store.state()      the whole snapshot (tracked)
 *   store.$.tests()    one field, as a cached computed: dependents re-run only when that field changes
 */
export class Store {
  private state: AppState;
  private readonly stateSig: Signal<AppState>;
  private scheduled = false;
  private seq = 0;
  transport!: Transport;
  logPanelOpen = false;
  /** client-side memo of per-test coverage lookups (null = known miss); `runId` = filled by that run's coverageUpdated */
  private readonly covMemo = new Map<string, { report: CoverageReport | null; runId?: string }>();
  private covAbort?: AbortController;
  private covTimer?: number;
  /** runs whose start this client saw (runStarted, or a snapshot taken mid-run): only those can be celebrated */
  private readonly liveRuns = new Set<string>();

  sawRunStart(runId: string) {
    return this.liveRuns.has(runId);
  }

  constructor(kind: "ws" | "mock") {
    this.state = {
      transport: kind,
      conn: "connecting",
      loaded: false,
      everConnected: false,
      projects: [],
      tests: new Map(),
      testsVersion: 0,
      watch: false,
      coverageEnabled: false,
      logs: [],
      unseenErrors: 0,
      runs: [],
    };
    this.stateSig = signal(this.state);
    this.state$ = this.stateSig;
    const cache = new Map<string, ReadSignal<unknown>>();
    this.$ = new Proxy({} as StateSignals, {
      get: (_t, key: string) => {
        let c = cache.get(key);
        if (!c) {
          c = computed(() => this.stateSig()[key as keyof AppState]);
          cache.set(key, c);
        }
        return c;
      },
    });
  }

  /** Reactive snapshot of the whole state (published once per frame). */
  readonly state$: ReadSignal<AppState>;
  /** Per-field reactive state: `store.$.projects()`. */
  readonly $: StateSignals;

  private readonly runCache = new Map<string, RunDetails>();

  /**
   * Show a past run read-only (undefined = back to live). `fallback` describes the run when it isn't in the run list
   * (e.g. picked from a test's history strip).
   */
  viewRun(runId: string | undefined, fallback?: Partial<RunSummary>) {
    if (!runId) {
      if (this.state.viewing) {
        this.state = { ...this.state, viewing: undefined };
        this.notify();
      }
      return;
    }
    if (this.state.viewing?.run.runId === runId) return;
    const run: RunSummary = this.state.runs.find((r) => r.runId === runId) ?? { runId, startedAt: fallback?.startedAt ?? "", total: 0, passed: 0, failed: 0, skipped: 0, ...fallback };
    const cached = this.runCache.get(runId);
    this.state = { ...this.state, viewing: cached ? { run: cached.run, status: "ready", details: cached } : { run, status: "loading" } };
    this.notify();
    if (cached) return;
    this.transport.fetchRun(runId).then(
      (details) => {
        if (details) this.runCache.set(runId, details);
        if (this.state.viewing?.run.runId !== runId) return;
        this.state = { ...this.state, viewing: details ? { run: details.run, status: "ready", details } : { run, status: "missing" } };
        this.notify();
      },
      (err) => {
        console.warn("[runs] fetch failed", err);
        if (this.state.viewing?.run.runId !== runId) return;
        this.state = { ...this.state, viewing: { run, status: "missing" } };
        this.notify();
      },
    );
  }

  attach(t: Transport, onFirstFailure?: () => void) {
    this.transport = t;
    this.state = { ...this.state, transport: t.kind };
    t.start({
      onEvent: (e) => this.apply(e),
      onStatus: (s, info) => {
        this.state = {
          ...this.state,
          conn: s,
          retryInMs: info?.retryInMs,
          everConnected: this.state.everConnected || s === "open",
        };
        this.notify();
      },
      onFirstFailure,
    });
  }

  /** The latest state, synchronously (may be ahead of the published signals by up to one frame). */
  get = () => this.state;

  /** Coalesce bursts of events into one UI update per frame. */
  /**
   * Copy-on-write per frame: the tests Map / pending Set created since the last publish may be mutated in place by the
   * next event of the same frame (a burst of testsUpdated → one copy, not one per event). Published ones never change.
   */
  private draftTests?: Map<string, TestNode>;
  private draftPending?: Set<string>;
  private draftDelta?: TestsDelta;

  private notify() {
    if (this.scheduled) return;
    this.scheduled = true;
    requestAnimationFrame(() => {
      this.scheduled = false;
      this.draftTests = undefined;
      this.draftPending = undefined;
      this.draftDelta = undefined;
      this.stateSig.set(this.state);
    });
  }

  send(cmd: ClientCommand) {
    // Optimistic UI where the contract has no explicit acknowledgement event.
    if (cmd.type === "setWatch") this.state = { ...this.state, watch: cmd.enabled };
    if (cmd.type === "setCoverage") this.state = { ...this.state, coverageEnabled: cmd.enabled };
    // "Run all" (no testIds) returns the coverage view to the global overview.
    if (cmd.type === "run" && !cmd.testIds?.length) this.resetCoverageScope();
    this.transport.send(cmd);
    this.notify();
  }

  /** Run `testIds` (or a whole project) with coverage forced on for that run only. */
  requestCoverage(label: CoverageLabel, testIds: string[], projectId?: string) {
    this.state = { ...this.state, coverageRequest: { label, testIds, status: "pending" } };
    this.transport.send({ type: "run", projectId, testIds: testIds.length ? testIds : undefined, coverage: true });
    this.notify();
  }

  dismissCoverageRequest() {
    this.state = { ...this.state, coverageRequest: undefined };
    this.notify();
  }

  /** Switch the coverage view back to the cached full report. */
  showFullCoverage() {
    this.cancelCoverageLookup();
    if (!this.state.fullCoverage) {
      if (this.state.coverageSelection) {
        this.state = { ...this.state, coverageSelection: undefined };
        this.notify();
      }
      return;
    }
    this.state = { ...this.state, coverage: this.state.fullCoverage, coverageLabel: undefined, coverageRequest: undefined, coverageSelection: undefined };
    this.notify();
  }

  /**
   * Back to the global coverage overview (e.g. on "Run all"): drops the per-test scope, any pending per-test request and
   * the selection context; the next "all" report then replaces the view.
   */
  resetCoverageScope() {
    this.cancelCoverageLookup();
    const s = this.state;
    this.state = {
      ...s,
      coverage: isFull(s.coverage) ? s.coverage : s.fullCoverage,
      coverageLabel: undefined,
      coverageRequest: undefined,
      coverageSelection: undefined,
    };
    this.notify();
  }

  private cancelCoverageLookup() {
    window.clearTimeout(this.covTimer);
    this.covAbort?.abort();
    this.covAbort = undefined;
  }

  /**
   * Show the cached coverage of a tree selection (Coverage tab). Never starts a run: a known report is shown at once,
   * otherwise GET /api/coverage/test after `debounceMs` (stale lookups are aborted). Undefined = back to the full report.
   */
  selectCoverage(target: CoverageTarget | undefined, debounceMs = 0) {
    this.cancelCoverageLookup();
    if (!target?.testIds.length) return this.showFullCoverage();
    const key = coverageKey(target.projectId, target.testIds);
    const memo = this.covMemo.get(key);
    if (memo) return this.applySelection(target, memo.report);
    const prev = this.state.coverageSelection;
    this.state = { ...this.state, coverageSelection: { ...target, status: "loading", prev: prev?.status === "loading" ? prev.prev : prev } };
    this.notify();
    this.covTimer = window.setTimeout(() => this.lookup(target, key), debounceMs);
  }

  private lookup(target: CoverageTarget, key: string) {
    const ac = new AbortController();
    this.covAbort = ac;
    this.transport.fetchTestCoverage(target.projectId, target.testIds, ac.signal).then(
      (report) => {
        if (ac.signal.aborted) return;
        this.covMemo.set(key, { report });
        if (this.state.coverageSelection?.key === target.key) this.applySelection(target, report);
      },
      (err) => {
        if (ac.signal.aborted || (err as Error)?.name === "AbortError") return;
        console.warn("[coverage] lookup failed", err);
        if (this.state.coverageSelection?.key === target.key) this.applySelection(target, null);
      },
    );
  }

  private applySelection(full: CoverageTarget, report: CoverageReport | null) {
    const s = this.state;
    const target: CoverageTarget = { key: full.key, label: full.label, testIds: full.testIds, projectId: full.projectId, isTest: full.isTest };
    if (report) {
      this.state = { ...s, coverage: report, coverageLabel: target.label, coverageSelection: { ...target, status: "found" } };
    } else if (target.isTest) {
      this.state = { ...s, coverage: s.fullCoverage ?? (isFull(s.coverage) ? s.coverage : undefined), coverageLabel: undefined, coverageSelection: { ...target, status: "missing" } };
    } else {
      // A class/namespace without its own cached run: the full report, but the selection stays ("missing") so the pane
      // can say that it shows all tests and offer to collect the class's own coverage.
      this.state = { ...s, coverage: s.fullCoverage ?? (isFull(s.coverage) ? s.coverage : undefined), coverageLabel: undefined, coverageSelection: { ...target, status: "missing" } };
    }
    this.notify();
  }

  /** Re-check the current selection silently (after builds/runs: the cached report may be stale now). */
  private refreshSelection() {
    const sel = this.state.coverageSelection;
    if (!sel || sel.status === "loading") return;
    const key = coverageKey(sel.projectId, sel.testIds);
    if (this.covMemo.has(key)) return;
    this.cancelCoverageLookup();
    this.lookup(sel, key);
  }

  private labelFor(c: CoverageReport): CoverageLabel | undefined {
    if (isFull(c)) return undefined;
    const ids = c.testIds ?? [];
    const req = this.state.coverageRequest;
    if (req && (req.runId === c.runId || sameIds(req.testIds, ids))) return req.label;
    if (ids.length === 1) {
      const t = [...this.state.tests.values()].find((x) => x.id === ids[0]);
      if (t) return { title: t.displayName, context: [t.namespace, t.className].filter(Boolean).join(".") };
    }
    return { title: `${ids.length} tests` };
  }

  // ---- lean mode (Round 4): history and console output on demand ----------------------------------------------

  private lean = false;
  private historyLoaded = false;
  private historyFetch?: Promise<void>;
  /** another history sync was asked for while a fetch was in flight */
  private historyAgain = false;
  /** ExplorerState.historyStamps of the latest lean snapshot (Round 7): what the history should be loaded for */
  private historyWanted?: Record<string, string>;
  /** projectId → history stamp loaded (or being loaded) */
  private readonly historyStamps = new Map<string, string>();
  /** keys whose history changed through testResults while a history fetch was in flight (merged into its response) */
  private historyTouched?: Set<string>;
  /** testKey → history (GET /api/tests/history), kept current by testResults (historyAdd / recorded) */
  private history = new Map<string, { history: HistoryEntry[]; flaky?: boolean }>();
  /** testKey → full details of one result (GET /api/tests/details), valid while lastRunAt matches */
  private readonly details = new Map<string, { lastRunAt?: string; stdOut?: string; stdErr?: string; outputTruncated?: boolean }>();
  private readonly detailsInFlight = new Set<string>();
  /**
   * The host's version of tests whose displayed entry is kept at the previous result while the run is pending (their
   * queued update was not shown): later result patches apply to this, not to what is displayed.
   */
  private readonly shadow = new Map<string, TestNode>();
  /** like `shadow`, cheaper: the host's version is the displayed test with this outcome (a run's queued patches) */
  private readonly shadowOutcome = new Map<string, TestNode["outcome"]>();

  /** `base` with a testResults patch applied; lean: the fetched output only while it is for this result, history kept current. */
  private patched(k: string, base: TestNode, p: import("./contract").TestResultPatch, runId?: string): TestNode {
    let n = applyResultPatch(base, p, runId);
    if (!this.lean) return n;
    if (n.lastRunAt !== base.lastRunAt || n.hasOutput !== base.hasOutput) {
      n.stdOut = undefined;
      n.stdErr = undefined;
      n = this.withExtras(n);
    }
    if (n.history !== base.history || n.flaky !== base.flaky) {
      const h = this.history.get(k);
      if (h && n.history) {
        h.history = n.history;
        h.flaky = n.flaky;
      } else if (n.history) this.history.set(k, { history: n.history, flaky: n.flaky });
      this.historyTouched?.add(k);
    }
    return n;
  }

  /** A lean test with the history / output this client already fetched (same object when there is nothing to add). */
  private withExtras(t: TestNode): TestNode {
    if (!this.lean) return t;
    const k = testKey(t);
    const hist = t.history ? undefined : this.history.get(k);
    const d = t.hasOutput && t.stdOut == null && t.stdErr == null ? this.details.get(k) : undefined;
    const det = d && d.lastRunAt === t.lastRunAt ? d : undefined;
    if (!hist && !det) return t;
    return {
      ...t,
      ...(hist ? { history: hist.history, flaky: t.flaky ?? hist.flaky } : {}),
      ...(det ? { stdOut: det.stdOut, stdErr: det.stdErr, outputTruncated: t.outputTruncated ?? det.outputTruncated } : {}),
    };
  }

  /**
   * Copy-on-write access to `tests` for an update of some keys: the Map of this frame (one copy per frame however many
   * events arrive) and the delta the tree patches from.
   */
  private draftTests_(): { tests: Map<string, TestNode>; delta: TestsDelta } {
    const s = this.state;
    const draft = this.draftTests === s.tests && this.draftDelta === s.testsDelta && !!s.testsDelta;
    if (draft) return { tests: s.tests, delta: this.draftDelta! };
    this.draftDelta = { from: s.tests, pendingFrom: s.pending, keys: new Set() };
    this.draftTests = new Map(s.tests);
    return { tests: this.draftTests, delta: this.draftDelta };
  }

  /** Re-apply fetched extras to `keys` (after a history / details fetch). `history`: the history changed (else the output). */
  private patchTests(keys: Iterable<string>, history: boolean) {
    let tests: Map<string, TestNode> | undefined;
    let delta: TestsDelta | undefined;
    for (const k of keys) {
      const t = this.state.tests.get(k);
      if (!t) continue;
      const n = this.withExtras(withoutExtras(t, history));
      const sh = this.shadow.get(k);
      if (sh) this.shadow.set(k, this.withExtras(withoutExtras(sh, history)));
      if (sameExtras(n, t)) continue;
      if (!tests) ({ tests, delta } = this.draftTests_());
      tests.set(k, n);
      delta!.keys.add(k);
    }
    if (!tests) return;
    this.state = { ...this.state, tests, testsDelta: delta, testsVersion: this.state.testsVersion + 1 };
    this.notify();
  }

  /** Tests per merge step of a big history response: each step is one short task (and an incremental tree patch). */
  static readonly historyChunk = 2500;

  /** Merge fetched history into `keys`' tests, in steps of `historyChunk` with a macrotask in between (no long task). */
  private mergeHistory(keys: string[]): Promise<void> {
    const n = Store.historyChunk;
    return new Promise<void>((resolve, reject) => {
      // one step per macrotask, the first one right away: a chain of timeouts (the steps are sequential by design)
      const step = (i: number) => {
        try {
          if (i >= keys.length) return resolve();
          this.patchTests(keys.slice(i, i + n), true);
          if (i + n < keys.length) setTimeout(() => step(i + n), 0);
          else resolve();
        } catch (err) {
          reject(err instanceof Error ? err : new Error(String(err)));
        }
      };
      step(0);
    });
  }

  /**
   * GET /api/tests/history (all tests, or the given projects): the history strip, run diff, flaky and "new failure"
   * rules need it. One request at a time.
   */
  fetchHistory(projectIds?: string[]): Promise<void> {
    if (this.state.transport !== "ws") return Promise.resolve();
    if (this.historyFetch) return this.historyFetch;
    const touched = new Set<string>();
    this.historyTouched = touched;
    this.historyFetch = this.loadHistory(projectIds, touched);
    return this.historyFetch;
  }

  private async loadHistory(projectIds: string[] | undefined, touched: Set<string>) {
    try {
      const bodies = await fetchHistoryBodies(projectIds);
      if (bodies.some((b) => !b)) for (const p of projectIds ?? [...this.historyStamps.keys()]) this.historyStamps.delete(p); // retried with the next snapshot
      if (!bodies.some(Boolean)) return;
      const keys = this.takeHistory(bodies, projectIds, touched);
      this.historyLoaded = true;
      await this.mergeHistory(keys);
    } catch (err) {
      console.warn("[history] fetch failed", err);
    } finally {
      this.historyFetch = undefined;
      this.historyTouched = undefined;
      if (this.historyAgain) {
        this.historyAgain = false;
        void this.syncHistory();
      }
    }
  }

  /** Store fetched history (a full fetch replaces it, a per-project one adds to it); the keys whose tests need a patch. */
  private takeHistory(bodies: (HistoryBody | undefined)[], projectIds: string[] | undefined, touched: Set<string>) {
    const keys: string[] = [];
    const next = projectIds ? this.history : new Map<string, { history: HistoryEntry[]; flaky?: boolean }>();
    for (const b of bodies)
      for (const x of b?.tests ?? []) {
        const k = testKey(x);
        next.set(k, { history: this.withTouchedHistory(k, x.history, touched), flaky: x.flaky });
        keys.push(k);
      }
    if (!projectIds) this.replaceHistory(next, keys, touched);
    return keys;
  }

  /** A full history fetch: `next` replaces the history; tests that lost theirs are added to `keys`. */
  private replaceHistory(next: Map<string, { history: HistoryEntry[]; flaky?: boolean }>, keys: string[], touched: Set<string>) {
    // tests that have no history any more (e.g. after a host restart with another history file)
    for (const [k, t] of this.state.tests) if (t.history && !next.has(k) && !touched.has(k)) keys.push(k);
    for (const k of touched) if (!next.has(k) && this.history.has(k)) next.set(k, this.history.get(k)!);
    this.history = next;
  }

  /** A result recorded while the history response was on its way: keep it (testResults may arrive before or after it). */
  private withTouchedHistory(k: string, fetched: HistoryEntry[], touched: Set<string>) {
    const mine = touched.has(k) ? this.history.get(k)?.history : undefined;
    let h = fetched;
    for (const e of mine ?? []) if (!h.some((y) => y.at === e.at && y.runId === e.runId) && e.at > (h.at(-1)?.at ?? "")) h = [...h, e];
    return h;
  }

  /** Lean snapshots with historyStamps (Round 7): fetch the history of the projects whose stamp this client hasn't loaded. */
  private syncHistory(): Promise<void> {
    const want = this.historyWanted;
    if (!want || this.state.transport !== "ws") return Promise.resolve();
    if (this.historyFetch) {
      this.historyAgain = true;
      return this.historyFetch;
    }
    const ids = Object.keys(want);
    const stale = ids.filter((p) => this.historyStamps.get(p) !== want[p]);
    if (!stale.length) return Promise.resolve();
    for (const p of stale) this.historyStamps.set(p, want[p]);
    return this.fetchHistory(stale.length === ids.length ? undefined : stale);
  }

  /** Lean mode: fetch the console output of the selected test (GET /api/tests/details) unless it is already here. */
  ensureDetails(t: TestNode) {
    if (!this.lean || !t.hasOutput || t.stdOut != null || t.stdErr != null) return;
    const k = testKey(t);
    const flight = `${k}@${t.lastRunAt ?? ""}`;
    const known = this.details.get(k);
    if (this.detailsInFlight.has(flight) || (known && known.lastRunAt === t.lastRunAt)) return;
    this.detailsInFlight.add(flight);
    const q = new URLSearchParams({ projectId: t.projectId, id: t.id });
    fetch(new URL(`./api/tests/details?${q}`, location.href))
      .then((r) => (r.ok ? (r.json() as Promise<TestNode>) : undefined))
      .then((full) => {
        if (!full) return;
        this.details.set(k, { lastRunAt: full.lastRunAt, stdOut: full.stdOut, stdErr: full.stdErr, outputTruncated: full.outputTruncated });
        const hadHistory = !!this.history.get(k);
        if (full.history && !hadHistory) this.history.set(k, { history: full.history, flaky: full.flaky });
        this.patchTests([k], false);
        if (full.history && !hadHistory) this.patchTests([k], true);
      })
      .catch((err) => console.warn("[details] fetch failed", err))
      .finally(() => this.detailsInFlight.delete(flight));
  }

  /** The full (untruncated) console output of a test (GET /api/tests/output), when the host still has it. */
  async fetchFullOutput(t: TestNode): Promise<{ stdOut?: string; stdErr?: string; truncated: boolean } | undefined> {
    if (this.state.transport !== "ws") return { stdOut: t.stdOut, stdErr: t.stdErr, truncated: false };
    const q = new URLSearchParams({ projectId: t.projectId, id: t.id });
    const res = await fetch(new URL(`./api/tests/output?${q}`, location.href));
    if (!res.ok) return undefined;
    return (await res.json()) as { stdOut?: string; stdErr?: string; truncated: boolean };
  }

  markLogsSeen() {
    if (this.state.unseenErrors) {
      this.state = { ...this.state, unseenErrors: 0 };
      this.notify();
    }
  }

  clearLogs() {
    this.state = { ...this.state, logs: [], unseenErrors: 0 };
    this.notify();
  }

  apply(e: ServerEvent) {
    const s = this.state;
    switch (e.type) {
      case "state":
        this.applySnapshot(s, e.state);
        break;
      case "projectStatus": {
        const idx = s.projects.findIndex((p) => p.id === e.project.id);
        const projects = idx >= 0 ? s.projects.map((p, i) => (i === idx ? e.project : p)) : [...s.projects, e.project];
        // A rebuild may make cached per-test coverage stale: look it up again next time.
        if (e.project.status === "building") this.covMemo.clear();
        this.state = { ...s, projects };
        break;
      }
      case "testsUpdated":
      case "testResults":
        this.applyTests(s, e);
        break;
      case "runStarted":
        this.applyRunStarted(s, e);
        break;
      case "runFinished":
        this.applyRunFinished(s, e.run);
        break;
      case "coverageUpdated":
        this.applyCoverage(s, e.coverage);
        break;
      case "fullCoverageUpdated": {
        // The Coverage page's report: only ever replaced by this event (a subset run's coverageUpdated never touches it).
        this.state = { ...s, fullCoverage: e.coverage };
        break;
      }
      case "runsUpdated": {
        this.state = { ...s, runs: e.runs };
        break;
      }
      case "pendingRun": {
        this.state = { ...s, pendingRun: e.pending };
        break;
      }
      case "settingsChanged": {
        // lean clients get this instead of a full state on toggles
        const st = e.settings;
        this.state = { ...s, watch: st.watch, coverageEnabled: st.coverageEnabled, tracingEnabled: st.tracingEnabled, quarantined: st.quarantined ?? s.quarantined, impactMap: st.impactMap ?? s.impactMap };
        break;
      }
      case "log":
        this.applyLog(s, e);
        break;
    }
    // events other modules care about (e.g. debuggerWaiting → debug.ts), after the state update
    for (const l of serverEventListeners) l(e as { type: string });
    // SHELL: tests replaced by anything but testsUpdated → the tree can't patch from a delta
    // (also when only `pending` changed: runStarted / runFinished replace it for keys the delta doesn't list, and a
    // delta left in place would make the tree patch only the listed keys: stale "previous result" flags after a run)
    if (e.type !== "testsUpdated" && e.type !== "testResults" && (this.state.tests !== s.tests || this.state.pending !== s.pending) && this.state.testsDelta)
      this.state = { ...this.state, testsDelta: undefined };
    this.notify();
  }

  /** A full state snapshot (on connect, or a broadcast). */
  private applySnapshot(s: AppState, st: ExplorerState) {
    const pending = st.currentRun ? s.pending : undefined;
    const wasLean = this.lean;
    this.lean = !!st.lean;
    const tests = this.snapshotTests(s, st, pending);
    // lean snapshots carry no history. With historyStamps (Round 7) it is fetched per project when its stamp
    // changes and kept current by testResults; otherwise (older hosts) once, and again after every run.
    this.historyWanted = this.lean ? st.historyStamps : undefined;
    if (this.historyWanted) queueMicrotask(() => void this.syncHistory());
    else if (this.lean && (!wasLean || !this.historyLoaded)) queueMicrotask(() => void this.fetchHistory());
    if (st.currentRun) this.liveRuns.add(st.currentRun.runId);
    this.state = {
      ...s,
      loaded: true,
      projects: st.projects,
      tests,
      testsVersion: s.testsVersion + 1,
      currentRun: st.currentRun,
      lastRun: st.lastRun,
      runKeys: st.currentRun ? s.runKeys : undefined,
      pending,
      workspaceName: st.workspaceName ?? s.workspaceName,
      watch: st.watch,
      coverageEnabled: !!st.coverageEnabled,
      impactMap: st.impactMap,
      coverage: st.coverage,
      fullCoverage: st.fullCoverage ?? s.fullCoverage,
      coverageLabel: st.coverage?.runId === s.coverage?.runId ? s.coverageLabel : undefined,
      aspireDashboardUrl: st.aspireDashboardUrl,
      runs: st.runs ?? s.runs,
      pendingRun: st.pendingRun,
      ...detailStateFields(st), // DETAIL addition (see the end of the file)
      ...runsStateFields(st), // RUNS addition (see the end of the file)
      // The on-connect snapshot carries the server's log history (e.g. startup build output);
      // broadcast snapshots omit it so the panel keeps its own (possibly cleared) contents.
      ...(st.logs ? this.snapshotLogs(st.logs) : {}),
    };
  }

  /** The snapshot's tests; a test still pending in the current run keeps showing its previous result. */
  private snapshotTests(s: AppState, st: ExplorerState, pending: Set<string> | undefined) {
    const tests = new Map<string, TestNode>();
    this.shadow.clear();
    this.shadowOutcome.clear();
    for (const t of st.tests) {
      normalizeTest(t, st.files); // lean: fileIndex → filePath, omitted traits → []
      const k = testKey(t);
      const prev = pending?.has(k) && t.outcome === "queued" ? s.tests.get(k) : undefined;
      if (prev) this.shadow.set(k, this.withExtras(t));
      tests.set(k, prev ?? this.withExtras(t));
    }
    return tests;
  }

  private snapshotLogs(logs: NonNullable<ExplorerState["logs"]>): Pick<AppState, "logs" | "unseenErrors"> {
    return {
      logs: logs.map((l) => ({ seq: ++this.seq, level: l.level, message: l.message, timestamp: l.timestamp, projectId: l.projectId })),
      unseenErrors: this.logPanelOpen ? 0 : logs.filter((l) => l.level === "error").length,
    };
  }

  /** testsUpdated (whole tests) and testResults (patches of this run's results). */
  private applyTests(s: AppState, e: EventOf<"testsUpdated" | "testResults">) {
    const draft: TestsDraft = { ...this.draftTests_(), pending: s.pending };
    if (e.type === "testsUpdated") for (const t of e.tests) this.upsertTest(draft, testKey(normalizeTest(t)), this.withExtras(t));
    else for (const g of e.projects) for (const p of g.results) this.applyResult(draft, `${g.projectId}|${p.id}`, p, e.runId);
    this.state = { ...s, tests: draft.tests, pending: draft.pending, testsDelta: draft.delta, testsVersion: s.testsVersion + 1 };
  }

  /** Store one updated test. */
  private upsertTest(draft: TestsDraft, k: string, t: TestNode) {
    draft.delta.keys.add(k);
    if (draft.pending?.has(k)) {
      // Still waiting for this run's result: keep showing the previous one (dimmed) instead of "queued"; later
      // patches apply to the host's version (shadow).
      if (t.outcome === "queued" || t.outcome === "notRun") {
        this.shadow.set(k, t);
        return;
      }
      if (draft.pending !== this.draftPending) {
        this.draftPending = new Set(draft.pending);
        draft.pending = this.draftPending;
      }
      draft.pending.delete(k);
    }
    this.shadow.delete(k);
    this.shadowOutcome.delete(k);
    draft.tests.set(k, t);
  }

  /** One result patch of a testResults event. */
  private applyResult(draft: TestsDraft, k: string, p: TestResultPatch, runId: string | undefined) {
    // a pending test's queued update (most of a run's first event): just remember the outcome
    if ((p.outcome === "queued" || p.outcome === "notRun") && draft.pending?.has(k) && !this.shadow.has(k) && onlyOutcome(p)) {
      this.shadowOutcome.set(k, p.outcome);
      return;
    }
    const so = this.shadowOutcome.get(k);
    const shown = draft.tests.get(k);
    const base = this.shadow.get(k) ?? (so && shown ? { ...shown, outcome: so } : shown);
    // a patch for a test this client doesn't have can't be applied (never expected: the host sends new tests in full)
    if (base) this.upsertTest(draft, k, this.patched(k, base, p, runId));
  }

  private applyRunStarted(s: AppState, e: EventOf<"runStarted">) {
    this.liveRuns.add(e.run.runId);
    // testIds carry no projectId; ids are MTP uids so we match across projects.
    const ids = new Set(e.testIds);
    const runKeys = new Set<string>();
    const pending = new Set<string>();
    const tests = s.tests;
    for (const [k, t] of s.tests) {
      if (ids.has(t.id)) {
        runKeys.add(k);
        if (t.outcome !== "running") pending.add(k);
      }
    }
    let coverageRequest = s.coverageRequest;
    if (coverageRequest?.status === "pending" && !coverageRequest.runId && (!coverageRequest.testIds.length || sameIds(coverageRequest.testIds, e.testIds)))
      coverageRequest = { ...coverageRequest, runId: e.run.runId };
    // A run-all started elsewhere (e.g. the Aspire dashboard): every test of the touched projects -> global coverage view.
    const runAll = !coverageRequest && coversWholeProjects(s.tests, runKeys);
    // Any new run returns the Tests page to live.
    this.state = { ...s, currentRun: e.run, runKeys, pending, tests, testsVersion: s.testsVersion + 1, coverageRequest, viewing: undefined };
    if (runAll && (s.coverageSelection || s.coverageLabel)) this.resetCoverageScope();
  }

  private applyRunFinished(s: AppState, run: RunSummary) {
    const tests = cancelUnfinished(s);
    for (const [k, v] of this.covMemo) if (v.runId !== run.runId) this.covMemo.delete(k);
    this.shadow.clear();
    this.shadowOutcome.clear();
    // older hosts (no historyStamps): the run's history only arrives with a new fetch
    if (this.lean && !this.historyWanted) queueMicrotask(() => void this.fetchHistory());
    queueMicrotask(() => this.refreshSelection());
    this.state = {
      ...s,
      currentRun: undefined,
      lastRun: run,
      pending: undefined,
      tests,
      testsVersion: tests === s.tests ? s.testsVersion : s.testsVersion + 1,
      // The host publishes coverageUpdated before runFinished: a request still pending here produced no report.
      coverageRequest:
        s.coverageRequest?.runId === run.runId && s.coverageRequest.status === "pending" ? { ...s.coverageRequest, status: "noResult" } : s.coverageRequest,
    };
  }

  private applyCoverage(s: AppState, coverage: CoverageReport) {
    const req = s.coverageRequest;
    const matches = !!req && (req.runId === coverage.runId || (!isFull(coverage) && sameIds(req.testIds, coverage.testIds ?? [])));
    const ids = coverage.testIds ?? [];
    if (!isFull(coverage) && ids.length && coverage.projectIds.length === 1)
      this.covMemo.set(coverageKey(coverage.projectIds[0], ids), { report: coverage, runId: coverage.runId });
    const sel = s.coverageSelection;
    const selMatches = !!sel && !isFull(coverage) && sameIds(sel.testIds, ids);
    if (sel && !selMatches) this.cancelCoverageLookup();
    this.state = {
      ...s,
      coverage,
      coverageLabel: selMatches && !matches ? sel.label : this.labelFor(coverage),
      coverageRequest: matches ? undefined : req,
      coverageSelection: selMatches ? { ...sel, status: "found" } : undefined,
    };
  }

  private applyLog(s: AppState, e: EventOf<"log">) {
    const entry: LogEntry = { seq: ++this.seq, level: e.level, message: e.message, timestamp: e.timestamp, projectId: e.projectId };
    const logs = s.logs.length >= MAX_LOGS ? [...s.logs.slice(s.logs.length - MAX_LOGS + 1), entry] : [...s.logs, entry];
    this.state = {
      ...s,
      logs,
      unseenErrors: e.level === "error" && !this.logPanelOpen ? s.unseenErrors + 1 : s.unseenErrors,
    };
  }
}

type EventOf<T extends ServerEvent["type"]> = Extract<ServerEvent, { type: T }>;

/** The tests Map, delta and pending Set a tests update writes to (copy-on-write per frame). */
interface TestsDraft {
  tests: Map<string, TestNode>;
  delta: TestsDelta;
  pending?: Set<string>;
}

/** The run's keys hold every test of the projects they touch (a run-all started elsewhere). */
function coversWholeProjects(tests: Map<string, TestNode>, runKeys: Set<string>) {
  if (!runKeys.size) return false;
  const pids = new Set([...runKeys].map((k) => k.slice(0, k.indexOf("|"))));
  return [...tests.entries()].every(([k, t]) => !pids.has(t.projectId) || runKeys.has(k));
}

/**
 * At runFinished: anything still running in the finished run can't be running any more; tests that never reported keep
 * their previous result. The same Map when nothing changed.
 */
function cancelUnfinished(s: AppState) {
  let tests = s.tests;
  for (const k of s.runKeys ?? []) {
    const t = tests.get(k);
    if (t && !s.pending?.has(k) && (t.outcome === "queued" || t.outcome === "running")) {
      if (tests === s.tests) tests = new Map(s.tests);
      tests.set(k, { ...t, outcome: "cancelled" });
    }
  }
  return tests;
}

// --- SHELL additions ---

/** Connection state for the header pill: "live" (connected), "connecting" (first attempt), "offline" (lost / unreachable). */
export type Connection = "live" | "connecting" | "offline";

export function connectionOf(s: Pick<AppState, "conn" | "everConnected">): Connection {
  if (s.conn === "open") return "live";
  return s.everConnected || s.conn === "reconnecting" ? "offline" : "connecting";
}

export type { EditorId } from "./contract";
const EDITOR_NAMES: Record<string, string> = { vscode: "Visual Studio Code", rider: "JetBrains Rider", visualstudio: "Visual Studio" };

/** Last "open in editor" failure, shown by the shell as a dismissible message bar. */
export const editorNotice = signal<string | undefined>(undefined);

/** ":line" / ":line:column" after a path ("" without a line). */
function lineSuffix(line?: number, column?: number) {
  if (!line) return "";
  return column ? `:${line}:${column}` : `:${line}`;
}

/**
 * Open a source file in the user's editor (Settings → editor; VS Code by default) through the host
 * (POST /api/open-in-editor). Falls back to a vscode:// link for VS Code when the host can't launch it.
 */
export async function openInEditor(path: string, line?: number, column?: number) {
  const s = getStore();
  const editor = (getSettings() as { editor?: EditorId }).editor ?? "vscode";
  const vscodeLink = () => {
    let p = path.replaceAll("\\", "/");
    if (!p.startsWith("/")) p = "/" + p;
    location.href = `vscode://file${encodeURI(p)}${lineSuffix(line, column)}`;
  };
  if (s.get().transport === "mock") {
    s.apply({ type: "log", level: "info", message: `Open in editor (${editor}): ${path}${lineSuffix(line)} (mock)`, timestamp: new Date().toISOString() });
    return;
  }
  try {
    const res = await fetch(new URL("./api/open-in-editor", location.href), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ editor, path, line, column }),
    });
    const body = (await res.json().catch(() => ({}))) as { ok?: boolean; message?: string };
    if (res.ok && body.ok !== false) return;
    if (editor === "vscode" && res.status !== 403 && res.status !== 404) return vscodeLink();
    const reason = body.message ?? `${res.status} ${res.statusText}`.trim();
    editorNotice.set(`Couldn't open ${EDITOR_NAMES[editor] ?? editor}: ${reason}`);
  } catch (err) {
    if (editor === "vscode") return vscodeLink();
    editorNotice.set(`Couldn't open ${EDITOR_NAMES[editor] ?? editor}: ${(err as Error)?.message ?? err}`);
  }
}

// Listeners for server events live in ./server-events (no imports) to avoid a store ⇄ settings ⇄ ui-defaults cycle.
export { serverEventListeners } from "./server-events";

let store: Store | undefined;
export function setStore(s: Store) {
  store = s;
}
export function getStore() {
  if (!store) throw new Error("store not initialised");
  return store;
}

// --- DETAIL additions --- (Round 3 state fields used by the detail pane; interfaces merge with AppState above)
export interface AppState {
  /** per-test traces are collected (Settings → tracing; off by default): trace links and failure telemetry only then */
  tracingEnabled?: boolean;
  /** directories served by GET /api/source: stack frames under them are "my code" */
  sourceRoots?: string[];
  /** quarantined tests as "projectId|id" keys (same format as testKey) */
  quarantined?: string[];
  editors?: import("./contract").EditorInfo[];
  /** GET /api/tests/telemetry is available (Aspire dashboard known) */
  telemetryAvailable?: boolean;
}

function detailStateFields(st: import("./contract").ExplorerState): Partial<AppState> {
  return {
    tracingEnabled: !!st.tracingEnabled,
    sourceRoots: st.sourceRoots,
    quarantined: st.quarantined,
    editors: st.editors,
    telemetryAvailable: st.telemetryAvailable,
  };
}

// --- RUNS additions --- (Round 3 state fields used by Settings and the run switcher)
export interface AppState {
  /** an OTLP endpoint is known (hosted by an Aspire AppHost): Settings shows the "Traces in the Aspire dashboard" switch */
  tracingAvailable?: boolean;
}

function runsStateFields(st: import("./contract").ExplorerState): Partial<AppState> {
  return { tracingAvailable: !!st.tracingAvailable };
}
