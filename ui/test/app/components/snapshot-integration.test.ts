// Verify snapshots in the tree (row pill, accessible name, context menu) and the command palette.
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { h, root } from "@eriklieben/labcoat-design-system";
import { commandPalette, paletteOpen, type PaletteActions } from "../../../src/components/command-palette";
import "../../../src/components/tree-pane";
import type { TestNode } from "../../../src/contract";
import { click, flushFrame, makeStore, testNode, text } from "../../helpers/app";
import { jsonResponse } from "../../helpers/app3";
import { finishAnimations, flushMicrotasks } from "../../helpers/dom";
import { menuItem, mountTree, resetTreeState, suiteTests } from "../../helpers/tree-pane";

const MSG = "VerifyException: Directory: /src\nNew:\n  - Received: ClassA.Snap.received.txt\n    Verified: ClassA.Snap.verified.txt\n";
const snapTest = (): TestNode => testNode({ id: "Ns.ClassA.Snap", outcome: "failed", errorMessage: MSG, hasSnapshotMismatch: true, filePath: "/src/A.cs", line: 30 });

let store: ReturnType<typeof makeStore>["store"];
beforeAll(() => {
  store = makeStore({ kind: "ws" }).store;
});
afterEach(() => {
  document.body.innerHTML = "";
  resetTreeState();
});

describe("tree", () => {
  it("marks tests with a pending snapshot and offers Accept / Reject snapshot", async () => {
    const posts: { url: string; body: unknown }[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (u: unknown, init?: RequestInit) => {
        posts.push({ url: String(u), body: JSON.parse(String(init?.body ?? "{}")) });
        return jsonResponse({ tests: [] });
      }),
    );
    const t = mountTree({ tests: [...suiteTests(), snapTest()] });
    t.el.querySelector<HTMLButtonElement>(".expand-all, [aria-label='Expand all']")?.click();
    await flushMicrotasks();
    const row = t.rows().find((r) => r.dataset.name?.startsWith("Snap"))!;
    expect(row).toBeDefined();
    expect(text(row.querySelector(".row-pill.snapshot"))).toBe("Snapshot");
    expect(row.dataset.name).toContain("snapshot mismatch");

    row.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: 10, clientY: 10 }));
    await flushMicrotasks();
    click(menuItem("Accept snapshot")!);
    await flushMicrotasks();
    expect(posts.at(-1)).toEqual({ url: expect.stringContaining("/api/snapshots/accept"), body: { projectId: "P1", id: "Ns.ClassA.Snap" } });

    row.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: 10, clientY: 10 }));
    await flushMicrotasks();
    click(menuItem("Reject snapshot")!);
    await flushMicrotasks();
    expect(posts.at(-1)!.url).toContain("/api/snapshots/reject");

    // a plain failed test has no snapshot entries
    await finishAnimations();
    const beta = t.rows().find((r) => r.dataset.name?.startsWith("Beta"))!;
    beta.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: 10, clientY: 10 }));
    await flushMicrotasks();
    expect(menuItem("Accept snapshot")).toBeUndefined();
    t.dispose();
  });
});

describe("command palette", () => {
  const actions = (): PaletteActions =>
    ({
      runAll: vi.fn(), runFailed: vi.fn(), runSelected: undefined, selectedLabel: undefined, coverageSelected: undefined, toggleWatch: vi.fn(),
      toggleCoverage: vi.fn(), buildImpactMap: vi.fn(), cancel: vi.fn(), rediscover: vi.fn(), selectTest: vi.fn(), viewRun: vi.fn(), page: vi.fn(),
      toggleOutput: vi.fn(), showShortcuts: vi.fn(), sort: vi.fn(), currentSort: "default",
      sortLabels: { default: "Default order", slowest: "Slowest first", fastest: "Fastest first" },
    }) as PaletteActions;

  it("offers Accept all snapshots while tests have pending snapshots", async () => {
    const fetch = vi.fn(async (_u: unknown, _i?: RequestInit) => jsonResponse({ tests: [] }));
    vi.stubGlobal("fetch", fetch);
    store.apply({ type: "state", state: { projects: [], tests: [snapTest()], watch: false, coverageEnabled: false } });
    await flushFrame();
    let dispose!: () => void;
    root((d) => {
      dispose = d;
      document.body.append(h("div", null, commandPalette(actions)));
    });
    paletteOpen.set(true);
    const option = () => [...document.querySelectorAll<HTMLElement>("dialog.palette-dialog [role=option]")].find((o) => text(o.querySelector(".palette-label")) === "Accept all snapshots (1)");
    expect(option()).toBeDefined();
    click(option()!);
    await flushMicrotasks();
    expect(String(fetch.mock.calls.at(-1)![0])).toContain("/api/snapshots/accept");
    paletteOpen.set(false);

    store.apply({ type: "testsUpdated", tests: [{ ...snapTest(), hasSnapshotMismatch: undefined }] });
    await flushFrame();
    paletteOpen.set(true);
    expect(option()).toBeUndefined();
    paletteOpen.set(false);
    dispose();
  });
});
