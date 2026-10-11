import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import "../../../src/components/log-panel";
import type { LogPanel, LogStrip } from "../../../src/components/log-panel";
import type { Store } from "../../../src/store";
import type { LogLevel } from "../../../src/contract";
import { flushFrame, mount, project, text } from "../../helpers/app";
import { flushMicrotasks, triggerResize } from "../../helpers/dom";
import { freshStore } from "../../helpers/app6";

let store: Store;

beforeEach(async () => {
  vi.useFakeTimers();
  ({ store } = await freshStore({ projects: [project(), project({ id: "P2", name: "Project.Two", status: "building" })] }));
});
afterEach(async () => {
  await flushFrame();
});

const ts = "2026-10-09T10:00:00Z";
async function log(message: string, level: LogLevel = "info", projectId?: string) {
  store.apply({ type: "log", level, message, timestamp: ts, projectId });
  await flushFrame();
}

function panel(onClose = vi.fn()) {
  const el = document.createElement("lc-log-panel") as LogPanel;
  el.onClose = onClose;
  mount(el);
  return el;
}

const lines = (el: Element) => [...el.querySelectorAll(".console-line .console-text")].map((x) => text(x));

describe("<lc-log-panel>", () => {
  it("is a labelled region with a log, project chips and an empty state", () => {
    const el = panel();
    expect(el.classList.contains("log-panel")).toBe(true);
    expect(el.getAttribute("role")).toBe("region");
    expect(el.getAttribute("aria-label")).toBe("Output");
    const con = el.querySelector(".log-console")!;
    expect(con.getAttribute("role")).toBe("log");
    expect(con.getAttribute("aria-label")).toBe("Output log");
    expect(text(el.querySelector(".console-empty"))).toContain("No output yet");
    const chips = [...el.querySelectorAll(".proj-status")];
    expect(chips.map((c) => text(c))).toEqual(["Project.OneReady", "Project.TwoBuilding"]);
    expect(chips[1].classList.contains("ps-building")).toBe(true);
    // project filter options follow the projects
    expect([...el.querySelectorAll<HTMLOptionElement>('select[aria-label="Filter by project"] option')].map((o) => o.value)).toEqual(["", "P1", "P2"]);
  });

  it("says 'No projects' without projects and 'Build failed' for errored ones", async () => {
    store.apply({ type: "state", state: { projects: [], tests: [], watch: false, coverageEnabled: false } });
    await flushFrame();
    const el = panel();
    expect(text(el.querySelector(".project-status-strip"))).toBe("No projects");
    store.apply({ type: "projectStatus", project: project({ status: "error", lastError: "CS1002" }) });
    await flushFrame();
    const chip = el.querySelector<HTMLElement>(".proj-status")!;
    expect(text(chip)).toBe("Project.OneBuild failed");
    expect(chip.title).toBe("CS1002");
    expect(el.querySelector(".project-status-strip .dim")).toBeNull();
  });

  it("streams log lines with number, time, source and level classes", async () => {
    const el = panel();
    await log("hello");
    await log("careful", "warning", "P1");
    await log("boom", "error", "P2");
    expect(el.querySelector(".console-empty")).toBeNull();
    expect(lines(el)).toEqual(["hello", "careful", "boom"]);
    const rows = [...el.querySelectorAll(".console-line")];
    expect(rows.map((r) => text(r.querySelector(".console-ln")))).toEqual(["1", "2", "3"]);
    expect(rows[1].classList.contains("warn")).toBe(true);
    expect(rows[2].classList.contains("stderr")).toBe(true);
    expect(rows[0].querySelector(".console-src")).toBeNull();
    expect(text(rows[2].querySelector(".console-src"))).toBe("P2");
    expect(text(el.querySelector(".log-count"))).toBe("3");
  });

  it("linkifies build output with file locations", async () => {
    const el = panel();
    await log("/src/A.cs(12,5): error CS1002: ; expected", "error");
    expect(el.querySelector(".console-text .trace-line, .console-text div")).not.toBeNull();
  });

  it("filters by project and by level", async () => {
    const el = panel();
    await log("i1", "info", "P1");
    await log("w2", "warning", "P2");
    await log("e1", "error", "P1");
    const [projSel, levelSel] = el.querySelectorAll<HTMLSelectElement>("select");
    projSel.value = "P1";
    projSel.dispatchEvent(new Event("change"));
    expect(lines(el)).toEqual(["i1", "e1"]);
    levelSel.value = "warning";
    levelSel.dispatchEvent(new Event("change"));
    expect(lines(el)).toEqual(["e1"]);
    projSel.value = "";
    projSel.dispatchEvent(new Event("change"));
    expect(lines(el)).toEqual(["w2", "e1"]);
    levelSel.value = "error";
    levelSel.dispatchEvent(new Event("change"));
    expect(lines(el)).toEqual(["e1"]);
    expect(text(el.querySelector(".log-count"))).toBe("1");
  });

  it("wrap toggle is pressed by default, persists, and removes nowrap", () => {
    const el = panel();
    const wrap = el.querySelector<HTMLButtonElement>('button[aria-label="Wrap lines"]')!;
    const con = el.querySelector(".log-console")!;
    expect(wrap.getAttribute("aria-pressed")).toBe("true");
    expect(con.classList.contains("nowrap")).toBe(false);
    wrap.click();
    expect(wrap.getAttribute("aria-pressed")).toBe("false");
    expect(wrap.classList.contains("toggled")).toBe(false);
    expect(con.classList.contains("nowrap")).toBe(true);
    expect(localStorage.getItem("mste.logWrap")).toBe("false");
  });

  it("starts unwrapped when that was saved", () => {
    localStorage.setItem("mste.logWrap", "false");
    const el = panel();
    expect(el.querySelector(".log-console")!.classList.contains("nowrap")).toBe(true);
  });

  it("clear empties the log; hide calls onClose", async () => {
    const onClose = vi.fn();
    const el = panel(onClose);
    await log("x", "error");
    expect(store.get().unseenErrors).toBe(1);
    el.querySelector<HTMLButtonElement>('button[aria-label="Clear output"]')!.click();
    await flushFrame();
    expect(lines(el)).toEqual([]);
    expect(store.get().unseenErrors).toBe(0);
    el.querySelector<HTMLButtonElement>('button[aria-label="Hide output panel"]')!.click();
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("sticks to the bottom unless the user scrolled up (also on resize)", async () => {
    const el = panel();
    const con = el.querySelector<HTMLDivElement>(".log-console")!;
    let height = 500;
    Object.defineProperty(con, "scrollHeight", { configurable: true, get: () => height });
    Object.defineProperty(con, "clientHeight", { configurable: true, get: () => 100 });
    await log("a");
    await flushMicrotasks();
    expect(con.scrollTop).toBe(500);

    height = 800;
    triggerResize(con);
    expect(con.scrollTop).toBe(800);

    // the user scrolls up: no more sticking
    con.scrollTop = 100;
    con.dispatchEvent(new Event("scroll"));
    height = 900;
    await log("b");
    await flushMicrotasks();
    triggerResize(con);
    expect(con.scrollTop).toBe(100);

    // back to the bottom: sticks again
    con.scrollTop = 800;
    con.dispatchEvent(new Event("scroll"));
    height = 1000;
    await log("c");
    await flushMicrotasks();
    expect(con.scrollTop).toBe(1000);
  });

  it("disconnects its ResizeObserver when removed", async () => {
    const el = panel();
    el.remove();
    await flushMicrotasks();
    expect(el.childNodes).toHaveLength(0);
  });
});

describe("<lc-log-strip>", () => {
  function strip(onOpen = vi.fn()) {
    const el = document.createElement("lc-log-strip") as LogStrip;
    el.onOpen = onOpen;
    mount(el);
    return { el, btn: el.querySelector<HTMLButtonElement>("button.log-strip-btn")! };
  }

  it("shows 'No output yet', a spinner while a project is busy, and opens the panel on click", async () => {
    const onOpen = vi.fn();
    const { el, btn } = strip(onOpen);
    expect(el.classList.contains("log-strip")).toBe(true);
    expect(text(btn.querySelector(".log-strip-msg"))).toBe("No output yet");
    expect(btn.querySelector(".log-strip-spinner")).not.toBeNull(); // P2 is building
    expect(text(btn)).toContain("— show the output panel");
    btn.click();
    expect(onOpen).toHaveBeenCalledOnce();
    store.apply({ type: "projectStatus", project: project({ id: "P2", name: "Project.Two", status: "idle" }) });
    await flushFrame();
    expect(btn.querySelector(".log-strip-spinner")).toBeNull();
    expect(btn.querySelector(".icon")).not.toBeNull();
  });

  it("shows the last line (first line only, ANSI stripped) with its level and source", async () => {
    const { btn } = strip();
    await log("\u001b[31mred\u001b[0m text\nsecond line", "warning", "P1");
    expect(text(btn.querySelector(".log-strip-msg"))).toBe("red text …");
    expect(text(btn.querySelector(".log-strip-src"))).toBe("P1");
    expect(btn.classList.contains("lvl-warn")).toBe(true);
    await log("plain");
    expect(btn.querySelector(".log-strip-src")).toBeNull();
    expect(btn.classList.contains("lvl-warn")).toBe(false);
  });

  it("counts unseen errors (99+ cap) and build errors of failed projects", async () => {
    const { btn } = strip();
    await log("bad", "error");
    expect(btn.classList.contains("lvl-error")).toBe(true);
    expect(text(btn.querySelector(".log-strip-errors"))).toBe("1");
    expect(text(btn.querySelector(".log-strip-errors + .visually-hidden"))).toBe(", 1 error");
    for (let i = 0; i < 120; i++) store.apply({ type: "log", level: "error", message: `e${i}`, timestamp: ts });
    await flushFrame();
    expect(text(btn.querySelector(".log-strip-errors"))).toBe("99+");
    // a failing build: its MSBuild error count wins
    store.apply({ type: "projectStatus", project: project({ status: "error", buildErrorCount: 2 }) });
    await flushFrame();
    expect(text(btn.querySelector(".log-strip-errors"))).toBe("2");
    expect(text(btn.querySelector(".log-strip-errors + .visually-hidden"))).toBe(", 2 errors");
    store.markLogsSeen();
    await flushFrame();
    expect(btn.querySelector(".log-strip-errors")).toBeNull();
  });
});
