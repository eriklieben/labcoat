import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h, root, type Disposer } from "@eriklieben/labcoat-design-system";
import {
  buildErrorsDialog,
  buildErrorsFor,
  errorCount,
  onOpenSourceRequest,
  openBuildErrors,
  outputErrorCount,
  requestOpenSource,
  type BuildErrors,
  type OpenSourceRequest,
} from "../../../src/components/build-errors";
import type { Store } from "../../../src/store";
import type { BuildDiagnostic, TestProject } from "../../../src/contract";
import { FakeTransport, flushFrame, mount, project, text } from "../../helpers/app";
import { flushMicrotasks } from "../../helpers/dom";
import { freshStore } from "../../helpers/app6";

let store: Store;
let transport: FakeTransport;
let disposers: Disposer[] = [];

beforeEach(async () => {
  ({ store, transport } = await freshStore());
  buildErrorsFor.set(undefined);
});
afterEach(async () => {
  for (const d of disposers) d();
  disposers = [];
  buildErrorsFor.set(undefined);
  await flushFrame();
});

const err = (p: Partial<BuildDiagnostic> = {}): BuildDiagnostic => ({ severity: "error", message: "; expected", code: "CS1002", file: "/src/Lib/Calc.cs", line: 12, column: 5, ...p });

function failedProject(p: Partial<TestProject> = {}) {
  return project({ status: "error", buildErrors: [err(), err({ severity: "warning", code: "CS0168", message: "unused", line: 3, column: undefined })], buildWarningCount: 1, ...p });
}

function list(p: TestProject | undefined, onOpened = vi.fn()) {
  const el = document.createElement("lc-build-errors") as BuildErrors;
  el.project = p;
  el.onOpened = onOpened;
  return mount(el);
}

describe("errorCount / outputErrorCount", () => {
  it("prefers the host's count, else counts error diagnostics", () => {
    expect(errorCount(project())).toBe(0);
    expect(errorCount(failedProject())).toBe(1);
    expect(errorCount(failedProject({ buildErrorCount: 7 }))).toBe(7);
  });

  it("is 0 without unseen errors, the build errors while a build fails, else the unseen error lines", async () => {
    expect(outputErrorCount()).toBe(0);
    store.apply({ type: "log", level: "error", message: "x", timestamp: "" });
    await flushFrame();
    expect(outputErrorCount()).toBe(1);
    store.apply({ type: "projectStatus", project: failedProject({ buildErrorCount: 3 }) });
    await flushFrame();
    expect(outputErrorCount()).toBe(3);
  });
});

describe("open-source requests", () => {
  it("dispatches a window event the shell listens to (and can stop listening)", () => {
    const got: OpenSourceRequest[] = [];
    const off = onOpenSourceRequest((r) => got.push(r));
    requestOpenSource({ path: "/a.cs", line: 2 });
    off();
    requestOpenSource({ path: "/b.cs" });
    expect(got).toEqual([{ path: "/a.cs", line: 2 }]);
  });
});

describe("<lc-build-errors>", () => {
  it("shows the build output (or last error, or a placeholder) when there are no parsed diagnostics", () => {
    const el = list(project({ status: "error", buildOutput: "MSBuild exploded" }));
    expect(el.classList.contains("build-errors")).toBe(true);
    expect(el.style.display).toBe("block");
    expect(text(el.querySelector("pre.build-output"))).toBe("MSBuild exploded");
    el.project = project({ status: "error", lastError: "Restore failed" });
    expect(text(el.querySelector("pre.build-output"))).toBe("Restore failed");
    el.project = undefined;
    expect(text(el.querySelector("pre.build-output"))).toBe("No build output.");
  });

  it("lists diagnostics with severity, code, message and location", () => {
    const el = list(failedProject({ buildErrors: [err({ project: "/src/Lib/Lib.csproj" }), err({ severity: "warning", file: undefined, line: undefined, project: "/src/X/X.csproj", message: "no file" }), err({ file: undefined, project: undefined, message: "bare" })] }));
    const ul = el.querySelector("ul.build-error-list")!;
    expect(ul.getAttribute("aria-label")).toBe("Build errors");
    const items = [...ul.querySelectorAll("li.build-error")];
    expect(items.map((i) => i.className)).toEqual(["build-error sev-error", "build-error sev-warning", "build-error sev-error"]);
    expect(text(items[0].querySelector(".be-message"))).toBe("CS1002; expected");
    expect(text(items[0].querySelector(".be-file"))).toBe("Calc.cs:12:5");
    expect(items[0].querySelector<HTMLButtonElement>(".be-file")!.title).toBe("Show /src/Lib/Calc.cs:12");
    expect(items[0].querySelector(".be-editor")!.getAttribute("aria-label")).toBe("Open Calc.cs in editor");
    expect(text(items[0].querySelector(".be-project"))).toBe("Lib.csproj");
    // no file: only the project
    expect(items[1].querySelector(".be-file")).toBeNull();
    expect(text(items[1].querySelector(".be-project"))).toBe("X.csproj");
    expect(items[2].querySelector(".be-loc")).toBeNull();
  });

  it("clicking a location toggles the inline source and asks the shell to select the project", async () => {
    const got: OpenSourceRequest[] = [];
    const off = onOpenSourceRequest((r) => got.push(r));
    const el = list(failedProject());
    const file = el.querySelector<HTMLButtonElement>(".be-file")!;
    expect(file.getAttribute("aria-expanded")).toBe("false");
    file.click();
    off();
    expect(got).toEqual([{ path: "/src/Lib/Calc.cs", line: 12, column: 5, selectKey: "p:P1" }]);
    expect(file.getAttribute("aria-expanded")).toBe("true");
    const view = el.querySelector(".be-source lc-code-view") as HTMLElement & { path?: string; focusLine?: number; annotations?: { title: string }[] };
    expect(view).not.toBeNull();
    expect(view.path).toBe("/src/Lib/Calc.cs");
    expect(view.focusLine).toBe(12);
    expect(view.annotations).toEqual([{ line: 12, title: "CS1002: ; expected" }]);
    await flushMicrotasks();
    expect(transport.fetchSource).toHaveBeenCalledWith("/src/Lib/Calc.cs");
    file.click();
    expect(file.getAttribute("aria-expanded")).toBe("false");
    expect(el.querySelector(".be-source")).toBeNull();
  });

  it("the editor button opens the location through the host", async () => {
    const fetchMock = vi.fn(async () => ({ ok: true, status: 200, json: async () => ({ ok: true }) }));
    vi.stubGlobal("fetch", fetchMock);
    const el = list(failedProject());
    el.querySelector<HTMLButtonElement>(".be-editor")!.click();
    await flushMicrotasks();
    expect(fetchMock).toHaveBeenCalledOnce();
    const [, init] = fetchMock.mock.calls[0] as unknown as [URL, RequestInit];
    expect(JSON.parse(init.body as string)).toMatchObject({ path: "/src/Lib/Calc.cs", line: 12, column: 5 });
  });
});

describe("buildErrorsDialog", () => {
  function mountDialog() {
    root((d) => {
      disposers.push(d);
      document.body.append(h("div", null, buildErrorsDialog()));
    });
  }

  it("opens for the requested project with a title counting errors and warnings", async () => {
    store.apply({ type: "projectStatus", project: failedProject() });
    await flushFrame();
    mountDialog();
    expect(document.querySelector("dialog")).toBeNull();
    openBuildErrors("P1");
    const dlg = document.querySelector<HTMLDialogElement>("dialog.build-errors-dialog")!;
    expect(dlg).not.toBeNull();
    expect(dlg.open).toBe(true);
    expect(text(dlg.querySelector(".lc-dialog-title"))).toBe("Project.One: 1 build error, 1 warning");
    expect(dlg.querySelectorAll("lc-build-errors")).toHaveLength(1);
    expect(dlg.querySelector(".build-errors-heading")).toBeNull();
  });

  it("lists the requested project first, then other failed builds, with headings", async () => {
    store.apply({ type: "state", state: { projects: [failedProject({ id: "P0", name: "Zero", buildErrorCount: 2, buildWarningCount: 0 }), failedProject({ buildWarningCount: 2 })], tests: [], watch: false, coverageEnabled: false } });
    await flushFrame();
    mountDialog();
    openBuildErrors("P1");
    const dlg = document.querySelector<HTMLDialogElement>("dialog.build-errors-dialog")!;
    expect(text(dlg.querySelector(".lc-dialog-title"))).toBe("3 build errors, 2 warnings");
    expect([...dlg.querySelectorAll(".build-errors-heading")].map((x) => text(x))).toEqual(["Project.One · 1 error", "Zero · 2 errors"]);
  });

  it("Rebuild sends discover for every failed project and closes; disabled while running or offline", async () => {
    store.apply({ type: "state", state: { projects: [failedProject(), failedProject({ id: "P2", name: "Two" }), project({ id: "P3" })], tests: [], watch: false, coverageEnabled: false } });
    await flushFrame();
    mountDialog();
    openBuildErrors("P1");
    const rebuild = [...document.querySelectorAll<HTMLButtonElement>("dialog .build-errors-footer button")].find((b) => text(b) === "Rebuild")!;
    expect(rebuild.disabled).toBe(false);
    rebuild.click();
    expect(transport.sent).toEqual([
      { type: "discover", projectId: "P1" },
      { type: "discover", projectId: "P2" },
    ]);
    expect(buildErrorsFor.peek()).toBeUndefined();
    await flushMicrotasks();
    expect(document.querySelector("dialog.build-errors-dialog")).toBeNull();

    transport.setStatus("reconnecting");
    await flushFrame();
    openBuildErrors("P1");
    const again = [...document.querySelectorAll<HTMLButtonElement>("dialog .build-errors-footer button")].find((b) => text(b) === "Rebuild")!;
    expect(again.disabled).toBe(true);
  });

  it("a project whose build is fine again still gets a rebuild of itself; Escape closes", async () => {
    store.apply({ type: "projectStatus", project: project() });
    await flushFrame();
    mountDialog();
    openBuildErrors("P1");
    const dlg = document.querySelector<HTMLDialogElement>("dialog.build-errors-dialog")!;
    expect(text(dlg.querySelector(".lc-dialog-title"))).toBe("Project.One: 0 build errors");
    [...dlg.querySelectorAll<HTMLButtonElement>(".build-errors-footer button")].find((b) => text(b) === "Rebuild")!.click();
    expect(transport.sent).toEqual([{ type: "discover", projectId: "P1" }]);
    openBuildErrors("P1");
    const d2 = document.querySelector<HTMLDialogElement>("dialog.build-errors-dialog")!;
    d2.dispatchEvent(new Event("cancel", { cancelable: true }));
    expect(buildErrorsFor.peek()).toBeUndefined();
  });
});
