import { afterEach, describe, expect, it, vi } from "vitest";
import { h } from "@eriklieben/labcoat-design-system";
import {
  fetchCoveringTests,
  onSelectTestRequest,
  requestSelectNode,
  requestSelectTest,
} from "../../../src/components/coverage-line-tests";
import type { CoveringTests } from "../../../src/contract";
import { click, flushFrame, makeStore, mount, runSummary, testNode, text } from "../../helpers/app";

afterEach(() => {
  location.hash = "";
});

type LineFetch = (path: string, line: number, signal?: AbortSignal) => Promise<CoveringTests>;
const withLineFetch = (transport: object, fn: LineFetch) => ((transport as { fetchCoverageLine?: LineFetch }).fetchCoverageLine = vi.fn(fn));

const tests = [
  testNode({ id: "Ns.ClassA.Test1" }),
  testNode({ id: "Ns.ClassA.Test2", outcome: "failed" }),
  testNode({ id: "Ns.ClassB.Test3", outcome: "passed" }),
];

describe("fetchCoveringTests", () => {
  it("uses the transport's fetchCoverageLine when it has one (mock)", async () => {
    const { transport } = makeStore();
    const data: CoveringTests = { tests: [], partial: true };
    const fn = withLineFetch(transport, async () => data);
    await expect(fetchCoveringTests("/a.cs", 3)).resolves.toBe(data);
    expect(fn).toHaveBeenCalledWith("/a.cs", 3, undefined);
  });

  it("GETs /api/coverage/line otherwise; non-ok responses throw with the status", async () => {
    makeStore({ kind: "ws" });
    const fetchMock = vi.fn(async (url: URL) =>
      url.searchParams.get("line") === "7" ? new Response(JSON.stringify({ tests: [], partial: false }), { status: 200 }) : new Response("", { status: 404, statusText: "Not Found" }),
    );
    vi.stubGlobal("fetch", fetchMock);
    await expect(fetchCoveringTests("/src/a b.cs", 7)).resolves.toEqual({ tests: [], partial: false });
    const url = fetchMock.mock.calls[0][0] as URL;
    expect(url.pathname).toBe("/api/coverage/line");
    expect(url.searchParams.get("path")).toBe("/src/a b.cs");
    await expect(fetchCoveringTests("/src/a.cs", 8)).rejects.toMatchObject({ message: "404 Not Found", status: 404 });
  });
});

describe("select-test requests", () => {
  it("a handled request cancels the hash fallback", () => {
    const seen: unknown[] = [];
    const off = onSelectTestRequest((r) => seen.push(r));
    requestSelectTest({ projectId: "P1", id: "A.B" });
    requestSelectNode("m:P1|A.B", { projectId: "P1", id: "A.B#0" });
    expect(seen).toEqual([
      { key: "t:P1|A.B", projectId: "P1", id: "A.B" },
      { key: "m:P1|A.B", projectId: "P1", id: "A.B#0" },
    ]);
    expect(location.hash).toBe("");
    off();
  });

  it("unhandled: falls back to the deep link", () => {
    requestSelectTest({ projectId: "P 1", id: "A.B" });
    expect(decodeURIComponent(location.hash)).toBe("#/tests?t=P 1|A.B");
    location.hash = "";
    requestSelectNode("c:x", { projectId: "P1", id: "C#1" });
    expect(location.hash).toBe("#/tests?t=P1|C%231");
  });
});

describe("<lc-coverage-line-tests>", () => {
  async function setup(fn: LineFetch, hits?: number) {
    const ctx = makeStore({ state: { tests } });
    await flushFrame();
    withLineFetch(ctx.transport, fn);
    const onClose = vi.fn();
    const el = mount(h("lc-coverage-line-tests", { path: "/src/Lib/Calc.cs", line: 12, hits, onClose }));
    return { el, onClose, ...ctx };
  }

  it("loading → rows: exact first, approximate marked, stale/hits chips, foot note", async () => {
    let resolve!: (d: CoveringTests) => void;
    const { el } = await setup(() => new Promise((r) => (resolve = r)), 3);
    expect(el.classList.contains("cov-lt")).toBe(true);
    expect(text(el.querySelector(".cov-lt-heading-title"))).toBe("Tests covering line 12");
    expect(text(el.querySelector(".cov-lt-sub"))).toBe("Calc.cs · 3 hits in this report");
    expect(text(el.querySelector(".cov-lt-msg"))).toBe("Looking up tests…");
    resolve({
      tests: [
        { projectId: "P1", id: "Ns.ClassB.Test3", source: "impactMap" },
        { projectId: "P1", id: "Ns.ClassA.Test1", source: "perTest", hits: 1, stale: true },
        { projectId: "P1", id: "Gone.Test", source: "perTest", hits: 2 },
      ],
      partial: true,
    });
    await vi.waitFor(() => expect(el.querySelectorAll(".cov-lt-row")).toHaveLength(3));
    expect(el.querySelector(".cov-lt-msg")).toBeNull();
    const rows = [...el.querySelectorAll(".cov-lt-row")];
    expect(rows.map((r) => text(r.querySelector(".cov-lt-title")))).toEqual(["Test1", "Gone.Test", "Test3"]);
    expect(text(rows[0].querySelector(".cov-lt-ctx"))).toBe("Ns.ClassA");
    expect(text(rows[1].querySelector(".cov-lt-ctx"))).toBe("P1");
    expect(text(rows[0].querySelector(".cov-lt-stale"))).toBe("stale");
    expect(text(rows[0].querySelector(".cov-lt-hits"))).toBe("1×");
    expect(rows[0].querySelector<HTMLElement>(".cov-lt-hits")!.title).toBe("1 hit in this test's own coverage");
    expect(rows[1].querySelector<HTMLElement>(".cov-lt-hits")!.title).toBe("2 hits in this test's own coverage");
    expect(rows[2].classList.contains("approx")).toBe(true);
    expect(text(rows[2].querySelector(".cov-lt-approx"))).toBe("≈ approx.");
    // an unknown test can't be selected or run
    expect(rows[1].querySelector<HTMLButtonElement>('[title="Select this test"]')!.disabled).toBe(true);
    expect(rows[1].querySelector<HTMLButtonElement>(".cov-lt-run")!.disabled).toBe(true);
    expect(rows[0].querySelector('[title="Select this test"]')!.getAttribute("aria-label")).toBe("Select Test1");
    expect(text(el.querySelector(".cov-lt-note"))).toBe("≈ approx. = from the impact map, not the test's own coverage. Only tests with collected coverage are listed.");
    expect(text(el.querySelector(".cov-lt-foot .btn"))).toBe("Run all 2");
  });

  it("runs one test, all listed tests, and selects a test (closing the popup)", async () => {
    const { el, transport, onClose } = await setup(async () => ({
      tests: [
        { projectId: "P1", id: "Ns.ClassA.Test1", source: "perTest", hits: 1 },
        { projectId: "P1", id: "Ns.ClassA.Test2", source: "perTest", hits: 1 },
      ],
      partial: false,
    }));
    await vi.waitFor(() => expect(el.querySelectorAll(".cov-lt-row")).toHaveLength(2));
    expect(text(el.querySelector(".cov-lt-note"))).toBe("");
    click(el.querySelectorAll(".cov-lt-run")[1]);
    expect(transport.sent).toEqual([{ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test2"] }]);
    click(el.querySelector(".cov-lt-foot .btn")!);
    expect(transport.sent[1]).toEqual({ type: "run", projectId: "P1", testIds: ["Ns.ClassA.Test1", "Ns.ClassA.Test2"] });
    const seen: unknown[] = [];
    const off = onSelectTestRequest((r) => seen.push(r.key));
    click(el.querySelector('[title="Select this test"]')!);
    off();
    expect(seen).toEqual(["t:P1|Ns.ClassA.Test1"]);
    expect(onClose).toHaveBeenCalledTimes(1);
    click(el.querySelector(".cov-lt-close")!);
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("several projects: run without a projectId; a single test says 'Run test'; disabled while running", async () => {
    const { el, store, transport } = await setup(async () => ({
      tests: [
        { projectId: "P1", id: "Ns.ClassA.Test1", source: "perTest" },
        { projectId: "P2", id: "Other", source: "impactMap" },
      ],
      partial: false,
    }));
    store.apply({ type: "testsUpdated", tests: [testNode({ projectId: "P2", id: "Other" })] });
    await flushFrame();
    await vi.waitFor(() => expect(el.querySelectorAll(".cov-lt-row")).toHaveLength(2));
    const runAll = el.querySelector<HTMLButtonElement>(".cov-lt-foot .btn")!;
    click(runAll);
    expect(transport.sent[0]).toEqual({ type: "run", projectId: undefined, testIds: ["Ns.ClassA.Test1", "Other"] });
    store.apply({ type: "runStarted", run: runSummary(), testIds: [] });
    await flushFrame();
    expect(runAll.disabled).toBe(true);
    expect(el.querySelector<HTMLButtonElement>(".cov-lt-run")!.disabled).toBe(true);
  });

  it("empty result: explains how to fill the list", async () => {
    const { el } = await setup(async () => ({ tests: [], partial: false }));
    expect(text(el.querySelector(".cov-lt-sub"))).toBe("Calc.cs");
    await vi.waitFor(() => expect(el.querySelector(".cov-lt-empty")).toBeTruthy());
    expect(text(el.querySelector(".cov-lt-empty"))).toContain("No test is known to cover this line yet.");
    expect(el.querySelector(".cov-lt-list")).toBeNull();
  });

  it("errors are shown; an abort after removal is not", async () => {
    const { el } = await setup(async () => {
      throw new Error("boom");
    });
    await vi.waitFor(() => expect(text(el.querySelector(".cov-lt-msg"))).toBe("Couldn't look up the tests (boom)."));
    const { el: el2 } = await setup(async () => {
      throw "plain";
    }, 1);
    expect(text(el2.querySelector(".cov-lt-sub"))).toBe("Calc.cs · 1 hit in this report");
    await vi.waitFor(() => expect(text(el2.querySelector(".cov-lt-msg"))).toBe("Couldn't look up the tests (plain)."));
    let reject!: (e: unknown) => void;
    const { el: el3 } = await setup((_p, _l, signal) => {
      expect(signal).toBeInstanceOf(AbortSignal);
      return new Promise((_r, rj) => (reject = rj));
    });
    el3.remove();
    await Promise.resolve();
    reject(new Error("aborted"));
    await Promise.resolve();
    expect(el3.querySelector(".cov-lt-msg")).toBeNull();
  });
});
