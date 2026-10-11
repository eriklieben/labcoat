/**
 * <lc-watch-strip> (P1-8): one line under the toolbar while watch mode is on, telling what the watch loop did:
 *
 *   Orders.cs saved → build 1.2 s → 4 affected → 1 fixed, 0 new failures
 *
 * From the latest watch-triggered run (RunSummary.watch / .affected) and the history diff of its tests (each test's
 * history entry for that run vs the entry before it). While the host rebuilds (projectStatus "building", before any
 * runStarted) it says so.
 */
import { animateEnter, computed, cx, define, flash, h, icon, LabcoatElement, show, type Child } from "@eriklieben/labcoat-design-system";
import type { RunSummary, TestNode } from "../contract";
import { getStore } from "../store";
import { bucket, requestStatusFilter } from "../tree";
import { formatDuration, formatRunTime } from "../util";
import { isNewFailureAt } from "./run-diff";
import "./watch-strip.css";

const fileName = (p: string) => p.split(/[\\/]/).pop() ?? p;
// RunSummary.affected with strategy "git" is a "Run affected" run (run-affected.ts), not a watch run
const isWatchRun = (r: RunSummary | undefined) => !!r && (r.trigger === "watch" || !!r.watch || (!!r.affected && r.affected.strategy !== "git"));

const STRATEGY: Record<string, string> = {
  testFile: "tests in the changed test file",
  impactMap: "tests whose covered code changed (impact map)",
  mixed: "tests in changed test files + impact map",
  all: "all tests of the project (no impact data for the changed file)",
};

export interface WatchDiff {
  fixed: TestNode[];
  newFailures: TestNode[];
  stillFailing: number;
}

/** Fixed / newly failing tests of `runId`, from each test's history (entry of that run vs the one before it). */
export function historyDiff(tests: Map<string, TestNode>, runId: string): WatchDiff {
  const out: WatchDiff = { fixed: [], newFailures: [], stillFailing: 0 };
  for (const t of tests.values()) {
    const hist = t.history ?? [];
    const i = hist.findLastIndex((e) => e.runId === runId);
    if (i < 0) continue;
    const now = bucket(hist[i].outcome);
    const prev = i > 0 ? bucket(hist[i - 1].outcome) : undefined;
    // "new" = the same rule as everywhere else (run-diff.ts isNewFailureAt): failing now, passed in the previous full run
    if (now === "failed" && isNewFailureAt(hist, i)) out.newFailures.push(t);
    else if (now === "failed") out.stillFailing++;
    else if (now === "passed" && prev === "failed") out.fixed.push(t);
  }
  return out;
}

/** "Foo.cs saved", "Foo.cs +2 saved", or "Change detected" when the host didn't say which files changed. */
function savedLabel(fs: string[]): string {
  if (!fs.length) return "Change detected";
  const more = fs.length > 1 ? ` +${fs.length - 1}` : "";
  return `${fileName(fs[0])}${more} saved`;
}

export class WatchStrip extends LabcoatElement {
  static hostClass = "watch-strip";

  protected render(): Child {
    const store = getStore();
    const $ = store.$;
    this.setAttribute("aria-label", "Watch mode");
    this.setAttribute("role", "group");

    const current = computed(() => (isWatchRun($.currentRun()) ? $.currentRun() : undefined));
    const last = computed(() => (isWatchRun($.lastRun()) ? $.lastRun() : undefined));
    const run = computed(() => current() ?? last());
    const building = computed(() => !$.currentRun() && $.projects().some((p) => p.status === "building" || p.status === "discovering"));
    const buildFailed = computed(() => $.projects().filter((p) => p.status === "error"));

    // Progress of a running watch run: only reads the run's keys.
    const done = computed(() => {
      if (!current()) return 0;
      const keys = $.runKeys();
      const pending = $.pending();
      const tests = $.tests();
      let n = 0;
      if (keys) for (const k of keys) if (!pending?.has(k) && tests.get(k)?.outcome !== "running" && tests.get(k)?.outcome !== "queued") n++;
      return n;
    });
    // The diff only once the run is finished (history arrives just before runFinished).
    const diff = computed(() => {
      const r = last();
      if (!r || current()) return undefined;
      return historyDiff($.tests(), r.runId);
    });

    const files = () => {
      const r = run()!;
      return r.watch?.changedFiles ?? r.affected?.changedFiles ?? [];
    };

    // P13: each step reveals as its event arrives (opacity + 4 px from the left, 140 ms); a new cycle crossfades the
    // strip (120 ms); "n fixed" / "n new" flash once. Steps already shown in this cycle never replay on re-renders.
    const seen = new Set<string>();
    let cycle: string | undefined;
    let ready = false;
    queueMicrotask(() => (ready = true));
    const nextCycle = (c: string) => {
      if (c === cycle) return;
      const replace = cycle !== undefined && cycle !== "build" && ready;
      cycle = c;
      seen.clear();
      if (replace) animateEnter(this, "fade");
    };
    const reveal = <E extends HTMLElement>(key: string, el: E): E => {
      if (seen.has(key)) return el;
      seen.add(key);
      return ready ? animateEnter(el, "x") : el;
    };
    const step = (key: string, cls: string, ...children: Child[]) => [
      reveal(`${key}>`, h("span", { class: "ws-arrow", "aria-hidden": "true" }, "→")),
      reveal(key, h("span", { class: cx("ws-step", cls) }, ...children)),
    ];
    const count = (key: string, el: HTMLElement, tone: "good" | "bad", n: number) => {
      if (!seen.has(key) && ready && n > 0) flash(el, tone);
      seen.add(key);
      return el;
    };

    return [
      icon("eyeFilled", { size: 16, class: "ws-icon" }),
      () => {
        // Rebuilding after a change (no runStarted yet), or the rebuild failed.
        if (building()) {
          nextCycle("build");
          return reveal("building", h("span", { class: "ws-step" }, h("span", { class: "spinner" }), " Change detected → building…"));
        }
        const r = run();
        nextCycle(r?.runId ?? "idle");
        if (!r)
          return buildFailed().length
            ? h("span", { class: "ws-step ws-bad" }, `Build failed (${buildFailed().map((p) => p.name).join(", ")}) — fix and save to re-run`)
            : h("span", { class: "ws-step ws-dim" }, "Watching for changes — save a file to re-run the affected tests");
        const fs = files();
        const strategy = r.watch?.strategy ?? r.affected?.strategy;
        return [
          reveal(
            "file",
            h(
              "span",
              { class: "ws-step ws-file", title: fs.join("\n") || undefined },
              savedLabel(fs),
            ),
          ),
          r.watch?.buildMs != null ? step("build", "", `build ${formatDuration(r.watch.buildMs)}`) : null,
          step("affected", "", h("span", { title: strategy ? `Ran ${STRATEGY[strategy] ?? strategy}` : undefined }, `${r.total} affected`)),
          () => {
            if (current()) return step("running", "ws-running", h("span", { class: "spinner" }), " running ", () => `${done()}/${r.total}`);
            const d = diff();
            if (!d) return null;
            return step(
              "result",
              "ws-result",
              count("fixed", h(
                "button",
                {
                  class: cx("ws-count", d.fixed.length > 0 && "good"),
                  title: d.fixed.map((t) => t.displayName).join("\n") || "No test went from failing to passing",
                  onclick: () => requestStatusFilter("passed"),
                  tabIndex: -1,
                },
                `${d.fixed.length} fixed`,
              ), "good", d.fixed.length),
              ", ",
              count("new", h(
                "button",
                {
                  class: cx("ws-count", d.newFailures.length > 0 && "bad"),
                  title: d.newFailures.map((t) => t.displayName).join("\n") || "No new failures",
                  onclick: () => requestStatusFilter(d.newFailures.length ? "new" : "failed"),
                  tabIndex: -1,
                },
                `${d.newFailures.length} new`,
              ), "bad", d.newFailures.length),
              d.stillFailing ? h("span", { class: "ws-dim" }, ` · ${d.stillFailing} still failing`) : null,
            );
          },
          show(
            () => !current() && !!last()?.finishedAt,
            () => h("span", { class: "ws-time ws-dim" }, () => formatRunTime(last()?.finishedAt)),
          ),
        ];
      },
    ];
  }
}
define("lc-watch-strip", WatchStrip);

declare global {
  interface HTMLElementTagNameMap {
    "lc-watch-strip": WatchStrip;
  }
}
