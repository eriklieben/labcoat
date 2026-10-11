/**
 * The tests running right now and for how long (server event `runningNow`, ExplorerState.runningNow; docs/contract.md
 * "Running now, slow tests and timeouts"). The host sends the longest-running tests when that list changes (at most
 * about once a second); elapsed times are advanced here, from when the event arrived, so the client's clock never has
 * to agree with the host's.
 */
import { computed, effect, onCleanup, signal, type ReadSignal } from "@eriklieben/labcoat-design-system/core";
import type { RunningNowInfo } from "./contract";
import { serverEventListeners } from "./server-events";

export interface RunningTestView {
  projectId: string;
  id: string;
  displayName: string;
  elapsedMs: number;
  /** running longer than the hang threshold ("possibly hanging") */
  slow: boolean;
}

export interface RunningNowView {
  count: number;
  tests: RunningTestView[];
  hangThresholdMs?: number;
  testTimeoutMs?: number;
}

const info = signal<{ value: RunningNowInfo; at: number } | undefined>(undefined);
const now = signal(Date.now());

/** Applies a server event (exported for tests; the module subscribes itself). */
export function applyRunningNowEvent(e: { type: string } & Record<string, unknown>, at = Date.now()) {
  if (e.type === "runningNow") {
    const v = e.runningNow as RunningNowInfo | null | undefined;
    info.set(v ? { value: v, at } : undefined);
  } else if (e.type === "state") {
    const st = e.state as { runningNow?: RunningNowInfo; currentRun?: unknown } | undefined;
    info.set(st?.runningNow && st.currentRun ? { value: st.runningNow, at } : undefined);
  } else if (e.type === "runFinished") info.set(undefined);
}
serverEventListeners.add((e) => applyRunningNowEvent(e));

/** The view at time `t`: elapsed advanced since the event, slow when past the threshold. */
export function runningNowAt(v: { value: RunningNowInfo; at: number } | undefined, t: number): RunningNowView | undefined {
  if (!v || v.value.tests.length === 0) return undefined;
  const delta = Math.max(0, t - v.at);
  const threshold = v.value.hangThresholdMs;
  return {
    count: v.value.count,
    hangThresholdMs: threshold,
    testTimeoutMs: v.value.testTimeoutMs,
    tests: v.value.tests.map((x) => {
      const elapsedMs = x.elapsedMs + delta;
      return { projectId: x.projectId, id: x.id, displayName: x.displayName, elapsedMs, slow: !!x.slow || (threshold != null && elapsedMs >= threshold) };
    }),
  };
}

// a 1 s tick only while something runs
effect(() => {
  if (!info()) return;
  now.set(Date.now());
  const id = window.setInterval(() => now.set(Date.now()), 1000);
  onCleanup(() => window.clearInterval(id));
});

/** The running tests, longest first, with elapsed times that tick once a second; undefined when nothing runs. */
export const runningNow: ReadSignal<RunningNowView | undefined> = computed(() => runningNowAt(info(), now()));

/** "1:05" / "12 s" style elapsed time for the running-now list. */
export function formatElapsed(ms: number): string {
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}:${String(s % 60).padStart(2, "0")}`;
  return `${Math.floor(m / 60)}:${String(m % 60).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}
