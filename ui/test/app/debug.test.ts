import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ATTACH_HELP, attachSteps, debugAvailable, debugSession, debugTests, endDebugSession } from "../../src/debug";
import { makeStore, runSummary, testNode } from "../helpers/app";

const a = testNode({ id: "Ns.C.A" });
const b = testNode({ id: "Ns.C.B" });
const other = testNode({ id: "Ns.D.X", projectId: "P2" });

beforeEach(() => vi.useFakeTimers());
afterEach(() => debugSession.set(undefined));

describe("debug sessions (ws)", () => {
  it("debugTests sends a debug command for the first test's project only", () => {
    const { transport } = makeStore({ kind: "ws" });
    debugTests([a, other, b], "Two");
    expect(transport.sent.at(-1)).toEqual({ type: "debug", projectId: "P1", testIds: [a.id, b.id] });
    expect(debugSession.peek()).toMatchObject({ label: "Two", status: "starting", projectId: "P1" });
  });

  it("does nothing without tests", () => {
    const { transport } = makeStore({ kind: "ws" });
    debugTests([], "none");
    expect(transport.sent).toEqual([]);
    expect(debugSession.peek()).toBeUndefined();
  });

  it("follows debuggerWaiting → debuggerAttached → runFinished", () => {
    const { store } = makeStore({ kind: "ws" });
    debugTests([a], "A");
    store.apply({ type: "debuggerWaiting", projectId: "P1", pid: 1234, testIds: [a.id], runId: "d1", hints: [{ editor: "rider", name: "Rider", steps: "attach!" }] });
    expect(debugSession.peek()).toMatchObject({ label: "A", status: "waiting", pid: 1234, runId: "d1" });
    store.apply({ type: "debuggerAttached", projectId: "P1", pid: 1235, runId: "d1" });
    expect(debugSession.peek()).toMatchObject({ status: "attached", pid: 1235 });
    store.apply({ type: "runFinished", run: runSummary({ runId: "other" }) });
    expect(debugSession.peek()).toBeDefined();
    store.apply({ type: "runFinished", run: runSummary({ runId: "d1" }) });
    expect(debugSession.peek()).toBeUndefined();
  });

  it("debuggerAttached without a pid keeps the known one; without a session it is ignored", () => {
    const { store } = makeStore({ kind: "ws" });
    store.apply({ type: "debuggerAttached", projectId: "P1", pid: 1, runId: "r" });
    expect(debugSession.peek()).toBeUndefined();
    debugSession.set({ label: "x", projectId: "P1", testIds: [], status: "waiting", pid: 77 });
    store.apply({ type: "debuggerAttached", projectId: "P1", runId: "r" } as never);
    expect(debugSession.peek()).toMatchObject({ status: "attached", pid: 77 });
  });

  it("a debug run started elsewhere: label from the test's display name, or the test count", () => {
    const { store } = makeStore({ kind: "ws", state: { tests: [a, b] } });
    store.apply({ type: "debuggerWaiting", projectId: "P1", pid: 1, testIds: [a.id], runId: "r", hints: [] });
    expect(debugSession.peek()?.label).toBe("A");
    debugSession.set(undefined);
    store.apply({ type: "debuggerWaiting", projectId: "P1", pid: 1, testIds: [a.id, b.id], runId: "r", hints: [] });
    expect(debugSession.peek()?.label).toBe("2 tests");
  });

  it("a rejected debug command turns a starting session into an error", () => {
    const { store } = makeStore({ kind: "ws" });
    debugTests([a], "A");
    store.apply({ type: "commandRejected", command: "run", status: 409, error: "busy" });
    expect(debugSession.peek()?.status).toBe("starting");
    store.apply({ type: "commandRejected", command: "debug", status: 409, error: "A run is in progress" });
    expect(debugSession.peek()).toMatchObject({ status: "error", message: "A run is in progress" });
    debugSession.set({ label: "x", projectId: "P1", testIds: [], status: "starting" });
    store.apply({ type: "commandRejected", command: "debug", status: 400 } as never);
    expect(debugSession.peek()?.message).toBe("The host rejected the request");
  });

  it("endDebugSession(cancel) cancels a waiting/attached run; otherwise just dismisses", () => {
    const { transport } = makeStore({ kind: "ws" });
    debugSession.set({ label: "x", projectId: "P1", testIds: [], status: "waiting" });
    endDebugSession(true);
    expect(transport.sent).toEqual([{ type: "cancel" }]);
    expect(debugSession.peek()).toBeUndefined();
    debugSession.set({ label: "x", projectId: "P1", testIds: [], status: "error" });
    endDebugSession(true);
    debugSession.set({ label: "x", projectId: "P1", testIds: [], status: "attached" });
    endDebugSession(false);
    expect(transport.sent).toHaveLength(1);
  });

  it("debugAvailable follows the connection", () => {
    const { store, transport } = makeStore({ kind: "ws", open: false });
    expect(debugAvailable()).toBe(false);
    transport.setStatus("open");
    vi.advanceTimersToNextFrame();
    expect(debugAvailable()).toBe(true);
    expect(store.$.conn()).toBe("open");
  });
});

describe("debug sessions (mock)", () => {
  it("simulates a waiting test host after 600 ms; Stop never sends cancel", () => {
    const { transport } = makeStore({ kind: "mock" });
    debugTests([a], "A");
    expect(transport.sent).toEqual([]);
    vi.advanceTimersByTime(600);
    expect(debugSession.peek()).toMatchObject({ status: "waiting", pid: 6500, runId: "mock-debug", label: "A" });
    endDebugSession(true);
    expect(transport.sent).toEqual([]);
  });

  it("the simulated wait is dropped when the session changed meanwhile", () => {
    makeStore({ kind: "mock" });
    debugTests([a], "A");
    debugSession.set(undefined);
    vi.advanceTimersByTime(600);
    expect(debugSession.peek()).toBeUndefined();
  });
});

describe("attachSteps", () => {
  it("prefers the host's hint, falls back to built-in help", () => {
    const s = { label: "x", projectId: "P1", testIds: [], status: "waiting" as const, hints: [{ editor: "rider" as const, name: "Rider", steps: "host steps", launchJson: "{}" }] };
    expect(attachSteps(s, "rider")).toEqual(s.hints[0]);
    expect(attachSteps(s, "vscode")).toBe(ATTACH_HELP.vscode);
    expect(attachSteps({ ...s, hints: undefined }, "visualstudio")).toBe(ATTACH_HELP.visualstudio);
    expect(attachSteps({ ...s, hints: undefined }, "nope" as never)).toBe(ATTACH_HELP.vscode);
  });
});
