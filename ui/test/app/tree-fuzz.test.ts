/**
 * Randomized check of the incremental tree (createTreeBuilder + the store's testsDelta): event sequences shaped like the
 * mock's runs (watch runs: projectStatus building → runStarted (subset) → testsUpdated batches → runFinished; plus
 * snapshots, lean-mode extras) arrive with random frame boundaries; after every published frame the patched tree must
 * equal a full buildTree of the same state (structure, order, counts, status, pending / outdated flags, parents).
 */
import { describe, expect, it } from "vitest";
import type { Outcome, ServerEvent, TestNode, TestProject } from "../../src/contract";
import { testKey } from "../../src/store";
import { buildTree, createTreeBuilder, type TreeNode } from "../../src/tree";
import { flushFrame, makeStore, project, testNode } from "../helpers/app";

function catalog(): { projects: TestProject[]; tests: TestNode[] } {
  const projects = [project({ id: "P1", name: "P1" }), project({ id: "P2", name: "P2", path: "/src/P2/P2.csproj" })];
  const tests: TestNode[] = [];
  const file = (c: string) => `/src/${c}.cs`;
  for (const [pid, ns] of [["P1", "P1.Math"], ["P1", "P1.Orders"], ["P2", "P2"]] as const)
    for (const cls of ["CalcTests", "OrderTests"]) {
      let line = 1;
      // like the mock's CalculatorTests: a data-driven method declared between single tests
      for (const m of ["Add", "Subtract"]) tests.push(testNode({ id: `${ns}.${cls}.${m}`, projectId: pid, filePath: file(cls), line: (line += 3) }));
      for (let i = 0; i < 3; i++)
        tests.push(testNode({ id: `${ns}.${cls}.Multiply#${i}`, projectId: pid, methodName: "Multiply", displayName: `Multiply (${i})`, filePath: file(cls), line: 16 }));
      tests.push(testNode({ id: `${ns}.${cls}.Divide_ByZero_Throws`, projectId: pid, filePath: file(cls), line: 19 }));
    }
  return { projects, tests };
}

/** Deterministic PRNG (mulberry32) so a failure prints a reproducible seed. */
function rng(seed: number) {
  let s = seed;
  return () => {
    s = (s + 0x6d2b79f5) | 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** A comparable description of a tree: every node with its place, aggregates and flags; parents must be consistent. */
function describeTree(roots: TreeNode[]): string[] {
  const out: string[] = [];
  const walk = (n: TreeNode, parent: TreeNode | undefined, depth: number) => {
    if (n.parent !== parent) out.push(`!! parent of ${n.key} is ${n.parent?.key ?? "none"} (stale object: ${n.parent !== parent})`);
    out.push(
      `${"  ".repeat(depth)}${n.key} ${n.status} ${n.durationMs} p=${!!n.pending} o=${!!n.outdated} ${JSON.stringify(n.counts)} ${n.test ? `${n.test.outcome}/${n.test.durationMs ?? ""}/${n.test.history?.length ?? ""}` : ""}`,
    );
    for (const c of n.children) walk(c, n, depth + 1);
  };
  for (const r of roots) walk(r, undefined, 0);
  return out;
}

/** keys of the test leaves flagged pending */
function findPending(roots: TreeNode[]): string[] {
  const out: string[] = [];
  const walk = (n: TreeNode) => (n.test ? n.pending && out.push(testKey(n.test)) : n.children.forEach(walk));
  roots.forEach(walk);
  return out.sort();
}

const OUTCOMES: Outcome[] = ["passed", "failed", "skipped", "passed", "passed", "timedOut"];

describe("incremental tree vs full build (randomized event order and frame boundaries)", () => {
  it("clears pending flags when runStarted / runFinished share a frame with a testsUpdated", async () => {
    // Regression: runStarted / runFinished replace `pending` but kept the testsDelta of an earlier testsUpdated of the
    // same frame, so the tree patched only that delta's keys: tests of the finished run kept their "previous result"
    // (pending) dimming, and tests of a new run were not dimmed.
    const { projects, tests } = catalog();
    const { store, transport } = makeStore({ kind: "mock", state: { projects, tests } });
    const builder = createTreeBuilder();
    const check = async () => {
      await flushFrame();
      const s = store.get();
      const patched = builder.build(store.$.projects(), store.$.tests(), store.$.pending(), store.$.testsDelta(), undefined);
      expect(describeTree(patched)).toEqual(describeTree(buildTree(s.projects, s.tests, s.pending, undefined)));
    };
    await check();
    const [a, b, c] = tests;
    const run = { runId: "r1", startedAt: "", total: 2, passed: 0, failed: 0, skipped: 0 };
    // an unrelated update, then a run starts (no queued testsUpdated, no projectStatus) in the same frame
    transport.emit({ type: "testsUpdated", tests: [{ ...a, outcome: "passed" }] });
    transport.emit({ type: "runStarted", run, testIds: [b.id, c.id] });
    await check();
    expect(findPending(builder.build(store.$.projects(), store.$.tests(), store.$.pending(), store.$.testsDelta()))).toEqual([testKey(b), testKey(c)].sort());
    // b reports; the history update and runFinished arrive in one frame: nothing is pending any more
    transport.emit({ type: "testsUpdated", tests: [{ ...b, outcome: "failed" }] });
    await check();
    transport.emit({ type: "testsUpdated", tests: [{ ...b, outcome: "failed", history: [{ outcome: "failed", at: "x" }] }] });
    transport.emit({ type: "runFinished", run });
    await check();
    expect(findPending(builder.build(store.$.projects(), store.$.tests(), store.$.pending(), store.$.testsDelta()))).toEqual([]);
  });

  it("matches buildTree after every published frame", async () => {
    let frames = 0;
    const SEEDS = Number(process.env.TREE_FUZZ_SEEDS ?? 60);
    for (let seed = 1; seed <= SEEDS; seed++) {
      const rand = rng(seed);
      const pick = <T,>(a: readonly T[]) => a[Math.floor(rand() * a.length)];
      const { projects, tests } = catalog();
      const { store, transport } = makeStore({ kind: "mock", state: { projects, tests } });
      const builder = createTreeBuilder();
      let current = new Map(tests.map((t) => [testKey(t), t]));
      let projs = projects;
      let run = 0;
      let inRun: string[] | undefined;
      const log: string[] = [];
      const emit = (e: ServerEvent) => {
        log.push(e.type === "testsUpdated" ? `testsUpdated ${e.tests.map((t) => `${t.id}=${t.outcome}`).join(",")}` : e.type);
        transport.emit(e);
      };
      const update = (keys: string[], patch: (t: TestNode) => Partial<TestNode>) => {
        const batch = keys.map((k) => {
          const t = { ...current.get(k)!, ...patch(current.get(k)!) };
          current.set(k, t);
          return t;
        });
        emit({ type: "testsUpdated", tests: batch });
      };
      let step = 0;
      /** one frame: the store publishes, the app's roots computed rebuilds; compare with a full build */
      const frame = async () => {
        await flushFrame();
        const s = store.get();
        const patched = builder.build(store.$.projects(), store.$.tests(), store.$.pending(), store.$.testsDelta(), undefined);
        const full = buildTree(s.projects, s.tests, s.pending, undefined);
        const a = describeTree(patched);
        const b = describeTree(full);
        // the order of the tree must not depend on the order the tests arrived in (a method's data rows keep their
        // discovery order by design: shuffle whole methods)
        const slot = new Map<string, number>();
        const slotOf = (k: string) => slot.get(k.replace(/#\d+$/, "")) ?? slot.set(k.replace(/#\d+$/, ""), rand()).get(k.replace(/#\d+$/, ""))!;
        const shuffled = new Map([...s.tests].sort(([a], [b]) => slotOf(a) - slotOf(b)));
        const c = describeTree(buildTree(s.projects, shuffled, s.pending, undefined));
        if (c.join("\n") !== b.join("\n")) {
          const i = c.findIndex((x, j) => x !== b[j]);
          throw new Error(`seed ${seed} step ${step}: the tree order depends on the input order (line ${i})\n  ${b[i]}\n  ${c[i]}`);
        }
        frames++;
        if (a.join("\n") !== b.join("\n")) {
          const i = a.findIndex((x, j) => x !== b[j]);
          throw new Error(`seed ${seed} step ${step}: incremental tree differs from a full build at line ${i}\n  patched: ${a[i]}\n  full:    ${b[i]}\n  last events:\n    ${log.slice(-12).join("\n    ")}`);
        }
      };
      const setProject = (id: string, status: TestProject["status"]) => {
        projs = projs.map((p) => (p.id === id ? { ...p, status } : p));
        emit({ type: "projectStatus", project: projs.find((p) => p.id === id)! });
      };

      for (step = 0; step < 120; step++) {
        const r = rand();
        const keys = [...current.keys()];
        if (!inRun && r < 0.25) {
          // a run: a project (watch run), a subset, or everything
          const scope = pick(["project", "subset", "all"] as const);
          const sel = scope === "all" ? keys : scope === "project" ? keys.filter((k) => k.startsWith(pick(["P1|", "P2|"]))) : keys.filter(() => rand() < 0.3);
          if (!sel.length) continue;
          const pids = [...new Set(sel.map((k) => k.slice(0, k.indexOf("|"))))];
          // the host does not always report a build (no-op build, or the status arrives in another frame)
          if (rand() < 0.5) for (const pid of pids) setProject(pid, "building");
          if (rand() < 0.5) await frame();
          inRun = [...sel];
          emit({ type: "runStarted", run: { runId: `r${++run}`, startedAt: new Date(2026, 1, 1, 0, run).toISOString(), total: sel.length, passed: 0, failed: 0, skipped: 0 }, testIds: sel.map((k) => current.get(k)!.id) });
          if (rand() < 0.7) update(sel, () => ({ outcome: "queued" }));
          if (rand() < 0.5) for (const pid of pids) setProject(pid, "running");
        } else if (inRun && r < 0.7) {
          // a batch of results (or "running" starts)
          const n = 1 + Math.floor(rand() * 4);
          const batch = inRun.splice(0, n);
          if (rand() < 0.3) update(batch, () => ({ outcome: "running" }));
          else update(batch, () => ({ outcome: pick(OUTCOMES), durationMs: Math.round(rand() * 50), lastRunAt: new Date().toISOString() }));
          if (rand() < 0.3) inRun.push(...batch.filter(() => rand() < 0.5)); // reported "running" first, result later
          if (!inRun.length || rand() < 0.05) {
            // finish: history testsUpdated right before runFinished (like the host), sometimes in the same frame
            const done = keys.filter(() => rand() < 0.4);
            if (done.length) update(done, (t) => ({ history: [...(t.history ?? []), { outcome: t.outcome, at: new Date().toISOString() }] }));
            emit({ type: "runFinished", run: { runId: `r${run}`, startedAt: "", total: 0, passed: 0, failed: 0, skipped: 0 } });
            for (const p of projs) if (p.status !== "idle") setProject(p.id, "idle");
            inRun = undefined;
          }
        } else if (r < 0.75) {
          emit({ type: "state", state: { projects: projs, tests: [...current.values()], watch: false, coverageEnabled: false, currentRun: undefined } });
          inRun = undefined;
        } else if (r < 0.8) {
          // a project fails to build / recovers (its results become outdated)
          const p = pick(projs);
          setProject(p.id, p.status === "error" ? "idle" : "error");
        } else if (r < 0.85) {
          emit({ type: "log", level: "info", message: "x", timestamp: "" });
        }
        if (rand() < 0.6) await frame();
      }
    }
    expect(frames).toBeGreaterThan(SEEDS * 50);
  }, 60_000);
});
