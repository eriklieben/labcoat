import { beforeEach, describe, expect, it, vi } from "vitest";
import { h, signal } from "@eriklieben/labcoat-design-system";
import type { ExplorerState, TestNode, TestOutputEvent } from "../../../src/contract";
import { AutoScroll, isNearBottom, LineRenderer } from "../../../src/components/live-console";
import "../../../src/components/detail-pane";
import { getLiveOutput, type LiveBuffer } from "../../../src/live-output";
import { getStore, testKey } from "../../../src/store";
import { buildTree, findNode, type TreeNode } from "../../../src/tree";
import { click, flushFrame, mount, testNode, text } from "../../helpers/app";
import { jsonResponse, resetStore } from "../../helpers/app3";
import { flushMicrotasks } from "../../helpers/dom";

const T1 = "Ns.ClassA.Test1";

const out = (text: string, seq: number, p: Partial<TestOutputEvent> = {}): TestOutputEvent => ({
  type: "testOutput",
  projectId: "P1",
  id: T1,
  stream: "stdout",
  text,
  seq,
  ...p,
});

function nodeOf(k: string): TreeNode {
  const s = getStore().get();
  const n = findNode(buildTree(s.projects, s.tests), k);
  if (!n) throw new Error(`no node ${k}`);
  return n;
}

async function pane(state: Partial<ExplorerState>) {
  await resetStore(state);
  const t = state.tests![0];
  const nodeKey = `t:${testKey(t)}`;
  const node = signal<TreeNode | undefined>(nodeOf(nodeKey));
  const el = mount(h("lc-detail-pane", { node, onRun: vi.fn(), onSelect: vi.fn() }));
  await flushMicrotasks();
  const refresh = async () => {
    await flushFrame();
    node.set(nodeOf(nodeKey));
    await flushFrame();
    await flushMicrotasks();
  };
  return { el, refresh };
}

const liveLines = (el: Element) => [...el.querySelectorAll(".live-section .console-line .console-text")].map((x) => x.textContent);

describe("isNearBottom / AutoScroll", () => {
  it("sticks to the bottom until the user scrolls up, counting unseen lines", () => {
    expect(isNearBottom({ scrollTop: 880, scrollHeight: 1000, clientHeight: 100 })).toBe(true);
    expect(isNearBottom({ scrollTop: 500, scrollHeight: 1000, clientHeight: 100 })).toBe(false);
    const a = new AutoScroll();
    expect(a.onAppend(3)).toBe(true);
    a.onScroll({ scrollTop: 0, scrollHeight: 1000, clientHeight: 100 });
    expect(a.stick()).toBe(false);
    expect(a.onAppend(2)).toBe(false);
    expect(a.onAppend(1)).toBe(false);
    expect(a.unseen()).toBe(3);
    a.onScroll({ scrollTop: 900, scrollHeight: 1000, clientHeight: 100 }); // back at the bottom
    expect(a.stick()).toBe(true);
    expect(a.unseen()).toBe(0);
    a.onScroll({ scrollTop: 0, scrollHeight: 1000, clientHeight: 100 });
    a.onAppend(4);
    a.jump();
    expect(a.stick()).toBe(true);
    expect(a.unseen()).toBe(0);
  });
});

describe("LineRenderer", () => {
  const buffer = (texts: string[], p: Partial<LiveBuffer> = {}): LiveBuffer => ({
    key: "k",
    lines: texts.map((t) => ({ text: t, stream: "stdout" as const })),
    seq: 1,
    chars: 0,
    headLines: 0,
    omitted: 0,
    rev: 0,
    finished: false,
    ...p,
  });

  it("appends new lines instead of rebuilding, and rebuilds after a structural change", () => {
    const el = document.createElement("div");
    const r = new LineRenderer(el);
    const b = buffer(["a", "b"]);
    expect(r.render(b)).toBe(2);
    const first = el.firstElementChild;
    b.lines.push({ text: "c", stream: "host", shared: true });
    b.lines[1].text = "b+"; // the open last line was continued
    expect(r.render(b)).toBe(1);
    expect(el.firstElementChild).toBe(first); // not rebuilt
    expect([...el.querySelectorAll(".console-text")].map((x) => x.textContent)).toEqual(["a", "b+", "c"]);
    expect(el.querySelector(".live-tag-shared")).not.toBeNull();
    // the cap dropped lines from the middle: full rebuild with the omitted marker
    const capped = buffer(["a", "z"], { rev: 1, omitted: 5000, headLines: 1 });
    r.render(capped);
    expect(text(el)).toContain("5,000 characters omitted");
    expect(r.render(undefined)).toBe(0);
    expect(el.children).toHaveLength(0);
  });
});

describe("<lc-live-console> in the detail pane", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ error: "not running" }, { status: 404 })));
  });

  it("shows output while the test runs, then only the live-only lines, with one polite summary", async () => {
    const running = testNode({ id: T1, outcome: "running" });
    const { el, refresh } = await pane({ tests: [running] });
    getLiveOutput(); // subscribed (the pane created it already)
    expect(text(el.querySelector(".live-section"))).toContain("Waiting for output…");
    expect(el.querySelector(".live-indicator")).not.toBeNull();
    expect(el.querySelector(".live-scroll")?.getAttribute("aria-live")).toBeNull(); // not a live region

    getStore().apply(out("captured 1\n", 1));
    getStore().apply(out("display \u001b[32mgreen\u001b[0m\n", 2, { stream: "host" }));
    await refresh();
    expect(liveLines(el)).toEqual(["captured 1", "display green"]);
    expect(el.querySelector(".live-section .console-text span")?.getAttribute("style")).toContain("--lc-ansi-green");

    // finished: the final output is authoritative, the host line that is not in it stays as "Live-only output"
    getStore().apply({ type: "testsUpdated", tests: [testNode({ id: T1, outcome: "passed", stdOut: "captured 1\n", lastRunAt: "2026-10-09T10:00:00Z" })] });
    await refresh();
    const sections = [...el.querySelectorAll(".detail-section h3")].map((x) => x.textContent);
    expect(sections).toContain("Console output");
    expect(sections).toContain("Live-only output");
    expect(liveLines(el)).toEqual(["display green"]);
    expect(el.querySelector(".live-console [role=status]")?.textContent).toBe("Test finished; 2 lines of live output.");
  });

  it("offers 'Jump to latest' when the user scrolled up", async () => {
    const { el, refresh } = await pane({ tests: [testNode({ id: T1, outcome: "running" })] });
    getStore().apply(out("1\n", 1));
    await refresh();
    const scroller = el.querySelector<HTMLElement>(".live-scroll")!;
    Object.defineProperty(scroller, "scrollHeight", { value: 1000, configurable: true });
    Object.defineProperty(scroller, "clientHeight", { value: 100, configurable: true });
    scroller.scrollTop = 0;
    scroller.dispatchEvent(new Event("scroll"));
    getStore().apply(out("2\n3\n", 2));
    await refresh();
    const jump = el.querySelector<HTMLButtonElement>(".live-jump");
    expect(text(jump)).toBe("Jump to latest (2 new)");
    click(jump!);
    await flushMicrotasks();
    expect(el.querySelector(".live-jump")).toBeNull();
  });

  it("toggles follow and the Output mirror", async () => {
    const { el } = await pane({ tests: [testNode({ id: T1, outcome: "running" })] });
    const follow = [...el.querySelectorAll<HTMLButtonElement>(".live-toggle")].find((b) => text(b).includes("Follow"))!;
    click(follow);
    expect(follow.getAttribute("aria-pressed")).toBe("true");
    expect(getLiveOutput().follow()).toBe(true);
    click(follow);
    expect(getLiveOutput().follow()).toBe(false);
    const mirror = [...el.querySelectorAll<HTMLButtonElement>(".live-toggle")].find((b) => text(b).includes("Output"))!;
    click(mirror);
    expect(getLiveOutput().mirror()).toBe(true);
    click(mirror);
  });

  it("seeds from GET /api/tests/live-output when the test was already running", async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ projectId: "P1", id: T1, seq: 2, chunks: [{ stream: "stdout", text: "earlier\n" }] }));
    vi.stubGlobal("fetch", fetchMock);
    const t: TestNode = testNode({ id: "Ns.ClassA.Test2", outcome: "running" });
    const { el, refresh } = await pane({ tests: [t] });
    await refresh();
    expect(String((fetchMock.mock.calls[0] as unknown[])[0])).toContain("/api/tests/live-output?projectId=P1&id=Ns.ClassA.Test2");
    expect(liveLines(el)).toEqual(["earlier"]);
  });
});
