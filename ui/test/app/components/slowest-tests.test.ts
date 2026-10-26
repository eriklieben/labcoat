import { afterEach, describe, expect, it, vi } from "vitest";
import { h, signal } from "@eriklieben/labcoat-design-system";
import type { SlowestTestsResponse, SlowTest } from "../../../src/contract";
import "../../../src/components/detail-pane";
import { slowestTrendList } from "../../../src/components/slowest-tests";
import { fetchSlowestTests, trendLabel } from "../../../src/slowest";
import { getStore, testKey } from "../../../src/store";
import { buildTree } from "../../../src/tree";
import { click, flushFrame, makeStore, mount, testNode, text } from "../../helpers/app";
import { jsonResponse, resetStore } from "../../helpers/app3";
import { flushMicrotasks } from "../../helpers/dom";

afterEach(() => vi.unstubAllGlobals());

const slow = (id: string, p: Partial<SlowTest> = {}): SlowTest => ({
  projectId: "P1",
  id,
  name: id.split(".").pop()!,
  className: "ClassA",
  durationMs: 900,
  medianMs: 300,
  factor: 3,
  trend: [
    { at: "2026-10-01T00:00:00Z", durationMs: 300, outcome: "passed" },
    { at: "2026-10-02T00:00:00Z", durationMs: 300, outcome: "passed" },
    { at: "2026-10-03T00:00:00Z", durationMs: 900, outcome: "passed" },
  ],
  ...p,
});

type SlowestFetch = (projectId: string, limit: number, signal?: AbortSignal) => Promise<SlowestTestsResponse>;
const withSlowestFetch = (transport: object, fn: SlowestFetch) => ((transport as { fetchSlowest?: SlowestFetch }).fetchSlowest = vi.fn(fn));

describe("trendLabel", () => {
  it("compares the latest duration with the median of the earlier ones", () => {
    expect(trendLabel(slow("a"))).toBe("3.0× median");
    expect(trendLabel(slow("a", { factor: 1.1 }))).toBe("as usual");
    expect(trendLabel(slow("a", { factor: 0.5 }))).toBe("0.5× median (faster)");
    expect(trendLabel(slow("a", { factor: undefined, medianMs: undefined }))).toBe("no earlier runs");
  });
});

describe("fetchSlowestTests", () => {
  it("uses the transport's fetchSlowest when it has one (mock)", async () => {
    const { transport } = makeStore();
    const data: SlowestTestsResponse = { total: 0, tests: [] };
    const fn = withSlowestFetch(transport, async () => data);
    await expect(fetchSlowestTests("P1", 5)).resolves.toBe(data);
    expect(fn).toHaveBeenCalledWith("P1", 5, undefined);
  });

  it("GETs /api/tests/slowest otherwise; non-ok responses throw with the status", async () => {
    makeStore({ kind: "ws" });
    const fetchMock = vi.fn(async (url: URL) => (url.searchParams.get("projectId") === "P1" ? jsonResponse({ total: 1, tests: [] }) : jsonResponse({}, { status: 404, statusText: "Not Found" })));
    vi.stubGlobal("fetch", fetchMock);
    await expect(fetchSlowestTests("P1", 7)).resolves.toEqual({ total: 1, tests: [] });
    const url = fetchMock.mock.calls[0][0] as URL;
    expect([url.pathname, url.searchParams.get("limit")]).toEqual(["/api/tests/slowest", "7"]);
    await expect(fetchSlowestTests("Other")).rejects.toMatchObject({ message: "404 Not Found", status: 404 });
  });
});

describe("slowestTrendList", () => {
  it("lists the slowest tests with a sparkline, the comparison and the duration; a row opens the test", async () => {
    const { transport } = makeStore();
    withSlowestFetch(transport, async () => ({ total: 3, tests: [slow("Ns.ClassA.Big"), slow("Ns.ClassA.Small", { durationMs: 40, factor: 1, trend: [] })] }));
    const onSelect = vi.fn();
    const el = mount(slowestTrendList(signal("P1"), signal(0), onSelect));
    await flushMicrotasks();
    const rows = [...el.querySelectorAll<HTMLButtonElement>(".slowest-item")];
    expect(rows.map((r) => text(r.querySelector(".failure-name")))).toEqual(["ClassA.Big", "ClassA.Small"]);
    expect(text(rows[0].querySelector(".slowest-factor"))).toBe("3.0× median");
    expect(rows[0].querySelector(".slowest-factor")!.classList.contains("is-slow")).toBe(true);
    expect(text(rows[0].querySelector(".row-duration"))).toBe("900 ms");
    expect(rows[0].querySelector(".slowest-spark svg")).not.toBeNull();
    expect(rows[1].querySelector(".slowest-spark svg")).toBeNull();
    expect(text(el.querySelector(".slowest-more"))).toBe("1 more tests with a duration");
    rows[0].click();
    expect(onSelect.mock.calls[0][0].id).toBe("Ns.ClassA.Big");
  });

  it("fetches again when the refresh signal changes", async () => {
    const { transport } = makeStore();
    const fn = withSlowestFetch(transport, async () => ({ total: 0, tests: [] }));
    const refresh = signal(0);
    mount(slowestTrendList(signal("P1"), refresh, vi.fn()));
    await flushMicrotasks();
    refresh.set(1);
    await flushMicrotasks();
    expect(fn).toHaveBeenCalledTimes(2);
  });

  it("says so when the list cannot be loaded", async () => {
    const { transport } = makeStore();
    withSlowestFetch(transport, async () => Promise.reject(new Error("500")));
    const el = mount(slowestTrendList(signal("P1"), signal(0), vi.fn()));
    await flushMicrotasks();
    expect(text(el.querySelector(".detail-note"))).toBe("The slowest tests could not be loaded.");
  });
});

describe("<lc-detail-pane> of a project", () => {
  it("shows the slowest tests of the project with their trend (from the host)", async () => {
    const a = testNode({ id: "Ns.ClassA.Big", outcome: "passed", durationMs: 900 });
    const { transport } = await resetStore({ tests: [a, testNode({ id: "Ns.ClassA.Quick", outcome: "passed", durationMs: 5 })] });
    const fn = withSlowestFetch(transport, async () => ({ total: 2, tests: [slow("Ns.ClassA.Big")] }));
    const s = getStore().get();
    const root = buildTree(s.projects, s.tests)[0];
    const onSelect = vi.fn();
    const el = mount(h("lc-detail-pane", { node: signal(root), onRun: vi.fn(), onSelect }));
    await flushFrame();
    await flushMicrotasks();
    expect(fn).toHaveBeenCalledWith("P1", 10, expect.anything());
    const section = [...el.querySelectorAll(".detail-section")].find((x) => text(x.querySelector("h3")) === "Slowest tests")!;
    click(section.querySelector<HTMLElement>(".slowest-item")!);
    expect(onSelect).toHaveBeenCalledWith(`t:${testKey(a)}`);
  });
});
