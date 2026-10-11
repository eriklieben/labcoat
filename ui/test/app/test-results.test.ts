import { describe, expect, it } from "vitest";
import type { HistoryEntry, ServerEvent, TestNode, TestResultsEvent } from "../../src/contract";
import { appendHistory, applyResultPatch, MAX_HISTORY, normalizeTest } from "../../src/test-results";
import { diffResult, encodeUpdates, isAppend, resetSent, sameShape, withFileTable } from "../../src/mock/results";
import { explorerState, testNode } from "../helpers/app";

const h = (n: number, run = "r"): HistoryEntry => ({ outcome: "passed", durationMs: n, at: `t${n}`, runId: `${run}${n}` });

describe("normalizeTest (lean snapshot wire fields)", () => {
  it("resolves fileIndex against the file table and defaults omitted traits", () => {
    const t = { id: "a", projectId: "P", displayName: "a", outcome: "notRun", fileIndex: 1 } as unknown as TestNode;
    const files = ["/src/A.cs", "/src/B.cs"];
    expect(normalizeTest(t, files)).toBe(t);
    expect(t.filePath).toBe("/src/B.cs");
    expect(t.traits).toEqual([]);
    // without a table / an index nothing changes; existing traits stay
    const u = testNode({ filePath: "/x.cs", traits: [{ key: "k", value: "v" }] });
    normalizeTest(u);
    expect(u.filePath).toBe("/x.cs");
    expect(u.traits).toHaveLength(1);
  });
});

describe("appendHistory", () => {
  it("appends, keeps the last MAX_HISTORY entries and ignores a replayed entry", () => {
    expect(appendHistory(undefined, h(1))).toEqual([h(1)]);
    const full = Array.from({ length: MAX_HISTORY }, (_, i) => h(i));
    const next = appendHistory(full, h(99));
    expect(next).toHaveLength(MAX_HISTORY);
    expect(next[0]).toEqual(h(1));
    expect(next[MAX_HISTORY - 1]).toEqual(h(99));
    const same = [h(1), h(2)];
    expect(appendHistory(same, h(2))).toBe(same);
  });
});

describe("applyResultPatch", () => {
  const base = (): TestNode =>
    testNode({ id: "a", outcome: "failed", durationMs: 3, errorMessage: "boom", stackTrace: "at", lastRunAt: "t0", stale: true, history: [h(0)] });

  it("sets present fields, clears the listed ones and keeps everything else", () => {
    const prev = base();
    const n = applyResultPatch(prev, { id: "a", outcome: "passed", durationMs: 4, lastRunAt: "t1", clear: ["errorMessage", "stackTrace", "stale"] });
    expect(n).not.toBe(prev);
    expect(n).toMatchObject({ outcome: "passed", durationMs: 4, lastRunAt: "t1", displayName: "a", history: [h(0)] });
    expect(n.errorMessage).toBeUndefined();
    expect(n.stale).toBeUndefined();
    expect(prev.errorMessage).toBe("boom"); // not mutated
    expect("id" in n && "clear" in n).toBe(false);
  });

  it("appends history from historyAdd or, short form, from the patched result and the event's run id", () => {
    const prev = base();
    expect(applyResultPatch(prev, { id: "a", historyAdd: h(7) }).history).toEqual([h(0), h(7)]);
    const rec = applyResultPatch(prev, { id: "a", outcome: "passed", durationMs: 9, lastRunAt: "t9", recorded: true }, "run9");
    expect(rec.history).toEqual([h(0), { outcome: "passed", durationMs: 9, at: "t9", runId: "run9" }]);
    // no duration, no run id: left out of the entry
    const bare = applyResultPatch(testNode({ outcome: "skipped" }), { id: "a", lastRunAt: "t", recorded: true });
    expect(bare.history).toEqual([{ outcome: "skipped", at: "t" }]);
    // a replaced history
    expect(applyResultPatch(prev, { id: "a", history: [h(5)] }).history).toEqual([h(5)]);
    expect(applyResultPatch(prev, { id: "a", history: [h(5)], historyAdd: h(6) }).history).toEqual([h(5), h(6)]);
  });
});

describe("mock host encoding (src/mock/results.ts)", () => {
  it("sends unknown and restructured tests in full, the rest as patches that rebuild the same tests", () => {
    const a = testNode({ id: "a", filePath: "/a.cs", line: 3 });
    const b = testNode({ id: "b", filePath: "/a.cs", line: 9, projectId: "P2" });
    const sent = new Map<string, TestNode>();
    resetSent(explorerState({ tests: [a, b] }), sent);
    const client = new Map([a, b].map((t) => [`${t.projectId}|${t.id}`, { ...t }]));
    const apply = (events: ServerEvent[]) => {
      for (const e of events) {
        if (e.type === "testsUpdated") for (const t of e.tests) client.set(`${t.projectId}|${t.id}`, t);
        if (e.type === "testResults")
          for (const g of e.projects) for (const p of g.results) client.set(`${g.projectId}|${p.id}`, applyResultPatch(client.get(`${g.projectId}|${p.id}`)!, p, e.runId));
      }
    };
    const strip = (t: TestNode) => JSON.parse(JSON.stringify(t));

    // a result: patches only
    const a1 = { ...a, outcome: "failed" as const, errorMessage: "x", durationMs: 2, lastRunAt: "t1" };
    const b1 = { ...b, outcome: "running" as const };
    let ev = encodeUpdates([a1, b1], sent);
    expect(ev.map((e) => e.type)).toEqual(["testResults"]);
    expect((ev[0] as TestResultsEvent).projects.map((g) => g.projectId)).toEqual(["P1", "P2"]);
    apply(ev);
    // history recorded (short form), an error cleared, a rename (full entry)
    const a2 = { ...a1, outcome: "passed" as const, errorMessage: undefined, durationMs: 1, lastRunAt: "t2", history: [{ outcome: "passed" as const, durationMs: 1, at: "t2", runId: "r2" }] };
    const b2 = { ...b1, displayName: "b renamed" };
    ev = encodeUpdates([a2, b2], sent);
    expect(ev.map((e) => e.type)).toEqual(["testsUpdated", "testResults"]);
    const patch = (ev[1] as TestResultsEvent).projects[0].results[0];
    expect(patch).toMatchObject({ outcome: "passed", recorded: true, clear: ["errorMessage"] });
    expect((ev[1] as TestResultsEvent).runId).toBe("r2");
    apply(ev);
    expect(strip(client.get("P1|a")!)).toEqual(strip(a2));
    expect(strip(client.get("P2|b")!)).toEqual(strip(b2));
    // nothing changed: nothing sent
    expect(encodeUpdates([{ ...a2 }], sent)).toEqual([]);
  });

  it("diffResult: history replaced, cleared, appended at the cap; isAppend; sameShape", () => {
    const full = Array.from({ length: MAX_HISTORY }, (_, i) => h(i));
    const t = testNode({ history: full });
    const run: { id?: string } = { id: "other" };
    expect(diffResult(t, { ...t, history: [...full.slice(1), h(50)] }, run)?.historyAdd).toEqual(h(50));
    expect(diffResult(t, { ...t, history: [h(3)] }, run)?.history).toEqual([h(3)]);
    expect(diffResult(t, { ...t, history: undefined }, run)?.clear).toEqual(["history"]);
    expect(diffResult(t, { ...t, history: [...full] }, run)).toBeUndefined();
    expect(isAppend(undefined, [h(1)])).toBe(true);
    expect(isAppend([h(1)], [h(2), h(3)])).toBe(false);
    expect(sameShape(t, { ...t, traits: [{ key: "a", value: "b" }] })).toBe(false);
    expect(sameShape(t, { ...t, outcome: "passed" })).toBe(true);
  });

  it("withFileTable: each file once, tests point to it", () => {
    const s = withFileTable(explorerState({ tests: [testNode({ id: "a", filePath: "/x.cs" }), testNode({ id: "b" }), testNode({ id: "c", filePath: "/x.cs" })] }));
    expect(s.files).toEqual(["/x.cs"]);
    expect(s.tests.map((t) => t.fileIndex)).toEqual([0, undefined, 0]);
    expect(s.tests[0].filePath).toBeUndefined();
  });
});
