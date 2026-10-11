import { afterEach, describe, expect, it, vi } from "vitest";
import { root } from "@eriklieben/labcoat-design-system/core";
import { signal } from "@eriklieben/labcoat-design-system/core";
import {
  ansi,
  cx,
  displayPath,
  editorLink,
  fileName,
  formatClock,
  formatDuration,
  formatRelative,
  formatRunTime,
  keepFocus,
  linkifyLine,
  loadPref,
  parseStackFrame,
  pathView,
  pct,
  projectAccent,
  rateTone,
  runCounts,
  savePref,
  stripAnsi,
  trimTrailingSlashes,
} from "../../src/util";
import { makeStore, stateEvent } from "../helpers/app";
import { flushMicrotasks } from "../helpers/dom";

const render = (c: unknown) => {
  const d = document.createElement("div");
  const items = Array.isArray(c) ? c : [c];
  for (const x of items) d.append(x as Node | string);
  return d;
};

describe("formatters", () => {
  it("formatDuration", () => {
    expect(formatDuration(undefined)).toBe("");
    expect(formatDuration(NaN)).toBe("");
    expect(formatDuration(0)).toBe("0 ms");
    expect(formatDuration(0.4)).toBe("<1 ms");
    expect(formatDuration(12.6)).toBe("13 ms");
    expect(formatDuration(1234)).toBe("1.23 s");
    expect(formatDuration(12_345)).toBe("12.3 s");
    expect(formatDuration(125_000)).toBe("2m 5s");
  });

  it("formatClock / formatRelative", () => {
    expect(formatClock(undefined)).toBe("");
    expect(formatClock("not a date")).toBe("");
    const iso = "2026-10-01T10:11:12Z";
    expect(formatClock(iso)).toBe(new Date(iso).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" }));
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-01T12:00:00Z"));
    expect(formatRelative(undefined)).toBe("");
    expect(formatRelative("2026-10-01T11:59:55Z")).toBe("just now");
    expect(formatRelative("2026-10-01T11:59:30Z")).toBe("30s ago");
    expect(formatRelative("2026-10-01T11:45:00Z")).toBe("15m ago");
    expect(formatRelative("2026-10-01T09:00:00Z")).toBe(formatClock("2026-10-01T09:00:00Z"));
  });

  it("formatRunTime: today → clock, yesterday, older → date", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 9, 9, 15, 0, 0));
    expect(formatRunTime(undefined)).toBe("");
    expect(formatRunTime("garbage")).toBe("");
    const today = new Date(2026, 9, 9, 9, 30, 5).toISOString();
    expect(formatRunTime(today)).toBe(formatClock(today));
    const y = new Date(2026, 9, 8, 22, 54);
    expect(formatRunTime(y.toISOString())).toBe(`Yesterday ${y.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })}`);
    const old = new Date(2026, 9, 6, 22, 54);
    expect(formatRunTime(old.toISOString())).toBe(
      `${old.toLocaleDateString(undefined, { month: "short", day: "numeric" })}, ${old.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })}`,
    );
  });

  it("editorLink / fileName", () => {
    expect(editorLink("C:\\a b\\c.cs", 3, 4)).toBe("vscode://file/C:/a%20b/c.cs:3:4");
    expect(editorLink("/x/y.cs", 3)).toBe("vscode://file/x/y.cs:3");
    expect(editorLink("/x/y.cs")).toBe("vscode://file/x/y.cs");
    expect(fileName("C:\\a\\b.cs")).toBe("b.cs");
    expect(fileName("/a/b/c.cs")).toBe("c.cs");
  });

  it("runCounts", () => {
    expect(runCounts({ passed: 3, failed: 1, skipped: 0 })).toBe("3 passed, 1 failed");
    expect(runCounts({ passed: 0, failed: 0, skipped: 2, notRun: 1 })).toBe("2 skipped, 1 not run");
    expect(runCounts({ passed: 0, failed: 0, skipped: 0 })).toBe("no results");
    expect(runCounts({ passed: 1, failed: 0, skipped: 0, aborted: true, cancelled: true })).toBe("1 passed (aborted)");
    expect(runCounts({ passed: 1, failed: 0, skipped: 0, cancelled: true })).toBe("1 passed (cancelled)");
  });

  it("rateTone / pct", () => {
    expect([rateTone(0.8), rateTone(0.5), rateTone(0.49)]).toEqual(["good", "warn", "bad"]);
    expect(pct(1)).toBe("100%");
    expect(pct(0.756)).toBe("75.6%");
    expect(pct(0)).toBe("0.0%");
  });

  it("projectAccent is stable per id and from the palette", () => {
    expect(projectAccent("P1")).toBe(projectAccent("P1"));
    expect(projectAccent("P1")).toMatch(/^var\(--lc-tag-[a-z-]+\)$/);
    const all = new Set(["a", "b", "c", "d", "e", "f", "g", "h", "LongProjectName.Tests"].map(projectAccent));
    expect(all.size).toBeGreaterThan(2);
  });

  it("cx is re-exported", () => {
    expect(cx("a", false, "b")).toBe("a b");
  });
});

describe("prefs", () => {
  it("loadPref / savePref round-trip under mste.*, fallback on missing / bad JSON / storage errors", () => {
    expect(loadPref("x", 5)).toBe(5);
    savePref("x", { a: 1 });
    expect(localStorage.getItem("mste.x")).toBe('{"a":1}');
    expect(loadPref("x", null)).toEqual({ a: 1 });
    localStorage.setItem("mste.bad", "{");
    expect(loadPref("bad", "fb")).toBe("fb");
    const set = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota");
    });
    expect(() => savePref("y", 1)).not.toThrow();
    set.mockRestore();
  });
});

describe("ansi", () => {
  it("plain text passes through", () => {
    expect(ansi("hello")).toBe("hello");
    expect(stripAnsi("\u001b[31mred\u001b[0m")).toBe("red");
  });

  it("renders foreground colors and bold as styled spans, resets with 0/22/39", () => {
    const out = ansi("a\u001b[31mred\u001b[1mbold\u001b[22m\u001b[39mplain\u001b[92mx\u001b[mend\u001b[1;34mbb\u001b[0m") as unknown[];
    const el = render(out);
    const spans = [...el.querySelectorAll("span")];
    expect(el.textContent).toBe("aredboldplainxendbb");
    expect(spans.map((s) => [s.textContent, s.style.color, s.style.fontWeight])).toEqual([
      ["red", "var(--lc-ansi-red)", ""],
      ["bold", "var(--lc-ansi-red)", "600"],
      ["x", "var(--lc-ansi-bright-green)", ""],
      ["bb", "var(--lc-ansi-blue)", "600"],
    ]);
  });
});

describe("parseStackFrame", () => {
  it("splits a frame with a source location", () => {
    expect(parseStackFrame("   at Ns.A.T() in /src/A.cs:line 12 ")).toEqual({ at: "   at ", method: "Ns.A.T()", inWord: " in ", path: "/src/A.cs", line: "12" });
    // the first " in " that leaves a path; the method may contain " in " further on
    expect(parseStackFrame("at A in B in C:line 3")).toEqual({ at: "at ", method: "A", inWord: " in ", path: "B in C", line: "3" });
    // no method name: the " in " starts inside the whitespace after "at"
    expect(parseStackFrame("at  in /x.cs:line 1")).toEqual({ at: "at ", method: "", inWord: " in ", path: "/x.cs", line: "1" });
  });

  it("undefined without a location, a path or on a line break in the path", () => {
    expect(parseStackFrame("   at System.X()")).toBeUndefined();
    expect(parseStackFrame("at A in :line 1")).toBeUndefined();
    expect(parseStackFrame("at A in x\ry:line 1")).toBeUndefined();
    expect(parseStackFrame("A in x:line 1")).toBeUndefined();
  });

  it("matches the backtracking pattern it replaces", () => {
    const old = /^(\s*at\s+)(.*?)( in )(.+?):line (\d+)\s*$/;
    const parts = ["at", " ", "  ", "\t", "\n", "\r", "in", " in ", "x", ":line ", "1", "(", "."];
    let seed = 7;
    const next = () => (seed = (seed * 48271) % 2147483647);
    for (let k = 0; k < 20_000; k++) {
      let s = k % 2 ? "  at " : "";
      for (let n = next() % 10; n > 0; n--) s += parts[next() % parts.length];
      const m = old.exec(s);
      expect(parseStackFrame(s), JSON.stringify(s)).toEqual(m ? { at: m[1], method: m[2], inWord: m[3], path: m[4], line: m[5] } : undefined);
    }
  });
});

describe("linkifyLine", () => {
  afterEach(() => vi.restoreAllMocks());

  it("stack frame: method, dimmed parts and an editor link", () => {
    const el = linkifyLine("   at Foo.Bar() in /src/A.cs:line 42");
    expect(el.className).toBe("trace-line");
    expect(el.querySelector(".trace-method")!.textContent).toBe("Foo.Bar()");
    const a = el.querySelector<HTMLAnchorElement>("a.trace-link")!;
    expect(a.textContent).toBe("A.cs:42");
    expect(a.getAttribute("href")).toBe("vscode://file/src/A.cs:42");
    expect(a.title).toBe("Open /src/A.cs:42 in the editor");
  });

  it("stack frame with onOpenFrame: a frame button (active highlight) + an editor icon link", () => {
    const { store } = makeStore({ kind: "mock" });
    const opened: [string, number][] = [];
    const active = signal<string | undefined>(undefined);
    root((dispose) => {
      const el = linkifyLine("   at Foo.Bar() in /src/A.cs:line 42", { onOpenFrame: (p, l) => opened.push([p, l]), active });
      const btn = el.querySelector<HTMLButtonElement>("button.frame-btn")!;
      expect(btn.textContent).toBe("A.cs:42");
      expect(btn.title).toBe("Show /src/A.cs:42");
      btn.click();
      expect(opened).toEqual([["/src/A.cs", 42]]);
      expect(btn.className).toBe("frame-btn");
      active.set("/src/A.cs:42");
      expect(btn.className).toBe("frame-btn active");
      const icon = el.querySelector<HTMLAnchorElement>("a.frame-vscode")!;
      expect(icon.getAttribute("aria-label")).toBe("Open in editor");
      expect(icon.querySelector("svg")).not.toBeNull();
      // a plain left click opens through the editor setting (the mock logs it)
      const ev = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
      icon.dispatchEvent(ev);
      expect(ev.defaultPrevented).toBe(true);
      // modified clicks keep the browser's default
      const ctrl = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ctrlKey: true });
      icon.dispatchEvent(ctrl);
      expect(ctrl.defaultPrevented).toBe(false);
      dispose();
    });
    return flushMicrotasks().then(() => expect(store.get().logs.at(-1)?.message).toContain("/src/A.cs:42 (mock)"));
  });

  it("MSBuild diagnostic: file(line,col) link + the rest (with ANSI)", async () => {
    const { store } = makeStore({ kind: "mock" });
    const el = linkifyLine("  /src/B.cs(10,5): error CS1002: ; expected");
    const a = el.querySelector<HTMLAnchorElement>("a.trace-link")!;
    expect(a.textContent).toBe("/src/B.cs(10,5)");
    expect(a.getAttribute("href")).toBe("vscode://file/src/B.cs:10:5");
    expect(el.textContent).toBe("  /src/B.cs(10,5): error CS1002: ; expected");
    a.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    await flushMicrotasks();
    expect(store.get().logs.at(-1)?.message).toContain("/src/B.cs:10");
    // middle click: ignored
    const mid = new MouseEvent("click", { bubbles: true, cancelable: true, button: 1 });
    a.dispatchEvent(mid);
    expect(mid.defaultPrevented).toBe(false);
  });

  it("external frames (no source) and plain lines", () => {
    const ext = linkifyLine("   at System.Linq.Enumerable.First()");
    expect(ext.className).toBe("trace-line trace-external");
    expect(ext.querySelector(".trace-dim")!.textContent).toBe("   at ");
    expect(ext.textContent).toBe("   at System.Linq.Enumerable.First()");
    const plain = linkifyLine("Build succeeded.");
    expect(plain.className).toBe("trace-line");
    expect(plain.textContent).toBe("Build succeeded.");
  });
});

describe("paths", () => {
  it("trimTrailingSlashes drops only the slashes at the end", () => {
    expect(trimTrailingSlashes("/repo/src//")).toBe("/repo/src");
    expect(trimTrailingSlashes("/repo/src")).toBe("/repo/src");
    expect(trimTrailingSlashes("///")).toBe("");
    expect(trimTrailingSlashes("")).toBe("");
    expect(trimTrailingSlashes("a\\")).toBe("a\\");
  });

  it("displayPath: relative to the deepest matching source root, keeping its folder", () => {
    expect(displayPath("/repo/tests/App.Tests/A.cs", ["/repo/tests", "/repo/tests/App.Tests/"])).toBe("App.Tests/A.cs");
    expect(displayPath("C:\\Repo\\src\\A.cs", ["c:/repo"])).toBe("Repo/src/A.cs");
    expect(displayPath("/elsewhere/A.cs", ["/repo"])).toBe("/elsewhere/A.cs");
    expect(displayPath("/x/A.cs", undefined)).toBe("/x/A.cs");
  });

  it("displayPath defaults to the store's source roots (and works without a store)", async () => {
    vi.resetModules();
    const fresh = await import("../../src/util");
    expect(fresh.displayPath("/repo/src/A.cs")).toBe("/repo/src/A.cs");
    const { store } = makeStore();
    store.apply(stateEvent({ sourceRoots: ["/repo/src"] }));
    expect(displayPath("/repo/src/A.cs")).toBe("src/A.cs");
  });

  it("pathView: dir + file + line", () => {
    const el = pathView("/repo/src/A.cs", 12, { relative: false });
    expect(el.className).toBe("path");
    expect(el.title).toBe("/repo/src/A.cs:12");
    expect(el.querySelector(".path-dir")!.textContent).toBe("\u200e/repo/src/\u200e");
    expect(el.querySelector(".path-file")!.textContent).toBe("A.cs");
    expect(el.querySelector(".path-line")!.textContent).toBe(":12");
    const bare = pathView("A.cs");
    expect(bare.querySelector(".path-dir")).toBeNull();
    expect(bare.querySelector(".path-line")).toBeNull();
    expect(bare.title).toBe("A.cs");
  });
});

describe("keepFocus", () => {
  async function settle() {
    await flushMicrotasks();
    await new Promise((r) => setTimeout(r, 0));
    await flushMicrotasks();
  }

  it("moves focus to the equivalent control when the focused one is re-rendered", async () => {
    const host = document.createElement("div");
    document.body.append(host);
    const mk = (label: string) => {
      const b = document.createElement("button");
      b.className = "btn run";
      b.setAttribute("aria-label", label);
      return b;
    };
    const first = mk("Run");
    host.append(first);
    let dispose!: () => void;
    root((d) => {
      dispose = d;
      keepFocus(host);
    });
    first.focus();
    expect(document.activeElement).toBe(first);
    const second = mk("Run");
    first.replaceWith(second);
    await settle();
    expect(document.activeElement).toBe(second);
    dispose();
  });

  it("falls back when no equivalent exists; waits while the control is disabled", async () => {
    const host = document.createElement("div");
    const fb = document.createElement("button");
    fb.textContent = "fallback";
    const b = document.createElement("button");
    b.textContent = "Stop";
    host.append(b, fb);
    document.body.append(host);
    root(() => keepFocus(host, () => fb));
    b.focus();
    b.disabled = true;
    b.blur(); // browsers blur a control that gets disabled
    await settle();
    expect(document.activeElement).not.toBe(fb); // still waiting for b to be enabled again
    b.disabled = false;
    await settle();
    expect(document.activeElement).toBe(b);
    b.remove();
    await settle();
    expect(document.activeElement).toBe(fb);
  });

  it("does nothing when the user moved focus elsewhere on purpose or a dialog is open", async () => {
    const host = document.createElement("div");
    const a = document.createElement("button");
    a.textContent = "A";
    const outside = document.createElement("input");
    host.append(a);
    document.body.append(host, outside);
    const fb = vi.fn(() => null);
    root(() => keepFocus(host, fb));
    a.focus();
    outside.focus();
    a.remove();
    await settle();
    expect(document.activeElement).toBe(outside);
    expect(fb).not.toHaveBeenCalled();
    // a removed control while a dialog is open: no restore
    const b = document.createElement("button");
    b.textContent = "B";
    host.append(b);
    b.focus();
    const dlg = document.createElement("dialog");
    dlg.setAttribute("open", "");
    document.body.append(dlg);
    b.remove();
    await settle();
    expect(fb).not.toHaveBeenCalled();
  });

  it("stops observing when its owner is disposed", async () => {
    const host = document.createElement("div");
    const b = document.createElement("button");
    b.textContent = "B";
    host.append(b);
    document.body.append(host);
    const fb = vi.fn(() => null);
    root((dispose) => {
      keepFocus(host, fb);
      dispose();
    });
    b.focus();
    b.remove();
    await settle();
    expect(fb).not.toHaveBeenCalled();
  });
});
