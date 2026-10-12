/**
 * <lc-tree-pane>: the test tree (port of TreePane.tsx). Virtualized: only the rows in view (+ overscan) exist in the
 * DOM; rows are keyed by node key, so a test update only touches the bindings whose values changed (status icon,
 * counts, duration, ...), and scrolling hands the elements of rows that left to the rows that entered (renderRows).
 *
 * IDE workflows (review 2, #7): a context menu per row (right click, Shift+F10, the ContextMenu key), Ctrl/Cmd+click and
 * Shift+click (Shift+↑/↓, Ctrl+Space) multi-select with a selection bar, group by Namespace / Project / Trait / Outcome,
 * and search tokens (`is:failed`, `cat:`, `trait:k=v`, `msg:`, `proj:`; see tree.ts compileQuery) with a syntax hint.
 *
 * Accessibility (#6): each row's accessible name carries its outcome and counts ("Add_ReturnsSum, passed, 4 ms",
 * "OrderPricingTests, 1 failed of 7"), rows have aria-level / aria-posinset / aria-setsize, and the active row is
 * always rendered (pinned into the virtual window) so aria-activedescendant never dangles. The status chips are one
 * tab stop (roving tabindex).
 *
 * Reference pattern for porting components — see ARCHITECTURE.md.
 */
import {
  animateEnter,
  batch,
  button,
  canAnimate,
  chip,
  flash,
  flip,
  isBusy,
  computed,
  cx,
  define,
  effect,
  getOwner,
  h,
  icon,
  iconButton,
  keyed,
  LabcoatElement,
  list,
  locate,
  menu,
  scrollToTop,
  menuButton,
  onCleanup,
  root,
  rovingIndex,
  show,
  signal,
  statusIcon,
  STATUS_LABEL,
  toast,
  untracked,
  type MenuEntry,
  type ReadSignal,
  type Signal,
  type Child,
} from "@eriklieben/labcoat-design-system";
import type { ClientCommand, DetailCommand, TestNode, TestProject } from "../contract";
import { debugAvailable } from "../debug";
import {
  createFilterCache,
  defaultExpanded,
  findNode,
  flatten,
  fqName,
  GROUP_LABELS,
  leaves,
  needsChangedKeys,
  needsNewKeys,
  onStatusFilterRequest,
  QUERY_TOKENS,
  sortRanks,
  type Counts,
  type ExpandMode,
  type FilterPass,
  type Row,
  type StatusFilter,
  type TreeGroup,
  type TreeNode,
  type TreeSort,
} from "../tree";
import { changedKeys, watchChanged } from "./changed-tests";
import { newFailureKeys } from "./run-diff";
import { formatDuration, loadPref, savePref } from "../util";
import { getStore, openInEditor, testKey } from "../store";
import { acceptSnapshots, rejectSnapshots } from "../snapshots";
import { getSettings } from "../settings";
import { openBuildErrors } from "./build-errors";
import { needsDiagnostics } from "./diagnostics";
import "./tree-pane.css";

const ROW_H = 28;
const OVERSCAN = 8;
/** Run N× from the context menu (same as the detail pane's "Run 20×") */
const REPEAT = 20;

const CHIPS: { id: StatusFilter; label: string }[] = [
  { id: "all", label: "All" },
  { id: "failed", label: "Failed" },
  { id: "new", label: "New" },
  { id: "changed", label: "Changed" },
  { id: "passed", label: "Passed" },
  { id: "skipped", label: "Skipped" },
  { id: "notRun", label: "Not run" },
  { id: "flaky", label: "Flaky" },
];

/** The tree's status filter (chips). Module-level so the shell can mirror it in the URL (deep links). */
export const statusFilter = signal<StatusFilter>(loadPref<StatusFilter>("statusFilter", "all"));
/** The tree's search text (module-level: the toolbar's "Run N matching" and the palette read it). */
export const filterText = signal("");
/** Failed / Flaky filters show a flat list of tests instead of the deep tree (toggle in the filter row). */
export const flatFailures = signal<boolean>(loadPref("flatFailures", true));
const flatCapable = (f: StatusFilter) => f === "failed" || f === "flaky" || f === "new" || f === "changed";

/** Sort order of the tree (Default = source / discovery order; duration sorts rank siblings at every level). */
export const treeSort = signal<TreeSort>(loadPref<TreeSort>("treeSort", "default"));
export const SORT_LABELS: Record<TreeSort, string> = { default: "Default order", slowest: "Slowest first", fastest: "Fastest first" };
/** How the tree groups tests (the shell regroups the roots it passes in, see tree.ts regroup). */
export const treeGroup = signal<TreeGroup>(loadPref<TreeGroup>("treeGroup", "namespace"));
/** The group-by menu's descriptions. */
const GROUP_DESCRIPTIONS: Record<TreeGroup, string> = {
  namespace: "Project › Namespace › Class (default)",
  project: "Project › Class",
  trait: "TestCategory and other trait values",
  outcome: "Failed · Not run · Passed · Skipped",
};

/** Where the last whitespace-separated token of `t` starts (`t.length` when `t` ends with whitespace). */
export function lastTokenStart(t: string): number {
  let i = t.length;
  while (i > 0 && !/\s/.test(t[i - 1])) i--;
  return i;
}

/** A search value, quoted when it contains whitespace. */
const quoteIfSpaced = (v: string) => (/\s/.test(v) ? `"${v}"` : v);
/** Keys of the rows in a multi-selection (Ctrl/Cmd/Shift+click); empty = a single selection (selectedKey). */
export const multiSelection = signal<ReadonlySet<string>>(new Set());

/** The search text or status chip narrows the tree (the toolbar runs "N matching" instead of all). */
export const treeFiltered = computed(() => filterText().trim() !== "" || statusFilter() !== "all");

const filterCache = createFilterCache();
/**
 * The filter pass (tree.ts filterPass) of `roots` for the tree's search text and status chip: the rows, the chip counts
 * and the shell's "Run N matching" all come from it, so a keystroke walks the tests once (cached per roots / text /
 * status; typing on narrows the previous pass's matches). Tracked: reads the filter signals.
 */
export function treeFilterPass(roots: readonly TreeNode[]): FilterPass {
  const text = filterText();
  const status = statusFilter();
  return filterCache(roots, text, status, needsNewKeys(text, status) ? newFailureKeys() : undefined, needsChangedKeys(text, status) ? changedKeys() : undefined);
}

let activeTree: TreePane | undefined;
/** `tree` is the tree the module-level functions below act on (the last one rendered); returns its unregistration. */
function setActiveTree(tree: TreePane): () => void {
  activeTree = tree;
  return () => {
    if (activeTree === tree) activeTree = undefined;
  };
}
/** Move keyboard focus to the test tree (its active row is the aria-activedescendant). */
export function focusTree() {
  activeTree?.focusRows();
}
/** Open the context menu of the active row (palette, Shift+F10). */
export function openTreeContextMenu() {
  activeTree?.openMenuForSelection();
}
/**
 * A test picked outside the tree (a code view's test link / gutter icon): its row is revealed like a navigation —
 * ancestors expanded, scrolled (smoothly when close), one locator ring. Call right before selecting `key`.
 */
export function prepareTreeLanding(key: string) {
  const tree = activeTree;
  if (!tree) return;
  multiSelection.set(new Set());
  if (tree.selectedKey === key) {
    // already selected: no reveal will run, land now
    queueMicrotask(() => {
      const i = tree.rowIndex(key);
      if (i >= 0) tree.landOnRow(i, key);
    });
  } else tree.landing = key;
}
/** Clear the filter text (palette, "Clear filters"). */
export function clearTreeFilter() {
  batch(() => {
    filterText.set("");
    statusFilter.set("all");
  });
}

/** Where a node's source is: the test's file/line, or the first test's file for a class / method group. */
export function sourceOf(n: TreeNode): { path: string; line?: number } | undefined {
  const file = n.test?.filePath ?? (n.kind === "class" || n.kind === "method" ? firstFile(n) : undefined);
  if (!file) return undefined;
  const line = n.test?.line ?? (n.kind === "method" ? n.children[0]?.test?.line : undefined);
  return { path: file, line };
}

const KIND_LABEL = { project: "Project", namespace: "Namespace", class: "Class", method: "Method", test: "Test", group: "Group" } as const;

/** Deep link to a tree node (`#/tests?t=<projectId>|<id>`, `n=<key>` for groups), absolute. */
export function nodeLink(key: string): string {
  const q = new URLSearchParams();
  if (key.startsWith("t:")) q.set("t", key.slice(2));
  else q.set("n", key);
  return `${location.origin}${location.pathname}${location.search}#/tests?${q.toString().replace(/%7C/gi, "|")}`;
}

/** The fully qualified name of a node: Namespace.Class.Test for tests, Namespace.Class for classes, the path for projects. */
export function fqnOf(n: TreeNode): string {
  if (n.test) return fqName(n.test);
  if (n.kind === "project") return n.project?.path ?? n.label;
  const t = leaves(n)[0];
  if (n.kind === "class" && t) return [t.namespace, t.className].filter(Boolean).join(".");
  if (n.kind === "method" && t) return [t.namespace, t.className, t.methodName].filter(Boolean).join(".");
  return n.label;
}

function copyText(text: string, what: string) {
  const done = () => toast(`Copied ${what}`, { intent: "success", icon: "copy" });
  if (navigator.clipboard?.writeText) navigator.clipboard.writeText(text).then(done, () => toast(`Couldn't copy the ${what}`, { intent: "error" }));
}

/** Union of the leaves of the multi-selection (tree order, deduplicated). */
export function selectionTests(roots: readonly TreeNode[], keys: ReadonlySet<string>): TestNode[] {
  const out: TestNode[] = [];
  const seen = new Set<string>();
  const walk = (n: TreeNode, inSel: boolean) => {
    const sel = inSel || keys.has(n.key);
    if (n.test) {
      const k = testKey(n.test);
      if (sel && !seen.has(k)) (seen.add(k), out.push(n.test));
      return;
    }
    for (const c of n.children) walk(c, sel);
  };
  for (const r of roots) walk(r, false);
  return out;
}

export interface TreePaneProps {
  roots: TreeNode[];
  selectedKey: string | undefined;
  /** Coverage tab is active: double-click collects coverage instead of running / toggling */
  coverageMode: boolean;
  runDisabled: boolean;
  loaded: boolean;
  onSelect: (key: string) => void;
  onRun: (n: TreeNode) => void;
  /** run the node with coverage and show it in the Coverage tab */
  onCoverage: (n: TreeNode) => void;
  onRediscover: () => void;
  /** run these tests (multi-selection) */
  onRunTests: (tests: TestNode[]) => void;
  /** collect coverage for these tests (multi-selection) */
  onCoverageTests: (tests: TestNode[], label: string) => void;
  /** debug a test (undefined: not available) */
  onDebug: ((n: TreeNode) => void) | undefined;
}

export interface TreePane extends TreePaneProps {}

export class TreePane extends LabcoatElement<TreePaneProps> {
  static defaults: TreePaneProps = {
    roots: [],
    selectedKey: undefined,
    coverageMode: false,
    runDisabled: false,
    loaded: false,
    onSelect: () => {},
    onRun: () => {},
    onCoverage: () => {},
    onRediscover: () => {},
    onRunTests: () => {},
    onCoverageTests: () => {},
    onDebug: undefined,
  };
  static hostClass = "tree-pane";

  // ---- local state (survives a move; reset when the element is recreated, like React state) ----
  private readonly text = filterText;
  private readonly status = statusFilter;
  private readonly mode = signal<ExpandMode>("default");
  private readonly overrides = signal(new Map<string, boolean>());
  /** expansion overrides while filtering; reset whenever the filter changes */
  private readonly filterOverrides = signal(new Map<string, boolean>());
  private readonly scrollPos = signal(0);
  private readonly viewH = signal(600);
  /** context menu: the row it is for, and the pointer position (undefined = anchored to the row) */
  readonly #menuAt = signal<{ key: string; x?: number; y?: number } | undefined>(undefined);
  /** Shift+click / Shift+↑↓ range anchor */
  #anchor: string | undefined;

  private readonly filtering = treeFiltered;

  private readonly isExpanded = (n: TreeNode) => {
    const filtering = this.filtering();
    const o = (filtering ? this.filterOverrides() : this.overrides()).get(n.key);
    if (o != null) return o;
    return filtering ? true : defaultExpanded(n.kind, this.mode());
  };

  private readonly flat = computed(() => flatCapable(this.status()) && flatFailures());
  /**
   * Duration sort ranks. Frozen while a run is in flight (rows don't jump on every result); recomputed when it ends.
   */
  private lastRanks: ReturnType<typeof sortRanks>;
  private readonly ranks = computed(() => {
    const mode = treeSort();
    if (mode === "default") {
      this.lastRanks = undefined;
      return undefined;
    }
    if (getStore().$.currentRun() && this.lastRanks) return this.lastRanks;
    this.lastRanks = sortRanks(this.props.roots(), mode);
    return this.lastRanks;
  });
  /** the new-failure keys the filter uses (undefined unless it needs them; scripts/lib/tree-invariants.mjs reads it) */
  readonly newKeys = computed(() => this.pass().newKeys);
  /** the changed-test keys the filter uses (undefined unless it needs them) */
  readonly changedKeys = computed(() => this.pass().changedKeys);
  private readonly pass = computed(() => treeFilterPass(this.props.roots()));
  private readonly rows = computed(() => {
    const r = this.ranks();
    const pass = this.pass();
    return flatten(this.props.roots(), this.isExpanded, pass.text, pass.status, this.flat(), {
      newKeys: pass.newKeys,
      changedKeys: pass.changedKeys,
      rank: r?.rank,
      flatRank: r?.flatRank,
      pass,
    });
  });
  private readonly counts = computed(() => this.pass().counts);
  private readonly selIndex = computed(() => {
    const key = this.props.selectedKey();
    return this.rows().findIndex((r) => r.node.key === key);
  });

  // Window: start/end only change when the scroll position crosses a row boundary, so `visible` (and the list
  // reconciliation) re-runs once per row scrolled, not once per scroll event.
  private readonly start = computed(() => Math.max(0, Math.floor(this.scrollPos() / ROW_H) - OVERSCAN));
  private readonly end = computed(() => Math.min(this.rows().length, Math.ceil((this.scrollPos() + this.viewH()) / ROW_H) + OVERSCAN));
  /** the rows in the window, plus the active row when it is outside (aria-activedescendant must exist) */
  private readonly visible = computed(() => {
    const rows = this.rows();
    const s = this.start();
    const e = this.end();
    const out = rows.slice(s, e);
    const i = this.selIndex();
    if (i >= 0 && (i < s || i >= e)) out.push(rows[i]);
    return out;
  });

  private scroller!: HTMLDivElement;
  private search!: HTMLInputElement;
  private pointAnchor!: HTMLSpanElement;

  setText(v: string) {
    batch(() => {
      this.text.set(v);
      this.filterOverrides.set(new Map());
    });
  }

  setStatus(v: StatusFilter) {
    batch(() => {
      this.status.set(v);
      this.filterOverrides.set(new Map());
    });
  }

  rowIndex(key: string) {
    return this.rows.peek().findIndex((r) => r.node.key === key);
  }

  focusRows() {
    if (!this.scroller?.isConnected) return;
    this.scroller.focus({ preventScroll: true });
    const i = this.selIndex.peek();
    if (i >= 0) this.ensureVisible(i);
  }

  openMenuForSelection() {
    const k = this.props.selectedKey.peek();
    if (!k || this.selIndex.peek() < 0) return;
    this.ensureVisible(this.selIndex.peek());
    this.#menuAt.set({ key: k });
  }

  /**
   * P2: a user toggle (click, ←/→, Space) slides the rows that stay to their new place (FLIP on the individual
   * `translate` property, 180 ms emphasized; DOM moves by list() don't interrupt it) and rows that appear fade in from
   * 4 px above (≤ 8, 15 ms stagger). Not for expand/collapse all, filtering or auto-reveal (they don't come through
   * here), not while scrolling (new rows only animate when the list didn't scroll), not when busy with > 60 rows.
   */
  private anim = { until: 0, scroll: -1, entered: 0 };
  private toggle(n: TreeNode, value?: boolean) {
    const target = this.filtering.peek() ? this.filterOverrides : this.overrides;
    const next = new Map(target.peek());
    next.set(n.key, value ?? !untracked(() => this.isExpanded(n)));
    // under the busy gate only small toggles animate (a node with ≤ 60 tests)
    const animate = canAnimate("move") && !!this.scroller && !(isBusy() && n.counts.total > 60);
    if (!animate) return target.set(next);
    this.anim = { until: performance.now() + 220, scroll: this.scrollPos.peek(), entered: 0 };
    const moves = flip(this.scroller.querySelectorAll<HTMLElement>(".tree-row"), () => target.set(next), {
      duration: 180,
      maxDistance: this.scroller.clientHeight,
    });
    // an element recycled for an entering row is not the row that was there: it fades in (enter), it doesn't slide
    for (const a of moves) if (this.#recycled.has((a.effect as KeyframeEffect | null)?.target as HTMLElement)) a.cancel();
  }

  /** Inside a user-toggle window (and the list hasn't scrolled since): new rows get the enter animation. */
  private enteringRow(): number | undefined {
    if (performance.now() > this.anim.until || this.scrollPos.peek() !== this.anim.scroll || this.anim.entered >= 8) return undefined;
    return this.anim.entered++;
  }

  private expandAll(v: boolean) {
    batch(() => {
      this.overrides.set(new Map());
      this.filterOverrides.set(new Map());
      this.mode.set(v ? "all" : "none");
    });
  }

  // ---- multi-selection -------------------------------------------------------------------------------

  private toggleMulti(key: string) {
    const cur = new Set(multiSelection.peek());
    const sel = this.props.selectedKey.peek();
    if (!cur.size && sel && sel !== key) cur.add(sel);
    if (cur.has(key)) cur.delete(key);
    else cur.add(key);
    multiSelection.set(cur);
    this.#anchor = key;
  }

  private rangeTo(key: string) {
    const rows = this.rows.peek();
    const from = rows.findIndex((r) => r.node.key === (this.#anchor ?? this.props.selectedKey.peek()));
    const to = rows.findIndex((r) => r.node.key === key);
    if (from < 0 || to < 0) return multiSelection.set(new Set([key]));
    const [a, b] = from <= to ? [from, to] : [to, from];
    multiSelection.set(new Set(rows.slice(a, b + 1).map((r) => r.node.key)));
  }

  private clearMulti() {
    if (multiSelection.peek().size) multiSelection.set(new Set());
  }

  protected render(): Child {
    this.setAttribute("role", "region");
    this.setAttribute("aria-label", "Tests");
    onCleanup(setActiveTree(this));

    effect(() => savePref("statusFilter", this.status()));
    effect(() => savePref("flatFailures", flatFailures()));
    effect(() => savePref("treeSort", treeSort()));
    effect(() => savePref("treeGroup", treeGroup()));
    onCleanup(onStatusFilterRequest((f) => this.setStatus(f)));
    onCleanup(watchChanged()); // the Changed chip / is:changed (git; empty outside a repository)
    // a multi-selection doesn't survive a regrouping (its keys may not exist there)
    effect(() => {
      treeGroup();
      untracked(() => this.clearMulti());
    });

    // Reveal the selected node (e.g. when chosen from the detail pane's failure list).
    // Also re-checked when the tree changes until the node exists (a deep link is restored before the first snapshot).
    let revealed: string | undefined;
    effect(() => {
      const key = this.props.selectedKey();
      if (!key) return;
      if (revealed !== key) this.props.roots();
      untracked(() => {
        if (revealed === key) return;
        if (!this.rows().some((r) => r.node.key === key)) {
          const n = findNode(this.props.roots(), key);
          if (!n) return;
          const target = this.filtering() ? this.filterOverrides : this.overrides;
          const next = new Map(target());
          for (let p = n.parent; p; p = p.parent) next.set(p.key, true);
          target.set(next);
        }
        // Scroll it into view (F8, palette, deep link); ensureVisible is a no-op when it is visible already.
        const i = this.selIndex();
        if (i >= 0 && this.scroller?.clientHeight) {
          this.ensureVisible(i);
          revealed = key;
        }
      });
    });

    // "/" focuses the filter (Ctrl+K opens the command palette, see command-palette.ts).
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (e.key === "/" && !e.ctrlKey && !e.metaKey && !e.altKey && !(t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t?.isContentEditable || t?.closest("dialog"))) {
        e.preventDefault();
        this.search.focus();
        this.search.select();
      }
    };
    window.addEventListener("keydown", onKey);
    onCleanup(() => window.removeEventListener("keydown", onKey));

    this.pointAnchor = h("span", { class: "tree-menu-anchor", "aria-hidden": "true" });
    return [this.renderFilters(), this.renderSelectionBar(), this.renderScroller(), this.pointAnchor, this.renderMenu()];
  }

  // ---- filters ---------------------------------------------------------------------------------------

  private renderFilters() {
    const chipCount = (id: StatusFilter) => {
      const c = this.counts();
      switch (id) {
        case "new":
          return newFailureKeys().size;
        case "changed":
          return changedKeys().size;
        case "all":
          return c.total;
        case "flaky":
          return c.flaky;
        case "notRun":
          return c.notRun + c.queued + c.running;
        default:
          return c[id];
      }
    };
    // Zero-count chips are hidden (except All, Failed and the active one) so the row never wraps.
    const chips = computed(() => CHIPS.filter((c) => c.id === "all" || c.id === "failed" || this.status() === c.id || chipCount(c.id) > 0));
    // one tab stop: arrows / Home / End move between the chips and select (WAI-ARIA radio group)
    const onChipKey = (e: KeyboardEvent) => {
      const els = [...(e.currentTarget as HTMLElement).querySelectorAll<HTMLButtonElement>(".chip")];
      const i = rovingIndex(e, els.length, els.indexOf(e.target as HTMLButtonElement), "horizontal");
      if (i === undefined) return;
      e.preventDefault();
      const id = chips.peek()[i]?.id;
      if (!id) return;
      this.setStatus(id);
      els[i].focus();
    };

    const sortBtn = menuButton({
      label: "Sort tests",
      icon: "sort",
      iconOnly: true,
      items: () =>
        (["default", "slowest", "fastest"] as const).map((m) => ({
          label: SORT_LABELS[m],
          type: "radio" as const,
          checked: treeSort() === m,
          onSelect: () => treeSort.set(m),
        })),
    });
    // the button says what it sorts by (#26)
    effect(() => {
      const l = `Sort tests: ${SORT_LABELS[treeSort()].toLowerCase()}`;
      sortBtn.title = l;
      sortBtn.setAttribute("aria-label", l);
    });
    const groupBtn = menuButton({
      label: "Group tests",
      icon: "groupList",
      iconOnly: true,
      items: () => [
        { heading: "Group by" },
        ...(["namespace", "project", "trait", "outcome"] as const).map((g) => ({
          label: GROUP_LABELS[g],
          type: "radio" as const,
          checked: treeGroup() === g,
          description: GROUP_DESCRIPTIONS[g],
          onSelect: () => treeGroup.set(g),
        })),
      ],
    });
    effect(() => {
      const l = `Group tests by ${GROUP_LABELS[treeGroup()].toLowerCase()}`;
      groupBtn.title = l;
      groupBtn.setAttribute("aria-label", l);
    });

    return h(
      "div",
      { class: "tree-filters" },
      h(
        "div",
        { class: "search-row" },
        this.renderSearch(),
        show(
          () => flatCapable(this.status()),
          () =>
            iconButton({
              icon: "taskList",
              label: () => (flatFailures() ? "Show as tree" : "Show as flat list"),
              toggled: flatFailures,
              "aria-pressed": () => flatFailures(),
              onclick: () => flatFailures.update((v) => !v),
            }),
        ),
        h("span", { class: () => cx("tree-sort", treeSort() !== "default" && "active") }, sortBtn),
        h("span", { class: () => cx("tree-sort", treeGroup() !== "namespace" && "active") }, groupBtn),
        iconButton({ icon: "expandAll", label: "Expand all", onclick: () => this.expandAll(true) }),
        iconButton({ icon: "collapseAll", label: "Collapse all", onclick: () => this.expandAll(false) }),
      ),
      h(
        "div",
        { class: "chips", role: "radiogroup", "aria-label": "Status filter", onkeydown: onChipKey },
        list(
          chips,
          (c) => c.id,
          (c) => {
            const { id, label } = c.peek();
            return chip({
              id,
              label,
              count: () => chipCount(id),
              active: () => this.status() === id,
              tabIndex: () => (this.status() === id ? 0 : -1),
              onclick: () => this.setStatus(id),
              title:
                id === "new"
                  ? "New failures: failing now, passed in the previous full run (or a new test)"
                  : id === "changed"
                    ? "Tests added or modified on this branch: since it left the base branch, plus uncommitted changes"
                    : undefined,
            });
          },
        ),
      ),
    );
  }

  /** The search box with the token syntax hint (shown while focused: all tokens when empty, values after `is:` …). */
  private renderSearch() {
    const focused = signal(false);
    // the token list (empty box) shows once per focus, until the first keystroke: it never sits over the chips while
    // someone is filtering by status
    const armed = signal(false);
    const hintId = "tree-search-syntax";
    const hint = computed(() => {
      if (!focused()) return undefined;
      const t = this.text();
      const last = t.slice(lastTokenStart(t)).toLowerCase();
      if (!t.trim()) {
        if (!armed()) return undefined;
        return { kind: "tokens" as const, items: QUERY_TOKENS.map((q) => ({ insert: q.token, label: q.example, what: q.what })) };
      }
      const values = (prefix: string, vals: string[]) => ({ kind: "values" as const, items: vals.slice(0, 9).map((v) => ({ insert: `${prefix}${quoteIfSpaced(v)} `, label: `${prefix}${v}`, what: "" })) });
      if (last === "is:") return values("is:", ["failed", "passed", "skipped", "flaky", "new", "changed", "slow", "notrun", "snapshot"]);
      if (last === "cat:") return values("cat:", [...new Set([...getStore().get().tests.values()].flatMap((x) => (x.traits ?? []).map((tr) => tr.value)))].sort((x, y) => x.localeCompare(y)));
      if (last === "proj:") return values("proj:", getStore().get().projects.map((p) => p.name));
      return undefined;
    });
    const insert = (s: string) => {
      const t = this.text.peek();
      const start = lastTokenStart(t);
      const last = t.slice(start);
      const next = t.slice(0, start) + (s.startsWith(last.toLowerCase()) || last.endsWith(":") ? s : `${last} ${s}`);
      this.setText(next);
      this.search.value = next;
      this.search.focus();
    };
    return h(
      "label",
      { class: "search-box" },
      icon("search", { size: 16 }),
      h("input", {
        ref: (el) => (this.search = el),
        type: "search",
        placeholder: "Filter tests",
        value: () => this.text(),
        oninput: (e) => {
          armed.set(false);
          this.setText((e.currentTarget as HTMLInputElement).value);
        },
        onfocus: () =>
          batch(() => {
            focused.set(true);
            armed.set(true);
          }),
        onblur: () => focused.set(false),
        onkeydown: (e) => {
          if (e.key === "Escape") {
            armed.set(false);
            if (this.text.peek()) e.preventDefault();
            this.setText("");
          }
          if (e.key === "ArrowDown") {
            e.preventDefault();
            this.scroller.focus();
            const rows = this.rows.peek();
            if (rows.length && this.selIndex.peek() < 0) this.onSelect(rows[0].node.key);
          }
        },
        spellcheck: false,
        "aria-label": "Filter tests",
        "aria-describedby": hintId,
      }),
      h("span", { id: hintId, class: "visually-hidden" }, "Name, class or namespace; or tokens: is:failed, passed, skipped, flaky, new, slow or snapshot; cat: a trait value; trait: key=value; msg: text in the error or output; proj: a project."),
      show(
        () => this.text(),
        () =>
          h(
            "button",
            { class: "search-clear", type: "button", onclick: () => this.setText(""), title: "Clear filter", "aria-label": "Clear filter" },
            icon("dismiss", { size: 14 }),
          ),
        () => h("kbd", { class: "search-kbd" }, "/"),
      ),
      show(hint, (hn) =>
        h(
          "div",
          // mouse shortcut only: keyboard users type the tokens (the input's description lists them)
          { class: "search-hint", "aria-hidden": "true", onmousedown: (e: MouseEvent) => e.preventDefault() },
          () =>
            hn().items.map((it) =>
              h(
                "button",
                { type: "button", class: "search-hint-item", tabIndex: -1, onclick: (e: MouseEvent) => {
                    e.preventDefault();
                    insert(it.insert);
                  },
                },
                h("code", null, it.label),
                it.what ? h("span", { class: "search-hint-what" }, it.what) : null,
              ),
            ),
        ),
      ),
    );
  }

  // ---- selection bar ------------------------------------------------------------------------------------

  private renderSelectionBar() {
    const count = computed(() => multiSelection().size);
    const tests = () => selectionTests(this.props.roots.peek(), multiSelection.peek());
    return show(
      () => count() > 1,
      () =>
        h(
          "div",
          { class: "tree-selbar", role: "toolbar", "aria-label": "Selected tests" },
          h("span", { class: "selbar-count" }, () => `${count()} selected`),
          button(
            { size: "small", icon: "play", disabled: this.props.runDisabled, onclick: () => this.onRunTests(tests()), title: "Run the selected tests" },
            () => ` Run ${selectionTests(this.props.roots(), multiSelection()).length}`,
          ),
          button(
            { size: "small", icon: "shieldCheck", disabled: this.props.runDisabled, onclick: () => this.onCoverageTests(tests(), `${count.peek()} selected`), title: "Collect coverage for the selected tests" },
            " Collect coverage",
          ),
          button(
            {
              size: "small",
              variant: "link",
              onclick: () => {
                this.clearMulti();
                this.focusRows();
              },
              title: "Clear the selection (Esc)",
            },
            "Clear",
          ),
        ),
    );
  }

  // ---- context menu ----------------------------------------------------------------------------------------

  private renderMenu() {
    return menu({
      open: () => !!this.#menuAt(),
      anchor: () => {
        const at = this.#menuAt.peek();
        if (!at) return undefined;
        if (at.x != null && at.y != null) {
          this.pointAnchor.style.left = `${at.x}px`;
          this.pointAnchor.style.top = `${at.y}px`;
          return this.pointAnchor;
        }
        return this.querySelector<HTMLElement>(`#${rowId(at.key)}`) ?? this.scroller;
      },
      onClose: () => {
        this.#menuAt.set(undefined);
        // back to the tree unless the action moved focus somewhere on purpose (a dialog, the detail pane)
        queueMicrotask(() => {
          const a = document.activeElement;
          if (!a || a === document.body || a === this.pointAnchor || a.closest(".tree-context-menu")) this.focusRows();
        });
      },
      label: "Test actions",
      placement: "bottom-start",
      class: "tree-context-menu",
      items: () => {
        const at = this.#menuAt();
        return at ? untracked(() => this.menuItems(at.key)) : [];
      },
    });
  }

  private menuItems(key: string): MenuEntry[] {
    const n = findNode(this.props.roots.peek(), key);
    if (!n) return [];
    const multi = multiSelection.peek();
    if (multi.size > 1 && multi.has(key)) return this.multiMenuItems(multi);
    const busy = this.props.runDisabled.peek();
    const blocked = projectOf(n)?.status === "error";
    const kind = KIND_LABEL[n.kind].toLowerCase();
    const t = n.test;
    const items: MenuEntry[] = [
      { label: t ? "Run test" : `Run ${kind} (${n.counts.total})`, icon: "play", shortcut: "Enter", disabled: busy || blocked || !n.counts.total, onSelect: () => this.onRun(n) },
    ];
    const debug = this.props.onDebug.peek();
    if (debug && debugAvailable() && (t || n.kind === "method"))
      items.push({ label: "Debug test", icon: "bug", description: "Start it paused until a debugger attaches", disabled: busy || blocked, onSelect: () => debug(n) });
    items.push({ label: "Collect coverage", icon: "shieldCheck", shortcut: "C", disabled: busy || blocked || !n.counts.total, onSelect: () => this.onCoverage(n) });
    if (t) items.push(repeatMenuItem(t, busy || blocked));
    items.push({ separator: true }, ...openAndCopyMenuItems(n));
    if (t) items.push({ separator: true }, quarantineMenuItem(t));
    if (t?.hasSnapshotMismatch) items.push({ separator: true }, ...snapshotMenuItems(t, busy));
    const pid = n.kind === "group" ? undefined : n.projectId;
    if (pid)
      items.push(
        ...(t ? [] : [{ separator: true } as MenuEntry]),
        {
          label: "Restart test host",
          icon: "arrowReset",
          description: "Fresh process: fixtures start again on the next run",
          disabled: busy,
          onSelect: () => getStore().send({ type: "restartHost", projectId: pid }),
        },
      );
    return items;
  }

  /** The context menu of a row in a multi-selection: actions on all selected tests. */
  private multiMenuItems(multi: ReadonlySet<string>): MenuEntry[] {
    const busy = this.props.runDisabled.peek();
    const tests = selectionTests(this.props.roots.peek(), multi);
    const nTests = plural(tests.length, "test");
    return [
      { heading: `${multi.size} selected · ${nTests}` },
      { label: `Run ${nTests}`, icon: "play", disabled: busy || !tests.length, onSelect: () => this.onRunTests(tests) },
      { label: "Collect coverage", icon: "shieldCheck", disabled: busy || !tests.length, onSelect: () => this.onCoverageTests(tests, `${multi.size} selected`) },
      { separator: true },
      { label: "Clear selection", icon: "dismiss", shortcut: "Escape", onSelect: () => this.clearMulti() },
    ];
  }

  // ---- scroller + rows ---------------------------------------------------------------------------------

  private renderScroller() {
    const p = this.props;
    const scroller = h(
      "div",
      {
        class: "tree-scroller",
        ref: (el) => (this.scroller = el),
        tabIndex: 0,
        // a tree only while it shows rows: an empty state (no projects, only VSTest projects) has no tree items
        role: () => (this.contentKind() === "rows" ? "tree" : "region"),
        "aria-label": "Test tree",
        "aria-multiselectable": () => (this.contentKind() === "rows" ? "true" : undefined),
        "aria-activedescendant": () => {
          const k = p.selectedKey();
          return k && this.selIndex() >= 0 ? rowId(k) : undefined;
        },
        onscroll: (e) => this.scrollPos.set((e.currentTarget as HTMLElement).scrollTop),
        onkeydown: (e) => this.onKeyDown(e),
      },
      keyed(
        () => this.contentKind(),
        (kind) =>
          kind === "rows"
            ? h(
                "div",
                { style: { height: () => this.rows().length * ROW_H, position: "relative" } },
                this.renderRows(this.visible),
              )
            : this.renderEmpty(kind),
      ),
    );
    const ro = new ResizeObserver(() => this.viewH.set(scroller.clientHeight));
    ro.observe(scroller);
    onCleanup(() => ro.disconnect());
    return scroller;
  }

  /** What the scroller shows: the rows, or an empty state (loading, nothing matches, no tests). */
  private contentKind(): "rows" | "loading" | "filtered" | "none" {
    // only projects that aren't on Microsoft.Testing.Platform: the empty state lists them with the switch
    if (this.rows().length > 0 && !onlyNotOnMtp(this.props.roots())) return "rows";
    if (!this.props.loaded()) return "loading";
    return this.filtering() ? "filtered" : "none";
  }

  private onKeyDown(e: KeyboardEvent) {
    const rows = this.rows.peek();
    if (!rows.length) return;
    const selIndex = this.selIndex.peek();
    const cur = selIndex >= 0 ? rows[selIndex] : undefined;
    // Shift+F10 / the ContextMenu key: the row's context menu
    if (e.key === "ContextMenu" || (e.key === "F10" && e.shiftKey)) {
      e.preventDefault();
      if (!cur && rows.length) this.onSelect(rows[0].node.key);
      queueMicrotask(() => this.openMenuForSelection());
      return;
    }
    const handled = NAV_KEYS.has(e.key) ? this.onNavKey(e, rows, selIndex, cur) : this.onActionKey(e, cur);
    if (handled) e.preventDefault();
  }

  /** Select row `i` (clamped); `extend`: extend the multi-selection from the anchor (Shift). */
  private goToRow(rows: readonly Row[], cur: Row | undefined, i: number, extend = false) {
    const j = Math.max(0, Math.min(rows.length - 1, i));
    const key = rows[j].node.key;
    if (extend) {
      if (!this.#anchor || !multiSelection.peek().size) this.#anchor = cur?.node.key ?? key;
      this.rangeTo(key);
    } else this.clearMulti();
    this.onSelect(key);
    this.ensureVisible(j);
  }

  /** Arrow keys, Home / End, Page Up / Down; false when the key is left to the browser. */
  private onNavKey(e: KeyboardEvent, rows: readonly Row[], selIndex: number, cur: Row | undefined): boolean {
    const page = Math.floor(this.viewH.peek() / ROW_H);
    const go = (i: number, extend = false) => this.goToRow(rows, cur, i, extend);
    switch (e.key) {
      case "ArrowDown":
        go(selIndex + 1, e.shiftKey);
        return true;
      case "ArrowUp":
        go(selIndex < 0 ? 0 : selIndex - 1, e.shiftKey);
        return true;
      case "Home":
        go(0, e.shiftKey);
        return true;
      case "End":
        go(rows.length - 1, e.shiftKey);
        return true;
      case "PageDown":
        go(selIndex + page, e.shiftKey);
        return true;
      case "PageUp":
        go(selIndex - page, e.shiftKey);
        return true;
      case "ArrowRight":
        if (!cur) {
          go(0);
          return false;
        }
        if (cur.hasChildren && !cur.expanded) this.toggle(cur.node, true);
        else if (cur.hasChildren) go(selIndex + 1);
        return true;
      default: // ArrowLeft
        if (!cur) return false;
        if (cur.hasChildren && cur.expanded) this.toggle(cur.node, false);
        else this.goToParent(rows, cur);
        return true;
    }
  }

  /** ArrowLeft on a collapsed row or a leaf: select its parent row. */
  private goToParent(rows: readonly Row[], cur: Row) {
    const parentKey = cur.node.parent?.key;
    if (parentKey === undefined) return;
    const pi = rows.findIndex((r) => r.node.key === parentKey);
    if (pi >= 0) this.goToRow(rows, cur, pi);
  }

  /** Space, Escape, Enter / R, O, C on the active row; false when the key is left to the browser (or the shell). */
  private onActionKey(e: KeyboardEvent, cur: Row | undefined): boolean {
    switch (e.key) {
      case " ":
        this.onSpaceKey(e, cur);
        return true;
      case "Escape":
        // a multi-selection first (the shell's Escape = back to live only when there is nothing to clear here)
        if (!multiSelection.peek().size) return false;
        this.clearMulti();
        e.stopPropagation();
        return true;
      case "Enter":
      case "r":
      case "R":
        return this.onRunKey(e, cur);
      case "o":
      case "O":
        if (!singleKeyShortcut(e)) return false;
        if (cur) openSource(cur.node);
        return true;
      case "c":
      case "C":
        if (!singleKeyShortcut(e)) return false;
        if (cur && this.canRun(cur.node)) this.onCoverage(cur.node);
        return true;
      default:
        return false;
    }
  }

  /** Space toggles the row's expansion; Ctrl/Cmd+Space adds it to (or removes it from) the multi-selection. */
  private onSpaceKey(e: KeyboardEvent, cur: Row | undefined) {
    if (!cur) return;
    if (e.ctrlKey || e.metaKey) this.toggleMulti(cur.node.key);
    else if (cur.hasChildren) this.toggle(cur.node);
  }

  /** Enter (or R): run the multi-selection, else the active row. */
  private onRunKey(e: KeyboardEvent, cur: Row | undefined): boolean {
    if (e.ctrlKey || e.metaKey || e.altKey) return false;
    if (e.key !== "Enter" && !getSettings().singleKeyShortcuts) return false;
    if (multiSelection.peek().size > 1) {
      if (!this.props.runDisabled.peek()) this.onRunTests(selectionTests(this.props.roots.peek(), multiSelection.peek()));
    } else if (cur && this.canRun(cur.node)) this.onRun(cur.node);
    return true;
  }

  /** No run in progress and the node's project built. */
  private canRun(n: TreeNode): boolean {
    return !this.props.runDisabled.peek() && n.project?.status !== "error";
  }

  private ensureVisible(i: number) {
    const el = this.scroller;
    if (!el) return;
    const key = this.rows.peek()[i]?.node.key;
    if (key && this.landing === key) return this.landOnRow(i, key);
    const top = i * ROW_H;
    if (top < el.scrollTop) el.scrollTop = top;
    else if (top + ROW_H > el.scrollTop + el.clientHeight) el.scrollTop = top + ROW_H - el.clientHeight;
  }

  /** the next reveal of this key is a "landing" (a test picked outside the tree): smooth scroll if close + locator ring */
  landing: string | undefined;

  /** Scroll row `i` into view; a landing scrolls smoothly when it is within 2 viewports (MOTION.md) and rings the row. */
  landOnRow(i: number, key: string) {
    const el = this.scroller;
    this.landing = undefined;
    const top = Math.max(0, i * ROW_H - Math.round(el.clientHeight / 3));
    const visible = i * ROW_H >= el.scrollTop && (i + 1) * ROW_H <= el.scrollTop + el.clientHeight;
    if (!visible) scrollToTop(el, top);
    const ring = () => {
      const row = this.querySelector<HTMLElement>(`#${rowId(key)}`);
      if (row) locate(row);
    };
    // after the (smooth) scroll settled and the row exists
    window.setTimeout(() => requestAnimationFrame(ring), visible ? 0 : 260);
  }

  private renderEmpty(kind: "loading" | "filtered" | "none"): Child {
    if (kind === "loading") return h("div", { class: "empty-state small" }, h("span", { class: "spinner" }), h("span", "Loading tests…"));
    if (kind === "filtered")
      return h(
        "div",
        { class: "empty-state small" },
        icon("search", { size: 24 }),
        h("div", { class: "empty-title" }, "No matching tests"),
        h(
          "button",
          {
            class: "btn btn-link",
            onclick: () =>
              batch(() => {
                this.setText("");
                this.setStatus("all");
              }),
          },
          "Clear filters",
        ),
      );
    const notOnMtp = onlyNotOnMtp(this.props.roots.peek());
    if (notOnMtp) return notOnMtpProjects(notOnMtp, () => this.onRediscover());
    // RUNS' diagnostics (P2-3) instead of a bare "no tests" when a project explains why
    const diag = getStore().get().projects.filter((p) => needsDiagnostics(p, 0));
    if (diag.length)
      return h(
        "div",
        { class: "tree-diagnostics" },
        diag.map((p) => h("lc-project-diagnostics", { projectId: p.id })),
      );
    const st = getStore().get();
    if (st.loaded && st.projects.length === 0) return noTestProjects(st.sourceRoots?.[0], () => this.onRediscover());
    return h(
      "div",
      { class: "empty-state small" },
      icon("beaker", { size: 28 }),
      h("div", { class: "empty-title" }, "No tests discovered"),
      h("div", { class: "empty-sub" }, "Build the solution and discover tests to populate the explorer."),
      h("button", { class: "btn", title: "Rebuild and rediscover tests", onclick: () => this.onRediscover() }, icon("arrowSync"), " Rediscover"),
    );
  }

  /**
   * The rendered rows (the window + the pinned active row), in order. Like `list()` keyed by node key: a row that stays
   * in the window keeps its element (a tree rebuild only pushes the new Row into it; FLIP moves it). Rows that leave are
   * not disposed: their element, with all its bindings, is handed to a row that enters in the same update and patched
   * (an attribute or text node per changed value) instead of ~25 elements and bindings being built and torn down per
   * row. Scrolling 240 px moves ~9 elements from one end to the other. Leftover elements (the window shrank) are
   * disposed, so nothing outlives the update that freed it.
   */
  private renderRows(items: ReadSignal<Row[]>): DocumentFragment {
    interface Slot {
      el: HTMLElement;
      row: Signal<Row>;
      dispose: () => void;
    }
    const frag = document.createDocumentFragment();
    const end = document.createComment("/rows");
    frag.append(end);
    const owner = getOwner();
    let live = new Map<string, Slot>();
    const create = (r: Row): Slot =>
      root((dispose) => {
        const row = signal(r);
        return { el: this.renderRow(row), row, dispose };
      }, owner);

    effect(() => {
      const arr = items();
      untracked(() => {
        const next = new Map<string, Slot>();
        const entering: Row[] = [];
        for (const r of arr) {
          const k = r.node.key;
          const s = live.get(k);
          if (s) {
            live.delete(k);
            next.set(k, s);
          } else if (!next.has(k)) entering.push(r);
        }
        const free = [...live.values()];
        const recycled = new Set<HTMLElement>();
        const enter: [HTMLElement, number][] = [];
        batch(() => {
          for (const r of arr) next.get(r.node.key)?.row.set(r);
          for (const r of entering) {
            let s = free.pop();
            if (s) {
              const was = s.row.peek();
              // a FLIP slide of the old row (a toggle < 220 ms ago) must not carry over
              if (performance.now() < this.anim.until) for (const a of s.el.getAnimations()) a.cancel();
              // transitions would start where a state the CSS animates differs: open twisty, selected / multi row
              const sel = (k: string) => this.props.selectedKey.peek() === k || multiSelection.peek().has(k);
              recycle(s.el, (was.hasChildren && r.hasChildren && was.expanded !== r.expanded) || sel(was.node.key) !== sel(r.node.key));
              recycled.add(s.el);
              s.row.set(r);
            } else s = create(r);
            next.set(r.node.key, s);
            const i = this.enteringRow();
            if (i !== undefined) enter.push([s.el, i]);
          }
        });
        for (const s of free) {
          s.el.remove();
          s.dispose();
        }
        live = next;
        this.#recycled = recycled;
        // place: walk backwards, moving an element only when it is not already right before the next one
        let ref: ChildNode = end;
        for (let i = arr.length - 1; i >= 0; i--) {
          const el = next.get(arr[i].node.key)!.el;
          if (el.nextSibling !== ref) ref.before(el);
          ref = el;
        }
        // after the bindings ran (the class binding rewrites className)
        for (const [el, i] of enter) animateEnter(el, "row", { index: i });
      });
    });
    onCleanup(() => {
      for (const s of live.values()) s.dispose();
      live.clear();
    });
    return frag;
  }

  /** elements the last row update gave to another row (a FLIP toggle must not slide them from their old row's place) */
  #recycled = new Set<HTMLElement>();

  /**
   * One row. `row` is the slot's row accessor: it gets a new Row object whenever the tree is rebuilt (test updates,
   * expand/collapse) and another node's row when the element is recycled (renderRows); each binding below re-applies
   * only if its own value changed. Everything, including the key, the kind and the id, comes from `row`.
   */
  private renderRow(row: ReadSignal<Row>): HTMLElement {
    const p = this.props;
    const node = () => row().node;
    const key = computed(() => row().node.key);
    const kind = computed(() => row().node.kind);
    const isProject = computed(() => kind() === "project");
    const blocked = () => node().project?.status === "error";
    const inMulti = () => multiSelection().has(key());
    const kindLabel = () => KIND_LABEL[kind()].toLowerCase();

    const el = h(
      "div",
      {
        id: () => rowId(key()),
        role: "treeitem",
        "aria-selected": () => p.selectedKey() === key() || inMulti(),
        "aria-expanded": () => (row().hasChildren ? row().expanded : undefined),
        "aria-level": () => row().depth + 1,
        "aria-posinset": () => row().pos,
        "aria-setsize": () => row().size,
        // the name a screen reader speaks comes from the content: the visible label first, then a visually hidden
        // ", passed, 4 ms" / ", 1 failed of 7" (rowExtra); the meta column and the hover actions are aria-hidden
        "data-name": () => rowLabel(row()),
        class: () => {
          const n = node();
          return cx(
            "tree-row",
            `kind-${n.kind}`,
            p.selectedKey() === n.key && "selected",
            inMulti() && "multi",
            n.status === "failed" && "is-failed",
            n.test?.stale && "is-stale",
            n.test?.notInRun && "not-in-run",
            n.pending && "is-pending",
            n.outdated && "is-outdated",
            row().flat && "is-flat",
          );
        },
        // individual `translate` (not top): transitionable during a toggle and independent of enter transforms (P2)
        style: { translate: () => `0 ${row().index * ROW_H}px`, paddingLeft: () => 6 + row().depth * 16 },
        onclick: (e: MouseEvent) => {
          const k = key.peek();
          if (e.ctrlKey || e.metaKey) this.toggleMulti(k);
          else if (e.shiftKey) this.rangeTo(k);
          else {
            this.clearMulti();
            this.#anchor = k;
          }
          this.onSelect(k);
        },
        oncontextmenu: (e: MouseEvent) => {
          e.preventDefault();
          const k = key.peek();
          // right-click outside the multi-selection selects just that row
          if (!multiSelection.peek().has(k)) {
            this.clearMulti();
            this.onSelect(k);
          }
          this.scroller.focus({ preventScroll: true });
          this.#menuAt.set({ key: k, x: e.clientX, y: e.clientY });
        },
        // Double-click always RUNS a test (coverage is the C key / row action); groups expand / collapse.
        ondblclick: (e) => {
          if ((e.target as HTMLElement).closest(".twisty.has, .row-actions, .row-pill")) return;
          const n = row.peek().node;
          if (row.peek().hasChildren) this.toggle(n);
          else if (!p.runDisabled.peek() && n.project?.status !== "error" && n.test) this.onRun(n);
        },
      },
      h(
        "span",
        {
          class: () => cx("twisty", row().hasChildren && "has", row().expanded && "open"),
          onclick: (e) => {
            if (!row.peek().hasChildren) return;
            e.stopPropagation();
            this.toggle(row.peek().node);
          },
        },
        show(
          () => row().hasChildren,
          () => icon("chevronRight", { size: 12, class: "lc-caret" }),
        ),
      ),
      h("span", { class: "row-status", "aria-hidden": "true" }, rowStatusIcon(node)),
      h(
        "span",
        {
          class: "row-label",
          // a tooltip only when the name is cut off (or there is a note): never a duplicate of the visible text, and
          // never over the hover actions (#25)
          onmouseenter: (e: MouseEvent) => {
            const lbl = e.currentTarget as HTMLElement;
            const n = row.peek().node;
            const note = rowNote(n);
            const cut = lbl.scrollWidth > lbl.clientWidth + 1;
            const full = n.test ? fqName(n.test) : n.label;
            if (cut || note) lbl.title = [cut ? full : "", note].filter(Boolean).join("\n");
            else lbl.removeAttribute("title");
          },
        },
        show(
          isProject,
          () => h("span", { class: "proj-label" }, () => node().label),
          () => () => node().label,
        ),
        show(
          () => (row().flat ? flatContext(node()) : node().sublabel),
          (sub) => h("span", { class: "row-sublabel" }, sub),
        ),
        h("span", { class: "visually-hidden" }, () => rowExtra(row())),
      ),
      h("span", { class: "row-meta", "aria-hidden": "true" }, renderMeta(row)),
      h(
        "span",
        // mouse shortcuts (out of the tab order): the keyboard has Enter / C / O and the context menu
        { class: "row-actions", "aria-hidden": "true", onclick: (e) => e.stopPropagation() },
        // the "N build errors" pill stays clickable on hover / selection (the meta column is hidden then)
        show(isProject, () =>
          keyed(
            () => (node().project?.status === "error" ? (node().project?.buildErrorCount ?? node().project?.buildErrors?.filter((d) => d.severity === "error").length ?? 0) : 0),
            (errs) =>
              errs > 0
                ? h(
                    "button",
                    {
                      class: "row-pill err",
                      title: "Show the build errors",
                      tabIndex: -1,
                      onclick: () => openBuildErrors(row.peek().node.projectId),
                    },
                    plural(errs, "build error"),
                  )
                : null,
          ),
        ),
        show(
          () => isProject() && !node().project?.testPlatform,
          () =>
          h(
            "button",
            {
              class: "row-action restart",
              title: () => (p.runDisabled() ? "A run is in progress" : "Restart test host (fresh process; fixtures start again on the next run)"),
              "aria-label": "Restart test host",
              tabIndex: -1,
              disabled: () => p.runDisabled(),
              onclick: () => getStore().send({ type: "restartHost", projectId: row.peek().node.projectId }),
            },
            icon("arrowReset", { size: 16 }),
          ),
        ),
        show(
          () => !!sourceOf(node()),
          () =>
            h(
              "button",
              {
                class: "row-action",
                title: "Open in editor (O)",
                "aria-label": "Open in editor",
                tabIndex: -1,
                onclick: () => openSource(row.peek().node),
              },
              icon("code", { size: 16 }),
            ),
        ),
        h(
          "button",
          {
            class: "row-action cov",
            title: () => (p.runDisabled() ? "A run is in progress" : `Collect coverage for this ${kindLabel()} (C)`),
            "aria-label": "Collect coverage",
            tabIndex: -1,
            // nothing to run in a project without tests (its diagnostics say why)
            hidden: () => node().counts.total === 0,
            disabled: () => p.runDisabled() || blocked(),
            onclick: () => this.onCoverage(row.peek().node),
          },
          icon("shieldCheck", { size: 16 }),
        ),
        h(
          "button",
          {
            class: "row-action run",
            title: () => (p.runDisabled() ? "A run is in progress" : `Run ${kindLabel()} (Enter)`),
            "aria-label": "Run",
            tabIndex: -1,
            hidden: () => node().counts.total === 0,
            disabled: () => p.runDisabled() || blocked(),
            onclick: () => this.onRun(row.peek().node),
          },
          icon("play", { size: 16 }),
        ),
      ),
    );
    // P6: a test whose result changes class (passed → failed: regressed, failed → passed: fixed) flashes once. A row
    // that comes into view (a new or recycled element) starts from its current outcome, so it never flashes for old news.
    let settledKey: string | undefined;
    let settled: ReturnType<typeof settledOf>;
    effect(() => {
      const n = node();
      const s = settledOf(n.test?.outcome);
      if (n.key !== settledKey) {
        settledKey = n.key;
        settled = s;
        return;
      }
      if (!s) return;
      if (settled && s !== settled && (s === "failed" || settled === "failed") && s !== "skipped" && settled !== "skipped")
        flash(el, s === "failed" ? "bad" : "good");
      settled = s;
    });
    return el;
  }
}

/**
 * A recycled row element gets another node: drop what belonged to the old one (its flash / locator / enter classes, the
 * label's tooltip). `settle`: also suppress transitions for a frame (the twisty would turn, the hover actions fade, as if
 * the old row had changed).
 */
function recycle(el: HTMLElement, settle: boolean) {
  el.classList.remove("lc-flash-bad", "lc-flash-good", "lc-flash-accent", "lc-locator", "lc-row-enter");
  (el.children[2] as HTMLElement | undefined)?.removeAttribute("title"); // .row-label
  if (!settle) return;
  el.dataset.recycled = "";
  requestAnimationFrame(() => {
    delete el.dataset.recycled;
  });
}

/**
 * The row's status icon. It pops (.st-anim, like the design system's reactive statusIcon) only when the same node's
 * status changes; a recycled row with another node shows its status without a pop (and keeps the icon element when the
 * status is the same).
 */
function rowStatusIcon(node: () => TreeNode): Child {
  const sig = computed(() => {
    const n = node();
    return `${n.status}\u0000${n.key}`;
  });
  let shownKey: string | undefined;
  let shownStatus: string | undefined;
  let plain: HTMLElement | undefined;
  return () => {
    sig();
    return untracked(() => {
      const n = node();
      const same = n.key === shownKey;
      shownKey = n.key;
      if (plain && n.status === shownStatus) return plain; // another node, same status: same icon
      const pop = same && !!plain;
      shownStatus = n.status;
      plain = statusIcon(n.status) as HTMLElement;
      return pop ? h("span", { class: "st-anim" }, plain) : plain;
    });
  };
}

/** The outcome class a result settles in (undefined while queued / running / not run). */
function settledOf(o: string | undefined): "passed" | "failed" | "skipped" | undefined {
  switch (o) {
    case "passed":
      return "passed";
    case "failed":
    case "timedOut":
      return "failed";
    case "skipped":
      return "skipped";
    default:
      return undefined;
  }
}

/** "Run 20×" (flaky hunting) of a test's context menu. */
function repeatMenuItem(t: TestNode, disabled: boolean): MenuEntry {
  return {
    label: `Run ${REPEAT}×`,
    icon: "arrowRepeat",
    description: "Flaky hunting: pass rate and duration spread",
    disabled,
    onSelect: () => getStore().send({ type: "run", projectId: t.projectId, testIds: [t.id], repeat: REPEAT } satisfies DetailCommand as ClientCommand),
  };
}

/** "Open in editor" and the copy actions of a node's context menu. */
function openAndCopyMenuItems(n: TreeNode): MenuEntry[] {
  const src = sourceOf(n);
  const isProject = n.kind === "project";
  return [
    { label: "Open in editor", icon: "code", shortcut: "O", disabled: !src, onSelect: () => src && void openInEditor(src.path, src.line) },
    { label: "Copy name", icon: "copy", onSelect: () => copyText(n.test ? n.test.displayName : n.label, "name") },
    { label: isProject ? "Copy project path" : "Copy fully qualified name", icon: "copy", onSelect: () => copyText(fqnOf(n), isProject ? "path" : "fully qualified name") },
    { label: "Copy link", icon: "link", onSelect: () => copyText(nodeLink(n.key), "link") },
  ];
}

/** Quarantine / release of a test's context menu. */
function quarantineMenuItem(t: TestNode): MenuEntry {
  const store = getStore();
  const quarantined = !!store.get().quarantined?.includes(testKey(t));
  return {
    label: quarantined ? "Release from quarantine" : "Quarantine",
    icon: "shieldProhibited",
    description: quarantined ? "Count its result toward passed/failed again" : "Keep running it, but don't let it turn the suite red",
    onSelect: () => store.send({ type: "setQuarantine", projectId: t.projectId, id: t.id, quarantined: !quarantined } satisfies DetailCommand as unknown as ClientCommand),
  };
}

/** Accept / reject of a test with a snapshot mismatch. */
function snapshotMenuItems(t: TestNode, busy: boolean): MenuEntry[] {
  return [
    { label: "Accept snapshot", icon: "checkmark", description: "Received → verified, then re-run the test", disabled: busy, onSelect: () => void acceptSnapshots({ projectId: t.projectId, id: t.id }) },
    { label: "Reject snapshot", icon: "dismiss", description: "Delete the received file", disabled: busy, onSelect: () => void rejectSnapshots({ projectId: t.projectId, id: t.id }) },
  ];
}

/** Open a node's source in the editor (when it has one). */
function openSource(n: TreeNode) {
  const src = sourceOf(n);
  if (src) void openInEditor(src.path, src.line);
}

/** The tree's navigation keys (TreePane.onNavKey). */
const NAV_KEYS = new Set(["ArrowDown", "ArrowUp", "Home", "End", "PageDown", "PageUp", "ArrowRight", "ArrowLeft"]);

/** A single-key shortcut (O, C, R): no modifier, and single-key shortcuts are on. */
const singleKeyShortcut = (e: KeyboardEvent) => !(e.ctrlKey || e.metaKey || e.altKey) && getSettings().singleKeyShortcuts;

define("lc-tree-pane", TreePane);

declare global {
  interface HTMLElementTagNameMap {
    "lc-tree-pane": TreePane;
  }
}

export const rowId = (key: string) => `row-${key.replace(/[^a-zA-Z0-9_-]/g, "_")}`;

/** Outdated / previous session / pending notes of a row (tooltip and accessible name). */
function rowNote(n: TreeNode): string {
  if (n.outdated) return `Outdated: ${projectOf(n)?.name ?? "the project"} failed to build — this result is from the last successful build`;
  if (n.test?.stale) return "Result from a previous session — run again to refresh";
  if (n.pending) return "Previous result — waiting for this run's result";
  return "";
}

const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? "" : "s"}`;

/** The part of the accessible name after the visible label: ", passed, 4 ms" / ", 1 failed of 7". */
function rowExtra(r: Row): string {
  const parts = rowParts(r).slice(1);
  return parts.length ? `, ${parts.join(", ")}` : "";
}

/** Accessible name of a row: "Add_ReturnsSum, passed, 4 ms" / "OrderPricingTests, 1 failed of 7". */
function rowLabel(r: Row): string {
  return rowParts(r).join(", ");
}

function rowParts(r: Row): string[] {
  const n = r.node;
  if (n.test) return testRowParts(n, n.test);
  const c = n.counts;
  const parts = [n.label];
  const ps = n.project?.status;
  if (ps === "building" || ps === "discovering") parts.push(ps);
  else if (ps === "error") parts.push("build failed");
  if (n.project?.testPlatform) parts.push(NOT_MTP_LABEL[n.project.testPlatform]);
  const summary = groupSummary(c, r.matched);
  if (summary) parts.push(summary);
  if (n.durationMs > 0 && n.status !== "running") parts.push(formatDuration(n.durationMs));
  return parts;
}

/** A test row's accessible name parts: name, outcome, duration, pills, result notes. */
function testRowParts(n: TreeNode, t: TestNode): string[] {
  const parts = [t.displayName, STATUS_LABEL[t.outcome].toLowerCase()];
  if (t.durationMs != null && t.outcome !== "running" && t.outcome !== "queued" && t.outcome !== "notRun") parts.push(formatDuration(t.durationMs));
  if (t.flaky) parts.push("flaky");
  if (t.hasSnapshotMismatch) parts.push("snapshot mismatch");
  if (n.pending) parts.push("previous result");
  else if (n.outdated) parts.push("outdated");
  else if (t.stale) parts.push("from a previous session");
  return parts;
}

/** A group row's counts as words ("1 failed of 7"); undefined for an empty group. */
function groupSummary(c: Counts, matched: number | undefined): string | undefined {
  if (matched != null) return `${matched} of ${plural(c.total, "test")} match`;
  const inFlight = c.running + c.queued + c.pending;
  if (inFlight > 0) {
    const failed = c.failed ? `, ${c.failed} failed` : "";
    return `running, ${c.total - inFlight} of ${c.total} done${failed}`;
  }
  if (c.failed) return `${c.failed} failed of ${c.total}`;
  if (!c.total) return undefined;
  if (c.passed + c.skipped === c.total) return c.skipped ? `${c.passed} passed, ${c.skipped} skipped` : `${plural(c.total, "test")}, all passed`;
  return c.notRun === c.total ? `${plural(c.total, "test")}, not run` : plural(c.total, "test");
}

/**
 * The meta column (pills, host state, counts, duration). Each part is there only while it applies (`show`) and binds
 * its text, so a test update or a recycled row patches text instead of rebuilding the column.
 */
function renderMeta(row: ReadSignal<Row>): Child {
  const n = () => row().node;
  const c = () => n().counts;
  const ps = () => n().project?.status;
  const inFlight = () => c().running + c().queued + c().pending;
  const matched = () => row().matched;
  const host = () => n().project?.hostState;
  const errs = () => {
    const p = n().project;
    return p ? (p.buildErrorCount ?? p.buildErrors?.filter((d) => d.severity === "error").length ?? 0) : 0;
  };
  const total = () => c().total;
  return [
    show(
      () => ps() === "building" || ps() === "discovering",
      () => h("span", { class: "row-pill busy" }, () => (ps() === "building" ? "Building" : "Discovering")),
    ),
    show(
      () => ps() === "error",
      () => h("span", { class: "row-pill err" }, () => (errs() > 0 ? plural(errs(), "build error") : "Build failed")),
    ),
    // a project Labcoat doesn't run: "VSTest" (its message says how to switch it)
    show(
      () => n().project?.testPlatform,
      (tp) => h("span", { class: "row-pill warn", title: () => notOnMtpTitle(n().project) }, () => NOT_MTP_PILL[tp()]),
    ),
    show(
      () => host() && host() !== "cold",
      () =>
        h(
          "span",
          {
            class: () => cx("host-state", host()),
            title: () => (host() === "warm" ? "Test host warm — reruns skip startup" : "Starting the test host…"),
          },
          icon("fire", { size: 12 }),
        ),
    ),
    show(
      () => n().test?.flaky,
      () => h("span", { class: "row-pill flaky", title: "Flaky: both passed and failed in recent runs" }, "Flaky"),
    ),
    show(
      () => n().test?.hasSnapshotMismatch,
      () => h("span", { class: "row-pill snapshot", title: "Verify snapshot mismatch: accept or reject it in the details" }, "Snapshot"),
    ),
    show(
      () => !n().test && total() > 0,
      () =>
        h(
          "span",
          { class: "row-counts" },
          show(
            () => c().failed > 0 && matched() == null,
            () => h("span", { class: "cnt-failed", title: () => `${c().failed} failed` }, () => c().failed),
          ),
          h(
            "span",
            {
              class: "cnt-total",
              title: () => {
                const m = matched();
                if (m != null) return `${m} of ${total()} tests match the filter`;
                return inFlight() > 0 ? `${total() - inFlight()} of ${total()} done` : `${total()} tests`;
              },
            },
            () => {
              const m = matched();
              if (m != null) return `${m} of ${total()}`;
              return inFlight() > 0 ? `${total() - inFlight()}/${total()}` : total();
            },
          ),
        ),
    ),
    show(
      () => n().durationMs > 0 && n().status !== "running",
      () => h("span", { class: "row-duration" }, () => formatDuration(n().durationMs)),
    ),
  ];
}

function projectOf(n: TreeNode) {
  let p: TreeNode | undefined = n;
  while (p && !p.project) p = p.parent;
  return p?.project;
}

/** Flat list mode: the test's class (and method for data rows), dimmed after its name. */
function flatContext(n: TreeNode): string | undefined {
  const t = n.test;
  if (!t) return undefined;
  if (n.parent?.kind === "group") return n.sublabel;
  const method = n.parent?.kind === "method" ? n.parent.label : undefined;
  return [t.className, method].filter(Boolean).join(".") || t.namespace;
}

function firstFile(n: TreeNode): string | undefined {
  if (n.test) return n.test.filePath;
  for (const c of n.children) {
    const f = firstFile(c);
    if (f) return f;
  }
}

/** Where the troubleshooting guide explains how Labcoat finds test projects. */
export const TROUBLESHOOTING_URL = "https://github.com/eriklieben/labcoat/blob/main/docs/troubleshooting.md#a-project-shows-no-tests";

type NotMtp = NonNullable<TestProject["testPlatform"]>;
const NOT_MTP_PILL: Record<NotMtp, string> = { vstest: "VSTest", none: "Not a test app" };
const NOT_MTP_LABEL: Record<NotMtp, string> = { vstest: "uses VSTest, not run", none: "not a test app, not run" };

/** The project row's pill tooltip: the host's message and the switch. */
function notOnMtpTitle(p: TestProject | undefined): string {
  const d = p?.diagnostics?.[0];
  return d ? [d.message, d.hint].filter(Boolean).join("\n") : "";
}

/** The projects when every one of them is a project Labcoat doesn't run (no tests anywhere); else undefined. */
function onlyNotOnMtp(roots: readonly TreeNode[]): TestProject[] | undefined {
  if (roots.length === 0 || !roots.every((r) => r.project?.testPlatform && r.counts.total === 0)) return undefined;
  return roots.map((r) => r.project!);
}

/**
 * The tree when every project is one Labcoat doesn't run (VSTest): each project with its message and the switch for its
 * framework, a link to the guide, and Rediscover (after switching).
 */
function notOnMtpProjects(projects: TestProject[], rediscover: () => void) {
  const many = projects.length > 1;
  const found = many ? `${projects.length} test projects` : "a test project";
  return h(
    "div",
    { class: "empty-state small not-mtp" },
    icon("beaker", { size: 28 }),
    h("div", { class: "empty-title" }, "No Microsoft.Testing.Platform test projects"),
    h(
      "div",
      { class: "empty-sub" },
      `Labcoat found ${found}, but runs only Microsoft.Testing.Platform test apps. Switch ${many ? "them" : "it"}, then Rediscover:`,
    ),
    h(
      "ul",
      { class: "not-mtp-list" },
      projects.map((p) => {
        const d = p.diagnostics?.[0];
        return h(
          "li",
          { class: "not-mtp-item" },
          h("div", { class: "not-mtp-message" }, d?.message ?? `${p.name}: ${NOT_MTP_LABEL[p.testPlatform!]}`),
          d?.hint ? h("div", { class: "not-mtp-hint" }, d.hint) : null,
        );
      }),
    ),
    h("a", { href: VSTEST_URL, target: "_blank", rel: "noopener noreferrer" }, "How to switch from VSTest"),
    h("button", { class: "btn", title: "Look for test projects again, then build and discover them", onclick: rediscover }, icon("arrowSync"), " Rediscover"),
  );
}

/** Where the troubleshooting guide explains the switch from VSTest. */
export const VSTEST_URL = "https://github.com/eriklieben/labcoat/blob/main/docs/troubleshooting.md#my-project-uses-vstest";

/**
 * The tree when the workspace has no test project at all: what Labcoat looked for, where, and Rediscover (which looks
 * for test projects again; new ones are also picked up by themselves when their project file appears).
 */
function noTestProjects(root: string | undefined, rediscover: () => void) {
  return h(
    "div",
    { class: "empty-state small" },
    icon("beaker", { size: 28 }),
    h("div", { class: "empty-title" }, "No test projects found"),
    h(
      "div",
      { class: "empty-sub" },
      root ? ["No test projects found under ", h("code", null, root), ". "] : "No test projects found. ",
      "Labcoat looks for Microsoft.Testing.Platform test projects (MSTest, NUnit, xUnit v3, TUnit) and picks up new ones when their project file appears. ",
      h("a", { href: TROUBLESHOOTING_URL, target: "_blank", rel: "noopener noreferrer" }, "Why is my project not found?"),
    ),
    h("button", { class: "btn", title: "Look for test projects again, then build and discover them", onclick: rediscover }, icon("arrowSync"), " Rediscover"),
  );
}
