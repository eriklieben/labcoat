/**
 * Tree filter / search (src/tree.ts): query parsing, compiling and the filter pass (which leaves match, chip counts,
 * "Run N matching") over 20,000 tests, for each kind of term: plain text, `is:`, `cat:`, `msg:` and combinations,
 * plus the status chips and incremental narrowing while typing (createFilterCache, as tree-pane.ts uses it).
 */
import { describe, test } from "vitest";
import { buildTree, compileQuery, createFilterCache, filterPass, flatten, parseQuery, type TreeNode } from "../src/tree";
import { catalog, QUICK } from "./fixtures";

const QUERIES = ["order", "is:failed", "cat:Integration", "msg:timeout", "is:failed|flaky cat:Unit", 'proj:billing "validate_order" is:passed', "trait:TestCategory=Integration msg:expected"];

describe("filter", () => {
  test("parse + compile", async ({ bench }) => {
    await bench.compare(
      bench(`parseQuery × ${QUERIES.length}`, () => QUERIES.map(parseQuery)),
      bench(`compileQuery × ${QUERIES.length}`, () => QUERIES.map((q) => compileQuery(q))),
      QUICK,
    );
  });

  for (const n of [1_000, 20_000]) {
    const size = n.toLocaleString("en");

    test(`filter pass, ${size} tests`, async ({ bench }) => {
      const c = catalog(n);
      const roots = buildTree(c.projects, c.map);
      const opts = { ...QUICK, iterations: n > 5_000 ? 5 : 10 };
      await bench.compare(
        bench("no filter", () => filterPass(roots, "", "all")),
        bench('text "order"', () => filterPass(roots, "order", "all")),
        bench('text "handle order succeeds" (3 words)', () => filterPass(roots, "handle order succeeds", "all")),
        bench("is:failed", () => filterPass(roots, "is:failed", "all")),
        bench("cat:Integration", () => filterPass(roots, "cat:Integration", "all")),
        bench("msg:timeout", () => filterPass(roots, "msg:timeout", "all")),
        bench("is:failed|flaky cat:Unit", () => filterPass(roots, "is:failed|flaky cat:Unit", "all")),
        bench("status chip: failed", () => filterPass(roots, "", "failed")),
        bench('text "order" + chip passed', () => filterPass(roots, "order", "passed")),
        opts,
      );
    });

    test(`typing a query, ${size} tests`, async ({ bench }) => {
      const c = catalog(n);
      const roots = buildTree(c.projects, c.map);
      const typing = ["o", "or", "ord", "orde", "order", "order v", "order va", "order val"];
      const expanded = (x: TreeNode) => x.kind !== "method";
      await bench.compare(
        bench("8 keystrokes, filterPass each (no cache)", () => typing.map((q) => filterPass(roots, q, "all"))),
        bench("8 keystrokes, createFilterCache (narrowing)", () => {
          const cache = createFilterCache();
          return typing.map((q) => cache(roots, q, "all"));
        }),
        bench("8 keystrokes, narrowing + flatten rows", () => {
          const cache = createFilterCache();
          return typing.map((q) => {
            const pass = cache(roots, q, "all");
            return flatten(roots, expanded, q, "all", false, { pass }).length;
          });
        }),
        { ...QUICK, iterations: n > 5_000 ? 5 : 10 },
      );
    });
  }
});
