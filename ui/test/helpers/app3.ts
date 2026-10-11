/**
 * Helpers for the detail-pane / code-view / run-overview tests. Several app modules keep module-level computeds that
 * call getStore() once (repeatProgress, run-diff's subset run ids): they bind to the first store they see. So these
 * tests use ONE store per test file and reset its state before each test (resetStore) instead of a new store per test.
 */
import type { ExplorerState } from "../../src/contract";
import { Store, setStore } from "../../src/store";
import { FakeTransport, flushFrame, stateEvent } from "./app";

let shared: Store | undefined;

/**
 * The file's shared store, reset to a fresh snapshot (`state`) on a new FakeTransport of `kind` (connected unless
 * `open: false`). Published (one frame flushed) before it returns.
 */
export async function resetStore(state: Partial<ExplorerState> = {}, opts: { kind?: "ws" | "mock"; open?: boolean } = {}) {
  shared ??= new Store(opts.kind ?? "ws");
  const store = shared;
  setStore(store);
  const transport = new FakeTransport(opts.kind ?? "ws");
  store.attach(transport);
  transport.setStatus(opts.open === false ? "connecting" : "open");
  store.viewRun(undefined);
  store.apply(stateEvent({ runs: [], logs: [], ...state }));
  await flushFrame();
  return { store, transport };
}

/** Resolve with a fetch Response-like object. */
export function jsonResponse(body: unknown, init: { status?: number; statusText?: string } = {}) {
  const status = init.status ?? 200;
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: init.statusText ?? "",
    json: async () => body,
    text: async () => (typeof body === "string" ? body : JSON.stringify(body)),
  } as unknown as Response;
}
