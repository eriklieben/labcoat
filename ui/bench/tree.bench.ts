/**
 * Test tree (src/tree.ts): full build, incremental patch from a results delta, flatten to rows, regroup and duration
 * sort, at 1,000 and 20,000 tests (the `?mock=huge` shape: 8 projects, deep namespaces, data rows).
 * Pure functions, no DOM: these are the per-frame costs behind the tree pane during a run.
 */
import { describe, test } from "vitest";
import type { TestNode } from "../src/contract";
import { testKey } from "../src/store";
import { buildTree, createTreeBuilder, flatten, indexNodes, regroup, sortRanks, type TreeNode } from "../src/tree";
import { catalog, every, QUICK } from "./fixtures";

const SIZES = [1_000, 20_000] as const;

/** `base` with every `stride`th test replaced by a new object with another result (a frame of a running run). */
function withResults(base: Map<string, TestNode>, stride: number, outcome: TestNode["outcome"]) {
  const next = new Map(base);
  const keys = new Set<string>();
  for (const t of every([...base.values()], stride)) {
    const k = testKey(t);
    next.set(k, { ...t, outcome, durationMs: (t.durationMs ?? 0) + 1 });
    keys.add(k);
  }
  return { next, keys };
}

describe("tree", () => {
  for (const n of SIZES) {
    const size = n.toLocaleString("en");

    test(`build ${size} tests`, async ({ bench }) => {
      const c = catalog(n);
      await bench.compare(
        bench("buildTree (full)", () => buildTree(c.projects, c.map)),
        bench("buildTree + indexNodes", () => indexNodes(buildTree(c.projects, c.map))),
        bench("createTreeBuilder().build (first build)", () => createTreeBuilder().build(c.projects, c.map)),
        { ...QUICK, iterations: n > 5_000 ? 5 : 10 },
      );
    });

    test(`incremental patch, ${size} tests`, async ({ bench }) => {
      const c = catalog(n);
      // A ⇄ B: B has 1 % (or 1 test in 20 for "5 %") of the results changed; each sample patches one direction.
      const toggler = (stride: number, useDelta: boolean) => {
        const builder = createTreeBuilder();
        const a = c.map;
        const { next: b, keys } = withResults(a, stride, "running");
        builder.build(c.projects, a);
        let onB = false;
        return () => {
          const [from, to] = onB ? [b, a] : [a, b];
          onB = !onB;
          return builder.build(c.projects, to, undefined, useDelta ? { from, keys } : undefined);
        };
      };
      await bench.compare(
        bench("1 % changed, with store delta", toggler(100, true)),
        bench("1 % changed, scan (no delta)", toggler(100, false)),
        bench("5 % changed, with store delta", toggler(20, true)),
        bench("1 test changed, with store delta", toggler(n, true)),
        QUICK,
      );
    });

    test(`flatten / regroup / sort, ${size} tests`, async ({ bench }) => {
      const c = catalog(n);
      const roots = buildTree(c.projects, c.map);
      const byDefault = (x: TreeNode) => x.kind === "project" || x.kind === "namespace";
      const ranks = sortRanks(roots, "slowest");
      await bench.compare(
        bench("flatten: projects + namespaces expanded", () => flatten(roots, byDefault, "", "all")),
        bench("flatten: everything expanded", () => flatten(roots, () => true, "", "all")),
        bench("flatten: everything expanded, slowest first", () => flatten(roots, () => true, "", "all", false, ranks)),
        bench("sortRanks(slowest)", () => sortRanks(roots, "slowest")),
        bench("regroup by outcome", () => regroup(roots, "outcome")),
        bench("regroup by project", () => regroup(roots, "project")),
        bench("regroup by trait", () => regroup(roots, "trait")),
        { ...QUICK, iterations: n > 5_000 ? 5 : 10 },
      );
    });
  }
});
