import { beforeEach, describe, expect, it, vi } from "vitest";
import { h, root, signal } from "@eriklieben/labcoat-design-system";
import type { TestNode, TestTelemetry } from "../../../src/contract";
import { aspireFailureContext, aspireLinks, dashboardLinks, glyph, testLinks } from "../../../src/components/aspire-links";
import { testNode, text } from "../../helpers/app";
import { jsonResponse, resetStore } from "../../helpers/app3";
import { flushMicrotasks } from "../../helpers/dom";

const traced = (p: Partial<TestNode> = {}) => testNode({ traceId: "abc 123", outcome: "failed", errorMessage: "boom", ...p });
const ASPIRE = { tracingEnabled: true, aspireDashboardUrl: "http://dash:18888//" };

function renderInto(child: () => unknown) {
  let dispose!: () => void;
  const el = root((d) => {
    dispose = d;
    return h("div", null, child() as never);
  });
  document.body.append(el);
  return { el, dispose };
}

const telemetry = (p: Partial<TestTelemetry> = {}): TestTelemetry => ({ traceId: "abc 123", spans: [], logs: [], ...p });

describe("dashboardLinks()", () => {
  it("builds trace and structured-log links (trailing slashes trimmed, id encoded)", () => {
    expect(dashboardLinks("http://d:1/", "a b")).toEqual({ trace: "http://d:1/traces/detail/a%20b", logs: "http://d:1/structuredlogs?traceId=a%20b" });
  });
  it("is undefined without a dashboard or a trace id", () => {
    expect(dashboardLinks(undefined, "x")).toBeUndefined();
    expect(dashboardLinks("http://d", undefined)).toBeUndefined();
  });
});

describe("glyph()", () => {
  it("renders a sized, decorative icon span", () => {
    const g = root(() => glyph("trace", 20));
    expect(g.className).toBe("icon");
    expect(g.style.width).toBe("20px");
    const svg = g.querySelector("svg")!;
    expect(svg.getAttribute("aria-hidden")).toBe("true");
    expect(svg.hasAttribute("width")).toBe(false);
  });
});

describe("testLinks() / aspireLinks()", () => {
  beforeEach(async () => {
    await resetStore(ASPIRE);
  });

  it("links only when tracing is on", async () => {
    const t = signal(traced());
    const links = root(() => testLinks(t));
    expect(links()?.trace).toBe("http://dash:18888/traces/detail/abc%20123");
    await resetStore({ ...ASPIRE, tracingEnabled: false });
    expect(links()).toBeUndefined();
  });

  it("renders View trace + Logs buttons for a traced test, nothing otherwise", () => {
    const t = signal(traced());
    const { el } = renderInto(() => aspireLinks(t));
    const a = [...el.querySelectorAll("a")];
    expect(a.map((x) => text(x))).toEqual(["View trace", "Logs"]);
    expect(a[0].target).toBe("_blank");
    expect(a[0].rel).toBe("noreferrer");
    expect(a[0].title).toContain("abc 123");
    expect(a[1].href).toContain("structuredlogs");
    t.set(traced({ traceId: undefined }));
    expect(el.querySelectorAll("a")).toHaveLength(0);
  });
});

describe("aspireFailureContext()", () => {
  beforeEach(async () => {
    await resetStore(ASPIRE);
  });

  it("renders nothing without links", async () => {
    await resetStore({});
    const { el } = renderInto(() => aspireFailureContext(() => traced()));
    expect(el.querySelector(".aspire-context")).toBeNull();
  });

  it("fetches the telemetry and lists error spans and logs", async () => {
    const fetchMock = vi.fn(async () =>
      jsonResponse(
        telemetry({
          spans: [
            { name: "GET /a", resource: "api", status: "error", httpStatusCode: 503, durationMs: 1500, exceptionType: "X.Boom", exceptionMessage: "kaput" },
            { name: "GET /b", resource: "web", status: "error", httpStatusCode: 404, statusMessage: "nope" },
            { name: "Db", resource: "db", status: "ok", httpStatusCode: 200 },
          ],
          logs: [
            { resource: "api", level: "warning", message: "careful", timestamp: "t", category: "Cat" },
            { resource: "api", level: "critical", message: "dead", timestamp: "t" },
            { resource: "api", level: "error", message: "err", timestamp: "t" },
          ],
        }),
      ),
    );
    vi.stubGlobal("fetch", fetchMock);
    const { el } = renderInto(() => aspireFailureContext(() => traced()));
    expect(text(el.querySelector(".aspire-context-note"))).toBe("Loading service telemetry…");
    expect(el.querySelector(".aspire-context-note .spinner")).not.toBeNull();
    await flushMicrotasks();
    const url = String((fetchMock.mock.calls[0] as unknown[])[0]);
    expect(url).toContain("/api/tests/telemetry?projectId=P1&id=Ns.ClassA.Test1");
    expect(text(el.querySelector(".aspire-context-title"))).toBe("Service activity during this test");
    const rows = [...el.querySelectorAll(".aspire-row")];
    expect(rows).toHaveLength(6);
    expect(rows[0].getAttribute("title")).toBe("X.Boom: kaput");
    expect(rows[0].querySelector(".aspire-http")!.getAttribute("data-err")).toBe("5xx");
    expect(text(rows[0].querySelector(".aspire-msg"))).toBe("'kaput'");
    expect(text(rows[0].querySelector(".aspire-dur"))).toBe("1.50 s");
    expect(rows[1].querySelector(".aspire-http")!.getAttribute("data-err")).toBe("4xx");
    expect(text(rows[1].querySelector(".aspire-msg"))).toBe("'nope'");
    expect(rows[2].querySelector(".aspire-http")!.hasAttribute("data-err")).toBe(false);
    expect(rows[2].querySelector(".aspire-msg")).toBeNull();
    expect([...el.querySelectorAll(".aspire-level")].map((x) => x.textContent)).toEqual(["warn", "crit", "error"]);
    expect(rows[3].getAttribute("title")).toBe("Cat");
    expect([...el.querySelectorAll(".aspire-context-links a")].map((a) => text(a))).toEqual(["Trace of this test", "Service logs"]);
  });

  it("more than 6 rows collapse behind 'Show all N'", async () => {
    const spans = Array.from({ length: 8 }, (_, i) => ({ name: `s${i}`, resource: "api", status: "error" as const }));
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(telemetry({ spans }))));
    const { el } = renderInto(() => aspireFailureContext(() => traced()));
    await flushMicrotasks();
    expect(el.querySelectorAll(".aspire-row")).toHaveLength(6);
    const toggle = el.querySelector<HTMLButtonElement>(".raw-toggle")!;
    expect(text(toggle)).toBe("Show all 8");
    toggle.click();
    expect(el.querySelectorAll(".aspire-row")).toHaveLength(8);
    expect(text(toggle)).toBe("Show less");
  });

  it("an empty answer for a recent run is retried a few times (telemetry arrives late)", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    vi.setSystemTime(new Date("2026-10-01T10:00:05Z"));
    const fetchMock = vi.fn(async () => jsonResponse(telemetry()));
    vi.stubGlobal("fetch", fetchMock);
    const { el } = renderInto(() => aspireFailureContext(() => traced({ lastRunAt: "2026-10-01T10:00:00Z" })));
    await flushMicrotasks();
    expect(text(el.querySelector(".aspire-context-note"))).toBe("No failed requests or warnings in this test's trace.");
    for (let i = 0; i < 5; i++) {
      await vi.advanceTimersByTimeAsync(2500);
      await flushMicrotasks();
    }
    expect(fetchMock).toHaveBeenCalledTimes(4); // first try + 3 retries
  });

  it("an empty answer for an old result is not retried", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(telemetry()));
    vi.stubGlobal("fetch", fetchMock);
    renderInto(() => aspireFailureContext(() => traced({ lastRunAt: "2020-01-01T00:00:00Z" })));
    await flushMicrotasks();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("shows why telemetry is unavailable (host error body, else status)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ error: "Dashboard unreachable" }, { status: 502 })));
    const { el } = renderInto(() => aspireFailureContext(() => traced()));
    await flushMicrotasks();
    const note = el.querySelector(".aspire-context-note")!;
    expect(text(note)).toBe("Service telemetry unavailable: Dashboard unreachable");
    expect(note.getAttribute("title")).toBe("Dashboard unreachable");
  });

  it("falls back to the HTTP status when the error body is not JSON", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: false, status: 500, statusText: "Server Error", json: async () => { throw new Error("no json"); } }) as unknown as Response));
    const { el } = renderInto(() => aspireFailureContext(() => traced()));
    await flushMicrotasks();
    expect(text(el.querySelector(".aspire-context-note"))).toBe("Service telemetry unavailable: 500 Server Error");
  });

  it("a non-Error rejection is shown as text", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject("offline")));
    const { el } = renderInto(() => aspireFailureContext(() => traced()));
    await flushMicrotasks();
    expect(text(el.querySelector(".aspire-context-note"))).toBe("Service telemetry unavailable: offline");
  });

  it("does not fetch when the host says telemetry is unavailable (links stay)", async () => {
    await resetStore({ ...ASPIRE, telemetryAvailable: false });
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const { el } = renderInto(() => aspireFailureContext(() => traced()));
    await flushMicrotasks();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(el.querySelector(".aspire-context-note")).toBeNull();
    expect(el.querySelectorAll(".aspire-context-links a")).toHaveLength(2);
  });

  it("aborts the request when disposed and ignores its late answer", async () => {
    let resolve!: (r: Response) => void;
    let seenSignal: AbortSignal | undefined;
    vi.stubGlobal("fetch", vi.fn((_u: unknown, init?: RequestInit) => ((seenSignal = init?.signal ?? undefined), new Promise<Response>((r) => (resolve = r)))));
    const { el, dispose } = renderInto(() => aspireFailureContext(() => traced()));
    dispose();
    expect(seenSignal?.aborted).toBe(true);
    resolve(jsonResponse(telemetry({ spans: [{ name: "x", resource: "r", status: "error" }] })));
    await flushMicrotasks();
    expect(el.querySelector(".aspire-row")).toBeNull();
  });

  it("refetches when the test's result changes", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(telemetry()));
    vi.stubGlobal("fetch", fetchMock);
    const t = signal(traced({ lastRunAt: "2020-01-01T00:00:00Z" }));
    renderInto(() => aspireFailureContext(t));
    await flushMicrotasks();
    t.set({ ...t.peek(), durationMs: 5 }); // same result key: no refetch
    await flushMicrotasks();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    t.set({ ...t.peek(), lastRunAt: "2020-01-02T00:00:00Z" });
    await flushMicrotasks();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("uses the in-browser mock with ?mock (no network)", async () => {
    await resetStore(ASPIRE, { kind: "mock" });
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const { el } = renderInto(() => aspireFailureContext(() => traced({ outcome: "passed", lastRunAt: "2020-01-01T00:00:00Z" })));
    await vi.dynamicImportSettled();
    await vi.runAllTimersAsync();
    await flushMicrotasks();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(text(el.querySelector(".aspire-context-note"))).toBe("No failed requests or warnings in this test's trace.");
  });
});
