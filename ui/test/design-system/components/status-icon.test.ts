import { describe, expect, it } from "vitest";
import { insert, root, signal } from "../../../design-system/src/core";
import { renderStatus, STATUS_LABEL, statusIcon, type Status } from "../../../design-system/src/components/status-icon";

const ALL: Status[] = ["notRun", "queued", "running", "passed", "failed", "skipped", "timedOut", "cancelled", "building", "discovering", "error"];

describe("renderStatus", () => {
  it("renders the icon/spinner/dot for every status with its label as title", () => {
    const classes: Record<string, string> = {};
    for (const s of ALL) {
      const el = renderStatus(s, 14);
      expect(el.title, s).toBe(STATUS_LABEL[s]);
      expect(el.classList.contains("st"), s).toBe(true);
      expect(el.style.width).toBe("14px");
      classes[s] = el.className;
    }
    expect(classes.passed).toContain("st-passed");
    expect(classes.failed).toContain("st-failed");
    expect(classes.timedOut).toContain("st-failed");
    expect(classes.error).toContain("st-failed");
    expect(classes.skipped).toContain("st-skipped");
    expect(classes.cancelled).toContain("st-muted");
    expect(classes.notRun).toContain("st-muted");
    for (const s of ["running", "building", "discovering"] as const) expect(renderStatus(s).querySelector(".spinner")).not.toBeNull();
    expect(renderStatus("queued").querySelector(".queued-dot")).not.toBeNull();
    expect(renderStatus("passed").style.width).toBe("16px");
  });

  it("has a label for every status", () => {
    expect(Object.keys(STATUS_LABEL).sort()).toEqual([...ALL].sort());
    expect(STATUS_LABEL.error).toBe("Build error");
  });
});

describe("statusIcon", () => {
  it("static status → the element itself", () => {
    const el = statusIcon("passed") as HTMLElement;
    expect(el.classList.contains("st-passed")).toBe(true);
  });

  it("reactive status pops (.st-anim) only on changes, never on first render", () => {
    root((dispose) => {
      const s = signal<Status>("running");
      const host = document.createElement("div");
      const child = statusIcon(s, 12);
      expect(typeof child).toBe("function");
      document.body.append(host);
      insert(host, child, null);
      expect(host.querySelector(".st-anim")).toBeNull();
      expect(host.querySelector(".spinner")).not.toBeNull();
      s.set("failed");
      expect(host.querySelector(".st-anim > .st-failed")).not.toBeNull();
      dispose();
    });
  });
});

describe("<lc-status-icon>", () => {
  it("renders from the status attribute and pops on change", () => {
    const el = document.createElement("lc-status-icon");
    el.setAttribute("status", "queued");
    el.setAttribute("size", "20");
    document.body.append(el);
    expect(el.style.display).toBe("contents");
    expect(el.querySelector(".queued-dot-wrap")!.getAttribute("style")).toContain("20px");
    el.setAttribute("status", "passed");
    expect(el.querySelector(".st-anim .st-passed")).not.toBeNull();
    el.setAttribute("status", "");
    expect(el.status).toBe("passed");
  });
});

