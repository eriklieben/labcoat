/**
 * Debug test (review 2, #7; docs/contract.md "Round 4 → Debug a test"): run tests in a fresh test host that waits for a
 * debugger (TESTINGPLATFORM_WAIT_ATTACH_DEBUGGER=1). Flow: WS `{ type: "debug", projectId, testIds }` → runStarted
 * (debug: true) → `debuggerWaiting { pid, hints }` → the shell's message bar "Waiting for debugger — attach to PID 1234"
 * with the configured editor's attach steps and Copy PID → `debuggerAttached` → results → runFinished (the bar goes).
 * Stop = cancel (the host kills the waiting process). With ?mock the flow is simulated.
 */
import { signal } from "@eriklieben/labcoat-design-system/core";
import type { CommandRejectedEvent, DebugHint, TestNode } from "./contract";
import { getStore, serverEventListeners } from "./store";

export interface DebugSession {
  /** what is being debugged ("PlaceOrder_SendsConfirmationEmail", "OrderTests (3 tests)") */
  label: string;
  projectId: string;
  testIds: string[];
  status: "starting" | "waiting" | "attached" | "error";
  pid?: number;
  runId?: string;
  hints?: DebugHint[];
  message?: string;
}

/** The debug session shown in the shell's message bar (undefined = none). */
export const debugSession = signal<DebugSession | undefined>(undefined);

/** Debug actions are offered while connected (the host supports it since Round 4; the mock simulates it). */
export const debugAvailable = () => getStore().$.conn() === "open";

/** Start `tests` (one project: the first test's) in debug mode. */
export function debugTests(tests: TestNode[], label: string): void {
  if (!tests.length) return;
  const projectId = tests[0].projectId;
  const testIds = tests.filter((t) => t.projectId === projectId).map((t) => t.id);
  debugSession.set({ label, projectId, testIds, status: "starting" });
  const store = getStore();
  if (store.get().transport === "mock") {
    // the mock: a test host that waits for a debugger, "attaches" never (Stop ends it)
    window.setTimeout(() => {
      const s = debugSession.peek();
      if (s?.status === "starting" && s.projectId === projectId)
        onWaiting({ type: "debuggerWaiting", projectId, pid: 6500, testIds, runId: "mock-debug", hints: [] });
    }, 600);
    return;
  }
  store.send({ type: "debug", projectId, testIds });
}

function onWaiting(e: { type: "debuggerWaiting"; projectId: string; pid: number; testIds: string[]; runId: string; hints: DebugHint[] }) {
  const cur = debugSession.peek();
  const t = e.testIds.length === 1 ? getStore().get().tests.get(`${e.projectId}|${e.testIds[0]}`) : undefined;
  debugSession.set({
    label: cur?.projectId === e.projectId ? cur.label : (t?.displayName ?? `${e.testIds.length} tests`),
    projectId: e.projectId,
    testIds: e.testIds,
    status: "waiting",
    pid: e.pid,
    runId: e.runId,
    hints: e.hints,
  });
}

serverEventListeners.add((e) => {
  const s = debugSession.peek();
  if (e.type === "debuggerWaiting") onWaiting(e as unknown as Parameters<typeof onWaiting>[0]);
  else if (e.type === "debuggerAttached" && s) debugSession.set({ ...s, status: "attached", pid: (e.pid as number) ?? s.pid });
  else if (e.type === "runFinished" && s && (s.status === "waiting" || s.status === "attached") && (e.run as { runId?: string })?.runId === s.runId) debugSession.set(undefined);
  else if (e.type === "commandRejected" && e.command === "debug" && s?.status === "starting")
    debugSession.set({ ...s, status: "error", message: (e as unknown as CommandRejectedEvent).error ?? "The host rejected the request" });
});

/** Dismiss the bar; while waiting / attached this also cancels the debug run. */
export function endDebugSession(cancel: boolean) {
  const s = debugSession.peek();
  if (cancel && (s?.status === "waiting" || s?.status === "attached") && getStore().get().transport !== "mock") getStore().send({ type: "cancel" });
  debugSession.set(undefined);
}

/** How to attach in each editor when the host sent no hints (the mock). */
export const ATTACH_HELP: Record<"vscode" | "rider" | "visualstudio", { name: string; steps: string }> = {
  vscode: { name: "VS Code", steps: "Run and Debug → “.NET: Attach”, pick the process id" },
  rider: { name: "Rider", steps: "Run → Attach to Process… (Ctrl+Alt+F5), pick the process id" },
  visualstudio: { name: "Visual Studio", steps: "Debug → Attach to Process… (Ctrl+Alt+P), Managed (.NET Core, .NET 5+)" },
};

/** The attach steps for `editor`: the host's hint when it sent one, else ours. */
export function attachSteps(s: DebugSession, editor: "vscode" | "rider" | "visualstudio"): { name: string; steps: string; launchJson?: string } {
  return s.hints?.find((x) => x.editor === editor) ?? ATTACH_HELP[editor] ?? ATTACH_HELP.vscode;
}
