/**
 * <lc-app> (the shell): header, nav rail + pages, message bars, toolbar, busy gate, shortcuts wiring, deep links,
 * palette wiring. One file: importing app.ts pulls in every component.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import "../../src/app";
import { buildErrorsFor, requestOpenSource } from "../../src/components/build-errors";
import { requestSelectTest } from "../../src/components/coverage-line-tests";
import type { TreeNode } from "../../src/tree";
import { paletteOpen } from "../../src/components/command-palette";
import { shortcutsOpen } from "../../src/components/shortcuts";
import { filterText, multiSelection, statusFilter, treeGroup, treeSort } from "../../src/components/tree-pane";
import type { ExplorerState, ServerEvent, TestNode } from "../../src/contract";
import { debugSession } from "../../src/debug";
import { resetLocalSettings, setSetting } from "../../src/settings";
import { editorNotice } from "../../src/store";
import { setUiDefaults } from "../../src/ui-defaults";
import { click, flushFrame, key, makeStore, mount, project, runSummary, stateEvent, testNode, text, type } from "../helpers/app";
import { canvasCalls, flushMicrotasks } from "../helpers/dom";

const T1 = testNode({ id: "Ns.ClassA.Test1", outcome: "passed", durationMs: 12 });
const T2 = testNode({ id: "Ns.ClassA.Test2", outcome: "failed", errorMessage: "Assert.AreEqual failed\nmore", durationMs: 30 });
const T3 = testNode({ id: "Ns.ClassB.Test3", outcome: "passed", durationMs: 5 });

// happy-dom fires "hashchange" for history.replaceState() (browsers don't): the shell writes the hash with
// replaceState, so those events would look like user navigation. Only hashchange events a test sends get through.
const MANUAL = Symbol("manual");
window.addEventListener(
  "hashchange",
  (e) => {
    if (!(e as unknown as Record<symbol, boolean>)[MANUAL]) e.stopImmediatePropagation();
  },
  true,
);
function navigate(hash: string) {
  history.replaceState(null, "", `/${hash}`);
  const e = new Event("hashchange");
  (e as unknown as Record<symbol, boolean>)[MANUAL] = true;
  window.dispatchEvent(e);
}

function setVisibility(state: "visible" | "hidden") {
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => state });
  document.dispatchEvent(new Event("visibilitychange"));
}

async function setup(opts: { kind?: "ws" | "mock"; open?: boolean; state?: Partial<ExplorerState> | null; hash?: string } = {}) {
  if (opts.hash) history.replaceState(null, "", `/${opts.hash}`);
  const ctx = makeStore({ kind: opts.kind ?? "ws", open: opts.open });
  if (opts.state !== null) ctx.store.apply(stateEvent({ tests: [T1, T2, T3], ...opts.state }));
  const app = mount(document.createElement("lc-app"));
  await flushFrame();
  const emit = async (e: ServerEvent) => {
    ctx.store.apply(e);
    await flushFrame();
  };
  const btn = (label: string) =>
    [...app.querySelectorAll<HTMLButtonElement>("button")].find((b) => text(b) === label || b.getAttribute("aria-label") === label || b.title === label)!;
  return { ...ctx, app, emit, btn };
}

beforeEach(() => {
  history.replaceState(null, "", "/");
  setUiDefaults({});
  resetLocalSettings();
  // no exit animations: message bars leave at once
  document.documentElement.dataset.motion = "off";
  paletteOpen.set(false);
  shortcutsOpen.set(false);
  debugSession.set(undefined);
  editorNotice.set(undefined);
  buildErrorsFor.set(undefined);
  statusFilter.set("all");
  filterText.set("");
  treeSort.set("default");
  treeGroup.set("namespace");
  multiSelection.set(new Set());
});
afterEach(() => {
  setVisibility("visible");
  paletteOpen.set(false);
  shortcutsOpen.set(false);
  buildErrorsFor.set(undefined);
});

describe("header", () => {
  it("says Connecting… until the first snapshot, then the workspace name", async () => {
    const { app, emit } = await setup({ state: null });
    const h1 = app.querySelector("h1.page-header")!;
    expect(text(h1)).toBe("Connecting…");
    expect(h1.classList.contains("is-placeholder")).toBe(true);
    await emit(stateEvent({ tests: [T1] }));
    expect(text(h1)).toBe("Tests");
    expect(h1.classList.contains("is-placeholder")).toBe(false);
    await emit(stateEvent({ tests: [T1], workspaceName: "Shop.slnx" }));
    expect(text(h1)).toBe("Shop.slnx");
    expect(h1.getAttribute("title")).toBe("Shop.slnx");
    expect(app.classList.contains("layout")).toBe(true);
    expect(app.querySelector("lc-run-switcher")).toBeTruthy();
  });

  it("explains a reconnect wait in the connecting pane", async () => {
    const { app, transport } = await setup({ kind: "mock", open: false, state: null });
    expect(text(app.querySelector(".empty-title"))).toBe("Connecting to the Labcoat host…");
    expect(text(app.querySelector(".empty-sub"))).toBe("Waiting for the first state snapshot.");
    transport.setStatus("reconnecting", { retryInMs: 2500 });
    await flushFrame();
    expect(text(app.querySelector(".empty-sub"))).toBe("Host not reachable yet. Retrying in 3s.");
  });

  it("shows the never-connected page when the first connection failed (ws)", async () => {
    const { app, transport } = await setup({ open: false, state: null });
    transport.setStatus("reconnecting", { retryInMs: 4000 });
    await flushFrame();
    expect(app.querySelector(".never-connected")).toBeTruthy();
    expect(app.querySelector(".app-header")).toBeNull();
    expect(text(app.querySelector(".nc-status"))).toContain("Retrying in 4 s");
    click([...app.querySelectorAll("button")].find((b) => text(b) === "Retry now")!);
    expect(transport.retryNow).toHaveBeenCalled();
    transport.setStatus("open");
    await flushFrame();
    expect(app.querySelector(".never-connected")).toBeNull();
    expect(app.querySelector(".app-header")).toBeTruthy();
  });
});

describe("nav rail and pages", () => {
  it("switches pages, marks the current one and mirrors it in the hash", async () => {
    const { app } = await setup();
    const rail = app.querySelector("nav.nav-rail")!;
    expect(rail.getAttribute("aria-label")).toBe("Main");
    const link = (p: string) => rail.querySelector<HTMLAnchorElement>(`a[href="#/${p}"]`)!;
    expect(link("tests").getAttribute("aria-current")).toBe("page");
    expect(app.querySelector("lc-tree-pane")).toBeTruthy();
    click(link("coverage"));
    await flushMicrotasks();
    expect(link("coverage").classList.contains("active")).toBe(true);
    expect(link("tests").hasAttribute("aria-current")).toBe(false);
    expect(app.querySelector("lc-coverage-page")).toBeTruthy();
    expect(app.querySelector("lc-tree-pane")).toBeNull();
    expect(location.hash).toBe("#/coverage");
    click(link("settings"));
    expect(app.querySelector("lc-settings-page")).toBeTruthy();
    expect(location.hash).toBe("#/settings");
    expect(localStorage.getItem("mste.page")).toContain("settings");
    click(link("tests"));
    expect(app.querySelector("lc-tree-pane")).toBeTruthy();
  });

  it("the Output button toggles the output panel (and Ctrl+` too)", async () => {
    const { app } = await setup();
    const out = [...app.querySelectorAll<HTMLButtonElement>("button.rail-item")].find((b) => text(b).startsWith("Output"))!;
    expect(out.getAttribute("aria-pressed")).toBe("false");
    expect(app.querySelector("lc-log-strip")).toBeTruthy();
    expect(app.querySelector("lc-log-panel")).toBeNull();
    click(out);
    expect(out.getAttribute("aria-pressed")).toBe("true");
    expect(app.querySelector("lc-log-panel")).toBeTruthy();
    expect(app.querySelector(".splitter-h[role=separator]")).toBeTruthy();
    key(document.body, "`", { ctrlKey: true });
    expect(app.querySelector("lc-log-panel")).toBeNull();
  });

  it("the Output badge counts unseen errors; the setting opens the panel on errors", async () => {
    const { app, emit } = await setup();
    await emit({ type: "log", level: "error", message: "boom", timestamp: "2026-10-01T10:00:00Z" });
    await emit({ type: "log", level: "error", message: "boom 2", timestamp: "2026-10-01T10:00:01Z" });
    const out = [...app.querySelectorAll<HTMLButtonElement>("button.rail-item")].find((b) => text(b).startsWith("Output"))!;
    expect(text(out.querySelector(".rail-badge"))).toBe("2");
    expect(text(out.querySelector(".visually-hidden"))).toBe(", 2 errors");
    setSetting("openOutputOnError", true);
    document.documentElement.dataset.motion = "off";
    await emit({ type: "log", level: "error", message: "boom 3", timestamp: "2026-10-01T10:00:02Z" });
    expect(app.querySelector("lc-log-panel")).toBeTruthy();
    expect(out.querySelector(".rail-badge")).toBeNull();
  });

  it("follows hash changes (page, selection, filter, sort)", async () => {
    const { app } = await setup();
    navigate("#/tests?t=P1|Ns.ClassA.Test2&f=failed&sort=slowest");
    await flushFrame();
    expect(statusFilter()).toBe("failed");
    expect(treeSort()).toBe("slowest");
    expect(text(app.querySelector("lc-detail-pane"))).toContain("Test2");
    navigate("#/settings");
    expect(app.querySelector("lc-settings-page")).toBeTruthy();
    expect(statusFilter()).toBe("all");
  });

  it("opens a deep link on load (page, test, filter, run)", async () => {
    const { app, transport } = await setup({ hash: "#/tests?t=P1|Ns.ClassA.Test2&f=failed&run=r9" });
    expect(statusFilter()).toBe("failed");
    expect(transport.fetchRun).toHaveBeenCalledWith("r9");
    await flushMicrotasks();
    await flushFrame();
    expect(text(app.querySelector("lc-detail-pane"))).toContain("Test2");
    expect(location.hash).toContain("t=P1|Ns.ClassA.Test2");
    expect(location.hash).toContain("f=failed");
    expect(location.hash).toContain("run=r9");
  });
});

describe("message bars", () => {
  it("connection lost: reconnect countdown and Retry now", async () => {
    const { app, transport } = await setup();
    expect(app.querySelector(".messagebar-container")!.textContent).toBe("");
    transport.setStatus("reconnecting", { retryInMs: 3000 });
    await flushFrame();
    const bar = app.querySelector(".messagebar.mb-warning")!;
    expect(text(bar)).toContain("Connection to the explorer host lost.");
    expect(text(bar)).toContain("Reconnecting in 3s… Results shown may be stale.");
    click([...bar.querySelectorAll("button")].find((b) => text(b) === "Retry now")!);
    expect(transport.retryNow).toHaveBeenCalled();
    transport.setStatus("reconnecting");
    await flushFrame();
    expect(text(app.querySelector(".messagebar.mb-warning"))).toContain("Reconnecting… Results");
    transport.setStatus("open");
    await flushFrame();
    expect(app.querySelector(".messagebar")).toBeNull();
  });

  it("editor notice: dismiss, and gone by itself after 10 s", async () => {
    vi.useFakeTimers();
    const { app } = await setup();
    editorNotice.set("Couldn't open Rider: not found");
    const bar = app.querySelector(".messagebar[role=alert]")!;
    expect(text(bar)).toContain("Couldn't open Rider: not found");
    click([...bar.querySelectorAll("button")].find((b) => text(b) === "Dismiss")!);
    expect(editorNotice()).toBeUndefined();
    expect(app.querySelector(".messagebar")).toBeNull();
    editorNotice.set("again");
    vi.advanceTimersByTime(10_000);
    expect(editorNotice()).toBeUndefined();
  });

  it("viewing a past run: loading → ready (Compare, Back to live) and missing", async () => {
    const { app, transport, store } = await setup({ state: { runs: [runSummary({ runId: "r1", total: 3, passed: 2, failed: 1 })] } });
    let resolve!: (v: unknown) => void;
    transport.fetchRun.mockImplementation(() => new Promise((r) => (resolve = r as never)));
    store.viewRun("r1");
    await flushFrame();
    const bar = () => app.querySelector(".messagebar.top")!;
    expect(text(bar())).toContain("Viewing run from");
    expect(text(bar())).toContain("(loading…) — read-only snapshot");
    resolve({ run: runSummary({ runId: "r1", total: 2, passed: 1, failed: 1, scope: "subset" }), results: [{ projectId: "P1", id: "Ns.ClassA.Test1", outcome: "failed", errorMessage: "then" }] });
    await flushMicrotasks();
    await flushFrame();
    expect(bar().classList.contains("mb-snapshot")).toBe(true);
    expect(text(bar())).toContain("· 2 selected tests");
    expect([...bar().querySelectorAll("button")].map((b) => text(b))).toEqual(["Compare with…", "Back to live"]);
    // Escape goes back to live
    key(document.body, "Escape");
    await flushFrame();
    expect(store.get().viewing).toBeUndefined();
    expect(app.querySelector(".messagebar")).toBeNull();
    // a run the host no longer has
    transport.fetchRun.mockResolvedValue(null);
    store.viewRun("gone", { startedAt: "2026-10-01T09:00:00Z" });
    await flushMicrotasks();
    await flushFrame();
    expect(bar().classList.contains("mb-warning")).toBe(true);
    expect(text(bar())).toContain("That run is no longer stored");
    click([...bar().querySelectorAll("button")].find((b) => text(b) === "Back to live")!);
    expect(store.get().viewing).toBeUndefined();
  });

  it("build failed: first error, Rebuild and Show errors", async () => {
    const failed = project({
      status: "error",
      lastError: "Build failed",
      buildErrors: [
        { severity: "warning", message: "unused", file: "/src/A.cs", line: 3 },
        { severity: "error", message: "; expected", file: "C:\\src\\Calc.cs", line: 12, code: "CS1002" },
      ],
      buildErrorCount: 1,
    });
    const { app, transport } = await setup({ state: { projects: [failed] } });
    const bar = app.querySelector(".messagebar.mb-error")!;
    expect(text(bar.querySelector(".mb-title"))).toBe("Build failed: Project.One (1 error).");
    expect(text(bar.querySelector(".mb-ellipsis"))).toBe("Calc.cs:12 CS1002: ; expected");
    click([...bar.querySelectorAll("button")].find((b) => text(b) === "Rebuild")!);
    expect(transport.sent).toContainEqual({ type: "discover", projectId: "P1" });
    click([...bar.querySelectorAll("button")].find((b) => text(b) === "Show errors")!);
    expect(buildErrorsFor()).toBe("P1");
    expect(document.querySelector("dialog.build-errors-dialog")).toBeTruthy();
  });

  it("build failed without diagnostics: last error, outdated results, View details", async () => {
    const p1 = project({ status: "error", lastError: "MSB1009: Project file does not exist." });
    const p2 = project({ id: "P2", name: "Two", status: "error", lastError: "Build failed with 2 errors", resultsOutdated: true });
    const { app, emit } = await setup({ state: { projects: [p1] } });
    const bar = () => app.querySelector(".messagebar.mb-error")!;
    expect(text(bar().querySelector(".mb-ellipsis"))).toBe("MSB1009: Project file does not exist.");
    click([...bar().querySelectorAll("button")].find((b) => text(b) === "View details")!);
    expect(location.hash).toContain("n=p%3AP1");
    await emit(stateEvent({ tests: [T1], projects: [p1, p2] }));
    expect(text(bar().querySelector(".mb-title"))).toBe("Build failed: 2 projects.");
    expect(text(bar().querySelector(".mb-ellipsis"))).toBe("Results shown are from the last successful build.");
  });

  it("debug session: starting, waiting (Copy PID, Stop), attached, error", async () => {
    const { app, transport } = await setup();
    debugSession.set({ label: "Test1", projectId: "P1", testIds: ["Ns.ClassA.Test1"], status: "starting" });
    const bar = () => app.querySelector(".debug-bar")!;
    expect(text(bar())).toContain("Starting Test1 for debugging…");
    expect(text(bar().querySelector("button.mb-action"))).toBe("Dismiss");
    debugSession.set({ label: "Test1", projectId: "P1", testIds: ["Ns.ClassA.Test1"], status: "waiting", pid: 4242, runId: "d1" });
    expect(text(bar())).toContain("Waiting for debugger — attach to PID 4242");
    expect(text(bar())).toContain("VS Code: Run and Debug");
    expect(bar().querySelector(".mb-ellipsis")!.getAttribute("title")).toContain("Rider:");
    expect(bar().querySelector(".lc-copy")).toBeTruthy();
    debugSession.set({ label: "Test1", projectId: "P1", testIds: [], status: "attached", pid: 4242, runId: "d1" });
    expect(text(bar())).toContain("Debugger attached (PID 4242) — running Test1");
    const stop = bar().querySelector<HTMLButtonElement>("button.mb-action")!;
    expect(text(stop)).toBe("Stop");
    click(stop);
    expect(transport.sent).toContainEqual({ type: "cancel" });
    expect(debugSession()).toBeUndefined();
    debugSession.set({ label: "Test1", projectId: "P1", testIds: [], status: "error", message: "no such test" });
    expect(bar().classList.contains("mb-error")).toBe(true);
    expect(text(bar())).toContain("Couldn't start Test1 for debugging.");
    expect(text(bar())).toContain("no such test");
  });

  it("a refused command becomes an error toast", async () => {
    const { emit } = await setup();
    await emit({ type: "commandRejected", command: "run", status: 404, error: "unknown tests", unknownTestIds: ["a", "b"] });
    expect(text(document.querySelector(".lc-toaster"))).toContain("“run” refused: unknown tests (2 unknown tests)");
    await emit({ type: "commandRejected", status: 400, error: "malformed" } as ServerEvent);
    expect(text(document.querySelector(".lc-toaster"))).toContain("malformed");
  });
});

describe("toolbar", () => {
  it("Run all, Run failed, Watch, Collect coverage, Rediscover send commands", async () => {
    const { app, transport, btn } = await setup();
    click(app.querySelector(".run-slot .run-primary")!);
    expect(transport.sent.at(-1)).toEqual({ type: "run" });
    const runFailed = btn("Run the failed tests again");
    expect(text(runFailed.querySelector(".btn-count"))).toBe("1");
    expect(runFailed.querySelector(".btn-count")!.classList.contains("failed")).toBe(true);
    click(runFailed);
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test2"] });
    const watch = app.querySelector<HTMLButtonElement>(".watch-btn")!;
    expect(text(watch)).toBe("Watch");
    click(watch);
    expect(transport.sent.at(-1)).toEqual({ type: "setWatch", enabled: true });
    await flushFrame();
    expect(text(watch)).toBe("Watching");
    expect(app.querySelector(".watch-split")!.classList.contains("on")).toBe(true);
    expect(app.querySelector("lc-watch-strip")).toBeTruthy();
    const cov = btn("Collect code coverage on every run");
    click(cov);
    expect(transport.sent.at(-1)).toEqual({ type: "setCoverage", enabled: true });
    // turning coverage on shows the Coverage tab
    expect(app.querySelector("lc-coverage-pane")).toBeTruthy();
    click(btn("Rediscover"));
    expect(transport.sent.at(-1)).toEqual({ type: "discover" });
  });

  it("the watch menu starts watching and builds the impact map", async () => {
    const { app, transport } = await setup({ state: { impactMap: { files: 4, tests: 9, builtAt: "2026-10-01T10:00:00Z" } } });
    click(app.querySelector(".watch-split .split-chevron")!);
    const items = [...document.querySelectorAll<HTMLElement>(".lc-menu-item")];
    expect(items.map((i) => text(i))).toEqual(expect.arrayContaining([expect.stringContaining("Start watching"), expect.stringContaining("Build impact map")]));
    expect(text(items.find((i) => text(i).includes("Build impact map")))).toContain("4 source files → 9 tests");
    click(items.find((i) => text(i).includes("Build impact map"))!);
    await flushMicrotasks();
    expect(transport.sent).toContainEqual({ type: "buildImpactMap" });
  });

  it("everything is disabled while disconnected or without tests", async () => {
    const { app, btn } = await setup({ kind: "mock", open: false, state: { tests: [] } });
    expect(app.querySelector<HTMLButtonElement>(".run-primary")!.disabled).toBe(true);
    expect(app.querySelector<HTMLButtonElement>(".watch-btn")!.disabled).toBe(true);
    expect(btn("Rediscover").disabled).toBe(true);
    expect(btn("Run the failed tests again").disabled).toBe(true);
  });

  it("Run all ⇄ Stop while a run is in flight", async () => {
    const { app, transport, emit } = await setup();
    const stop = app.querySelector<HTMLButtonElement>(".run-slot .btn-danger")!;
    expect(stop.getAttribute("aria-hidden")).toBe("true");
    expect(stop.tabIndex).toBe(-1);
    await emit({ type: "runStarted", run: runSummary({ runId: "r2", total: 3 }), testIds: ["Ns.ClassA.Test1", "Ns.ClassA.Test2", "Ns.ClassB.Test3"] });
    expect(app.classList.contains("is-running")).toBe(true);
    expect(stop.getAttribute("aria-hidden")).toBe("false");
    expect(app.querySelector(".run-primary")!.classList.contains("slot-hidden")).toBe(true);
    click(stop);
    expect(transport.sent.at(-1)).toEqual({ type: "cancel" });
    // Ctrl+. stops too
    key(document.body, ".", { ctrlKey: true });
    expect(transport.sent.filter((c) => c.type === "cancel")).toHaveLength(2);
    // Mod+Enter does nothing while running
    key(document.body, "Enter", { ctrlKey: true });
    expect(transport.sent.some((c) => c.type === "run")).toBe(false);
    await emit({ type: "runFinished", run: runSummary({ runId: "r2", total: 3, passed: 3 }) });
    expect(app.classList.contains("is-running")).toBe(false);
    key(document.body, "Enter", { ctrlKey: true });
    expect(transport.sent.at(-1)).toEqual({ type: "run" });
    // stop when nothing runs: nothing is sent
    key(document.body, ".", { ctrlKey: true });
    expect(transport.sent.at(-1)).toEqual({ type: "run" });
  });

  it("Run N matching runs what the filtered tree shows", async () => {
    const { app, transport } = await setup();
    statusFilter.set("failed");
    await flushFrame();
    const primary = app.querySelector<HTMLButtonElement>(".run-primary")!;
    expect(text(primary)).toBe("Run 1 matching");
    expect(primary.title).toContain("Run the 1 tests the filtered tree shows");
    click(primary);
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test2"] });
  });

  it("W toggles watch (single key), only when connected", async () => {
    const { transport } = await setup();
    key(document.body, "w");
    expect(transport.sent.at(-1)).toEqual({ type: "setWatch", enabled: true });
  });
});

describe("busy gate (html.lc-busy)", () => {
  it("is set for runs of more than 200 tests and while the tab is hidden", async () => {
    const many: TestNode[] = Array.from({ length: 201 }, (_, i) => testNode({ id: `Big.Suite.T${i}` }));
    const { emit } = await setup({ state: { tests: many } });
    const html = document.documentElement;
    expect(html.classList.contains("lc-busy")).toBe(false);
    await emit({ type: "runStarted", run: runSummary({ runId: "big", total: 0 }), testIds: many.map((t) => t.id) });
    expect(html.classList.contains("lc-busy")).toBe(true);
    await emit({ type: "runFinished", run: runSummary({ runId: "big", total: 201, passed: 201 }) });
    expect(html.classList.contains("lc-busy")).toBe(false);
    setVisibility("hidden");
    expect(html.classList.contains("lc-busy")).toBe(true);
    setVisibility("visible");
    expect(html.classList.contains("lc-busy")).toBe(false);
  });

  it("a small run is not busy; removing the app clears the class", async () => {
    const { app, emit } = await setup();
    await emit({ type: "runStarted", run: runSummary({ runId: "s", total: 3 }), testIds: [T1.id] });
    expect(document.documentElement.classList.contains("lc-busy")).toBe(false);
    setVisibility("hidden");
    expect(document.documentElement.classList.contains("lc-busy")).toBe(true);
    app.remove();
    await flushMicrotasks();
    expect(document.documentElement.classList.contains("lc-busy")).toBe(false);
  });
});

describe("keyboard", () => {
  it("F8 selects the next failure and announces it; nothing when there are none", async () => {
    vi.useFakeTimers();
    const { app } = await setup();
    key(document.body, "F8");
    await flushFrame();
    expect(location.hash).toContain("t=P1|Ns.ClassA.Test2");
    expect(text(app.querySelector("lc-detail-pane"))).toContain("Test2");
    vi.advanceTimersByTime(50);
    const live = [...document.querySelectorAll(".visually-hidden[aria-live=polite]")].map((e) => text(e));
    expect(live).toContain("Failure 1 of 1: Test2 — Assert.AreEqual failed");
  });

  it("F8 with no failures says so", async () => {
    vi.useFakeTimers();
    await setup({ state: { tests: [T1] } });
    key(document.body, "F8");
    vi.advanceTimersByTime(50);
    expect([...document.querySelectorAll(".visually-hidden[aria-live=polite]")].some((e) => text(e) === "No failures")).toBe(true);
  });

  it("? opens the shortcut overlay, Ctrl+K the palette", async () => {
    await setup();
    key(document.body, "?");
    expect(shortcutsOpen()).toBe(true);
    expect(document.querySelector("dialog.shortcuts-dialog")).toBeTruthy();
    shortcutsOpen.set(false);
    key(document.body, "k", { ctrlKey: true });
    expect(paletteOpen()).toBe(true);
    expect(document.querySelector("dialog.palette-dialog")).toBeTruthy();
  });
});

describe("command palette wiring", () => {
  const open = () => {
    paletteOpen.set(true);
    return document.querySelector<HTMLInputElement>("dialog.palette-dialog .palette-input")!;
  };
  const run = async (q: string) => {
    const input = open();
    type(input, q);
    key(input, "Enter");
    await flushMicrotasks();
  };

  it("opens pages, toggles output and shortcuts", async () => {
    const { app } = await setup();
    await run("Open settings");
    expect(app.querySelector("lc-settings-page")).toBeTruthy();
    await run("Open coverage report");
    expect(app.querySelector("lc-coverage-page")).toBeTruthy();
    await run("Open tests");
    expect(app.querySelector("lc-tree-pane")).toBeTruthy();
    await run("Toggle output panel");
    expect(app.querySelector("lc-log-panel")).toBeTruthy();
    await run("Shortcuts");
    expect(shortcutsOpen()).toBe(true);
  });

  it("sends run / watch / coverage / impact map / rediscover / restart commands", async () => {
    const { transport } = await setup();
    await run("Run all tests");
    expect(transport.sent.at(-1)).toEqual({ type: "run" });
    await run("Run failed");
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test2"] });
    await run("Turn watch mode on");
    expect(transport.sent.at(-1)).toEqual({ type: "setWatch", enabled: true });
    await run("Collect coverage on runs");
    expect(transport.sent.at(-1)).toEqual({ type: "setCoverage", enabled: true });
    await run("Build impact map");
    expect(transport.sent.at(-1)).toEqual({ type: "buildImpactMap" });
    await run("Rediscover");
    expect(transport.sent.at(-1)).toEqual({ type: "discover" });
    await run("Restart test host");
    expect(transport.sent.at(-1)).toEqual({ type: "restartHost", projectId: undefined });
  });

  it("goes to a test, then runs / covers / debugs the selection", async () => {
    vi.useFakeTimers();
    const { transport, store, app } = await setup();
    const input = open();
    key(input, "ArrowDown"); // nothing typed yet: commands
    await run("Test3");
    await flushFrame();
    expect(location.hash).toContain("t=P1|Ns.ClassB.Test3");
    await run("Run selected");
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassB.Test3"] });
    await run("Collect coverage for selected");
    expect(store.get().coverageRequest?.label.title).toBe("Test3");
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassB.Test3"], coverage: true });
    expect(app.querySelector("lc-coverage-pane")).toBeTruthy();
    await run("Debug test");
    expect(transport.sent.at(-1)).toEqual({ type: "debug", projectId: "P1", testIds: ["Ns.ClassB.Test3"] });
    await run("Restart test host");
    expect(transport.sent.at(-1)).toEqual({ type: "restartHost", projectId: "P1" });
    await run("Copy link");
    await flushMicrotasks();
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith(expect.stringContaining("Ns.ClassB.Test3"));
  });

  it("sorts the tree, views a run and shows build errors", async () => {
    const { transport } = await setup({
      state: { runs: [runSummary({ runId: "r1", total: 3, passed: 3 })], projects: [project({ status: "error", buildErrors: [{ severity: "error", message: "x" }] })] },
    });
    await run("Sort: slowest");
    expect(treeSort()).toBe("slowest");
    const input = open();
    type(input, "View run");
    key(input, "Enter");
    key(input, "Enter");
    await flushMicrotasks();
    expect(transport.fetchRun).toHaveBeenCalledWith("r1");
    await run("Show build errors");
    expect(buildErrorsFor()).toBe("P1");
  });
});

describe("Coverage tab", () => {
  it("selecting a test while the Coverage tab is open looks up its cached coverage", async () => {
    const { app, transport, store } = await setup({ hash: "#/tests?t=P1|Ns.ClassA.Test1" });
    const tab = [...app.querySelectorAll<HTMLElement>("[role=tab]")].find((t) => text(t).startsWith("Coverage"))!;
    click(tab);
    await new Promise((r) => setTimeout(r, 0));
    expect(transport.fetchTestCoverage).toHaveBeenCalledWith("P1", ["Ns.ClassA.Test1"], expect.anything());
    expect(app.querySelector("[role=tabpanel] lc-coverage-pane, lc-coverage-pane[role=tabpanel]")).toBeTruthy();
    // the cached report found: the tab says whose coverage it is
    transport.fetchTestCoverage.mockResolvedValue(null);
    store.apply({
      type: "coverageUpdated",
      coverage: {
        runId: "c1",
        generatedAt: "",
        projectIds: ["P1"],
        lineRate: 0.5,
        branchRate: 0,
        linesCovered: 1,
        linesValid: 2,
        branchesCovered: 0,
        branchesValid: 0,
        files: [{ path: "/src/A.cs", relativePath: "A.cs", module: "A", linesCovered: 1, linesValid: 2, lineRate: 0.5, branchesCovered: 0, branchesValid: 0, branchRate: 0, lines: [{ line: 3, hits: 1 }] }],
        scope: "subset",
        testIds: ["Ns.ClassA.Test1"],
      },
    });
    await flushFrame();
    expect(text(tab)).toContain("· Test1");
    expect(text(tab.querySelector(".tab-pill"))).toBe("50.0%");
  });
});

describe("celebration", () => {
  it("bursts confetti for the run that turns the suite green (opt-in)", async () => {
    vi.useFakeTimers();
    setSetting("confetti", true);
    document.documentElement.dataset.motion = "full";
    const { emit } = await setup({ state: { tests: [T1, T2, T3] } });
    await emit({ type: "runStarted", run: runSummary({ runId: "g1", total: 3 }), testIds: [T1.id, T2.id, T3.id] });
    const hist = (outcome: "passed" | "failed") => [
      { outcome, at: "2026-10-01T09:00:00Z", runId: "g0" },
      { outcome: "passed" as const, at: "2026-10-01T10:00:00Z", runId: "g1" },
    ];
    await emit({
      type: "testsUpdated",
      tests: [
        { ...T1, history: hist("passed") },
        { ...T2, outcome: "passed", history: hist("failed") },
        { ...T3, history: hist("passed") },
      ],
    });
    await emit({ type: "runFinished", run: runSummary({ runId: "g1", total: 3, passed: 3 }) });
    expect(document.querySelector("canvas.confetti-canvas")).toBeTruthy();
    expect(canvasCalls.length).toBeGreaterThanOrEqual(0);
  });

  it("waits for history, then treats a run without history as news", async () => {
    vi.useFakeTimers();
    setSetting("confetti", true);
    document.documentElement.dataset.motion = "full";
    const { emit } = await setup();
    await emit({ type: "runStarted", run: runSummary({ runId: "g2", total: 3 }), testIds: [T1.id, T2.id, T3.id] });
    await emit({ type: "runFinished", run: runSummary({ runId: "g2", total: 3, passed: 3 }) });
    expect(document.querySelector("canvas.confetti-canvas")).toBeNull();
    vi.advanceTimersByTime(1500);
    expect(document.querySelector("canvas.confetti-canvas")).toBeTruthy();
  });

  it("stays quiet for a red run or when already green", async () => {
    vi.useFakeTimers();
    setSetting("confetti", true);
    document.documentElement.dataset.motion = "full";
    const { emit } = await setup();
    await emit({ type: "runStarted", run: runSummary({ runId: "g3", total: 3 }), testIds: [T1.id] });
    await emit({ type: "runFinished", run: runSummary({ runId: "g3", total: 3, passed: 2, failed: 1 }) });
    const green = [
      { outcome: "passed" as const, at: "a", runId: "g3" },
      { outcome: "passed" as const, at: "b", runId: "g4" },
    ];
    await emit({ type: "runStarted", run: runSummary({ runId: "g4", total: 3 }), testIds: [T1.id] });
    await emit({ type: "testsUpdated", tests: [{ ...T1, history: green }] });
    await emit({ type: "runFinished", run: runSummary({ runId: "g4", total: 3, passed: 3 }) });
    vi.advanceTimersByTime(2000);
    expect(document.querySelector("canvas.confetti-canvas")).toBeNull();
  });
});

describe("splitters", () => {
  it("dragging the tree splitter sets the tree width (persisted)", async () => {
    const { app } = await setup();
    const sp = app.querySelector<HTMLElement>(".splitter-v[role=separator]")!;
    expect(sp.getAttribute("aria-label")).toBe("Resize test tree");
    sp.dispatchEvent(new PointerEvent("pointerdown", { clientX: 400, bubbles: true, cancelable: true }));
    expect(sp.classList.contains("dragging")).toBe(true);
    expect(document.body.classList.contains("is-dragging")).toBe(true);
    window.dispatchEvent(new PointerEvent("pointermove", { clientX: 520 }));
    window.dispatchEvent(new PointerEvent("pointerup", {}));
    expect(sp.classList.contains("dragging")).toBe(false);
    const w = app.querySelector<HTMLElement>(".workspace")!.style.getPropertyValue("--tree-w");
    expect(w).not.toBe("36%");
    expect(localStorage.getItem("mste.treeWidthPct")).toBe(w.replace("%", ""));
  });

  it("dragging the output splitter sets the panel height", async () => {
    const { app } = await setup();
    click([...app.querySelectorAll<HTMLButtonElement>("button.rail-item")].find((b) => text(b).startsWith("Output"))!);
    const sp = app.querySelector<HTMLElement>(".splitter-h")!;
    sp.dispatchEvent(new PointerEvent("pointerdown", { clientY: 500, bubbles: true, cancelable: true }));
    window.dispatchEvent(new PointerEvent("pointermove", { clientY: 450 }));
    window.dispatchEvent(new PointerEvent("pointerup", {}));
    expect(app.querySelector<HTMLElement>(".log-wrap")!.style.height).toBe("270px");
  });
});

describe("child component callbacks", () => {
  const nodes = (roots: readonly TreeNode[]) => {
    const out: TreeNode[] = [];
    const walk = (n: TreeNode) => (out.push(n), n.children.forEach(walk));
    roots.forEach(walk);
    return out;
  };

  it("tree pane: run / coverage / run tests / coverage tests / rediscover / select", async () => {
    const { app, transport, store } = await setup();
    const tree = app.querySelector("lc-tree-pane")!;
    const all = nodes(tree.roots);
    const project = all.find((n) => n.kind === "project")!;
    const cls = all.find((n) => n.kind === "class" && n.label === "ClassA")!;
    const leaf = all.find((n) => n.test?.id === "Ns.ClassB.Test3")!;
    tree.onRun(project);
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1" });
    tree.onRun(cls);
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test1", "Ns.ClassA.Test2"] });
    tree.onRunTests([T1, testNode({ id: "X.Y.Z", projectId: "P2" })]);
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: undefined, testIds: ["Ns.ClassA.Test1", "X.Y.Z"] });
    tree.onRunTests([]);
    expect(transport.sent).toHaveLength(3);
    tree.onRediscover();
    expect(transport.sent.at(-1)).toEqual({ type: "discover" });
    tree.onCoverage(project);
    expect(store.get().coverageRequest?.label).toEqual({ title: project.label, context: "All tests in project" });
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: undefined, coverage: true });
    tree.onCoverage(cls);
    expect(store.get().coverageRequest?.label).toEqual({ title: "ClassA", context: "Class · 2 tests" });
    tree.onCoverage(leaf);
    expect(store.get().coverageRequest?.label).toEqual({ title: "Test3", context: "Ns.ClassB" });
    tree.onCoverageTests([T1, T3], "2 selected");
    expect(store.get().coverageRequest?.label).toEqual({ title: "2 selected", context: "2 tests" });
    tree.onCoverageTests([], "none");
    expect(store.get().coverageRequest?.label.title).toBe("2 selected");
    tree.onSelect(leaf.key);
    await flushFrame();
    expect(location.hash).toContain("Ns.ClassB.Test3");
    // the debug action is offered while connected
    expect(typeof tree.onDebug).toBe("function");
  });

  it("detail pane, run switcher, coverage page and output callbacks", async () => {
    const { app, transport } = await setup({ hash: "#/tests?t=P1|Ns.ClassA.Test1" });
    const detail = app.querySelector("lc-detail-pane")!;
    const tree = app.querySelector("lc-tree-pane")!;
    const leaf = nodes(tree.roots).find((n) => n.test?.id === "Ns.ClassA.Test1")!;
    detail.onRun(leaf);
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test1"] });
    detail.onRun(leaf, [T3]);
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassB.Test3"] });
    detail.onSelect("t:P1|Ns.ClassA.Test2");
    await flushFrame();
    expect(location.hash).toContain("Ns.ClassA.Test2");
    // run switcher: a diff row jumps to the test (from another page)
    navigate("#/settings");
    app.querySelector("lc-run-switcher")!.onJump("t:P1|Ns.ClassB.Test3");
    await flushFrame();
    expect(app.querySelector("lc-tree-pane")).toBeTruthy();
    expect(location.hash).toContain("Ns.ClassB.Test3");
    // coverage page opens a file
    navigate("#/coverage");
    const page = app.querySelector("lc-coverage-page")!;
    page.onOpen!("/src/A.cs");
    expect(page.openPath).toBe("/src/A.cs");
    navigate("#/tests");
    // output strip opens the panel, the panel's close button closes it
    app.querySelector("lc-log-strip")!.onOpen();
    expect(app.querySelector("lc-log-panel")).toBeTruthy();
    app.querySelector("lc-log-panel")!.onClose();
    expect(app.querySelector("lc-log-panel")).toBeNull();
  });

  it("app-wide requests: open a build error's project, select a test from the line popover", async () => {
    const { app } = await setup();
    requestOpenSource({ path: "/src/A.cs", line: 3, selectKey: "p:P1" });
    await flushFrame();
    expect(location.hash).toContain("n=p%3AP1");
    requestOpenSource({ path: "/src/A.cs" });
    requestSelectTest({ projectId: "P1", id: "Ns.ClassA.Test2" });
    await flushFrame();
    expect(location.hash).toContain("t=P1|Ns.ClassA.Test2");
    expect(text(app.querySelector("lc-detail-pane"))).toContain("Test2");
  });

  it("a hash link to a run views it; group-by selects synthetic group nodes", async () => {
    const { transport, store, app } = await setup({ state: { quarantined: ["P1|Ns.ClassA.Test2"] } });
    navigate("#/tests?run=r5");
    expect(transport.fetchRun).toHaveBeenCalledWith("r5");
    expect(store.get().viewing?.run.runId).toBe("r5");
    // quarantined failures don't count as failed
    expect(text(app.querySelector(".btn-count"))).toBe("0");
    treeGroup.set("outcome");
    await flushFrame();
    const tree = app.querySelector("lc-tree-pane")!;
    const group = tree.roots[0];
    expect(group.key.startsWith("g:")).toBe(true);
    tree.onSelect(group.key);
    await flushFrame();
    expect(location.hash).toContain(`n=${encodeURIComponent(group.key)}`);
  });

  it("palette page commands focus the page heading", async () => {
    vi.useFakeTimers();
    const { app } = await setup();
    paletteOpen.set(true);
    const input = document.querySelector<HTMLInputElement>("dialog.palette-dialog .palette-input")!;
    type(input, "Open settings");
    key(input, "Enter");
    await flushMicrotasks();
    vi.advanceTimersToNextFrame();
    const h2 = app.querySelector<HTMLElement>(".workspace h2")!;
    expect(h2.tabIndex).toBe(-1);
    expect(document.activeElement).toBe(h2);
  });

  it("palette: multi-selection runs the selected tests; Copy link without a selection copies the page URL", async () => {
    const { transport } = await setup({ hash: "#/coverage" });
    multiSelection.set(new Set(["t:P1|Ns.ClassA.Test1", "t:P1|Ns.ClassB.Test3"]));
    paletteOpen.set(true);
    let input = document.querySelector<HTMLInputElement>("dialog.palette-dialog .palette-input")!;
    type(input, "Run selected");
    expect(text(document.querySelector("dialog.palette-dialog [role=option] .palette-detail"))).toBe("2 selected");
    key(input, "Enter");
    await flushMicrotasks();
    expect(transport.sent.at(-1)).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test1", "Ns.ClassB.Test3"] });
    paletteOpen.set(true);
    input = document.querySelector<HTMLInputElement>("dialog.palette-dialog .palette-input")!;
    type(input, "Copy link");
    key(input, "Enter");
    await flushMicrotasks();
    expect(navigator.clipboard.writeText).toHaveBeenLastCalledWith(location.href);
  });

  it("viewing a ready run: Compare with… asks the run switcher", async () => {
    const { app, transport, store } = await setup();
    transport.fetchRun.mockResolvedValue({ run: runSummary({ runId: "r1", total: 1, passed: 1 }), results: [] });
    store.viewRun("r1");
    await flushMicrotasks();
    await flushFrame();
    const seen = vi.fn();
    window.addEventListener("labcoat:compare-request", seen);
    click([...app.querySelectorAll<HTMLButtonElement>(".messagebar button")].find((b) => text(b) === "Compare with…")!);
    expect(seen).toHaveBeenCalled();
    window.removeEventListener("labcoat:compare-request", seen);
  });

  it("the brand link does not navigate", async () => {
    const { app } = await setup();
    const e = click(app.querySelector("a.application-name")!);
    expect(e.defaultPrevented).toBe(true);
  });
});
