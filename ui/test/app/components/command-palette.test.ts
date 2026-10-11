import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h, root } from "@eriklieben/labcoat-design-system";
import { commandPalette, fuzzy, paletteOpen, type PaletteActions } from "../../../src/components/command-palette";
import { clearThemeOverrides, THEME_FAMILIES, themeFamily, themePref } from "../../../src/theme";
import { setUiDefaults } from "../../../src/ui-defaults";
import type { ExplorerState } from "../../../src/contract";
import { flushFrame, key, makeStore, runSummary, testNode, text, type } from "../../helpers/app";
import { flushMicrotasks } from "../../helpers/dom";

describe("fuzzy()", () => {
  it("empty query matches everything with score 0", () => {
    expect(fuzzy("Anything", "  ")).toEqual({ score: 0, hits: [] });
  });
  it("a contiguous substring scores highest, a word start best", () => {
    const start = fuzzy("Run all tests", "all")!;
    const mid = fuzzy("Overall", "all")!;
    expect(start.hits).toEqual([4, 5, 6]);
    expect(start.score).toBeGreaterThan(mid.score);
    expect(mid.score).toBeGreaterThan(3000 - 10);
  });
  it("word-start subsequences beat compact ones, scattered ones are rejected", () => {
    const words = fuzzy("PlaceOrder_ReturnsCode", "prc")!;
    expect(words.hits).toEqual([0, 11, 18]);
    expect(words.score).toBeGreaterThan(1500);
    const compact = fuzzy("abxcd", "acd")!;
    expect(compact.score).toBeLessThan(1500);
    expect(compact.hits).toEqual([0, 3, 4]);
    expect(fuzzy("abcdefghijklmnop", "aop")).toBeUndefined();
    expect(fuzzy("abc", "abd")).toBeUndefined();
  });
  it("ignores case and spaces in the query", () => {
    expect(fuzzy("Toggle Output", "TOGGLE out")?.hits.length).toBe(9);
  });
});

function actions(over: Partial<PaletteActions> = {}): PaletteActions {
  return {
    runAll: vi.fn(),
    runFailed: vi.fn(),
    runSelected: undefined,
    selectedLabel: undefined,
    coverageSelected: undefined,
    toggleWatch: vi.fn(),
    toggleCoverage: vi.fn(),
    buildImpactMap: vi.fn(),
    cancel: vi.fn(),
    rediscover: vi.fn(),
    selectTest: vi.fn(),
    viewRun: vi.fn(),
    page: vi.fn(),
    toggleOutput: vi.fn(),
    showShortcuts: vi.fn(),
    sort: vi.fn(),
    sortLabels: { default: "Default order", slowest: "Slowest first", fastest: "Fastest first" },
    currentSort: "default",
    ...over,
  };
}

const TESTS = [
  testNode({ id: "Shop.Orders.PlaceOrder_ReturnsCode", outcome: "passed" }),
  testNode({ id: "Shop.Orders.CancelOrder_Refunds", outcome: "failed" }),
  testNode({ id: "Shop.Cart.AddItem_IncreasesTotal", outcome: "notRun" }),
];

let dispose: (() => void) | undefined;
async function setup(opts: { a?: Partial<PaletteActions>; state?: Partial<ExplorerState>; open?: boolean } = {}) {
  const ctx = makeStore({ kind: "ws", open: opts.open, state: { tests: TESTS, ...opts.state } });
  await flushFrame();
  const a = actions(opts.a);
  root((d) => {
    dispose = d;
    document.body.append(h("div", null, commandPalette(() => a)));
  });
  paletteOpen.set(true);
  // the dialog's content is rendered per opening: always query the current one
  const dlgNow = () => document.querySelector<HTMLDialogElement>("dialog.palette-dialog")!;
  const options = () => [...dlgNow().querySelectorAll<HTMLElement>("[role=option]")];
  const labels = () => options().map((o) => text(o.querySelector(".palette-label")));
  const find = (label: string) => options().find((o) => text(o.querySelector(".palette-label")) === label)!;
  const activeLabel = () => text(dlgNow().querySelector("[role=option].active .palette-label"));
  return {
    ...ctx,
    a,
    get dlg() {
      return dlgNow();
    },
    get input() {
      return dlgNow().querySelector<HTMLInputElement>(".palette-input")!;
    },
    options,
    labels,
    find,
    activeLabel,
  };
}

beforeEach(() => {
  paletteOpen.set(false);
  setUiDefaults({});
  clearThemeOverrides();
});
afterEach(() => {
  paletteOpen.set(false);
  dispose?.();
  dispose = undefined;
});

describe("commandPalette()", () => {
  it("opens as a combobox + listbox with the command list", async () => {
    const { dlg, input, labels, options } = await setup();
    expect(dlg.open).toBe(true);
    expect(dlg.getAttribute("aria-label")).toBe("Command palette");
    expect(input.getAttribute("role")).toBe("combobox");
    expect(input.getAttribute("aria-controls")).toBe("palette-list");
    expect(input.placeholder).toBe("Type a command or a test name…");
    expect(dlg.querySelector("#palette-list")!.getAttribute("role")).toBe("listbox");
    expect(labels()).toContain("Run all tests");
    expect(labels()).toContain("Run failed (1)");
    expect(labels()).toContain("Switch theme…");
    expect(labels()).toEqual(expect.arrayContaining(["Run affected: uncommitted changes", "Run affected: branch vs main", "Preview affected tests…"]));
    expect(labels()).not.toContain("Run selected");
    expect(labels()).not.toContain("Back to live results");
    // first option is active
    expect(options()[0].getAttribute("aria-selected")).toBe("true");
    expect(input.getAttribute("aria-activedescendant")).toBe("palette-opt-0");
    // shortcuts are shown
    expect(options()[0].querySelector(".palette-kbd")).toBeTruthy();
  });

  it("disables run commands while disconnected and Stop when nothing runs", async () => {
    const { find } = await setup({ open: false });
    expect(find("Run all tests").getAttribute("aria-disabled")).toBe("true");
    expect(find("Run all tests").classList.contains("disabled")).toBe(true);
    expect(find("Stop the current run").getAttribute("aria-disabled")).toBe("true");
    expect(text(find("Stop the current run").querySelector(".palette-detail"))).toBe("nothing is running");
    expect(find("View run…").getAttribute("aria-disabled")).toBe("true");
  });

  it("offers the optional commands when the shell provides them", async () => {
    const opt = {
      runSelected: vi.fn(),
      selectedLabel: "CancelOrder_Refunds",
      coverageSelected: vi.fn(),
      runMatching: { count: 2, run: vi.fn() },
      debugSelected: vi.fn(),
      restartHost: vi.fn(),
      showBuildErrors: vi.fn(),
      compareRuns: vi.fn(),
      copyLink: vi.fn(),
    };
    const { labels, find, store } = await setup({ a: opt, state: { runs: [runSummary()], coverageEnabled: true, watch: true } });
    for (const l of ["Run 2 matching", "Run selected", "Collect coverage for selected", "Debug test", "Restart test host", "Show build errors", "Compare runs…", "Copy link"])
      expect(labels()).toContain(l);
    expect(labels()).toContain("Turn watch mode off");
    expect(labels()).toContain("Stop collecting coverage on runs");
    expect(text(find("Run selected").querySelector(".palette-detail"))).toBe("CancelOrder_Refunds");
    expect(text(find("View run…").querySelector(".palette-detail"))).toBe("1 stored");
    void store;
  });

  it("copies the diagnostics report for a bug report", async () => {
    const fetch = vi.fn(async () => ({ ok: true, status: 200, json: async () => ({ markdown: "### Labcoat diagnostics" }) }));
    vi.stubGlobal("fetch", fetch);
    const { find } = await setup();
    find("Copy diagnostics").click();
    await flushMicrotasks();
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/diagnostics");
    vi.unstubAllGlobals();
  });

  it("Back to live is offered while viewing a past run", async () => {
    const p = await setup({ state: { runs: [runSummary()] } });
    p.transport.fetchRun.mockResolvedValue(null);
    p.store.viewRun("r1");
    paletteOpen.set(false);
    paletteOpen.set(true);
    type(p.input, "live");
    expect(p.find("Back to live results")).toBeTruthy();
    key(p.input, "Enter");
    await flushMicrotasks();
    expect(p.a.viewRun).toHaveBeenCalledWith(undefined);
  });

  it("typing ranks commands (with highlighted matches) and tests together", async () => {
    const { input, labels, options, dlg } = await setup();
    type(input, "settings");
    expect(labels()[0]).toBe("Open settings");
    expect(text(options()[0].querySelector("mark"))).toBe("settings");
    type(input, "cancelorder");
    expect(labels()).toContain("CancelOrder_Refunds");
    // a test row shows its status and its namespace.class
    const row = options().find((o) => text(o.querySelector(".palette-label")) === "CancelOrder_Refunds")!;
    expect(row.querySelector(".palette-status")).toBeTruthy();
    expect(text(row.querySelector(".palette-detail"))).toBe("Shop.Orders");
    // a single letter does not search tests
    type(input, "c");
    expect(labels()).not.toContain("CancelOrder_Refunds");
    type(input, "zzzzqqq");
    expect(options()).toHaveLength(0);
    expect(text(dlg.querySelector(".palette-empty"))).toBe("Nothing matches.");
    expect(input.hasAttribute("aria-activedescendant")).toBe(false);
  });

  it("Enter runs the active command after closing", async () => {
    const { a, input } = await setup();
    type(input, "Toggle output");
    const e = key(input, "Enter");
    expect(e.defaultPrevented).toBe(true);
    expect(paletteOpen()).toBe(false);
    expect(a.toggleOutput).not.toHaveBeenCalled();
    await flushMicrotasks();
    expect(a.toggleOutput).toHaveBeenCalledTimes(1);
  });

  it("page, sort and shortcut commands call their actions", async () => {
    const { a, find } = await setup();
    find("Open coverage report").click();
    await flushMicrotasks();
    expect(a.page).toHaveBeenCalledWith("coverage");
    paletteOpen.set(true);
    find("Sort: slowest first").click();
    await flushMicrotasks();
    expect(a.sort).toHaveBeenCalledWith("slowest");
    paletteOpen.set(true);
    expect(text(find("Sort: default order").querySelector(".palette-detail"))).toBe("current");
    find("Shortcuts").click();
    await flushMicrotasks();
    expect(a.showShortcuts).toHaveBeenCalled();
  });

  it("a disabled command does nothing", async () => {
    const { a, find } = await setup({ open: false });
    find("Run all tests").click();
    await flushMicrotasks();
    expect(paletteOpen()).toBe(true);
    expect(a.runAll).not.toHaveBeenCalled();
  });

  it("keyboard: arrows wrap, PageUp/Down jump, Ctrl+Home/End, mouse hover moves", async () => {
    const { input, options, activeLabel, labels } = await setup();
    const n = options().length;
    key(input, "ArrowUp");
    expect(activeLabel()).toBe(labels()[n - 1]);
    key(input, "ArrowDown");
    expect(activeLabel()).toBe(labels()[0]);
    key(input, "ArrowDown");
    expect(activeLabel()).toBe(labels()[1]);
    key(input, "PageDown");
    expect(activeLabel()).toBe(labels()[9]);
    key(input, "PageUp");
    expect(activeLabel()).toBe(labels()[1]);
    key(input, "PageUp");
    expect(activeLabel()).toBe(labels()[0]);
    key(input, "End", { ctrlKey: true });
    expect(activeLabel()).toBe(labels()[n - 1]);
    // Home/End without Ctrl stay with the text field
    expect(key(input, "Home").defaultPrevented).toBe(false);
    key(input, "Home", { ctrlKey: true });
    expect(activeLabel()).toBe(labels()[0]);
    expect(key(input, "a").defaultPrevented).toBe(false);
    options()[3].dispatchEvent(new MouseEvent("mousemove", { bubbles: true }));
    expect(activeLabel()).toBe(labels()[3]);
    expect(input.getAttribute("aria-activedescendant")).toBe("palette-opt-3");
  });

  it("Escape closes; reopening starts over", async () => {
    const { input, activeLabel, labels, dlg } = await setup();
    type(input, "sett");
    key(input, "Escape");
    expect(dlg.open).toBe(false);
    expect(paletteOpen()).toBe(false);
    paletteOpen.set(true);
    expect(activeLabel()).toBe(labels()[0]);
    expect(labels()[0]).toBe("Run all tests");
  });

  describe("sub-lists", () => {
    it("Go to test…: failing tests first, Enter selects the test", async () => {
      const { a, input, find, labels, dlg } = await setup();
      find("Go to test…").click();
      expect(paletteOpen()).toBe(true);
      expect(input.placeholder).toBe("Go to test…");
      expect(text(dlg.querySelector(".palette-mode"))).toBe("Tests");
      expect(labels()).toEqual(["CancelOrder_Refunds", "PlaceOrder_ReturnsCode", "AddItem_IncreasesTotal"]);
      type(input, "prc");
      expect(labels()[0]).toBe("PlaceOrder_ReturnsCode");
      // a fully qualified name matches too (ranked lower)
      type(input, "Shop.Cart");
      expect(labels()).toEqual(["AddItem_IncreasesTotal"]);
      key(input, "Enter");
      await flushMicrotasks();
      expect(a.selectTest).toHaveBeenCalledWith("t:P1|Shop.Cart.AddItem_IncreasesTotal");
    });

    it("Backspace in an empty sub-list and Escape go back to the commands", async () => {
      const { input, find, labels } = await setup();
      find("Go to test…").click();
      expect(key(input, "Backspace").defaultPrevented).toBe(true);
      expect(labels()).toContain("Run all tests");
      // Backspace with text or in the command list is plain editing
      expect(key(input, "Backspace").defaultPrevented).toBe(false);
      find("Switch theme…").click();
      type(input, "dark");
      expect(key(input, "Backspace").defaultPrevented).toBe(false);
      key(input, "Escape");
      expect(paletteOpen()).toBe(true);
      expect(input.value).toBe("");
      expect(labels()).toContain("Run all tests");
    });

    it("View run…: lists stored runs, Enter views one", async () => {
      const runs = [
        runSummary({ runId: "r2", startedAt: "2026-10-02T10:00:00Z", total: 3, passed: 2, failed: 1, scope: "subset", trigger: "watch" }),
        runSummary({ runId: "r1", startedAt: "2026-10-01T10:00:00Z", total: 3, passed: 3 }),
      ];
      const p = await setup({ state: { runs } });
      const { a, find, options } = p;
      let input = p.input;
      find("View run…").click();
      expect(input.placeholder).toBe("View run from…");
      expect(options()).toHaveLength(2);
      const detail = text(options()[0].querySelector(".palette-detail"));
      expect(detail).toContain("3 selected");
      expect(detail).toContain("watch");
      key(input, "ArrowDown");
      key(input, "Enter");
      await flushMicrotasks();
      expect(a.viewRun).toHaveBeenCalledWith("r1");
      paletteOpen.set(true);
      input = p.input;
      find("View run…").click();
      type(input, "zzzzzz");
      expect(text(p.dlg.querySelector(".palette-empty"))).toBe("No stored runs match.");
    });

    it("Switch theme…: every family and mode", async () => {
      const p = await setup();
      const { find, labels } = p;
      const input = p.input;
      expect(text(find("Switch theme…").querySelector(".palette-detail"))).toBe("Catppuccin · system");
      find("Switch theme…").click();
      for (const f of THEME_FAMILIES) expect(labels()).toContain(`${f.label} colors`);
      expect(labels()).toContain("Dark mode");
      expect(text(find("Catppuccin colors").querySelector(".palette-detail"))).toContain("current");
      type(input, "dracula");
      expect(labels()[0]).toBe("Dracula colors");
      key(input, "Enter");
      await flushMicrotasks();
      expect(themeFamily()).toBe("dracula");
      paletteOpen.set(true);
      find("Switch theme…").click();
      find("Light mode").click();
      await flushMicrotasks();
      expect(themePref()).toBe("light");
      paletteOpen.set(true);
      find("Switch theme…").click();
      expect(text(find("Light mode").querySelector(".palette-detail"))).toBe("current");
      find("Follow system (light / dark)").click();
      await flushMicrotasks();
      expect(themePref()).toBe("system");
    });
  });

  it("test details show the project when there are several", async () => {
    const { input, options } = await setup({ state: { projects: [{ id: "P1", name: "One", path: "/1", status: "idle" }, { id: "P2", name: "Two", path: "/2", status: "idle" }] } });
    type(input, "cancelorder");
    const row = options().find((o) => text(o.querySelector(".palette-label")) === "CancelOrder_Refunds")!;
    expect(text(row.querySelector(".palette-detail"))).toBe("Shop.Orders · P1");
  });
});
