// run-diff.ts (used here) keeps module-level computeds over getStore(): one store for the whole file.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h } from "@eriklieben/labcoat-design-system";
import "../../../src/components/run-switcher";
import { requestCompare } from "../../../src/components/run-switcher";
import { comparison, comparisonOpen, LIVE } from "../../../src/components/run-diff";
import type { HistoryEntry, RunSummary } from "../../../src/contract";
import { localSettings, setSetting } from "../../../src/settings";
import { setStore } from "../../../src/store";
import { click, explorerState, flushFrame, key, makeStore, mount, runSummary, testNode, text } from "../../helpers/app";
import { finishAnimations, flushMicrotasks } from "../../helpers/dom";

const { store, transport } = makeStore({ open: false });

const runs: RunSummary[] = [
  runSummary({ runId: "r4", startedAt: "2026-10-04T10:00:00Z", passed: 1, failed: 2, total: 3, trigger: "watch", durationMs: 1500 }),
  runSummary({ runId: "s3", startedAt: "2026-10-03T10:00:00Z", passed: 1, total: 1, scope: "subset", trigger: "api" }),
  runSummary({ runId: "r2", startedAt: "2026-10-02T10:00:00Z", passed: 2, failed: 1, total: 3, trigger: "debug" }),
  runSummary({ runId: "r1", startedAt: "2026-10-01T10:00:00Z", passed: 3, total: 3, trigger: "mystery" as RunSummary["trigger"] }),
];
const he = (outcome: HistoryEntry["outcome"], runId: string): HistoryEntry => ({ outcome, runId, at: "2026-10-01T10:00:00Z" });

async function setState(p: Parameters<typeof explorerState>[0] = {}) {
  store.apply({ type: "state", state: explorerState({ runs, ...p }) });
  await flushFrame();
}

/** the open (not leaving) run menu */
const menu = () => document.querySelector<HTMLElement>(".lc-popover.run-menu:not([inert])");
const items = () => [...(menu()?.querySelectorAll<HTMLButtonElement>(".run-menu-item") ?? [])];
const runItems = () => [...(menu()?.querySelectorAll<HTMLButtonElement>(".run-menu-run") ?? [])];

function switcher() {
  const onJump = vi.fn();
  const el = mount(h("lc-run-switcher", { onJump }));
  const pill = el.querySelector<HTMLButtonElement>(".run-pill")!;
  return { el, pill, onJump };
}

beforeEach(async () => {
  transport.setStatus("open");
  store.viewRun(undefined);
  transport.fetchRun.mockReset();
  transport.fetchRun.mockResolvedValue(null);
  await setState();
});
afterEach(() => {
  localSettings.set({});
  comparison.set(undefined);
  comparisonOpen.set(false);
});

describe("<lc-run-switcher> pill", () => {
  it("Live with the number of stored (full) runs", () => {
    const { el, pill } = switcher();
    expect(el.classList.contains("run-switcher")).toBe(true);
    expect(text(pill)).toBe("Live· 3 runs");
    expect(pill.getAttribute("aria-haspopup")).toBe("menu");
    expect(pill.getAttribute("aria-expanded")).toBe("false");
    expect(pill.title).toBe("Connected: showing live results. 3 stored runs: pick one to view it");
    expect(pill.querySelector(".run-dot")!.className).toBe("run-dot live");
  });

  it("connecting → pulse; lost → Offline (static grey); back → one ripple", async () => {
    const { pill } = switcher();
    transport.setStatus("reconnecting", { retryInMs: 500 });
    await flushFrame();
    expect(text(pill.querySelector(".run-pill-label"))).toBe("Offline");
    expect(pill.classList.contains("offline")).toBe(true);
    const dot = pill.querySelector(".run-dot")!;
    expect(dot.classList.contains("offline")).toBe(true);
    expect(pill.title).toContain("Not connected to the Labcoat host");
    transport.setStatus("open");
    await flushFrame();
    expect(text(pill.querySelector(".run-pill-label"))).toBe("Live");
    expect(dot.classList.contains("ripple")).toBe(true);
  });

  it("never connected: Connecting… with a pulsing dot", async () => {
    const fresh = makeStore({ open: false });
    const el = mount(h("lc-run-switcher", {}));
    const pill = el.querySelector(".run-pill")!;
    expect(text(pill.querySelector(".run-pill-label"))).toBe("Connecting…");
    expect(pill.querySelector(".run-dot")!.classList.contains("lc-pulse")).toBe(true);
    expect((pill as HTMLElement).title).toContain("Connecting to the Labcoat host…");
    expect(fresh.store).not.toBe(store);
    el.remove();
    await flushMicrotasks();
    setStore(store); // back to the file's store
  });

  it("no runs: no count", async () => {
    await setState({ runs: [] });
    const { pill } = switcher();
    expect(text(pill)).toBe("Live");
    expect(pill.title).toContain("0 stored runs");
  });
});

describe("<lc-run-switcher> menu", () => {
  it("labels Run affected (git) runs as Affected", async () => {
    await setState({
      runs: [runSummary({ runId: "g1", passed: 2, total: 2, trigger: "user", affected: { strategy: "git", base: "branch", ref: "origin/main", changedFiles: [] } })],
    });
    const { pill } = switcher();
    click(pill);
    const t = runItems()[0].querySelector<HTMLElement>(".run-trigger")!;
    expect(text(t)).toBe("Affected");
    expect(t.title).toBe("Affected (branch vs origin/main): the tests affected by the changes (git)");
    expect(runItems()[0].getAttribute("aria-label") ?? runItems()[0].title).toBeTruthy();
  });

  it("opens on click: Live (checked), Compare with…, the full runs with details", async () => {
    const { pill } = switcher();
    click(pill);
    expect(pill.getAttribute("aria-expanded")).toBe("true");
    const m = menu()!;
    expect(m).toBeTruthy();
    const body = m.querySelector(".run-menu-body")!;
    expect(body.getAttribute("role")).toBe("menu");
    expect(body.getAttribute("aria-label")).toBe("Runs");
    const [live, compare] = items();
    expect(live.getAttribute("aria-checked")).toBe("true");
    expect(text(live)).toBe("Livecurrent results");
    expect(text(compare)).toBe("Compare with…");
    const rs = runItems();
    expect(rs).toHaveLength(3);
    expect(rs[0].getAttribute("role")).toBe("menuitemradio");
    expect(rs[0].getAttribute("aria-checked")).toBe("false");
    expect(text(rs[0].querySelector(".run-trigger"))).toBe("Watch");
    expect(text(rs[1].querySelector(".run-trigger"))).toBe("Debug");
    expect(text(rs[2].querySelector(".run-trigger"))).toBe("Run"); // unknown trigger → "Run"
    expect(text(rs[0].querySelector(".run-scope"))).toBe("Full · 3 tests");
    expect(text(rs[0].querySelector(".run-menu-counts"))).toBe("12");
    expect(rs[0].querySelector(".run-dot")!.classList.contains("tone-failed")).toBe(true);
    expect(rs[2].querySelector(".run-dot")!.classList.contains("tone-passed")).toBe(true);
    expect(text(rs[0].querySelector(".run-menu-sub"))).toContain("1.50 s");
    expect(rs[0].getAttribute("aria-label")).toContain("watch run, full, 3 tests, 1 passed, 2 failed, 1.50 s");
    expect(rs[0].title).toContain("Started by watch mode");
    // summary-based deltas: r4 has 1 more failure than r2, r2 1 more than r1
    expect(text(rs[0].querySelector(".run-delta"))).toBe("+1 failed");
    expect(text(rs[2].querySelector(".run-delta"))).toBe("");
    expect(text(m.querySelector(".run-menu-foot"))).toContain("partial runs are hidden (Settings)");
    await flushMicrotasks();
    expect(document.activeElement).toBe(live);
    // the pill toggles it closed
    click(pill);
    expect(menu()).toBeNull();
  });

  it("history-based deltas: new failures (highlighted) and fixes", async () => {
    await setState({
      tests: [
        testNode({ id: "A.B.One", history: [he("passed", "r1"), he("passed", "r2"), he("failed", "r4")] }),
        testNode({ id: "A.B.Two", history: [he("failed", "r1"), he("passed", "r2"), he("passed", "r4")] }),
        testNode({ id: "A.B.Three", history: [he("passed", "r1"), he("failed", "r2"), he("passed", "r4")] }),
      ],
    });
    const { pill } = switcher();
    click(pill);
    const rs = runItems();
    expect(text(rs[0].querySelector(".run-delta"))).toBe("+1 failed1 fixed");
    expect(rs[0].classList.contains("first-fail")).toBe(true);
    expect(rs[0].getAttribute("aria-label")).toContain("1 new failure, 1 fixed");
    expect(rs[0].title).toContain("1 new failure · 1 fixed");
    expect(text(rs[1].querySelector(".run-delta"))).toBe("+1 failed1 fixed");
    expect(rs[2].classList.contains("first-fail")).toBe(false);
  });

  it("fewer failures vs. the previous full run", async () => {
    await setState({ runs: [runSummary({ runId: "b", failed: 0, passed: 3, total: 3 }), runSummary({ runId: "a", failed: 2, passed: 1, total: 3 })] });
    const { pill } = switcher();
    click(pill);
    expect(text(runItems()[0].querySelector(".run-delta"))).toBe("2 fewer failed");
  });

  it("partial runs are listed with the setting; only-partial and empty lists say so", async () => {
    setSetting("partialRunsInHistory", true);
    const { pill } = switcher();
    click(pill);
    const rs = runItems();
    expect(rs).toHaveLength(4);
    expect(text(rs[1].querySelector(".run-scope"))).toBe("Partial · 1 test");
    expect(rs[1].querySelector(".run-scope")!.classList.contains("partial")).toBe(true);
    expect(text(rs[1].querySelector(".run-trigger"))).toBe("API");
    expect(text(rs[1].querySelector(".run-delta"))).toBe("");
    expect(text(menu()!.querySelector(".run-menu-foot"))).not.toContain("partial runs are hidden");
    localSettings.set({});
    await setState({ runs: [runs[1]] });
    expect(text(menu()!.querySelector(".run-menu-empty"))).toBe("Only partial runs so far (Settings → History)");
    await setState({ runs: [] });
    expect(text(menu()!.querySelector(".run-menu-empty"))).toBe("No past runs yet");
    expect(items()[1].disabled).toBe(true); // Compare with… needs runs
  });

  it("picking a run views it read-only (pill shows it); Live goes back", async () => {
    transport.fetchRun.mockResolvedValue({ run: runs[2], results: [] });
    const { pill } = switcher();
    click(pill);
    click(runItems()[1]);
    expect(menu()).toBeNull();
    expect(document.activeElement).toBe(pill);
    expect(transport.fetchRun).toHaveBeenCalledWith("r2");
    await flushMicrotasks();
    await flushFrame();
    expect(store.get().viewing?.run.runId).toBe("r2");
    expect(pill.classList.contains("past")).toBe(true);
    expect(pill.title).toMatch(/^Viewing the run from .* \(2 passed, 1 failed\)$/);
    expect(pill.querySelector(".run-dot")!.classList.contains("tone-failed")).toBe(true);
    click(pill);
    expect(runItems()[1].getAttribute("aria-checked")).toBe("true");
    expect(items()[0].getAttribute("aria-checked")).toBe("false");
    await flushMicrotasks();
    expect(document.activeElement).toBe(runItems()[1]);
    click(items()[0]); // Live
    await flushFrame();
    expect(store.get().viewing).toBeUndefined();
    expect(pill.classList.contains("past")).toBe(false);
  });

  it("keyboard: ArrowDown opens; arrows/Home/End move; Escape closes (not reaching the app) and focuses the pill; Tab closes", async () => {
    const { pill } = switcher();
    key(pill, "Enter");
    expect(menu()).toBeNull();
    const e = key(pill, "ArrowDown");
    expect(e.defaultPrevented).toBe(true);
    await flushMicrotasks();
    const all = items();
    expect(document.activeElement).toBe(all[0]);
    key(all[0], "ArrowDown");
    expect(document.activeElement).toBe(all[1]);
    key(all[1], "ArrowUp");
    expect(document.activeElement).toBe(all[0]);
    key(all[0], "End");
    expect(document.activeElement).toBe(all[all.length - 1]);
    key(all[all.length - 1], "Home");
    expect(document.activeElement).toBe(all[0]);
    key(all[0], "x"); // ignored
    const app = vi.fn();
    window.addEventListener("keydown", app);
    key(all[0], "Escape");
    window.removeEventListener("keydown", app);
    expect(app).not.toHaveBeenCalled();
    expect(menu()).toBeNull();
    expect(document.activeElement).toBe(pill);
    click(pill);
    key(items()[0], "Tab");
    expect(menu()).toBeNull();
    // ArrowDown on an open menu's pill does nothing
    click(pill);
    expect(key(pill, "ArrowDown").defaultPrevented).toBe(false);
  });

  it("closes on window resize and outside pointerdown", async () => {
    const { pill } = switcher();
    click(pill);
    window.dispatchEvent(new Event("resize"));
    expect(menu()).toBeNull();
    click(pill);
    document.body.dispatchEvent(new Event("pointerdown", { bubbles: true }));
    expect(menu()).toBeNull();
    await finishAnimations();
    expect(document.querySelector(".run-menu")).toBeNull();
  });
});

describe("<lc-run-switcher> compare", () => {
  it("Compare with… → pick a run → the run diff sheet; jump and close", async () => {
    transport.fetchRun.mockImplementation(async (id: string) => ({ run: runs.find((r) => r.runId === id)!, results: [] }));
    await setState({ tests: [testNode({ id: "A.B.C", outcome: "passed" })] });
    const { pill, el, onJump } = switcher();
    click(pill);
    click(items()[1]);
    const body = menu()!.querySelector(".run-menu-body")!;
    expect(body.getAttribute("aria-label")).toBe("Compare with");
    expect(menu()!.classList.contains("picking")).toBe(true);
    expect(text(menu()!.querySelector(".run-menu-pick-title"))).toBe("Compare Live with…");
    expect(text(menu()!.querySelector(".run-menu-foot"))).toBe("Pick the run to compare with. The older run is the base.");
    const live = [...menu()!.querySelectorAll<HTMLButtonElement>(".run-menu-item")].find((b) => text(b).startsWith("Live"))!;
    expect(live.disabled).toBe(true); // the shown side can't be picked
    // Escape leaves picking mode first
    key(live, "Escape");
    expect(menu()!.querySelector(".run-menu-body")!.getAttribute("aria-label")).toBe("Runs");
    click(items()[1]);
    // the back button too
    click(menu()!.querySelector(".run-menu-back")!);
    expect(menu()!.classList.contains("picking")).toBe(false);
    click(items()[1]);
    click(runItems()[0]);
    expect(menu()).toBeNull();
    expect(comparison()).toEqual({ base: "r4", target: LIVE });
    expect(comparisonOpen()).toBe(true);
    const sheet = el.querySelector("lc-run-diff")!;
    expect(sheet).toBeTruthy();
    // a row jump selects the test in the tree and closes the sheet
    (sheet as unknown as { onJump: (k: string) => void }).onJump("P1|A.B.C");
    expect(onJump).toHaveBeenCalledWith("t:P1|A.B.C");
    expect(comparisonOpen()).toBe(false);
    // "Show comparison" reopens it from the menu
    click(pill);
    const show = items().find((b) => text(b).startsWith("Show comparison"))!;
    expect(text(show)).toMatch(/^Show comparison .* → Live$/);
    click(show);
    expect(comparisonOpen()).toBe(true);
    const sheet2 = el.querySelector("lc-run-diff")!;
    click(sheet2.querySelector(".run-diff-close")!);
    expect(comparisonOpen()).toBe(false);
    expect(document.activeElement).toBe(pill);
  });

  it("picking the same run as the viewed one is not offered; picking from a viewed run compares with it", async () => {
    transport.fetchRun.mockImplementation(async (id: string) => ({ run: runs.find((r) => r.runId === id)!, results: [] }));
    store.viewRun("r2");
    await flushMicrotasks();
    await flushFrame();
    switcher();
    requestCompare();
    expect(menu()!.classList.contains("picking")).toBe(true);
    expect(text(menu()!.querySelector(".run-menu-pick-title"))).toMatch(/^Compare .+ with…$/);
    const rs = runItems();
    expect(rs[1].disabled).toBe(true); // r2 itself
    expect(rs[1].getAttribute("aria-checked")).toBe("false");
    click(rs[2]); // r1 (older) → base
    expect(comparison()).toEqual({ base: "r1", target: "r2" });
  });
});

describe("<lc-run-switcher> export", () => {
  const exportItems = () => items().filter((b) => text(b).startsWith("Export as"));
  function stubDownload() {
    const fetch = vi.fn(async () => ({ ok: true, status: 200, headers: new Headers(), blob: async () => new Blob(["x"]) }) as unknown as Response);
    vi.stubGlobal("fetch", fetch);
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: vi.fn(() => "blob:x"), revokeObjectURL: vi.fn() }));
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    return fetch;
  }
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    document.querySelectorAll(".lc-toaster").forEach((t) => t.remove());
  });

  it("lists HTML report and JUnit XML for the latest run; keyboard reachable; downloads and refocuses the pill", async () => {
    const fetch = stubDownload();
    const { pill } = switcher();
    key(pill, "ArrowDown");
    await flushMicrotasks();
    const [html, junit] = exportItems();
    expect(text(html)).toBe("Export as HTML report");
    expect(text(junit)).toBe("Export as JUnit XML");
    expect(html.getAttribute("role")).toBe("menuitem");
    expect(html.title).toContain("Download the latest run as HTML report");
    expect(items()).toContain(junit); // part of the arrow-key navigation
    click(junit);
    expect(menu()).toBeNull();
    expect(document.activeElement).toBe(pill);
    await flushMicrotasks();
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/runs/latest/export?format=junit");
  });

  it("exports the viewed past run; disabled without stored runs; CLI runs are labelled", async () => {
    const fetch = stubDownload();
    await setState({ runs: [runSummary({ runId: "c1", startedAt: "2026-10-05T10:00:00Z", passed: 1, total: 1, trigger: "cli" as RunSummary["trigger"] }), ...runs] });
    const { pill } = switcher();
    click(pill);
    expect(text(runItems()[0].querySelector(".run-trigger"))).toBe("CLI");
    transport.fetchRun.mockResolvedValue({ run: runs[0], results: [] });
    click(runItems()[1]); // view r4
    await flushMicrotasks();
    await flushFrame();
    click(pill);
    expect(exportItems()[0].title).toContain("Download this run");
    click(exportItems()[0]);
    await flushMicrotasks();
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/runs/r4/export?format=html");
    store.viewRun(undefined);
    await setState({ runs: [] });
    click(pill);
    const disabled = [...(menu()?.querySelectorAll<HTMLButtonElement>(".run-menu-action") ?? [])].filter((b) => text(b).startsWith("Export as"));
    expect(disabled).toHaveLength(5);
    expect(disabled.every((b) => b.disabled)).toBe(true);
  });
});
