/**
 * <lc-coverage-line-tests>: "Which tests cover this line?" (feature F1). Opened from a covered line's hit count in a
 * coverage source view; lists the tests known to execute that line — from the per-test coverage cache (exact) and
 * the smart-watch impact map (class-granular, marked approximate) — with run buttons and "select test".
 *
 *   GET /api/coverage/line?path=<abs>&line=<n>  ->  CoveringTests (see below; docs/contract.md "Round 3 additions")
 *
 * The popup itself (placement, Escape / outside click, focus return) is the design system's popover(); this element is
 * its content.
 */
import { computed, cx, define, h, icon, LabcoatElement, list, onCleanup, show, signal, statusIcon, kbd, type Child, type ReadSignal } from "@eriklieben/labcoat-design-system";
import type { CoveringTest, CoveringTests, TestNode } from "../contract";
import { getStore, testKey } from "../store";
import { fileName } from "../util";
import "./coverage.css";

type LineTransport = { fetchCoverageLine?: (path: string, line: number, signal?: AbortSignal) => Promise<CoveringTests> };

export async function fetchCoveringTests(path: string, line: number, signal?: AbortSignal): Promise<CoveringTests> {
  const t = getStore().transport as unknown as LineTransport;
  if (t.fetchCoverageLine) return t.fetchCoverageLine(path, line, signal);
  const q = new URLSearchParams({ path, line: String(line) });
  const res = await fetch(new URL(`./api/coverage/line?${q}`, location.href), { signal });
  if (!res.ok) throw Object.assign(new Error(`${res.status} ${res.statusText || ""}`.trim()), { status: res.status });
  return (await res.json()) as CoveringTests;
}

// ---- "select this test" request (the shell selects the tree node and shows its details) ------------------------

const SELECT_TEST_EVENT = "labcoat:select-test";

export interface SelectTestRequest {
  /** tree node key, `t:${projectId}|${id}` */
  key: string;
  projectId: string;
  id: string;
}

/**
 * Select a test in the tree and show it. Dispatches a cancelable request (a shell listener that handles it calls
 * preventDefault(), e.g. to also switch to the details tab); unhandled, it falls back to the shell's deep link
 * (`#/tests?t=<projectId>|<id>`, handled on hashchange).
 */
export function requestSelectTest(t: { projectId: string; id: string }) {
  const e = new CustomEvent<SelectTestRequest>(SELECT_TEST_EVENT, { detail: { key: `t:${testKey(t)}`, projectId: t.projectId, id: t.id }, cancelable: true });
  if (window.dispatchEvent(e)) location.hash = `#/tests?t=${encodeURIComponent(t.projectId)}|${encodeURIComponent(t.id)}`;
}

/** Select any tree node (e.g. a data-driven method `m:…`) the same way; `t` is one of its tests (hash fallback). */
export function requestSelectNode(key: string, t: { projectId: string; id: string }) {
  const e = new CustomEvent<SelectTestRequest>(SELECT_TEST_EVENT, { detail: { key, projectId: t.projectId, id: t.id }, cancelable: true });
  if (window.dispatchEvent(e)) location.hash = `#/tests?t=${encodeURIComponent(t.projectId)}|${encodeURIComponent(t.id)}`;
}

/** Listen for select-test requests (register the returned disposer with onCleanup); handling one cancels the hash fallback. */
export function onSelectTestRequest(handler: (r: SelectTestRequest) => void): () => void {
  const listener = (e: Event) => {
    e.preventDefault();
    handler((e as CustomEvent<SelectTestRequest>).detail);
  };
  window.addEventListener(SELECT_TEST_EVENT, listener);
  return () => window.removeEventListener(SELECT_TEST_EVENT, listener);
}

// ---- element ----------------------------------------------------------------------------------------------------

export interface CoverageLineTestsProps {
  path: string;
  line: number;
  /** hits of the line in the shown report */
  hits: number | undefined;
  onClose: () => void;
}
export interface CoverageLineTests extends CoverageLineTestsProps {}

type Row = CoveringTest & { test?: TestNode };

/** Exact (per-test) coverage sorts before approximate (impact map). */
const sourceRank = (source: "perTest" | "impactMap") => (source === "perTest" ? 0 : 1);

export class CoverageLineTests extends LabcoatElement<CoverageLineTestsProps> {
  static defaults: CoverageLineTestsProps = { path: "", line: 0, hits: undefined, onClose: () => {} };
  static hostClass = "cov-lt";

  protected render(): Child {
    const store = getStore();
    const $ = store.$;
    const path = this.props.path.peek();
    const line = this.props.line.peek();

    const state = signal<{ status: "loading" } | { status: "error"; message: string } | { status: "ready"; data: CoveringTests }>({ status: "loading" });
    const ac = new AbortController();
    onCleanup(() => ac.abort());
    fetchCoveringTests(path, line, ac.signal).then(
      (data) => state.set({ status: "ready", data }),
      (e: unknown) => {
        if (!ac.signal.aborted) state.set({ status: "error", message: e instanceof Error ? e.message : String(e) });
      },
    );

    const rows = computed<Row[]>(() => {
      const s = state();
      if (s.status !== "ready") return [];
      const tests = $.tests();
      // exact (per-test) first, then approximate; keep the server's order within each group
      return [...s.data.tests]
        .sort((a, b) => sourceRank(a.source) - sourceRank(b.source))
        .map((t) => ({ ...t, test: tests.get(testKey(t)) }));
    });
    const anyApprox = computed(() => rows().some((r) => r.source === "impactMap"));
    const running = computed(() => !!$.currentRun() || $.conn() !== "open");

    const close = () => this.onClose();
    const run = (ids: { projectId: string; id: string }[]) => {
      if (!ids.length) return;
      const pids = new Set(ids.map((x) => x.projectId));
      store.send({ type: "run", projectId: pids.size === 1 ? ids[0].projectId : undefined, testIds: ids.map((x) => x.id) });
    };

    const row = (r: ReadSignal<Row>) =>
      h(
        "li",
        { class: () => cx("cov-lt-row", r().source === "impactMap" && "approx") },
        () => statusIcon(r().test?.outcome ?? "notRun", 14),
        h(
          "div",
          { class: "cov-lt-name" },
          h("span", { class: "cov-lt-title", title: () => r().test?.displayName ?? r().id }, () => r().test?.displayName ?? r().id),
          h("span", { class: "cov-lt-ctx dim" }, () => {
            const t = r().test;
            return [t?.namespace, t?.className].filter(Boolean).join(".") || r().projectId;
          }),
        ),
        () => {
          const x = r();
          if (x.source === "impactMap")
            return h(
              "span",
              { class: "cov-lt-approx", title: "Approximate (impact map): this test ran a method that contains the line; it may not execute the line itself" },
              "≈ approx.",
            );
          return [
            x.stale
              ? h("span", { class: "cov-lt-stale", title: "Collected before the last rebuild: may be out of date" }, icon("warning", { size: 12 }), "stale")
              : null,
            x.hits != null ? h("span", { class: "cov-lt-hits mono", title: `${x.hits} hit${x.hits === 1 ? "" : "s"} in this test's own coverage` }, `${x.hits}×`) : null,
          ];
        },
        h(
          "button",
          {
            class: "icon-btn small",
            title: "Select this test",
            "aria-label": () => `Select ${r().test?.displayName ?? r().id}`,
            disabled: () => !r().test,
            onclick: () => {
              requestSelectTest(r.peek());
              close();
            },
          },
          icon("open", { size: 14 }),
        ),
        h(
          "button",
          {
            class: "icon-btn small cov-lt-run",
            title: "Run this test",
            "aria-label": () => `Run ${r().test?.displayName ?? r().id}`,
            disabled: () => running() || !r().test,
            onclick: () => run([r.peek()]),
          },
          icon("play", { size: 14 }),
        ),
      );

    return [
      h(
        "div",
        { class: "cov-lt-head" },
        h(
          "div",
          { class: "cov-lt-heading" },
          h("span", { class: "cov-lt-heading-title" }, "Tests covering line ", h("b", null, String(line))),
          h(
            "span",
            { class: "cov-lt-sub dim" },
            fileName(path),
            () => {
              const n = this.props.hits();
              if (!n) return "";
              return ` · ${n.toLocaleString()} hit${n === 1 ? "" : "s"} in this report`;
            },
          ),
        ),
        h("button", { class: "icon-btn small cov-lt-close", title: "Close (Esc)", "aria-label": "Close", onclick: close }, icon("dismiss", { size: 14 })),
      ),
      () => {
        const s = state();
        if (s.status === "loading") return h("div", { class: "cov-lt-msg" }, h("span", { class: "spinner" }), " Looking up tests…");
        if (s.status === "error") return h("div", { class: "cov-lt-msg" }, icon("warning", { size: 14 }), ` Couldn't look up the tests (${s.message}).`);
        return null;
      },
      show(
        () => state().status === "ready" && rows().length === 0,
        () =>
          h(
            "div",
            { class: "cov-lt-msg cov-lt-empty" },
            "No test is known to cover this line yet. Collect per-test coverage (",
            kbd("C"),
            " on a test) or build the impact map to fill this list.",
          ),
      ),
      show(
        () => rows().length > 0,
        () => [
          h("ul", { class: "cov-lt-list" }, list(rows, (r) => `${r.projectId}|${r.id}`, (r) => row(r))),
          h(
            "div",
            { class: "cov-lt-foot" },
            h(
              "span",
              { class: "cov-lt-note dim" },
              () =>
                [
                  anyApprox() ? "≈ approx. = from the impact map, not the test's own coverage." : "",
                  (state() as { data?: CoveringTests }).data?.partial ? "Only tests with collected coverage are listed." : "",
                ]
                  .filter(Boolean)
                  .join(" "),
            ),
            h(
              "button",
              {
                class: "btn btn-small",
                disabled: () => running() || !rows().some((r) => r.test),
                title: "Run every listed test",
                onclick: () => run(rows.peek().filter((r) => r.test)),
              },
              icon("play", { size: 14 }),
              () => {
                const n = rows().filter((r) => r.test).length;
                return n === 1 ? " Run test" : ` Run all ${n}`;
              },
            ),
          ),
        ],
      ),
    ];
  }
}
define("lc-coverage-line-tests", CoverageLineTests);

declare global {
  interface HTMLElementTagNameMap {
    "lc-coverage-line-tests": CoverageLineTests;
  }
}
