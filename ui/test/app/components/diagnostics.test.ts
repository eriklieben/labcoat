import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { needsDiagnostics, type ProjectDiagnostics } from "../../../src/components/diagnostics";
import type { Store } from "../../../src/store";
import type { ProjectDiagnostic, TestProject } from "../../../src/contract";
import { FakeTransport, flushFrame, mount, project, text } from "../../helpers/app";
import { freshStore } from "../../helpers/app6";

let store: Store;
let transport: FakeTransport;

beforeEach(async () => {
  ({ store, transport } = await freshStore());
});
afterEach(async () => {
  await flushFrame();
});

async function setProject(p: Partial<TestProject>) {
  store.apply({ type: "projectStatus", project: project(p) });
  await flushFrame();
}

function diag(onOpenOutput?: () => void, attr = true) {
  const el = document.createElement("lc-project-diagnostics") as ProjectDiagnostics;
  if (attr) el.setAttribute("project-id", "P1");
  else el.projectId = "P1";
  el.onOpenOutput = onOpenOutput;
  return mount(el);
}

const checks = (el: Element) => [...el.querySelectorAll(".diag-checklist > li")];

describe("needsDiagnostics", () => {
  it("is true with host findings or for an idle project without tests", () => {
    expect(needsDiagnostics(project({ diagnostics: [{ level: "info", message: "x" }] }), 5)).toBe(true);
    expect(needsDiagnostics(project(), 0)).toBe(true);
    expect(needsDiagnostics(project({ status: "building" }), 0)).toBe(false);
    expect(needsDiagnostics(project(), 3)).toBe(false);
  });
});

describe("<lc-project-diagnostics>", () => {
  it("without findings: the four checks to look at, no log yet, Rediscover", () => {
    const el = diag();
    expect(el.classList.contains("project-diagnostics")).toBe(true);
    expect(text(el.querySelector(".diag-title"))).toBe("No tests discovered in Project.One");
    expect(text(el.querySelector(".diag-section-title"))).toBe("What to check");
    const items = checks(el);
    expect(items).toHaveLength(4);
    expect(items.every((i) => i.classList.contains("diag-check") && !i.classList.contains("flagged"))).toBe(true);
    expect(text(items[0].querySelector(".diag-message"))).toBe("The test project is an executable");
    expect(text(el.querySelectorAll(".diag-section")[1])).toContain("No log lines for this project yet.");
    expect([...el.querySelectorAll(".diag-actions button")].map((b) => text(b))).toEqual(["Rediscover"]);
  });

  it("a VSTest project: its own title, the finding with the switch and the guide, no generic checklist", async () => {
    await setProject({
      testPlatform: "vstest",
      diagnostics: [
        {
          level: "warning",
          message: "Project.One uses VSTest (Microsoft.NET.Test.Sdk + MSTest.TestAdapter). Labcoat runs Microsoft.Testing.Platform projects.",
          hint: "Set <EnableMSTestRunner>true</EnableMSTestRunner> and <OutputType>Exe</OutputType> (or use MSTest.Sdk), then Rediscover.",
          link: "https://example.test/docs/troubleshooting.md#my-project-uses-vstest",
        },
        { level: "info", message: "Project.One is not restored yet: Labcoat read its project files." },
      ],
    });
    const el = diag();
    expect(text(el.querySelector(".diag-title"))).toBe("Project.One uses VSTest");
    expect(text(el.querySelector(".diag-sub"))).toContain("doesn't build or start this project");
    const items = checks(el);
    expect(items).toHaveLength(2);
    expect(text(items[0].querySelector(".diag-hint"))).toContain("EnableMSTestRunner");
    expect(items[0].querySelector<HTMLAnchorElement>("a.diag-link")!.href).toBe("https://example.test/docs/troubleshooting.md#my-project-uses-vstest");
    expect(items[1].querySelector("a.diag-link")).toBeNull();
    await setProject({ testPlatform: "none", diagnostics: [{ level: "warning", message: "Project.One is not a Microsoft.Testing.Platform test project." }] });
    expect(text(el.querySelector(".diag-title"))).toBe("Project.One is not a test app");
  });

  it("a finding with a link on a checklist item shows the link too", async () => {
    await setProject({ diagnostics: [{ level: "error", message: "OutputType is 'Library'", link: "https://example.test/x" }] });
    const el = diag();
    expect(checks(el)[0].querySelector<HTMLAnchorElement>(".diag-finding a.diag-link")!.href).toBe("https://example.test/x");
  });

  it("puts matching host findings on their checklist item (flagged, first) and the rest above as their own items", async () => {
    const ds: ProjectDiagnostic[] = [
      { level: "warning", message: "No test runner found", hint: "Enable the MSTest runner" },
      { level: "error", message: "OutputType is 'Library'", hint: "Add <OutputType>Exe</OutputType>", details: "stderr 1\nstderr 2" },
      { level: "info", message: "Something unrelated", hint: "a hint", details: "more" },
    ];
    await setProject({ diagnostics: ds });
    const el = diag();
    expect(text(el.querySelector(".diag-section-title"))).toBe("What the host found");
    const items = checks(el);
    expect(items).toHaveLength(5);
    // unmatched finding first, as its own item
    expect(items[0].className).toBe("diag-item level-info");
    expect(text(items[0].querySelector(".diag-message"))).toBe("Something unrelated");
    expect(text(items[0].querySelector(".diag-hint"))).toBe("a hint");
    expect(text(items[0].querySelector("details pre"))).toBe("more");
    // flagged checks (in checklist order): exe, runner
    expect(items[1].classList.contains("flagged")).toBe(true);
    expect(text(items[1].querySelector(".diag-message"))).toBe("The test project is an executable");
    expect(text(items[1].querySelector(".diag-finding-msg"))).toBe("OutputType is 'Library'");
    expect(text(items[1].querySelector(".diag-finding .diag-hint"))).toBe("Add <OutputType>Exe</OutputType>");
    expect(text(items[1].querySelector(".diag-finding pre"))).toBe("stderr 1 stderr 2");
    expect(items[1].querySelector(".diag-check-icon")!.classList.contains("level-error")).toBe(true);
    expect(items[2].classList.contains("flagged")).toBe(true);
    expect(items[2].querySelector(".diag-finding details")).toBeNull();
    expect(items[3].classList.contains("flagged")).toBe(false);
    expect(items[4].classList.contains("flagged")).toBe(false);
  });

  it("a test host that did not start changes the title", async () => {
    await setProject({ diagnostics: [{ level: "error", message: "The test host did not connect" }] });
    const el = diag(undefined, false);
    expect(text(el.querySelector(".diag-title"))).toBe("Project.One: the test app did not start");
    expect(checks(el)[0].classList.contains("flagged")).toBe(true);
  });

  it("busy: 'Discovering tests in …' and Rediscover disabled; it sends discover when idle", async () => {
    await setProject({ status: "discovering" });
    const el = diag();
    expect(text(el.querySelector(".diag-title"))).toBe("Discovering tests in Project.One…");
    const btn = el.querySelector<HTMLButtonElement>(".diag-actions button")!;
    expect(btn.disabled).toBe(true);
    await setProject({ status: "idle" });
    expect(btn.disabled).toBe(false);
    btn.click();
    expect(transport.sent).toEqual([{ type: "discover", projectId: "P1" }]);
  });

  it("shows the project's last log lines (levels prefixed) and an Open output action", async () => {
    for (let i = 0; i < 16; i++) store.apply({ type: "log", level: i === 15 ? "error" : "info", message: `line ${i}`, timestamp: "", projectId: "P1" });
    store.apply({ type: "log", level: "info", message: "other project", timestamp: "", projectId: "P2" });
    await flushFrame();
    const open = vi.fn();
    const el = diag(open);
    const log = el.querySelector(".diag-log")!;
    expect(log).not.toBeNull();
    const code = log.textContent ?? "";
    expect(code).not.toContain("line 1\n");
    expect(code).toContain("line 2");
    expect(code).toContain("error: line 15");
    expect(code).not.toContain("other project");
    const openBtn = [...el.querySelectorAll<HTMLButtonElement>(".diag-actions button")].find((b) => text(b) === "Open output")!;
    openBtn.click();
    expect(open).toHaveBeenCalledOnce();
  });

  it("falls back to the project id when the project is unknown; attribute removal clears it", () => {
    const el = document.createElement("lc-project-diagnostics") as ProjectDiagnostics;
    el.setAttribute("project-id", "Ghost");
    mount(el);
    expect(text(el.querySelector(".diag-title"))).toBe("No tests discovered in Ghost");
    el.removeAttribute("project-id");
    expect(el.projectId).toBe("");
  });
});
