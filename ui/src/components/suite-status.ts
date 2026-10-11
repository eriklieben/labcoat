/**
 * Suite status (P0-1, P1-3, P1-6, P2-1): the headline in the toolbar, the composition bar under it, the screen-reader
 * announcer and the document title / favicon. All of them describe the SUITE (every test's current outcome), not the
 * last run: a 1-test run fills only its share of the bar, and a build failure / previous-session results turn the
 * headline amber ("outdated"). The last run is secondary text; while a run is in flight the headline shows its
 * progress and an ETA from the tests' history medians.
 */
import {
  computed,
  cx,
  effect,
  replayClass,
  tween,
  h,
  icon,
  list,
  onCleanup,
  show,
  signal,
  statusIcon,
  untracked,
  type Child,
  type ReadSignal,
  type Status,
} from "@eriklieben/labcoat-design-system";
import type { PendingRunInfo, RunSummary, TestNode, TestProject } from "../contract";
import { connectionOf, getStore } from "../store";
import { debugSession } from "../debug";
import { emptyCounts, requestStatusFilter, type Counts, type TreeNode } from "../tree";
import { formatDuration, formatRunTime } from "../util";
import { runningNow } from "../running-now";
import { runningNowChip } from "./running-now";
import "./suite-status.css";

export interface Suite {
  total: number;
  passed: number;
  failed: number;
  skipped: number;
  notRun: number;
  /** tests of the current run without a new result yet (pending, queued or running): not in any bucket above */
  inRun: number;
  /** tests with a result from older code (their project's build failed since) */
  outdated: number;
  /** results loaded from a previous session */
  stale: number;
  outdatedProjects: TestProject[];
  /** failing quarantined tests (not in `failed`) */
  quarantined: number;
  /** current run: its size and how many reported */
  runTotal: number;
  runDone: number;
}

/** Suite counts from the tree's aggregates (O(projects), not O(tests)): every test's current outcome. */
export function suiteOf(roots: readonly TreeNode[], projects: TestProject[], run: RunSummary | undefined, runKeys: ReadonlySet<string> | undefined): Suite {
  const c = emptyCounts();
  for (const r of roots) for (const k of Object.keys(c) as (keyof Counts)[]) c[k] += r.counts[k];
  const outdatedProjects = projects.filter((p) => p.resultsOutdated || p.status === "error");
  const inRun = run ? c.pending + c.running + c.queued : 0;
  const passed = c.passed - (run ? c.pendingPassed : 0);
  const failed = c.failed - (run ? c.pendingFailed : 0);
  const skipped = c.skipped - (run ? c.pendingSkipped : 0);
  const runTotal = run ? Math.max(runKeys?.size ?? 0, run.total) : 0;
  return {
    total: c.total,
    passed,
    failed,
    skipped,
    notRun: Math.max(0, c.total - passed - failed - skipped - inRun - c.quarantined),
    quarantined: c.quarantined,
    inRun,
    outdated: c.outdated,
    stale: Math.max(0, c.stale - c.outdated),
    outdatedProjects,
    runTotal,
    runDone: Math.max(0, runTotal - inRun),
  };
}

export type Tone = "running" | "building" | "failed" | "passed" | "outdated" | "notRun" | "empty";

const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? "" : "s"}`;

/** "7 failed · 16 passed · 25 tests" (never "Failed 25/25"). */
export function suiteLabel(s: Suite): string {
  if (s.total === 0) return "No tests";
  const ran = s.passed + s.failed + s.skipped;
  if (ran === 0) return `${plural(s.total, "test")} · not run yet`;
  const q = s.quarantined ? ` · ${s.quarantined} quarantined` : "";
  if (s.failed === 0 && s.notRun === 0) {
    const skipped = s.skipped ? ` · ${s.skipped} skipped` : "";
    return `${s.passed} passed${skipped}${q}`;
  }
  // the parts add up to the total (skipped / not run included)
  return [
    s.failed && `${s.failed} failed`,
    s.passed && `${s.passed} passed`,
    s.skipped && `${s.skipped} skipped`,
    s.notRun && `${s.notRun} not run`,
    s.quarantined && `${s.quarantined} quarantined`,
    plural(s.total, "test"),
  ]
    .filter(Boolean)
    .join(" · ");
}

// ---- ETA -------------------------------------------------------------------------------------------------------

function median(xs: number[]) {
  if (!xs.length) return undefined;
  const a = [...xs].sort((x, y) => x - y);
  const m = a.length >> 1;
  return a.length % 2 ? a[m] : (a[m - 1] + a[m]) / 2;
}

/** Expected duration per test of a run (median of its recent history; the run's average for unknown tests). */
function expectedDurations(tests: Map<string, TestNode>, keys: ReadonlySet<string>): Map<string, number> {
  const out = new Map<string, number>();
  let sum = 0;
  let n = 0;
  for (const k of keys) {
    const t = tests.get(k);
    const hist = t?.history?.filter((e) => e.durationMs != null && e.outcome !== "skipped").slice(-10).map((e) => e.durationMs!) ?? [];
    const m = median(hist) ?? t?.durationMs;
    if (m != null && m > 0) {
      out.set(k, m);
      sum += m;
      n++;
    }
  }
  const avg = n ? sum / n : 100;
  for (const k of keys) if (!out.has(k)) out.set(k, avg);
  return out;
}

/** "~40 s left" text (rounded so it doesn't flicker); undefined until there is enough to estimate. */
export function formatEta(ms: number | undefined): string | undefined {
  if (ms == null || !Number.isFinite(ms)) return undefined;
  if (ms < 1500) return "almost done";
  const s = ms / 1000;
  if (s < 10) return `~${Math.round(s)} s left`;
  if (s < 60) return `~${Math.round(s / 5) * 5} s left`;
  const m = Math.round(s / 60);
  return `~${m} min left`;
}

// ---- current run progress (one source for the headline, the title, the run overview and the repeat card) -------

export interface RunProgress {
  /** tests of the run with this run's result */
  done: number;
  total: number;
  /** of those, failed (only grows during the run: "2 failed so far") */
  failed: number;
}

/** Progress of the current run from its own results (not the suite's counts, which shift as old results are replaced). */
export const runProgress = computed<RunProgress | undefined>(() => {
  const $ = getStore().$;
  const run = $.currentRun();
  if (!run) return undefined;
  const keys = $.runKeys();
  const pending = $.pending();
  const tests = $.tests();
  let done = 0;
  let failed = 0;
  if (keys)
    for (const k of keys) {
      if (pending?.has(k)) continue;
      const t = tests.get(k);
      if (!t || t.outcome === "running" || t.outcome === "queued") continue;
      done++;
      if (t.outcome === "failed" || t.outcome === "timedOut") failed++;
    }
  return { done, total: Math.max(keys?.size ?? 0, run.total), failed };
});

/** A repeat run (Run 20×) in flight: the repetition it is on (from the host's "Repeat i/N" log lines). */
export const repeatProgress = computed<{ at: number; count: number } | undefined>(() => {
  const $ = getStore().$;
  const cur = $.currentRun();
  if (!cur?.repeat) return undefined;
  const logs = $.logs();
  let at = 0;
  for (let k = logs.length - 1; k >= 0 && k > logs.length - 200; k--) {
    const m = /^Repeat (\d+)\/(\d+)$/.exec(logs[k].message);
    if (m) {
      at = Number(m[1]);
      break;
    }
    if (logs[k].timestamp < cur.startedAt) break;
  }
  return { at: Math.max(1, at), count: cur.repeat.count };
});

/** "Running 9/25 · 2 failed so far", "Running 20× · repetition 12/20". */
export function runningLabel(): string {
  const rp = repeatProgress();
  if (rp) return `Running ${rp.count}× · repetition ${rp.at}/${rp.count}`;
  const p = runProgress();
  if (!p) return "Running";
  const failed = p.failed ? ` · ${p.failed} failed so far` : "";
  return `Running ${p.done}/${p.total}${failed}`;
}

// ---- the model the shell creates once -------------------------------------------------------------------------

export interface SuiteModel {
  suite: ReadSignal<Suite>;
  tone: ReadSignal<Tone>;
  running: ReadSignal<boolean>;
  /** project names being built / discovered (before a run starts) */
  busy: ReadSignal<string[]>;
  elapsed: ReadSignal<number | undefined>;
  eta: ReadSignal<string | undefined>;
  offline: ReadSignal<boolean>;
  /** results can't be trusted as current (offline / outdated / previous session only) */
  outdated: ReadSignal<boolean>;
}

export function suiteModel(roots: ReadSignal<TreeNode[]>): SuiteModel {
  const store = getStore();
  const $ = store.$;
  const running = computed(() => !!$.currentRun());
  const suite = computed(() => suiteOf(roots(), $.projects(), $.viewing() ? undefined : $.currentRun(), $.runKeys()));
  const busy = computed(() => $.projects().filter((p) => p.status === "building" || p.status === "discovering").map((p) => p.name));
  const offline = computed(() => connectionOf({ conn: $.conn(), everConnected: $.everConnected() }) === "offline" && $.loaded());
  const tone = computed<Tone>(() => {
    const s = suite();
    if (running()) return "running";
    if (busy().length) return "building";
    if (s.total === 0) return "empty";
    if (s.failed) return "failed";
    if (offline() || s.outdated || s.stale) return "outdated";
    if (s.passed + s.skipped + s.quarantined === s.total) return "passed";
    return "notRun";
  });

  const now = signal(Date.now());
  effect(() => {
    if (!running()) return;
    now.set(Date.now());
    const id = window.setInterval(() => now.set(Date.now()), 100);
    onCleanup(() => window.clearInterval(id));
  });
  const elapsed = computed(() => {
    const run = $.currentRun();
    return run ? now() - new Date(run.startedAt).getTime() : undefined;
  });

  // ETA: elapsed × (expected time of the tests still to come / expected time of the tests done). Medians are taken
  // once per run; the estimate is refreshed once per second.
  let expected: { runId: string; map: Map<string, number> } | undefined;
  const second = computed(() => Math.floor(now() / 1000));
  const eta = computed(() => {
    const run = $.currentRun();
    if (!run) return undefined;
    second();
    return untracked(() => {
      const st = store.get();
      const keys = st.runKeys;
      if (!keys?.size) return undefined;
      if (expected?.runId !== run.runId) expected = { runId: run.runId, map: expectedDurations(st.tests, keys) };
      let done = 0;
      let left = 0;
      let doneCount = 0;
      for (const k of keys) {
        const e = expected.map.get(k) ?? 0;
        const t = st.tests.get(k);
        if (st.pending?.has(k) || t?.outcome === "running" || t?.outcome === "queued") left += e;
        else {
          done += e;
          doneCount++;
        }
      }
      const el = Date.now() - new Date(run.startedAt).getTime();
      if (doneCount === 0 || done <= 0 || el < 1500) return undefined;
      return formatEta((el * left) / done);
    });
  });

  // amber: offline, outdated by a failed build, or only previous-session results (also when tests fail)
  const outdated = computed(() => {
    if (running()) return false;
    const s = suite();
    return offline() || s.outdated > 0 || (s.stale > 0 && s.stale >= s.passed + s.failed + s.skipped);
  });

  return { suite, tone, running, busy, elapsed, eta, offline, outdated };
}

// ---- run finish (P11) ------------------------------------------------------------------------------------------

/**
 * Calls `fn(before, after)` once when a run finishes: `before` is the suite as it was before the run started (the last
 * idle value), `after` the settled result. Never during a run (counts stream at ~33 mutations/s there).
 */
function onRunSettled(m: SuiteModel, fn: (before: Suite, after: Suite) => void) {
  let idle: Suite | undefined;
  let wasRunning = untracked(m.running);
  effect(() => {
    const r = m.running();
    const s = m.suite();
    untracked(() => {
      if (!r && wasRunning && idle) {
        const from = idle;
        // after the busy gate is lifted (same flush) and the last results are in
        queueMicrotask(() => fn(from, m.suite.peek()));
      }
      if (!r) idle = s;
      wasRunning = r;
    });
  });
}

const changed = (a: Suite, b: Suite) => a.passed !== b.passed || a.failed !== b.failed || a.skipped !== b.skipped;
const lerp = (a: number, b: number, k: number) => Math.round(a + (b - a) * k);

/** The headline icon per tone; the other tones show "not run". */
const TONE_ICON: Partial<Record<Tone, Status>> = { running: "running", building: "building", failed: "failed", passed: "passed" };

/** "next: 3 tests queued (2 requests)": the run queue (Round 4), at most one request waits; merged requests say so. */
function queuedText(q: PendingRunInfo): string {
  const what = q.scope === "subset" && q.testCount ? plural(q.testCount, "test") : "all tests";
  const merged = q.requests > 1 ? ` (${q.requests} requests)` : "";
  return `next: ${what} queued${merged}`;
}

/** The secondary text while a run is in flight: the debugger wait or the ETA, plus the queued run. */
function runningSecondary(m: SuiteModel, s: Suite, pending: PendingRunInfo | undefined): string {
  const dbg = debugSession();
  let base: string;
  if (dbg?.status === "waiting") base = `waiting for the debugger (PID ${dbg.pid})`;
  else base = m.eta() ?? (s.runDone || repeatProgress() ? "estimating…" : "starting…");
  return pending ? `${base} · ${queuedText(pending)}` : base;
}

const notRunSuffix = (n: number | undefined) => (n ? ` · ${n} not run` : "");

/** "Last run 10:42 · all tests · 3.2 s", "Last run aborted 10:42 · 4 not run". */
function lastRunText(last: RunSummary, suiteTotal: number): string {
  const when = formatRunTime(last.finishedAt ?? last.startedAt);
  if (last.aborted) return `Last run aborted ${when}${notRunSuffix(last.notRun)}`;
  if (last.cancelled) return `Last run cancelled ${when}${notRunSuffix(last.notRun)}`;
  const what = last.scope !== "subset" && last.total === suiteTotal ? "all tests" : plural(last.total, "test");
  const took = last.durationMs == null ? "" : ` · ${formatDuration(last.durationMs)}`;
  return `Last run ${when} · ${what}${took}`;
}

/** Projects Labcoat doesn't run (TestProject.testPlatform): "SampleTests uses VSTest: switch it to Microsoft.Testing.Platform". */
export function notOnMtpText(projects: readonly TestProject[]): string {
  if (projects.length !== 1) return `${projects.length} projects use VSTest: switch them to Microsoft.Testing.Platform`;
  const p = projects[0];
  return p.testPlatform === "none" ? `${p.name} is not a Microsoft.Testing.Platform test app` : `${p.name} uses VSTest: switch it to Microsoft.Testing.Platform`;
}

const outdatedWhat = (projects: readonly { name: string }[]) => (projects.length === 1 ? projects[0].name : `${projects.length} projects`);

// ---- headline -------------------------------------------------------------------------------------------------

export function runSummaryBar(m: SuiteModel, celebrating: ReadSignal<boolean>): Child {
  const store = getStore();
  const $ = store.$;
  const icn = computed<Status>(() => {
    return TONE_ICON[m.tone()] ?? "notRun";
  });
  // P11: on run finish the changed counts tick from their value before the run to the final one (320 ms); decorative
  // (skipped when busy, reduced motion or a hidden tab: tween() then jumps to the end).
  const counting = signal<Suite | undefined>(undefined);
  onRunSettled(m, (from, to) => {
    if (!changed(from, to)) return;
    tween(0, 1, (k) => counting.set(k >= 1 ? undefined : { ...to, passed: lerp(from.passed, to.passed, k), failed: lerp(from.failed, to.failed, k), skipped: lerp(from.skipped, to.skipped, k) }), {
      onDone: () => counting.set(undefined),
    });
  });
  const primary = computed(() => {
    const s = m.suite();
    // the run's own progress: counts never jump back while results replace older ones
    if (m.running()) return runningLabel();
    if (m.busy().length) return `Building ${m.busy().join(", ")}…`;
    return suiteLabel(counting() ?? s);
  });
  const secondary = computed(() => {
    const s = m.suite();
    if (m.running()) return runningSecondary(m, s, $.pendingRun());
    if (m.busy().length) return s.total ? suiteLabel(s) : "";
    const viewing = $.viewing();
    if (viewing) return `Run of ${formatRunTime(viewing.run.startedAt)} (snapshot)`;
    if (m.offline()) return "Offline — last known results";
    const notMtp = $.projects().filter((p) => p.testPlatform);
    if (s.total === 0 && notMtp.length) return notOnMtpText(notMtp);
    if (s.outdatedProjects.length && s.outdated)
      return `Outdated: ${outdatedWhat(s.outdatedProjects)} failed to build`;
    if (s.stale && s.stale >= s.passed + s.failed + s.skipped) return "Results from a previous session";
    const last = $.lastRun();
    if (!last) return s.stale ? `${plural(s.stale, "result")} from a previous session` : "No runs yet";
    return lastRunText(last, s.total);
  });
  const secondaryTitle = computed(() => {
    const s = m.suite();
    const quarantined = s.quarantined ? `, ${s.quarantined} quarantined (failing)` : "";
    const inRun = s.inRun ? `, ${s.inRun} in the current run` : "";
    const lines = [`${s.passed} passed, ${s.failed} failed, ${s.skipped} skipped, ${s.notRun} not run${quarantined}${inRun} — ${plural(s.total, "test")}`];
    if (s.outdated) lines.push(`${s.outdated} results are outdated: ${s.outdatedProjects.map((p) => p.name).join(", ")} failed to build since.`);
    if (s.stale) lines.push(`${plural(s.stale, "result")} ${s.stale === 1 ? "is" : "are"} from a previous session.`);
    const last = $.lastRun();
    if (last) {
      const notRun = last.notRun ? `, ${last.notRun} not run` : "";
      lines.push(`Last run: ${last.passed} passed, ${last.failed} failed, ${last.skipped} skipped${notRun} of ${plural(last.total, "test")}`);
    }
    if (last?.abortReason) lines.push(last.abortReason);
    for (const p of $.projects()) if (p.testPlatform && p.diagnostics?.[0]) lines.push(`Not run: ${p.diagnostics[0].message}`);
    return lines.join("\n");
  });

  return h(
    "div",
    {
      class: () =>
        cx(
          "run-summary",
          `tone-${m.tone()}`,
          m.outdated() && "is-outdated",
          m.tone() === "passed" && "all-passed",
          m.tone() === "passed" && celebrating() && "celebrate",
        ),
      title: secondaryTitle,
    },
    h(
      "span",
      { class: "summary-status" },
      () => (m.tone() === "outdated" ? icon("warning", { size: 16, class: "st st-outdated" }) : statusIcon(icn)),
      h("span", { class: "summary-label lc-tabular" }, primary),
    ),
    show(
      () => secondary() !== "",
      () => [h("span", { class: "summary-sep summary-secondary-sep" }), h("span", { class: "summary-secondary" }, secondary)],
    ),
    // the tests running now, with elapsed time and "possibly hanging" (running-now.ts)
    show(
      () => m.running() && !!runningNow(),
      () => runningNowChip(),
    ),
    show(
      () => m.elapsed() != null,
      () => [
        h("span", { class: "summary-sep" }),
        h("span", { class: "sum sum-time mono", title: "Elapsed" }, icon("timer", { size: 14 }), () => formatDuration(m.elapsed())),
      ],
    ),
  );
}

// ---- composition bar ------------------------------------------------------------------------------------------

type Band = "passed" | "failed" | "skipped";

/**
 * The suite's composition: passed | failed | skipped | in the current run (striped, shrinking as results arrive) |
 * not run (track). Layers are cumulative and scaled with transform: scaleX (compositor-only). Click a band to filter
 * the tree; the band buttons are out of the tab order (the tree's status chips do the same thing).
 */
export function suiteBar(m: SuiteModel): HTMLElement {
  const total = () => m.suite().total;
  const frac = (n: number) => (total() ? Math.min(1, n / total()) : 0);
  const bands = computed(() => {
    const s = m.suite();
    const all: [Band, number, number][] = [
      ["passed", 0, s.passed],
      ["failed", s.passed, s.failed],
      ["skipped", s.passed + s.failed, s.skipped],
    ];
    return all.filter(([, , n]) => n > 0);
  });
  const label = computed(() => {
    const s = m.suite();
    if (m.running()) {
      const p = runProgress();
      const failed = p?.failed ? `, ${p.failed} failed` : "";
      return `Run progress: ${p?.done ?? s.runDone} of ${plural(p?.total ?? s.runTotal, "test")} done${failed}`;
    }
    return `Suite: ${s.failed} failed, ${s.passed} passed, ${s.skipped} skipped, ${s.notRun} not run of ${plural(s.total, "test")}`;
  });
  // P11: red → green (the run that fixed the last failure) gets one sheen across the bar once the layers settled.
  // Green → green (every watch save) gets nothing. Decorative: not when busy / reduced.
  let track!: HTMLElement;
  onRunSettled(m, (from, to) => {
    if (from.failed > 0 && to.failed === 0 && to.passed > 0) window.setTimeout(() => track?.isConnected && replayClass(track, "lc-sheen", 600), 250);
  });
  return h(
    "div",
    { class: () => cx("run-progress", m.running() && "live", total() === 0 && "empty", m.outdated() && "outdated") },
    h(
      "div",
      {
        ref: (el: HTMLDivElement) => (track = el),
        class: "rp-track",
        role: "progressbar",
        "aria-label": label,
        "aria-valuemin": 0,
        "aria-valuemax": () => (m.running() ? m.suite().runTotal : total()),
        "aria-valuenow": () => (m.running() ? m.suite().runDone : m.suite().passed + m.suite().failed + m.suite().skipped),
      },
      h("span", { class: "layer inrun", style: { transform: () => `scaleX(${frac(m.suite().passed + m.suite().failed + m.suite().skipped + m.suite().inRun)})` } }),
      h("span", { class: "layer skipped", style: { transform: () => `scaleX(${frac(m.suite().passed + m.suite().failed + m.suite().skipped)})` } }),
      h("span", { class: "layer failed", style: { transform: () => `scaleX(${frac(m.suite().passed + m.suite().failed)})` } }),
      h("span", { class: "layer passed", style: { transform: () => `scaleX(${frac(m.suite().passed)})` } }),
      show(
        () => m.busy().length > 0 && !m.running(),
        () => h("span", { class: "indeterminate" }),
      ),
    ),
    list(
      bands,
      ([f]) => f,
      (band) => {
        const f = band.peek()[0];
        return h("button", {
          class: `band-hit ${f}`,
          tabIndex: -1,
          style: { left: () => `${frac(band()[1]) * 100}%`, width: () => `${frac(band()[2]) * 100}%` },
          title: () => `Show ${band()[2]} ${f} test${band()[2] === 1 ? "" : "s"}`,
          "aria-label": () => `Show ${band()[2]} ${f} tests`,
          onclick: () => requestStatusFilter(f),
        });
      },
    ),
  );
}

// ---- announcer (P1-6) -----------------------------------------------------------------------------------------

/** "Run finished: 1 failed, 24 passed of 25 tests in 3.2 s." (+ the abort reason). */
function finishedAnnouncement(last: RunSummary): string {
  const parts = [last.failed && `${last.failed} failed`, `${last.passed} passed`, last.skipped && `${last.skipped} skipped`, last.notRun && `${last.notRun} not run`].filter(Boolean).join(", ");
  let how = "Run finished";
  if (last.aborted) how = "Run aborted";
  else if (last.cancelled) how = "Run cancelled";
  const took = last.durationMs == null ? "" : ` in ${formatDuration(last.durationMs)}`;
  const reason = last.aborted && last.abortReason ? ` ${last.abortReason}` : "";
  return `${how}: ${parts} of ${plural(last.total, "test")}${took}.${reason}`;
}

/** A visually hidden polite live region that only speaks "Run started" / "Run finished: …". */
export function runAnnouncer(): HTMLElement {
  const $ = getStore().$;
  const text = signal("");
  let lastStarted: string | undefined;
  let lastFinished: string | undefined;
  effect(() => {
    const cur = $.currentRun();
    const last = $.lastRun();
    untracked(() => {
      if (cur && cur.runId !== lastStarted) {
        lastStarted = cur.runId;
        text.set(`Run started: ${plural(cur.total, "test")}${cur.trigger === "watch" ? " (watch)" : ""}.`);
      } else if (!cur && last && last.runId !== lastFinished && last.runId === lastStarted) {
        lastFinished = last.runId;
        text.set(finishedAnnouncement(last));
      }
    });
  });
  return h("div", { class: "visually-hidden", role: "status", "aria-live": "polite", "aria-atomic": "true" }, text);
}

// ---- document title + favicon (P2-1) --------------------------------------------------------------------------

const DOT: Partial<Record<Tone, string>> = { failed: "#d13438", passed: "#2e9e44", running: "#1f7ae0", building: "#1f7ae0", outdated: "#e0a100" };
const GLYPH: Partial<Record<Tone, string>> = { failed: "✕", passed: "✓", running: "▶", building: "▶", outdated: "⚠" };

/** The document title's status text per tone: "Running 9/25", "1 failed", "25 passed", … */
function toneTitle(t: Tone, s: Suite): string {
  switch (t) {
    case "running":
      return runningLabel();
    case "building":
      return "Building…";
    case "failed":
      return `${s.failed} failed`;
    case "passed":
      return `${s.passed} passed`;
    case "outdated":
      return suiteLabel(s).split(" · ")[0];
    default:
      return `${s.total} tests`;
  }
}

/** Keeps document.title ("✕ 1 failed · tests-failing") and the favicon (logo tile + status dot) in sync. */
export function followDocumentStatus(m: SuiteModel) {
  const $ = getStore().$;
  const title = computed(() => {
    const ws = $.workspaceName() ?? "Labcoat";
    const s = m.suite();
    const t = m.tone();
    if (t === "empty" || !$.loaded()) return ws === "Labcoat" ? "Labcoat" : `${ws} · Labcoat`;
    const glyph = GLYPH[t] ? `${GLYPH[t]} ` : "";
    return `${glyph}${toneTitle(t, s)} · ${ws}`;
  });
  // Setting document.title is not free (the browser re-renders the tab strip): at most ~2× per second while counts stream.
  let titleTimer: number | undefined;
  let lastTitleAt = 0;
  effect(() => {
    title(); // subscribes: re-run on every title change (write() reads the latest with peek)
    const write = () => {
      titleTimer = undefined;
      lastTitleAt = performance.now();
      document.title = title.peek();
    };
    const wait = 500 - (performance.now() - lastTitleAt);
    if (wait <= 0 || !m.running.peek()) {
      window.clearTimeout(titleTimer);
      write();
    } else titleTimer ??= window.setTimeout(write, wait);
  });
  onCleanup(() => window.clearTimeout(titleTimer));
  onCleanup(() => (document.title = "Labcoat"));

  // The mark comes from /favicon.svg (24×24, transparent; fetched once); the status dot is drawn at the bottom-right.
  const base = signal<string | undefined>(undefined);
  fetch(new URL("./favicon.svg", location.href))
    .then((r) => (r.ok ? r.text() : undefined))
    .then((t) => t && t.includes("</svg>") && base.set(t))
    .catch(() => {});
  const link = (document.querySelector('link[rel="icon"]') as HTMLLinkElement | null) ?? document.head.appendChild(Object.assign(document.createElement("link"), { rel: "icon" }));
  const original = link.href;
  effect(() => {
    const svg = base();
    if (!svg) return;
    const color = DOT[m.tone()];
    const out = color ? svg.replace(/<\/svg>\s*$/, `<circle cx="19" cy="19" r="4.6" fill="${color}" stroke="#fff" stroke-width="1.4"/></svg>`) : svg;
    link.type = "image/svg+xml";
    link.href = `data:image/svg+xml,${encodeURIComponent(out)}`;
  });
  onCleanup(() => (link.href = original));
}
