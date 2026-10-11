// Wire helpers for test entries (docs/contract.md "Round 7 additions"): lean snapshot fields and `testResults` patches.
import type { HistoryEntry, TestNode, TestResultPatch } from "./contract";

/** History entries kept per test (the host's cap). */
export const MAX_HISTORY = 20;

const NO_TRAITS = Object.freeze([]) as unknown as TestNode["traits"];

/**
 * Wire → store, in place: a lean snapshot's `fileIndex` becomes `filePath` (the shared string of `files`), an omitted
 * `traits` becomes []. Everything in the UI reads `filePath` / `traits` as before.
 */
export function normalizeTest(t: TestNode, files?: readonly string[]): TestNode {
  if (t.fileIndex !== undefined && files) t.filePath = files[t.fileIndex];
  if (!t.traits) t.traits = NO_TRAITS;
  return t;
}

/** `h` plus `e`, keeping the last MAX_HISTORY entries; `h` itself when it already ends with that entry (a replay). */
export function appendHistory(h: HistoryEntry[] | undefined, e: HistoryEntry): HistoryEntry[] {
  const last = h?.[h.length - 1];
  if (last?.at === e.at && last.runId === e.runId) return h!;
  if (!h) return [e];
  const out = h.length >= MAX_HISTORY ? h.slice(h.length - MAX_HISTORY + 1) : h.slice();
  out.push(e);
  return out;
}

/**
 * `prev` with a `testResults` patch applied: present fields set, `clear`ed ones removed (set to undefined), the history
 * appended to for `historyAdd` / `recorded` (`runId`: the event's). Returns a new object; `prev` is not changed.
 */
export function applyResultPatch(prev: TestNode, p: TestResultPatch, runId?: string): TestNode {
  const n: Record<string, unknown> = { ...prev };
  if (p.clear) for (const f of p.clear) n[f] = undefined;
  for (const key in p) if (key !== "id" && key !== "clear" && key !== "historyAdd" && key !== "recorded") n[key] = (p as unknown as Record<string, unknown>)[key];
  const t = n as unknown as TestNode;
  if (p.historyAdd) t.history = appendHistory(p.history ?? prev.history, p.historyAdd);
  else if (p.recorded) {
    const e: HistoryEntry = { outcome: t.outcome, at: t.lastRunAt ?? "" };
    if (t.durationMs !== undefined) e.durationMs = t.durationMs;
    if (runId !== undefined) e.runId = runId;
    t.history = appendHistory(p.history ?? prev.history, e);
  }
  return t;
}

/** The patch only sets `outcome` (e.g. the queued / running updates of a run). */
export function onlyOutcome(p: TestResultPatch): boolean {
  for (const key in p) if (key !== "id" && key !== "outcome") return false;
  return true;
}
