import type { Outcome, TestNode, TestProject } from "./contract";
import type { Status } from "@eriklieben/labcoat-design-system";
import { testKey } from "./store";

/** "group": a synthetic group of a non-default grouping (trait values, outcome) */
export type NodeKind = "project" | "namespace" | "class" | "method" | "test" | "group";

/** Aggregated node status; the same union as the design system's `Status` (status icons, badges). */
export type AggStatus = Outcome | "building" | "discovering" | "error";
/** compile-time check: every AggStatus has a design-system status icon */
export type AggStatusIsStatus<T extends Status = AggStatus> = T;

export interface Counts {
  total: number;
  passed: number;
  failed: number;
  skipped: number;
  notRun: number;
  running: number;
  queued: number;
  /** tests of the current run still showing their previous result (not a bucket: they also count in their old one) */
  pending: number;
  /** tests whose result is outdated (their project's last build failed) */
  outdated: number;
  /** flaky tests (TestNode.flaky) */
  flaky: number;
  /** pending tests by the bucket of their previous result (suite status excludes them from those buckets) */
  pendingPassed: number;
  pendingFailed: number;
  pendingSkipped: number;
  /** results from a previous session (TestNode.stale) */
  stale: number;
  /** failing quarantined tests: counted here instead of `failed` (kept out of the red/green state) */
  quarantined: number;
}

export interface TreeNode {
  key: string;
  kind: NodeKind;
  label: string;
  /** shown dimmed after the label, e.g. full method name for data rows */
  sublabel?: string;
  projectId: string;
  children: TreeNode[];
  parent?: TreeNode;
  test?: TestNode;
  project?: TestProject;
  counts: Counts;
  status: AggStatus;
  durationMs: number;
  /** lowercase text used for filtering */
  search: string;
  /** test leaf: part of the current run, still showing its previous result */
  pending?: boolean;
  /** test leaf: testKey(test), one string per leaf (map lookups by it reuse its cached hash) */
  tkey?: string;
  /** the project's last build failed after these results were produced: they are from older code */
  outdated?: boolean;
}

export const emptyCounts = (): Counts => ({ total: 0, passed: 0, failed: 0, skipped: 0, notRun: 0, running: 0, queued: 0, pending: 0, outdated: 0, flaky: 0, pendingPassed: 0, pendingFailed: 0, pendingSkipped: 0, stale: 0, quarantined: 0 });

export function bucket(o: Outcome): "passed" | "failed" | "skipped" | "running" | "queued" | "notRun" {
  switch (o) {
    case "passed":
      return "passed";
    case "failed":
    case "timedOut":
      return "failed";
    case "skipped":
      return "skipped";
    case "running":
      return "running";
    case "queued":
      return "queued";
    default:
      return "notRun";
  }
}

function aggregate(c: Counts): Outcome {
  if (c.total === 0) return "notRun";
  if (c.running) return "running";
  if (c.queued) return "queued";
  if (c.failed) return "failed";
  if (c.notRun) return "notRun";
  if (c.skipped === c.total) return "skipped";
  return "passed";
}

/** A test's traits; tolerant of payloads without the field (an older / partial server message). */
export const traitsOf = (t: TestNode): readonly { key: string; value: string }[] => t.traits ?? [];
/** The trait values as search text. */
const traitText = (t: TestNode) => traitsOf(t).map((x) => x.value).join(" ");

export function fqName(t: TestNode) {
  return [t.namespace, t.className, t.displayName].filter(Boolean).join(".");
}

/** Find-or-create of tree nodes by key (buildTree): a new node is appended to its parent's children, or to `roots`. */
class NodeIndex {
  readonly roots: TreeNode[] = [];
  private readonly byKey = new Map<string, TreeNode>();

  get(key: string): TreeNode | undefined {
    return this.byKey.get(key);
  }

  mk(key: string, kind: NodeKind, label: string, projectId: string, parent?: TreeNode): TreeNode {
    let n = this.byKey.get(key);
    if (n) return n;
    n = { key, kind, label, projectId, children: [], parent, counts: emptyCounts(), status: "notRun", durationMs: 0, search: label.toLowerCase() };
    this.byKey.set(key, n);
    if (parent) parent.children.push(n);
    else this.roots.push(n);
    return n;
  }
}

/** Tests grouped by method, so single tests can be told from data-driven ones. */
function groupByMethod(tests: Map<string, TestNode>): Map<string, TestNode[]> {
  const groups = new Map<string, TestNode[]>();
  for (const t of tests.values()) {
    const ns = t.namespace ?? "";
    const cls = t.className ?? "";
    const method = t.methodName ?? t.displayName;
    const gk = `${t.projectId}\u0000${ns}\u0000${cls}\u0000${method}`;
    const g = groups.get(gk);
    if (g) g.push(t);
    else groups.set(gk, [t]);
  }
  return groups;
}

/** A test leaf under `parent`. */
function addLeaf(idx: NodeIndex, t: TestNode, label: string, parent: TreeNode): TreeNode {
  const tk = testKey(t);
  const n = idx.mk(`t:${tk}`, "test", label, t.projectId, parent);
  n.test = t;
  n.tkey = tk;
  n.search = `${t.projectId} ${fqName(t)} ${traitText(t)}`.toLowerCase();
  return n;
}

/** A data row's label: its display name without the method name ("(1, 2)"), or the display name when that leaves nothing. */
function dataRowLabel(t: TestNode): string {
  const label = t.displayName;
  if (!t.methodName || !label.startsWith(t.methodName)) return label;
  return label.slice(t.methodName.length).trim() || label;
}

/** The nodes of one method group (its tests share project, namespace, class and method). */
function addMethodGroup(idx: NodeIndex, g: TestNode[]) {
  const t0 = g[0];
  const pid = t0.projectId;
  let parent = idx.get(`p:${pid}`) ?? idx.mk(`p:${pid}`, "project", pid, pid);
  const ns = t0.namespace;
  const cls = t0.className;
  // a namespace named like its project (SampleTests.csproj → namespace SampleTests) adds a level that only repeats the
  // project name: its classes go straight under the project
  if (ns && ns !== parent.label && ns !== pid) parent = idx.mk(`n:${pid}:${ns}`, "namespace", ns, pid, parent);
  if (cls) parent = idx.mk(`c:${pid}:${ns ?? ""}.${cls}`, "class", cls, pid, parent);
  const method = t0.methodName ?? t0.displayName;
  const single = g.length === 1 && (t0.displayName === method || !t0.methodName);
  if (single) {
    addLeaf(idx, t0, t0.displayName, parent);
    return;
  }
  const mn = idx.mk(`m:${pid}:${ns ?? ""}.${cls ?? ""}.${method}`, "method", method, pid, parent);
  mn.search = `${ns ?? ""}.${cls ?? ""}.${method}`.toLowerCase();
  for (const t of g) {
    const label = dataRowLabel(t);
    const n = addLeaf(idx, t, label, mn);
    n.sublabel = t.displayName === label ? undefined : t.methodName;
  }
}

// Sibling order must come from a consistent (transitive) comparator. With an inconsistent one the sort order is
// implementation-defined (ECMAScript Array.prototype.sort): the old one compared a data-driven method to tests by
// label but tests to each other by line, and Chromium sometimes returned a different order for the same input
// later in a page's life (Divide_ByZero_Throws jumped from before Multiply_ReturnsProduct to after
// Subtract_ReturnsDifference after a watch run; regression tests in test/app/tree.test.ts and tree-fuzz.test.ts).
// Tests and methods with a source location come first, in source order (file, then line; label breaks ties);
// everything else (classes, namespaces, tests without a location) follows by label.
function sortSiblings(nodes: TreeNode[]) {
  const at = new Map<TreeNode, SourcePos | undefined>();
  for (const n of nodes) at.set(n, declaredAt(n));
  nodes.sort((a, b) => {
    const pa = at.get(a);
    const pb = at.get(b);
    if (pa && pb) {
      if (pa.file !== pb.file) return pa.file < pb.file ? -1 : 1;
      return pa.line - pb.line || a.label.localeCompare(b.label);
    }
    if (pa || pb) return pa ? -1 : 1;
    return a.label.localeCompare(b.label);
  });
  // a method's data rows keep their discovery order
  for (const n of nodes) if (n.children.length && n.kind !== "method") sortSiblings(n.children);
}

/** Build Project > Namespace > Class > Method > DataRow tree. */
export function buildTree(projects: TestProject[], tests: Map<string, TestNode>, pending?: ReadonlySet<string>, quarantined?: ReadonlySet<string>): TreeNode[] {
  const idx = new NodeIndex();
  for (const p of projects) {
    const pn = idx.mk(`p:${p.id}`, "project", p.name, p.id);
    pn.project = p;
  }
  for (const g of groupByMethod(tests).values()) addMethodGroup(idx, g);

  const roots = idx.roots;
  // projects keep server order
  for (const r of roots) sortSiblings(r.children);

  const finish = (n: TreeNode): void => {
    if (n.test) return finishLeaf(n, pending, quarantined);
    for (const ch of n.children) finish(ch);
    finishGroup(n);
  };
  roots.forEach(finish);
  return roots;
}

const hasResult = (o: Outcome) => o === "passed" || o === "failed" || o === "skipped" || o === "timedOut";

type SourcePos = { file: string; line: number };

/** Where a test (or a data-driven method: its first data row) is declared; undefined without a file and line. */
function declaredAt(n: TreeNode): SourcePos | undefined {
  let t: TestNode | undefined;
  if (n.kind === "test") t = n.test;
  else if (n.kind === "method") t = n.children.find((c) => c.test?.filePath && c.test.line != null)?.test;
  return t?.filePath && t.line != null ? { file: t.filePath, line: t.line } : undefined;
}

export function leaves(n: TreeNode, out: TestNode[] = []): TestNode[] {
  if (n.test) out.push(n.test);
  else for (const c of n.children) leaves(c, out);
  return out;
}

export type StatusFilter = "all" | "failed" | "new" | "passed" | "skipped" | "notRun" | "flaky";

const STATUS_FILTER_EVENT = "labcoat:status-filter";

/** Sets the test tree's status filter from elsewhere (e.g. clicking a band of the run progress bar). */
export function requestStatusFilter(f: StatusFilter) {
  window.dispatchEvent(new CustomEvent<StatusFilter>(STATUS_FILTER_EVENT, { detail: f }));
}

export function onStatusFilterRequest(handler: (f: StatusFilter) => void): () => void {
  const listener = (e: Event) => handler((e as CustomEvent<StatusFilter>).detail);
  window.addEventListener(STATUS_FILTER_EVENT, listener);
  return () => window.removeEventListener(STATUS_FILTER_EVENT, listener);
}

export function matchesStatus(o: Outcome, f: StatusFilter) {
  if (f === "all") return true;
  if (f === "flaky" || f === "new") return false; // decided per test (TestNode.flaky / new failure keys), see flatten
  const b = bucket(o);
  if (f === "notRun") return b === "notRun" || b === "queued" || b === "running";
  return b === f;
}

export interface Row {
  node: TreeNode;
  /** position in the flattened list (virtualized rows are placed at index * row height) */
  index: number;
  depth: number;
  expanded: boolean;
  hasChildren: boolean;
  /** while filtering: leaves beneath this node that match (group rows show "matched of total") */
  matched?: number;
  /** flat list mode (Failed / Flaky filters): leaves only, with their class as context */
  flat?: boolean;
  /** position among the visible siblings (1-based) and their count: aria-posinset / aria-setsize */
  pos: number;
  size: number;
}

export type ExpandMode = "default" | "all" | "none";

export function defaultExpanded(kind: NodeKind, mode: ExpandMode) {
  if (mode === "all") return true;
  if (mode === "none") return false;
  return kind === "project" || kind === "namespace" || kind === "class" || kind === "group";
}

/**
 * Flatten to visible rows. When filtering, a node is visible if any leaf beneath it matches.
 * `isExpanded` decides expansion for nodes with children.
 */
export function flatten(
  roots: TreeNode[],
  isExpanded: (n: TreeNode) => boolean,
  text: string,
  status: StatusFilter,
  flat = false,
  opts: {
    /** testKeys of new failures (status "new") */
    newKeys?: ReadonlySet<string>;
    /** sibling order (duration sorts): rank per node key; leaves without a rank keep default order at the end */
    rank?: ReadonlyMap<string, number>;
    /** flat list order (duration sorts) */
    flatRank?: ReadonlyMap<string, number>;
    /** the filter pass of these roots / text / status / newKeys (filterPass), when the caller has it already */
    pass?: FilterPass;
  } = {},
): Row[] {
  const p = opts.pass?.roots === roots && opts.pass.text === text && opts.pass.status === status ? opts.pass : filterPass(roots, text, status, opts.newKeys);
  const filtering = p.filtering;
  const rows: Row[] = [];
  // vis: matching leaves beneath a node (absent = 0); -1 = an empty node kept visible
  const vis = p.vis;

  if (filtering && flat) return flatRows(roots, vis, opts.flatRank);

  const shown = (n: TreeNode) => !filtering || !!vis.get(n);
  const walk = (n: TreeNode, depth: number, pos: number, size: number) => {
    const m = vis.get(n);
    const hasChildren = n.children.length > 0;
    const expanded = hasChildren && isExpanded(n);
    rows.push({ node: n, index: rows.length, depth, expanded, hasChildren, matched: filtering && !n.test ? Math.max(0, m ?? 0) : undefined, pos, size });
    if (expanded) walkAll(ordered(n.children, opts.rank), depth + 1);
  };
  const walkAll = (nodes: readonly TreeNode[], depth: number) => {
    let size = 0;
    for (const c of nodes) if (shown(c)) size++;
    let pos = 0;
    for (const c of nodes) if (shown(c)) walk(c, depth, ++pos, size);
  };
  walkAll(ordered(roots, opts.rank), 0);
  return rows;
}

/** Flat list: matching leaves in tree order, no grouping (a failure list instead of a deep tree). */
function flatRows(roots: TreeNode[], vis: ReadonlyMap<TreeNode, number>, fr: ReadonlyMap<string, number> | undefined): Row[] {
  const rows: Row[] = [];
  const found: TreeNode[] = [];
  const leavesOf = (n: TreeNode) => {
    if (!vis.get(n)) return;
    if (n.test) found.push(n);
    else for (const c of n.children) leavesOf(c);
  };
  const broken: TreeNode[] = [];
  for (const r of roots) {
    if (vis.get(r) === -1) broken.push(r);
    else leavesOf(r);
  }
  if (fr) found.sort((a, b) => (fr.get(a.key) ?? Infinity) - (fr.get(b.key) ?? Infinity));
  const size = found.length + broken.length;
  for (const n of found) rows.push({ node: n, index: rows.length, depth: 0, expanded: false, hasChildren: false, flat: true, pos: rows.length + 1, size });
  // broken (empty, failed-to-build) projects stay discoverable, after the tests
  for (const r of broken) rows.push({ node: r, index: rows.length, depth: 0, expanded: false, hasChildren: false, matched: 0, pos: rows.length + 1, size });
  return rows;
}

/** Counts of leaves matching the text filter (for status chips). */
export function filterCounts(roots: TreeNode[], text: string, newKeys?: ReadonlySet<string>): Counts {
  return filterPass(roots, text, "all", newKeys).counts;
}

/** `c += d`, field by field (no Object.keys per call: this runs for every matching leaf). */
function addCounts(c: Counts, d: Counts) {
  c.total += d.total;
  c.passed += d.passed;
  c.failed += d.failed;
  c.skipped += d.skipped;
  c.notRun += d.notRun;
  c.running += d.running;
  c.queued += d.queued;
  c.pending += d.pending;
  c.outdated += d.outdated;
  c.flaky += d.flaky;
  c.pendingPassed += d.pendingPassed;
  c.pendingFailed += d.pendingFailed;
  c.pendingSkipped += d.pendingSkipped;
  c.stale += d.stale;
  c.quarantined += d.quarantined;
}

// ---- filter pass ------------------------------------------------------------------------------------------------

/**
 * Everything a filter (search text + status chip) derives from a tree, from one walk over its leaves: which nodes the
 * tree shows (`vis`, for flatten), the chip counts (text only) and the matching tests ("Run N matching").
 */
export interface FilterPass {
  readonly roots: readonly TreeNode[];
  readonly text: string;
  readonly status: StatusFilter;
  /** the new-failure keys the pass used (undefined unless the status or the query needs them) */
  readonly newKeys: ReadonlySet<string> | undefined;
  /** the text or the status narrows the tree */
  readonly filtering: boolean;
  /** matching leaves (text + status) beneath each node: 1 for a leaf, absent = 0; -1 = an empty broken project kept visible */
  readonly vis: ReadonlyMap<TreeNode, number>;
  /** totals of the leaves matching the text (any status): the status chips */
  readonly counts: Counts;
  /** tests matching text + status, tree order, deduplicated (not filtering: every test) */
  readonly tests: TestNode[];
  /** leaves matching the text in tree order; undefined without a text query (every leaf) */
  readonly textLeaves: readonly TreeNode[] | undefined;
  /** the parsed query (narrowing checks) */
  readonly terms: readonly QueryTerm[];
}

/** Does the status chip or the query read the new-failure keys? (Otherwise a pass does not depend on them.) */
export const needsNewKeys = (text: string, status: StatusFilter) => status === "new" || /\bis:[a-z|,]*n/i.test(text);

/** The status chip's rule for a test leaf. */
function statusMatcher(status: StatusFilter, newKeys: ReadonlySet<string> | undefined): ((n: TreeNode) => boolean) | undefined {
  if (status === "all") return undefined;
  if (status === "flaky") return (n) => !!n.test!.flaky;
  if (status === "new") return (n) => !!newKeys?.has(testKey(n.test!));
  if (status === "failed") return (n) => n.counts.failed > 0;
  return (n) => matchesStatus(n.test!.outcome, status);
}

/**
 * The filter pass of `roots` for `text` + `status`. With `base` (an earlier pass of the same roots and new-failure
 * keys whose query this one implies, e.g. "ord" → "order", or the same text with another status chip) only the
 * leaves that matched the base's text are tested again (incremental narrowing); otherwise every leaf once.
 */
export function filterPass(roots: readonly TreeNode[], text: string, status: StatusFilter, newKeys?: ReadonlySet<string>, base?: FilterPass): FilterPass {
  if (!needsNewKeys(text, status)) newKeys = undefined;
  const terms = parseQuery(text);
  const match = compileTerms(terms, { newKeys });
  const filtering = !!match || status !== "all";

  if (!filtering) {
    // no filter: the roots' aggregates are the counts; every test matches (collected only when someone asks)
    const counts = emptyCounts();
    for (const r of roots) addCounts(counts, r.counts);
    let all: TestNode[] | undefined;
    return {
      roots, text, status, newKeys, filtering, vis: new Map(), counts, textLeaves: undefined, terms,
      get tests() {
        all ??= matchingTestsOf(roots);
        return all;
      },
    };
  }

  const acc = new PassAccumulator(match, statusMatcher(status, newKeys));
  if (match && base?.textLeaves && base.roots === roots && base.newKeys === newKeys && implies(terms, base.terms)) acc.narrow(base.textLeaves, base.text === text);
  else for (const r of roots) acc.visit(r);
  return { roots, text, status, newKeys, filtering, vis: acc.vis, counts: acc.counts, tests: acc.tests, textLeaves: acc.textLeaves, terms };
}

/** What a filtering pass collects while it walks the leaves (filterPass). */
class PassAccumulator {
  readonly counts = emptyCounts();
  readonly vis = new Map<TreeNode, number>();
  readonly tests: TestNode[] = [];
  readonly textLeaves: TreeNode[] | undefined;
  private readonly seen = new Set<TestNode>();

  constructor(
    private readonly match: ((n: TreeNode) => boolean) | undefined,
    private readonly statusOk: ((n: TreeNode) => boolean) | undefined,
  ) {
    this.textLeaves = match ? [] : undefined;
  }

  /** A leaf matching the text: counts it; keeps it when the status matches too. */
  private take(n: TreeNode): boolean {
    addCounts(this.counts, n.counts);
    this.textLeaves?.push(n);
    if (this.statusOk && !this.statusOk(n)) return false;
    const t = n.test!;
    if (!this.seen.has(t)) {
      this.seen.add(t);
      this.tests.push(t);
    }
    this.vis.set(n, 1);
    return true;
  }

  /** Incremental narrowing: only the leaves an earlier pass matched are tested again (none when the text is the same). */
  narrow(baseLeaves: readonly TreeNode[], sameText: boolean) {
    for (const n of baseLeaves) {
      if (!sameText && !this.match!(n)) continue;
      if (!this.take(n)) continue;
      for (let a = n.parent; a; a = a.parent) this.vis.set(a, (this.vis.get(a) ?? 0) + 1);
    }
  }

  /** Matching leaves beneath `n` (-1: an empty broken project kept visible). A leaf's search text includes project,
   *  namespace, class and method, so leaves alone decide. */
  visit(n: TreeNode): number {
    if (n.test) return (!this.match || this.match(n)) && this.take(n) ? 1 : 0;
    let count = 0;
    for (const c of n.children) {
      const v = this.visit(c);
      if (v > 0) count += v;
    }
    // keep broken/empty projects (and VSTest ones: their row says how to switch) discoverable unless the user is searching for text
    if (count === 0 && n.kind === "project" && !this.match && (n.project?.status === "error" || !!n.project?.testPlatform)) {
      this.vis.set(n, -1);
      return -1;
    }
    if (count) this.vis.set(n, count);
    return count;
  }
}

/** Every test under `roots` (tree order, deduplicated). */
function matchingTestsOf(roots: readonly TreeNode[]): TestNode[] {
  const out: TestNode[] = [];
  const seen = new Set<TestNode>();
  const walk = (n: TreeNode) => {
    if (n.test) {
      if (!seen.has(n.test)) {
        seen.add(n.test);
        out.push(n.test);
      }
      return;
    }
    for (const c of n.children) walk(c);
  };
  roots.forEach(walk);
  return out;
}

/**
 * A small cache of filter passes for one view (the tree pane and the shell's "Run N matching" share it, so a keystroke
 * walks the tree once). The same arguments return the cached pass; a query that narrows a cached one (typing on, or
 * another status chip with the same text) starts from that pass's matches; backspace finds the earlier pass again.
 * Passes of other roots are dropped when the tree changes.
 */
export function createFilterCache(size = 6) {
  let recent: FilterPass[] = [];
  return (roots: readonly TreeNode[], text: string, status: StatusFilter, newKeys?: ReadonlySet<string>): FilterPass => {
    if (!needsNewKeys(text, status)) newKeys = undefined;
    if (recent.length && recent[0].roots !== roots) recent = recent.filter((p) => p.roots === roots);
    const hit = recent.findIndex((p) => p.text === text && p.status === status && p.newKeys === newKeys);
    if (hit >= 0) {
      const p = recent[hit];
      if (hit) {
        recent.splice(hit, 1);
        recent.unshift(p);
      }
      return p;
    }
    // the narrowest cached pass this query implies
    const terms = parseQuery(text);
    let base: FilterPass | undefined;
    for (const p of recent)
      if (p.textLeaves && p.newKeys === newKeys && (!base || p.textLeaves.length < base.textLeaves!.length) && implies(terms, p.terms)) base = p;
    const p = filterPass(roots, text, status, newKeys, base);
    recent.unshift(p);
    if (recent.length > size) recent.length = size;
    return p;
  };
}

export function findNode(roots: TreeNode[], key: string): TreeNode | undefined {
  const stack = [...roots];
  while (stack.length) {
    const n = stack.pop()!;
    if (n.key === key) return n;
    for (const c of n.children) stack.push(c);
  }
  return undefined;
}

export function indexNodes(roots: TreeNode[]): Map<string, TreeNode> {
  const m = new Map<string, TreeNode>();
  const stack = [...roots];
  while (stack.length) {
    const n = stack.pop()!;
    m.set(n.key, n);
    for (const c of n.children) stack.push(c);
  }
  return m;
}

// ---- incremental tree (P2-13) -------------------------------------------------------------------------------

/**
 * `structureOf(a) === structureOf(b)` without building the strings (a run that touches all 20k tests compares every
 * leaf twice: once when results arrive, once when the history follows).
 */
function sameStructure(a: TestNode, b: TestNode): boolean {
  if (
    a.projectId !== b.projectId ||
    (a.namespace ?? "") !== (b.namespace ?? "") ||
    (a.className ?? "") !== (b.className ?? "") ||
    (a.methodName ?? "") !== (b.methodName ?? "") ||
    a.displayName !== b.displayName ||
    (a.filePath ?? "") !== (b.filePath ?? "") ||
    String(a.line ?? "") !== String(b.line ?? "")
  )
    return false;
  const at = traitsOf(a);
  const bt = traitsOf(b);
  if (at === bt) return true;
  if (at.length === bt.length && at.every((x, i) => x.value === bt[i].value)) return true;
  return structureOf(a) === structureOf(b); // e.g. traits ["a b"] vs ["a", "b"]: same search text
}

/** What places a test in the tree (and its search text); a change here needs a full rebuild. */
const structureOf = (t: TestNode) =>
  `${t.projectId}\u0000${t.namespace ?? ""}\u0000${t.className ?? ""}\u0000${t.methodName ?? ""}\u0000${t.displayName}\u0000${t.filePath ?? ""}\u0000${t.line ?? ""}\u0000${traitText(t)}`;

/**
 * Builds the tree like `buildTree`, but when only results changed (same projects, same tests in the same places) it
 * patches the previous tree: new leaf objects for the changed tests, new ancestors on their paths, every other subtree
 * reused. A 2000-test run then costs O(changed × depth) per frame instead of a full rebuild + sort.
 * `index()` is the key → node map of the latest tree. It is updated in place by patches: read it after (and
 * tracked through) the roots, e.g. `computed(() => (roots(), builder.index().get(key)))`.
 */
export function createTreeBuilder() {
  let prev: BuiltTree | undefined;

  let quarantined: ReadonlySet<string> | undefined;
  const full = (projects: TestProject[], tests: Map<string, TestNode>, pending?: ReadonlySet<string>) => {
    const roots = buildTree(projects, tests, pending, quarantined);
    const index = indexNodes(roots);
    const leaf = new Map<string, TreeNode>();
    for (const n of index.values()) if (n.test) leaf.set(n.tkey ?? testKey(n.test), n);
    prev = { projects, tests, pending, quarantined, roots, index, leaf };
    return roots;
  };

  const build = (
    projects: TestProject[],
    tests: Map<string, TestNode>,
    pending?: ReadonlySet<string>,
    /** tests/pending differ from `from`/`pendingFrom` only in `keys` (store.testsDelta): no scan needed */
    delta?: TestsDelta,
    /** quarantined test keys (a change rebuilds) */
    quarantine?: ReadonlySet<string>,
  ): TreeNode[] => {
    // a project status change (building → running → idle) only replaces the project rows, not the whole tree
    if (prev && prev.projects !== projects && sameProjectShape(prev.projects, projects)) prev = swapProjects(prev, projects);
    const p = prev;
    quarantined = quarantine;
    if (p?.projects !== projects || p.tests.size !== tests.size || p.quarantined !== quarantine) return full(projects, tests, pending);
    if (p.tests === tests && p.pending === pending) return p.roots;
    const pendingChanged = p.pending !== pending;
    const useDelta = delta?.from === p.tests && (!pendingChanged || delta.pendingFrom === p.pending);
    // No delta (run start / finish: every test's pending flag flips), or a large one: one copy-on-write walk over the
    // tree beats collecting the changed leaves and rebuilding their ancestors deepest first.
    if (!useDelta || delta.keys.size * 8 > p.leaf.size) {
      const roots = patchAll(p.roots, p.index, p.leaf, tests, pending, quarantine);
      if (!roots) return full(projects, tests, pending);
      prev = { projects, tests, pending, quarantined: quarantine, roots, index: p.index, leaf: p.leaf };
      return roots;
    }
    const changed = changedLeaves(p, tests, pending, delta.keys, pendingChanged);
    if (!changed) return full(projects, tests, pending);
    if (!changed.length) {
      prev = { ...p, tests, pending };
      return p.roots;
    }
    const roots = patchChanged(p, changed, tests, pending, quarantine);
    prev = { projects, tests, pending, quarantined: quarantine, roots, index: p.index, leaf: p.leaf };
    return roots;
  };

  return {
    build,
    /** key → node of the latest build */
    index: () => prev?.index ?? new Map<string, TreeNode>(),
  };
}

/** The latest build of a tree builder (createTreeBuilder) and its inputs. */
interface BuiltTree {
  projects: TestProject[];
  tests: Map<string, TestNode>;
  pending?: ReadonlySet<string>;
  quarantined?: ReadonlySet<string>;
  roots: TreeNode[];
  index: Map<string, TreeNode>;
  leaf: Map<string, TreeNode>;
}

/** tests/pending differ from `from`/`pendingFrom` only in `keys` (store.testsDelta). */
type TestsDelta = { from: Map<string, TestNode>; pendingFrom?: ReadonlySet<string>; keys: ReadonlySet<string> };

/** A leaf to replace and its test key. */
type ChangedLeaf = { old: TreeNode; key: string };

/**
 * The leaves among `keys` whose test object or pending flag changed; undefined when a test moved, appeared or
 * disappeared (a full build then).
 */
function changedLeaves(p: BuiltTree, tests: Map<string, TestNode>, pending: ReadonlySet<string> | undefined, keys: ReadonlySet<string>, pendingChanged: boolean): ChangedLeaf[] | undefined {
  const changed: ChangedLeaf[] = [];
  const candidates = [...keys].map((k) => [k, tests.get(k)] as [string, TestNode | undefined]);
  for (const [k, t] of candidates) {
    const old = p.leaf.get(k);
    if (!old || !t) return undefined;
    if (old.test === t && (!pendingChanged || !!old.pending === !!pending?.has(k))) continue;
    if (old.test !== t && !sameStructure(old.test!, t)) return undefined;
    changed.push({ old, key: k });
  }
  return changed;
}

/** Number of ancestors of `n`. */
function depthOf(n: TreeNode): number {
  let d = 0;
  for (let a = n.parent; a; a = a.parent) d++;
  return d;
}

/**
 * The tree of `p` with the `changed` leaves replaced and their ancestors rebuilt (deepest first); `p.index` and
 * `p.leaf` are patched in place (consumers read them through the new roots).
 */
function patchChanged(p: BuiltTree, changed: readonly ChangedLeaf[], tests: Map<string, TestNode>, pending: ReadonlySet<string> | undefined, quarantine: ReadonlySet<string> | undefined): TreeNode[] {
  const { index, leaf } = p;
  // old node -> its replacement
  const repl = new Map<TreeNode, TreeNode>();
  const dirty = new Set<TreeNode>();
  for (const { old, key: k } of changed) {
    const t = tests.get(k)!;
    const n: TreeNode = { ...old, test: t, pending: undefined, outdated: undefined };
    finishLeaf(n, pending, quarantine, k);
    repl.set(old, n);
    index.set(n.key, n);
    leaf.set(k, n);
    for (let a = old.parent; a && !dirty.has(a); a = a.parent) dirty.add(a); // an ancestor in `dirty` has its own ancestors there too
  }
  // rebuild dirty ancestors deepest first
  const depth = new Map<TreeNode, number>();
  for (const n of dirty) depth.set(n, depthOf(n));
  const order = [...dirty].sort((a, b) => depth.get(b)! - depth.get(a)!);
  for (const old of order) {
    const children = old.children.map((c) => repl.get(c) ?? c);
    const n: TreeNode = { ...old, children };
    for (const c of children) c.parent = n;
    finishGroup(n);
    repl.set(old, n);
    index.set(n.key, n);
  }
  // parents of replaced nodes that were not dirty can't exist (every ancestor is dirty); fix the new nodes' parents
  for (const n of repl.values()) if (n.parent && repl.has(n.parent)) n.parent = repl.get(n.parent);
  return p.roots.map((r) => repl.get(r) ?? r);
}

/**
 * The tree with every leaf whose test object or pending flag changed replaced (new leaf, new ancestors; untouched
 * subtrees reused), `index` / `leaf` updated in place. Undefined when a test moved or disappeared (a full build then).
 */
function patchAll(
  roots: TreeNode[],
  index: Map<string, TreeNode>,
  leaf: Map<string, TreeNode>,
  tests: Map<string, TestNode>,
  pending: ReadonlySet<string> | undefined,
  quarantine: ReadonlySet<string> | undefined,
): TreeNode[] | undefined {
  let abort = false;
  const visit = (n: TreeNode): TreeNode => {
    if (n.test) {
      const m = patchLeaf(n, tests, pending, quarantine);
      if (!m) abort = true;
      else if (m !== n) {
        index.set(m.key, m);
        leaf.set(m.tkey!, m);
      }
      return m ?? n;
    }
    const ch = n.children;
    let next: TreeNode[] | undefined;
    for (let i = 0; i < ch.length; i++) {
      const c = visit(ch[i]);
      if (abort) return n;
      if (c === ch[i]) continue;
      next ??= ch.slice();
      next[i] = c;
    }
    if (!next) return n;
    const m: TreeNode = { ...n, children: next };
    for (const c of next) c.parent = m;
    finishGroup(m);
    index.set(m.key, m);
    return m;
  };
  const out = roots.map(visit);
  if (abort) return undefined;
  return out.some((r, i) => r !== roots[i]) ? out : roots;
}

/**
 * Leaf `n` for the current tests and pending keys: `n` itself when neither its test object nor its pending flag
 * changed, else a new leaf; undefined when its test moved or disappeared.
 */
function patchLeaf(n: TreeNode, tests: Map<string, TestNode>, pending: ReadonlySet<string> | undefined, quarantine: ReadonlySet<string> | undefined): TreeNode | undefined {
  const k = n.tkey ?? n.key.slice(2); // "t:" + testKey
  const t = tests.get(k);
  if (!t || (t !== n.test && !sameStructure(n.test!, t))) return undefined;
  const isPending = !!pending?.has(k);
  if (t === n.test && !!n.pending === isPending) return n;
  // an explicit copy (one object shape): a spread of 20,000 leaves at run start costs about as much as the rest
  const m: TreeNode = {
    key: n.key,
    kind: n.kind,
    label: n.label,
    sublabel: n.sublabel,
    projectId: n.projectId,
    children: n.children,
    parent: n.parent,
    test: t,
    project: n.project,
    counts: n.counts,
    status: n.status,
    durationMs: n.durationMs,
    search: n.search,
    pending: undefined,
    outdated: undefined,
    tkey: k,
  };
  finishLeaf(m, isPending ? pending : undefined, quarantine, k);
  return m;
}

/** What a project contributes to the tree besides its row: its place (id, name) and whether its results are outdated. */
const outdatedProject = (p: TestProject) => !!(p.resultsOutdated || p.status === "error");
const sameProjectShape = (a: TestProject[], b: TestProject[]) =>
  a.length === b.length && a.every((p, i) => p.id === b[i].id && p.name === b[i].name && outdatedProject(p) === outdatedProject(b[i]));

/** The previous tree with new project objects on the project rows (new row objects, children re-parented in place). */
function swapProjects<P extends { projects: TestProject[]; roots: TreeNode[]; index: Map<string, TreeNode> }>(p: P, projects: TestProject[]): P {
  const byId = new Map(projects.map((x) => [x.id, x]));
  const roots = p.roots.map((r) => {
    const np = r.project && byId.get(r.project.id);
    if (!np || np === r.project) return r;
    const n: TreeNode = { ...r, project: np };
    for (const c of n.children) c.parent = n;
    finishGroup(n); // the row's status shows "building" / "discovering" / "error"
    p.index.set(n.key, n);
    return n;
  });
  return { ...p, projects, roots: roots.some((r, i) => r !== p.roots[i]) ? roots : p.roots };
}

const BUCKETS = ["passed", "failed", "skipped", "running", "queued", "notRun"] as const;
/** Leaf counts take only a few distinct values: shared (frozen) objects, not one allocation per leaf and build. */
const LEAF_COUNTS: Counts[] = [];

function leafCounts(b: ReturnType<typeof bucket>, quarantined: boolean, flaky: boolean, stale: boolean, pending: boolean, outdated: boolean): Counts {
  const code = (((((BUCKETS.indexOf(b) * 2 + +quarantined) * 2 + +flaky) * 2 + +stale) * 2 + +pending) * 2) + +outdated;
  LEAF_COUNTS[code] ??= Object.freeze(newLeafCounts(b, quarantined, flaky, stale, pending, outdated));
  return LEAF_COUNTS[code];
}

function newLeafCounts(b: ReturnType<typeof bucket>, quarantined: boolean, flaky: boolean, stale: boolean, pending: boolean, outdated: boolean): Counts {
  const c = emptyCounts();
  c.total = 1;
  if (quarantined) c.quarantined = 1;
  else c[b]++;
  if (flaky) c.flaky = 1;
  if (stale) c.stale = 1;
  if (pending) {
    c.pending = 1;
    if (b === "passed") c.pendingPassed = 1;
    else if (b === "failed") c.pendingFailed = 1;
    else if (b === "skipped") c.pendingSkipped = 1;
  }
  if (outdated) c.outdated = 1;
  return c;
}

function finishLeaf(n: TreeNode, pending?: ReadonlySet<string>, quarantined?: ReadonlySet<string>, key?: string) {
  const t = n.test!;
  const k = () => (key ??= testKey(t));
  const b = bucket(t.outcome);
  const q = b === "failed" && !!quarantined?.has(k());
  const isPending = !!pending?.has(k());
  if (isPending) n.pending = true;
  let pn: TreeNode | undefined = n;
  while (pn && !pn.project) pn = pn.parent;
  const p = pn?.project;
  const outdated = !!p && (!!p.resultsOutdated || p.status === "error") && hasResult(t.outcome);
  if (outdated) n.outdated = true;
  n.counts = leafCounts(b, q, !!t.flaky, !!t.stale, isPending, outdated);
  n.status = t.outcome;
  n.durationMs = t.durationMs ?? 0;
}

export function finishGroup(n: TreeNode) {
  const c = emptyCounts();
  let d = 0;
  for (const ch of n.children) {
    c.total += ch.counts.total;
    c.passed += ch.counts.passed;
    c.failed += ch.counts.failed;
    c.skipped += ch.counts.skipped;
    c.notRun += ch.counts.notRun;
    c.running += ch.counts.running;
    c.queued += ch.counts.queued;
    c.pending += ch.counts.pending;
    c.outdated += ch.counts.outdated;
    c.flaky += ch.counts.flaky;
    c.pendingPassed += ch.counts.pendingPassed;
    c.pendingFailed += ch.counts.pendingFailed;
    c.pendingSkipped += ch.counts.pendingSkipped;
    c.stale += ch.counts.stale;
    c.quarantined += ch.counts.quarantined;
    d += ch.durationMs;
  }
  n.counts = c;
  n.durationMs = d;
  n.status = aggregate(c);
  n.outdated = c.outdated > 0 && c.outdated === c.total - c.notRun;
  if (n.project) {
    const ps = n.project.status;
    if (ps === "building" || ps === "discovering") n.status = ps;
    else if (ps === "error") n.status = "error";
  }
}

// ---- duration sort ------------------------------------------------------------------------------------------

export type TreeSort = "default" | "slowest" | "fastest";

const sortedCache = new WeakMap<readonly TreeNode[], { rank: ReadonlyMap<string, number>; sorted: TreeNode[] }>();

/** `nodes` in rank order (cached per sibling array + rank map; stable for unranked nodes). */
function ordered(nodes: TreeNode[], rank: ReadonlyMap<string, number> | undefined): readonly TreeNode[] {
  if (!rank || nodes.length < 2) return nodes;
  const c = sortedCache.get(nodes);
  if (c?.rank === rank) return c.sorted;
  const sorted = nodes
    .map((n, i) => [n, i] as const)
    .sort(([a, i], [b, j]) => (rank.get(a.key) ?? Infinity) - (rank.get(b.key) ?? Infinity) || i - j)
    .map(([n]) => n);
  sortedCache.set(nodes, { rank, sorted });
  return sorted;
}

/** a duration to sort by, or undefined (not run / skipped / no duration: always last) */
function sortDuration(n: TreeNode): number | undefined {
  if (n.test) {
    const o = n.test.outcome;
    return n.test.durationMs != null && o !== "skipped" && o !== "notRun" && o !== "queued" && o !== "running" ? n.test.durationMs : undefined;
  }
  return n.durationMs > 0 ? n.durationMs : undefined;
}

function compareBy(mode: TreeSort) {
  return (a: readonly [TreeNode, number], b: readonly [TreeNode, number]) => {
    const da = sortDuration(a[0]);
    const db = sortDuration(b[0]);
    if (da == null && db == null) return a[1] - b[1];
    if (da == null) return 1;
    if (db == null) return -1;
    return (mode === "slowest" ? db - da : da - db) || a[1] - b[1];
  };
}

/**
 * Sibling ranks for a duration sort: leaves by their duration, groups by the sum of their children (n.durationMs);
 * no duration → last; ties keep the default order. `flat`: all leaves ranked against each other (flat list mode).
 */
export function sortRanks(roots: TreeNode[], mode: TreeSort): { rank: Map<string, number>; flatRank: Map<string, number> } | undefined {
  if (mode === "default") return undefined;
  const cmp = compareBy(mode);
  const rank = new Map<string, number>();
  const leaves: [TreeNode, number][] = [];
  const visit = (nodes: TreeNode[]) => {
    nodes
      .map((n, i) => [n, i] as const)
      .sort(cmp)
      .forEach(([n], i) => rank.set(n.key, i));
    for (const n of nodes) {
      if (n.test) leaves.push([n, leaves.length]);
      else if (n.children.length) visit(n.children);
    }
  };
  visit(roots);
  const flatRank = new Map<string, number>();
  leaves.sort(cmp);
  leaves.forEach(([n], i) => flatRank.set(n.key, i));
  return { rank, flatRank };
}

/** The tests the tree shows for a filter (search text + status chip): "Run N matching". */
export function matchingTests(roots: readonly TreeNode[], text: string, status: StatusFilter, newKeys?: ReadonlySet<string>): TestNode[] {
  return filterPass(roots, text, status, newKeys).tests;
}

// ---- search syntax ----------------------------------------------------------------------------------------------

/** Search tokens of the tree filter (also the syntax hint). Free text matches name, class, namespace, project, traits. */
export const QUERY_TOKENS: { token: string; example: string; what: string }[] = [
  { token: "is:", example: "is:failed", what: "failed · passed · skipped · flaky · new · slow · notrun · snapshot" },
  { token: "cat:", example: "cat:Integration", what: "a trait value (TestCategory, …)" },
  { token: "trait:", example: "trait:Owner=web-team", what: "a trait key (=value)" },
  { token: "msg:", example: "msg:timeout", what: "text in the error message, stack trace or output" },
  { token: "proj:", example: "proj:Inventory", what: "project name" },
];

const IS_VALUES = ["failed", "passed", "skipped", "flaky", "new", "slow", "notrun", "running", "snapshot"] as const;

/** Split on whitespace, keeping "quoted values" (also after a token prefix: msg:"no such host"). */
function words(text: string): string[] {
  const out: string[] = [];
  const n = text.length;
  let i = 0;
  while (i < n) {
    if (isSpace(text[i])) {
      i++;
      continue;
    }
    // a run of non-space characters up to the first quote (if any)
    let j = i;
    while (j < n && text[j] !== '"' && !isSpace(text[j])) j++;
    if (text[j] === '"') {
      // a quoted part: up to the closing quote or the end; the text before the quote is a prefix (`cat:"a b"`)
      const close = text.indexOf('"', j + 1);
      const end = close < 0 ? n : close;
      out.push(text.slice(i, j) + text.slice(j + 1, end));
      i = end + 1;
    } else {
      out.push(text.slice(i, j));
      i = j;
    }
  }
  return out.filter(Boolean);
}

/** `ch` is whitespace as `\s` matches it in a regular expression. */
const isSpace = (ch: string) => /\s/.test(ch);

/**
 * The tree filter as a predicate on test leaves, or undefined for an empty query. Every term must match (AND):
 * `is:failed|flaky` (alternatives with | or ,), `cat:<trait value>`, `trait:key=value`, `msg:<text>`, `proj:<name>`,
 * free text (each word in the test's search text). Unknown prefixes are free text.
 */
export function compileQuery(text: string, ctx: { newKeys?: ReadonlySet<string> } = {}): ((n: TreeNode) => boolean) | undefined {
  return compileTerms(parseQuery(text), ctx);
}

/** One term of a query: free text (`kind` "text") or a token with its lowercase value (never empty). */
export interface QueryTerm {
  kind: "text" | "is" | "cat" | "trait" | "msg" | "proj";
  v: string;
}

/** The terms of a query (tokens without a value yet, like "is:" while typing, are left out: no constraint). */
export function parseQuery(text: string): QueryTerm[] {
  const out: QueryTerm[] = [];
  for (const w of words(text.trim())) {
    const m = /^(is|cat|trait|msg|proj):(.*)$/i.exec(w);
    const v = (m?.[2] ?? w).toLowerCase();
    if (!m) out.push({ kind: "text", v });
    else if (v) out.push({ kind: m[1].toLowerCase() as QueryTerm["kind"], v });
  }
  return out;
}

/**
 * Every leaf matching `terms` also matches `base` (so a pass for `terms` only needs to test the leaves `base` matched):
 * each base term has a term of the same kind that is at least as strict. Substring terms (free text, cat:, msg:, proj:)
 * are stricter when their value contains the base's ("ord" → "order"); `is:` and `trait:` keys only when equal (is:f is
 * failed, is:fl is flaky).
 */
export function implies(terms: readonly QueryTerm[], base: readonly QueryTerm[]): boolean {
  return base.every((b) =>
    terms.some((t) => {
      if (t.kind !== b.kind) return false;
      if (t.kind === "is") return t.v === b.v;
      if (t.kind === "trait") {
        const [tk, tv] = t.v.split("=", 2);
        const [bk, bv] = b.v.split("=", 2);
        return tk === bk && (bv === undefined || tv?.includes(bv) === true);
      }
      return t.v.includes(b.v);
    }),
  );
}

function compileTerms(terms: readonly QueryTerm[], ctx: { newKeys?: ReadonlySet<string> } = {}): ((n: TreeNode) => boolean) | undefined {
  const preds: ((n: TreeNode, t: TestNode) => boolean)[] = [];
  for (const { kind, v } of terms) {
    if (kind === "text") {
      preds.push((n) => n.search.includes(v));
      continue;
    }
    switch (kind) {
      case "is": {
        // resolved once per query, not per leaf
        const alts = v.split(/[|,]/).filter(Boolean).map((a) => IS_VALUES.find((k) => k.startsWith(a.replace(/[^a-z]/g, ""))));
        preds.push((n, t) =>
          alts.some((x) => {
            if (x === "failed") return n.counts.failed > 0; // like the Failed chip: quarantined failures excluded
            if (x === "passed" || x === "skipped") return bucket(t.outcome) === x;
            if (x === "flaky") return !!t.flaky;
            if (x === "snapshot") return !!t.hasSnapshotMismatch; // Verify snapshot mismatch pending
            if (x === "new") return !!ctx.newKeys?.has(testKey(t));
            if (x === "slow") return slowFactor(t) !== undefined;
            if (x === "notrun") return bucket(t.outcome) === "notRun";
            if (x === "running") return t.outcome === "running" || t.outcome === "queued";
            return false;
          }),
        );
        break;
      }
      case "cat":
        preds.push((_n, t) => traitsOf(t).some((x) => x.value.toLowerCase().includes(v)));
        break;
      case "trait": {
        const [key, val] = v.split("=", 2);
        preds.push((_n, t) => traitsOf(t).some((x) => x.key.toLowerCase() === key && (val === undefined || x.value.toLowerCase().includes(val))));
        break;
      }
      case "msg":
        preds.push((_n, t) => [t.errorMessage, t.stackTrace, t.stdOut, t.stdErr].some((s) => !!s && s.toLowerCase().includes(v)));
        break;
      case "proj":
        preds.push((n, t) => t.projectId.toLowerCase().includes(v) || !!n.project?.name.toLowerCase().includes(v));
        break;
    }
  }
  if (!preds.length) return undefined;
  if (preds.length === 1) {
    const p = preds[0];
    return (n) => !!n.test && p(n, n.test);
  }
  return (n) => !!n.test && preds.every((p) => p(n, n.test!));
}

// ---- slow tests (F6) --------------------------------------------------------------------------------------------

function quantile(sorted: number[], q: number) {
  if (!sorted.length) return 0;
  const pos = (sorted.length - 1) * q;
  const lo = Math.floor(pos);
  const hi = Math.ceil(pos);
  return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
}

/** F6: the latest duration is > 2× the median of the earlier results (≥ 3 of them, 20 ms minimum to ignore noise). */
export function slowFactor(t: TestNode): number | undefined {
  const d = t.durationMs;
  if (d == null || d < 20 || !t.history || t.outcome === "skipped" || t.outcome === "running" || t.outcome === "queued") return undefined;
  const hist = t.history;
  const prior = (hist.length && hist.at(-1)!.at === t.lastRunAt ? hist.slice(0, -1) : hist)
    .filter((e) => e.outcome !== "skipped" && e.durationMs != null)
    .map((e) => e.durationMs!)
    .sort((a, b) => a - b);
  if (prior.length < 3) return undefined;
  const median = quantile(prior, 0.5);
  return median > 0 && d > 2 * median ? d / median : undefined;
}

// ---- grouping ---------------------------------------------------------------------------------------------------

/** How the tree groups tests. "namespace" = Project > Namespace > Class > Method (the default). */
export type TreeGroup = "namespace" | "project" | "trait" | "outcome";
export const GROUP_LABELS: Record<TreeGroup, string> = { namespace: "Namespace", project: "Project", trait: "Trait", outcome: "Outcome" };

const OUTCOME_GROUPS: { id: ReturnType<typeof bucket>; label: string }[] = [
  { id: "failed", label: "Failed" },
  { id: "running", label: "Running" },
  { id: "queued", label: "Queued" },
  { id: "notRun", label: "Not run" },
  { id: "passed", label: "Passed" },
  { id: "skipped", label: "Skipped" },
];

/** A leaf under a synthetic group: its full name, the class (and method) as dimmed context. */
function groupLeaf(n: TreeNode, parent: TreeNode): TreeNode {
  const t = n.test!;
  const method = n.parent?.kind === "method" ? n.parent.label : undefined;
  return { ...n, parent, label: t.displayName, sublabel: [t.className, method && t.displayName.startsWith(method) ? undefined : method].filter(Boolean).join(".") || t.namespace, children: [] };
}

function groupNode(key: string, label: string): TreeNode {
  return { key, kind: "group", label, projectId: "", children: [], counts: emptyCounts(), status: "notRun", durationMs: 0, search: label.toLowerCase() };
}

/** Trait groups by label, "No traits" last. */
function compareTraitGroups(a: TreeNode, b: TreeNode): number {
  if (a.key === "g:trait:") return 1;
  if (b.key === "g:trait:") return -1;
  return a.label.localeCompare(b.label);
}

/**
 * The tree regrouped for `mode` (new nodes; leaves are copies with the same keys, so the selection, deep links and the
 * detail pane keep working). "project": Project > Class > Method (namespaces dropped, the namespace becomes the class's
 * context); "trait": one group per combination of trait values; "outcome": Failed / Not run / Passed / Skipped.
 */
export function regroup(roots: readonly TreeNode[], mode: TreeGroup): TreeNode[] {
  if (mode === "namespace") return roots as TreeNode[];
  if (mode === "project") {
    const copy = (n: TreeNode, parent: TreeNode, sublabel?: string): TreeNode => {
      const c: TreeNode = { ...n, parent, sublabel: sublabel ?? n.sublabel, children: [] };
      c.children = n.children.map((ch) => copy(ch, c));
      return c;
    };
    return roots.map((r) => {
      const p: TreeNode = { ...r, children: [] };
      const lift = (n: TreeNode, ns?: string) => {
        for (const ch of n.children) {
          if (ch.kind === "namespace") lift(ch, ch.label);
          else p.children.push(copy(ch, p, ch.kind === "class" ? ns : undefined));
        }
      };
      lift(r);
      p.children.sort((a, b) => a.label.localeCompare(b.label));
      finishGroup(p);
      return p;
    });
  }
  const groups = new Map<string, TreeNode>();
  const add = (key: string, label: string, n: TreeNode) => {
    let g = groups.get(key);
    if (!g) {
      g = groupNode(key, label);
      groups.set(key, g);
    }
    g.children.push(groupLeaf(n, g));
  };
  const walk = (n: TreeNode) => {
    if (n.test) {
      const t = n.test;
      if (mode === "trait") {
        const vals = [...new Set(traitsOf(t).map((x) => x.value))].sort((a, b) => a.localeCompare(b));
        add(vals.length ? `g:trait:${vals.join("|")}` : "g:trait:", vals.length ? vals.join(" · ") : "No traits", n);
      } else {
        const b = n.counts.quarantined ? "failed" : bucket(t.outcome);
        add(`g:outcome:${b}`, OUTCOME_GROUPS.find((o) => o.id === b)!.label, n);
      }
      return;
    }
    n.children.forEach(walk);
  };
  roots.forEach(walk);
  let out = [...groups.values()];
  if (mode === "outcome") out = OUTCOME_GROUPS.map((o) => groups.get(`g:outcome:${o.id}`)).filter((g): g is TreeNode => !!g);
  else out.sort(compareTraitGroups);
  out.forEach(finishGroup);
  return out;
}

/**
 * The tree key of the tests declared at one source line: the test (`t:`) or, for data-driven tests, their method node
 * (`m:`) — the same keys buildTree creates.
 */
export function treeKeyOf(tests: readonly TestNode[]): string {
  const t0 = tests[0];
  const method = t0.methodName ?? t0.displayName;
  const single = tests.length === 1 && (t0.displayName === method || !t0.methodName);
  return single ? `t:${testKey(t0)}` : `m:${t0.projectId}:${t0.namespace ?? ""}.${t0.className ?? ""}.${method}`;
}
