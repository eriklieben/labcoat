/**
 * <lc-run-overview>: the right pane when nothing is selected (P1-2). Instead of "No test selected" after a run:
 * the suite's state, new failures (failing now, not failing in the test's previous result), the other failures, and
 * the slowest tests. The first new failure (else the first failure) is the "next" one: F8 / Enter opens it.
 *
 * Failure navigation (F8 / Shift+F8) is shared with the shell: `requestFailureNav(1 | -1)` asks the detail pane to
 * select the next / previous failure (tree order, wrapping); `nextFailureKey()` is the pure helper behind it.
 */
import { animateEnter, button, computed, cx, define, effect, icon, onCleanup, signal, untracked, emptyState, h, kbd, LabcoatElement, list, show, statusIcon, type Child, type ReadSignal } from "@eriklieben/labcoat-design-system";
import type { ClientCommand, RunSummary, TestNode } from "../contract";
import { getStore, testKey } from "../store";
import { bucket, type TreeNode } from "../tree";
import { formatDuration, formatRunTime, runCounts } from "../util";
import { isNewFailure } from "./run-diff"; // RUNS: same "new failure" rule as the run diff
import { runningLabel } from "./suite-status";
import "./run-overview.css";

const isFailed = (o: TestNode["outcome"]) => o === "failed" || o === "timedOut";

// ---- failure navigation -------------------------------------------------------------------------------------------

const NAV_EVENT = "labcoat:failure-nav";

/** Ask the detail pane to select the next (1) or previous (-1) failing test. For SHELL's F8 / Shift+F8 shortcuts. */
export function requestFailureNav(dir: 1 | -1) {
  window.dispatchEvent(new CustomEvent<1 | -1>(NAV_EVENT, { detail: dir }));
}

export function onFailureNavRequest(handler: (dir: 1 | -1) => void): () => void {
  const l = (e: Event) => handler((e as CustomEvent<1 | -1>).detail);
  window.addEventListener(NAV_EVENT, l);
  return () => window.removeEventListener(NAV_EVENT, l);
}

function failingLeaves(roots: TreeNode[]): TreeNode[] {
  const out: TreeNode[] = [];
  const walk = (n: TreeNode) => {
    if (n.test) {
      if (isFailed(n.test.outcome)) out.push(n);
      return;
    }
    if (n.counts.failed) n.children.forEach(walk);
  };
  roots.forEach(walk);
  return out;
}

/**
 * Key of the next / previous failing test in tree order (wrapping). From nothing (or a group), F8 starts at the first
 * NEW failure when there is one.
 */
export function nextFailureKey(roots: TreeNode[], currentKey: string | undefined, dir: 1 | -1): string | undefined {
  const fails = failingLeaves(roots);
  if (!fails.length) return undefined;
  const i = currentKey ? fails.findIndex((n) => n.key === currentKey) : -1;
  if (i < 0) {
    const running = getStore().get().currentRun;
    const firstNew = fails.find((n) => isNewFailure(n.test!, running));
    return (dir === 1 ? (firstNew ?? fails[0]) : fails.at(-1)!).key;
  }
  return fails[(i + dir + fails.length) % fails.length].key;
}

// ---- component -----------------------------------------------------------------------------------------------------

export interface RunOverviewProps {
  onSelect: (key: string) => void;
}
export interface RunOverview extends RunOverviewProps {}

export class RunOverview extends LabcoatElement<RunOverviewProps> {
  static defaults: RunOverviewProps = { onSelect: () => {} };

  protected render(): Child {
    this.style.display = "contents";
    const store = getStore();
    const $ = store.$;
    // the suite overview as soon as there are tests (also on first open, before any run of this session): never a blank
    // "No test selected" next to a red tree
    const has = computed(() => $.tests().size > 0);
    return show(has, () => this.#overview(), () => noSelection());
  }

  #overview(): Child {
    const store = getStore();
    const $ = store.$;
    const select = (t: TestNode) => this.props.onSelect.peek()(`t:${testKey(t)}`);
    const live = computed(() => !!$.currentRun());
    const run = computed(() => $.currentRun() ?? $.lastRun());
    const quarantined = computed(() => new Set($.quarantined() ?? []));
    // Perf (2000-test runs): during a run the store publishes every frame; the overview's lists and counts follow at
    // most twice per second (a trailing snapshot), and immediately once the run is over.
    const snap = signal($.tests.peek());
    let snapTimer = 0;
    effect(() => {
      const t = $.tests();
      const isLive = !!$.currentRun();
      untracked(() => {
        if (!isLive) {
          window.clearTimeout(snapTimer);
          snapTimer = 0;
          snap.set(t);
        } else if (!snapTimer)
          snapTimer = window.setTimeout(() => {
            snapTimer = 0;
            snap.set($.tests.peek());
          }, 500);
      });
    });
    onCleanup(() => window.clearTimeout(snapTimer));
    // Perf (20k tests): iterate the Map, no copy into an array per snapshot; a test key only when something is quarantined
    const tests = () => snap().values(); // a fresh iterator per use (tracked through snap())
    const isQuarantined = (t: TestNode) => quarantined().size > 0 && quarantined().has(testKey(t));
    const failing = computed(() => {
      const out: TestNode[] = [];
      for (const t of tests()) if (isFailed(t.outcome) && !isQuarantined(t)) out.push(t);
      return out;
    });
    const newFailures = computed(() => failing().filter((t) => isNewFailure(t, $.currentRun())));
    const otherFailures = computed(() => {
      const n = new Set(newFailures());
      return failing().filter((t) => !n.has(t));
    });
    const next = computed(() => newFailures()[0] ?? failing()[0]);
    // top 5 by duration: a linear pass (no full sort); frozen while a run is in flight
    let lastSlowest: TestNode[] = [];
    const slowest = computed(() => {
      if (live() && lastSlowest.length) return lastSlowest;
      const top: TestNode[] = [];
      for (const t of tests()) {
        const d = t.durationMs ?? 0;
        if (d <= 0 || t.stale) continue;
        if (top.length < 5 || d > (top.at(-1)!.durationMs ?? 0)) {
          top.push(t);
          top.sort((a, b) => (b.durationMs ?? 0) - (a.durationMs ?? 0));
          if (top.length > 5) top.pop();
        }
      }
      lastSlowest = top;
      return top;
    });
    const counts = computed(() => {
      const c = { total: 0, passed: 0, failed: 0, skipped: 0, notRun: 0 };
      for (const t of tests()) {
        if (isQuarantined(t)) continue;
        c.total++;
        const b = bucket(t.outcome);
        if (b === "passed" || b === "failed" || b === "skipped") c[b]++;
        else c.notRun++;
      }
      return c;
    });

    const row = (t: ReadSignal<TestNode>, opts: { msg?: boolean; markNext?: boolean }) =>
      h(
        "li",
        null,
        h(
          "button",
          {
            class: () => cx("failure-item", opts.markNext && next() === t() && "is-next"),
            onclick: () => select(t.peek()),
            "aria-describedby": () => (opts.markNext && next() === t() ? "run-overview-next" : null),
          },
          statusIcon(() => t().outcome),
          h(
            "span",
            { class: "failure-text" },
            h("span", { class: "failure-name" }, () => (t().className ? h("span", { class: "dim" }, `${t().className}.`) : null), () => t().displayName),
            opts.msg ? () => (t().errorMessage ? h("span", { class: "failure-msg" }, t().errorMessage!.split("\n")[0]) : null) : null,
          ),
          () => (opts.markNext && next() === t() ? h("span", { class: "next-hint", id: "run-overview-next" }, kbd("F8")) : null),
          h("span", { class: "row-duration" }, () => formatDuration(t().durationMs)),
        ),
      );

    // P17: failures that arrive while the overview is shown slide in (lc-row-enter, 15 ms stagger); not on first render,
    // not when more than 5 arrive at once, not under html.lc-busy.
    let ready = false;
    queueMicrotask(() => (ready = true));
    let batch: HTMLElement[] = [];
    const entering = (el: HTMLElement) => {
      if (!ready) return el;
      if (!batch.length)
        queueMicrotask(() => {
          const b = batch;
          batch = [];
          if (b.length <= 5) b.forEach((x, i) => animateEnter(x, "row", { index: i }));
        });
      batch.push(el);
      return el;
    };
    const block = (title: () => string, items: ReadSignal<TestNode[]>, opts: { msg?: boolean; markNext?: boolean; tone?: string }) =>
      show(
        () => items().length > 0,
        () =>
          h(
            "section",
            { class: cx("detail-section", "overview-section", opts.tone && `tone-${opts.tone}`) },
            h("div", { class: "section-header" }, h("h3", { class: "overview-h" }, title)),
            h("ul", { class: "failure-list" }, list(items, (t) => testKey(t), (t) => entering(row(t, opts)))),
          ),
      );

    const headline = () => {
      const c = counts();
      // the toolbar's wording (suite-status.ts): the run's own progress, counts never jump back
      if (live()) return runningLabel();
      if (c.failed) return `${c.failed} failed · ${c.total} tests`;
      if (c.notRun === c.total) return `${c.total} tests, none run yet`;
      return c.notRun ? `${c.passed + c.skipped} of ${c.total} passed` : `All ${c.total} passed`;
    };

    return h(
      "div",
      { class: "detail-scroll run-overview" },
      h(
        "header",
        { class: "detail-header" },
        h("div", { class: "kind-label" }, () => (live() ? "Run in progress" : "Test suite")),
        h(
          "div",
          { class: "detail-title-row" },
          statusIcon(() => {
            if (live()) return "running";
            if (counts().failed) return "failed";
            return counts().passed ? "passed" : "notRun";
          }, 20),
          h("h2", { class: "detail-title", tabIndex: -1 }, headline),
        ),
        h("div", { class: "overview-run" }, () => {
          const r = run();
          if (!r) {
            let stale = 0;
            for (const t of tests()) if (t.stale) stale++;
            return stale ? `No run in this session yet · ${stale} results from a previous session` : "No run in this session yet";
          }
          return runLine(r, live());
        }),
        h(
          "div",
          { class: "detail-actions" },
          () => {
            if (failing().length)
              return button(
                {
                  variant: "accent",
                  icon: "arrowClockwise",
                  // soft disabled (stays focusable during the run it starts)
                  "aria-disabled": () => live() || $.conn() !== "open",
                  onclick: () => !(live() || $.conn() !== "open") && runTests(failing.peek()),
                  title: "Run the failed tests again",
                },
                ` Run failed (${failing().length})`,
              );
            if (counts().notRun === counts().total)
              return button({ variant: "accent", icon: "playFilled", disabled: () => live() || $.conn() !== "open", onclick: () => getStore().send({ type: "run" }) }, " Run all");
            return null;
          },
          () => {
            if (!next()) return null;
            return button({ icon: "chevronRight", onclick: () => select(next.peek()!) }, " Open first ", newFailures().length ? "new failure" : "failure");
          },
        ),
      ),
      // Round 4: a test host that exited mid-run, or a cancelled run, says so (and how many tests got no result)
      () => {
        const r = run();
        if (live() || !r || !(r.aborted || r.cancelled)) return null;
        return h(
          "div",
          { class: cx("messagebar", r.aborted ? "mb-error" : "mb-warning"), role: "group", "aria-label": r.aborted ? "Run aborted" : "Run cancelled" },
          icon(r.aborted ? "errorCircle" : "warning", { size: 18, class: "mb-icon" }),
          h(
            "div",
            { class: "mb-body" },
            h("div", { class: "mb-title" }, r.aborted ? "Run aborted" : "Run cancelled"),
            h("div", null, r.abortReason ?? (r.notRun ? `${r.notRun} ${testsWord(r.notRun)} did not run.` : "Not every test reported a result.")),
          ),
        );
      },
      distBar(counts),
      block(() => `New failures (${newFailures().length})`, newFailures, { msg: true, markNext: true, tone: "new" }),
      block(() => (newFailures().length ? `Still failing (${otherFailures().length})` : `Failures (${otherFailures().length})`), otherFailures, { msg: true, markNext: true }),
      block(() => "Slowest tests", slowest, {}),
      () =>
        failing().length
          ? h("div", { class: "overview-hint" }, kbd("F8"), " / ", kbd("Shift+F8"), " next / previous failure · select a test in the tree for its details")
          : h("div", { class: "overview-hint" }, "Select a test in the tree to see its result, error, stack trace and output."),
    );
  }
}
define("lc-run-overview", RunOverview);

const testsWord = (n: number) => (n === 1 ? "test" : "tests");

/** "Last run 10:42 · 3.2 s · 4 selected tests · 3 passed, 1 failed" (while live: when it started, no counts). */
function runLine(r: RunSummary, live: boolean): string {
  const parts = [
    // "Last run" = when it finished (the toolbar says the same)
    live ? `Started ${formatRunTime(r.startedAt)}` : `Last run ${formatRunTime(r.finishedAt ?? r.startedAt)}`,
    r.durationMs != null && !live ? formatDuration(r.durationMs) : "",
    r.scope === "subset" ? `${r.total} selected ${testsWord(r.total)}` : "",
    r.repeat ? `${r.repeat.count}× repeat` : "",
    live ? "" : runCounts(r),
    r.quarantined ? `${r.quarantined} quarantined` : "",
  ];
  return parts.filter(Boolean).join(" · ");
}

declare global {
  interface HTMLElementTagNameMap {
    "lc-run-overview": RunOverview;
  }
}

function runTests(list: TestNode[]) {
  if (!list.length) return;
  const pids = new Set(list.map((t) => t.projectId));
  getStore().send({ type: "run", projectId: pids.size === 1 ? list[0].projectId : undefined, testIds: list.map((t) => t.id) } satisfies ClientCommand);
}

/**
 * Passed / failed / skipped distribution bar (`.dist-bar > .seg.<outcome>`). Segments are full-width layers placed
 * with `translateX(start%) scaleX(share)`: updates during a run are compositor-only (P10).
 */
export function distBar(counts: () => { total: number; passed: number; failed: number; skipped: number }): HTMLElement {
  const share = (n: number) => (counts().total ? n / counts().total : 0);
  const seg = (cls: string, start: () => number, w: () => number) =>
    h("span", { class: `seg ${cls}`, style: { transform: () => `translateX(${(start() * 100).toFixed(3)}%) scaleX(${w().toFixed(4)})` } });
  return h(
    "div",
    { class: "dist-bar", title: () => `${counts().passed + counts().failed + counts().skipped} of ${counts().total} have results` },
    seg("passed", () => 0, () => share(counts().passed)),
    seg("failed", () => share(counts().passed), () => share(counts().failed)),
    seg("skipped", () => share(counts().passed) + share(counts().failed), () => share(counts().skipped)),
  );
}

/** "No test selected": nothing ran yet. */
export function noSelection(): HTMLElement {
  return emptyState({
    icon: "beaker",
    title: "No test selected",
    body: ["Select a test in the tree to see its result, error, stack trace and output. Use ", kbd("ArrowUp"), " ", kbd("ArrowDown"), " to move and ", kbd("R"), " to run."],
  });
}
