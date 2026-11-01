/**
 * The slowest tests of a project with their duration trend (GET /api/tests/slowest; docs/contract.md "Slowest tests").
 * Mock mode answers through the transport's `fetchSlowest`, like the coverage line lookup.
 */
import type { HistoryEntry, SlowestTestsResponse, SlowTest } from "./contract";
import { getStore } from "./store";

type SlowestTransport = { fetchSlowest?: (projectId: string, limit: number, signal?: AbortSignal) => Promise<SlowestTestsResponse> };

export async function fetchSlowestTests(projectId: string, limit = 10, signal?: AbortSignal): Promise<SlowestTestsResponse> {
  const t = getStore().transport as unknown as SlowestTransport;
  if (t.fetchSlowest) return t.fetchSlowest(projectId, limit, signal);
  const q = new URLSearchParams({ projectId, limit: String(limit) });
  const res = await fetch(new URL(`./api/tests/slowest?${q}`, location.href), { signal });
  if (!res.ok) throw Object.assign(new Error(`${res.status} ${res.statusText || ""}`.trim()), { status: res.status });
  return (await res.json()) as SlowestTestsResponse;
}

/** The trend of a slow test as history entries, for the sparkline of the history strip. */
export function trendEntries(t: SlowTest): HistoryEntry[] {
  return t.trend.map((p) => ({ outcome: p.outcome, durationMs: p.durationMs, at: p.at }));
}

/** "2.3× median", "as usual" (within 25 %), "no earlier runs". */
export function trendLabel(t: SlowTest): string {
  if (t.factor == null) return "no earlier runs";
  if (t.factor >= 1.25) return `${t.factor.toFixed(1)}× median`;
  if (t.factor <= 0.8) return `${t.factor.toFixed(1)}× median (faster)`;
  return "as usual";
}
