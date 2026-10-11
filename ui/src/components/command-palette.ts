/**
 * Command palette (F7, Ctrl/Cmd+K): commands (run all / failed / selected, toggle watch / coverage, view a run, open
 * settings / the coverage report, switch theme, …) and fuzzy "go to test". WAI-ARIA combobox + listbox: the input
 * keeps focus, ArrowUp/Down (Home/End, PageUp/Down) move the active option, Enter runs it, Escape goes back / closes.
 *
 * Sub-lists: "Go to test…" (tests only), "View run…" (stored runs), "Switch theme…". Backspace in an empty input
 * returns to the command list.
 */
import { animateEnter, computed, cx, effect, h, icon, kbd, list, show, signal, statusIcon, untracked, type Child, type IconName, type Status } from "@eriklieben/labcoat-design-system";
import type { RunSummary, TestNode } from "../contract";
import { getStore, testKey } from "../store";
import { flavorNames, setThemeFamily, setThemePref, THEME_FAMILIES, themeFamily, themeFamilyInfo, themePref } from "../theme";
import { bucket, fqName, type TreeSort } from "../tree";
import { formatRunTime, runCounts } from "../util";
import { lockedHint } from "../settings";
import { acceptAllSnapshots, pendingSnapshotTests } from "../snapshots";
import { copyDiagnostics } from "../diagnostics";
import { appDialog } from "./shortcuts";
import { affectedCommands } from "./run-affected";
import { exportCommands } from "../export";
import "./command-palette.css";

export const paletteOpen = signal(false);

export interface PaletteActions {
  runAll: () => void;
  runFailed: () => void;
  /** undefined when nothing is selected */
  runSelected: (() => void) | undefined;
  selectedLabel: string | undefined;
  coverageSelected: (() => void) | undefined;
  toggleWatch: () => void;
  toggleCoverage: () => void;
  buildImpactMap: () => void;
  cancel: () => void;
  rediscover: () => void;
  selectTest: (key: string) => void;
  viewRun: (runId: string | undefined) => void;
  page: (p: "tests" | "coverage" | "settings") => void;
  toggleOutput: () => void;
  showShortcuts: () => void;
  sort: (mode: TreeSort) => void;
  sortLabels: Record<TreeSort, string>;
  currentSort: TreeSort;
  // review 2 additions (optional: older callers keep working)
  /** the tree is filtered: run the N tests it shows */
  runMatching?: { count: number; run: () => void };
  /** debug the selected test (undefined: not a test / not available) */
  debugSelected?: () => void;
  /** restart the test host of the selected test's project (or all) */
  restartHost?: () => void;
  /** open the build errors dialog (undefined: no failed build) */
  showBuildErrors?: () => void;
  /** pick a run to compare the live results (or the viewed run) with */
  compareRuns?: () => void;
  /** copy a deep link to the selection (or the current view) */
  copyLink?: () => void;
}

type Mode = "commands" | "tests" | "runs" | "theme";

interface Item {
  id: string;
  label: string;
  /** dimmed text after the label */
  detail?: string;
  icon?: IconName;
  status?: string;
  shortcut?: string;
  disabled?: boolean;
  /** stays open (switches to a sub-list) */
  sub?: Mode;
  run?: () => void;
  /** match positions in label (highlighted) */
  hits?: number[];
}

/**
 * Fuzzy match: a contiguous substring scores highest (word start best), then a subsequence whose letters all start
 * words ("prc" → PlaceOrder_ReturnsCode), then a compact subsequence. Scattered matches are rejected.
 */
export function fuzzy(text: string, query: string): { score: number; hits: number[] } | undefined {
  const t = text.toLowerCase();
  const q = query.toLowerCase().replace(/\s+/g, "");
  if (!q) return { score: 0, hits: [] };
  const contiguous = contiguousMatch(text, t, q, query);
  if (contiguous) return contiguous;
  const sub = subsequence(text, t, q);
  if (!sub) return undefined;
  const { hits, allBoundary } = sub;
  const span = hits.at(-1)! - hits[0] + 1;
  if (allBoundary) return { score: 2000 - span - t.length * 0.1, hits };
  if (span > q.length * 3) return undefined;
  return { score: 1000 - span - t.length * 0.1, hits };
}

/** Whether position `i` of `text` (`t` = lowercased) starts a word: after a separator, or an upper case after a non-upper. */
function isWordStart(text: string, t: string, i: number): boolean {
  if (i === 0 || /[\s._(,/-]/.test(t[i - 1])) return true;
  const upper = (c: string) => c >= "A" && c <= "Z";
  return upper(text[i]) && !upper(text[i - 1]);
}

/** The query as a substring: without its spaces, or as typed with them ("switch theme" in "Switch theme…"). */
function contiguousMatch(text: string, t: string, q: string, query: string): { score: number; hits: number[] } | undefined {
  const score = (at: number) => 3000 + (isWordStart(text, t, at) ? 500 : 0) - at - t.length * 0.1;
  const at = t.indexOf(q);
  if (at >= 0) return { score: score(at), hits: Array.from({ length: q.length }, (_, i) => at + i) };
  const raw = query.toLowerCase().trim().replace(/\s+/g, " ");
  const at2 = raw.includes(" ") ? t.indexOf(raw) : -1;
  if (at2 < 0) return undefined;
  return { score: score(at2), hits: Array.from({ length: raw.length }, (_, i) => at2 + i).filter((i) => t[i] !== " ") };
}

/** Greedy subsequence, preferring word starts; `allBoundary`: every hit starts a word. Undefined when `q` doesn't fit. */
function subsequence(text: string, t: string, q: string): { hits: number[]; allBoundary: boolean } | undefined {
  const hits: number[] = [];
  let j = 0;
  let allBoundary = true;
  let i = 0;
  while (i < t.length && j < q.length) {
    if (t[i] !== q[j]) {
      i++;
      continue;
    }
    let at = i;
    // a later word start with the same letter is better than a mid-word letter
    if (!isWordStart(text, t, i)) {
      const later = laterWordStart(text, t, i + 1, q[j]);
      if (later < 0) allBoundary = false;
      else at = later;
    }
    hits.push(at);
    j++;
    i = at + 1;
  }
  return j < q.length ? undefined : { hits, allBoundary };
}

/** The first position from `from` on where `ch` starts a word, or -1. */
function laterWordStart(text: string, t: string, from: number, ch: string): number {
  for (let k = from; k < t.length; k++) if (t[k] === ch && isWordStart(text, t, k)) return k;
  return -1;
}

const MAX_TESTS = 60;

const MODE_LABELS: Record<"system" | "light" | "dark", string> = { system: "Follow system (light / dark)", light: "Light mode", dark: "Dark mode" };
const MODE_ICONS: Record<"system" | "light" | "dark", IconName> = { system: "darkTheme", light: "sun", dark: "moon" };

/** A stored run's dot: failed if anything failed, passed if anything passed, else not run. */
/** Run / collect coverage / debug for the selection, each when the caller offers it. */
function selectionCommands(x: PaletteActions, busy: boolean): (Item | undefined)[] {
  return [
    x.runSelected
      ? { id: "run-selected", label: "Run selected", detail: x.selectedLabel, icon: "play", shortcut: "Enter", disabled: busy, run: x.runSelected }
      : undefined,
    x.coverageSelected
      ? { id: "cov-selected", label: "Collect coverage for selected", detail: x.selectedLabel, icon: "shieldCheck", shortcut: "C", disabled: busy, run: x.coverageSelected }
      : undefined,
    x.debugSelected ? { id: "debug-selected", label: "Debug test", detail: x.selectedLabel, icon: "bug", disabled: busy, run: x.debugSelected } : undefined,
  ];
}

/** "Sort: …" for each tree order, the current one marked. */
function sortCommands(x: PaletteActions): Item[] {
  return (["default", "slowest", "fastest"] as const).map((m) => ({
    id: `sort-${m}`,
    label: `Sort: ${x.sortLabels[m].toLowerCase()}`,
    detail: x.currentSort === m ? "current" : undefined,
    icon: (m === "fastest" ? "sortUp" : "sortDown") as IconName,
    run: () => x.sort(m),
  }));
}

function runStatus(r: RunSummary): string {
  if (r.failed) return "failed";
  return r.passed ? "passed" : "notRun";
}

/** The leading icon of a palette row: its status dot, its icon, or an empty slot that keeps labels aligned. */
function itemIcon(x: Item): Child {
  if (x.status) return statusDot(x.status);
  return x.icon ? icon(x.icon, { size: 16, class: "palette-icon" }) : h("span", { class: "palette-icon" });
}

export function commandPalette(a: () => PaletteActions): Child {
  const store = getStore();
  const $ = store.$;
  const mode = signal<Mode>("commands");
  const query = signal("");
  const active = signal(0);
  const close = () => paletteOpen.set(false);
  effect(() => {
    if (!paletteOpen()) return;
    untracked(() => {
      mode.set("commands");
      query.set("");
      active.set(0);
    });
  });

  const commands = (): Item[] => {
    const x = a();
    const s = store.get();
    const running = !!s.currentRun;
    const connected = s.conn === "open";
    const failed = [...s.tests.values()].filter((t) => bucket(t.outcome) === "failed").length;
    const pending = untracked(pendingSnapshotTests).length;
    // most commands need a connected host that isn't running
    const busy = running || !connected;
    return [
      { id: "run-all", label: "Run all tests", icon: "playFilled", shortcut: "Mod+Enter", disabled: busy, run: x.runAll },
      x.runMatching
        ? { id: "run-matching", label: `Run ${x.runMatching.count} matching`, detail: "the tests the filtered tree shows", icon: "play", disabled: busy || !x.runMatching.count, run: x.runMatching.run }
        : undefined,
      { id: "run-failed", label: `Run failed (${failed})`, detail: failed ? undefined : "none failed", icon: "arrowClockwise", disabled: busy || !failed, run: x.runFailed },
      pending
        ? { id: "accept-snapshots", label: `Accept all snapshots (${pending})`, detail: "Verify: received → verified, then re-run", icon: "checkmark", disabled: busy, run: () => void acceptAllSnapshots() }
        : undefined,
      ...affectedCommands(busy),
      ...selectionCommands(x, busy),
      { id: "cancel", label: "Stop the current run", detail: running ? undefined : "nothing is running", icon: "stopFilled", shortcut: "Mod+.", disabled: !running, run: x.cancel },
      x.restartHost ? { id: "restart-host", label: "Restart test host", detail: "fresh process: fixtures start again", icon: "arrowReset", disabled: busy, run: x.restartHost } : undefined,
      x.showBuildErrors ? { id: "build-errors", label: "Show build errors", icon: "errorCircle", run: x.showBuildErrors } : undefined,
      { id: "goto", label: "Go to test…", icon: "search", sub: "tests" },
      { id: "watch", label: s.watch ? "Turn watch mode off" : "Turn watch mode on", icon: s.watch ? "eyeFilled" : "eye", shortcut: "W", disabled: !connected, run: x.toggleWatch },
      { id: "impact", label: "Build impact map", detail: "run each test class once with coverage (smart watch)", icon: "targetArrow", disabled: busy, run: x.buildImpactMap },
      {
        id: "coverage",
        label: s.coverageEnabled ? "Stop collecting coverage on runs" : "Collect coverage on runs",
        icon: s.coverageEnabled ? "shieldCheckFilled" : "shieldCheck",
        disabled: !connected,
        run: x.toggleCoverage,
      },
      { id: "rediscover", label: "Rediscover", detail: "rebuild and rediscover tests", icon: "arrowSync", disabled: busy, run: x.rediscover },
      { id: "runs", label: "View run…", detail: `${s.runs.length} stored`, icon: "history", sub: "runs", disabled: !s.runs.length },
      x.compareRuns ? { id: "compare", label: "Compare runs…", detail: "new failures, fixed, newly slow", icon: "arrowSync", disabled: !s.runs.length, run: x.compareRuns } : undefined,
      ...exportCommands({ viewingRunId: s.viewing?.run.runId, hasRuns: s.runs.length > 0, hasCoverage: !!s.fullCoverage }),
      x.copyLink ? { id: "copy-link", label: "Copy link", detail: x.selectedLabel ?? "to this view", icon: "link", run: x.copyLink } : undefined,
      s.viewing ? { id: "live", label: "Back to live results", icon: "arrowLeft", shortcut: "Escape", run: () => x.viewRun(undefined) } : undefined,
      { id: "page-tests", label: "Open tests", icon: "taskList", run: () => x.page("tests") },
      { id: "page-coverage", label: "Open coverage report", icon: "dataPie", run: () => x.page("coverage") },
      { id: "page-settings", label: "Open settings", icon: "settings", run: () => x.page("settings") },
      { id: "output", label: "Toggle output panel", icon: "console", shortcut: "Ctrl+`", run: x.toggleOutput },
      ...sortCommands(x),
      { id: "theme", label: "Switch theme…", detail: `${themeFamilyInfo(themeFamily.peek()).label} · ${themePref.peek()}`, icon: "darkTheme", sub: "theme" },
      { id: "shortcuts", label: "Shortcuts", detail: "keyboard shortcuts", icon: "info", shortcut: "?", run: x.showShortcuts },
      { id: "copy-diagnostics", label: "Copy diagnostics", detail: "for a bug report: versions, projects, settings, recent log", icon: "bug", run: () => void copyDiagnostics() },
    ].filter(Boolean) as Item[];
  };

  const testItem = (t: TestNode, hits?: number[]): Item => ({
    id: `t:${testKey(t)}`,
    label: t.displayName,
    detail: [t.namespace, t.className].filter(Boolean).join(".") + (store.get().projects.length > 1 ? ` · ${t.projectId}` : ""),
    status: t.outcome,
    hits,
    run: () => a().selectTest(`t:${testKey(t)}`),
  });

  const runItem = (r: RunSummary): Item => ({
    id: `r:${r.runId}`,
    label: formatRunTime(r.startedAt),
    detail: [runCounts(r), r.scope === "subset" ? `${r.total} selected` : "", r.trigger && r.trigger !== "user" ? r.trigger : ""].filter(Boolean).join(" · "),
    status: runStatus(r),
    run: () => a().viewRun(r.runId),
  });

  const themeItems = (): Item[] => [
    ...THEME_FAMILIES.map((f) => ({
      id: `fam:${f.id}`,
      label: `${f.label} colors`,
      // locked by the host (ui-defaults): shown disabled with the Settings page's "Set by …" hint
      detail: untracked(() => lockedHint("theme")) ?? (themeFamily.peek() === f.id ? `current · ${flavorNames(f)}` : flavorNames(f)),
      disabled: !!untracked(() => lockedHint("theme")),
      icon: "darkTheme" as IconName,
      run: () => setThemeFamily(f.id),
    })),
    ...(["system", "light", "dark"] as const).map((p) => ({
      id: `mode:${p}`,
      label: MODE_LABELS[p],
      detail: untracked(() => lockedHint("mode")) ?? (themePref.peek() === p ? "current" : undefined),
      disabled: !!untracked(() => lockedHint("mode")),
      icon: MODE_ICONS[p],
      run: () => setThemePref(p),
    })),
  ];

  const items = computed<Item[]>(() => {
    if (!paletteOpen()) return [];
    const q = query().trim();
    const m = mode();
    const rank = <T,>(src: T[], text: (x: T) => string, mk: (x: T, hits: number[]) => Item, limit = Infinity) => {
      if (!q) return src.slice(0, limit).map((x) => mk(x, []));
      const scored: { x: T; score: number; hits: number[] }[] = [];
      for (const x of src) {
        const r = fuzzy(text(x), q);
        if (r) scored.push({ x, ...r });
      }
      scored.sort((p, r) => r.score - p.score);
      return scored.slice(0, limit).map((s) => mk(s.x, s.hits));
    };
    if (m === "runs") return rank($.runs(), (r) => `${formatRunTime(r.startedAt)} ${runCounts(r)}`, (r) => runItem(r));
    if (m === "theme") return rank(themeItems(), (i) => i.label, (i, hits) => ({ ...i, hits }));
    const tests = [...$.tests().values()];
    if (m === "tests") {
      if (!q) {
        // failing tests first: the usual reason to jump
        const failing = tests.filter((t) => bucket(t.outcome) === "failed");
        return [...failing, ...tests.filter((t) => bucket(t.outcome) !== "failed")].slice(0, MAX_TESTS).map((t) => testItem(t));
      }
      return rankTests(tests, q);
    }
    const cmds = untracked(commands);
    if (!q) return cmds;
    // commands and tests in one ranking (a command wins a tie)
    const scored: { item: Item; score: number }[] = [];
    for (const c of cmds) {
      const r = fuzzy(c.label, q);
      if (r) scored.push({ item: { ...c, hits: r.hits }, score: r.score + 1 });
    }
    if (q.length >= 2) for (const t of scoreTests(tests, q)) scored.push({ item: testItem(t.t, t.hits), score: t.score });
    scored.sort((x, y) => y.score - x.score);
    return scored.slice(0, MAX_TESTS).map((x) => x.item);
  });

  const scoreTests = (tests: TestNode[], q: string) => {
    const scored: { t: TestNode; score: number; hits: number[] }[] = [];
    for (const t of tests) {
      const r = fuzzy(t.displayName, q);
      if (r) scored.push({ t, ...r });
      else {
        const f = fuzzy(fqName(t), q);
        if (f) scored.push({ t, score: f.score - 500, hits: [] });
      }
    }
    return scored;
  };
  const rankTests = (tests: TestNode[], q: string): Item[] =>
    scoreTests(tests, q)
      .sort((p, r) => r.score - p.score)
      .slice(0, MAX_TESTS)
      .map((s) => testItem(s.t, s.hits));

  effect(() => {
    items();
    active.set(0);
  });

  let input!: HTMLInputElement;
  let listEl!: HTMLElement;
  const listId = "palette-list";
  const optId = (i: number) => `palette-opt-${i}`;

  const choose = (item: Item | undefined) => {
    if (!item || item.disabled) return;
    if (item.sub) {
      mode.set(item.sub);
      query.set("");
      input.value = "";
      return;
    }
    close();
    // after the dialog closed and focus went back
    queueMicrotask(() => item.run?.());
  };

  const move = (i: number) => {
    const n = items.peek().length;
    if (!n) return;
    const j = Math.max(0, Math.min(n - 1, i));
    active.set(j);
    listEl.querySelector(`#${optId(j)}`)?.scrollIntoView({ block: "nearest" });
  };

  const onKey = (e: KeyboardEvent) => {
    const cur = active.peek();
    switch (e.key) {
      case "ArrowDown":
        move(cur + 1 >= items.peek().length ? 0 : cur + 1);
        break;
      case "ArrowUp":
        move(cur - 1 < 0 ? items.peek().length - 1 : cur - 1);
        break;
      case "PageDown":
        move(cur + 8);
        break;
      case "PageUp":
        move(cur - 8);
        break;
      case "Home":
        if (!e.ctrlKey) return;
        move(0);
        break;
      case "End":
        if (!e.ctrlKey) return;
        move(items.peek().length - 1);
        break;
      case "Enter":
        choose(items.peek()[cur]);
        break;
      case "Escape":
        if (mode.peek() !== "commands") {
          mode.set("commands");
          query.set("");
          input.value = "";
        } else close();
        break;
      case "Backspace":
        if (input.value === "" && mode.peek() !== "commands") {
          mode.set("commands");
          break;
        }
        return;
      default:
        return;
    }
    e.preventDefault();
    e.stopPropagation();
  };

  const placeholder = () =>
    ({ commands: "Type a command or a test name…", tests: "Go to test…", runs: "View run from…", theme: "Switch theme…" })[mode()];

  const label = (it: Item) => {
    if (!it.hits?.length) return it.label;
    const set = new Set(it.hits);
    const out: Child[] = [];
    let buf = "";
    let inHit = false;
    for (let i = 0; i < it.label.length; i++) {
      const hit = set.has(i);
      if (hit !== inHit && buf) {
        out.push(inHit ? h("mark", buf) : buf);
        buf = "";
      }
      inHit = hit;
      buf += it.label[i];
    }
    if (buf) out.push(inHit ? h("mark", buf) : buf);
    return out;
  };

  return appDialog({
    open: paletteOpen,
    onClose: close,
    label: "Command palette",
    class: "palette-dialog",
    placement: "top",
    bare: true,
    initialFocus: () => input,
    content: () => [
      h(
        "div",
        { class: "palette-input-row" },
        () => (mode() === "commands" ? icon("search", { size: 18 }) : h("span", { class: "palette-mode" }, { tests: "Tests", runs: "Runs", theme: "Theme" }[mode() as "tests"])),
        h("input", {
          ref: (el) => (input = el),
          class: "palette-input",
          type: "text",
          role: "combobox",
          "aria-expanded": "true",
          "aria-controls": listId,
          "aria-autocomplete": "list",
          "aria-label": placeholder,
          "aria-activedescendant": () => (items().length ? optId(active()) : undefined),
          placeholder,
          spellcheck: false,
          autocomplete: "off",
          oninput: (e) => query.set((e.currentTarget as HTMLInputElement).value),
          onkeydown: onKey,
        }),
        kbd("Escape", { class: "palette-esc" }),
      ),
      h(
        "div",
        {
          class: "palette-list",
          id: listId,
          role: "listbox",
          "aria-label": "Results",
          ref: (el) => {
            listEl = el;
            // P9: a submenu (Go to test…, View run…, Theme) swaps in with a 100 ms fade; typing never animates
            let prev = untracked(mode);
            effect(() => {
              const m = mode();
              if (m !== prev) animateEnter(el, "fade");
              prev = m;
            });
          },
        },
        list(
          () => items(),
          (it) => it.id,
          (it, index) =>
            h(
              "div",
              {
                id: () => optId(index()),
                role: "option",
                "aria-selected": () => active() === index(),
                "aria-disabled": () => (it().disabled ? "true" : undefined),
                class: () => cx("palette-item", active() === index() && "active", it().disabled && "disabled"),
                onmousemove: () => active.peek() !== index.peek() && active.set(index.peek()),
                onclick: () => choose(it.peek()),
              },
              () => {
                const x = it();
                return [
                  itemIcon(x),
                  h("span", { class: "palette-label" }, label(x)),
                  x.detail ? h("span", { class: "palette-detail" }, x.detail) : null,
                  x.sub ? icon("chevronRight", { size: 14, class: "palette-sub" }) : null,
                  x.shortcut ? kbd(x.shortcut, { class: "palette-kbd" }) : null,
                ];
              },
            ),
        ),
        show(
          () => items().length === 0,
          () => h("div", { class: "palette-empty" }, () => (mode() === "runs" ? "No stored runs match." : "Nothing matches.")),
        ),
      ),
    ],
  });
}

function statusDot(status: string) {
  return h("span", { class: "palette-icon palette-status" }, statusIcon(status as Status));
}
