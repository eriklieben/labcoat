import { describe, expect, it, vi } from "vitest";
import type { TestNode, TestProject } from "../../src/contract";
import { testKey } from "../../src/store";
import {
  GROUP_LABELS,
  QUERY_TOKENS,
  bucket,
  buildTree,
  compileQuery,
  createTreeBuilder,
  defaultExpanded,
  emptyCounts,
  filterCounts,
  findNode,
  flatten,
  fqName,
  indexNodes,
  leaves,
  matchesStatus,
  matchingTests,
  onStatusFilterRequest,
  regroup,
  requestStatusFilter,
  slowFactor,
  sortRanks,
  treeKeyOf,
  type TreeNode,
} from "../../src/tree";
import { project, testNode } from "../helpers/app";

const mapOf = (tests: TestNode[]) => new Map(tests.map((t) => [testKey(t), t]));

/** A small suite: P1 (namespace Ns with ClassA / ClassB, a data-driven method), P2 (namespace = project id). */
function suite() {
  const tests = [
    testNode({ id: "Ns.ClassA.Alpha", outcome: "passed", durationMs: 10, line: 10, filePath: "/a.cs", traits: [{ key: "Category", value: "Unit" }] }),
    testNode({ id: "Ns.ClassA.Beta", outcome: "failed", durationMs: 30, line: 20, filePath: "/a.cs", errorMessage: "Expected 1 but was 2", stackTrace: "at Beta()" }),
    testNode({ id: "Ns.ClassB.Gamma", outcome: "skipped", traits: [{ key: "Owner", value: "web-team" }] }),
    testNode({ id: "Ns.ClassB.Data#0", methodName: "Data", displayName: "Data (1)", outcome: "passed", durationMs: 5 }),
    testNode({ id: "Ns.ClassB.Data#1", methodName: "Data", displayName: "Data (2)", outcome: "timedOut", durationMs: 50 }),
    testNode({ id: "P2.ClassC.Delta", projectId: "P2", namespace: "P2", className: "ClassC", outcome: "notRun", stdOut: "hello world" }),
  ];
  const projects = [project(), project({ id: "P2", name: "Project.Two" })];
  return { tests, projects, map: mapOf(tests) };
}

const allExpanded = () => true;
const labels = (rows: { node: TreeNode }[]) => rows.map((r) => r.node.label);

describe("bucket / matchesStatus / defaultExpanded / fqName", () => {
  it("maps outcomes to buckets", () => {
    expect(bucket("passed")).toBe("passed");
    expect(bucket("failed")).toBe("failed");
    expect(bucket("timedOut")).toBe("failed");
    expect(bucket("skipped")).toBe("skipped");
    expect(bucket("running")).toBe("running");
    expect(bucket("queued")).toBe("queued");
    expect(bucket("notRun")).toBe("notRun");
    expect(bucket("cancelled")).toBe("notRun");
  });

  it("matches status filters", () => {
    expect(matchesStatus("failed", "all")).toBe(true);
    expect(matchesStatus("failed", "flaky")).toBe(false);
    expect(matchesStatus("failed", "new")).toBe(false);
    expect(matchesStatus("running", "notRun")).toBe(true);
    expect(matchesStatus("queued", "notRun")).toBe(true);
    expect(matchesStatus("passed", "notRun")).toBe(false);
    expect(matchesStatus("timedOut", "failed")).toBe(true);
    expect(matchesStatus("skipped", "passed")).toBe(false);
  });

  it("expands groups by default, honours all/none", () => {
    expect(defaultExpanded("project", "default")).toBe(true);
    expect(defaultExpanded("class", "default")).toBe(true);
    expect(defaultExpanded("group", "default")).toBe(true);
    expect(defaultExpanded("method", "default")).toBe(false);
    expect(defaultExpanded("method", "all")).toBe(true);
    expect(defaultExpanded("project", "none")).toBe(false);
  });

  it("fqName joins namespace, class and display name", () => {
    expect(fqName(testNode({ id: "A.B.C" }))).toBe("A.B.C");
    expect(fqName(testNode({ id: "C", namespace: undefined, className: undefined }))).toBe("C");
  });

  it("emptyCounts is all zeros", () => {
    expect(Object.values(emptyCounts()).every((v) => v === 0)).toBe(true);
  });
});

describe("buildTree", () => {
  it("builds Project > Namespace > Class > Method > data rows with aggregated counts", () => {
    const { projects, map } = suite();
    const roots = buildTree(projects, map);
    expect(roots.map((r) => r.key)).toEqual(["p:P1", "p:P2"]);
    const p1 = roots[0];
    expect(p1.kind).toBe("project");
    expect(p1.project?.name).toBe("Project.One");
    expect(p1.children.map((c) => c.key)).toEqual(["n:P1:Ns"]);
    const ns = p1.children[0];
    expect(ns.children.map((c) => c.label)).toEqual(["ClassA", "ClassB"]);
    const classA = ns.children[0];
    // tests in one file sort by line
    expect(classA.children.map((c) => c.label)).toEqual(["Alpha", "Beta"]);
    expect(classA.status).toBe("failed");
    expect(classA.durationMs).toBe(40);
    const classB = ns.children[1];
    const method = classB.children.find((c) => c.kind === "method")!;
    expect(method.key).toBe("m:P1:Ns.ClassB.Data");
    // data rows keep their order and drop the method prefix from the label
    expect(method.children.map((c) => c.label)).toEqual(["(1)", "(2)"]);
    expect(method.children[0].sublabel).toBe("Data");
    expect(method.status).toBe("failed");
    expect(method.search).toBe("ns.classb.data");
    expect(p1.counts).toMatchObject({ total: 5, passed: 2, failed: 2, skipped: 1, notRun: 0 });
    expect(p1.status).toBe("failed");
  });

  it("skips a namespace that only repeats the project id", () => {
    const { projects, map } = suite();
    const p2 = buildTree(projects, map)[1];
    expect(p2.children.map((c) => c.key)).toEqual(["c:P2:P2.ClassC"]);
    expect(p2.status).toBe("notRun");
  });

  it("orders a class's tests and data-driven methods by source line, whatever order they arrive in", () => {
    // Regression: the sibling comparator sorted methods by label but tests by line (not transitive), so the order
    // depended on the input order and on the engine's sort internals; in the browser Divide_ByZero_Throws moved from
    // before Multiply_ReturnsProduct to after Subtract_ReturnsDifference between two builds of the same state.
    const f = "/src/CalculatorTests.cs";
    const calc = [
      testNode({ id: "M.CalculatorTests.Add_ReturnsSum", filePath: f, line: 7 }),
      testNode({ id: "M.CalculatorTests.Subtract_ReturnsDifference", filePath: f, line: 10 }),
      ...[0, 1, 2].map((i) =>
        testNode({ id: `M.CalculatorTests.Multiply_ReturnsProduct#${i}`, methodName: "Multiply_ReturnsProduct", displayName: `Multiply_ReturnsProduct (${i})`, filePath: f, line: 16 }),
      ),
      testNode({ id: "M.CalculatorTests.Divide_ByZero_Throws", filePath: f, line: 19 }),
      // no source location: after the located ones, by label
      testNode({ id: "M.CalculatorTests.Zeta_NoSource" }),
      testNode({ id: "M.CalculatorTests.Alpha_NoSource" }),
    ];
    const expected = ["Add_ReturnsSum", "Subtract_ReturnsDifference", "Multiply_ReturnsProduct", "Divide_ByZero_Throws", "Alpha_NoSource", "Zeta_NoSource"];
    const permutations = <T,>(a: T[]): T[][] => (a.length <= 1 ? [a] : a.flatMap((x, i) => permutations([...a.slice(0, i), ...a.slice(i + 1)]).map((p) => [x, ...p])));
    // every insertion order of the single tests (the data rows stay together, in their order)
    const singles = calc.filter((t) => !t.id.includes("#"));
    const rows = calc.filter((t) => t.id.includes("#"));
    for (const p of permutations(singles)) {
      const tests = [...p.slice(0, 2), ...rows, ...p.slice(2)];
      const cls = findNode(buildTree([project()], mapOf(tests)), "c:P1:M.CalculatorTests")!;
      expect(cls.children.map((c) => c.label)).toEqual(expected);
      expect(cls.children[2].children.map((c) => c.label)).toEqual(["(0)", "(1)", "(2)"]);
    }
  });

  it("sorts a partial class by file, then line; classes and namespaces by label", () => {
    const tests = [
      testNode({ id: "Ns.B.InB2", filePath: "/b2.cs", line: 1 }),
      testNode({ id: "Ns.B.InB1Late", filePath: "/b1.cs", line: 30 }),
      testNode({ id: "Ns.B.InB1", filePath: "/b1.cs", line: 3 }),
      testNode({ id: "Ns.A.Only", filePath: "/a.cs", line: 99 }),
    ];
    const ns = buildTree([project()], mapOf(tests))[0].children[0];
    expect(ns.children.map((c) => c.label)).toEqual(["A", "B"]);
    expect(ns.children[1].children.map((c) => c.label)).toEqual(["InB1", "InB1Late", "InB2"]);
  });

  it("creates a project node for tests of an unknown project", () => {
    const t = testNode({ id: "X.Y.Z", projectId: "PX" });
    const roots = buildTree([], mapOf([t]));
    expect(roots[0].key).toBe("p:PX");
    expect(roots[0].label).toBe("PX");
  });

  it("puts tests without namespace/class directly under the project and builds search text", () => {
    const t = testNode({ id: "Lonely", namespace: undefined, className: undefined, methodName: undefined, traits: [{ key: "k", value: "TraitV" }] });
    const roots = buildTree([project()], mapOf([t]));
    const leaf = roots[0].children[0];
    expect(leaf.key).toBe(`t:P1|Lonely`);
    expect(leaf.search).toBe("p1 lonely traitv");
  });

  it("keeps a data row's label when it does not start with the method name", () => {
    const t1 = testNode({ id: "N.C.M#0", methodName: "M", displayName: "Custom one" });
    const t2 = testNode({ id: "N.C.M#1", methodName: "M", displayName: "M" });
    const roots = buildTree([project()], mapOf([t1, t2]));
    const m = findNode(roots, "m:P1:N.C.M")!;
    expect(m.children.map((c) => [c.label, c.sublabel])).toEqual([
      ["Custom one", undefined],
      ["M", undefined],
    ]);
  });

  it("marks pending, quarantined, flaky, stale and outdated leaves in counts", () => {
    const tests = [
      testNode({ id: "N.C.A", outcome: "passed", flaky: true, stale: true }),
      testNode({ id: "N.C.B", outcome: "failed" }),
      testNode({ id: "N.C.C", outcome: "skipped" }),
      testNode({ id: "N.C.D", outcome: "failed" }),
    ];
    const pending = new Set(["P1|N.C.A", "P1|N.C.B", "P1|N.C.C"]);
    const quarantined = new Set(["P1|N.C.D"]);
    const roots = buildTree([project({ resultsOutdated: true })], mapOf(tests), pending, quarantined);
    const c = roots[0].counts;
    expect(c).toMatchObject({ total: 4, passed: 1, failed: 1, skipped: 1, quarantined: 1, pending: 3, pendingPassed: 1, pendingFailed: 1, pendingSkipped: 1, flaky: 1, stale: 1, outdated: 4 });
    expect(findNode(roots, "t:P1|N.C.A")!.pending).toBe(true);
    expect(findNode(roots, "t:P1|N.C.A")!.outdated).toBe(true);
    expect(findNode(roots, "c:P1:N.C")!.outdated).toBe(true);
  });

  it("project status overrides the aggregate (building / discovering / error)", () => {
    const t = testNode({ outcome: "passed" });
    expect(buildTree([project({ status: "building" })], mapOf([t]))[0].status).toBe("building");
    expect(buildTree([project({ status: "discovering" })], mapOf([t]))[0].status).toBe("discovering");
    const err = buildTree([project({ status: "error" })], mapOf([t]))[0];
    expect(err.status).toBe("error");
    expect(err.counts.outdated).toBe(1);
  });

  it("aggregates running / queued / notRun / all-skipped", () => {
    const st = (outcomes: TestNode["outcome"][]) =>
      buildTree([project()], mapOf(outcomes.map((o, i) => testNode({ id: `N.C.T${i}`, outcome: o }))))[0].status;
    expect(st(["passed", "running"])).toBe("running");
    expect(st(["passed", "queued"])).toBe("queued");
    expect(st(["passed", "notRun"])).toBe("notRun");
    expect(st(["skipped", "skipped"])).toBe("skipped");
    expect(st(["skipped", "passed"])).toBe("passed");
    expect(buildTree([project()], new Map())[0].status).toBe("notRun");
  });

  it("leaves / findNode / indexNodes", () => {
    const { projects, map } = suite();
    const roots = buildTree(projects, map);
    expect(leaves(roots[0])).toHaveLength(5);
    expect(findNode(roots, "missing")).toBeUndefined();
    const idx = indexNodes(roots);
    expect(idx.get("p:P2")).toBe(roots[1]);
    expect(idx.size).toBeGreaterThan(10);
  });
});

describe("flatten", () => {
  it("lists every expanded row with depth, posinset and setsize", () => {
    const { projects, map } = suite();
    const roots = buildTree(projects, map);
    const rows = flatten(roots, allExpanded, "", "all");
    expect(rows[0]).toMatchObject({ index: 0, depth: 0, expanded: true, hasChildren: true, pos: 1, size: 2 });
    expect(rows.at(-1)!.node.label).toBe("Delta");
    expect(rows.every((r, i) => r.index === i)).toBe(true);
    expect(rows.find((r) => r.node.label === "ClassB")!.depth).toBe(2);
  });

  it("collapsed nodes hide their children", () => {
    const { projects, map } = suite();
    const roots = buildTree(projects, map);
    const rows = flatten(roots, (n) => n.kind === "project", "", "all");
    expect(labels(rows)).toEqual(["Project.One", "Ns", "Project.Two", "ClassC"]);
    expect(rows[1].expanded).toBe(false);
  });

  it("filters by text and status, reporting matched counts on groups", () => {
    const { projects, map } = suite();
    const roots = buildTree(projects, map);
    const rows = flatten(roots, allExpanded, "alpha", "all");
    expect(labels(rows)).toEqual(["Project.One", "Ns", "ClassA", "Alpha"]);
    expect(rows[0].matched).toBe(1);
    expect(rows[3].matched).toBeUndefined();
    const failed = flatten(roots, allExpanded, "", "failed");
    expect(failed.filter((r) => r.node.test).map((r) => r.node.test!.id)).toEqual(["Ns.ClassA.Beta", "Ns.ClassB.Data#1"]);
    const notRun = flatten(roots, allExpanded, "", "notRun");
    expect(notRun.filter((r) => r.node.test).map((r) => r.node.label)).toEqual(["Delta"]);
  });

  it("flat mode lists matching leaves only, with broken projects at the end", () => {
    const { tests } = suite();
    const projects = [project(), project({ id: "P2", name: "Project.Two" }), project({ id: "P3", name: "Broken", status: "error" })];
    const roots = buildTree(projects, mapOf(tests));
    const rows = flatten(roots, allExpanded, "", "failed", true);
    expect(rows.map((r) => r.node.label)).toEqual(["Beta", "(2)", "Broken"]);
    expect(rows[0]).toMatchObject({ flat: true, depth: 0, pos: 1, size: 3 });
    expect(rows[2].matched).toBe(0);
    // flat ranking (slowest)
    const ranks = sortRanks(roots, "slowest")!;
    const ranked = flatten(roots, allExpanded, "", "failed", true, { flatRank: ranks.flatRank });
    expect(labels(ranked)).toEqual(["(2)", "Beta", "Broken"]);
  });

  it("keeps a broken empty project visible while filtering by status, not by text", () => {
    const roots = buildTree([project({ status: "error" })], new Map());
    expect(flatten(roots, allExpanded, "", "failed")).toHaveLength(1);
    expect(flatten(roots, allExpanded, "foo", "all")).toHaveLength(0);
  });

  it("flaky and new filters", () => {
    const tests = [testNode({ id: "N.C.A", flaky: true, outcome: "passed" }), testNode({ id: "N.C.B", outcome: "failed" })];
    const roots = buildTree([project()], mapOf(tests));
    expect(flatten(roots, allExpanded, "", "flaky", true).map((r) => r.node.label)).toEqual(["A"]);
    expect(flatten(roots, allExpanded, "", "new", true, { newKeys: new Set(["P1|N.C.B"]) }).map((r) => r.node.label)).toEqual(["B"]);
    expect(flatten(roots, allExpanded, "", "new", true)).toHaveLength(0);
  });

  it("orders siblings by rank (duration sort)", () => {
    const { projects, map } = suite();
    const roots = buildTree(projects, map);
    const r = sortRanks(roots, "slowest")!;
    const rows = flatten(roots, allExpanded, "", "all", false, { rank: r.rank });
    const classA = rows.filter((x) => x.node.parent?.label === "ClassA").map((x) => x.node.label);
    expect(classA).toEqual(["Beta", "Alpha"]);
    // cached ordering: same result again
    expect(labels(flatten(roots, allExpanded, "", "all", false, { rank: r.rank }))).toEqual(labels(rows));
  });
});

describe("sortRanks", () => {
  it("returns undefined for the default sort", () => {
    expect(sortRanks([], "default")).toBeUndefined();
  });

  it("ranks by duration, unknown durations last, ties stable", () => {
    const tests = [
      testNode({ id: "N.C.A", outcome: "passed", durationMs: 5 }),
      testNode({ id: "N.C.B", outcome: "skipped", durationMs: 100 }),
      testNode({ id: "N.C.C", outcome: "passed", durationMs: 50 }),
      testNode({ id: "N.C.D", outcome: "notRun" }),
      testNode({ id: "N.C.E", outcome: "passed", durationMs: 5 }),
    ];
    const roots = buildTree([project()], mapOf(tests));
    const slow = sortRanks(roots, "slowest")!;
    const order = (m: Map<string, number>) => [...m.entries()].filter(([k]) => k.startsWith("t:")).sort((a, b) => a[1] - b[1]).map(([k]) => k.slice(-1));
    expect(order(slow.flatRank)).toEqual(["C", "A", "E", "B", "D"]);
    const fast = sortRanks(roots, "fastest")!;
    expect(order(fast.flatRank)).toEqual(["A", "E", "C", "B", "D"]);
  });
});

describe("filterCounts / matchingTests", () => {
  it("sums the roots without a query and the matching leaves with one", () => {
    const { projects, map } = suite();
    const roots = buildTree(projects, map);
    expect(filterCounts(roots, "").total).toBe(6);
    const c = filterCounts(roots, "ClassA");
    expect(c).toMatchObject({ total: 2, passed: 1, failed: 1 });
  });

  it("returns the tests the tree shows, deduplicated", () => {
    const { projects, map } = suite();
    const roots = buildTree(projects, map);
    expect(matchingTests(roots, "", "failed").map((t) => t.id)).toEqual(["Ns.ClassA.Beta", "Ns.ClassB.Data#1"]);
    // regrouped trees may contain the same leaf twice (not here, but keys are deduplicated)
    expect(matchingTests([...roots, ...roots], "alpha", "all")).toHaveLength(1);
  });
});

describe("compileQuery", () => {
  const { projects, map } = suite();
  const roots = buildTree(projects, map);
  const ids = (q: string, newKeys?: Set<string>) => matchingTests(roots, q, "all", newKeys).map((t) => t.id.split(".").pop());

  it("empty / whitespace / incomplete token → no predicate", () => {
    expect(compileQuery("")).toBeUndefined();
    expect(compileQuery("   ")).toBeUndefined();
    expect(compileQuery("is:")).toBeUndefined();
  });

  it("free text matches search text, every word (AND)", () => {
    expect(ids("classa")).toEqual(["Alpha", "Beta"]);
    expect(ids("classa beta")).toEqual(["Beta"]);
    expect(ids("unknown:thing")).toEqual([]);
  });

  it("is: tokens and alternatives", () => {
    expect(ids("is:failed")).toEqual(["Beta", "Data#1"]);
    expect(ids("is:passed")).toEqual(["Alpha", "Data#0"]);
    expect(ids("is:skip")).toEqual(["Gamma"]);
    expect(ids("is:notrun")).toEqual(["Delta"]);
    expect(ids("is:passed|skipped")).toEqual(["Alpha", "Data#0", "Gamma"]);
    expect(ids("IS:failed,notrun")).toEqual(["Beta", "Data#1", "Delta"]);
    expect(ids("is:flaky")).toEqual([]);
    expect(ids("is:new", new Set(["P1|Ns.ClassA.Beta"]))).toEqual(["Beta"]);
    expect(ids("is:running")).toEqual([]);
    expect(ids("is:slow")).toEqual([]);
    expect(ids("is:zzz")).toEqual([]);
  });

  it("cat:, trait:, msg:, proj: and quoted values", () => {
    expect(ids("cat:unit")).toEqual(["Alpha"]);
    expect(ids("trait:owner")).toEqual(["Gamma"]);
    expect(ids("trait:owner=web")).toEqual(["Gamma"]);
    expect(ids("trait:owner=nope")).toEqual([]);
    expect(ids('msg:"expected 1"')).toEqual(["Beta"]);
    expect(ids("msg:hello")).toEqual(["Delta"]);
    expect(ids("proj:p2")).toEqual(["Delta"]);
    expect(ids('"class a"')).toEqual([]);
  });

  it("is:running matches running and queued tests; is:slow uses slowFactor", () => {
    const hist = [100, 100, 100].map((d, i) => ({ outcome: "passed" as const, durationMs: d, at: `2026-01-0${i + 1}` }));
    const tests = [
      testNode({ id: "N.C.R", outcome: "running" }),
      testNode({ id: "N.C.Q", outcome: "queued" }),
      testNode({ id: "N.C.S", outcome: "passed", durationMs: 500, history: hist }),
    ];
    const r = buildTree([project({ name: "Shop" })], mapOf(tests));
    expect(matchingTests(r, "is:running", "all").map((t) => t.id)).toEqual(["N.C.Q", "N.C.R"]);
    expect(matchingTests(r, "is:slow", "all").map((t) => t.id)).toEqual(["N.C.S"]);
    // proj: also matches the project name of a project node
    const pn = r[0];
    expect(compileQuery("proj:shop")!({ ...pn.children[0].children[0].children[0], project: pn.project })).toBe(true);
    expect(compileQuery("x")!(pn)).toBe(false);
  });

  it("QUERY_TOKENS documents every token", () => {
    expect(QUERY_TOKENS.map((t) => t.token)).toEqual(["is:", "cat:", "trait:", "msg:", "proj:"]);
  });
});

describe("slowFactor", () => {
  const h = (ds: number[], extra: Partial<{ outcome: TestNode["outcome"]; at: string }> = {}) =>
    ds.map((d, i) => ({ outcome: extra.outcome ?? ("passed" as const), durationMs: d, at: extra.at ?? `t${i}` }));

  it("is undefined without enough data, for fast or non-finished tests", () => {
    expect(slowFactor(testNode({ durationMs: 10, history: h([1, 1, 1]) }))).toBeUndefined();
    expect(slowFactor(testNode({ durationMs: 100 }))).toBeUndefined();
    expect(slowFactor(testNode({ durationMs: 100, history: h([10, 10]) }))).toBeUndefined();
    expect(slowFactor(testNode({ durationMs: 100, outcome: "skipped", history: h([10, 10, 10]) }))).toBeUndefined();
    expect(slowFactor(testNode({ durationMs: 100, history: h([10, 10, 10], { outcome: "skipped" }) }))).toBeUndefined();
    expect(slowFactor(testNode({ durationMs: 100, history: h([0, 0, 0]) }))).toBeUndefined();
  });

  it("returns the factor over the median of earlier results", () => {
    expect(slowFactor(testNode({ durationMs: 100, history: h([10, 20, 30]) }))).toBe(5);
    expect(slowFactor(testNode({ durationMs: 30, history: h([10, 20, 20, 30]) }))).toBeUndefined();
    // the latest entry equals this result: excluded from the median
    const hist = [...h([10, 10, 10]), { outcome: "passed" as const, durationMs: 100, at: "now" }];
    expect(slowFactor(testNode({ durationMs: 100, lastRunAt: "now", history: hist }))).toBe(10);
  });
});

describe("regroup", () => {
  const { projects, map } = suite();
  const roots = buildTree(projects, map);

  it("namespace grouping returns the roots unchanged", () => {
    expect(regroup(roots, "namespace")).toBe(roots);
    expect(GROUP_LABELS.trait).toBe("Trait");
  });

  it("project grouping lifts classes out of namespaces, the namespace becomes context", () => {
    const g = regroup(roots, "project");
    expect(g[0].children.map((c) => [c.label, c.sublabel])).toEqual([
      ["ClassA", "Ns"],
      ["ClassB", "Ns"],
    ]);
    expect(g[0].counts.total).toBe(5);
    expect(g[0].children[0].children[0].parent).toBe(g[0].children[0]);
    expect(g[1].children[0].label).toBe("ClassC");
  });

  it("trait grouping: one group per trait combination, 'No traits' last", () => {
    const g = regroup(roots, "trait");
    expect(g.map((x) => x.label)).toEqual(["Unit", "web-team", "No traits"]);
    expect(g[0].children[0]).toMatchObject({ label: "Alpha", sublabel: "ClassA", key: "t:P1|Ns.ClassA.Alpha" });
    expect(g[2].counts.total).toBe(4);
    const dataRow = g[2].children.find((c) => c.test?.id === "Ns.ClassB.Data#0")!;
    expect(dataRow.label).toBe("Data (1)");
    expect(dataRow.sublabel).toBe("ClassB");
  });

  it("outcome grouping in fixed order; quarantined failures go to Failed", () => {
    const g = regroup(roots, "outcome");
    expect(g.map((x) => x.label)).toEqual(["Failed", "Not run", "Passed", "Skipped"]);
    expect(g[0].status).toBe("failed");
    const q = buildTree([project()], mapOf([testNode({ id: "N.C.Q", outcome: "failed" })]), undefined, new Set(["P1|N.C.Q"]));
    expect(regroup(q, "outcome")[0].label).toBe("Failed");
  });

  it("leaf sublabel falls back to the namespace, keeps the method when names differ", () => {
    const t1 = testNode({ id: "N.C.M#0", methodName: "M", displayName: "Other", className: undefined, namespace: "N" });
    const t2 = testNode({ id: "N.C.M#1", methodName: "M", displayName: "Other2", className: undefined, namespace: "N" });
    const g = regroup(buildTree([project()], mapOf([t1, t2])), "outcome");
    expect(g[0].children[0].sublabel).toBe("M");
    const t3 = testNode({ id: "Solo", namespace: "Nsx", className: undefined, methodName: undefined });
    expect(regroup(buildTree([project()], mapOf([t3])), "trait")[0].children[0].sublabel).toBe("Nsx");
  });
});

describe("treeKeyOf", () => {
  it("returns the test key for a single test and the method key for data rows", () => {
    const t = testNode({ id: "N.C.M" });
    expect(treeKeyOf([t])).toBe("t:P1|N.C.M");
    const d0 = testNode({ id: "N.C.M#0", methodName: "M", displayName: "M (1)" });
    expect(treeKeyOf([d0])).toBe("m:P1:N.C.M");
    expect(treeKeyOf([t, d0])).toBe("m:P1:N.C.M");
    expect(treeKeyOf([testNode({ id: "X", namespace: undefined, className: undefined, methodName: undefined, displayName: "X y" })])).toBe("t:P1|X");
  });
});

describe("status filter requests", () => {
  it("delivers requests to listeners until unsubscribed", () => {
    const fn = vi.fn();
    const off = onStatusFilterRequest(fn);
    requestStatusFilter("failed");
    expect(fn).toHaveBeenCalledWith("failed");
    off();
    requestStatusFilter("passed");
    expect(fn).toHaveBeenCalledTimes(1);
  });
});

describe("createTreeBuilder (incremental)", () => {
  function setup() {
    const { projects, tests } = suite();
    const b = createTreeBuilder();
    const map = mapOf(tests);
    const roots = b.build(projects, map);
    return { b, projects, tests, map, roots };
  }

  it("starts empty and builds fully the first time", () => {
    const b = createTreeBuilder();
    expect(b.index().size).toBe(0);
    const { roots } = setup();
    expect(roots).toHaveLength(2);
  });

  it("returns the same roots when nothing changed", () => {
    const { b, projects, map, roots } = setup();
    expect(b.build(projects, map)).toBe(roots);
    // a new map with the same objects: no changed leaves → same roots
    expect(b.build(projects, new Map(map))).toBe(roots);
  });

  it("patches only the changed path (by scan and by delta)", () => {
    const { b, projects, map, roots } = setup();
    const k = "P1|Ns.ClassA.Alpha";
    const next = new Map(map);
    next.set(k, { ...map.get(k)!, outcome: "failed", durationMs: 99 });
    const r2 = b.build(projects, next);
    expect(r2).not.toBe(roots);
    expect(r2[1]).toBe(roots[1]); // untouched project reused
    const classA = findNode(r2, "c:P1:Ns.ClassA")!;
    expect(classA.counts.failed).toBe(2);
    expect(classA.children[0].parent).toBe(classA);
    expect(b.index().get(`t:${k}`)!.test!.outcome).toBe("failed");
    expect(findNode(r2, "n:P1:Ns")!.children[1]).toBe(findNode(roots, "c:P1:Ns.ClassB"));

    const next2 = new Map(next);
    next2.set(k, { ...next.get(k)!, outcome: "passed" });
    const r3 = b.build(projects, next2, undefined, { from: next, keys: new Set([k]) });
    expect(findNode(r3, "c:P1:Ns.ClassA")!.counts.failed).toBe(1);
  });

  it("patches pending flags", () => {
    const { b, projects, map } = setup();
    const pending = new Set(["P1|Ns.ClassA.Alpha"]);
    const r = b.build(projects, map, pending);
    expect(findNode(r, "t:P1|Ns.ClassA.Alpha")!.pending).toBe(true);
    expect(r[0].counts.pending).toBe(1);
    const r2 = b.build(projects, map, undefined);
    expect(findNode(r2, "t:P1|Ns.ClassA.Alpha")!.pending).toBeUndefined();
    // pending changed but no leaf flag changed → same roots
    expect(b.build(projects, map, new Set())).toBe(r2);
  });

  it("rebuilds fully on structure, project, size or quarantine changes", () => {
    const { b, projects, map, roots } = setup();
    const k = "P1|Ns.ClassA.Alpha";
    const moved = new Map(map);
    moved.set(k, { ...map.get(k)!, line: 999 });
    const r = b.build(projects, moved);
    expect(r).not.toBe(roots);
    expect(r[1]).not.toBe(roots[1]);
    // a new projects array with the same projects (ids, names, outdated flags) keeps the tree
    const r2 = b.build([...projects], moved);
    expect(r2).toBe(r);
    // a project status change replaces only that project's row (scale: no full rebuild per projectStatus event)
    const building = projects.map((p, i) => (i === 1 ? { ...p, status: "building" as const } : p));
    const r3 = b.build(building, moved);
    expect(r3[0]).toBe(r[0]);
    expect(r3[1]).not.toBe(r[1]);
    expect(r3[1].status).toBe("building");
    expect(r3[1].children[0]).toBe(r[1].children[0]);
    expect(r3[1].children[0].parent).toBe(r3[1]);
    // a renamed project → full rebuild
    const renamed = projects.map((p, i) => (i === 1 ? { ...p, name: "Renamed" } : p));
    expect(b.build(renamed, moved)[1].children[0]).not.toBe(r[1].children[0]);
    const smaller = new Map(moved);
    smaller.delete(k);
    expect(findNode(b.build([...projects], smaller), `t:${k}`)).toBeUndefined();
    const q = new Set(["P1|Ns.ClassA.Beta"]);
    const pr = [...projects];
    const r4 = b.build(pr, smaller, undefined, undefined, q);
    expect(findNode(r4, "c:P1:Ns.ClassA")!.counts.quarantined).toBe(1);
    // a delta naming an unknown key → full rebuild
    const extra = new Map(smaller);
    extra.delete("P1|Ns.ClassA.Beta");
    extra.set("P1|New.Key", testNode({ id: "New.Key" }));
    const r5 = b.build(pr, extra, undefined, { from: smaller, keys: new Set(["P1|New.Key"]) }, q);
    expect(findNode(r5, "t:P1|New.Key")).toBeDefined();
  });

  it("a delta key whose test vanished falls back to a full build", () => {
    const { b, projects, map } = setup();
    const next = new Map(map);
    const r = b.build(projects, next, undefined, { from: map, keys: new Set(["P1|Ns.ClassA.Alpha", "nope"]) });
    expect(r).toHaveLength(2);
  });
});

describe("project fixtures", () => {
  it("keeps projects in server order", () => {
    const ps: TestProject[] = [project({ id: "Z", name: "Zed" }), project({ id: "A", name: "Aye" })];
    expect(buildTree(ps, new Map()).map((r) => r.label)).toEqual(["Zed", "Aye"]);
  });
});
