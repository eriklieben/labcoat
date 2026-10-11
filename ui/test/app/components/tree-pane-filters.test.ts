import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { flatFailures, multiSelection, openTreeContextMenu, statusFilter, filterText, treeGroup, treeSort } from "../../../src/components/tree-pane";
import { click, flushFrame, key, makeStore, project, testNode, text, type } from "../../helpers/app";
import { clipboard, flushMicrotasks } from "../../helpers/dom";
import { menuItem, menuItems, mountTree, resetTreeState, suiteTests } from "../../helpers/tree-pane";

const { store, transport } = makeStore({ kind: "mock" });
let disposers: (() => void)[] = [];
const tree = (o: Parameters<typeof mountTree>[0] = {}) => {
  const t = mountTree(o);
  disposers.push(t.dispose);
  return t;
};
beforeEach(() => {
  resetTreeState();
  // popup exits are instant without motion (menus leave the DOM right away)
  document.documentElement.dataset.motion = "off";
});
afterEach(() => {
  disposers.forEach((d) => d());
  disposers = [];
});

const K = {
  p: "p:P1",
  a: "c:P1:Ns.ClassA",
  b: "c:P1:Ns.ClassB",
  alpha: "t:P1|Ns.ClassA.Alpha",
  beta: "t:P1|Ns.ClassA.Beta",
  gamma: "t:P1|Ns.ClassB.Gamma",
  data: "m:P1:Ns.ClassB.Data",
};

const chips = (el: Element) => [...el.querySelectorAll<HTMLButtonElement>(".chips .chip")];
const chipText = (el: Element) => chips(el).map((c) => text(c));

describe("status chips", () => {
  it("is a radio group with counts; zero-count chips are hidden", () => {
    const t = tree();
    const group = t.el.querySelector(".chips")!;
    expect(group.getAttribute("role")).toBe("radiogroup");
    expect(group.getAttribute("aria-label")).toBe("Status filter");
    expect(chipText(t.el)).toEqual(["All5", "Failed1", "Passed3", "Skipped1"]);
    const all = chips(t.el)[0];
    expect(all.getAttribute("aria-checked")).toBe("true");
    expect(all.tabIndex).toBe(0);
    expect(chips(t.el)[1].tabIndex).toBe(-1);
  });

  it("click and arrow keys select a chip and filter the rows", () => {
    const t = tree();
    click(chips(t.el)[1]);
    expect(statusFilter()).toBe("failed");
    // flat list by default for Failed
    expect(t.labels()).toEqual(["Beta"]);
    expect(t.row(K.beta)!.classList.contains("is-flat")).toBe(true);
    expect(text(t.row(K.beta)!.querySelector(".row-sublabel"))).toBe("ClassA");
    const group = t.el.querySelector(".chips")!;
    key(chips(t.el)[1], "ArrowRight");
    expect(statusFilter()).toBe("passed");
    expect(document.activeElement).toBe(chips(t.el)[2]);
    expect(t.labels()).toEqual(["Project.One", "Ns", "ClassA", "Alpha", "ClassB", "Data", "Data (1)", "Data (2)"]);
    key(chips(t.el)[2], "Home");
    expect(statusFilter()).toBe("all");
    expect(key(group, "x").defaultPrevented).toBe(false);
  });

  it("the flat-list toggle switches Failed between a list and the tree", () => {
    const t = tree();
    statusFilter.set("failed");
    const btn = t.el.querySelector<HTMLButtonElement>(".search-row button[aria-pressed]")!;
    expect(btn.getAttribute("aria-pressed")).toBe("true");
    expect(btn.getAttribute("aria-label")).toBe("Show as tree");
    click(btn);
    expect(flatFailures()).toBe(false);
    expect(t.labels()).toEqual(["Project.One", "Ns", "ClassA", "Beta"]);
    expect(t.row(K.a)!.dataset.name).toContain("1 of 2 tests match");
    expect(text(t.row(K.a)!.querySelector(".cnt-total"))).toBe("1 of 2");
    expect(localStorage.getItem("mste.flatFailures")).toBe("false");
    statusFilter.set("all");
    expect(t.el.querySelector(".search-row button[aria-pressed]")).toBeNull();
  });

  it("Flaky and New chips appear when they have tests; New uses the new-failure rule", async () => {
    const tests = [
      testNode({ id: "N.C.Flaky", outcome: "passed", flaky: true }),
      testNode({ id: "N.C.New", outcome: "failed", history: [{ outcome: "passed", at: "t0" }, { outcome: "failed", at: "t1" }] }),
      testNode({ id: "N.C.Old", outcome: "failed", history: [{ outcome: "failed", at: "t0" }, { outcome: "failed", at: "t1" }] }),
      testNode({ id: "N.C.Queued", outcome: "queued" }),
    ];
    const t = tree({ tests });
    await flushFrame();
    expect(chipText(t.el)).toEqual(["All4", "Failed2", "New1", "Passed1", "Not run1", "Flaky1"]);
    statusFilter.set("new");
    expect(t.labels()).toEqual(["New"]);
    statusFilter.set("flaky");
    expect(t.labels()).toEqual(["Flaky"]);
    statusFilter.set("notRun");
    expect(t.labels()).toEqual(["Project.One", "N", "C", "Queued"]);
    // is:new in the search text also uses the new-failure keys
    statusFilter.set("all");
    filterText.set("is:new");
    expect(t.labels()).toEqual(["Project.One", "N", "C", "New"]);
    expect(chipText(t.el)[0]).toBe("All1");
  });
});

describe("search box", () => {
  const input = (el: Element) => el.querySelector<HTMLInputElement>(".search-box input")!;

  it("filters while typing, clears with Escape or the clear button", () => {
    const t = tree();
    const i = input(t.el);
    expect(i.getAttribute("aria-label")).toBe("Filter tests");
    expect(i.getAttribute("aria-describedby")).toBe("tree-search-syntax");
    expect(t.el.querySelector(".search-kbd")).not.toBeNull();
    type(i, "gamma");
    expect(filterText()).toBe("gamma");
    expect(t.labels()).toEqual(["Project.One", "Ns", "ClassB", "Gamma"]);
    expect(t.row(K.b)!.dataset.name).toContain("1 of 3 tests match");
    const esc = key(i, "Escape");
    expect(esc.defaultPrevented).toBe(true);
    expect(filterText()).toBe("");
    expect(key(i, "Escape").defaultPrevented).toBe(false);
    type(i, "beta");
    click(t.el.querySelector(".search-clear")!);
    expect(filterText()).toBe("");
  });

  it("ArrowDown moves into the rows and selects the first one", () => {
    const t = tree();
    key(input(t.el), "ArrowDown");
    expect(document.activeElement).toBe(t.scroller);
    expect(t.selected()).toBe(K.p);
    // with a selection it only moves focus
    t.calls.onSelect.mockClear();
    key(input(t.el), "ArrowDown");
    expect(t.calls.onSelect).not.toHaveBeenCalled();
  });

  it("expansion while filtering is separate and reset when the filter changes", () => {
    const t = tree();
    type(input(t.el), "data");
    expect(t.labels()).toEqual(["Project.One", "Ns", "ClassB", "Data", "Data (1)", "Data (2)"]);
    t.selected.set(K.data);
    key(t.scroller, "ArrowLeft");
    expect(t.labels()).not.toContain("Data (1)");
    type(input(t.el), "dat");
    expect(t.labels()).toContain("Data (1)");
    filterText.set("");
    expect(t.labels()).not.toContain("Data (1)");
  });

  it("'/' focuses the search box unless typing somewhere", () => {
    const t = tree();
    const e = key(document.body, "/");
    expect(e.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(input(t.el));
    const other = document.createElement("input");
    document.body.append(other);
    other.focus();
    expect(key(other, "/").defaultPrevented).toBe(false);
    expect(key(document.body, "/", { ctrlKey: true }).defaultPrevented).toBe(false);
  });

  it("the syntax hint lists tokens on focus, values after is:/cat:/proj:, and inserts on click", () => {
    const t = tree();
    const i = input(t.el);
    i.dispatchEvent(new FocusEvent("focus"));
    const hint = () => t.el.querySelector(".search-hint");
    const items = () => [...t.el.querySelectorAll<HTMLButtonElement>(".search-hint-item")];
    expect(hint()!.getAttribute("aria-hidden")).toBe("true");
    expect(items().map((b) => text(b.querySelector("code")))).toEqual(["is:failed", "cat:Integration", "trait:Owner=web-team", "msg:timeout", "proj:Inventory"]);
    // mousedown inside the hint doesn't blur the input
    const md = new MouseEvent("mousedown", { bubbles: true, cancelable: true });
    hint()!.dispatchEvent(md);
    expect(md.defaultPrevented).toBe(true);
    click(items()[0]);
    expect(filterText()).toBe("is:");
    expect(items().map((b) => text(b))).toEqual(["is:failed", "is:passed", "is:skipped", "is:flaky", "is:new", "is:slow", "is:notrun", "is:snapshot"]);
    click(items()[0]);
    expect(filterText()).toBe("is:failed ");
    expect(i.value).toBe("is:failed ");
    expect(hint()).toBeNull();
    type(i, "cat:");
    expect(items().map((b) => text(b))).toEqual(["cat:Unit", "cat:web team"]);
    click(items()[1]);
    expect(filterText()).toBe('cat:"web team" ');
    type(i, "x proj:");
    expect(items().map((b) => text(b))).toEqual(["proj:Project.One"]);
    // a partial word is completed, an unrelated word gets the value appended
    type(i, "foo");
    expect(hint()).toBeNull();
    i.dispatchEvent(new FocusEvent("blur"));
    i.dispatchEvent(new FocusEvent("focus"));
    expect(hint()).toBeNull(); // not empty: no token list
    type(i, "");
    expect(hint()).toBeNull(); // typing disarms the token list until the next focus
  });
});

describe("sort and group menus, expand / collapse all", () => {
  it("the sort menu picks a duration sort and labels its button", async () => {
    const t = tree();
    const btn = t.el.querySelector<HTMLButtonElement>(".tree-sort button[aria-haspopup=menu]")!;
    expect(btn.getAttribute("aria-label")).toBe("Sort tests: default order");
    click(btn);
    expect(menuItems().map((b) => b.dataset.text)).toEqual(["Default order", "Slowest first", "Fastest first"]);
    expect(menuItem("Default order")!.getAttribute("aria-checked")).toBe("true");
    click(menuItem("Fastest first")!);
    expect(treeSort()).toBe("fastest");
    expect(btn.getAttribute("aria-label")).toBe("Sort tests: fastest first");
    expect(btn.parentElement!.classList.contains("active")).toBe(true);
    expect(localStorage.getItem("mste.treeSort")).toBe('"fastest"');
    expect(t.labels().slice(2, 5)).toEqual(["ClassB", "Data", "Gamma"]);
  });

  it("the group menu switches grouping and clears the multi-selection", () => {
    const t = tree();
    multiSelection.set(new Set([K.alpha, K.beta]));
    const btn = t.el.querySelectorAll<HTMLButtonElement>(".tree-sort button[aria-haspopup=menu]")[1];
    expect(btn.getAttribute("aria-label")).toBe("Group tests by namespace");
    click(btn);
    expect(document.querySelector(".lc-menu-heading")!.textContent).toBe("Group by");
    expect(menuItems().map((b) => b.dataset.text)).toEqual(["Namespace", "Project", "Trait", "Outcome"]);
    click(menuItem("Outcome")!);
    expect(treeGroup()).toBe("outcome");
    expect(btn.getAttribute("aria-label")).toBe("Group tests by outcome");
    expect(multiSelection().size).toBe(0);
  });

  it("expand all / collapse all", () => {
    const t = tree();
    click(t.el.querySelector("button[aria-label='Expand all']")!);
    expect(t.labels()).toContain("Data (2)");
    click(t.el.querySelector("button[aria-label='Collapse all']")!);
    expect(t.labels()).toEqual(["Project.One"]);
  });

  it("group rows (trait / outcome groups) render with their leaves' class as context", () => {
    const t = tree({ group: "outcome" });
    expect(t.labels()).toEqual(["Failed", "Beta", "Passed", "Alpha", "Data (1)", "Data (2)", "Skipped", "Gamma"]);
    expect(text(t.row(K.beta)!.querySelector(".row-sublabel"))).toBe("ClassA");
    // a group has no project: no restart in its menu
    t.el.querySelector(`[id='row-g_outcome_failed']`)!.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true }));
    expect(menuItem("Restart test host")).toBeUndefined();
    statusFilter.set("failed");
    expect(text(t.row(K.beta)!.querySelector(".row-sublabel"))).toBe("ClassA");
  });
});

describe("context menu", () => {
  const ctx = (row: HTMLElement, x = 10, y = 20) => {
    const e = new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: x, clientY: y });
    row.dispatchEvent(e);
    return e;
  };

  it("right click selects the row and opens its actions at the pointer", async () => {
    const debugged: string[] = [];
    const t = tree({ props: { onDebug: (n) => debugged.push(n.key) } });
    const e = ctx(t.row(K.alpha)!);
    expect(e.defaultPrevented).toBe(true);
    expect(t.selected()).toBe(K.alpha);
    const menu = document.querySelector(".lc-menu.tree-context-menu")!;
    expect(menu.querySelector("[role=menu]")!.getAttribute("aria-label")).toBe("Test actions");
    expect(menuItems().map((b) => b.dataset.text)).toEqual([
      "Run test",
      "Debug test",
      "Collect coverage",
      "Run 20×",
      "Open in editor",
      "Copy name",
      "Copy fully qualified name",
      "Copy link",
      "Quarantine",
      "Restart test host",
    ]);
    expect((t.el.querySelector(".tree-menu-anchor") as HTMLElement).style.left).toBe("10px");
    click(menuItem("Run test")!);
    expect(t.calls.onRun).toHaveBeenCalledTimes(1);
    expect(document.querySelector(".lc-menu.tree-context-menu")).toBeNull();
    await flushMicrotasks();
    expect(document.activeElement).toBe(t.scroller);

    ctx(t.row(K.alpha)!);
    click(menuItem("Debug test")!);
    expect(debugged).toEqual([K.alpha]);
    ctx(t.row(K.alpha)!);
    click(menuItem("Collect coverage")!);
    expect(t.calls.onCoverage).toHaveBeenCalledTimes(1);
    ctx(t.row(K.alpha)!);
    click(menuItem("Run 20×")!);
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Alpha"], repeat: 20 });
    ctx(t.row(K.alpha)!);
    click(menuItem("Quarantine")!);
    expect(transport.sent.at(-1)).toEqual({ type: "setQuarantine", projectId: "P1", id: "Ns.ClassA.Alpha", quarantined: true });
    ctx(t.row(K.alpha)!);
    click(menuItem("Restart test host")!);
    expect(transport.sent.at(-1)).toEqual({ type: "restartHost", projectId: "P1" });
    const logs = store.get().logs.length;
    ctx(t.row(K.alpha)!);
    click(menuItem("Open in editor")!);
    expect(store.get().logs).toHaveLength(logs + 1);
  });

  it("copy actions write names, fully qualified names and links", async () => {
    const t = tree();
    for (const [label, expected] of [
      ["Copy name", "Alpha"],
      ["Copy fully qualified name", "Ns.ClassA.Alpha"],
      ["Copy link", `${location.origin}${location.pathname}#/tests?t=P1|Ns.ClassA.Alpha`],
    ] as const) {
      ctx(t.row(K.alpha)!);
      click(menuItem(label)!);
      await flushMicrotasks();
      expect(clipboard.text).toBe(expected);
    }
    expect(document.querySelector(".lc-toast")?.textContent).toContain("Copied");
    ctx(t.row(K.p)!);
    expect(menuItem("Copy project path")).toBeDefined();
    click(menuItem("Copy project path")!);
    await flushMicrotasks();
    expect(clipboard.text).toBe("/src/Project.One/Project.One.csproj");
    clipboard.fail = true;
    ctx(t.row(K.a)!);
    click(menuItem("Copy name")!);
    await flushMicrotasks();
    expect(document.body.textContent).toContain("Couldn't copy the name");
  });

  it("group rows: run with count, no debug for classes, disabled while busy or blocked", () => {
    const t = tree({ props: { onDebug: () => {} }, quarantined: ["P1|Ns.ClassA.Beta"] });
    ctx(t.row(K.a)!);
    expect(menuItems().map((b) => b.dataset.text)).toEqual(["Run class (2)", "Collect coverage", "Open in editor", "Copy name", "Copy fully qualified name", "Copy link", "Restart test host"]);
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
    document.querySelector<HTMLElement>(".lc-popover")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    ctx(t.row(K.data)!);
    expect(menuItem("Debug test")).toBeDefined();
    document.querySelector<HTMLElement>(".lc-popover")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    ctx(t.row(K.beta)!);
    expect(menuItem("Release from quarantine")).toBeDefined();
    click(menuItem("Release from quarantine")!);
    expect(transport.sent.at(-1)).toMatchObject({ type: "setQuarantine", quarantined: false });
    t.runDisabled.set(true);
    ctx(t.row(K.gamma)!);
    expect(menuItem("Run test")!.disabled).toBe(true);
    expect(menuItem("Open in editor")!.disabled).toBe(true); // no source
    document.querySelector<HTMLElement>(".lc-popover")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    t.runDisabled.set(false);
    t.setTests(suiteTests(), [project({ status: "error" })]);
    ctx(t.row(K.p)!);
    expect(menuItem("Run project (5)")!.disabled).toBe(true);
  });

  it("a multi-selection gets its own menu", () => {
    const t = tree();
    click(t.row(K.alpha)!);
    click(t.row(K.gamma)!, { ctrlKey: true });
    ctx(t.row(K.gamma)!);
    expect(multiSelection().size).toBe(2);
    expect(document.querySelector(".lc-menu-heading")!.textContent).toBe("2 selected · 2 tests");
    expect(menuItems().map((b) => b.dataset.text)).toEqual(["Run 2 tests", "Collect coverage", "Clear selection"]);
    click(menuItem("Run 2 tests")!);
    expect(t.calls.onRunTests).toHaveBeenCalledTimes(1);
    ctx(t.row(K.gamma)!);
    click(menuItem("Collect coverage")!);
    expect(t.calls.onCoverageTests).toHaveBeenCalledWith(expect.any(Array), "2 selected");
    ctx(t.row(K.gamma)!);
    click(menuItem("Clear selection")!);
    expect(multiSelection().size).toBe(0);
    // right-click outside a multi-selection selects just that row
    click(t.row(K.alpha)!);
    click(t.row(K.gamma)!, { ctrlKey: true });
    ctx(t.row(K.beta)!);
    expect(multiSelection().size).toBe(0);
    expect(t.selected()).toBe(K.beta);
  });

  it("Shift+F10 / ContextMenu key and openTreeContextMenu open the menu for the selection", async () => {
    const t = tree();
    key(t.scroller, "F10", { shiftKey: true });
    await flushMicrotasks();
    expect(t.selected()).toBe(K.p);
    expect(menuItem("Run project (5)")).toBeDefined();
    document.querySelector<HTMLElement>(".lc-popover")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await flushMicrotasks();
    expect(menuItems()).toHaveLength(0);
    t.selected.set(K.alpha);
    key(t.scroller, "ContextMenu");
    await flushMicrotasks();
    expect(menuItem("Run test")).toBeDefined();
    document.querySelector<HTMLElement>(".lc-popover")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    openTreeContextMenu();
    expect(menuItem("Run test")).toBeDefined();
    document.querySelector<HTMLElement>(".lc-popover")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    t.selected.set(undefined);
    openTreeContextMenu();
    expect(menuItems()).toHaveLength(0);
  });
});
