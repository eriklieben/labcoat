import { describe, expect, it, vi } from "vitest";
import type { ServerEvent, TestNode, TestOutputEvent } from "../../src/contract";
import { appendText, HEAD_CHARS, initLiveOutput, getLiveOutput, KEEP_FINISHED, LiveOutputStore, MAX_CHARS, residualLines, type LiveBuffer } from "../../src/live-output";
import { serverEventListeners } from "../../src/server-events";
import { flushFrame, makeStore, testNode } from "../helpers/app";

const out = (id: string, text: string, seq: number, p: Partial<TestOutputEvent> = {}): TestOutputEvent => ({
  type: "testOutput",
  projectId: "P1",
  id,
  stream: "stdout",
  text,
  seq,
  ...p,
});

function setup(initial: TestNode[] = []) {
  const tests = new Map<string, TestNode>(initial.map((t) => [`${t.projectId}|${t.id}`, t]));
  const logs: { projectId: string; level: string; message: string }[] = [];
  const s = new LiveOutputStore((k) => tests.get(k), (e) => logs.push(e), (cb) => cb());
  const set = (...ts: TestNode[]) => {
    for (const t of ts) tests.set(`${t.projectId}|${t.id}`, t);
    s.apply({ type: "testsUpdated", tests: ts });
  };
  return { s, tests, set, logs };
}

const emptyBuffer = (): LiveBuffer => ({ key: "k", lines: [], seq: 0, chars: 0, headLines: 0, omitted: 0, rev: 0, finished: false });

describe("LiveOutputStore", () => {
  it("buffers output of running tests only, line by line", () => {
    const { s, set } = setup([testNode({ id: "A" }), testNode({ id: "B" })]);
    s.apply(out("A", "ignored\n", 1)); // not running yet
    expect(s.get("P1|A")).toBeUndefined();
    set(testNode({ id: "A", outcome: "running" }));
    s.apply(out("A", "one\ntwo\n", 1));
    s.apply(out("A", "th", 2));
    s.apply(out("A", "ree\n", 3));
    s.apply(out("A", "warn\n", 4, { stream: "host", err: true }));
    const b = s.get("P1|A")!;
    expect(b.lines.map((l) => l.text)).toEqual(["one", "two", "three", "warn"]);
    expect(b.lines[3]).toMatchObject({ stream: "host", err: true });
    expect(b.seq).toBe(4);
  });

  it("drops duplicates, flags gaps and restarts on seq 1", () => {
    const { s, set } = setup();
    set(testNode({ id: "A", outcome: "running" }));
    s.apply(out("A", "a\n", 1));
    s.apply(out("A", "a\n", 1 + 0)); // seq 1 again = a new execution
    expect(s.get("P1|A")!.lines).toHaveLength(1);
    s.apply(out("A", "b\n", 2));
    s.apply(out("A", "b\n", 2)); // duplicate
    expect(s.get("P1|A")!.lines.map((l) => l.text)).toEqual(["a", "b"]);
    s.apply(out("A", "e\n", 5));
    expect(s.get("P1|A")!.gap).toBe(true);
  });

  it("keeps a finished test's buffer (live-only lines) and resets it when the test runs again", () => {
    const { s, set } = setup();
    set(testNode({ id: "A", outcome: "running" }));
    s.apply(out("A", "x\n", 1));
    set(testNode({ id: "A", outcome: "passed" }));
    expect(s.get("P1|A")!.finished).toBe(true);
    set(testNode({ id: "A", outcome: "queued" }));
    expect(s.get("P1|A")!.lines).toHaveLength(0);
    expect(s.get("P1|A")!.finished).toBe(false);
  });

  it("follows result patches (testResults): the patched outcome, else the test as it is", () => {
    const { s, tests } = setup([testNode({ id: "A" })]);
    const patch = (p: { id: string; outcome?: TestNode["outcome"] }) => s.apply({ type: "testResults", projects: [{ projectId: "P1", results: [p] }] });
    tests.set("P1|A", testNode({ id: "A", outcome: "running" })); // the store applied the patch first
    patch({ id: "A", outcome: "running" });
    s.apply(out("A", "x\n", 1));
    expect(s.get("P1|A")!.lines).toHaveLength(1);
    tests.set("P1|A", testNode({ id: "A", outcome: "passed" }));
    patch({ id: "A" }); // e.g. only the history changed: the store's test decides
    expect(s.get("P1|A")!.finished).toBe(true);
    patch({ id: "A", outcome: "queued" });
    expect(s.get("P1|A")!.finished).toBe(false);
  });

  it(`is memory safe: keeps only the last ${KEEP_FINISHED} finished buffers (plus pinned ones)`, () => {
    const { s, set } = setup();
    const ids = Array.from({ length: 200 }, (_, i) => `T${i}`);
    const unpin = s.pin("P1|T0");
    for (const id of ids) {
      set(testNode({ id, outcome: "running" }));
      s.apply(out(id, `line of ${id}\n`, 1));
      set(testNode({ id, outcome: "passed" }));
    }
    expect(s.size).toBe(KEEP_FINISHED + 1); // the pinned one survives
    expect(s.get("P1|T0")).toBeDefined();
    unpin();
    expect(s.size).toBe(KEEP_FINISHED);
    // finished tests without lines are not kept at all
    set(testNode({ id: "Quiet", outcome: "running" }), testNode({ id: "Quiet", outcome: "passed" }));
    expect(s.get("P1|Quiet")).toBeUndefined();
  });

  it("finishes buffers on runFinished and on a state snapshot", () => {
    const { s, set, tests } = setup();
    set(testNode({ id: "A", outcome: "running" }), testNode({ id: "B", outcome: "running" }));
    s.apply(out("A", "a\n", 1));
    s.apply(out("B", "b\n", 1));
    tests.set("P1|A", testNode({ id: "A", outcome: "cancelled" }));
    s.apply({ type: "state", state: { projects: [], tests: [], watch: false, coverageEnabled: false } } as ServerEvent);
    expect(s.get("P1|A")!.finished).toBe(true);
    expect(s.get("P1|B")!.finished).toBe(false);
    tests.set("P1|B", testNode({ id: "B", outcome: "passed" }));
    s.apply({ type: "runFinished", run: { runId: "r", startedAt: "", total: 2, passed: 1, failed: 0, skipped: 0 } });
    expect(s.get("P1|B")!.finished).toBe(true);
  });

  it("mirrors lines into the Output panel when asked", () => {
    const { s, set, logs } = setup();
    set(testNode({ id: "A", displayName: "Slow", outcome: "running" }));
    s.apply(out("A", "quiet\n", 1));
    expect(logs).toHaveLength(0);
    s.setMirror(true);
    s.apply(out("A", "one\ntwo\n", 2));
    s.apply(out("A", "bad\n", 3, { stream: "stderr" }));
    expect(logs).toEqual([
      { projectId: "P1", level: "info", message: "Slow │ one" },
      { projectId: "P1", level: "info", message: "Slow │ two" },
      { projectId: "P1", level: "warning", message: "Slow │ bad" },
    ]);
    s.setMirror(false);
  });

  it("follow: selects a running test, stays on the shown one until it finishes", () => {
    const { s, set } = setup();
    s.setFollow(true);
    set(testNode({ id: "A", outcome: "running" }));
    expect(s.followTarget()).toBe("t:P1|A");
    const unpin = s.pin("P1|A"); // the detail pane now shows A
    set(testNode({ id: "B", outcome: "running" }));
    expect(s.followTarget()).toBe("t:P1|A"); // A still runs: no jump
    set(testNode({ id: "A", outcome: "passed" }));
    expect(s.followTarget()).toBe("t:P1|B");
    unpin();
    s.setFollow(false);
    expect(s.followTarget()).toBeUndefined();
  });

  it("seeds a buffer from the live-output snapshot unless it already has newer output", () => {
    const { s, set } = setup();
    set(testNode({ id: "A", outcome: "running" }));
    s.seed("P1|A", { seq: 3, chunks: [{ stream: "stdout", text: "a\nb\n" }, { stream: "host", text: "c\n", shared: true }] });
    expect(s.get("P1|A")!.lines.map((l) => l.text)).toEqual(["a", "b", "c"]);
    s.apply(out("A", "d\n", 4));
    s.seed("P1|A", { seq: 2, chunks: [{ stream: "stdout", text: "old\n" }] });
    expect(s.get("P1|A")!.lines.map((l) => l.text)).toEqual(["a", "b", "c", "d"]);
  });

  it("bumps the tick once per frame", async () => {
    const tests = new Map([["P1|A", testNode({ id: "A", outcome: "running" })]]);
    const frames: (() => void)[] = [];
    const s = new LiveOutputStore((k) => tests.get(k), undefined, (cb) => frames.push(cb));
    s.apply(out("A", "1\n", 1));
    s.apply(out("A", "2\n", 2));
    expect(frames).toHaveLength(1);
    expect(s.tick()).toBe(0);
    frames[0]();
    expect(s.tick()).toBe(1);
  });
});

describe("appendText (64 K cap)", () => {
  it("keeps the first 16 K and the last characters up to 64 K, counting what was dropped", () => {
    const b = emptyBuffer();
    const line = "x".repeat(99); // 100 chars per line with the newline
    for (let i = 0; i < 2000; i++) appendText(b, { stream: "stdout", text: `${line}\n` });
    expect(b.chars).toBeLessThanOrEqual(MAX_CHARS);
    expect(b.headLines).toBe(Math.floor(HEAD_CHARS / 100));
    expect(b.omitted).toBe(2000 * 100 - b.chars);
    expect(b.rev).toBeGreaterThan(0);
  });

  it("continues an open line only for the same kind of output", () => {
    const b = emptyBuffer();
    appendText(b, { stream: "stdout", text: "ab" });
    appendText(b, { stream: "host", text: "host\n" });
    appendText(b, { stream: "stdout", text: "cd\n" });
    expect(b.lines.map((l) => l.text)).toEqual(["ab", "host", "cd"]);
    const c = emptyBuffer();
    appendText(c, { stream: "stdout", text: "ab" });
    appendText(c, { stream: "stdout", text: "cd\nef\n" });
    expect(c.lines.map((l) => l.text)).toEqual(["abcd", "ef"]);
    expect(c.lines.every((l) => !l.open)).toBe(true);
  });
});

describe("residualLines (final output stays authoritative)", () => {
  const buf = (lines: { text: string; stream?: "stdout" | "host"; shared?: boolean }[]): LiveBuffer => ({
    ...emptyBuffer(),
    lines: lines.map((l) => ({ stream: "host", ...l })),
  });

  it("drops lines that are in the final output and shared lines", () => {
    const b = buf([{ text: "captured", stream: "stdout" }, { text: "display 1" }, { text: "other test", shared: true }, { text: "  " }]);
    expect(residualLines(b, { stdOut: "captured\r\n", hasOutput: true }).map((l) => l.text)).toEqual(["display 1"]);
  });

  it("waits for lean details and gives up on truncated output", () => {
    const b = buf([{ text: "x" }]);
    expect(residualLines(b, { hasOutput: true })).toEqual([]);
    expect(residualLines(b, { stdOut: "y", outputTruncated: true })).toEqual([]);
    expect(residualLines(b, {}).map((l) => l.text)).toEqual(["x"]);
    expect(residualLines(undefined, {})).toEqual([]);
  });
});

describe("initLiveOutput", () => {
  it("subscribes to server events through the store and logs mirrored lines into it", async () => {
    const { store } = makeStore();
    const s = initLiveOutput(store);
    expect(getLiveOutput()).toBe(s);
    store.apply({ type: "testsUpdated", tests: [testNode({ id: "A", outcome: "running" })] });
    store.apply(out("A", "hello\n", 1));
    expect(s.get("P1|A")!.lines[0].text).toBe("hello");
    s.setMirror(true);
    store.apply(out("A", "mirrored\n", 2));
    await flushFrame();
    expect(store.get().logs.at(-1)?.message).toContain("mirrored");
    s.setMirror(false);
    const before = serverEventListeners.size;
    initLiveOutput(store); // replaces the previous subscription
    expect(serverEventListeners.size).toBe(before);
    vi.restoreAllMocks();
  });
});
