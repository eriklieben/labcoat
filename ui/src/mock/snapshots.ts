// SNAPSHOT mock (`?mock&snapshots`): Verify snapshot tests for the snapshot panel (contract Round 6) without a backend.
//   - four tests in SampleTests.Snapshots.ReceiptSnapshotTests: one passing, a changed text snapshot, a new one and a
//     changed image (two SVG bar charts as data: URLs)
//   - hasSnapshotMismatch is a live getter on the node (failed + received file present); a run writes the received
//     files again (like Verify), accept makes the test pass on its next run, reject deletes the received file
//   - answers GET /api/tests/snapshots, /api/snapshots/file and POST /api/snapshots/accept|reject (see ../snapshots.ts)
import type { SnapshotActionResult, SnapshotFile, TestNode, TestResultsEvent, TestSnapshots } from "../contract";
import { serverEventListeners } from "../server-events";

interface SpecLike {
  node: TestNode;
  behavior: "pass" | "fail" | "skip" | "timeout" | "flaky";
  error?: { type: string; message: string; stack: string };
}
type Factory<S> = (
  projectId: string,
  ns: string,
  cls: string,
  method: string,
  opts: { file: string; line: number; behavior?: "pass" | "fail"; ms?: [number, number]; error?: SpecLike["error"] },
) => S;

interface MockSnap {
  file: SnapshotFile;
  received: string;
  verified?: string;
  /** the received file exists (cleared by accept / reject, written again by a failing run) */
  present: boolean;
}
interface Entry {
  spec: SpecLike;
  snaps: MockSnap[];
}

const entries = new Map<string, Entry>();
const files = new Map<string, () => string | undefined>();
const key = (t: { projectId: string; id: string }) => `${t.projectId}|${t.id}`;

const chart = (bars: number[], label: string) => {
  const w = 240;
  const rects = bars.map((v, i) => `<rect x="${20 + i * 50}" y="${150 - v}" width="34" height="${v}" rx="3" fill="#6b5bd2"/>`).join("");
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="180" viewBox="0 0 ${w} 180"><rect width="${w}" height="180" fill="#ffffff"/>${rects}<text x="20" y="172" font-family="sans-serif" font-size="12" fill="#333">${label}</text></svg>`;
  return `data:image/svg+xml,${encodeURIComponent(svg)}`;
};

const ORDER_VERIFIED = `{
  Id: 42,
  Status: sent,
  Items: [
    {
      Item: Coffee,
      Qty: 2
    },
    {
      Item: Croissant,
      Qty: 1
    }
  ],
  Total: 9.45
}`;

function verifyMessage(dir: string, cls: string, method: string, isNew: boolean, ext = "txt") {
  const n = `${cls}.${method}`;
  return (
    `Test method SampleTests.Snapshots.${n} threw exception:\nVerifyException: Directory: ${dir}\n${isNew ? "New" : "NotEqual"}:\n` +
    `  - Received: ${n}.received.${ext}\n    Verified: ${n}.verified.${ext}\n\nFileContent:\n`
  );
}

/** The snapshot tests for the mock catalog (buildCatalog in mockServer.ts, only with `?snapshots`). */
export function snapshotSpecs<S extends SpecLike>(root: string, t: Factory<S>): S[] {
  const dir = `${root}/SampleTests/Snapshots`;
  const file = `${dir}/ReceiptSnapshotTests.cs`;
  const cls = "ReceiptSnapshotTests";
  const mk = (method: string, line: number, fail: boolean, isNew = false, ext = "txt") =>
    t("SampleTests", "SampleTests.Snapshots", cls, method, {
      file,
      line,
      ms: [8, 40],
      behavior: fail ? "fail" : "pass",
      error: fail
        ? { type: "VerifyTests.VerifyException", message: verifyMessage(dir, cls, method, isNew, ext), stack: `   at SampleTests.Snapshots.${cls}.${method}() in ${file}:line ${line + 1}` }
        : undefined,
    });
  const specs = [mk("Receipt_MatchesSnapshot", 24, false), mk("Order_MatchesSnapshot", 29, true), mk("Invoice_NewSnapshot", 40, true, true), mk("Chart_MatchesSnapshot", 44, true, false, "png")];
  const snap = (s: S, ext: string, kind: SnapshotFile["kind"], received: string, verified?: string): MockSnap => {
    const n = `${cls}.${s.node.methodName}`;
    return {
      file: { name: `${n}.verified.${ext}`, receivedPath: `${dir}/${n}.received.${ext}`, verifiedPath: `${dir}/${n}.verified.${ext}`, kind, isNew: verified === undefined },
      received,
      verified,
      present: true,
    };
  };
  const add = (s: S, ...snaps: MockSnap[]) => {
    const e: Entry = { spec: s, snaps };
    entries.set(key(s.node), e);
    for (const m of snaps) {
      files.set(m.file.receivedPath, () => (m.present ? m.received : undefined));
      files.set(m.file.verifiedPath, () => m.verified);
    }
    Object.defineProperty(s.node, "hasSnapshotMismatch", {
      enumerable: true,
      configurable: true,
      get: () => (s.node.outcome === "failed" && e.snaps.some((x) => x.present) ? true : undefined),
    });
  };
  add(specs[1], snap(specs[1], "txt", "text", ORDER_VERIFIED.replace("Status: sent", "Status: queued"), ORDER_VERIFIED));
  add(specs[2], snap(specs[2], "txt", "text", "{\n  Number: INV-2026-0042,\n  Customer: Contoso Coffee,\n  Amount: 9.45,\n  Paid: false\n}"));
  add(specs[3], snap(specs[3], "png", "image", chart([60, 95, 120, 70], "Revenue 2026"), chart([60, 90, 110, 70], "Revenue 2026")));
  return specs;
}

// A failing run writes the received files again (like Verify does).
serverEventListeners.add((e) => {
  const running: { projectId: string; id: string }[] = [];
  if (e.type === "testsUpdated") for (const t of (e.tests as TestNode[] | undefined) ?? []) if (t.outcome === "running") running.push(t);
  if (e.type === "testResults")
    for (const g of (e as unknown as TestResultsEvent).projects) for (const p of g.results) if (p.outcome === "running") running.push({ projectId: g.projectId, id: p.id });
  for (const t of running) {
    const en = entries.get(key(t));
    if (en && en.spec.behavior === "fail") for (const s of en.snaps) s.present = true;
  }
});

export function mockSnapshots(projectId: string, id: string): TestSnapshots {
  const en = entries.get(`${projectId}|${id}`);
  const pending = en && en.spec.node.outcome === "failed" ? en.snaps.filter((s) => s.present).map((s) => ({ ...s.file, isNew: s.verified === undefined })) : [];
  return { projectId, id, directory: en?.snaps[0]?.file.receivedPath.replace(/\/[^/]+$/, ""), snapshots: pending };
}

/** Text content, or the data: URL of an image; undefined = 404. */
export function mockSnapshotFile(path: string): string | undefined {
  return files.get(path)?.();
}

/** Accept / reject like the host; `emit` publishes the changed nodes (testsUpdated). */
export function mockResolveSnapshots(
  req: { projectId?: string; id?: string; names?: string[] },
  accept: boolean,
  emit: (tests: TestNode[]) => void,
): SnapshotActionResult {
  const targets = [...entries.values()].filter((e) =>
    req.id ? e.spec.node.id === req.id && (!req.projectId || e.spec.node.projectId === req.projectId) : !!e.spec.node.hasSnapshotMismatch && (!req.projectId || e.spec.node.projectId === req.projectId),
  );
  const tests: SnapshotActionResult["tests"] = [];
  for (const e of targets) {
    const done: string[] = [];
    for (const s of e.snaps) {
      if (!s.present || (req.names?.length && !req.names.includes(s.file.name))) continue;
      if (accept) s.verified = s.received;
      s.present = false;
      done.push(s.file.name);
    }
    // accepted everything -> the next run passes
    if (accept && e.snaps.every((s) => s.verified === s.received)) {
      e.spec.behavior = "pass";
      e.spec.error = undefined;
    }
    tests.push({ projectId: e.spec.node.projectId, id: e.spec.node.id, files: done });
  }
  emit(targets.map((e) => ({ ...e.spec.node })));
  return { tests };
}
