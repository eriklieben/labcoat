// The mock host's side of docs/contract.md "Round 7": full entries only for new tests and structural changes, compact
// `testResults` patches for everything else (same rules as src/Labcoat/TestUpdateEncoder.cs), and the file table of
// snapshots.
import type { ExplorerState, HistoryEntry, ServerEvent, TestNode, TestResultPatch } from "../contract";
import { MAX_HISTORY } from "../test-results";

const key = (t: { projectId: string; id: string }) => `${t.projectId}|${t.id}`;

/** Same structure (what the tree is built from): a result-only change. */
export function sameShape(a: TestNode, b: TestNode): boolean {
  return (
    a.displayName === b.displayName &&
    a.namespace === b.namespace &&
    a.className === b.className &&
    a.methodName === b.methodName &&
    a.filePath === b.filePath &&
    a.line === b.line &&
    a.lineEnd === b.lineEnd &&
    (a.traits === b.traits || (a.traits.length === b.traits.length && a.traits.every((x, i) => x.key === b.traits[i].key && x.value === b.traits[i].value)))
  );
}

const RESULT_FIELDS = [
  "durationMs",
  "errorMessage",
  "errorType",
  "stackTrace",
  "stdOut",
  "stdErr",
  "outputTruncated",
  "lastRunAt",
  "traceId",
  "stale",
  "flaky",
  "hasSnapshotMismatch",
] as const satisfies readonly (keyof TestNode)[];

const sameEntry = (a: HistoryEntry, b: HistoryEntry) =>
  a.outcome === b.outcome && a.durationMs === b.durationMs && a.at === b.at && a.runId === b.runId && (a as { flaky?: boolean }).flaky === (b as { flaky?: boolean }).flaky;

/** `b` is `a` plus one entry (the oldest dropped at the cap). */
export function isAppend(a: HistoryEntry[] | undefined, b: HistoryEntry[]): boolean {
  const p = a?.length ?? 0;
  if (b.length === p + 1) return a ? a.every((e, i) => sameEntry(e, b[i])) : true;
  return !!a && p > 0 && b.length === p && p >= MAX_HISTORY && a.slice(1).every((e, i) => sameEntry(e, b[i]));
}

/** The patch from `a` to `b` (undefined when nothing changed). `run.id`: the event's run id for `recorded` (set by the first). */
export function diffResult(a: TestNode, b: TestNode, run: { id?: string }): TestResultPatch | undefined {
  const p: TestResultPatch = { id: b.id };
  let changed = false;
  const clear: (keyof TestNode)[] = [];
  if (a.outcome !== b.outcome) ((p.outcome = b.outcome), (changed = true));
  for (const f of RESULT_FIELDS) {
    if (a[f] === b[f]) continue;
    changed = true;
    if (b[f] === undefined || b[f] === null) clear.push(f);
    else (p as unknown as Record<string, unknown>)[f] = b[f];
  }
  const ha = a.history, hb = b.history;
  if (ha !== hb && !(ha?.length === hb?.length && (ha ?? []).every((e, i) => sameEntry(e, hb![i])))) {
    changed = true;
    if (!hb?.length) clear.push("history");
    else if (isAppend(ha, hb)) {
      const e = hb[hb.length - 1];
      if (e.runId && !(e as { flaky?: boolean }).flaky && e.outcome === b.outcome && e.durationMs === b.durationMs && e.at === b.lastRunAt && (!run.id || run.id === e.runId)) {
        run.id = e.runId;
        p.recorded = true;
      } else p.historyAdd = e;
    } else p.history = hb;
  }
  if (!changed) return undefined;
  if (clear.length) p.clear = clear;
  return p;
}

/**
 * The events for a batch of updated nodes: a `testsUpdated` with the new / restructured ones and a `testResults` with
 * patches against `sent` (the copy the client has: the last emitted one), which is updated to (shallow copies of) `nodes`.
 */
export function encodeUpdates(nodes: TestNode[], sent: Map<string, TestNode>): ServerEvent[] {
  const full: TestNode[] = [];
  const groups = new Map<string, TestResultPatch[]>();
  const run: { id?: string } = {};
  for (const n of nodes) {
    const k = key(n);
    const prev = sent.get(k);
    const copy = { ...n };
    sent.set(k, copy);
    if (!prev || !sameShape(prev, copy)) {
      full.push(copy);
      continue;
    }
    const p = diffResult(prev, copy, run);
    if (!p) continue;
    let g = groups.get(n.projectId);
    if (!g) groups.set(n.projectId, (g = []));
    g.push(p);
  }
  const out: ServerEvent[] = [];
  if (full.length) out.push({ type: "testsUpdated", tests: full });
  if (groups.size) out.push({ type: "testResults", projects: [...groups].map(([projectId, results]) => ({ projectId, results })), ...(run.id ? { runId: run.id } : {}) });
  return out;
}

/** A snapshot's tests as the client gets them: each source file once in `files`, tests point to it (`fileIndex`). */
export function withFileTable(state: ExplorerState): ExplorerState {
  const files: string[] = [];
  const index = new Map<string, number>();
  const tests = state.tests.map((t) => {
    const c: TestNode = { ...t };
    if (t.filePath !== undefined) {
      let i = index.get(t.filePath);
      if (i === undefined) (index.set(t.filePath, (i = files.length)), files.push(t.filePath));
      c.fileIndex = i;
      delete c.filePath;
    }
    return c;
  });
  return { ...state, tests, files };
}

/** `sent` after a snapshot: what the client has now (shallow copies; the mock mutates its nodes in place). */
export function resetSent(state: ExplorerState, sent: Map<string, TestNode>) {
  sent.clear();
  for (const t of state.tests) sent.set(key(t), { ...t });
}
