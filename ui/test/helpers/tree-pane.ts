/**
 * Harness for <lc-tree-pane> tests: a small suite, the roots as a signal (like the shell passes them) and spy callbacks.
 * Every test file creates one store (newFailureKeys and friends are module-level computeds bound to the first store).
 */
import { vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system";
import type { TestNode, TestProject } from "../../src/contract";
import { getStore, testKey } from "../../src/store";
import { buildTree, regroup, type TreeGroup, type TreeNode } from "../../src/tree";
import { flatFailures, multiSelection, rowId, statusFilter, filterText, treeGroup, treeSort, type TreePane, type TreePaneProps } from "../../src/components/tree-pane";
import { mount, project, stateEvent, testNode } from "./app";

export const mapOf = (tests: TestNode[]) => new Map(tests.map((t) => [testKey(t), t]));

export function suiteTests(): TestNode[] {
  return [
    testNode({ id: "Ns.ClassA.Alpha", outcome: "passed", durationMs: 10, line: 10, filePath: "/src/A.cs", traits: [{ key: "Category", value: "Unit" }] }),
    testNode({ id: "Ns.ClassA.Beta", outcome: "failed", durationMs: 30, line: 20, filePath: "/src/A.cs", errorMessage: "boom" }),
    testNode({ id: "Ns.ClassB.Gamma", outcome: "skipped", traits: [{ key: "Owner", value: "web team" }] }),
    testNode({ id: "Ns.ClassB.Data#0", methodName: "Data", displayName: "Data (1)", outcome: "passed", durationMs: 5, filePath: "/src/B.cs", line: 7 }),
    testNode({ id: "Ns.ClassB.Data#1", methodName: "Data", displayName: "Data (2)", outcome: "passed", durationMs: 6, filePath: "/src/B.cs", line: 7 }),
  ];
}

/** Reset the tree's module-level state (filters, sort, group, multi-selection). */
export function resetTreeState() {
  statusFilter.set("all");
  filterText.set("");
  flatFailures.set(true);
  treeSort.set("default");
  treeGroup.set("namespace");
  multiSelection.set(new Set());
}

export interface MountOptions {
  tests?: TestNode[];
  projects?: TestProject[];
  props?: Partial<TreePaneProps>;
  selected?: string;
  quarantined?: string[];
  loaded?: boolean;
  group?: TreeGroup;
}

export function mountTree(opts: MountOptions = {}) {
  const projects = opts.projects ?? [project()];
  let tests = opts.tests ?? suiteTests();
  const q = new Set(opts.quarantined ?? []);
  getStore().apply(stateEvent({ projects, tests, quarantined: opts.quarantined }));
  const build = (ts: TestNode[], ps = projects, pending?: Set<string>) => {
    const r = buildTree(ps, mapOf(ts), pending, q);
    return opts.group ? regroup(r, opts.group) : r;
  };
  const roots = signal<TreeNode[]>(build(tests));
  const selected = signal<string | undefined>(opts.selected);
  const runDisabled = signal(false);
  const loaded = signal(opts.loaded ?? true);
  const calls = {
    onSelect: vi.fn((k: string) => selected.set(k)),
    onRun: vi.fn((_n: TreeNode) => {}),
    onCoverage: vi.fn((_n: TreeNode) => {}),
    onRediscover: vi.fn(),
    onRunTests: vi.fn((_t: TestNode[]) => {}),
    onCoverageTests: vi.fn((_t: TestNode[], _l: string) => {}),
  };
  let el!: TreePane;
  const dispose = root((d) => {
    el = h("lc-tree-pane", {
      roots: () => roots(),
      selectedKey: () => selected(),
      runDisabled: () => runDisabled(),
      loaded: () => loaded(),
      ...calls,
      ...opts.props,
    });
    return d;
  });
  mount(el);
  const scroller = el.querySelector<HTMLDivElement>(".tree-scroller")!;
  Object.defineProperty(scroller, "clientHeight", { configurable: true, value: 280 });
  return {
    el,
    roots,
    selected,
    runDisabled,
    loaded,
    calls,
    scroller,
    dispose,
    /** replace the tests (rebuilds the roots) */
    setTests(next: TestNode[], ps?: TestProject[], pending?: Set<string>) {
      tests = next;
      roots.set(build(next, ps ?? projects, pending));
    },
    rows: () => [...el.querySelectorAll<HTMLElement>("[role=treeitem]")],
    /** visible row names in DOM order (= list order) */
    labels: () => [...el.querySelectorAll<HTMLElement>("[role=treeitem]")].map((r) => r.dataset.name!.split(",")[0]),
    row: (key: string) => el.querySelector<HTMLElement>(`#${rowId(key)}`),
  };
}

export const menuItems = () => [...document.querySelectorAll<HTMLButtonElement>(".lc-menu-item")];
export const menuItem = (label: string) => menuItems().find((b) => b.dataset.text === label);
