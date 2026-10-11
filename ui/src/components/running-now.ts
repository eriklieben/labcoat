/**
 * "Running now" in the run summary (docs/features.md "Slow and hanging tests"): the longest-running test with its
 * elapsed time, marked "possibly hanging" after the host's hang threshold; the button opens the list of the tests
 * running now (the longest ten) with their times and, when a test timeout is set, how long until it applies.
 */
import { computed, cx, h, icon, list, show, signal, type Child } from "@eriklieben/labcoat-design-system";
import { formatElapsed, runningNow, type RunningTestView } from "../running-now";
import "./running-now.css";

function testLine(t: RunningTestView, timeoutMs: number | undefined): string {
  const hang = t.slow ? " · possibly hanging" : "";
  const timeout = timeoutMs ? ` · times out after ${formatElapsed(timeoutMs)}` : "";
  return `${t.displayName}: running for ${formatElapsed(t.elapsedMs)}${hang}${timeout}`;
}

export function runningNowChip(): Child {
  const open = signal(false);
  const first = computed(() => runningNow()?.tests[0]);
  const slow = computed(() => runningNow()?.tests.some((t) => t.slow) ?? false);
  const more = computed(() => Math.max(0, (runningNow()?.count ?? 0) - 1));
  const label = computed(() => {
    const v = runningNow();
    if (!v) return "";
    return v.tests.map((t) => testLine(t, v.testTimeoutMs)).join("\n") + (v.count > v.tests.length ? `\n+${v.count - v.tests.length} more running` : "");
  });
  const close = (e: KeyboardEvent) => {
    if (e.key === "Escape" && open.peek()) {
      e.stopPropagation();
      open.set(false);
    }
  };
  return h(
    "span",
    { class: "running-now-wrap", onkeydown: close },
    h("span", { class: "summary-sep" }),
    h(
      "button",
      {
        type: "button",
        class: () => cx("running-now", slow() && "is-slow"),
        title: label,
        "aria-expanded": () => String(open()),
        "aria-label": () => `Running now: ${label()}`,
        onclick: () => open.update((v) => !v),
      },
      () => icon(slow() ? "warning" : "timer", { size: 14 }),
      h("span", { class: "rn-name" }, () => first()?.displayName ?? ""),
      h("span", { class: "rn-time mono" }, () => (first() ? formatElapsed(first()!.elapsedMs) : "")),
      show(
        () => slow(),
        () => h("span", { class: "rn-flag" }, "possibly hanging"),
      ),
      show(
        () => more() > 0,
        () => h("span", { class: "rn-more" }, () => `+${more()}`),
      ),
    ),
    show(
      () => open() && !!runningNow(),
      () =>
        h(
          "div",
          { class: "running-now-list", role: "list", "aria-label": "Tests running now" },
          list(
            computed(() => runningNow()?.tests ?? []),
            (t) => `${t.projectId}|${t.id}`,
            (t) =>
              h(
                "div",
                { class: () => cx("rn-row", t().slow && "is-slow"), role: "listitem" },
                () => icon(t().slow ? "warning" : "timer", { size: 14 }),
                h("span", { class: "rn-name" }, () => t().displayName),
                h("span", { class: "rn-project" }, () => t().projectId),
                h("span", { class: "rn-time mono" }, () => formatElapsed(t().elapsedMs)),
              ),
          ),
          show(
            () => (runningNow()?.count ?? 0) > (runningNow()?.tests.length ?? 0),
            () => h("div", { class: "rn-row rn-rest" }, () => `+${runningNow()!.count - runningNow()!.tests.length} more running`),
          ),
          show(
            () => !!runningNow()?.testTimeoutMs,
            () => h("div", { class: "rn-row rn-rest" }, () => `Tests time out after ${formatElapsed(runningNow()!.testTimeoutMs!)} (testTimeout).`),
          ),
        ),
    ),
  );
}
