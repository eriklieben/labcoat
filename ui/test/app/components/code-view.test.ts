import { beforeEach, describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system";
import type { FileCoverage, TestNode } from "../../../src/contract";
import {
  editorAnchor,
  editorId,
  editorName,
  fetchSourceCached,
  highlightLines,
  isMyCode,
  isPathmapped,
  normPath,
  openInEditor,
  parseFrames,
  samePath,
  splitTags,
  stripTags,
  withoutArgs,
  type Annotation,
} from "../../../src/components/code-view";
import { onSelectTestRequest } from "../../../src/components/coverage-line-tests";
import { setSetting } from "../../../src/settings";
import { testKey } from "../../../src/store";
import { click, fileCoverage, flushFrame, key, mount, testNode, text } from "../../helpers/app";
import { jsonResponse, resetStore } from "../../helpers/app3";
import { flushMicrotasks } from "../../helpers/dom";

let fileSeq = 0;
/** a fresh path per test: fetchSourceCached caches by path for 15 s */
const freshPath = (ext = "cs") => `/src/Lib/File${++fileSeq}.${ext}`;

const SOURCE = [
  "namespace Ns;", // 1
  "public class ClassA", // 2
  "{", // 3
  "    [TestMethod]", // 4
  "    public void Test1()", // 5
  "    {", // 6
  "        Assert.AreEqual(1, 2);", // 7
  "    }", // 8
  "    [DataRow(1)]", // 9
  "    [DataRow(2)]", // 10
  "    public void Rows(int x)", // 11
  "    {", // 12
  "    }", // 13
  "}", // 14
].join("\n");

interface ViewProps {
  path?: string | (() => string | undefined);
  range?: { from: number; to: number };
  focusLine?: number | (() => number | undefined);
  focusNonce?: () => number | undefined;
  coverage?: FileCoverage;
  showCoverage?: boolean | (() => boolean);
  annotations?: Annotation[] | (() => Annotation[]);
  markers?: number[];
  viewClass?: string;
  onLineHits?: (line: number, anchor: HTMLElement) => void;
}

async function view(props: ViewProps, source: string | Error = SOURCE) {
  const transport = (await import("../../../src/store")).getStore().transport as unknown as { fetchSource: ReturnType<typeof vi.fn> };
  transport.fetchSource.mockImplementation(async () => {
    if (source instanceof Error) throw source;
    return source;
  });
  const el = mount(h("lc-code-view", props as never));
  await flushMicrotasks();
  await flushFrame();
  return el;
}
const row = (el: Element, n: number) => el.querySelector<HTMLElement>(`[data-line="${n}"]`)!;

describe("source helpers", () => {
  it("fetchSourceCached caches per path for 15 s and forgets failures", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    const f = vi.fn(async (p: string) => `text of ${p}`);
    const p = freshPath();
    expect(await fetchSourceCached(p, f)).toBe(`text of ${p}`);
    await fetchSourceCached(p, f);
    expect(f).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(15_001);
    await fetchSourceCached(p, f);
    expect(f).toHaveBeenCalledTimes(2);

    const bad = vi.fn().mockRejectedValueOnce(new Error("404")).mockResolvedValueOnce("ok");
    const q = freshPath();
    await expect(fetchSourceCached(q, bad)).rejects.toThrow("404");
    await flushMicrotasks();
    expect(await fetchSourceCached(q, bad)).toBe("ok");
  });

  it("highlightLines escapes non-C# files line by line", () => {
    expect(highlightLines("a < b\r\nc & d\n", "x.txt")).toEqual(["a &lt; b", "c &amp; d"]);
  });

  it("highlightLines highlights C# and keeps spans balanced across lines", () => {
    const lines = highlightLines('/* multi\nline */ var s = "x";', "a.cs");
    expect(lines).toHaveLength(2);
    for (const l of lines) expect(l.match(/<span/g) ?? []).toHaveLength((l.match(/<\/span>/g) ?? []).length);
    expect(lines[0]).toContain("hljs-comment");
    expect(lines[1].startsWith('<span class="hljs-comment">')).toBe(true);
    expect(lines[1]).toContain("hljs-string");
  });

  it("parseFrames: the method name is the shortest one, the path may be a lone whitespace character", () => {
    expect(parseFrames("at  A  in  B in C :line  4 ")).toEqual([{ method: "A", path: "B in C ", line: 4 }]);
    expect(parseFrames("at  in x:line 1")).toEqual([{ method: "", path: "x", line: 1 }]);
    expect(parseFrames("at A in  :line 1")).toEqual([{ method: "A", path: " ", line: 1 }]);
    expect(parseFrames("at A in :line 1")).toEqual([]);
  });

  it("parseFrames matches the backtracking pattern it replaces", () => {
    const old = /^\s*at\s+(.*?)\s+in\s+(.+?):line\s+(\d+)\s*$/;
    const parts = ["at", " ", "  ", "\t", "\r", "in", " in ", "x", ":line", ":line ", "1", "(", "."];
    let seed = 11;
    const next = () => (seed = (seed * 48271) % 2147483647);
    for (let k = 0; k < 20_000; k++) {
      let s = k % 2 ? " at " : "";
      for (let n = next() % 10; n > 0; n--) s += parts[next() % parts.length];
      const m = old.exec(s);
      expect(parseFrames(s), JSON.stringify(s)).toEqual(m ? [{ method: m[1], path: m[2], line: Number(m[3]) }] : []);
    }
  });

  it("splitTags / stripTags split like /(<[^>]+>)/ (tags at odd indexes)", () => {
    for (const s of ["", "a<b>c</b>", "<>x<", "a<<b>>c", "x < y", "<i>", "<a><b>"]) {
      expect(splitTags(s)).toEqual(s.split(/(<[^>]+>)/));
      expect(stripTags(s)).toBe(s.replace(/<[^>]+>/g, ""));
    }
  });

  it("withoutArgs drops the argument list and the whitespace before it", () => {
    expect(withoutArgs("Add (1, 2)")).toBe("Add");
    expect(withoutArgs("Add")).toBe("Add");
    expect(withoutArgs("A(1)\nB (2)")).toBe("A(1)\nB");
    expect(withoutArgs("A(1)\n")).toBe("A(1)\n");
  });

  it("parseFrames reads .NET stack frames", () => {
    expect(parseFrames(undefined)).toEqual([]);
    expect(parseFrames("   at Ns.A.T() in /src/A.cs:line 12\r\n   at System.X()\nnoise")).toEqual([{ method: "Ns.A.T()", path: "/src/A.cs", line: 12 }]);
  });

  it("paths: normalize, compare, pathmap and 'my code'", () => {
    expect(normPath("C:\\Src\\A.cs")).toBe("c:/src/a.cs");
    expect(samePath("C:\\Src\\A.cs", "c:/src/a.cs")).toBe(true);
    expect(isPathmapped("/_/src/A.cs")).toBe(true);
    expect(isPathmapped("\\_1\\src\\A.cs")).toBe(true);
    expect(isPathmapped("/src/A.cs")).toBe(false);
    expect(isMyCode("/_/src/A.cs", undefined)).toBe(false);
    expect(isMyCode("/anything.cs", [])).toBe(true);
    expect(isMyCode("/repo/src/A.cs", ["/repo/"])).toBe(true);
    expect(isMyCode("/repo", ["/repo"])).toBe(true);
    expect(isMyCode("/repository/A.cs", ["/repo"])).toBe(false);
  });

  it("editorId / editorName follow Settings → editor", () => {
    expect(editorId()).toBe("vscode");
    expect(editorName()).toBe("VS Code");
    setSetting("editor", "rider");
    expect(editorName()).toBe("Rider");
    setSetting("editor", "visualstudio");
    expect(editorId()).toBe("visualstudio");
    setSetting("editor", "vscode");
  });
});

describe("openInEditor() / editorAnchor()", () => {
  let hrefs: string[];
  beforeEach(async () => {
    await resetStore();
    hrefs = [];
    vi.spyOn(location, "href", "set").mockImplementation((v: string) => void hrefs.push(v));
  });

  it("posts to the host and stops there when it launched the editor", async () => {
    const f = vi.fn(async () => jsonResponse({ ok: true }));
    vi.stubGlobal("fetch", f);
    await openInEditor("/src/A.cs", 3, 4);
    const [url, init] = f.mock.calls[0] as unknown as [URL, RequestInit];
    expect(String(url)).toMatch(/\/api\/open-in-editor$/);
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({ editor: "vscode", path: "/src/A.cs", line: 3, column: 4 });
    expect(hrefs).toEqual([]);
  });

  it("falls back to the vscode:// link when the host can't (VS Code only)", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ ok: false, message: "no code" }, { status: 500 })));
    await openInEditor("/src/A.cs", 3);
    expect(hrefs).toEqual(["vscode://file/src/A.cs:3"]);
    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject(new Error("offline"))));
    await openInEditor("C:\\src\\B.cs");
    expect(hrefs[1]).toBe("vscode://file/C:/src/B.cs");
    setSetting("editor", "rider");
    await openInEditor("/src/A.cs", 3);
    expect(hrefs).toHaveLength(2);
    setSetting("editor", "vscode");
  });

  it("a bad JSON body still counts as success when the response is ok", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => { throw new Error("x"); } }) as unknown as Response));
    await openInEditor("/src/A.cs");
    expect(hrefs).toEqual([]);
  });

  it("with the mock backend: the vscode:// link directly", async () => {
    await resetStore({}, { kind: "mock" });
    const f = vi.fn();
    vi.stubGlobal("fetch", f);
    await openInEditor("/src/A.cs", 9);
    expect(f).not.toHaveBeenCalled();
    expect(hrefs).toEqual(["vscode://file/src/A.cs:9"]);
  });

  it("editorAnchor keeps the vscode:// href; a plain click goes through the host, modified clicks don't", async () => {
    const f = vi.fn(async () => jsonResponse({ ok: true }));
    vi.stubGlobal("fetch", f);
    const a = root(() => editorAnchor({ class: "lnk", path: () => "/src/A.cs", line: () => 7, "aria-label": (n) => `Open in ${n}` }, "A.cs"));
    expect(a.getAttribute("href")).toBe("vscode://file/src/A.cs:7");
    expect(a.title).toBe("Open in VS Code");
    expect(a.getAttribute("aria-label")).toBe("Open in VS Code");
    const plain = click(a);
    expect(plain.defaultPrevented).toBe(true);
    await flushMicrotasks();
    expect(f).toHaveBeenCalledTimes(1);
    const ctrl = click(a, { ctrlKey: true });
    expect(ctrl.defaultPrevented).toBe(false);
    expect(f).toHaveBeenCalledTimes(1);
    const titled = root(() => editorAnchor({ class: "x", path: () => "/a", title: (n) => `go ${n}` }));
    expect(titled.title).toBe("go VS Code");
    expect(titled.hasAttribute("aria-label")).toBe(false);
  });
});

describe("<lc-code-view>", () => {
  beforeEach(async () => {
    await resetStore();
  });

  it("shows a loading state, then the source as a table of rows with line numbers", async () => {
    const path = freshPath();
    const { getStore } = await import("../../../src/store");
    let resolve!: (s: string) => void;
    (getStore().transport.fetchSource as ReturnType<typeof vi.fn>).mockImplementation(() => new Promise<string>((r) => (resolve = r)));
    const el = mount(h("lc-code-view", { path, viewClass: "inline" }));
    expect(text(el)).toBe(`Loading ${path.split("/").pop()}…`);
    expect(el.querySelector(".spinner")).not.toBeNull();
    expect(el.hasAttribute("role")).toBe(false);
    resolve(SOURCE);
    await flushMicrotasks();
    expect(el.className).toBe("code-view source-code inline");
    expect(el.getAttribute("role")).toBe("table");
    expect(el.getAttribute("aria-label")).toBe(`Source of ${path.split("/").pop()}`);
    expect(el.tabIndex).toBe(0);
    expect(el.querySelectorAll(".src-line")).toHaveLength(14);
    expect(text(row(el, 5).querySelector(".src-ln"))).toBe("5");
    expect(row(el, 5).getAttribute("role")).toBe("row");
    expect(row(el, 1).querySelector(".src-text")!.classList.contains("hljs")).toBe(true);
  });

  it("without a path: 'Source not available'", async () => {
    const el = await view({ path: undefined });
    expect(el.classList.contains("code-unavailable")).toBe(true);
    expect(text(el.querySelector(".empty-title"))).toBe("Source not available");
    expect(text(el.querySelector(".empty-sub"))).toBe("Couldn't load the file (No file).");
  });

  it("explains 403 / 404 and offers 'Open in editor' (not for pathmapped paths)", async () => {
    const forbidden = await view({ path: freshPath(), focusLine: 3 }, Object.assign(new Error("Forbidden"), { status: 403 }));
    expect(text(forbidden.querySelector(".empty-sub"))).toMatch(/doesn't serve this file/);
    const a = forbidden.querySelector("a.btn")!;
    expect(text(a)).toBe("Open in VS Code");
    expect(a.getAttribute("href")).toMatch(/:3$/);

    const gone = await view({ path: freshPath() }, new Error("404 Not Found"));
    expect(text(gone.querySelector(".empty-sub"))).toBe("The file no longer exists at this path.");

    const mapped = await view({ path: `/_/src/X${++fileSeq}.cs` }, new Error("500 boom"));
    expect(text(mapped.querySelector(".empty-sub"))).toBe("Couldn't load the file (500 boom).");
    expect(mapped.querySelector("a")).toBeNull();
  });

  it("emphasises the range and marks error lines with annotations", async () => {
    const el = await view({
      path: freshPath(),
      range: { from: 4, to: 8 },
      annotations: [{ line: 7, title: "AssertFailedException", message: "Expected <1>" }, { line: 7, title: "Second" }],
      markers: [5],
    });
    expect(row(el, 3).classList.contains("in-range")).toBe(false);
    expect([...row(el, 4).classList]).toEqual(expect.arrayContaining(["in-range", "range-start"]));
    expect(row(el, 8).classList.contains("range-end")).toBe(true);
    expect(row(el, 5).classList.contains("err-line")).toBe(true);
    expect(row(el, 5).querySelector(".src-mark")!.getAttribute("aria-hidden")).toBe("false");
    expect(row(el, 6).querySelector(".src-mark")!.getAttribute("aria-hidden")).toBe("true");
    expect(row(el, 7).classList.contains("err-line")).toBe(true);
    const notes = [...el.querySelectorAll(".src-annotation")];
    expect(notes).toHaveLength(2);
    expect(text(notes[0].querySelector(".src-annotation-title"))).toBe("AssertFailedException");
    expect(notes[0].querySelector("pre")!.textContent).toBe("Expected <1>");
    expect(notes[1].classList.contains("short")).toBe(true);
    // P7: annotations slide in
    expect(notes[0].querySelector(".src-annotation-box")!.classList.contains("lc-enter-x")).toBe(true);
  });

  it("coverage overlay: hits, branch badges and line states only when shown", async () => {
    const coverage = fileCoverage({
      lines: [
        { line: 1, hits: 1 },
        { line: 2, hits: 0 },
        { line: 3, hits: 1500, branchesCovered: 1, branchesTotal: 2 },
        { line: 4, hits: 12000, branchesCovered: 2, branchesTotal: 2 },
        { line: 5, hits: 0, branchesTotal: 2 },
      ],
    });
    const show = signal(false);
    const el = await view({ path: freshPath(), coverage, showCoverage: show });
    expect(el.classList.contains("with-cov")).toBe(false);
    expect(el.querySelector(".src-hits")).toBeNull();
    show.set(true);
    expect(el.classList.contains("with-cov")).toBe(true);
    expect(row(el, 1).classList.contains("cov")).toBe(true);
    expect(row(el, 2).classList.contains("uncov")).toBe(true);
    expect(row(el, 3).classList.contains("partial")).toBe(true);
    expect(text(row(el, 1).querySelector(".src-hits"))).toBe("1×");
    expect(row(el, 1).querySelector(".src-hits")!.getAttribute("title")).toBe("1 hit");
    expect(text(row(el, 2).querySelector(".src-hits"))).toBe("0");
    expect(text(row(el, 3).querySelector(".src-hits"))).toBe("1.5k×");
    expect(text(row(el, 4).querySelector(".src-hits"))).toBe("12k×");
    expect(row(el, 6).querySelector(".src-hits")!.getAttribute("title")).toBe("Not executable");
    const b3 = row(el, 3).querySelector(".branch-badge")!;
    expect(b3.className).toBe("branch-badge partial");
    expect(b3.getAttribute("title")).toBe("1 of 2 branches taken");
    expect(row(el, 4).querySelector(".branch-badge")!.className).toBe("branch-badge cov");
    expect(text(row(el, 5).querySelector(".branch-badge"))).toBe("0/2");
    expect(row(el, 5).querySelector(".branch-badge")!.className).toBe("branch-badge uncov");
    // not clickable without onLineHits
    expect(el.querySelector(".src-hits-btn")).toBeNull();
  });

  it("hit counts become buttons with onLineHits", async () => {
    const onLineHits = vi.fn();
    const el = await view({ path: freshPath(), coverage: fileCoverage(), showCoverage: true, onLineHits });
    const btn = row(el, 2).querySelector<HTMLButtonElement>(".src-hits-btn")!;
    expect(btn.title).toBe("2 hits — which tests cover this line?");
    btn.click();
    expect(onLineHits).toHaveBeenCalledWith(2, btn);
    // clicks elsewhere don't call it
    click(row(el, 2).querySelector(".src-text")!);
    expect(onLineHits).toHaveBeenCalledTimes(1);
  });

  it("scrolls the focus line into view, jumping on a new file and gliding + locating on navigation", async () => {
    const nonce = signal<number | undefined>(undefined);
    const line = signal<number | undefined>(7);
    const el = await view({ path: freshPath(), focusLine: line, focusNonce: nonce, annotations: [{ line: 7, title: "Boom" }] });
    // first display of an error line: locator ring
    expect(row(el, 7).classList.contains("lc-locator")).toBe(true);
    row(el, 7).classList.remove("lc-locator");
    el.scrollTo = vi.fn() as never;
    line.set(11);
    nonce.set(1);
    await flushFrame();
    expect(el.scrollTo).toHaveBeenCalled();
    expect(row(el, 11).classList.contains("lc-locator")).toBe(true);
    // same line, no new nonce: no re-scroll
    (el.scrollTo as ReturnType<typeof vi.fn>).mockClear();
    line.set(11);
    await flushFrame();
    expect(el.scrollTo).not.toHaveBeenCalled();
  });

  it("without a focus line it scrolls to the top", async () => {
    const el = await view({ path: freshPath() });
    expect(el.scrollTop).toBe(0);
  });
});

describe("<lc-code-view> tests in the file", () => {
  let path: string;
  let t1: TestNode;
  let r1: TestNode;
  let r2: TestNode;
  beforeEach(async () => {
    path = freshPath();
    t1 = testNode({ id: "Ns.ClassA.Test1", filePath: path, line: 4, lineEnd: 8, outcome: "failed" });
    r1 = testNode({ id: "Ns.ClassA.Rows#0", methodName: "Rows", displayName: "Rows (1)", filePath: path, line: 9, lineEnd: 13, outcome: "passed" });
    r2 = testNode({ id: "Ns.ClassA.Rows#1", methodName: "Rows", displayName: "Rows (2)", filePath: path, line: 9, outcome: "failed" });
    await resetStore({ tests: [t1, r1, r2, testNode({ id: "Other.X.Y", filePath: "/elsewhere.cs", line: 1 })] });
  });

  it("puts a gutter button + run button on each test's line and links the method name", async () => {
    const el = await view({ path });
    const btns = [...el.querySelectorAll<HTMLButtonElement>(".src-test-btn")];
    expect(btns).toHaveLength(2);
    expect(row(el, 4).classList.contains("has-test")).toBe(true);
    expect(btns[0].getAttribute("aria-label")).toBe("Select test Test1 (failed)");
    expect(btns[0].dataset.test).toBe(`t:${testKey(t1)}`);
    expect(btns[1].getAttribute("aria-label")).toBe("Select test Rows (2 cases, 1 failed)");
    expect(btns[1].dataset.test).toBe("m:P1:Ns.ClassA.Rows");
    // one tab stop for the gutter buttons
    expect(btns.map((b) => b.tabIndex)).toEqual([0, -1]);
    expect(row(el, 4).querySelector(".src-test-run")!.getAttribute("aria-label")).toBe("Run test Test1 (failed)");
    // the name is linked where it is written (below the attributes)
    expect(row(el, 5).querySelector(".src-test-name")!.textContent).toBe("Test1");
    expect(row(el, 11).querySelector(".src-test-name")!.textContent).toBe("Rows");
  });

  it("clicking the gutter icon or the name asks to select the test", async () => {
    const el = await view({ path });
    const seen: string[] = [];
    const off = onSelectTestRequest((r) => seen.push(r.key));
    el.querySelector<HTMLElement>(".src-test-btn")!.click();
    click(row(el, 11).querySelector(".src-test-name")!);
    off();
    expect(seen).toEqual([`t:${testKey(t1)}`, "m:P1:Ns.ClassA.Rows"]);
  });

  it("Ctrl/Cmd+click inside a method body selects it; a plain click does not", async () => {
    const el = await view({ path });
    const seen: string[] = [];
    const off = onSelectTestRequest((r) => seen.push(r.key));
    click(row(el, 7).querySelector(".src-text")!);
    expect(seen).toEqual([]);
    const e = click(row(el, 7).querySelector(".src-text")!, { ctrlKey: true });
    expect(e.defaultPrevented).toBe(true);
    click(row(el, 1).querySelector(".src-text")!, { metaKey: true });
    off();
    expect(seen).toEqual([`t:${testKey(t1)}`]);
  });

  it("the ▶ button runs only that test (all cases of a data-driven method); not during a run", async () => {
    const { getStore } = await import("../../../src/store");
    const el = await view({ path });
    const transport = getStore().transport as unknown as { sent: unknown[] };
    el.querySelectorAll<HTMLElement>(".src-test-run")[1].click();
    expect(transport.sent).toEqual([{ type: "run", projectId: "P1", testIds: [r1.id, r2.id] }]);
    getStore().apply({ type: "runStarted", run: { runId: "x", startedAt: "", total: 1, passed: 0, failed: 0, skipped: 0 }, testIds: [t1.id] });
    el.querySelectorAll<HTMLElement>(".src-test-run")[0].click();
    expect(transport.sent).toHaveLength(1);
  });

  it("keyboard: ↑/↓/Home/End move between gutter buttons, Enter selects, Shift+Enter runs", async () => {
    const { getStore } = await import("../../../src/store");
    const el = await view({ path });
    const btns = () => [...el.querySelectorAll<HTMLButtonElement>(".src-test-btn")];
    btns()[0].focus();
    expect(key(btns()[0], "ArrowDown").defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(btns()[1]);
    expect(btns().map((b) => b.tabIndex)).toEqual([-1, 0]);
    key(btns()[1], "ArrowUp");
    expect(document.activeElement).toBe(btns()[0]);
    key(btns()[0], "End");
    expect(document.activeElement).toBe(btns()[1]);
    key(btns()[1], "Home");
    expect(document.activeElement).toBe(btns()[0]);
    expect(key(btns()[0], "a").defaultPrevented).toBe(false);
    const seen: string[] = [];
    const off = onSelectTestRequest((r) => seen.push(r.key));
    key(btns()[0], "Enter");
    off();
    expect(seen).toEqual([`t:${testKey(t1)}`]);
    key(btns()[0], "Enter", { shiftKey: true });
    expect((getStore().transport as unknown as { sent: unknown[] }).sent).toEqual([{ type: "run", projectId: "P1", testIds: [t1.id] }]);
    // keys outside the gutter are ignored
    expect(key(row(el, 1), "ArrowDown").defaultPrevented).toBe(false);
  });

  it("an outcome change rebuilds the gutter and keeps the focused button focused", async () => {
    const { getStore } = await import("../../../src/store");
    const el = await view({ path });
    el.querySelector<HTMLButtonElement>(".src-test-btn")!.focus();
    getStore().apply({ type: "testsUpdated", tests: [{ ...t1, outcome: "passed" }] });
    await flushFrame();
    const btn = el.querySelector<HTMLButtonElement>(".src-test-btn")!;
    expect(btn.getAttribute("aria-label")).toBe("Select test Test1 (passed)");
    expect(document.activeElement).toBe(btn);
  });

  it("aggregates data-driven outcomes (running / queued / not run / skipped / passed)", async () => {
    const { getStore } = await import("../../../src/store");
    const el = await view({ path });
    const label = () => el.querySelectorAll(".src-test-btn")[1].getAttribute("aria-label");
    const set = async (a: TestNode["outcome"], b: TestNode["outcome"]) => {
      getStore().apply({ type: "testsUpdated", tests: [{ ...r1, outcome: a }, { ...r2, outcome: b }] });
      await flushFrame();
    };
    await set("running", "passed");
    expect(label()).toBe("Select test Rows (2 cases, running)");
    await set("queued", "passed");
    expect(label()).toBe("Select test Rows (2 cases, queued)");
    await set("notRun", "passed");
    expect(label()).toBe("Select test Rows (2 cases, not run)");
    await set("skipped", "skipped");
    expect(label()).toBe("Select test Rows (2 cases, skipped)");
    await set("passed", "skipped");
    expect(label()).toBe("Select test Rows (2 cases, passed)");
  });
});
