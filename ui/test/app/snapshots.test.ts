import { afterEach, describe, expect, it, vi } from "vitest";
import type { TestNode } from "../../src/contract";
import { acceptAllSnapshots, acceptSnapshots, fetchSnapshotText, fetchSnapshots, pendingSnapshotTests, rejectSnapshots, snapshotFileUrl } from "../../src/snapshots";
import { buildTree, compileQuery, matchingTests } from "../../src/tree";
import { mockResolveSnapshots, mockSnapshotFile, mockSnapshots, snapshotSpecs } from "../../src/mock/snapshots";
import { flushFrame, project, testNode, text } from "../helpers/app";
import { jsonResponse, resetStore } from "../helpers/app3";
import { flushMicrotasks } from "../helpers/dom";

const MSG = "VerifyException: Directory: /src/P1/Snaps\nNotEqual:\n  - Received: ClassA.Test1.received.txt\n    Verified: ClassA.Test1.verified.txt\n";
const failedSnap = (p: Partial<TestNode> = {}) => testNode({ outcome: "failed", errorMessage: MSG, hasSnapshotMismatch: true, ...p });
const toasts = () => [...document.querySelectorAll(".lc-toast")].map((t) => text(t));

afterEach(() => {
  document.querySelectorAll(".lc-toaster").forEach((t) => t.remove());
});

describe("snapshot API (host)", () => {
  it("fetches a test's snapshots and file contents", async () => {
    await resetStore({ tests: [failedSnap()] });
    const fetch = vi.fn(async (u: unknown) => {
      const url = String(u);
      if (url.includes("/api/tests/snapshots")) return jsonResponse({ projectId: "P1", id: "x", snapshots: [] });
      if (url.includes("missing")) return jsonResponse("", { status: 404 });
      if (url.includes("broken")) return jsonResponse("", { status: 500 });
      return jsonResponse("text content");
    });
    vi.stubGlobal("fetch", fetch);
    const s = await fetchSnapshots({ projectId: "P1", id: "Ns.ClassA.Test1" });
    expect(s.snapshots).toEqual([]);
    expect(String(fetch.mock.calls[0][0])).toContain("/api/tests/snapshots?projectId=P1&id=Ns.ClassA.Test1");
    expect(await fetchSnapshotText("/a/b.verified.txt")).toBe("text content");
    expect(String(fetch.mock.calls[1][0])).toContain(`/api/snapshots/file?path=${encodeURIComponent("/a/b.verified.txt")}`);
    expect(await fetchSnapshotText("/missing.verified.txt")).toBeUndefined();
    await expect(fetchSnapshotText("/broken.verified.txt")).rejects.toThrow("HTTP 500");
    expect(await snapshotFileUrl("/a/c.received.png")).toContain("/api/snapshots/file?path=%2Fa%2Fc.received.png");
    fetch.mockImplementationOnce(async () => jsonResponse({}, { status: 404 }));
    await expect(fetchSnapshots({ projectId: "P1", id: "nope" })).rejects.toThrow("HTTP 404");
  });

  it("accept / reject post JSON and report the outcome as a toast", async () => {
    await resetStore({ tests: [failedSnap()] });
    const bodies: unknown[] = [];
    const fetch = vi.fn(async (u: unknown, init?: RequestInit) => {
      bodies.push({ url: String(u), method: init?.method, type: (init?.headers as Record<string, string>)?.["Content-Type"], body: JSON.parse(String(init?.body)) });
      return jsonResponse({ tests: [{ projectId: "P1", id: "Ns.ClassA.Test1", files: ["ClassA.Test1.verified.txt"] }], runId: "r9" });
    });
    vi.stubGlobal("fetch", fetch);
    const r = await acceptSnapshots({ projectId: "P1", id: "Ns.ClassA.Test1", names: ["ClassA.Test1.verified.txt"] });
    expect(r?.runId).toBe("r9");
    expect(bodies[0]).toEqual({
      url: expect.stringContaining("/api/snapshots/accept"),
      method: "POST",
      type: "application/json",
      body: { projectId: "P1", id: "Ns.ClassA.Test1", names: ["ClassA.Test1.verified.txt"] },
    });
    expect(toasts().at(-1)).toContain("Accepted 1 snapshot · re-running 1 test");

    await rejectSnapshots({ projectId: "P1", id: "Ns.ClassA.Test1" });
    expect((bodies[1] as { url: string }).url).toContain("/api/snapshots/reject");
    expect(toasts().at(-1)).toContain("Rejected 1 snapshot");

    await acceptSnapshots({ id: "x", rerun: false });
    expect(toasts().at(-1)).toMatch(/Accepted 1 snapshot$/);

    fetch.mockImplementationOnce(async () => jsonResponse({ tests: [] }));
    await acceptAllSnapshots();
    expect(JSON.parse(String((fetch.mock.calls.at(-1)![1] as RequestInit).body))).toEqual({});
    expect(toasts().at(-1)).toContain("No pending snapshots");

    fetch.mockImplementationOnce(async () =>
      jsonResponse({ tests: [{ projectId: "P1", id: "a", files: [] }], errors: [{ projectId: "P1", id: "a", name: "x", error: "The received file is a link." }] }),
    );
    await acceptSnapshots({ id: "a" });
    expect(toasts().at(-1)).toContain("1 failed: The received file is a link.");

    fetch.mockImplementationOnce(async () => jsonResponse({ error: "The test is running." }, { status: 409 }));
    expect(await rejectSnapshots({ id: "a" })).toBeUndefined();
    expect(toasts().at(-1)).toContain("Could not reject the snapshot: The test is running.");
    fetch.mockImplementationOnce(async () => ({ ok: false, status: 500, json: async () => { throw new Error("no json"); } }) as unknown as Response);
    await acceptSnapshots({ id: "a" });
    expect(toasts().at(-1)).toContain("HTTP 500");
  });

  it("pendingSnapshotTests lists the tests with hasSnapshotMismatch", async () => {
    const { store } = await resetStore({ tests: [failedSnap(), testNode({ id: "Ns.ClassA.Other", outcome: "passed" })] });
    expect(pendingSnapshotTests().map((t) => t.id)).toEqual(["Ns.ClassA.Test1"]);
    store.apply({ type: "testsUpdated", tests: [failedSnap({ hasSnapshotMismatch: undefined })] });
    await flushFrame();
    expect(pendingSnapshotTests()).toEqual([]);
  });

  it("is:snapshot filters the tree", () => {
    const tests = new Map<string, TestNode>();
    for (const t of [failedSnap(), testNode({ id: "Ns.ClassA.Plain", outcome: "failed" })]) tests.set(`${t.projectId}|${t.id}`, t);
    const roots = buildTree([project()], tests);
    expect(matchingTests(roots, "is:snapshot", "all").map((t) => t.id)).toEqual(["Ns.ClassA.Test1"]);
    expect(matchingTests(roots, "is:snap", "all").map((t) => t.id)).toEqual(["Ns.ClassA.Test1"]);
    expect(compileQuery("is:snapshot")).toBeTypeOf("function");
  });
});

describe("snapshot mock (?mock&snapshots)", () => {
  type Spec = { node: TestNode; behavior: "pass" | "fail" | "skip" | "timeout" | "flaky"; error?: { type: string; message: string; stack: string } };
  const factory = (projectId: string, ns: string, cls: string, method: string, o: { file: string; line: number; behavior?: "pass" | "fail"; error?: Spec["error"] }): Spec => ({
    node: { id: `${ns}.${cls}.${method}`, projectId, displayName: method, namespace: ns, className: cls, methodName: method, filePath: o.file, line: o.line, traits: [], outcome: "notRun" },
    behavior: o.behavior ?? "pass",
    error: o.error,
  });

  it("serves snapshots, accepts (the next run passes) and rejects", async () => {
    const { store } = await resetStore({}, { kind: "mock" });
    const specs = snapshotSpecs("/r", factory);
    expect(specs.map((s) => s.node.methodName)).toEqual(["Receipt_MatchesSnapshot", "Order_MatchesSnapshot", "Invoice_NewSnapshot", "Chart_MatchesSnapshot"]);
    const [, order, invoice, chart] = specs;
    expect(order.error!.message).toContain("NotEqual:");
    expect(order.node.hasSnapshotMismatch).toBeUndefined(); // not failed yet
    for (const s of specs.slice(1)) s.node.outcome = "failed";
    expect(order.node.hasSnapshotMismatch).toBe(true);
    expect(JSON.parse(JSON.stringify(order.node)).hasSnapshotMismatch).toBe(true);

    const os = mockSnapshots("SampleTests", order.node.id);
    expect(os.directory).toBe("/r/SampleTests/Snapshots");
    expect(os.snapshots).toEqual([expect.objectContaining({ name: "ReceiptSnapshotTests.Order_MatchesSnapshot.verified.txt", kind: "text", isNew: false })]);
    expect(mockSnapshotFile(os.snapshots[0].verifiedPath)).toContain("Status: sent");
    expect(mockSnapshotFile(os.snapshots[0].receivedPath)).toContain("Status: queued");
    expect(mockSnapshots("SampleTests", invoice.node.id).snapshots[0].isNew).toBe(true);
    expect(mockSnapshotFile(mockSnapshots("SampleTests", chart.node.id).snapshots[0].receivedPath)).toMatch(/^data:image\/svg\+xml,/);
    expect(mockSnapshotFile("/nope")).toBeUndefined();
    expect(mockSnapshots("SampleTests", "unknown").snapshots).toEqual([]);

    const emitted: TestNode[][] = [];
    const r = mockResolveSnapshots({ projectId: "SampleTests", id: order.node.id }, true, (t) => emitted.push(t));
    expect(r.tests[0].files).toEqual(["ReceiptSnapshotTests.Order_MatchesSnapshot.verified.txt"]);
    expect(order.behavior).toBe("pass");
    expect(order.node.hasSnapshotMismatch).toBeUndefined();
    expect(emitted[0][0].id).toBe(order.node.id);

    mockResolveSnapshots({ id: chart.node.id }, false, () => {});
    expect(chart.behavior).toBe("fail");
    expect(chart.node.hasSnapshotMismatch).toBeUndefined();
    // a failing run writes the received file again
    store.apply({ type: "testsUpdated", tests: [{ ...chart.node, outcome: "running" }] });
    expect(chart.node.hasSnapshotMismatch).toBe(true);

    // accept all: the remaining tests with a mismatch
    const all = mockResolveSnapshots({}, true, () => {});
    expect(all.tests.map((t) => t.id).sort()).toEqual([chart.node.id, invoice.node.id].sort());
  });

  it("the app API uses the mock in mock mode and re-runs accepted tests", async () => {
    const { transport } = await resetStore({}, { kind: "mock" });
    const specs = snapshotSpecs("/m", factory);
    for (const s of specs) s.node.outcome = s.behavior === "fail" ? "failed" : "passed";
    const emit = vi.fn();
    (window as unknown as { __labcoatMock?: unknown }).__labcoatMock = { emit };
    const s = await fetchSnapshots({ projectId: "SampleTests", id: specs[1].node.id });
    expect(s.snapshots).toHaveLength(1);
    expect(await fetchSnapshotText(s.snapshots[0].receivedPath)).toContain("queued");
    expect(await snapshotFileUrl("/none")).toBeUndefined();
    await acceptSnapshots({ projectId: "SampleTests", id: specs[1].node.id });
    await flushMicrotasks();
    expect(emit).toHaveBeenCalledWith(expect.objectContaining({ type: "testsUpdated" }));
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "SampleTests", testIds: [specs[1].node.id] });
    await rejectSnapshots({ projectId: "SampleTests", id: specs[2].node.id });
    expect(transport.sent).toHaveLength(1);
    delete (window as unknown as { __labcoatMock?: unknown }).__labcoatMock;
  });
});
