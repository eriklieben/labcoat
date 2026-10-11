import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system";
import type { SnapshotFile, TestNode, TestSnapshots } from "../../../src/contract";
import { lineDiff } from "../../../src/diff";
import { foldRows, sideBySideRows, snapshotModes, snapshotPanel, snapshotToolbarButton } from "../../../src/components/snapshot-view";
import { click, flushFrame, mount, testNode, text } from "../../helpers/app";
import { jsonResponse, resetStore } from "../../helpers/app3";
import { flushMicrotasks } from "../../helpers/dom";

const DIR = "/src/P1/Snaps";
const MSG = `VerifyException: Directory: ${DIR}\nNotEqual:\n  - Received: ClassA.Test1.received.txt\n    Verified: ClassA.Test1.verified.txt\n`;
const failed = (p: Partial<TestNode> = {}) => testNode({ outcome: "failed", errorMessage: MSG, hasSnapshotMismatch: true, lastRunAt: "2026-10-09T10:00:00Z", ...p });
const snap = (name: string, p: Partial<SnapshotFile> = {}): SnapshotFile => ({
  name: `${name}.verified.txt`,
  receivedPath: `${DIR}/${name}.received.txt`,
  verifiedPath: `${DIR}/${name}.verified.txt`,
  kind: "text",
  isNew: false,
  ...p,
});

/** fetch stub: snapshots list + file contents by path */
function stubHost(snapshots: SnapshotFile[], files: Record<string, string>, onPost?: (url: string, body: unknown) => unknown) {
  const fetch = vi.fn(async (u: unknown, init?: RequestInit) => {
    const url = String(u);
    if (init?.method === "POST") return jsonResponse(onPost?.(url, JSON.parse(String(init.body))) ?? { tests: [{ projectId: "P1", id: "Ns.ClassA.Test1", files: ["x"] }] });
    if (url.includes("/api/tests/snapshots")) return jsonResponse({ projectId: "P1", id: "Ns.ClassA.Test1", directory: DIR, snapshots } satisfies TestSnapshots);
    if (url.includes("/api/open-in-editor")) return jsonResponse({ ok: true });
    const path = decodeURIComponent(url.split("path=")[1] ?? "");
    return path in files ? jsonResponse(files[path]) : jsonResponse("", { status: 404 });
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
}

async function render(test: TestNode) {
  const t = signal(test);
  const el = mount(h("div", null, root(() => snapshotPanel(t))));
  await flushMicrotasks();
  await flushMicrotasks();
  return { el, t };
}

const btn = (el: Element, label: string) => [...el.querySelectorAll<HTMLButtonElement>("button")].find((b) => text(b) === label);

beforeEach(() => snapshotModes.set({ text: "unified", image: "split" }));
afterEach(() => {
  document.body.innerHTML = "";
  localStorage.clear();
});

describe("side-by-side rows", () => {
  it("pairs removed with added lines and keeps equal lines on both sides", () => {
    const rows = sideBySideRows(lineDiff("a\nb\nc", "a\nB\nx\nc"));
    expect(rows.map((r) => [r.left?.text ?? null, r.right?.text ?? null])).toEqual([
      ["a", "a"],
      ["b", "B"],
      [null, "x"],
      ["c", "c"],
    ]);
  });

  it("folds long unchanged runs", () => {
    const a = Array.from({ length: 20 }, (_, i) => `l${i}`);
    const b = [...a];
    b[10] = "changed";
    const folded = foldRows(sideBySideRows(lineDiff(a.join("\n"), b.join("\n"))));
    expect(folded.filter((r) => "fold" in r)).toEqual([{ fold: 7, from: 0 }, { fold: 6, from: 14 }]);
    expect(foldRows(sideBySideRows(lineDiff("a\nb", "a\nc")))).toHaveLength(2); // nothing to fold
  });
});

describe("snapshotPanel()", () => {
  it("renders nothing without a snapshot mismatch", async () => {
    await resetStore({ tests: [] });
    const { el } = await render(testNode({ outcome: "failed", errorMessage: "Assert.AreEqual failed" }));
    expect(el.querySelector(".snapshot-panel")).toBeNull();
  });

  it("shows a changed text snapshot as a unified or side-by-side diff; accept / reject / open", async () => {
    await resetStore({ tests: [failed()] });
    const s = snap("ClassA.Test1");
    const posts: { url: string; body: unknown }[] = [];
    const fetch = stubHost([s], { [s.verifiedPath]: "Id: 42\nStatus: sent\nTotal: 9.45", [s.receivedPath]: "Id: 42\nStatus: queued\nTotal: 9.45" }, (url, body) => {
      posts.push({ url, body });
      return undefined;
    });
    const { el } = await render(failed());
    const panel = el.querySelector(".snapshot-panel")!;
    expect(text(panel.querySelector("h3"))).toBe("Snapshot");
    expect(panel.getAttribute("aria-labelledby")).toBe(panel.querySelector("h3")!.id);
    expect(text(panel.querySelector(".snapshot-names"))).toContain("ClassA.Test1.verified.txt");
    expect(text(panel.querySelector(".snapshot-names"))).toContain("ClassA.Test1.received.txt");
    expect(text(panel.querySelector(".diff-stats"))).toBe("−1 +1");
    const unified = panel.querySelector(".diff-lines")!;
    expect(unified.getAttribute("aria-label")).toBe("Line diff: verified (−) and received (+)");
    expect(unified.getAttribute("tabindex")).toBe("0");
    expect(text(unified.querySelector(".diff-line.exp"))).toContain("verified: Status: sent");

    // side by side (remembered)
    click([...panel.querySelectorAll<HTMLButtonElement>(".snap-mode .segment")].find((b) => text(b) === "Side by side")!);
    await flushMicrotasks();
    const table = panel.querySelector("table.snap-split")!;
    expect(table).not.toBeNull();
    expect(panel.querySelector(".diff-lines")).toBeNull();
    expect(table.querySelectorAll("tbody tr")).toHaveLength(3);
    expect(text(table.querySelector("td.snap-code.exp"))).toContain("Status: sent");
    expect(text(table.querySelector("td.snap-code.act"))).toContain("Status: queued");
    expect(JSON.parse(localStorage.getItem("mste.snapshotModes")!)).toEqual({ text: "split", image: "split" });

    click(btn(panel, "Accept")!);
    await flushMicrotasks();
    expect(posts[0]).toEqual({ url: expect.stringContaining("/api/snapshots/accept"), body: { projectId: "P1", id: "Ns.ClassA.Test1", names: ["ClassA.Test1.verified.txt"] } });
    click(btn(panel, "Reject")!);
    await flushMicrotasks();
    expect(posts[1].url).toContain("/api/snapshots/reject");

    click(btn(panel, "Open both in editor")!);
    await flushMicrotasks();
    const opened = fetch.mock.calls.filter((c) => String(c[0]).includes("open-in-editor")).map((c) => JSON.parse(String((c[1] as RequestInit).body)).path);
    expect(opened).toEqual([s.verifiedPath, s.receivedPath]);
  });

  it("a new snapshot shows the received output and says accepting creates it", async () => {
    await resetStore({ tests: [failed()] });
    const s = snap("ClassA.Test1", { isNew: true });
    stubHost([s], { [s.receivedPath]: "{\n  Number: INV-1\n}" });
    const { el } = await render(failed());
    expect(text(el.querySelector(".snap-new"))).toBe("No verified snapshot yet — accept to create it.");
    expect(text(el.querySelector(".snap-badge-new"))).toBe("New");
    expect(text(el.querySelector(".lc-code"))).toContain("Number: INV-1");
    expect(btn(el, "Open in editor")).toBeDefined();
    expect(btn(el, "Accept")!.title).toContain("Create the verified snapshot");
  });

  it("images: side by side, onion skin and slider", async () => {
    await resetStore({ tests: [failed()] });
    const s = snap("ClassA.Chart", { kind: "image", name: "ClassA.Chart.verified.png", receivedPath: `${DIR}/ClassA.Chart.received.png`, verifiedPath: `${DIR}/ClassA.Chart.verified.png` });
    stubHost([s], {});
    const { el } = await render(failed());
    await flushMicrotasks();
    const imgs = () => [...el.querySelectorAll<HTMLImageElement>("img.snap-img")];
    expect(imgs().map((i) => i.alt)).toEqual(["Verified snapshot ClassA.Chart.verified.png", "Received snapshot ClassA.Chart.received.png"]);
    expect(imgs()[0].src).toContain("/api/snapshots/file?path=");
    expect(text(el.querySelector(".snap-badge-kind"))).toBe("Image");

    const mode = (label: string) => click([...el.querySelectorAll<HTMLButtonElement>(".snap-mode .segment")].find((b) => text(b) === label)!);
    mode("Onion skin");
    await flushMicrotasks();
    const range = el.querySelector<HTMLInputElement>(".snap-range input")!;
    expect(range.getAttribute("aria-label")).toBe("Opacity of the received image");
    range.value = "25";
    range.dispatchEvent(new Event("input"));
    expect(el.querySelector<HTMLElement>(".snap-overlay")!.style.getPropertyValue("--opacity")).toBe("0.25");
    expect(range.getAttribute("aria-valuetext")).toBe("25% received");

    mode("Slider");
    await flushMicrotasks();
    expect(el.querySelector(".snap-overlay.slider .snap-divider")).not.toBeNull();
    expect(el.querySelector<HTMLElement>(".snap-overlay")!.style.getPropertyValue("--amount")).toBe("25%");
    expect(el.querySelector(".snap-range input")!.getAttribute("aria-label")).toContain("Divider position");
  });

  it("binary snapshots, several snapshots, nothing left and errors", async () => {
    await resetStore({ tests: [failed(), failed({ id: "Ns.ClassB.Other" })] });
    const a = snap("ClassA.Blob", { kind: "binary", name: "ClassA.Blob.verified.bin" });
    const b = snap("ClassA.Test1", { isNew: true });
    stubHost([a, b], { [b.receivedPath]: "x" }, (url, body) => {
      (globalThis as unknown as { lastPost: unknown }).lastPost = { url, body };
      return { tests: [] };
    });
    const { el } = await render(failed());
    expect(text(el.querySelector(".snapshot-item .detail-note"))).toContain("Binary snapshot");
    expect(text(el.querySelector("h3"))).toBe("Snapshot (2)");
    click(btn(el, "Accept 2")!);
    await flushMicrotasks();
    expect((globalThis as unknown as { lastPost: { body: unknown } }).lastPost.body).toEqual({ projectId: "P1", id: "Ns.ClassA.Test1" });
    click(btn(el, "Accept all snapshots (2 tests)")!);
    await flushMicrotasks();
    expect((globalThis as unknown as { lastPost: { body: unknown } }).lastPost.body).toEqual({});

    document.body.innerHTML = "";
    stubHost([], {});
    const empty = await render(failed());
    expect(text(empty.el.querySelector(".snapshot-panel .detail-note"))).toContain("No received snapshot files are left");

    document.body.innerHTML = "";
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({}, { status: 500 })));
    const err = await render(failed());
    expect(text(err.el.querySelector(".snap-error"))).toContain("Could not load the snapshots: HTTP 500");

    // the received file disappeared between listing and reading it
    document.body.innerHTML = "";
    const gone = snap("ClassA.Gone");
    stubHost([gone], { [gone.verifiedPath]: "v" });
    const g = await render(failed());
    await flushMicrotasks();
    expect(text(g.el.querySelector(".snap-error"))).toContain("The received file is gone");
  });

  it("re-renders for a new result and hides while the test runs", async () => {
    await resetStore({ tests: [failed()] });
    const s = snap("ClassA.Test1");
    stubHost([s], { [s.verifiedPath]: "a", [s.receivedPath]: "a" });
    const { el, t } = await render(failed());
    expect(text(el.querySelector(".snap-identical"))).toContain("differ only in encoding");
    t.set(failed({ outcome: "running" }));
    await flushMicrotasks();
    expect(el.querySelector(".snapshot-panel")).toBeNull();
  });
});

describe("snapshotToolbarButton()", () => {
  it("appears while tests have pending snapshots and accepts them all", async () => {
    const { store } = await resetStore({ tests: [failed()] });
    const fetch = stubHost([], {});
    const el = mount(h("div", null, root(() => snapshotToolbarButton())));
    const b = el.querySelector<HTMLButtonElement>(".snap-toolbar-btn")!;
    expect(text(b)).toBe("Accept snapshots1 (1)"); // visible count + hidden " (1)"
    expect(b.title).toContain("1 test");
    click(b);
    expect(b.getAttribute("aria-disabled")).toBe("true");
    await flushMicrotasks();
    expect(fetch.mock.calls.some((c) => String(c[0]).includes("/api/snapshots/accept"))).toBe(true);
    store.apply({ type: "testsUpdated", tests: [failed({ hasSnapshotMismatch: undefined })] });
    await flushFrame();
    expect(el.querySelector(".snap-toolbar-btn")).toBeNull();
  });
});
