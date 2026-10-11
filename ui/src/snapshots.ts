/**
 * Verify snapshot testing (docs/contract.md "Round 6 → Snapshot testing"): a failed test with
 * `hasSnapshotMismatch` has received files waiting to replace (or create) its verified snapshots.
 *
 *   GET  /api/tests/snapshots?projectId&id   → TestSnapshots (the pending files)
 *   GET  /api/snapshots/file?path            → text, or the image bytes (snapshotFileUrl for <img>)
 *   POST /api/snapshots/accept|reject        { projectId?, id?, names?, rerun? } (no id = every test with a mismatch)
 *
 * With ?mock the calls are answered by mock/snapshots.ts (`?mock&snapshots` adds the snapshot tests).
 */
import { computed, toast } from "@eriklieben/labcoat-design-system";
import type { SnapshotActionResult, TestNode, TestSnapshots } from "./contract";
import { getStore } from "./store";

const isMock = () => getStore().get().transport === "mock";
const mockModule = () => import("./mock/snapshots");
const url = (path: string) => new URL(path, location.href);

export const hasSnapshotMismatch = (t: TestNode | undefined): boolean => !!t?.hasSnapshotMismatch;

const pendingByStore = new WeakMap<object, () => TestNode[]>();
/** Tests with pending snapshots (live state, reactive when read in a binding), in order of arrival. */
export function pendingSnapshotTests(): TestNode[] {
  const store = getStore();
  let c = pendingByStore.get(store);
  if (!c) {
    c = computed(() => {
      const out: TestNode[] = [];
      for (const t of store.$.tests().values()) if (t.hasSnapshotMismatch) out.push(t);
      return out;
    });
    pendingByStore.set(store, c);
  }
  return c();
}

export async function fetchSnapshots(t: { projectId: string; id: string }, signal?: AbortSignal): Promise<TestSnapshots> {
  if (isMock()) return (await mockModule()).mockSnapshots(t.projectId, t.id);
  const q = new URLSearchParams({ projectId: t.projectId, id: t.id });
  const res = await fetch(url(`./api/tests/snapshots?${q}`), { signal });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return (await res.json()) as TestSnapshots;
}

/** The text of a received / verified file; undefined when it doesn't exist (a new snapshot's verified file). */
export async function fetchSnapshotText(path: string, signal?: AbortSignal): Promise<string | undefined> {
  if (isMock()) return (await mockModule()).mockSnapshotFile(path);
  const res = await fetch(url(`./api/snapshots/file?path=${encodeURIComponent(path)}`), { signal });
  if (res.status === 404) return undefined;
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.text();
}

/** URL of an image snapshot for <img src>. Mock: the data: URL (undefined when the file doesn't exist). */
export async function snapshotFileUrl(path: string): Promise<string | undefined> {
  if (isMock()) return (await mockModule()).mockSnapshotFile(path);
  return url(`./api/snapshots/file?path=${encodeURIComponent(path)}`).href;
}

export interface SnapshotRequest {
  projectId?: string;
  id?: string;
  names?: string[];
  /** accept only: run the tests again afterwards (default true) */
  rerun?: boolean;
}

async function resolve(req: SnapshotRequest, accept: boolean): Promise<SnapshotActionResult> {
  const store = getStore();
  if (isMock()) {
    const m = await mockModule();
    const mock = (window as unknown as { __labcoatMock?: { emit?: (e: unknown) => void } }).__labcoatMock;
    const r = m.mockResolveSnapshots(req, accept, (tests) => mock?.emit?.({ type: "testsUpdated", tests }));
    const ran = r.tests.filter((t) => t.files.length);
    if (accept && req.rerun !== false && ran.length) {
      const pids = [...new Set(ran.map((t) => t.projectId))];
      store.send({ type: "run", projectId: pids.length === 1 ? pids[0] : undefined, testIds: ran.map((t) => t.id) });
    }
    return r;
  }
  const res = await fetch(url(`./api/snapshots/${accept ? "accept" : "reject"}`), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(req),
  });
  const body = (await res.json().catch(() => ({}))) as SnapshotActionResult & { error?: string };
  if (!res.ok) throw new Error(body.error ?? `HTTP ${res.status}`);
  return body;
}

const count = (r: SnapshotActionResult) => r.tests.reduce((n, t) => n + t.files.length, 0);
const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? "" : "s"}`;

/** Accept (received → verified) and, by default, re-run; reports the outcome as a toast. */
export async function acceptSnapshots(req: SnapshotRequest): Promise<SnapshotActionResult | undefined> {
  return act(req, true);
}

/** Reject: delete the received files (the verified snapshots stay). */
export async function rejectSnapshots(req: SnapshotRequest): Promise<SnapshotActionResult | undefined> {
  return act(req, false);
}

/** "Accept all snapshots": every test with a pending snapshot mismatch. */
export function acceptAllSnapshots(): Promise<SnapshotActionResult | undefined> {
  return act({}, true);
}

async function act(req: SnapshotRequest, accept: boolean): Promise<SnapshotActionResult | undefined> {
  try {
    const r = await resolve(req, accept);
    const n = count(r);
    const failed = r.errors?.length ?? 0;
    if (failed) toast(`${accept ? "Accepted" : "Rejected"} ${plural(n, "snapshot")}; ${failed} failed: ${r.errors![0].error}`, { intent: "error" });
    else if (n === 0) toast("No pending snapshots", { intent: "info" });
    else if (accept) {
      const rerun = req.rerun === false ? "" : ` · re-running ${plural(r.tests.filter((t) => t.files.length).length, "test")}`;
      toast(`Accepted ${plural(n, "snapshot")}${rerun}`, { intent: "success" });
    } else toast(`Rejected ${plural(n, "snapshot")}`, { intent: "success" });
    return r;
  } catch (e) {
    toast(`Could not ${accept ? "accept" : "reject"} the snapshot: ${(e as Error).message}`, { intent: "error" });
    return undefined;
  }
}
