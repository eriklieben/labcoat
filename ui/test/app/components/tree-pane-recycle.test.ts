/**
 * <lc-tree-pane> row recycling (renderRows): rows leaving the window hand their element to rows entering it. Every
 * attribute the accessibility tree and the checks read must follow the new row, and nothing of the old row may stay.
 */
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { TestNode } from "../../../src/contract";
import { rowId } from "../../../src/components/tree-pane";
import type { Row } from "../../../src/tree";
import { makeStore, testNode } from "../../helpers/app";
import { triggerResize } from "../../helpers/dom";
import { mountTree, resetTreeState } from "../../helpers/tree-pane";

makeStore({ kind: "mock" });
let disposers: (() => void)[] = [];
const tree = (o: Parameters<typeof mountTree>[0] = {}) => {
  const t = mountTree(o);
  disposers.push(t.dispose);
  return t;
};
beforeEach(() => resetTreeState());
afterEach(() => {
  disposers.forEach((d) => d());
  disposers = [];
});

/** classes N.C0 … with 6 tests each; every 4th test failed, every 3rd has a duration */
function many(classes: number): TestNode[] {
  const out: TestNode[] = [];
  for (let c = 0; c < classes; c++)
    for (let i = 0; i < 6; i++)
      out.push(testNode({ id: `N.C${String(c).padStart(2, "0")}.T${i}`, outcome: i % 4 === 0 ? "failed" : "passed", durationMs: i % 3 === 0 ? 10 + i : undefined, flaky: i === 5 || undefined }));
  return out;
}

/** the pane's private window state (the same fields scripts/lib/tree-invariants.mjs reads) */
const pane = (t: ReturnType<typeof tree>) => t.el as unknown as { rows: () => Row[]; start: () => number; end: () => number; setText: (v: string) => void };

/** each rendered element shows exactly its row: id, ARIA attributes, name, label, status, classes */
function expectRowsMatch(t: ReturnType<typeof tree>) {
  const p = pane(t);
  const rows = p.rows();
  const want = rows.slice(p.start(), p.end());
  const els = t.rows();
  expect(els.map((e) => e.id)).toEqual(want.map((r) => rowId(r.node.key)));
  for (const [i, el] of els.entries()) {
    const r = want[i];
    expect(el.getAttribute("aria-level")).toBe(String(r.depth + 1));
    expect(el.getAttribute("aria-posinset")).toBe(String(r.pos));
    expect(el.getAttribute("aria-setsize")).toBe(String(r.size));
    expect(el.getAttribute("aria-expanded")).toBe(r.hasChildren ? String(r.expanded) : null);
    expect(el.style.translate).toBe(`0 ${r.index * 28}px`);
    expect(el.classList.contains(`kind-${r.node.kind}`)).toBe(true);
    expect(el.className.match(/kind-/g)).toHaveLength(1);
    expect(el.dataset.name!.startsWith(r.node.test ? r.node.test.displayName : r.node.label)).toBe(true);
    expect(el.querySelector(".row-label")!.textContent!.startsWith(r.node.label)).toBe(true);
    expect(!!el.querySelector(".twisty .lc-caret")).toBe(r.hasChildren);
    expect(!!el.querySelector(".row-counts")).toBe(!r.node.test && r.node.counts.total > 0);
    expect(!!el.querySelector(".row-pill.flaky")).toBe(!!r.node.test?.flaky);
  }
}

describe("<lc-tree-pane> row recycling", () => {
  it("scrolling reuses the elements of rows that left, patched to the rows that entered", () => {
    const t = tree({ tests: many(40) });
    triggerResize(t.scroller);
    expectRowsMatch(t);
    for (let step = 1; step <= 30; step++) {
      const before = new Set(t.rows());
      t.scroller.scrollTop = step * 3 * 28;
      t.scroller.dispatchEvent(new Event("scroll"));
      const after = t.rows();
      // same number of rows in the window: no element created, none disposed
      if (after.length === before.size) expect(after.every((e) => before.has(e))).toBe(true);
      expectRowsMatch(t);
    }
    // a jump far down and back
    t.scroller.scrollTop = 200 * 28;
    t.scroller.dispatchEvent(new Event("scroll"));
    expectRowsMatch(t);
    t.scroller.scrollTop = 0;
    t.scroller.dispatchEvent(new Event("scroll"));
    expectRowsMatch(t);
  });

  it("a recycled element drops the old row's state: no status pop, no flash, no tooltip; the selection follows the key", () => {
    const t = tree({ tests: many(40) });
    triggerResize(t.scroller);
    const first = t.rows()[3];
    first.classList.add("lc-flash-bad");
    first.querySelector<HTMLElement>(".row-label")!.title = "old";
    const key = first.id;
    t.selected.set(`t:P1|N.C00.T2`);
    expect(t.row("t:P1|N.C00.T2")!.getAttribute("aria-selected")).toBe("true");
    t.scroller.scrollTop = 30 * 28;
    t.scroller.dispatchEvent(new Event("scroll"));
    expect(first.id).not.toBe(key);
    expect(first.isConnected).toBe(true);
    expect(first.classList.contains("lc-flash-bad")).toBe(false);
    expect(first.querySelector(".row-label")!.hasAttribute("title")).toBe(false);
    expect(t.el.querySelector(".st-anim")).toBeNull();
    // only the (pinned) selected row is selected
    expect(t.rows().filter((e) => e.getAttribute("aria-selected") === "true").map((e) => e.id)).toEqual([rowId("t:P1|N.C00.T2")]);
    expectRowsMatch2Pinned(t);
  });

  it("a status change of the same row still pops and flashes", () => {
    const tests = many(4);
    const t = tree({ tests });
    triggerResize(t.scroller);
    const row = t.row("t:P1|N.C00.T1")!;
    t.setTests(tests.map((x) => (x.id === "N.C00.T1" ? { ...x, outcome: "failed" as const } : x)));
    expect(row.querySelector(".st-anim")).not.toBeNull();
    expect(row.classList.contains("lc-flash-bad")).toBe(true);
  });

  it("shrinking the window disposes the leftover elements", () => {
    const t = tree({ tests: many(40) });
    triggerResize(t.scroller);
    const n = t.rows().length;
    pane(t).setText("N.C03.T1");
    expect(t.rows().length).toBeLessThan(n);
    expectRowsMatch(t);
    pane(t).setText("");
    expect(t.rows()).toHaveLength(n);
    expectRowsMatch(t);
  });
});

/** the window plus the pinned selected row at the end */
function expectRowsMatch2Pinned(t: ReturnType<typeof tree>) {
  const p = pane(t);
  const rows = p.rows();
  const want = rows.slice(p.start(), p.end());
  const si = rows.findIndex((r) => r.node.key === t.selected());
  if (si >= 0 && (si < p.start() || si >= p.end())) want.push(rows[si]);
  expect(t.rows().map((e) => e.id)).toEqual(want.map((r) => rowId(r.node.key)));
  const pinned = t.rows().at(-1)!;
  expect(pinned.style.translate).toBe(`0 ${want.at(-1)!.index * 28}px`);
}
