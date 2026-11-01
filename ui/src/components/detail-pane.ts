/**
 * <lc-detail-pane>: the "Test details" tab (port of DetailPane.tsx).
 *
 *   - nothing selected → the run overview (<lc-run-overview>, P1-2) or the "No test selected" empty state
 *   - a test → failure first (P1-1): header, error + diff (+ Aspire failure context), one meta line with the history
 *     strip, repeat results (F5), source with the failure annotated (exception type only), stack trace ("my code"
 *     frames, P2-4), console output (trimmed, P2-5)
 *   - a group (project / namespace / class / data-driven method) → summary cards (zero cards collapsed, P2-11),
 *     failures, slowest tests, build output
 *
 * Structure is rebuilt per selection (keyed on the node key); inside, sub-parts are keyed on the few fields they
 * depend on, so a test updating during a run only touches what changed (and disclosure state survives).
 */
import repeatSvg from "@fluentui/svg-icons/icons/arrow_repeat_all_20_regular.svg?raw";
import quarantineSvg from "@fluentui/svg-icons/icons/shield_prohibited_20_regular.svg?raw";
import {
  animateEnter,
  badge,
  button,
  caret,
  collapsible,
  computed,
  copyButton as dsCopyButton,
  cx,
  define,
  effect,
  h,
  icon,
  keyed,
  LabcoatElement,
  list,
  onCleanup,
  show,
  signal,
  STATUS_LABEL,
  statusBadge,
  statusIcon,
  untracked,
  type Child,
  type ReadSignal,
  scrubGuard,
} from "@eriklieben/labcoat-design-system";
import type { ClientCommand, DetailCommand, HistoryEntry, RepeatTestResult, RunSummary, TestNode } from "../contract";
import { settings } from "../settings";
import { getStore, testKey } from "../store";
import { bucket, buildTree, fqName, leaves, slowFactor, type TreeNode } from "../tree";
import { ansi, linkifyLine, fileName, parseExternalFrame, parseStackFrame, formatClock, formatDuration, formatRelative, formatRunTime, keepFocus, pathView, projectAccent } from "../util";
import { debugAvailable, debugTests } from "../debug";
import { repeatProgress } from "./suite-status";
import { aspireFailureContext, aspireLinks } from "./aspire-links";
import { editorAnchor, editorName, isMyCode, isPathmapped, parseFrames, samePath, type Annotation } from "./code-view";
import { failureMessage } from "./diff-view";
import { snapshotPanel } from "./snapshot-view";
import { needsDiagnostics } from "./diagnostics";
import { distBar, nextFailureKey, onFailureNavRequest } from "./run-overview";
import { slowestTrendList } from "./slowest-tests";
import "./history-strip";
import "./live-console"; // LIVE OUTPUT: <lc-live-console>
import "./detail-pane.css";

const svgGlyph = (raw: string, size = 16) =>
  h("span", {
    class: "icon",
    style: { width: size, height: size },
    innerHTML: raw.replace(/<svg([^>]*)>/, (_m, a: string) => `<svg${a.replace(/\s(width|height)="[^"]*"/g, "")} fill="currentColor" aria-hidden="true" focusable="false">`),
  });

/** Repetitions of the "Run N×" action (flaky hunting, F5). */
export const REPEAT_COUNT = 20;

const isFailed = (o: TestNode["outcome"]) => o === "failed" || o === "timedOut";

export interface DetailPaneProps {
  node: TreeNode | undefined;
  /** run a node (project = whole project), or only the given tests */
  onRun: (n: TreeNode, only?: TestNode[]) => void;
  onSelect: (key: string) => void;
}
export interface DetailPane extends DetailPaneProps {}

export class DetailPane extends LabcoatElement<DetailPaneProps> {
  static defaults: DetailPaneProps = { node: undefined, onRun: () => {}, onSelect: () => {} };
  static hostClass = "detail-pane";

  protected render(): Child {
    const store = getStore();
    const $ = store.$;
    const node = this.props.node;
    const ctx: Ctx = {
      node: node as ReadSignal<TreeNode | undefined>,
      run: (n, only) => this.props.onRun.peek()(n, only),
      select: (k) => this.props.onSelect.peek()(k),
      runDisabled: computed(() => !!$.currentRun() || $.conn() !== "open"),
    };

    // F8 / Shift+F8: next / previous failure. SHELL's shortcut handler calls requestFailureNav(); a plain F8 keydown that
    // nobody handled (defaultPrevented) is taken here too, so it works before the shortcut is wired.
    const navigate = (dir: 1 | -1) => {
      const s = store.get();
      const roots = buildTree(s.projects, s.tests);
      const key = nextFailureKey(roots, this.props.node.peek()?.key, dir);
      if (key) ctx.select(key);
    };
    onCleanup(onFailureNavRequest(navigate));
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "F8" || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
      e.preventDefault();
      navigate(e.shiftKey ? -1 : 1);
    };
    window.addEventListener("keydown", onKey);
    onCleanup(() => window.removeEventListener("keydown", onKey));

    // a control re-rendered / disabled under the keyboard focus (Run this test during its run, Copy error after it):
    // focus goes to the same control again, else to the title
    keepFocus(this, () => this.querySelector<HTMLElement>(".detail-title"));

    const mode = computed(() => {
      const n = node();
      if (!n) return "";
      return `${n.test ? "test" : "group"}:${n.key}`;
    });
    // P1: the new content fades in with a 4 px rise (160 ms); not on first paint, and not while the selection keeps
    // changing within 150 ms (held ↑/↓ scrubbing stays instant). Live updates of the same test don't re-key.
    const swapOk = scrubGuard(150);
    let firstSwap = true;
    return keyed(mode, (m) => {
      const animate = swapOk() && !firstSwap;
      firstSwap = false;
      if (!m) {
        const ov = h("lc-run-overview", { onSelect: (k: string) => ctx.select(k) });
        // display: contents host: animate its root once it rendered (connected right after this region inserts it)
        if (animate) queueMicrotask(() => ov.querySelector<HTMLElement>(":scope > .detail-scroll, :scope > .empty-state") && animateEnter(ov.querySelector<HTMLElement>(":scope > .detail-scroll, :scope > .empty-state")!));
        return ov;
      }
      const n = untracked(node)!;
      const el = n.test ? testDetail(ctx) : groupDetail(ctx);
      return animate && el instanceof HTMLElement ? animateEnter(el) : el;
    });
  }
}
define("lc-detail-pane", DetailPane);

declare global {
  interface HTMLElementTagNameMap {
    "lc-detail-pane": DetailPane;
  }
}

interface Ctx {
  node: ReadSignal<TreeNode | undefined>;
  run: (n: TreeNode, only?: TestNode[]) => void;
  select: (key: string) => void;
  runDisabled: ReadSignal<boolean>;
}

// ---- small shared pieces --------------------------------------------------------------------------------

/**
 * "Soft" disabled for run actions: aria-disabled (still focusable) + a click guard, so a button that becomes unavailable
 * while it has the keyboard focus (Run this test → the run starts) keeps the focus instead of dropping it to <body>.
 */
export function soft(disabled: () => boolean, onclick: () => void) {
  return { "aria-disabled": () => disabled(), onclick: () => (disabled() ? undefined : onclick()) };
}

/** P8: the design system's copy button (icon morph + polite "Copied"). */
const copyButton = (text: () => string, label: string) => dsCopyButton({ text, label });

function breadcrumb(n: TreeNode, select: (k: string) => void) {
  const chain: TreeNode[] = [];
  for (let p = n.parent; p; p = p.parent) chain.unshift(p);
  if (!chain.length) return null;
  return h(
    "nav",
    { class: "breadcrumb", "aria-label": "Location" },
    chain.map((c, i) => h("span", { class: "crumb" }, i > 0 ? icon("chevronRight", { size: 12 }) : null, h("button", { class: "crumb-btn", onclick: () => select(c.key) }, c.label))),
  );
}

function section(title: string | (() => string), body: () => Child, opts: { copy?: () => string; open?: boolean; extra?: Child } = {}) {
  const open = signal(opts.open ?? true);
  const t = typeof title === "function" ? title : () => title;
  return h(
    "section",
    { class: "detail-section" },
    h(
      "div",
      { class: "section-header" },
      h(
        "button",
        { class: "section-toggle", "aria-expanded": () => open(), onclick: () => open.update((v) => !v) },
        caret(14),
        h("h3", null, t),
      ),
      h("span", { class: "section-extra" }, opts.extra ?? null, opts.copy ? copyButton(opts.copy, `Copy ${t().toLowerCase()}`) : null),
    ),
    // P14: grid 0fr ⇄ 1fr height reveal (instant above 600 px)
    collapsible({ open, content: body }),
  );
}

// ---- durations ---------------------------------------------------------------------------------------------

export { slowFactor };

function quantile(sorted: number[], q: number) {
  if (!sorted.length) return 0;
  const pos = (sorted.length - 1) * q;
  const lo = Math.floor(pos);
  const hi = Math.ceil(pos);
  return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
}

/** Per-test history without the entries of partial runs (single test, class, ...) unless the setting includes them. */
function visibleHistory(history: HistoryEntry[] | undefined, partialRunIds: Set<string> | undefined): HistoryEntry[] {
  if (!history) return [];
  if (!partialRunIds || partialRunIds.size === 0) return history;
  return history.filter((e) => !e.runId || !partialRunIds.has(e.runId));
}

// ---- test detail -----------------------------------------------------------------------------------------------

function testDetail(ctx: Ctx): Child {
  const store = getStore();
  const $ = store.$;
  const node = computed(() => ctx.node()!);
  // keep the last test while the node disappears for a frame (e.g. rediscovery)
  let lastTest: TestNode;
  const test = computed(() => (lastTest = ctx.node()?.test ?? lastTest));
  const key = untracked(() => testKey(test()));
  const snapshot = computed(() => {
    const v = $.viewing();
    return v?.status === "ready" ? v.run : undefined;
  });
  const running = computed(() => test().outcome === "running" || test().outcome === "queued");
  const quarantined = computed(() => !!$.quarantined()?.includes(key));
  const slow = computed(() => ($.viewing() ? undefined : slowFactor(test())));
  const fq = computed(() => fqName(test()));

  // source location shown in the code view (a stack frame click moves it)
  const loc = signal<{ path: string; line: number; nonce: number } | undefined>(undefined);
  const openFrame = (path: string, line: number) => loc.update((l) => ({ path, line, nonce: (l?.nonce ?? 0) + 1 }));

  const header = h(
    "header",
    { class: "detail-header" },
    // one header structure everywhere: breadcrumb, eyebrow (kind), title, actions
    breadcrumb(untracked(node), ctx.select),
    h("div", { class: "kind-label" }, () => (node().parent?.kind === "method" ? "Test case" : "Test")),
    h(
      "div",
      { class: "detail-title-row" },
      statusIcon(() => test().outcome, 20),
      h("h2", { class: "detail-title", tabIndex: -1, title: () => test().displayName }, () => test().displayName),
      h(
        "span",
        { class: "title-badges" },
        () =>
          test().flaky
            ? h("span", { class: "badge badge-flaky", title: "Both passed and failed within the last 10 runs" }, icon("warning", { size: 14 }), " Flaky")
            : null,
        () =>
          quarantined()
            ? h(
                "span",
                { class: "badge badge-quarantined", title: "Quarantined: still runs, but its result is counted separately and does not turn the suite red" },
                svgGlyph(quarantineSvg, 14),
                " Quarantined",
              )
            : null,
        () => {
          const f = slow();
          return f
            ? h(
                "span",
                { class: "badge badge-slow", title: `This run took ${f.toFixed(1)}× the median of its earlier runs` },
                icon("timer", { size: 14 }),
                ` Slow · ${f.toFixed(1)}×`,
              )
            : null;
        },
      ),
    ),
    h("div", { class: "fqn" }, h("code", null, fq), copyButton(fq, "Copy fully qualified name")),
    h(
      "div",
      { class: "detail-actions" },
      button({ variant: "accent", icon: "play", ...soft(ctx.runDisabled, () => ctx.run(node.peek())) }, " Run this test"),
      () =>
        debugAvailable()
          ? button(
              { icon: "bug", title: "Start this test paused until a debugger attaches (shows the process id to attach to)", ...soft(ctx.runDisabled, () => debugTests([test.peek()], test.peek().displayName)) },
              " Debug",
            )
          : null,
      button(
        {
          title: `Run this test ${REPEAT_COUNT} times in a row on the warm host (flaky hunting): pass rate and duration spread`,
          ...soft(ctx.runDisabled, () => {
            const t = test.peek();
            store.send({ type: "run", projectId: t.projectId, testIds: [t.id], repeat: REPEAT_COUNT } satisfies DetailCommand as ClientCommand);
          }),
        },
        svgGlyph(repeatSvg),
        ` Run ${REPEAT_COUNT}×`,
      ),
      () =>
        test().filePath && !isPathmapped(test().filePath!)
          ? editorAnchor({ class: "btn", path: () => test().filePath!, line: () => test().line }, icon("code"), () => ` ${editorName()}`)
          : null,
      button(
        {
          pressed: quarantined,
          class: "btn-quarantine",
          title: () =>
            quarantined()
              ? "Release from quarantine: its result counts toward passed/failed again"
              : "Quarantine: keep running this test, but count its result separately so it doesn't turn the suite red",
          onclick: () => {
            const t = test.peek();
            store.send({ type: "setQuarantine", projectId: t.projectId, id: t.id, quarantined: !quarantined.peek() } satisfies DetailCommand as unknown as ClientCommand);
          },
        },
        svgGlyph(quarantineSvg),
        () => (quarantined() ? " Release" : " Quarantine"),
      ),
      aspireLinks(test),
    ),
  );

  // ---- state bars: not in this (past) run, previous session, outdated build
  const bars = () => {
    const t = test();
    if (snapshot() && t.notInRun)
      return h(
        "div",
        { class: "messagebar mb-snapshot", role: "status" },
        icon("history", { size: 18, class: "mb-icon" }),
        h(
          "div",
          { class: "mb-body" },
          h("div", { class: "mb-title" }, "Not in this run"),
          h("div", null, `This test wasn't part of the run from ${formatRunTime(snapshot()!.startedAt)}, so there is no result to show.`),
        ),
      );
    if (!snapshot() && node().outdated)
      return h(
        "div",
        { class: "messagebar mb-warning", role: "status" },
        icon("warning", { size: 18, class: "mb-icon" }),
        h("div", { class: "mb-body" }, h("div", { class: "mb-title" }, "Outdated result"), h("div", null, "The last build of this project failed: this result is from older code.")),
      );
    if (!snapshot() && t.stale && !running() && !$.runKeys()?.has(key))
      return h(
        "div",
        { class: "messagebar mb-info mb-stale", role: "status" },
        icon("history", { size: 18, class: "mb-icon" }),
        h(
          "div",
          { class: "mb-body" },
          h("span", { class: "mb-title" }, "Result from a previous session"),
          h("span", { class: "mb-stale-text" }, t.lastRunAt ? `${formatRunTime(t.lastRunAt)} — ${REFRESH_HINT}` : REFRESH_HINT),
        ),
        button({ size: "small", class: "mb-action", ...soft(ctx.runDisabled, () => ctx.run(node.peek())) }, "Run now"),
      );
    return null;
  };

  // ---- error box (failure first)
  const errorSig = computed(() => {
    const t = test();
    return t.errorMessage ? `${t.outcome}\u0000${t.errorType ?? ""}\u0000${t.errorMessage}` : "";
  });
  const errorBox = keyed(errorSig, (sig) => {
    if (!sig) return null;
    const t = untracked(test);
    const f = isFailed(t.outcome);
    const box = errorBoxLook(t.outcome);
    return h(
      "div",
      // static content (not role=alert: arrowing through failed tests must not interrupt the screen reader; a finished run
      // is announced once by the shell's polite announcer)
      { class: `messagebar ${box.cls} detail-error`, role: "group", "aria-label": box.label },
      icon(box.icon, { size: 18, class: "mb-icon" }),
      h(
        "div",
        { class: "mb-body" },
        h("div", { class: "mb-title", title: f ? t.errorType : undefined }, f ? shortTypeName(t.errorType) || STATUS_LABEL[t.outcome] : box.label),
        f ? failureMessage(t.errorMessage!) : h("pre", { class: "mb-message" }, t.errorMessage!),
        f ? aspireFailureContext(test) : null,
      ),
      copyButton(() => [test.peek().errorType, test.peek().errorMessage, test.peek().stackTrace].filter(Boolean).join("\n"), "Copy error"),
    );
  });

  // ---- meta line + history (replaces the 7-row property grid)
  const partialRunIds = computed(() => (settings().partialRunsInHistory ? undefined : new Set($.runs().filter((r) => r.scope === "subset").map((r) => r.runId))));
  const history = computed(() => visibleHistory(test().history, partialRunIds()), );
  const meta = h(
    "div",
    { class: "detail-meta" },
    h(
      "div",
      { class: "meta-line" },
      statusBadge(() => test().outcome),
      h("span", { class: "meta-item", title: "Duration" }, icon("timer", { size: 14 }), () => (running() ? "—" : formatDuration(test().durationMs) || "—")),
      h(
        "span",
        { class: "meta-item", title: () => test().lastRunAt ?? "" },
        () => {
          const t = test();
          if (!t.lastRunAt) return snapshot() && t.notInRun ? "Not in this run" : "Never run";
          const rel = formatRelative(t.lastRunAt);
          return rel === formatClock(t.lastRunAt) ? formatRunTime(t.lastRunAt) : `${formatClock(t.lastRunAt)} · ${rel}`;
        },
      ),
      h("span", { class: "meta-item project-tag", style: { "--tag": projectAccent(untracked(test).projectId) } }, untracked(test).projectId),
      () => {
        const t = test();
        if (!t.filePath) return null;
        return editorAnchor(
          { class: "meta-item link", path: () => test().filePath!, line: () => test().line, title: (name) => `${test().filePath} — open in ${name}` },
          t.line ? `${fileName(t.filePath)}:${t.line}` : fileName(t.filePath),
        );
      },
      () => test().traits.map((tr) => h("span", { class: "badge trait" }, h("span", { class: "trait-key" }, tr.key), tr.value)),
    ),
    show(
      () => history().length > 0,
      () =>
        h(
          "div",
          { class: "meta-history" },
          h("span", { class: "meta-label" }, "History"),
          h("lc-history-strip", {
            history,
            activeRunId: () => $.viewing()?.run.runId,
            onPick: (id: string | undefined, fallback?: { startedAt?: string }) => store.viewRun(id, fallback),
          }),
        ),
    ),
  );

  // ---- source
  const source = show(
    () => !!test().filePath,
    () => section("Source", () => sourceView(test, loc, () => loc.set(undefined))),
  );

  // ---- stack trace
  const stack = keyed(
    () => test().stackTrace ?? "",
    (st) => (st ? section("Stack trace", () => stackView(st, openFrame, loc), { copy: () => st }) : null),
  );

  // ---- console output (lean mode: fetched for the selected test, GET /api/tests/details)
  effect(() => store.ensureDetails(test()));
  // "show full output": the untruncated text (GET /api/tests/output) replaces the capped one for this result
  const fullOutput = signal<{ at?: string; stdOut?: string; stdErr?: string; truncated: boolean } | undefined>(undefined);
  const consoleSig = computed(() => {
    const t = test();
    const f = fullOutput();
    const useFull = f && f.at === t.lastRunAt;
    return `${(useFull ? f.stdOut : t.stdOut) ?? ""}\u0000${(useFull ? f.stdErr : t.stdErr) ?? ""}\u0000${t.outputTruncated && !(useFull && !f.truncated) ? "1" : ""}`;
  });
  const consoleOut = keyed(consoleSig, (sig) => {
    const [out, err, cut] = sig.split("\u0000");
    const lines = consoleLines(out, err);
    if (!lines.length) return untracked(() => (test().hasOutput && test().stdOut == null && test().stdErr == null ? h("div", { class: "detail-note" }, "Loading console output…") : null));
    const more = cut
      ? h(
          "div",
          { class: "output-truncated" },
          icon("info", { size: 14 }),
          " Output truncated (64 K characters per stream). ",
          h(
            "button",
            {
              class: "btn-link link-button",
              onclick: async (e: MouseEvent) => {
                const btn = e.currentTarget as HTMLButtonElement;
                btn.disabled = true;
                const t = test.peek();
                const r = await store.fetchFullOutput(t).catch(() => undefined);
                if (r) fullOutput.set({ at: t.lastRunAt, ...r });
                btn.disabled = false;
                if (!r || r.truncated) btn.textContent = "The full output is no longer kept by the host";
              },
            },
            "Show full output",
          ),
        )
      : null;
    return section("Console output", () => [more, consoleBlock(lines)], { copy: () => [trimOutput(out), trimOutput(err)].filter(Boolean).join("\n") });
  });

  const note = () => {
    const t = test();
    if (t.errorMessage || t.stackTrace || t.hasOutput || consoleLines(t.stdOut, t.stdErr).length) return null;
    if (running()) return null; // <lc-live-console> shows "Waiting for output…" / the live output
    return h("div", { class: "detail-note" }, noOutputNote(t.outcome));
  };

  return h(
    "div",
    { class: "detail-scroll" },
    header,
    bars,
    errorBox,
    snapshotPanel(test),
    meta,
    repeatSection(test),
    // LIVE OUTPUT: the console output while the test runs, above the source (live-console.ts)
    h("lc-live-console", { test: () => test(), kind: "live" }),
    source,
    stack,
    consoleOut,
    // LIVE OUTPUT: afterwards, the streamed lines missing from the final output
    h("lc-live-console", { test: () => test(), kind: "residual" }),
    note,
  );
}

// ---- source view ---------------------------------------------------------------------------------------------

function sourceView(test: ReadSignal<TestNode>, loc: ReadSignal<{ path: string; line: number; nonce: number } | undefined>, onReset: () => void) {
  const $ = getStore().$;
  const path = computed(() => loc()?.path ?? test().filePath!);
  const failed = computed(() => isFailed(test().outcome));
  const frames = computed(() => (failed() ? parseFrames(test().stackTrace) : []));
  const here = computed(() => frames().filter((f) => samePath(f.path, path())));
  const fileCov = computed(() => $.coverage()?.files.find((f) => samePath(f.path, path())));
  const showCov = signal(false);
  const isTestFile = computed(() => samePath(path(), test().filePath!));
  const annotations = computed<Annotation[]>(() => {
    const t = test();
    if (!failed()) return [];
    const title = t.errorType || (t.outcome === "timedOut" ? "Timed out" : "Failed");
    // P1-1: the failure box above shows the message; the annotation names the exception only
    if (here().length) return [{ line: here()[0].line, title }];
    // Timeouts/failures without frames in this file: annotate the method declaration instead.
    if (isTestFile() && t.line && t.errorMessage) return [{ line: t.lineEnd ?? t.line, title }];
    return [];
  });
  const range = computed(() => {
    const t = test();
    return isTestFile() && t.line ? { from: t.line, to: t.lineEnd ?? t.line } : undefined;
  });
  const focusLine = computed(() => loc()?.line ?? annotations()[0]?.line ?? test().line);
  const markers = computed(() => here().map((f) => f.line));

  return h(
    "div",
    null,
    h(
      "div",
      { class: "code-head" },
      icon("document", { size: 16 }),
      h("span", { class: "code-head-path" }, () => pathView(path(), focusLine())),
      h("span", { class: "flex-filler" }),
      () => (isTestFile() ? null : button({ size: "small", icon: "arrowLeft", onclick: onReset, title: "Back to the test method" }, " Test source")),
      () => {
        const fc = fileCov();
        return fc
          ? h(
              "button",
              { class: "toggle-chip", "aria-pressed": showCov, onclick: () => showCov.update((v) => !v), title: "Overlay line coverage from the last coverage report" },
              icon("shieldCheck", { size: 14 }),
              ` Coverage ${Math.round(fc.lineRate * 100)}%`,
            )
          : null;
      },
      editorAnchor({ class: "icon-btn small", path, line: focusLine, "aria-label": (name) => `Open in ${name}` }, icon("code", { size: 16 })),
    ),
    h("lc-code-view", {
      viewClass: "inline",
      path,
      range,
      focusLine,
      focusNonce: () => loc()?.nonce,
      annotations,
      markers,
      coverage: fileCov,
      showCoverage: showCov,
    }),
  );
}

// ---- stack trace (P2-4) -------------------------------------------------------------------------------------------

type StackLine = { kind: "frame"; at: string; method: string; inWord: string; path: string; line: number; mine: boolean } | { kind: "external"; at: string; rest: string } | { kind: "text"; text: string };

function parseStack(stack: string, roots: readonly string[] | undefined): StackLine[] {
  return stack.split(/\r?\n/).map((l) => {
    const f = parseStackFrame(l);
    // the frame indentation is dropped (the first frame used to keep its leading spaces)
    if (f) return { kind: "frame", at: f.at.trimStart(), method: f.method, inWord: f.inWord, path: f.path, line: Number(f.line), mine: isMyCode(f.path, roots) };
    const x = parseExternalFrame(l);
    if (x) return { kind: "external", at: x.at.trimStart(), rest: x.rest };
    return { kind: "text", text: l };
  });
}

function stackView(stack: string, openFrame: (path: string, line: number) => void, loc: ReadSignal<{ path: string; line: number } | undefined>) {
  const $ = getStore().$;
  const lines = computed(() => parseStack(stack, $.sourceRoots()));
  const showAll = signal(false);
  const active = computed(() => {
    const l = loc();
    return l ? `${l.path}:${l.line}` : undefined;
  });
  const hasMine = computed(() => lines().some((l) => l.kind === "frame" && l.mine));
  const hidden = computed(() => lines().filter((l) => l.kind === "external" || (l.kind === "frame" && !l.mine)).length);

  const frameLine = (l: Extract<StackLine, { kind: "frame" }>) =>
    h(
      "div",
      { class: cx("trace-line", !l.mine && "trace-external") },
      h("span", { class: "trace-dim" }, l.at),
      h("span", { class: l.mine ? "trace-method" : "trace-dim" }, l.method),
      h("span", { class: "trace-dim" }, l.inWord),
      isPathmapped(l.path)
        ? h("span", { class: "trace-dim", title: "Path from a deterministic build (not a file on this machine)" }, `${l.path}:${l.line}`)
        : [
            h(
              "button",
              { class: () => cx("frame-btn", active() === `${l.path}:${l.line}` && "active"), onclick: () => openFrame(l.path, l.line), title: `Show ${l.path}:${l.line}` },
              `${fileName(l.path)}:${l.line}`,
            ),
            editorAnchor({ class: "frame-vscode", path: () => l.path, line: () => l.line, "aria-label": (n) => `Open in ${n}` }, icon("code", { size: 13 })),
          ],
    );
  const plain = (l: StackLine) => {
    if (l.kind === "frame") return frameLine(l);
    if (l.kind === "external") return h("div", { class: "trace-line trace-external" }, h("span", { class: "trace-dim" }, l.at), l.rest);
    return h("div", { class: "trace-line" }, ansi(l.text));
  };

  return h(
    "div",
    { class: "code-block stack", tabIndex: 0 },
    () => {
      const ls = lines();
      if (showAll() || !hasMine()) return ls.map(plain);
      // "my code" frames; runs of other frames collapse into one expandable line
      const out: Child[] = [];
      for (let i = 0; i < ls.length; ) {
        const l = ls[i];
        const external = l.kind === "external" || (l.kind === "frame" && !l.mine);
        if (!external) {
          out.push(plain(l));
          i++;
          continue;
        }
        let j = i;
        while (j < ls.length && (ls[j].kind === "external" || (ls[j].kind === "frame" && !(ls[j] as { mine: boolean }).mine))) j++;
        // one toggle for the whole trace (below): a folded run is a quiet marker, not a second expander
        out.push(h("div", { class: "trace-fold" }, `… ${j - i} framework frame${j - i === 1 ? "" : "s"}`));
        i = j;
      }
      return out;
    },
    () =>
      hasMine() && hidden() > 0
        ? h(
            "button",
            { class: "trace-all", "aria-expanded": () => showAll(), onclick: () => showAll.update((v) => !v) },
            () => (showAll() ? "Only my code" : `Show all ${lines().filter((l) => l.kind !== "text").length} frames (${hidden()} hidden)`),
          )
        : null,
  );
}

// ---- console output (P2-5) ------------------------------------------------------------------------------------------

const HEADER_RE = /^\s*(TestContext Messages|Standard Output Messages|Standard Error Messages|Debug Trace|Debug Messages):\s*$/i;

/** Drop MSTest's section headers and leading/trailing blank lines; collapse runs of blank lines. */
export function trimOutput(s: string | undefined): string {
  if (!s) return "";
  const out: string[] = [];
  for (const l of s.replace(/\r\n?/g, "\n").split("\n")) {
    if (HEADER_RE.test(l)) continue;
    if (!l.trim() && (!out.length || !out.at(-1)!.trim())) continue;
    out.push(l);
  }
  while (out.length && !out.at(-1)!.trim()) out.pop();
  return out.join("\n");
}

function consoleLines(out: string | undefined, err: string | undefined) {
  const lines: { text: string; err: boolean }[] = [];
  const add = (s: string, isErr: boolean) => {
    const t = trimOutput(s);
    if (t) for (const p of t.split("\n")) lines.push({ text: p, err: isErr });
  };
  add(out ?? "", false);
  add(err ?? "", true);
  return lines;
}

function consoleBlock(lines: { text: string; err: boolean }[]) {
  return h(
    "div",
    { class: "console", tabIndex: 0 },
    lines.map((l, i) =>
      h(
        "div",
        { class: `console-line${l.err ? " stderr" : ""}` },
        h("span", { class: "console-ln" }, String(i + 1)),
        l.err ? h("span", { class: "console-stream" }, "stderr") : null,
        h("span", { class: "console-text" }, ansi(l.text)),
      ),
    ),
  );
}

// ---- repeat results (F5) ---------------------------------------------------------------------------------------------

/** The latest repeat run that included this test, if it is the test's latest result. */
function repeatFor(t: TestNode, runs: RunSummary[], lastRun: RunSummary | undefined): { run: RunSummary; result: RepeatTestResult } | undefined {
  const latestRunId = t.history?.[t.history.length - 1]?.runId;
  for (const r of lastRun ? [lastRun, ...runs] : runs) {
    if (!r.repeat) continue;
    const result = r.repeat.perTest.find((x) => x.projectId === t.projectId && x.id === t.id);
    if (!result) continue;
    if (latestRunId && latestRunId !== r.runId) return undefined;
    return { run: r, result };
  }
  return undefined;
}

function repeatSection(test: ReadSignal<TestNode>): Child {
  const store = getStore();
  const $ = store.$;
  const key = untracked(() => testKey(test()));
  // in progress: a repeat run that includes this test ("Repeat i/N" log lines give the progress)
  // the same progress the toolbar headline shows (suite-status.ts repeatProgress)
  const live = computed(() => ($.runKeys()?.has(key) ? repeatProgress() : undefined));
  // a past run being viewed shows that run only
  const done = computed(() => (live() || $.viewing() ? undefined : repeatFor(test(), $.runs(), $.lastRun())));
  const sig = computed(() => {
    if (live()) return "live";
    const d = done();
    return d ? `done:${d.run.runId}` : "";
  });
  return keyed(sig, (s) => {
    if (!s) return null;
    if (s === "live")
      return h(
        "div",
        { class: "repeat-card running", role: "status" },
        h("span", { class: "spinner" }),
        h("span", null, () => {
          const l = live();
          return l ? `Running ${l.count}× · repetition ${l.at}/${l.count}` : "";
        }),
      );
    const { run, result } = untracked(done)!;
    return repeatResults(run, result);
  });
}

/** flaky (both outcomes) / failed / passed */
function repeatTone(r: RepeatTestResult): string {
  if (r.passed > 0 && r.failed > 0) return "repeat-flaky";
  return r.failed ? "repeat-failed" : "repeat-passed";
}

function repeatResults(run: RunSummary, r: RepeatTestResult) {
  const runs = r.passed + r.failed;
  const rate = runs ? r.passed / runs : 0;
  const d = [...r.durationsMs].sort((a, b) => a - b);
  const mixed = r.passed > 0 && r.failed > 0;
  const max = d.at(-1) ?? 0;
  const min = d[0] ?? 0;
  const pos = (v: number) => (max === min ? 50 : ((v - min) / (max - min)) * 100);
  return h(
    "section",
    { class: cx("repeat-card", repeatTone(r)), "aria-label": "Repeat run results" },
    h(
      "div",
      { class: "repeat-head" },
      h("span", { class: "repeat-title" }, run.repeat!.completed < run.repeat!.count ? `Ran ${run.repeat!.completed} of ${run.repeat!.count} repetitions (cancelled)` : `Ran ${run.repeat!.count}×`),
      h("span", { class: "repeat-when" }, formatRunTime(run.startedAt)),
      mixed ? badge({ class: "badge-flaky" }, icon("warning", { size: 14 }), " Flaky") : null,
    ),
    h(
      "div",
      { class: "repeat-stats" },
      h("div", { class: "repeat-stat" }, h("div", { class: "stat-label" }, "Pass rate"), h("div", { class: "stat-value" }, `${Math.round(rate * 100)}%`)),
      h("div", { class: "repeat-stat tone-passed" }, h("div", { class: "stat-label" }, "Passed"), h("div", { class: "stat-value" }, String(r.passed))),
      h("div", { class: "repeat-stat tone-failed" }, h("div", { class: "stat-label" }, "Failed"), h("div", { class: "stat-value" }, String(r.failed))),
      r.skipped ? h("div", { class: "repeat-stat tone-skipped" }, h("div", { class: "stat-label" }, "Skipped"), h("div", { class: "stat-value" }, String(r.skipped))) : null,
    ),
    h("div", { class: "repeat-rate", role: "img", "aria-label": `${r.passed} passed, ${r.failed} failed` }, h("span", { class: "seg passed", style: { width: `${rate * 100}%` } }), h("span", { class: "seg failed", style: { width: `${runs ? (r.failed / runs) * 100 : 0}%` } })),
    d.length
      ? h(
          "div",
          { class: "repeat-spread" },
          h("div", { class: "repeat-dots", role: "img", "aria-label": `Durations from ${formatDuration(min)} to ${formatDuration(max)}` }, d.map((v) => h("span", { class: "repeat-dot", style: { left: `${pos(v)}%` }, title: formatDuration(v) }))),
          h(
            "div",
            { class: "repeat-quantiles mono" },
            h("span", null, "min ", h("b", null, formatDuration(min))),
            h("span", null, "median ", h("b", null, formatDuration(quantile(d, 0.5)))),
            h("span", null, "p95 ", h("b", null, formatDuration(quantile(d, 0.95)))),
            h("span", null, "max ", h("b", null, formatDuration(max))),
          ),
        )
      : null,
  );
}

// ---- group detail -----------------------------------------------------------------------------------------------------

const KIND_TITLE = { project: "Project", namespace: "Namespace", class: "Test class", method: "Data-driven test", test: "Test", group: "Group" } as const;

function groupDetail(ctx: Ctx): Child {
  const store = getStore();
  let lastNode: TreeNode;
  const node = computed(() => (lastNode = ctx.node() ?? lastNode));
  const n0 = untracked(node);
  const counts = computed(() => node().counts);
  const all = computed(() => leaves(node()));
  const failures = computed(() => all().filter((t) => bucket(t.outcome) === "failed"));
  const slowest = computed(() =>
    all()
      .filter((t) => (t.durationMs ?? 0) > 0)
      .sort((a, b) => (b.durationMs ?? 0) - (a.durationMs ?? 0))
      .slice(0, 5),
  );
  const project = computed(() => node().project);
  const diagnose = computed(() => {
    const p = project();
    return !!p && needsDiagnostics(p, counts().total);
  });

  const testRow = (t: ReadSignal<TestNode>, withMsg: boolean) =>
    h(
      "li",
      null,
      h(
        "button",
        { class: "failure-item", onclick: () => ctx.select(`t:${testKey(t.peek())}`) },
        statusIcon(() => t().outcome),
        h(
          "span",
          { class: "failure-text" },
          h("span", { class: "failure-name" }, () => (t().className ? h("span", { class: "dim" }, `${t().className}.`) : null), () => t().displayName),
          withMsg ? () => (t().errorMessage ? h("span", { class: "failure-msg" }, t().errorMessage!.split("\n")[0]) : null) : null,
        ),
        h("span", { class: "row-duration" }, () => formatDuration(t().durationMs)),
      ),
    );

  const stat = (label: string, value: () => number | string, tone?: string, mono?: boolean) =>
    h("div", { class: cx("stat-card", tone && `tone-${tone}`) }, h("div", { class: "stat-label" }, label), h("div", { class: cx("stat-value", mono && "mono") }, value));

  return h(
    "div",
    { class: "detail-scroll" },
    h(
      "header",
      { class: "detail-header" },
      breadcrumb(n0, ctx.select),
      h("div", { class: "kind-label" }, KIND_TITLE[n0.kind]),
      h("div", { class: "detail-title-row" }, statusIcon(() => node().status, 20), h("h2", { class: "detail-title", tabIndex: -1, title: n0.label }, () => node().label)),
      () => (project() ? h("div", { class: "fqn" }, h("code", null, project()!.path), copyButton(() => project()!.path, "Copy project path")) : null),
      // multi-targeting (Round 4): which TFM the tests run on; a picker when the project has several
      () => {
        const p = project();
        if (!p || (!p.targetFramework && !p.availableFrameworks?.length)) return null;
        const fws = p.availableFrameworks ?? [];
        if (fws.length < 2) return h("div", { class: "tfm-row" }, h("span", { class: "tfm-label" }, "Target framework"), h("span", { class: "badge tfm-badge" }, p.targetFramework ?? fws[0]));
        return h(
          "label",
          { class: "tfm-row" },
          h("span", { class: "tfm-label" }, "Target framework"),
          h(
            "select",
            {
              class: "select tfm-select",
              title: "Run the tests on this target framework (rebuilds and rediscovers the project)",
              disabled: () => ctx.runDisabled() || project()?.status === "building" || project()?.status === "discovering",
              onchange: (e: Event) => {
                const v = (e.currentTarget as HTMLSelectElement).value;
                store.send({ type: "setTargetFramework", projectId: p.id, targetFramework: v });
              },
            },
            fws.map((f) => h("option", { value: f, selected: f === p.targetFramework }, f)),
          ),
        );
      },
      h(
        "div",
        { class: "detail-actions" },
        // P2-3: a project without tests shows the diagnostics below instead of a disabled "Run 0 tests"
        () =>
          diagnose() || counts().total === 0
            ? null
            : button(
                { variant: "accent", icon: "play", ...soft(() => ctx.runDisabled() || project()?.status === "error" || counts().total === 0, () => ctx.run(node.peek())) },
                () => ` Run ${counts().total} test${counts().total === 1 ? "" : "s"}`,
              ),
        () =>
          failures().length > 0 && node().kind !== "method"
            ? button({ icon: "arrowClockwise", ...soft(ctx.runDisabled, () => ctx.run(node.peek(), failures.peek())), title: "Run the failed tests again" }, ` Run failed (${failures().length})`)
            : null,
        () =>
          project()
            ? button({ icon: "arrowSync", disabled: ctx.runDisabled, title: "Rebuild and rediscover tests", onclick: () => store.send({ type: "discover", projectId: project.peek()!.id }) }, " Rediscover")
            : null,
      ),
    ),
    () =>
      project()?.status === "error"
        ? h(
            "div",
            { class: "messagebar mb-error", role: "group", "aria-label": "Build failed" },
            icon("errorCircle", { size: 18, class: "mb-icon" }),
            h(
              "div",
              { class: "mb-body" },
              h("div", { class: "mb-title" }, "Build failed"),
              h("div", { class: "mb-message plain" }, project()!.lastError || "The project could not be built. Fix the errors below and rediscover."),
            ),
          )
        : null,
    keyed(
      () => project()?.buildOutput ?? "",
      (out) =>
        out
          ? section(
              "Build output",
              () =>
                h(
                  "div",
                  { class: "console", tabIndex: 0 },
                  out.split(/\r?\n/).map((l, i) =>
                    h(
                      "div",
                      { class: `console-line${buildLineTone(l)}` },
                      h("span", { class: "console-ln" }, String(i + 1)),
                      h("span", { class: "console-text" }, linkifyLine(l)),
                    ),
                  ),
                ),
              { copy: () => out },
            )
          : null,
    ),
    show(
      () => counts().total > 0,
      () => [
        // P2-11: only the cards with something to say (Total and Duration always)
        h(
          "div",
          { class: "stat-cards" },
          stat("Total", () => counts().total),
          () => (counts().passed ? stat("Passed", () => counts().passed, "passed") : null),
          () => (counts().failed ? stat("Failed", () => counts().failed, "failed") : null),
          () => (counts().skipped ? stat("Skipped", () => counts().skipped, "skipped") : null),
          () => (counts().notRun + counts().queued + counts().running ? stat("Not run", () => counts().notRun + counts().queued + counts().running) : null),
          stat("Duration", () => (node().durationMs ? formatDuration(node().durationMs) : "—"), undefined, true),
        ),
        distBar(counts),
      ],
    ),
    show(
      () => failures().length > 0,
      () => section(() => `Failures (${failures().length})`, () => h("ul", { class: "failure-list" }, list(failures, (t) => testKey(t), (t) => testRow(t, true)))),
    ),
    show(
      () => slowest().length > 0 && node().kind !== "method" && node().kind !== "project",
      () => section("Slowest tests", () => h("ul", { class: "failure-list" }, list(slowest, (t) => testKey(t), (t) => testRow(t, false)))),
    ),
    // a project: the slowest tests with their duration trend over the recent runs (GET /api/tests/slowest)
    show(
      () => slowest().length > 0 && node().kind === "project",
      () =>
        section("Slowest tests", () =>
          slowestTrendList(
            computed(() => node().projectId),
            computed(() => `${node().durationMs}|${counts().total}|${node().status}`),
            (t) => ctx.select(`t:${testKey(t)}`),
          ),
        ),
    ),
    () => {
      if (diagnose()) return h("lc-project-diagnostics", { projectId: project()!.id });
      if (counts().total === 0 && !project()?.buildOutput) return h("div", { class: "detail-note" }, `No tests discovered in this ${node().kind}.`);
      return null;
    },
  );
}

const REFRESH_HINT = "run the test to refresh it.";

/** " stderr" for a build output line with an error, " warn" for one with a warning */
function buildLineTone(l: string): string {
  if (/\berror\b/i.test(l)) return " stderr";
  return /\bwarning\b/i.test(l) ? " warn" : "";
}

/** The message box of a test with an error message: failed, skipped or any other outcome. */
function errorBoxLook(outcome: TestNode["outcome"]): { cls: string; label: string; icon: "dismissCircle" | "warning" | "errorCircle" } {
  if (isFailed(outcome)) return { cls: "mb-error", label: "Failure", icon: "dismissCircle" };
  if (outcome === "skipped") return { cls: "mb-warning", label: "Skipped", icon: "warning" };
  return { cls: "mb-info", label: "Message", icon: "errorCircle" };
}

/** The note in place of the output of a test that has none. */
function noOutputNote(outcome: TestNode["outcome"]): string {
  if (outcome === "notRun" || outcome === "cancelled") return "This test hasn't produced a result yet. Run it to see output here.";
  return outcome === "passed" ? "Passed with no output." : "No additional output.";
}

/** "AssertFailedException" for "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException" (generic args kept). */
export function shortTypeName(t: string | undefined): string {
  if (!t) return "";
  const base = t.split(/[<[`]/)[0];
  const i = base.lastIndexOf(".");
  return i < 0 ? t : t.slice(i + 1);
}
