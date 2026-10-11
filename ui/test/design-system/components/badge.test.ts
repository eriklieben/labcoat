import { describe, expect, it } from "vitest";
import { root, signal } from "../../../design-system/src/core";
import { badge, statusBadge } from "../../../design-system/src/components/badge";
import type { Status } from "../../../design-system/src/components/status-icon";
import { flushMicrotasks } from "../../helpers/dom";

describe("badge()", () => {
  it("renders span.badge with extra classes and children", () => {
    const b = badge({ class: "trait", title: "t" }, "Category", ": ", "Slow");
    expect(b.className).toBe("badge trait");
    expect(b.title).toBe("t");
    expect(b.textContent).toBe("Category: Slow");
    expect(badge({}).className).toBe("badge");
  });

  it("reactive class", () => {
    root((dispose) => {
      const on = signal(false);
      const b = badge({ class: () => on() && "hot" });
      expect(b.className).toBe("badge");
      on.set(true);
      expect(b.className).toBe("badge hot");
      dispose();
    });
  });
});

describe("statusBadge()", () => {
  it("shows the status icon and label, following a signal", () => {
    root((dispose) => {
      const s = signal<Status>("passed");
      const b = statusBadge(s);
      expect(b.className).toBe("badge badge-status badge-passed");
      expect(b.textContent).toBe("Passed");
      expect(b.querySelector(".st-passed")).not.toBeNull();
      s.set("timedOut");
      expect(b.className).toBe("badge badge-status badge-timedOut");
      expect(b.textContent).toBe("Timed out");
      dispose();
    });
    expect(statusBadge("failed").textContent).toBe("Failed");
  });
});

describe("<lc-badge>", () => {
  it("keeps author children, or shows a status", async () => {
    const el = document.createElement("lc-badge");
    el.append("smoke");
    document.body.append(el);
    expect(el.className).toBe("badge");
    expect(el.textContent).toBe("smoke");
    el.setAttribute("status", "failed");
    expect(el.className).toBe("badge badge-status badge-failed");
    expect(el.textContent).toBe("Failed");
    el.removeAttribute("status");
    expect(el.className).toBe("badge");
    expect(el.textContent).toBe("smoke");
    // re-render after a reconnect reuses the captured children
    el.remove();
    await flushMicrotasks();
    document.body.append(el);
    expect(el.textContent).toBe("smoke");
  });
});
