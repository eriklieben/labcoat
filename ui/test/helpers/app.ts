/**
 * App test helpers: fixtures that follow docs/contract.md, a fake Transport and a Store wired to it.
 *
 *   const { store, transport } = makeStore();          // installed as the app store (getStore())
 *   store.apply(stateEvent({ tests: [testNode()] }));  // or transport.emit(...)
 *   await flushFrame();                                // the store publishes once per animation frame
 */
import { vi } from "vitest";
import type {
  ClientCommand,
  CoverageReport,
  ExplorerState,
  FileCoverage,
  RunDetails,
  RunSummary,
  ServerEvent,
  TestNode,
  TestProject,
} from "../../src/contract";
import { Store, setStore } from "../../src/store";
import type { ConnectionStatus, Transport, TransportHandlers } from "../../src/transport";
import { flushMicrotasks } from "./dom";

export function testNode(p: Partial<TestNode> & { id?: string } = {}): TestNode {
  const id = p.id ?? "Ns.ClassA.Test1";
  const parts = id.split(".");
  return {
    id,
    projectId: "P1",
    displayName: parts[parts.length - 1],
    namespace: parts.slice(0, -2).join(".") || undefined,
    className: parts[parts.length - 2],
    methodName: parts[parts.length - 1],
    traits: [],
    outcome: "notRun",
    ...p,
  };
}

export function project(p: Partial<TestProject> = {}): TestProject {
  return { id: "P1", name: "Project.One", path: "/src/Project.One/Project.One.csproj", status: "idle", ...p };
}

export function runSummary(p: Partial<RunSummary> = {}): RunSummary {
  return { runId: "r1", startedAt: "2026-10-01T10:00:00Z", total: 0, passed: 0, failed: 0, skipped: 0, ...p };
}

export function explorerState(p: Partial<ExplorerState> = {}): ExplorerState {
  return { projects: [project()], tests: [], watch: false, coverageEnabled: false, ...p };
}

export const stateEvent = (p: Partial<ExplorerState> = {}): ServerEvent => ({ type: "state", state: explorerState(p) });

/** A full run's report, as the host sends it: the run's own report (coverage) is also the Coverage page's (fullCoverage). */
export const asFull = (c: CoverageReport): Pick<ExplorerState, "coverage" | "fullCoverage"> => ({ coverage: c, fullCoverage: c });

export function fileCoverage(p: Partial<FileCoverage> = {}): FileCoverage {
  return {
    path: "/src/Lib/Calc.cs",
    relativePath: "Lib/Calc.cs",
    module: "Lib",
    linesCovered: 3,
    linesValid: 4,
    lineRate: 0.75,
    branchesCovered: 1,
    branchesValid: 2,
    branchRate: 0.5,
    lines: [
      { line: 1, hits: 1 },
      { line: 2, hits: 2 },
      { line: 3, hits: 0 },
      { line: 4, hits: 1, branchesCovered: 1, branchesTotal: 2 },
    ],
    ...p,
  };
}

export function coverageReport(p: Partial<CoverageReport> = {}): CoverageReport {
  return {
    runId: "r1",
    generatedAt: "2026-10-01T10:00:05Z",
    projectIds: ["P1"],
    lineRate: 0.75,
    branchRate: 0.5,
    linesCovered: 3,
    linesValid: 4,
    branchesCovered: 1,
    branchesValid: 2,
    files: [fileCoverage()],
    ...p,
  };
}

/** A Transport whose server side the test drives: emit() events, setStatus(), and inspect sent commands. */
export class FakeTransport implements Transport {
  kind: "ws" | "mock";
  handlers?: TransportHandlers;
  sent: ClientCommand[] = [];
  stopped = false;
  fetchSource = vi.fn(async (path: string) => `// source of ${path}\nline 2\nline 3\nline 4\n`);
  fetchTestCoverage = vi.fn(async (_p: string | undefined, _ids: string[], _s?: AbortSignal): Promise<CoverageReport | null> => null);
  fetchRun = vi.fn(async (_id: string): Promise<RunDetails | null> => null);
  retryNow = vi.fn();
  constructor(kind: "ws" | "mock" = "ws") {
    this.kind = kind;
  }
  start(h: TransportHandlers) {
    this.handlers = h;
  }
  send(cmd: ClientCommand) {
    this.sent.push(cmd);
  }
  stop() {
    this.stopped = true;
  }
  emit(e: ServerEvent) {
    this.handlers!.onEvent(e);
  }
  setStatus(s: ConnectionStatus, info?: { retryInMs?: number; attempt?: number }) {
    this.handlers!.onStatus(s, info);
  }
}

/** A Store attached to a FakeTransport and installed as the app store. `open` (default true) marks it connected. */
export function makeStore(opts: { kind?: "ws" | "mock"; open?: boolean; state?: Partial<ExplorerState> } = {}) {
  const kind = opts.kind ?? "mock";
  const store = new Store(kind);
  const transport = new FakeTransport(kind);
  store.attach(transport);
  if (opts.open !== false) transport.setStatus("open");
  if (opts.state) store.apply(stateEvent(opts.state));
  setStore(store);
  return { store, transport };
}

/**
 * Let one animation frame pass (the store publishes its state once per frame), then flush microtasks.
 * Works with real and fake timers.
 */
export async function flushFrame() {
  if (vi.isFakeTimers()) vi.advanceTimersToNextFrame();
  else await new Promise<void>((r) => requestAnimationFrame(() => r()));
  await flushMicrotasks();
}

/** Mount `el` into the document (connectedCallback → render) and return it. */
export function mount<E extends Element>(el: E, parent: Element = document.body): E {
  parent.append(el);
  return el;
}

/** Dispatch a keydown on `target` (bubbles, cancelable); returns the event (check defaultPrevented). */
export function key(target: EventTarget, k: string, init: KeyboardEventInit = {}): KeyboardEvent {
  const e = new KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(e);
  return e;
}

/** Click (MouseEvent, bubbles). */
export function click(target: Element, init: MouseEventInit = {}): MouseEvent {
  const e = new MouseEvent("click", { bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(e);
  return e;
}

/** Set an input's value and fire `input`. */
export function type(input: HTMLInputElement | HTMLTextAreaElement, value: string) {
  input.value = value;
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

export const text = (el: Element | null | undefined) => (el?.textContent ?? "").replace(/\s+/g, " ").trim();
