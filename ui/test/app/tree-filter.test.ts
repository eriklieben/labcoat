/**
 * The combined filter pass (tree.ts filterPass / createFilterCache): one walk for the rows' visibility, the chip counts
 * and the matching tests, plus incremental narrowing. Checked against a straightforward per-leaf reference.
 */
import { describe, expect, it } from "vitest";
import type { Outcome, TestNode } from "../../src/contract";
import { testKey } from "../../src/store";
import {
  buildTree,
  compileQuery,
  createFilterCache,
  createTreeBuilder,
  emptyCounts,
  filterPass,
  flatten,
  implies,
  matchesStatus,
  parseQuery,
  regroup,
  type Counts,
  type StatusFilter,
  type TreeGroup,
  type TreeNode,
} from "../../src/tree";
import { project, testNode } from "../helpers/app";

const OUTCOMES: Outcome[] = ["passed", "passed", "passed", "failed", "skipped", "notRun", "timedOut", "running"];

function suite(n: number, seed = 1) {
  let s = seed;
  const rnd = () => (s = (s * 16807) % 2147483647) / 2147483647;
  const tests: TestNode[] = [];
  for (let i = 0; i < n; i++) {
    const cls = ["Order", "Customer", "Invoice"][i % 3] + "Tests";
    const ns = ["Shop.Api", "Shop.Domain"][i % 2];
    const m = ["Validate_Order", "Create_Customer", "Delete_Invoice", "Map_Address"][Math.floor(rnd() * 4)];
    tests.push(
      testNode({
        id: `${ns}.${cls}.${m}${i}`,
        projectId: i % 5 === 0 ? "P2" : "P1",
        outcome: OUTCOMES[Math.floor(rnd() * OUTCOMES.length)],
        flaky: rnd() < 0.1 || undefined,
        traits: i % 4 === 0 ? [{ key: "TestCategory", value: i % 8 === 0 ? "Integration" : "Unit" }] : [],
        errorMessage: i % 7 === 0 ? `timeout after ${i} ms` : undefined,
      }),
    );
  }
  return new Map(tests.map((t) => [testKey(t), t]));
}
/** a row as the tree shows it (deep equality of rows would walk the parent pointers) */
const sig = (rows: { node: TreeNode; index: number; depth: number; expanded: boolean; hasChildren: boolean; matched?: number; pos: number; size: number; flat?: boolean }[]) =>
  rows.map((r) => `${r.node.key}|${r.index}|${r.depth}|${r.expanded}|${r.hasChildren}|${r.matched ?? ""}|${r.pos}/${r.size}|${r.flat ? 1 : 0}`);
const projects = [project(), project({ id: "P2", name: "Project.Two", path: "/p2.csproj" })];

/** the reference: every leaf tested on its own (the pre-pass implementation's rules) */
function reference(roots: readonly TreeNode[], text: string, status: StatusFilter, newKeys?: ReadonlySet<string>) {
  const match = compileQuery(text, { newKeys });
  const statusOk = (n: TreeNode) =>
    status === "flaky" ? !!n.test!.flaky : status === "new" ? !!newKeys?.has(testKey(n.test!)) : status === "failed" ? n.counts.failed > 0 : matchesStatus(n.test!.outcome, status);
  const counts = emptyCounts();
  const tests: TestNode[] = [];
  const walk = (n: TreeNode) => {
    if (n.test) {
      if (match && !match(n)) return;
      for (const k of Object.keys(counts) as (keyof Counts)[]) counts[k] += n.counts[k];
      if (statusOk(n) && !tests.includes(n.test)) tests.push(n.test);
      return;
    }
    n.children.forEach(walk);
  };
  roots.forEach(walk);
  return { counts, tests };
}

const QUERIES = [
  "", "o", "or", "ord", "order", "order v", "order va", "order val", "order", "", "is:", "is:f", "is:fa", "is:fl", "is:failed|", "is:failed|p",
  "cat:", "cat:u", "cat:un", "cat:unit", "cat:unit order", "trait:testcategory", "trait:testcategory=", "trait:testcategory=int",
  "msg:", "msg:time", "msg:timeout", "proj:p", "proj:p2", "proj:project.two", '"shop.api', '"shop.api.order', "zz", "zzz", "",
  "is:new", "is:new order", "Validate", "Validate_O", "validate_order1",
];

describe("filterPass", () => {
  const newKeys = new Set<string>();
  const tests = suite(300);
  for (const t of [...tests.values()].filter((_, i) => i % 9 === 0)) newKeys.add(testKey(t));

  for (const group of ["namespace", "project", "trait", "outcome"] as TreeGroup[])
    it(`matches the per-leaf reference for every query / status, cached and narrowed (${group})`, () => {
      const roots = regroup(buildTree(projects, tests), group);
      for (const status of ["all", "failed", "passed", "notRun", "flaky", "new", "skipped"] as StatusFilter[]) {
        const cache = createFilterCache();
        for (const q of QUERIES) {
          const ref = reference(roots, q, status, newKeys);
          for (const p of [cache(roots, q, status, newKeys), filterPass(roots, q, status, newKeys)]) {
            expect({ ...p.counts }, `${q} ${status}`).toEqual(ref.counts);
            expect(p.tests.length, `${q} ${status}`).toBe(ref.tests.length);
            expect(p.tests.every((t, i) => t === ref.tests[i])).toBe(true);
            // rows: the pass handed in == flatten computing its own
            const own = flatten(roots as TreeNode[], () => true, q, status, false, { newKeys });
            const given = flatten(roots as TreeNode[], () => true, q, status, false, { newKeys, pass: p });
            expect(sig(given)).toEqual(sig(own));
          }
        }
      }
    });

  it("narrowing follows the incremental builder's patched trees (parents of reused leaves)", () => {
    const b = createTreeBuilder();
    let cur = tests;
    let roots = b.build(projects, cur);
    const cache = createFilterCache();
    for (let round = 0; round < 6; round++) {
      // results change for a few tests: the builder patches their ancestors, siblings are re-parented
      const next = new Map(cur);
      for (const [k, t] of [...cur].filter((_, i) => (i + round) % 37 === 0)) next.set(k, { ...t, outcome: t.outcome === "failed" ? "passed" : "failed" });
      cur = next;
      roots = b.build(projects, cur);
      for (const q of ["o", "or", "ord", "order", "order v"]) {
        const p = cache(roots, q, "all");
        const own = flatten(roots, () => true, q, "all", false);
        expect(sig(flatten(roots, () => true, q, "all", false, { pass: p }))).toEqual(sig(own));
        const ref = reference(roots, q, "all").tests;
        expect(p.tests.length === ref.length && p.tests.every((t, i) => t === ref[i])).toBe(true);
      }
    }
  });

  it("the cache returns the same pass for the same arguments and narrows from the closest earlier one", () => {
    const roots = buildTree(projects, tests);
    const cache = createFilterCache();
    const a = cache(roots, "ord", "all");
    expect(cache(roots, "ord", "all")).toBe(a);
    const b = cache(roots, "order", "all");
    expect(b.textLeaves!.length).toBeLessThanOrEqual(a.textLeaves!.length);
    // backspace: the earlier pass again
    expect(cache(roots, "ord", "all")).toBe(a);
    // the new-failure keys only matter when the filter reads them
    expect(cache(roots, "ord", "all", new Set(["x"]))).toBe(a);
    // another tree: a fresh pass
    const other = buildTree(projects, tests);
    expect(cache(other, "ord", "all") === a).toBe(false); // (not .not.toBe: its message deep-compares the trees)
  });

  it("no filter: the roots' counts, every test on demand", () => {
    const roots = buildTree(projects, tests);
    const p = filterPass(roots, "", "all");
    expect(p.filtering).toBe(false);
    expect(p.counts.total).toBe(tests.size);
    expect(p.tests).toHaveLength(tests.size);
  });
});

describe("query terms", () => {
  it("parseQuery drops tokens without a value and lowercases", () => {
    expect(parseQuery('is: Order msg:"No Such" cat:')).toEqual([
      { kind: "text", v: "order" },
      { kind: "msg", v: "no such" },
    ]);
  });

  it("parseQuery splits on any whitespace, keeping quoted values (also after a prefix)", () => {
    const words = (text: string) => parseQuery(text).map((t) => (t.kind === "text" ? t.v : `${t.kind}:${t.v}`));
    expect(words('a "b c" d')).toEqual(["a", "b c", "d"]);
    expect(words('msg:"no such host')).toEqual(["msg:no such host"]);
    expect(words('x"y"z')).toEqual(["xy", "z"]);
    expect(words('"" " a""b')).toEqual([" a", "b"]);
    expect(words("\u00a0a\tb\nc\u2028d ")).toEqual(["a", "b", "c", "d"]);
    expect(words('cat:"a b" "c')).toEqual(["cat:a b", "c"]);
    expect(words('p:"q" r"s t"u')).toEqual(["p:q", "rs t", "u"]);
  });

  it("implies: substring terms narrow, is: and trait keys only when equal", () => {
    const i = (a: string, b: string) => implies(parseQuery(a), parseQuery(b));
    expect(i("order", "ord")).toBe(true);
    expect(i("ord x", "ord")).toBe(true);
    expect(i("ord", "order")).toBe(false);
    expect(i("anything", "")).toBe(true);
    expect(i("msg:x", "msg")).toBe(false); // free text "msg" is not implied by the msg: token
    expect(i("is:fl", "is:f")).toBe(false); // is:f = failed, is:fl = flaky
    expect(i("is:failed|p", "is:failed")).toBe(false); // alternatives widen
    expect(i("is:failed order", "is:failed")).toBe(true);
    expect(i("trait:owner=web", "trait:owner")).toBe(true);
    expect(i("trait:owner=web-team", "trait:owner=web")).toBe(true);
    expect(i("trait:owners", "trait:owner")).toBe(false);
    expect(i("trait:owner", "trait:owner=web")).toBe(false);
    expect(i("cat:integration", "cat:int")).toBe(true);
  });
});

describe("payloads without traits", () => {
  it("build, search, group and filter tolerate a test without `traits`", () => {
    const t = { ...testNode({ id: "N.C.NoTraits", outcome: "passed" }), traits: undefined } as unknown as TestNode;
    const tests = new Map([[testKey(t), t]]);
    const roots = buildTree([project()], tests);
    expect(filterPass(roots, "notraits", "all").tests).toEqual([t]);
    expect(filterPass(roots, "cat:unit", "all").tests).toEqual([]);
    expect(filterPass(roots, "trait:owner", "all").tests).toEqual([]);
    expect(regroup(roots, "trait")[0].label).toBe("No traits");
    const b = createTreeBuilder();
    b.build([project()], tests);
    expect(() => b.build([project()], new Map([[testKey(t), { ...t, outcome: "failed" } as TestNode]]))).not.toThrow();
  });
});

describe("createTreeBuilder: run start / finish (every pending flag flips)", () => {
  const shape = (roots: readonly TreeNode[]) => {
    const out: string[] = [];
    const walk = (n: TreeNode, parent?: TreeNode) => {
      expect(n.parent).toBe(parent);
      out.push(`${n.key}|${n.status}|${!!n.pending}|${!!n.outdated}|${JSON.stringify(n.counts)}|${n.durationMs}`);
      n.children.forEach((c) => walk(c, n));
    };
    roots.forEach((r) => walk(r));
    return out;
  };

  it("patches the tree in one walk: same result as a full build, index and parents updated", () => {
    const tests = suite(200);
    const quarantine = new Set([...tests.keys()].filter((_, i) => i % 11 === 0));
    const b = createTreeBuilder();
    const first = b.build(projects, tests, undefined, undefined, quarantine);
    const all = new Set([...tests.keys()].filter((_, i) => i % 3 !== 0));
    const started = b.build(projects, tests, all, undefined, quarantine);
    expect(shape(started)).toEqual(shape(buildTree(projects, tests, all, quarantine)));
    for (const n of [...b.index().values()]) if (n.test) expect(b.index().get(n.key)!.pending).toBe(all.has(testKey(n.test)) || undefined);
    // untouched subtrees are reused; changed ones are new objects (the old tree is not mutated in its values)
    expect(started).not.toBe(first);
    const finished = b.build(projects, tests, undefined, undefined, quarantine);
    expect(shape(finished)).toEqual(shape(buildTree(projects, tests, undefined, quarantine)));
    // unchanged: the same roots
    expect(b.build(projects, tests, undefined, undefined, quarantine)).toBe(finished);
  });

  it("falls back to a full build when a test moved", () => {
    const tests = suite(50);
    const b = createTreeBuilder();
    b.build(projects, tests);
    const [k, t] = [...tests][3];
    const moved = new Map(tests).set(k, { ...t, className: "Elsewhere" });
    const roots = b.build(projects, moved, new Set(tests.keys()));
    expect(shape(roots)).toEqual(shape(buildTree(projects, moved, new Set(tests.keys()))));
  });

  it("leaf counts are shared, frozen objects", () => {
    const roots = buildTree(projects, suite(30));
    const leaves = [...new Set(roots.flatMap(function all(n: TreeNode): TreeNode[] { return n.test ? [n] : n.children.flatMap(all); }))];
    expect(Object.isFrozen(leaves[0].counts)).toBe(true);
    expect(new Set(leaves.map((n) => n.counts)).size).toBeLessThan(leaves.length);
  });
});
