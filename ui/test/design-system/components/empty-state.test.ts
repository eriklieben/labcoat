import { describe, expect, it } from "vitest";
import { h } from "../../../design-system/src/core";
import { emptyState } from "../../../design-system/src/components/empty-state";

describe("emptyState()", () => {
  it("icon tile, title, body and actions", () => {
    const el = emptyState({ icon: "shieldCheck", title: "No coverage yet", body: "Run all tests", actions: h("button", null, "Run"), as: "h2", class: "x" });
    expect(el.className).toBe("empty-state x");
    expect(el.hasAttribute("role")).toBe(false);
    const art = el.querySelector(".empty-illustration")!;
    expect(art.getAttribute("aria-hidden")).toBe("true");
    expect((art.querySelector(".icon") as HTMLElement).style.width).toBe("32px");
    expect(el.querySelector("h2.empty-title")!.textContent).toBe("No coverage yet");
    expect(el.querySelector(".empty-sub")!.textContent).toBe("Run all tests");
    expect(el.querySelector(".empty-actions > button")).not.toBeNull();
  });

  it("spinner states are status regions; small size; minimal content", () => {
    const s = emptyState({ spinner: true, title: "Connecting…", size: "small" });
    expect(s.className).toBe("empty-state small");
    expect(s.getAttribute("role")).toBe("status");
    expect(s.querySelector(".spinner")!.className).toBe("spinner");
    expect(emptyState({ spinner: true, title: "x" }).querySelector(".spinner")!.className).toBe("spinner large");
    expect(emptyState({ spinner: true, title: "x", role: null }).getAttribute("role")).toBe("status");
    const m = emptyState({ title: "Nothing", role: "status" });
    expect(m.getAttribute("role")).toBe("status");
    expect(m.querySelector("div.empty-title")!.textContent).toBe("Nothing");
    expect(m.querySelector(".empty-illustration, .spinner, .empty-sub, .empty-actions")).toBeNull();
  });
});
