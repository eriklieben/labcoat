/**
 * DOM building and patching with the design system's h() / list() / keyed() / show() (design-system/src/core/h.ts),
 * in happy-dom. happy-dom is slower than a browser at raw DOM work, so read these as relative numbers (before/after a
 * change), not as frame times; the Playwright checks (`npm run check:perf*`) measure the real thing.
 */
import { describe, test } from "vitest";
import { cx, h, keyed, list, root, show, signal, type Disposer } from "@eriklieben/labcoat-design-system/core";
import { fixed, QUICK } from "./fixtures";

interface Item {
  id: number;
  label: string;
  status: "passed" | "failed" | "skipped";
  ms: number;
}

const items = (n: number, from = 0): Item[] =>
  Array.from({ length: n }, (_, i) => ({ id: from + i, label: `Test_${from + i}_ReturnsExpected`, status: (from + i) % 97 === 0 ? "failed" : (from + i) % 53 === 0 ? "skipped" : "passed", ms: (from + i) % 40 }));

/** A tree-row-like element: a few classes, attributes, an icon span, text and a duration. */
function row(it: Item) {
  return h(
    "div",
    { class: cx("tree-row", it.status === "failed" && "failed"), role: "treeitem", "aria-level": 3, "data-key": String(it.id), tabIndex: -1 },
    h("span", { class: cx("status-icon", it.status) }),
    h("span", { class: "tree-label", title: it.label }, it.label),
    h("span", { class: "tree-duration" }, `${it.ms} ms`),
  );
}

/** The same row with reactive bindings (what list() rows look like). */
function reactiveRow(it: () => Item, selected: () => number) {
  return h(
    "div",
    { class: () => cx("tree-row", it().status === "failed" && "failed", selected() === it().id && "selected"), role: "treeitem", "aria-selected": () => selected() === it().id, tabIndex: -1 },
    h("span", { class: () => cx("status-icon", it().status) }),
    h("span", { class: "tree-label", title: () => it().label }, () => it().label),
    h("span", { class: "tree-duration" }, () => `${it().ms} ms`),
  );
}

/** Mounts a list of `n` reactive rows into a detached container; returns the source signal and a disposer. */
function mountList(n: number) {
  const src = signal(items(n));
  const selected = signal(-1);
  const host = document.createElement("div");
  let dispose!: Disposer;
  root((d) => {
    dispose = d;
    host.append(list(src, (it) => it.id, (it) => reactiveRow(it, selected)));
  });
  return { src, selected, host, dispose };
}

describe("h() building", () => {
  test("static rows", async ({ bench }) => {
    const data1k = items(1_000);
    const data20 = items(20);
    await bench.compare(
      bench("20 rows", () => {
        const el = h("div", null, data20.map(row));
        return el.childNodes.length;
      }),
      bench("1,000 rows", () => {
        const el = h("div", null, data1k.map(row));
        return el.childNodes.length;
      }),
      QUICK,
    );
  });

  test("reactive rows (bindings + owners)", async ({ bench }) => {
    const data = items(1_000);
    const selected = signal(-1);
    await bench.compare(
      bench("1,000 rows: build + dispose", () => {
        let dispose!: Disposer;
        root((d) => {
          dispose = d;
          h("div", null, data.map((it) => reactiveRow(() => it, selected)));
        });
        dispose();
      }),
      bench("1,000 static rows (reference)", () => h("div", null, data.map(row))),
      QUICK,
    );
  });
});

describe("h() patching", () => {
  test("attribute / class / text bindings", async ({ bench }) => {
    // 1,000 mounted reactive rows; one signal drives one binding per row (class), another a text binding per row.
    const N = 1_000;
    const tick = signal(0);
    const sel = signal(0);
    const host = document.createElement("div");
    root(() => {
      for (let i = 0; i < N; i++)
        host.append(
          h(
            "div",
            { class: () => cx("tree-row", sel() === i && "selected"), "aria-selected": () => sel() === i },
            h("span", null, () => `${tick()} ms`),
          ),
        );
    });
    let v = 0;
    await bench.compare(
      bench("selection move (2 of 1,000 rows change)", () => sel.set(++v % N)),
      bench("text tick (1,000 text bindings change)", () => tick.set(++v)),
      QUICK,
    );
  });

  test("regions: keyed / show / function child", async ({ bench }) => {
    const mode = signal<"a" | "b">("a");
    const on = signal(true);
    const n = signal(0);
    const host = document.createElement("div");
    root(() => {
      for (let i = 0; i < 200; i++)
        host.append(
          h(
            "div",
            null,
            keyed(mode, (m) => h("span", { class: m }, m === "a" ? "Alpha" : "Beta")),
            show(on, () => h("b", null, "on"), () => h("i", null, "off")),
            () => n(),
          ),
        );
    });
    let v = 0;
    await bench.compare(
      bench("keyed switch × 200", () => mode.set(mode.peek() === "a" ? "b" : "a")),
      bench("show flip × 200", () => on.set(!on.peek())),
      bench("text region × 200", () => n.set(++v)),
      QUICK,
    );
  });
});

// Sizes: 1k and 5k rows. happy-dom's nextSibling/insertBefore are O(siblings), so a 20k-row list in one parent measures
// happy-dom (a reverse takes seconds there, milliseconds in Chromium). The app never mounts 20k rows anyway: the tree
// is virtualized, and its 20k-test work (build, patch, flatten, filter) is benchmarked without DOM in tree.bench.ts.
describe("list() reconciliation", () => {
  test("initial render", async ({ bench }) => {
    const mk = (n: number) => () => mountList(n).dispose();
    await bench.compare(bench("1,000 rows", mk(1_000)), bench("5,000 rows", mk(5_000)), fixed(12));
  });

  test("updates of 1,000 rows (fresh list per sample)", async ({ bench }) => {
    const n = 1_000;
    let m: ReturnType<typeof mountList>;
    let next: Item[];
    const setup = (make: (b: Item[]) => Item[]) => ({
      beforeEach() {
        m = mountList(n);
        next = make(m.src.peek());
      },
      afterEach() {
        m.dispose();
      },
    });
    const set = () => m.src.set(next);
    await bench.compare(
      bench("append 100", setup((b) => [...b, ...items(100, n)]), set),
      bench("prepend 100", setup((b) => [...items(100, n), ...b]), set),
      bench("remove every 10th", setup((b) => b.filter((_, i) => i % 10 !== 0)), set),
      bench("swap 2 rows", setup((b) => swap(b)), set),
      bench("reverse", setup((b) => b.slice().reverse()), set),
      bench("update 1% (new item objects, same keys)", setup((b) => touch(b)), set),
      bench("replace all (new keys)", setup(() => items(n, n * 2)), set),
      bench("clear", setup(() => []), set),
      fixed(20),
    );
  });

  test("updates of 5,000 rows (toggling between two states)", async ({ bench }) => {
    // One mounted list per task, flipped between two arrays each sample (both directions cost about the same).
    const toggle = (make: (b: Item[]) => Item[]) => {
      const m = mountList(5_000);
      const a = m.src.peek();
      const b = make(a);
      let flip = false;
      return () => m.src.set((flip = !flip) ? b : a);
    };
    await bench.compare(
      bench("no-op (same items, new array)", toggle((b) => b.slice())),
      bench("update 1% ⇄", toggle(touch)),
      bench("append 100 ⇄ remove them", toggle((b) => [...b, ...items(100, 5_000)])),
      bench("swap 2 rows ⇄", toggle(swap)),
      bench("reverse ⇄", toggle((b) => b.slice().reverse())),
      { ...QUICK, iterations: 8 },
    );
  });
});

function swap(b: Item[]) {
  const c = b.slice();
  [c[1], c[c.length - 2]] = [c[c.length - 2], c[1]];
  return c;
}

function touch(b: Item[]) {
  return b.map((it, i) => (i % 100 === 0 ? { ...it, status: "failed" as const, ms: it.ms + 1 } : it));
}
