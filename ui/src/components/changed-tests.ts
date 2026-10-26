/**
 * CHANGED: the tests added or modified on the current branch (the "Changed" chip, `is:changed`).
 *
 *     changedKeys()          testKeys of those tests (empty outside a repository or while nothing changed)
 *     changedAvailable()     the workspace is in a repository (GET /api/changed-tests answered `available: true`)
 *     refreshChanged()       re-reads GET /api/changed-tests
 *     watchChanged()         refreshes now, on window focus, after each run and after each (re)discovery; returns its stop
 *
 * The host does the git work (its answer is cached ~2 s); the set is replaced only when it changed, so an equal answer
 * does not re-run the tree filter.
 */
import { computed, signal } from "@eriklieben/labcoat-design-system";
import type { ChangedTests } from "../contract";
import { getStore, serverEventListeners, testKey } from "../store";

const state = signal<{ available: boolean; keys: ReadonlySet<string> }>({ available: false, keys: new Set() });

/** testKeys of the changed tests. */
export const changedKeys = computed(() => state().keys);
/** The workspace is in a git repository (the chip is hidden otherwise). */
export const changedAvailable = computed(() => state().available);

type ChangedTransport = { fetchChangedTests?: (signal?: AbortSignal) => Promise<ChangedTests> };

export async function fetchChangedTests(signal?: AbortSignal): Promise<ChangedTests> {
  const store = getStore();
  const t = store.transport as unknown as ChangedTransport;
  if (t.fetchChangedTests) return t.fetchChangedTests(signal);
  if (store.get().transport === "mock") return (await import("../mock/affected")).mockChangedTests([...store.get().tests.values()]);
  const res = await fetch(new URL("./api/changed-tests", location.href), { signal });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return (await res.json()) as ChangedTests;
}

/** Applies an answer; an unchanged set keeps the old one (identity: filter passes are cached by it). */
export function setChangedTests(c: ChangedTests) {
  const keys = new Set(c.available ? c.tests.map(testKey) : []);
  const old = state.peek();
  if (old.available === c.available && old.keys.size === keys.size && [...keys].every((k) => old.keys.has(k))) return;
  state.set({ available: c.available, keys });
}

let abort: AbortController | undefined;
/** Re-reads GET /api/changed-tests (errors keep the last answer: an old host without the endpoint, offline). */
export async function refreshChanged(): Promise<void> {
  abort?.abort();
  const ac = (abort = new AbortController());
  try {
    const c = await fetchChangedTests(ac.signal);
    if (!ac.signal.aborted) setChangedTests(c);
  } catch {
    /* keep what we had */
  }
}

/** Keeps `changedKeys` current: on mount, on window focus, after a run and after a (re)discovery. */
export function watchChanged(): () => void {
  void refreshChanged();
  const onFocus = () => void refreshChanged();
  window.addEventListener("focus", onFocus);
  const onEvent = (e: { type: string }) => {
    if (e.type === "runFinished" || e.type === "state") void refreshChanged();
  };
  serverEventListeners.add(onEvent);
  return () => {
    window.removeEventListener("focus", onFocus);
    serverEventListeners.delete(onEvent);
  };
}
