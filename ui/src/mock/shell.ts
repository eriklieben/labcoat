// SHELL mock: wraps the mock transport (registered in main.ts, mockServer.ts untouched) to add Round 3 data:
//   - ExplorerState.workspaceName (`?ws=<name>`, default "AspireSample")
//   - TestProject.buildErrors / buildErrorCount parsed from buildOutput for failed builds (like BuildOutputParser.cs)
//   - `?buildfail`: SampleTests' build fails ~1.2 s after start → its tests' results become outdated (resultsOutdated)
//   - watch-triggered runs get trigger "watch" + RunSummary.watch / .affected (the mock's watch tick runs SampleTests)
//   - `?offline=<ms>`: the connection drops after <ms> (Offline state with the last known results)
import type { AffectedRun, BuildDiagnostic, ClientCommand, ServerEvent, TestProject } from "../contract";
import type { Transport, TransportHandlers } from "../transport";
import { getStore } from "../store";
import { buildPlan, mockGitInfo } from "./affected";

const params = new URLSearchParams(location.search);
const ROOT = "/src/demo/samples";

const LINE_RE = /^\s*(.*?)\((\d+)(?:,(\d+))?\)\s*:\s*(error|warning)\s+([A-Za-z]+\d+)\s*:\s*(.*?)(?:\s+\[([^\]]+)\])?\s*$/;

export function parseBuildOutput(out: string | undefined): BuildDiagnostic[] {
  const seen = new Set<string>();
  const res: BuildDiagnostic[] = [];
  for (const line of (out ?? "").split("\n")) {
    const m = LINE_RE.exec(line);
    if (!m) continue;
    const d: BuildDiagnostic = { file: m[1], line: +m[2], column: m[3] ? +m[3] : undefined, severity: m[4] as "error" | "warning", code: m[5], message: m[6], project: m[7] };
    const k = `${d.file}|${d.line}|${d.column}|${d.code}|${d.message}`;
    if (seen.has(k)) continue;
    seen.add(k);
    res.push(d);
  }
  return res.sort((a, b) => (a.severity === b.severity ? 0 : a.severity === "error" ? -1 : 1));
}

const SAMPLE_BUILD_OUTPUT = `  Determining projects to restore...
${ROOT}/SampleTests/Math/Calculator.cs(42,23): error CS1061: 'Calculator' does not contain a definition for 'Divde' [${ROOT}/SampleTests/SampleTests.csproj]
${ROOT}/SampleTests/Math/CalculatorTests.cs(57,9): error CS0246: The type or namespace name 'Assrt' could not be found [${ROOT}/SampleTests/SampleTests.csproj]
${ROOT}/SampleTests/Strings/StringTests.cs(12,17): warning CS0168: The variable 'unused' is declared but never used [${ROOT}/SampleTests/SampleTests.csproj]

Build FAILED.`;

// Round 4 multi-targeting: AspireSample.Tests targets two frameworks (the TFM picker in its project details)
const tfm = new Map<string, string>([["AspireSample.Tests", "net10.0"]]);
const FRAMEWORKS: Record<string, string[]> = { "AspireSample.Tests": ["net8.0", "net10.0"] };

function enrichProject(p0: TestProject): TestProject {
  const fws = FRAMEWORKS[p0.id];
  const p = fws ? { ...p0, availableFrameworks: fws, targetFramework: tfm.get(p0.id) } : { ...p0, targetFramework: p0.targetFramework ?? "net10.0" };
  if (p.status !== "error" || p.buildErrors || !p.buildOutput) return p;
  const buildErrors = parseBuildOutput(p.buildOutput);
  return {
    ...p,
    buildErrors,
    buildErrorCount: buildErrors.filter((d) => d.severity === "error").length,
    buildWarningCount: buildErrors.filter((d) => d.severity === "warning").length,
  };
}

class ShellMockTransport {
  private h?: TransportHandlers;
  private watch = false;
  private lastUserRun = 0;
  private lastBuildStart = 0;
  private dropped = false;
  private failedProjects = new Map<string, TestProject>();
  /** RunSummary.affected of the run a runAffected command started (AFFECTED mock) */
  private affected?: AffectedRun;
  constructor(private inner: Transport) {}

  start(h: TransportHandlers) {
    this.h = h;
    this.inner.start({
      ...h,
      onStatus: (s, info) => !this.dropped && h.onStatus(s, info),
      onEvent: (e) => !this.dropped && h.onEvent(this.transform(e)),
    });
    if (params.has("buildfail")) window.setTimeout(() => this.failBuild("SampleTests"), 1200);
    const off = Number(params.get("offline"));
    if (off > 0)
      window.setTimeout(() => {
        this.dropped = true;
        let attempt = 0;
        const tick = () => {
          if (!this.dropped) return;
          attempt++;
          h.onStatus("reconnecting", { retryInMs: 4000, attempt });
          window.setTimeout(tick, 4000);
        };
        tick();
      }, off);
  }

  private failBuild(pid: string) {
    const ev: ServerEvent = { type: "projectStatus", project: { id: pid, name: pid, path: `${ROOT}/${pid}/${pid}.csproj`, status: "error", lastError: "Build failed with 2 error(s)", buildOutput: SAMPLE_BUILD_OUTPUT, resultsOutdated: true, hostState: "cold" } };
    this.failedProjects.set(pid, (ev as { project: TestProject }).project);
    this.h?.onEvent(this.transform(ev));
    this.h?.onEvent({ type: "log", level: "error", projectId: pid, message: "Build FAILED. 1 Warning(s), 2 Error(s)", timestamp: new Date().toISOString() });
  }

  private transform(e: ServerEvent): ServerEvent {
    switch (e.type) {
      case "state": {
        this.watch = e.state.watch;
        const projects = e.state.projects.map((p) => enrichProject(this.failedProjects.get(p.id) ?? p));
        return { ...e, state: { ...e.state, projects, workspaceName: params.get("ws") ?? "AspireSample" } };
      }
      case "projectStatus":
        if (e.project.status === "building") this.lastBuildStart = performance.now();
        if (this.failedProjects.has(e.project.id) && e.project.status !== "error") return { ...e, project: this.failedProjects.get(e.project.id)! };
        return { ...e, project: enrichProject(e.project) };
      case "runStarted":
      case "runFinished": {
        const r = e.run;
        if (this.affected) {
          const run = { ...r, trigger: "user" as const, affected: this.affected };
          if (e.type === "runFinished") this.affected = undefined;
          return { ...e, run } as ServerEvent;
        }
        const byWatch = this.watch && performance.now() - this.lastUserRun > 1500 && r.trigger !== "watch";
        if (!byWatch && !(r.trigger === "watch")) return e;
        const file = `${ROOT}/SampleTests/Math/CalculatorTests.cs`;
        const run = {
          ...r,
          trigger: "watch" as const,
          watch: r.watch ?? { changedFiles: [file], buildMs: 1180, strategy: "testFile" as const },
          affected: r.affected ?? { strategy: "testFile" as const, changedFiles: [file] },
        };
        return { ...e, run } as ServerEvent;
      }
      default:
        return e;
    }
  }

  send(cmd: ClientCommand) {
    if (cmd.type === "setTargetFramework") {
      // like the host: switch, rebuild + rediscover
      tfm.set(cmd.projectId, cmd.targetFramework || "net10.0");
      this.h?.onEvent({ type: "log", level: "info", projectId: cmd.projectId, message: `Target framework: ${tfm.get(cmd.projectId)} (rebuilding)`, timestamp: new Date().toISOString() });
      this.inner.send({ type: "discover", projectId: cmd.projectId });
      return;
    }
    if (cmd.type === "runAffected") {
      // like the host: compute the plan, then run exactly its tests (RunSummary.affected strategy "git")
      void mockGitInfo().then((git) => {
        const now = new Date().toISOString();
        if (!git.available) {
          this.h?.onEvent({ type: "commandRejected", command: "runAffected", status: 409, error: "The workspace is not in a git repository." } as ServerEvent);
          return;
        }
        const plan = buildPlan(cmd.base ?? "working", cmd.ref, [...getStore().get().tests.values()], git);
        const what = cmd.base === "branch" ? `branch vs ${plan.ref}` : cmd.base === "range" ? `range ${cmd.ref}` : "uncommitted changes";
        if (!plan.tests.length) {
          this.h?.onEvent({ type: "log", level: "info", message: `Run affected (${what}): ${plan.counts.files} files → no affected tests`, timestamp: now });
          return;
        }
        this.h?.onEvent({ type: "log", level: "info", message: `Run affected (${what}): ${plan.counts.files} files → ${plan.counts.tests} tests in ${plan.counts.projects} projects`, timestamp: now });
        this.affected = { strategy: "git", base: cmd.base ?? "working", ref: plan.ref, changedFiles: plan.files.map((f) => f.path), tests: plan.counts.tests };
        this.lastUserRun = performance.now();
        this.inner.send({ type: "run", testIds: plan.tests.map((t) => t.id), coverage: cmd.coverage });
      });
      return;
    }
    if (cmd.type === "run") this.lastUserRun = performance.now();
    if (cmd.type === "setWatch") this.watch = cmd.enabled;
    if (cmd.type === "discover" && this.failedProjects.size && (!cmd.projectId || this.failedProjects.has(cmd.projectId))) {
      // the "fix": rediscovering the failed project builds it again successfully
      if (cmd.projectId) this.failedProjects.delete(cmd.projectId);
      else this.failedProjects.clear();
    }
    void this.lastBuildStart;
    this.inner.send(cmd);
  }
  retryNow() {
    if (this.dropped) {
      this.dropped = false;
      this.h?.onStatus("open");
    }
  }
}

/** The mock transport with the SHELL additions; every other Transport method goes to the inner mock. */
export function withShellMock(inner: Transport): Transport {
  const shell = new ShellMockTransport(inner);
  return new Proxy(inner, {
    get(target, prop) {
      if (prop === "start" || prop === "send" || prop === "retryNow") return (shell[prop] as (...a: unknown[]) => unknown).bind(shell);
      const v = Reflect.get(target, prop, target);
      return typeof v === "function" ? v.bind(target) : v;
    },
  });
}
