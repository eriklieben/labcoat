import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  clearTreeFilter,
  filterText,
  focusTree,
  fqnOf,
  lastTokenStart,
  multiSelection,
  nodeLink,
  prepareTreeLanding,
  rowId,
  selectionTests,
  sourceOf,
  statusFilter,
  treeFiltered,
} from "../../../src/components/tree-pane";
import { buildTree, findNode, requestStatusFilter } from "../../../src/tree";
import { setSetting } from "../../../src/settings";
import { click, flushFrame, key, makeStore, project, runSummary, testNode, text } from "../../helpers/app";
import { animations, flushMicrotasks, triggerResize } from "../../helpers/dom";
import { mapOf, mountTree, resetTreeState, suiteTests } from "../../helpers/tree-pane";

const { store } = makeStore({ kind: "mock" });
let disposers: (() => void)[] = [];
const tree = (o: Parameters<typeof mountTree>[0] = {}) => {
  const t = mountTree(o);
  disposers.push(t.dispose);
  return t;
};

beforeEach(() => {
  resetTreeState();
  setSetting("singleKeyShortcuts", true);
});
afterEach(() => {
  disposers.forEach((d) => d());
  disposers = [];
});

const K = {
  p: "p:P1",
  ns: "n:P1:Ns",
  a: "c:P1:Ns.ClassA",
  b: "c:P1:Ns.ClassB",
  alpha: "t:P1|Ns.ClassA.Alpha",
  beta: "t:P1|Ns.ClassA.Beta",
  gamma: "t:P1|Ns.ClassB.Gamma",
  data: "m:P1:Ns.ClassB.Data",
  d0: "t:P1|Ns.ClassB.Data#0",
};

describe("module helpers", () => {
  const roots = buildTree([project()], mapOf(suiteTests()));

  it("sourceOf: a test's file/line, a class's first file, a method's first data row line", () => {
    expect(sourceOf(findNode(roots, K.alpha)!)).toEqual({ path: "/src/A.cs", line: 10 });
    expect(sourceOf(findNode(roots, K.a)!)).toEqual({ path: "/src/A.cs", line: undefined });
    expect(sourceOf(findNode(roots, K.data)!)).toEqual({ path: "/src/B.cs", line: 7 });
    expect(sourceOf(findNode(roots, K.gamma)!)).toBeUndefined();
    expect(sourceOf(findNode(roots, K.p)!)).toBeUndefined();
  });

  it("nodeLink: t= for tests, n= for groups", () => {
    expect(nodeLink(K.alpha)).toBe(`${location.origin}${location.pathname}#/tests?t=P1|Ns.ClassA.Alpha`);
    expect(nodeLink(K.a)).toContain("#/tests?n=c%3AP1%3ANs.ClassA");
  });

  it("fqnOf: tests, classes, methods, projects, namespaces", () => {
    expect(fqnOf(findNode(roots, K.alpha)!)).toBe("Ns.ClassA.Alpha");
    expect(fqnOf(findNode(roots, K.a)!)).toBe("Ns.ClassA");
    expect(fqnOf(findNode(roots, K.data)!)).toBe("Ns.ClassB.Data");
    expect(fqnOf(findNode(roots, K.p)!)).toBe("/src/Project.One/Project.One.csproj");
    expect(fqnOf(findNode(roots, K.ns)!)).toBe("Ns");
    expect(fqnOf({ ...findNode(roots, K.p)!, project: undefined })).toBe("Project.One");
  });

  it("selectionTests: union of the selected nodes' leaves, deduplicated, tree order", () => {
    const ids = selectionTests(roots, new Set([K.b, K.d0, K.alpha])).map((t) => t.id);
    expect(ids).toEqual(["Ns.ClassA.Alpha", "Ns.ClassB.Data#0", "Ns.ClassB.Data#1", "Ns.ClassB.Gamma"]);
  });

  it("rowId escapes keys; clearTreeFilter resets text and status; treeFiltered follows", () => {
    expect(rowId("t:P1|A.B#0")).toBe("row-t_P1_A_B_0");
    filterText.set("x");
    statusFilter.set("failed");
    expect(treeFiltered()).toBe(true);
    clearTreeFilter();
    expect([filterText(), statusFilter(), treeFiltered()]).toEqual(["", "all", false]);
  });

  it("focusTree / prepareTreeLanding without a tree are no-ops", () => {
    expect(() => focusTree()).not.toThrow();
    expect(() => prepareTreeLanding("x")).not.toThrow();
  });
});

describe("<lc-tree-pane> rendering", () => {
  it("renders a labelled region with an ARIA tree of rows", () => {
    const t = tree();
    expect(t.el.classList.contains("tree-pane")).toBe(true);
    expect(t.el.getAttribute("role")).toBe("region");
    expect(t.el.getAttribute("aria-label")).toBe("Tests");
    expect(t.scroller.getAttribute("role")).toBe("tree");
    expect(t.scroller.getAttribute("aria-multiselectable")).toBe("true");
    // the data-driven method is collapsed by default
    expect(t.labels()).toEqual(["Project.One", "Ns", "ClassA", "Alpha", "Beta", "ClassB", "Data", "Gamma"]);
    const p = t.row(K.p)!;
    expect(p.getAttribute("aria-level")).toBe("1");
    expect(p.getAttribute("aria-expanded")).toBe("true");
    expect(p.getAttribute("aria-posinset")).toBe("1");
    expect(p.getAttribute("aria-setsize")).toBe("1");
    const beta = t.row(K.beta)!;
    expect(beta.getAttribute("aria-level")).toBe("4");
    expect(beta.getAttribute("aria-posinset")).toBe("2");
    expect(beta.getAttribute("aria-setsize")).toBe("2");
    expect(beta.hasAttribute("aria-expanded")).toBe(false);
    expect(beta.classList.contains("is-failed")).toBe(true);
    expect(t.row(K.data)!.getAttribute("aria-expanded")).toBe("false");
  });

  it("accessible names carry outcome, duration and counts", () => {
    const t = tree();
    expect(t.row(K.beta)!.dataset.name).toBe("Beta, failed, 30 ms");
    expect(text(t.row(K.beta)!.querySelector(".row-label .visually-hidden"))).toBe(", failed, 30 ms");
    expect(t.row(K.a)!.dataset.name).toBe("ClassA, 1 failed of 2, 40 ms");
    expect(t.row(K.b)!.dataset.name).toContain("2 passed, 1 skipped");
    expect(text(t.row(K.a)!.querySelector(".row-meta"))).toContain("1");
    expect(t.row(K.a)!.querySelector(".cnt-failed")!.textContent).toBe("1");
  });

  it("names: all passed, not run, running and flaky/pending/outdated/stale notes", () => {
    const t = tree({
      tests: [
        testNode({ id: "N.Pass.A", outcome: "passed" }),
        testNode({ id: "N.NotRun.A", outcome: "notRun" }),
        testNode({ id: "N.NotRun.B", outcome: "notRun" }),
        testNode({ id: "N.Run.A", outcome: "running" }),
        testNode({ id: "N.Run.B", outcome: "failed" }),
        testNode({ id: "N.Mixed.A", outcome: "passed" }),
        testNode({ id: "N.Mixed.B", outcome: "notRun" }),
        testNode({ id: "N.Flags.Flaky", outcome: "passed", flaky: true, durationMs: 3 }),
        testNode({ id: "N.Flags.Stale", outcome: "passed", stale: true }),
      ],
    });
    expect(t.row("c:P1:N.Pass")!.dataset.name).toBe("Pass, 1 test, all passed");
    expect(t.row("c:P1:N.NotRun")!.dataset.name).toBe("NotRun, 2 tests, not run");
    expect(t.row("c:P1:N.Run")!.dataset.name).toBe("Run, running, 1 of 2 done, 1 failed");
    expect(t.row("c:P1:N.Mixed")!.dataset.name).toBe("Mixed, 2 tests");
    expect(t.row("t:P1|N.Flags.Flaky")!.dataset.name).toBe("Flaky, passed, 3 ms, flaky");
    expect(t.row("t:P1|N.Flags.Flaky")!.querySelector(".row-pill.flaky")).not.toBeNull();
    expect(t.row("t:P1|N.Flags.Stale")!.dataset.name).toBe("Stale, passed, from a previous session");
    expect(t.row("t:P1|N.Flags.Stale")!.classList.contains("is-stale")).toBe(true);
    expect(text(t.row("c:P1:N.Run")!.querySelector(".cnt-total"))).toBe("1/2");
    // pending (previous result) + outdated
    t.setTests([testNode({ id: "N.C.A", outcome: "passed" })], [project({ resultsOutdated: true })], new Set(["P1|N.C.A"]));
    expect(t.row("t:P1|N.C.A")!.dataset.name).toBe("A, passed, previous result");
    expect(t.row("t:P1|N.C.A")!.classList.contains("is-pending")).toBe(true);
    t.setTests([testNode({ id: "N.C.A", outcome: "passed" })], [project({ resultsOutdated: true })]);
    expect(t.row("t:P1|N.C.A")!.dataset.name).toBe("A, passed, outdated");
  });

  it("project rows show building / error pills, host state and the build-error pill", async () => {
    const t = tree({ projects: [project({ status: "building", hostState: "warm" })] });
    expect(t.row(K.p)!.dataset.name).toContain("building");
    expect(text(t.row(K.p)!.querySelector(".row-pill.busy"))).toBe("Building");
    expect(t.row(K.p)!.querySelector(".host-state.warm")).not.toBeNull();
    t.setTests(suiteTests(), [project({ status: "discovering", hostState: "starting" })]);
    expect(text(t.row(K.p)!.querySelector(".row-pill.busy"))).toBe("Discovering");
    t.setTests(suiteTests(), [project({ status: "error", buildErrorCount: 2 })]);
    expect(t.row(K.p)!.dataset.name).toContain("build failed");
    expect(text(t.row(K.p)!.querySelector(".row-meta .row-pill.err"))).toBe("2 build errors");
    const pill = t.row(K.p)!.querySelector<HTMLButtonElement>(".row-actions .row-pill.err")!;
    expect(text(pill)).toBe("2 build errors");
    click(pill);
    t.setTests(suiteTests(), [project({ status: "error", buildErrors: [{ severity: "error", message: "x" }] })]);
    expect(text(t.row(K.p)!.querySelector(".row-actions .row-pill.err"))).toBe("1 build error");
    t.setTests(suiteTests(), [project({ status: "error" })]);
    expect(text(t.row(K.p)!.querySelector(".row-meta .row-pill.err"))).toBe("Build failed");
    // blocked: the project's run action is disabled, Enter on it does not run
    expect(t.row(K.p)!.querySelector<HTMLButtonElement>(".row-action.run")!.disabled).toBe(true);
    t.selected.set(K.p);
    key(t.scroller, "Enter");
    key(t.scroller, "c");
    t.row(K.p)!.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
    expect(t.calls.onRun).not.toHaveBeenCalled();
    expect(t.calls.onCoverage).not.toHaveBeenCalled();
  });

  it("shows loading, empty, diagnostics and filtered empty states", () => {
    const t = tree({ tests: [], projects: [], loaded: false });
    expect(text(t.scroller)).toContain("Loading tests…");
    t.loaded.set(true);
    // no test project at all: what Labcoat looks for, where, a link to troubleshooting, and Rediscover
    expect(text(t.scroller)).toContain("No test projects found");
    expect(text(t.scroller)).toContain("Labcoat looks for Microsoft.Testing.Platform test projects");
    expect(t.scroller.querySelector<HTMLAnchorElement>("a")!.href).toContain("docs/troubleshooting.md");
    click(t.scroller.querySelector<HTMLButtonElement>("button[title^='Look for test projects again']")!);
    expect(t.calls.onRediscover).toHaveBeenCalled();
    t.el.remove();

    const t2 = tree({ tests: [], projects: [project({ status: "idle" })] });
    // an idle project with no tests → no rows? the project row exists; filter it away to see the empty states
    expect(t2.rows()).toHaveLength(1);
    filterText.set("zzz");
    expect(text(t2.scroller)).toContain("No matching tests");
    click([...t2.scroller.querySelectorAll("button")].find((b) => b.textContent === "Clear filters")!);
    expect(filterText()).toBe("");
    expect(t2.rows()).toHaveLength(1);
  });

  it("VSTest projects: a pill on the project row, and the empty state lists them with the switch when there is nothing else", async () => {
    const vstest = (id: string, name: string) =>
      project({
        id,
        name,
        testPlatform: "vstest",
        diagnostics: [
          {
            level: "warning",
            message: `${name} uses VSTest (Microsoft.NET.Test.Sdk + NUnit3TestAdapter). Labcoat runs Microsoft.Testing.Platform projects.`,
            hint: "Set <EnableNUnitRunner>true</EnableNUnitRunner> and <OutputType>Exe</OutputType>, then Rediscover.",
          },
        ],
      });
    // next to an MTP project with tests: a row with a "VSTest" pill (the tooltip has the switch), no restart action
    const t = tree({ projects: [project(), vstest("P2", "Legacy.Tests")] });
    expect(t.scroller.getAttribute("role")).toBe("tree");
    const row = t.row("p:P2")!;
    const pill = row.querySelector<HTMLElement>(".row-pill.warn")!;
    expect(text(pill)).toBe("VSTest");
    expect(pill.title).toContain("Set <EnableNUnitRunner>true</EnableNUnitRunner>");
    expect(row.getAttribute("data-name")).toContain("uses VSTest, not run");
    expect(row.querySelector(".row-action.restart")).toBeNull();
    expect(t.row("p:P1")!.querySelector(".row-action.restart")).not.toBeNull();
    // filtered by status: the VSTest project stays discoverable
    statusFilter.set("failed");
    await flushFrame();
    expect(t.row("p:P2")).not.toBeNull();
    statusFilter.set("all");
    t.el.remove();

    // only VSTest projects: not "No test projects found", but the projects with their switch, the guide and Rediscover
    const t2 = tree({ tests: [], projects: [vstest("P1", "A.Tests"), vstest("P2", "B.Tests")] });
    expect(text(t2.scroller)).toContain("No Microsoft.Testing.Platform test projects");
    expect(t2.scroller.getAttribute("role")).toBe("region"); // no tree items: not a tree
    expect(text(t2.scroller)).toContain("Labcoat found 2 test projects");
    expect(text(t2.scroller)).not.toContain("No test projects found");
    const items = [...t2.scroller.querySelectorAll(".not-mtp-item")];
    expect(items.map((i) => text(i.querySelector(".not-mtp-message")))).toEqual([
      "A.Tests uses VSTest (Microsoft.NET.Test.Sdk + NUnit3TestAdapter). Labcoat runs Microsoft.Testing.Platform projects.",
      "B.Tests uses VSTest (Microsoft.NET.Test.Sdk + NUnit3TestAdapter). Labcoat runs Microsoft.Testing.Platform projects.",
    ]);
    expect(text(items[0].querySelector(".not-mtp-hint"))).toContain("EnableNUnitRunner");
    expect(t2.scroller.querySelector<HTMLAnchorElement>("a")!.href).toContain("troubleshooting.md#my-project-uses-vstest");
    click(t2.scroller.querySelector<HTMLButtonElement>("button[title^='Look for test projects again']")!);
    expect(t2.calls.onRediscover).toHaveBeenCalled();
    t2.el.remove();

    // one project without diagnostics yet: its name and what it is
    const t3 = tree({ tests: [], projects: [project({ name: "Solo", testPlatform: "none" })] });
    expect(text(t3.scroller)).toContain("Labcoat found a test project");
    expect(text(t3.scroller.querySelector(".not-mtp-message"))).toBe("Solo: not a test app, not run");
    expect(t3.scroller.querySelector(".not-mtp-hint")).toBeNull();
  });

  it("diagnostics instead of 'No tests discovered' when a project explains why", () => {
    const t = tree({ tests: [], projects: [] });
    store.apply({ type: "projectStatus", project: project({ status: "idle", diagnostics: [{ level: "error", message: "OutputType is Library" }] }) });
    t.loaded.set(false);
    t.loaded.set(true);
    expect(t.scroller.querySelector("lc-project-diagnostics")).not.toBeNull();
  });

  it("the label tooltip shows notes only (and the full name when cut off)", () => {
    const t = tree({ tests: [testNode({ id: "N.C.A", outcome: "passed", stale: true }), testNode({ id: "N.C.B", outcome: "passed" })] });
    const lblA = t.row("t:P1|N.C.A")!.querySelector<HTMLElement>(".row-label")!;
    lblA.dispatchEvent(new MouseEvent("mouseenter"));
    expect(lblA.title).toBe("Result from a previous session — run again to refresh");
    const lblB = t.row("t:P1|N.C.B")!.querySelector<HTMLElement>(".row-label")!;
    lblB.title = "old";
    lblB.dispatchEvent(new MouseEvent("mouseenter"));
    expect(lblB.hasAttribute("title")).toBe(false);
    Object.defineProperty(lblB, "scrollWidth", { configurable: true, value: 500 });
    lblB.dispatchEvent(new MouseEvent("mouseenter"));
    expect(lblB.title).toBe("N.C.B");
    const cls = t.row("c:P1:N.C")!.querySelector<HTMLElement>(".row-label")!;
    Object.defineProperty(cls, "scrollWidth", { configurable: true, value: 500 });
    cls.dispatchEvent(new MouseEvent("mouseenter"));
    expect(cls.title).toBe("C");
    t.setTests([testNode({ id: "N.C.A", outcome: "passed" })], [project({ resultsOutdated: true })]);
    const lbl = t.row("t:P1|N.C.A")!.querySelector<HTMLElement>(".row-label")!;
    lbl.dispatchEvent(new MouseEvent("mouseenter"));
    expect(lbl.title).toContain("Outdated: Project.One failed to build");
    t.setTests([testNode({ id: "N.C.A", outcome: "passed" })], [project()], new Set(["P1|N.C.A"]));
    lbl.dispatchEvent(new MouseEvent("mouseenter"));
    expect(lbl.title).toBe("Previous result — waiting for this run's result");
  });

  it("flashes a test row whose result changes passed ⇄ failed", () => {
    const t = tree();
    const row = t.row(K.alpha)!;
    const next = suiteTests().map((x) => (x.id === "Ns.ClassA.Alpha" ? { ...x, outcome: "failed" as const } : x));
    t.setTests(next);
    expect(row.classList.contains("lc-flash-bad")).toBe(true);
    const back = suiteTests().map((x) => (x.id === "Ns.ClassA.Beta" ? { ...x, outcome: "passed" as const } : x));
    t.setTests(back);
    expect(t.row(K.beta)!.classList.contains("lc-flash-good")).toBe(true);
    // to skipped / running: no flash
    const gamma = t.row(K.gamma)!;
    t.setTests(back.map((x) => (x.id === "Ns.ClassB.Gamma" ? { ...x, outcome: "running" as const } : x)));
    t.setTests(back.map((x) => (x.id === "Ns.ClassB.Gamma" ? { ...x, outcome: "failed" as const } : x)));
    expect(gamma.className).not.toContain("lc-flash");
  });
});

describe("<lc-tree-pane> selection and keyboard", () => {
  it("click selects; the selected row is aria-selected and the active descendant", () => {
    const t = tree();
    click(t.row(K.alpha)!);
    expect(t.calls.onSelect).toHaveBeenLastCalledWith(K.alpha);
    expect(t.row(K.alpha)!.getAttribute("aria-selected")).toBe("true");
    expect(t.row(K.alpha)!.classList.contains("selected")).toBe(true);
    expect(t.row(K.beta)!.getAttribute("aria-selected")).toBe("false");
    expect(t.scroller.getAttribute("aria-activedescendant")).toBe(rowId(K.alpha));
  });

  it("arrows, Home, End, PageUp/PageDown move the selection", () => {
    const t = tree();
    key(t.scroller, "ArrowDown");
    expect(t.selected()).toBe(K.p);
    key(t.scroller, "ArrowDown");
    expect(t.selected()).toBe(K.ns);
    key(t.scroller, "ArrowUp");
    expect(t.selected()).toBe(K.p);
    key(t.scroller, "End");
    expect(t.selected()).toBe(K.gamma);
    key(t.scroller, "Home");
    expect(t.selected()).toBe(K.p);
    key(t.scroller, "PageDown");
    expect(t.selected()).toBe(K.gamma);
    key(t.scroller, "PageUp");
    expect(t.selected()).toBe(K.p);
    t.selected.set(undefined);
    key(t.scroller, "ArrowUp");
    expect(t.selected()).toBe(K.p);
    const e = key(t.scroller, "x");
    expect(e.defaultPrevented).toBe(false);
  });

  it("→ expands then enters, ← collapses then goes to the parent, Space toggles", () => {
    const t = tree();
    t.selected.set(undefined);
    key(t.scroller, "ArrowRight");
    expect(t.selected()).toBe(K.p);
    key(t.scroller, "ArrowLeft"); // collapse the project
    expect(t.labels()).toEqual(["Project.One"]);
    key(t.scroller, "ArrowLeft"); // root: no parent, nothing
    expect(t.selected()).toBe(K.p);
    key(t.scroller, "ArrowRight"); // expand
    expect(t.labels()).toHaveLength(8);
    key(t.scroller, "ArrowRight"); // into the first child
    expect(t.selected()).toBe(K.ns);
    t.selected.set(K.data);
    key(t.scroller, "ArrowRight");
    expect(t.labels()).toContain("Data (1)");
    key(t.scroller, " ");
    expect(t.labels()).not.toContain("Data (1)");
    t.selected.set(K.alpha);
    key(t.scroller, "ArrowLeft");
    expect(t.selected()).toBe(K.a);
    key(t.scroller, "ArrowRight"); // expanded group → first child
    expect(t.selected()).toBe(K.alpha);
    key(t.scroller, "ArrowRight"); // a leaf: nothing
    expect(t.selected()).toBe(K.alpha);
    t.selected.set(undefined);
    expect(key(t.scroller, "ArrowLeft").defaultPrevented).toBe(false);
  });

  it("toggle animates with FLIP when motion is allowed, and instantly when off", () => {
    const t = tree();
    t.selected.set(K.data);
    animations.length = 0;
    key(t.scroller, " ");
    expect(t.labels()).toContain("Data (2)");
    document.documentElement.dataset.motion = "off";
    key(t.scroller, " ");
    expect(t.labels()).not.toContain("Data (2)");
  });

  it("Enter / r run, c collects coverage, o opens the editor (single-key shortcuts)", async () => {
    const t = tree();
    t.selected.set(K.alpha);
    key(t.scroller, "Enter");
    expect(t.calls.onRun).toHaveBeenCalledTimes(1);
    expect(t.calls.onRun.mock.calls[0][0].key).toBe(K.alpha);
    key(t.scroller, "r");
    expect(t.calls.onRun).toHaveBeenCalledTimes(2);
    key(t.scroller, "Enter", { ctrlKey: true });
    expect(t.calls.onRun).toHaveBeenCalledTimes(2);
    key(t.scroller, "c");
    expect(t.calls.onCoverage).toHaveBeenCalledTimes(1);
    expect(key(t.scroller, "c", { ctrlKey: true }).defaultPrevented).toBe(false);
    const before = store.get().logs.length;
    key(t.scroller, "o");
    expect(store.get().logs).toHaveLength(before + 1);
    expect(store.get().logs.at(-1)!.message).toContain("/src/A.cs:10");
    expect(key(t.scroller, "o", { metaKey: true }).defaultPrevented).toBe(false);
    setSetting("singleKeyShortcuts", false);
    key(t.scroller, "r");
    key(t.scroller, "c");
    key(t.scroller, "o");
    expect(t.calls.onRun).toHaveBeenCalledTimes(2);
    expect(t.calls.onCoverage).toHaveBeenCalledTimes(1);
    key(t.scroller, "Enter");
    expect(t.calls.onRun).toHaveBeenCalledTimes(3);
    t.runDisabled.set(true);
    key(t.scroller, "Enter");
    expect(t.calls.onRun).toHaveBeenCalledTimes(3);
  });

  it("does nothing on keys when there are no rows", () => {
    const t = tree({ tests: [], projects: [] });
    expect(key(t.scroller, "ArrowDown").defaultPrevented).toBe(false);
  });

  it("Ctrl/Cmd+click and Ctrl+Space build a multi-selection; Shift extends a range; Escape clears", () => {
    const t = tree();
    click(t.row(K.alpha)!);
    click(t.row(K.gamma)!, { ctrlKey: true });
    expect([...multiSelection()]).toEqual([K.alpha, K.gamma]);
    expect(t.row(K.alpha)!.classList.contains("multi")).toBe(true);
    expect(t.row(K.alpha)!.getAttribute("aria-selected")).toBe("true");
    click(t.row(K.gamma)!, { metaKey: true });
    expect([...multiSelection()]).toEqual([K.alpha]);
    // plain click clears
    click(t.row(K.beta)!);
    expect(multiSelection().size).toBe(0);
    click(t.row(K.data)!, { shiftKey: true });
    expect([...multiSelection()]).toEqual([K.beta, K.b, K.data]);
    // Shift+arrow extends from the anchor
    multiSelection.set(new Set());
    t.selected.set(K.alpha);
    key(t.scroller, "ArrowDown", { shiftKey: true });
    key(t.scroller, "ArrowDown", { shiftKey: true });
    expect([...multiSelection()]).toEqual([K.alpha, K.beta, K.b]);
    // Ctrl+Space toggles the current row
    key(t.scroller, " ", { ctrlKey: true });
    expect(multiSelection().has(K.b)).toBe(false);
    // Escape clears (and stops propagation); a second Escape is not handled
    const esc = key(t.scroller, "Escape");
    expect(esc.defaultPrevented).toBe(true);
    expect(multiSelection().size).toBe(0);
    expect(key(t.scroller, "Escape").defaultPrevented).toBe(false);
  });

  it("the selection bar runs / covers / clears the multi-selection; Enter runs it", () => {
    const t = tree();
    click(t.row(K.alpha)!);
    click(t.row(K.b)!, { ctrlKey: true });
    const bar = t.el.querySelector(".tree-selbar")!;
    expect(bar.getAttribute("role")).toBe("toolbar");
    expect(text(bar.querySelector(".selbar-count"))).toBe("2 selected");
    const [run, cov, clear] = [...bar.querySelectorAll("button")];
    expect(text(run)).toBe("Run 4");
    click(run);
    expect(t.calls.onRunTests.mock.calls[0][0].map((x) => x.id)).toEqual(["Ns.ClassA.Alpha", "Ns.ClassB.Data#0", "Ns.ClassB.Data#1", "Ns.ClassB.Gamma"]);
    click(cov);
    expect(t.calls.onCoverageTests).toHaveBeenCalledWith(expect.any(Array), "2 selected");
    key(t.scroller, "Enter");
    expect(t.calls.onRunTests).toHaveBeenCalledTimes(2);
    t.runDisabled.set(true);
    key(t.scroller, "Enter");
    expect(t.calls.onRunTests).toHaveBeenCalledTimes(2);
    click(clear);
    expect(t.el.querySelector(".tree-selbar")).toBeNull();
  });

  it("double-click runs a test, toggles a group, ignores the twisty and actions", () => {
    const t = tree();
    t.row(K.alpha)!.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
    expect(t.calls.onRun).toHaveBeenCalledTimes(1);
    t.row(K.data)!.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
    expect(t.labels()).toContain("Data (1)");
    t.row(K.alpha)!.querySelector(".row-actions")!.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
    expect(t.calls.onRun).toHaveBeenCalledTimes(1);
    t.runDisabled.set(true);
    t.row(K.alpha)!.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
    expect(t.calls.onRun).toHaveBeenCalledTimes(1);
  });

  it("the twisty toggles groups (and ignores leaves)", () => {
    const t = tree();
    click(t.row(K.data)!.querySelector(".twisty")!);
    expect(t.labels()).toContain("Data (1)");
    expect(t.row(K.data)!.querySelector(".twisty")!.classList.contains("open")).toBe(true);
    click(t.row(K.alpha)!.querySelector(".twisty")!);
    expect(t.calls.onSelect).toHaveBeenCalledWith(K.alpha);
  });

  it("row action buttons run, cover, open in editor and restart the host", () => {
    const t = tree();
    const r = t.row(K.alpha)!;
    click(r.querySelector(".row-action.run")!);
    expect(t.calls.onRun).toHaveBeenCalledTimes(1);
    click(r.querySelector(".row-action.cov")!);
    expect(t.calls.onCoverage).toHaveBeenCalledTimes(1);
    // actions don't select the row
    expect(t.calls.onSelect).not.toHaveBeenCalled();
    const before = store.get().logs.length;
    click(r.querySelector(".row-action[aria-label='Open in editor']")!);
    expect(store.get().logs).toHaveLength(before + 1);
    expect(t.row(K.gamma)!.querySelector(".row-action[aria-label='Open in editor']")).toBeNull();
    click(t.row(K.p)!.querySelector(".row-action.restart")!);
    expect((store.transport as unknown as { sent: unknown[] }).sent.at(-1)).toEqual({ type: "restartHost", projectId: "P1" });
    t.runDisabled.set(true);
    expect(t.row(K.p)!.querySelector<HTMLButtonElement>(".row-action.restart")!.title).toBe("A run is in progress");
    expect(r.querySelector<HTMLButtonElement>(".row-action.run")!.title).toBe("A run is in progress");
  });

  it("reveals a selected node inside collapsed groups and scrolls it into view", () => {
    const t = tree();
    t.selected.set(K.d0);
    expect(t.row(K.d0)).not.toBeNull();
    expect(t.row(K.data)!.getAttribute("aria-expanded")).toBe("true");
    // a deep link restored before the tree has the node: revealed once it appears
    t.selected.set("t:P1|Later.C.T");
    t.setTests([...suiteTests(), testNode({ id: "Later.C.T" })]);
    expect(t.row("t:P1|Later.C.T")).not.toBeNull();
  });

  it("virtualizes: only the window (+ overscan) is rendered, scrolling moves it, the selected row stays pinned", () => {
    const tests = Array.from({ length: 120 }, (_, i) => testNode({ id: `N.C.T${String(i).padStart(3, "0")}`, outcome: "passed" }));
    const t = tree({ tests });
    triggerResize(t.scroller); // viewH := 280
    expect(t.rows()).toHaveLength(Math.ceil(280 / 28) + 8);
    t.selected.set(`t:P1|N.C.T000`);
    t.scroller.scrollTop = 28 * 60;
    t.scroller.dispatchEvent(new Event("scroll"));
    const names = t.labels();
    expect(names).toContain("T060");
    expect(names).not.toContain("T010");
    // pinned selected row (aria-activedescendant never dangles)
    expect(t.row("t:P1|N.C.T000")).not.toBeNull();
    // keyboard End scrolls to the bottom
    key(t.scroller, "End");
    expect(t.selected()).toBe("t:P1|N.C.T119");
    expect(t.scroller.scrollTop).toBe(123 * 28 - 280);
    key(t.scroller, "Home");
    expect(t.scroller.scrollTop).toBe(0);
  });

  it("focusTree focuses the rows; prepareTreeLanding rings the landed row", async () => {
    vi.useFakeTimers();
    const tests = Array.from({ length: 60 }, (_, i) => testNode({ id: `N.C.T${String(i).padStart(2, "0")}`, outcome: "passed" }));
    const t = tree({ tests });
    focusTree();
    expect(document.activeElement).toBe(t.scroller);
    prepareTreeLanding("t:P1|N.C.T50");
    t.selected.set("t:P1|N.C.T50");
    expect(t.scroller.scrollTop).toBeGreaterThan(0);
    vi.advanceTimersByTime(300);
    vi.advanceTimersToNextFrame();
    expect(t.row("t:P1|N.C.T50")!.classList.contains("lc-locator")).toBe(true);
    // already selected: lands right away (after a microtask)
    t.scroller.scrollTop = 0;
    prepareTreeLanding("t:P1|N.C.T50");
    await flushMicrotasks();
    vi.advanceTimersByTime(300);
    vi.advanceTimersToNextFrame();
    expect(t.scroller.scrollTop).toBeGreaterThan(0);
    // a visible row lands without scrolling
    multiSelection.set(new Set(["x", "y"]));
    t.selected.set("t:P1|N.C.T01");
    t.scroller.scrollTop = 0;
    prepareTreeLanding("t:P1|N.C.T02");
    expect(multiSelection().size).toBe(0);
    t.selected.set("t:P1|N.C.T02");
    vi.advanceTimersByTime(10);
    vi.advanceTimersToNextFrame();
    expect(t.scroller.scrollTop).toBe(0);
    expect(t.row("t:P1|N.C.T02")!.classList.contains("lc-locator")).toBe(true);
  });

  it("follows status filter requests from elsewhere and saves its prefs", () => {
    tree();
    requestStatusFilter("failed");
    expect(statusFilter()).toBe("failed");
    expect(localStorage.getItem("mste.statusFilter")).toBe('"failed"');
  });

  it("duration sort: ranks are frozen while a run is in flight", async () => {
    const t = tree();
    const { treeSort } = await import("../../../src/components/tree-pane");
    treeSort.set("slowest");
    expect(t.labels().slice(2, 5)).toEqual(["ClassA", "Beta", "Alpha"]);
    store.apply({ type: "runStarted", run: runSummary({ runId: "rx" }), testIds: [] });
    await flushFrame();
    t.setTests(suiteTests().map((x) => (x.id === "Ns.ClassA.Alpha" ? { ...x, durationMs: 999 } : x)));
    expect(t.labels().slice(2, 5)).toEqual(["ClassA", "Beta", "Alpha"]);
    store.apply({ type: "runFinished", run: runSummary({ runId: "rx" }) });
    await flushFrame();
    expect(t.labels().slice(2, 5)).toEqual(["ClassA", "Alpha", "Beta"]);
  });
});

describe("lastTokenStart", () => {
  it("finds where the trailing non-whitespace run starts (the text's length after trailing whitespace)", () => {
    expect(lastTokenStart("")).toBe(0);
    expect(lastTokenStart("is:")).toBe(0);
    expect(lastTokenStart("a is:")).toBe(2);
    expect(lastTokenStart("a b ")).toBe(4);
    expect(lastTokenStart("a\tcat:")).toBe(2);
    expect(lastTokenStart("x\u00a0proj:")).toBe(2);
    expect(lastTokenStart("a\u2028b")).toBe(2);
    expect(lastTokenStart("  ")).toBe(2);
  });
});
