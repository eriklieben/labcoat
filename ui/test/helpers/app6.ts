/**
 * Helpers for the suite-status / log-panel / build-errors / diagnostics / watch-strip tests. Some app modules keep
 * module-level computeds that call getStore() when first evaluated (runProgress, repeatProgress, outputErrorCount,
 * run-diff's subset run ids) and then stay bound to that store. So these tests use ONE store per test file and reset
 * it to a fresh snapshot before each test.
 */
import type { ExplorerState } from "../../src/contract";
import { Store, setStore } from "../../src/store";
import { FakeTransport, flushFrame, stateEvent } from "./app";

let shared: Store | undefined;

/** The file's store, reset to `state` (no logs, no runs unless given) on a fresh connected FakeTransport; published. */
export async function freshStore(state: Partial<ExplorerState> = {}, opts: { kind?: "ws" | "mock"; open?: boolean } = {}) {
  shared ??= new Store(opts.kind ?? "ws");
  const store = shared;
  setStore(store);
  const transport = new FakeTransport(opts.kind ?? "ws");
  store.attach(transport);
  transport.setStatus(opts.open === false ? "connecting" : "open");
  store.viewRun(undefined);
  store.logPanelOpen = false;
  store.apply(stateEvent({ runs: [], logs: [], ...state }));
  await flushFrame();
  return { store, transport };
}
