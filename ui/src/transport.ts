import type { ClientCommand, CoverageReport, RunDetails, ServerEvent } from "./contract";

export type ConnectionStatus = "connecting" | "open" | "reconnecting";

export interface TransportHandlers {
  onEvent: (e: ServerEvent) => void;
  onStatus: (s: ConnectionStatus, info?: { retryInMs?: number; attempt?: number }) => void;
  /** Called once if the very first connection attempt fails (used for dev mock fallback). */
  onFirstFailure?: () => void;
}

export interface Transport {
  readonly kind: "ws" | "mock";
  start(h: TransportHandlers): void;
  send(cmd: ClientCommand): void;
  stop(): void;
  /** Force an immediate reconnect attempt (resets backoff). */
  retryNow?(): void;
  /** Fetch a source file for the coverage viewer (GET /api/source). */
  fetchSource(path: string): Promise<string>;
  /** Cached per-test coverage (GET /api/coverage/test); null when none was collected. Never starts a run. */
  fetchTestCoverage(projectId: string | undefined, testIds: string[], signal?: AbortSignal): Promise<CoverageReport | null>;
  /** A persisted past run with its per-test results (GET /api/runs/{runId}); null when unknown. */
  fetchRun(runId: string): Promise<RunDetails | null>;
}

const MIN_BACKOFF = 500;
const MAX_BACKOFF = 15000;

export class WsTransport implements Transport {
  readonly kind = "ws" as const;
  private ws?: WebSocket;
  private h?: TransportHandlers;
  private attempt = 0;
  private timer?: number;
  private stopped = false;
  private everOpened = false;
  private queue: ClientCommand[] = [];

  start(h: TransportHandlers) {
    this.h = h;
    this.connect();
  }

  private url() {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    // Resolve relative to the page so the UI also works when served under a path prefix.
    const base = new URL("./ws", location.href);
    // lean mode (Round 4): no history / console output in snapshots and updates; the store fetches them on demand.
    // `?lean=0` in the page URL keeps the legacy full payloads.
    const lean = new URLSearchParams(location.search).get("lean") !== "0";
    return `${proto}//${base.host}${base.pathname}${lean ? "?lean=1" : ""}`;
  }

  private connect() {
    if (this.stopped) return;
    this.h?.onStatus(this.attempt === 0 ? "connecting" : "reconnecting", { attempt: this.attempt });
    let ws: WebSocket;
    try {
      ws = new WebSocket(this.url());
    } catch {
      this.scheduleReconnect();
      return;
    }
    this.ws = ws;
    ws.onopen = () => {
      this.attempt = 0;
      this.everOpened = true;
      this.h?.onStatus("open");
      const q = this.queue;
      this.queue = [];
      q.forEach((c) => this.send(c));
    };
    ws.onmessage = (m) => {
      try {
        const evt = JSON.parse(typeof m.data === "string" ? m.data : "") as ServerEvent;
        if (evt && typeof evt === "object" && "type" in evt) this.h?.onEvent(evt);
      } catch (err) {
        console.warn("[ws] bad message", err);
      }
    };
    ws.onclose = () => {
      if (this.ws !== ws) return;
      this.ws = undefined;
      if (!this.everOpened && this.attempt === 0) this.h?.onFirstFailure?.();
      this.scheduleReconnect();
    };
    ws.onerror = () => {
      /* onclose follows */
    };
  }

  private scheduleReconnect() {
    if (this.stopped) return;
    this.attempt++;
    const exp = Math.min(MAX_BACKOFF, MIN_BACKOFF * 2 ** (this.attempt - 1));
    const delay = Math.round(exp * (0.8 + Math.random() * 0.4));
    this.h?.onStatus("reconnecting", { retryInMs: delay, attempt: this.attempt });
    window.clearTimeout(this.timer);
    this.timer = window.setTimeout(() => this.connect(), delay);
  }

  retryNow() {
    window.clearTimeout(this.timer);
    if (this.ws && this.ws.readyState <= 1) return;
    this.connect();
  }

  send(cmd: ClientCommand) {
    if (this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(cmd));
    } else {
      // Commands issued while disconnected are flushed on reconnect (dedupe trivially by type).
      this.queue = this.queue.filter((c) => c.type !== cmd.type || cmd.type === "run").concat(cmd);
    }
  }

  async fetchSource(path: string): Promise<string> {
    const res = await fetch(new URL(`./api/source?path=${encodeURIComponent(path)}`, location.href));
    if (!res.ok) throw Object.assign(new Error(`${res.status} ${res.statusText || ""}`.trim()), { status: res.status });
    return res.text();
  }

  async fetchTestCoverage(projectId: string | undefined, testIds: string[], signal?: AbortSignal): Promise<CoverageReport | null> {
    const q = new URLSearchParams();
    if (projectId) q.set("projectId", projectId);
    for (const id of testIds) q.append("id", id);
    const res = await fetch(new URL(`./api/coverage/test?${q}`, location.href), { signal });
    if (res.status === 404) return null;
    if (!res.ok) throw Object.assign(new Error(`${res.status} ${res.statusText || ""}`.trim()), { status: res.status });
    return (await res.json()) as CoverageReport;
  }

  async fetchRun(runId: string): Promise<RunDetails | null> {
    const res = await fetch(new URL(`./api/runs/${encodeURIComponent(runId)}`, location.href));
    if (res.status === 404) return null;
    if (!res.ok) throw Object.assign(new Error(`${res.status} ${res.statusText || ""}`.trim()), { status: res.status });
    return (await res.json()) as RunDetails;
  }

  stop() {
    this.stopped = true;
    window.clearTimeout(this.timer);
    const ws = this.ws;
    this.ws = undefined;
    ws?.close();
  }
}
