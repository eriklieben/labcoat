/**
 * "Slowest tests" of a project: the tests with the longest latest duration, each with a sparkline of its recent
 * durations and how the latest compares to the median of the earlier runs (GET /api/tests/slowest).
 */
import { computed, cx, effect, h, list, onCleanup, show, signal, statusIcon, type Child, type ReadSignal } from "@eriklieben/labcoat-design-system";
import type { SlowestTestsResponse, SlowTest } from "../contract";
import { testKey } from "../store";
import { fetchSlowestTests, trendEntries, trendLabel } from "../slowest";
import { formatDuration } from "../util";
import { sparkline } from "./history-strip";
import "./history-strip.css";
import "./slowest-tests.css";

/** The sparkline of a test's recent durations (the strip's SVG, written as markup like there). */
function spark(t: ReadSignal<SlowTest>): Child {
  const host = h("span", { class: "slowest-spark", role: "img" });
  effect(() => {
    host.setAttribute("aria-label", `Duration trend of ${t().name}`);
    host.innerHTML = sparkline(trendEntries(t()), undefined);
  });
  return host;
}

/**
 * @param projectId the project whose tests are listed
 * @param refresh changes when the results changed (the list is fetched again)
 * @param onSelect opens a test in the detail pane
 */
export function slowestTrendList(projectId: ReadSignal<string>, refresh: ReadSignal<unknown>, onSelect: (t: SlowTest) => void, limit = 10): HTMLElement {
  const data = signal<SlowestTestsResponse | undefined>(undefined);
  const failed = signal(false);
  effect(() => {
    const id = projectId();
    refresh();
    const ctl = new AbortController();
    onCleanup(() => ctl.abort());
    fetchSlowestTests(id, limit, ctl.signal).then(
      (r) => {
        data.set(r);
        failed.set(false);
      },
      (e) => {
        if ((e as Error).name !== "AbortError") failed.set(true);
      },
    );
  });
  const tests = computed(() => data()?.tests ?? []);
  return h(
    "div",
    { class: "slowest-trend" },
    show(
      () => failed() && !data(),
      () => h("div", { class: "detail-note" }, "The slowest tests could not be loaded."),
    ),
    h(
      "ul",
      { class: "failure-list slowest-list" },
      list(
        tests,
        (t) => testKey(t),
        (t) =>
          h(
            "li",
            null,
            h(
              "button",
              { class: "failure-item slowest-item", onclick: () => onSelect(t.peek()) },
              statusIcon(() => t().trend.at(-1)?.outcome ?? "passed"),
              h(
                "span",
                { class: "failure-text" },
                h("span", { class: "failure-name" }, () => (t().className ? h("span", { class: "dim" }, `${t().className}.`) : null), () => t().name),
                h("span", { class: () => cx("slowest-factor", (t().factor ?? 0) >= 2 && "is-slow") }, () => trendLabel(t())),
              ),
              spark(t),
              h("span", { class: "row-duration" }, () => formatDuration(t().durationMs)),
            ),
          ),
      ),
    ),
    show(
      () => (data()?.total ?? 0) > tests().length,
      () => h("div", { class: "slowest-more" }, () => `${data()!.total - tests().length} more tests with a duration`),
    ),
  );
}
