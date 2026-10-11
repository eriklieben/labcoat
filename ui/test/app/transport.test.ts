import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { WsTransport, type ConnectionStatus, type TransportHandlers } from "../../src/transport";
import type { ServerEvent } from "../../src/contract";
import { FakeWebSocket } from "../helpers/dom";

function handlers() {
  const events: ServerEvent[] = [];
  const statuses: [ConnectionStatus, { retryInMs?: number; attempt?: number } | undefined][] = [];
  const onFirstFailure = vi.fn<() => void>();
  const h = {
    onEvent: (e: ServerEvent) => void events.push(e),
    onStatus: (s: ConnectionStatus, info?: { retryInMs?: number; attempt?: number }) => void statuses.push([s, info]),
    onFirstFailure,
  } satisfies TransportHandlers;
  return { h, events, statuses };
}

const res = (status: number, body: unknown = {}, text = "") =>
  ({ ok: status >= 200 && status < 300, status, statusText: status === 500 ? "Internal Server Error" : "", json: async () => body, text: async () => text }) as unknown as Response;

let t: WsTransport;
beforeEach(() => {
  vi.useFakeTimers();
  vi.spyOn(Math, "random").mockReturnValue(0.5); // jitter factor 1.0
  t = new WsTransport();
});
afterEach(() => {
  t.stop();
  history.replaceState(null, "", "/");
});

describe("WsTransport connection", () => {
  it("connects to ./ws in lean mode and reports connecting → open", () => {
    const { h, statuses } = handlers();
    t.start(h);
    expect(t.kind).toBe("ws");
    expect(FakeWebSocket.last!.url).toBe("ws://localhost:3000/ws?lean=1");
    expect(statuses).toEqual([["connecting", { attempt: 0 }]]);
    FakeWebSocket.last!.open();
    expect(statuses.at(-1)).toEqual(["open", undefined]);
  });

  it("?lean=0 keeps the legacy payloads; https pages use wss and keep the path prefix", () => {
    history.replaceState(null, "", "/labcoat/?lean=0");
    t.start(handlers().h);
    expect(FakeWebSocket.last!.url).toBe("ws://localhost:3000/labcoat/ws");
    t.stop();
    const loc = { protocol: "https:", href: "https://example.org/x/", search: "" };
    vi.stubGlobal("location", loc);
    const t2 = new WsTransport();
    t2.start(handlers().h);
    expect(FakeWebSocket.last!.url).toBe("wss://example.org/x/ws?lean=1");
    t2.stop();
  });

  it("parses JSON messages into events; ignores non-events and logs bad JSON", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { h, events } = handlers();
    t.start(h);
    const ws = FakeWebSocket.last!;
    ws.open();
    ws.message({ type: "runsUpdated", runs: [] });
    ws.message("null");
    ws.message("42");
    ws.message({ notAnEvent: true });
    ws.onmessage?.({ data: new ArrayBuffer(2) } as MessageEvent);
    ws.message("{bad json");
    expect(events).toEqual([{ type: "runsUpdated", runs: [] }]);
    expect(warn).toHaveBeenCalledTimes(2);
    ws.error(); // no-op: onclose follows
  });

  it("reconnects with exponential backoff (jittered, capped at 15 s) and resets after a successful open", () => {
    const { h, statuses } = handlers();
    t.start(h);
    FakeWebSocket.last!.open();
    const delays: number[] = [];
    for (let i = 0; i < 7; i++) {
      FakeWebSocket.last!.drop();
      const st = statuses.at(-1)!;
      expect(st[0]).toBe("reconnecting");
      delays.push(st[1]!.retryInMs!);
      const before = FakeWebSocket.instances.length;
      vi.advanceTimersByTime(st[1]!.retryInMs! - 1);
      expect(FakeWebSocket.instances).toHaveLength(before);
      vi.advanceTimersByTime(1);
      expect(FakeWebSocket.instances).toHaveLength(before + 1);
      expect(statuses.at(-1)).toEqual(["reconnecting", { attempt: i + 1 }]);
    }
    expect(delays).toEqual([500, 1000, 2000, 4000, 8000, 15000, 15000]);
    FakeWebSocket.last!.open();
    FakeWebSocket.last!.drop();
    expect(statuses.at(-1)![1]!.retryInMs).toBe(500);
    expect(h.onFirstFailure).not.toHaveBeenCalled();
  });

  it("jitter spans ±20 %", () => {
    const { h, statuses } = handlers();
    vi.mocked(Math.random).mockReturnValue(0);
    t.start(h);
    FakeWebSocket.last!.drop();
    expect(statuses.at(-1)![1]!.retryInMs).toBe(400);
    vi.mocked(Math.random).mockReturnValue(0.999999);
    vi.advanceTimersByTime(400);
    FakeWebSocket.last!.drop();
    expect(statuses.at(-1)![1]!.retryInMs).toBe(1200);
  });

  it("calls onFirstFailure once when the very first attempt fails", () => {
    const { h } = handlers();
    t.start(h);
    FakeWebSocket.last!.drop();
    expect(h.onFirstFailure).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(500);
    FakeWebSocket.last!.drop();
    expect(h.onFirstFailure).toHaveBeenCalledTimes(1);
  });

  it("a WebSocket constructor error schedules a reconnect", () => {
    const { h, statuses } = handlers();
    FakeWebSocket.throwOnCreate = true;
    t.start(h);
    expect(FakeWebSocket.instances).toHaveLength(0);
    expect(statuses.at(-1)).toEqual(["reconnecting", { retryInMs: 500, attempt: 1 }]);
    vi.advanceTimersByTime(500);
    expect(FakeWebSocket.instances).toHaveLength(1);
  });

  it("ignores close events of a socket it already replaced", () => {
    const { h, statuses } = handlers();
    t.start(h);
    const first = FakeWebSocket.last!;
    first.drop();
    vi.advanceTimersByTime(500);
    const n = statuses.length;
    first.onclose?.({} as CloseEvent);
    expect(statuses).toHaveLength(n);
  });

  it("retryNow reconnects at once unless a socket is connecting/open", () => {
    const { h } = handlers();
    t.start(h);
    t.retryNow();
    expect(FakeWebSocket.instances).toHaveLength(1); // still connecting
    FakeWebSocket.last!.drop();
    t.retryNow();
    expect(FakeWebSocket.instances).toHaveLength(2);
    vi.advanceTimersByTime(60_000); // the scheduled timer was cleared
    expect(FakeWebSocket.instances).toHaveLength(2);
  });

  it("stop() closes the socket and never reconnects", () => {
    const { h, statuses } = handlers();
    t.start(h);
    const ws = FakeWebSocket.last!;
    ws.open();
    t.stop();
    expect(ws.readyState).toBe(3);
    const n = statuses.length;
    vi.advanceTimersByTime(60_000);
    expect(FakeWebSocket.instances).toHaveLength(1);
    expect(statuses).toHaveLength(n);
    t.retryNow(); // connect() bails out after stop
    expect(FakeWebSocket.instances).toHaveLength(1);
  });

  it("stop() before a scheduled reconnect fires cancels it", () => {
    const { h } = handlers();
    t.start(h);
    FakeWebSocket.last!.drop();
    t.stop();
    vi.advanceTimersByTime(60_000);
    expect(FakeWebSocket.instances).toHaveLength(1);
  });
});

describe("WsTransport commands", () => {
  it("sends JSON while open", () => {
    t.start(handlers().h);
    const ws = FakeWebSocket.last!;
    ws.open();
    t.send({ type: "cancel" });
    expect(ws.sent).toEqual(['{"type":"cancel"}']);
  });

  it("queues commands while disconnected (latest per type, every run kept) and flushes them on open", () => {
    t.start(handlers().h);
    t.send({ type: "setWatch", enabled: true });
    t.send({ type: "run", testIds: ["a"] });
    t.send({ type: "setWatch", enabled: false });
    t.send({ type: "run", testIds: ["b"] });
    const ws = FakeWebSocket.last!;
    ws.open();
    expect(ws.sent.map((s) => JSON.parse(s))).toEqual([
      { type: "run", testIds: ["a"] },
      { type: "setWatch", enabled: false },
      { type: "run", testIds: ["b"] },
    ]);
    ws.drop();
    vi.advanceTimersByTime(500);
    FakeWebSocket.last!.open();
    expect(FakeWebSocket.last!.sent).toEqual([]); // queue emptied
  });
});

describe("WsTransport REST", () => {
  it("fetchSource: GET /api/source?path=…, text body; errors carry the status", async () => {
    const fetch = vi.fn(async () => res(200, {}, "class A {}"));
    vi.stubGlobal("fetch", fetch);
    expect(await t.fetchSource("/a b/c.cs")).toBe("class A {}");
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toBe("http://localhost:3000/api/source?path=%2Fa%20b%2Fc.cs");
    fetch.mockResolvedValueOnce(res(403));
    await expect(t.fetchSource("/x")).rejects.toMatchObject({ status: 403, message: "403" });
  });

  it("fetchTestCoverage: query, signal, 404 → null, errors throw", async () => {
    const report = { runId: "r" };
    const fetch = vi.fn(async () => res(200, report));
    vi.stubGlobal("fetch", fetch);
    const ac = new AbortController();
    expect(await t.fetchTestCoverage("P1", ["a", "b"], ac.signal)).toEqual(report);
    const [url, init] = fetch.mock.calls[0] as unknown as [URL, RequestInit];
    expect(String(url)).toBe("http://localhost:3000/api/coverage/test?projectId=P1&id=a&id=b");
    expect(init.signal).toBe(ac.signal);
    expect(await t.fetchTestCoverage(undefined, ["a"])).toEqual(report);
    expect(String((fetch.mock.calls[1] as unknown[])[0])).toBe("http://localhost:3000/api/coverage/test?id=a");
    fetch.mockResolvedValueOnce(res(404));
    expect(await t.fetchTestCoverage("P1", ["a"])).toBeNull();
    fetch.mockResolvedValueOnce(res(500));
    await expect(t.fetchTestCoverage("P1", ["a"])).rejects.toMatchObject({ status: 500, message: "500 Internal Server Error" });
  });

  it("fetchRun: GET /api/runs/{id}, 404 → null, errors throw", async () => {
    const details = { run: { runId: "a/b" }, results: [] };
    const fetch = vi.fn(async () => res(200, details));
    vi.stubGlobal("fetch", fetch);
    expect(await t.fetchRun("a/b")).toEqual(details);
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toBe("http://localhost:3000/api/runs/a%2Fb");
    fetch.mockResolvedValueOnce(res(404));
    expect(await t.fetchRun("x")).toBeNull();
    fetch.mockResolvedValueOnce(res(502));
    await expect(t.fetchRun("x")).rejects.toMatchObject({ status: 502 });
  });
});
