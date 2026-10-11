/**
 * Live console output of running tests (server event `testOutput`, docs/contract.md "Live console output").
 *
 * Kept outside the store's AppState (which is copied and published per frame): one mutable buffer per test, a single
 * `liveTick` signal bumped at most once per frame when any buffer changed. Buffers exist only for tests that are
 * running, the test shown in the detail pane (pinned) and the last {@link KEEP_FINISHED} finished ones (their "live-only"
 * lines: output the test framework did not capture, e.g. xUnit console output or MSTest DisplayMessage). Each buffer is
 * capped like the host caps a result: the first 16 K characters + the last 48 K, with an "omitted" count in between.
 */
import { signal, type ReadSignal } from "@eriklieben/labcoat-design-system/core";
import type { ServerEvent, TestNode, TestOutputEvent } from "./contract";
import { serverEventListeners } from "./server-events";
import { getStore } from "./store";
import { loadPref, savePref } from "./util";

export const MAX_CHARS = 64 * 1024;
export const HEAD_CHARS = 16 * 1024;
export const KEEP_FINISHED = 20;

export type LiveStream = TestOutputEvent["stream"];

export interface LiveLine {
  text: string;
  stream: LiveStream;
  err?: boolean;
  shared?: boolean;
  /** no newline yet: the next chunk of the same kind continues it */
  open?: boolean;
}

export interface LiveBuffer {
  key: string;
  lines: LiveLine[];
  /** last seq applied (0 = nothing yet) */
  seq: number;
  chars: number;
  /** lines [0, headLines) are the kept head; characters dropped after them */
  headLines: number;
  omitted: number;
  /** bumped when lines were removed or replaced (a renderer must rebuild instead of appending) */
  rev: number;
  /** the test reported its result (the buffer only matters for its live-only lines now) */
  finished: boolean;
  /** some events were missed (reconnect / gap in seq) */
  gap?: boolean;
}

type Lookup = (key: string) => TestNode | undefined;

const isLive = (t: TestNode | undefined) => t?.outcome === "running" || t?.outcome === "queued";

export class LiveOutputStore {
  private readonly buffers = new Map<string, LiveBuffer>();
  private finishedOrder: string[] = [];
  private readonly pinned = new Map<string, number>();
  private readonly running = new Set<string>(); // keys in start order (Set keeps insertion order)
  private shown?: string;
  private readonly tickSig = signal(0);
  private scheduled = false;
  /** "Follow running test": the tree key (`t:<projectId>|<id>`) the shell should select, set while following */
  readonly followTarget = signal<string | undefined>(undefined);
  readonly follow = signal<boolean>(loadPref("liveFollow", false));
  /** mirror live lines into the Output panel (as info / warning log lines) */
  readonly mirror = signal<boolean>(loadPref("liveMirror", false));
  /** unsubscribes from server events (set by initLiveOutput) */
  dispose: () => void = () => {};

  constructor(
    private readonly lookup: Lookup,
    private readonly log?: (entry: { projectId: string; level: "info" | "warning"; message: string }) => void,
    private readonly schedule: (cb: () => void) => void = (cb) => requestAnimationFrame(cb),
  ) {}

  /** Bumped (at most once per frame) when any buffer changed. */
  get tick(): ReadSignal<number> {
    return this.tickSig;
  }

  get(key: string): LiveBuffer | undefined {
    return this.buffers.get(key);
  }

  /** Number of buffers held (memory checks). */
  get size() {
    return this.buffers.size;
  }

  setFollow(on: boolean) {
    this.follow.set(on);
    savePref("liveFollow", on);
    if (on) this.updateFollow();
    else this.followTarget.set(undefined);
  }

  setMirror(on: boolean) {
    this.mirror.set(on);
    savePref("liveMirror", on);
  }

  /** The detail pane shows this test: keep its buffer after it finished. Returns the unpin function. */
  pin(key: string): () => void {
    this.pinned.set(key, (this.pinned.get(key) ?? 0) + 1);
    this.shown = key;
    return () => {
      const n = (this.pinned.get(key) ?? 1) - 1;
      if (n <= 0) this.pinned.delete(key);
      else this.pinned.set(key, n);
      if (this.shown === key) this.shown = undefined;
      this.prune();
    };
  }

  apply(e: ServerEvent) {
    switch (e.type) {
      case "testOutput":
        this.onOutput(e);
        break;
      case "testsUpdated":
        for (const t of e.tests) this.onTest(`${t.projectId}|${t.id}`, t);
        this.prune();
        this.updateFollow();
        break;
      case "testResults":
        this.onResults(e);
        break;
      case "state":
        this.onState();
        break;
      case "runFinished":
        for (const k of this.buffers.keys()) if (!isLive(this.lookup(k))) this.finish(k);
        this.running.clear();
        this.prune();
        break;
    }
  }

  private onResults(e: Extract<ServerEvent, { type: "testResults" }>) {
    // patches (Round 7): the outcome the host sent, else the test as it is now
    for (const g of e.projects)
      for (const p of g.results) {
        const k = `${g.projectId}|${p.id}`;
        this.onTest(k, p.outcome ? ({ outcome: p.outcome } as TestNode) : this.lookup(k));
      }
    this.prune();
    this.updateFollow();
  }

  private onState() {
    for (const k of this.buffers.keys()) this.onTest(k, this.lookup(k));
    for (const k of this.running) if (!isLive(this.lookup(k))) this.running.delete(k);
    this.prune();
  }

  private onTest(key: string, t: TestNode | undefined) {
    const b = this.buffers.get(key);
    if (t?.outcome === "running") {
      this.running.add(key);
      // a new execution of a test whose previous one we still hold
      if (b?.finished) this.reset(b);
      return;
    }
    this.running.delete(key);
    if (t?.outcome === "queued") {
      if (b?.finished) this.reset(b);
      return;
    }
    if (b && !b.finished) this.finish(key);
  }

  private finish(key: string) {
    const b = this.buffers.get(key);
    if (!b || b.finished) return;
    b.finished = true;
    this.finishedOrder = [...this.finishedOrder.filter((k) => k !== key), key];
    this.changed();
  }

  private reset(b: LiveBuffer) {
    b.lines = [];
    b.seq = 0;
    b.chars = 0;
    b.headLines = 0;
    b.omitted = 0;
    b.rev++;
    b.finished = false;
    b.gap = undefined;
    this.finishedOrder = this.finishedOrder.filter((k) => k !== b.key);
    this.changed();
  }

  /** Drop buffers nobody needs: finished, not pinned, beyond the last KEEP_FINISHED (or without live-only lines). */
  private prune() {
    const keep = new Set(this.finishedOrder.slice(-KEEP_FINISHED));
    for (const [k, b] of this.buffers) {
      if (!b.finished || this.pinned.has(k)) continue;
      if (!keep.has(k) || b.lines.length === 0) {
        this.buffers.delete(k);
        this.finishedOrder = this.finishedOrder.filter((x) => x !== k);
      }
    }
  }

  private onOutput(e: TestOutputEvent) {
    const key = `${e.projectId}|${e.id}`;
    const t = this.lookup(key);
    if (!isLive(t)) return; // the final result is authoritative: late output of a finished test is dropped
    let b = this.buffers.get(key);
    if (!b) {
      b = { key, lines: [], seq: 0, chars: 0, headLines: 0, omitted: 0, rev: 0, finished: false };
      this.buffers.set(key, b);
    }
    if (b.finished) this.reset(b);
    if (e.seq === 1 && b.seq > 0) this.reset(b); // the host restarted this test's execution (repeat runs)
    else if (e.seq <= b.seq) return; // duplicate
    else if (b.seq > 0 && e.seq > b.seq + 1) b.gap = true;
    b.seq = e.seq;
    appendText(b, e);
    this.running.add(key);
    if (this.mirror.peek() && this.log) {
      const name = t?.displayName ?? e.id;
      for (const line of e.text.replace(/\n$/, "").split("\n"))
        this.log({ projectId: e.projectId, level: e.stream === "stderr" || e.err ? "warning" : "info", message: `${name} │ ${line}` });
    }
    this.changed();
  }

  /** Seed a buffer from GET /api/tests/live-output (a test that was already running when this page loaded). */
  seed(key: string, snapshot: { seq: number; chunks: { stream: LiveStream; text: string; shared?: boolean; err?: boolean }[] }) {
    let b = this.buffers.get(key);
    if (b && b.seq >= snapshot.seq) return;
    if (!b) {
      b = { key, lines: [], seq: 0, chars: 0, headLines: 0, omitted: 0, rev: 0, finished: false };
      this.buffers.set(key, b);
    } else this.reset(b);
    for (const c of snapshot.chunks) appendText(b, c);
    b.seq = snapshot.seq;
    b.rev++;
    this.changed();
  }

  private updateFollow() {
    if (!this.follow.peek()) return;
    const shownRunning = this.shown && this.running.has(this.shown) && isLive(this.lookup(this.shown));
    if (shownRunning) return;
    const next = [...this.running].find((k) => this.lookup(k)?.outcome === "running");
    if (next && next !== this.shown) {
      this.followTarget.set(undefined); // re-select even when it is the same test as last time
      this.followTarget.set(`t:${next}`);
    }
  }

  private changed() {
    if (this.scheduled) return;
    this.scheduled = true;
    this.schedule(() => {
      this.scheduled = false;
      this.tickSig.update((n) => n + 1);
    });
  }
}

/** Appends a chunk (split into lines, continuing an open line of the same kind) and applies the 64 K head + tail cap. */
export function appendText(b: LiveBuffer, c: { stream: LiveStream; text: string; shared?: boolean; err?: boolean }) {
  if (!c.text) return;
  const parts = c.text.split("\n");
  const endsOpen = parts.at(-1) !== "";
  if (!endsOpen) parts.pop();
  const last = b.lines.at(-1);
  parts.forEach((text, i) => {
    const open = i === parts.length - 1 && endsOpen;
    if (i === 0 && last?.open && last.stream === c.stream && !!last.err === !!c.err && !!last.shared === !!c.shared) {
      last.text += text;
      last.open = open || undefined;
    } else {
      b.lines.push({ text, stream: c.stream, err: c.err || undefined, shared: c.shared || undefined, open: open || undefined });
    }
    b.chars += text.length + (open ? 0 : 1);
  });
  // head: the lines that end within the first HEAD_CHARS characters
  if (b.omitted === 0) {
    let n = 0;
    let chars = 0;
    for (const l of b.lines) {
      if (chars + l.text.length + 1 > HEAD_CHARS) break;
      chars += l.text.length + 1;
      n++;
    }
    b.headLines = n;
  }
  let removed = false;
  while (b.chars > MAX_CHARS && b.lines.length > b.headLines + 1) {
    const [drop] = b.lines.splice(b.headLines, 1);
    const size = drop.text.length + 1;
    b.chars -= size;
    b.omitted += size;
    removed = true;
  }
  if (removed) b.rev++;
}

/**
 * Lines of a finished test's live buffer that are not in its final output: what the framework did not capture (the
 * final output stays authoritative, so streamed copies of captured lines are dropped). Lines that may belong to another
 * test (`shared`) are left out. Empty while the final output is still loading or was truncated.
 */
export function residualLines(b: LiveBuffer | undefined, t: Pick<TestNode, "stdOut" | "stdErr" | "hasOutput" | "outputTruncated">): LiveLine[] {
  if (!b?.lines.length) return [];
  if (t.outputTruncated) return [];
  if (t.hasOutput && t.stdOut == null && t.stdErr == null) return []; // lean: still loading
  const final = new Set(
    `${t.stdOut ?? ""}\n${t.stdErr ?? ""}`
      .replace(/\r\n?/g, "\n")
      .split("\n")
      .map((l) => l.trimEnd()),
  );
  return b.lines.filter((l) => !l.shared && l.text.trim() !== "" && !final.has(l.text.trimEnd()));
}

// ---- app singleton ---------------------------------------------------------------------------------------------

let live: LiveOutputStore | undefined;

type StoreLike = { get: () => { tests: Map<string, TestNode> }; apply: (e: ServerEvent) => void };

/** Creates the app's live output store and subscribes it to server events (main.ts, before the transport starts). */
export function initLiveOutput(store: StoreLike): LiveOutputStore {
  live?.dispose();
  const s = new LiveOutputStore(
    (k) => store.get().tests.get(k),
    (entry) => store.apply({ type: "log", projectId: entry.projectId, level: entry.level, message: entry.message, timestamp: new Date().toISOString() }),
  );
  const listener = (e: { type: string }) => s.apply(e as ServerEvent);
  serverEventListeners.add(listener);
  s.dispose = () => serverEventListeners.delete(listener);
  live = s;
  return s;
}

/** The app's live output store (initialised from the main store on first use when main.ts did not). */
export function getLiveOutput(): LiveOutputStore {
  live ??= initLiveOutput({ get: () => getStore().get(), apply: (e) => getStore().apply(e) });
  return live;
}
