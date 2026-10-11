import { afterEach, describe, expect, it, vi } from "vitest";
import { copyDiagnostics, fetchDiagnostics } from "../../src/diagnostics";
import { text } from "../helpers/app";
import { jsonResponse } from "../helpers/app3";
import { clipboard } from "../helpers/dom";

const toasts = () => [...document.querySelectorAll(".lc-toast")].map((t) => text(t));

afterEach(() => {
  document.querySelectorAll(".lc-toaster").forEach((t) => t.remove());
  clipboard.fail = false;
  clipboard.text = "";
  vi.unstubAllGlobals();
});

describe("copy diagnostics", () => {
  it("fetches the host's report and puts its markdown on the clipboard", async () => {
    const fetch = vi.fn(async () => jsonResponse({ markdown: "### Labcoat diagnostics\n", report: {} }));
    vi.stubGlobal("fetch", fetch);
    expect(await fetchDiagnostics()).toBe("### Labcoat diagnostics\n");
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/diagnostics");
    expect(await copyDiagnostics()).toBe(true);
    expect(clipboard.text).toBe("### Labcoat diagnostics\n");
    expect(toasts().at(-1)).toContain("Copied diagnostics");
  });

  it("says why it could not copy", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({}, { status: 500 })));
    expect(await copyDiagnostics()).toBe(false);
    expect(toasts().at(-1)).toContain("Could not copy diagnostics: the host answered 500");

    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ report: {} })));
    await expect(fetchDiagnostics()).rejects.toThrow("the host sent no report");

    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ markdown: "md" })));
    clipboard.fail = true;
    expect(await copyDiagnostics()).toBe(false);
    expect(toasts().at(-1)).toContain("Could not copy diagnostics: denied");

    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject("offline")));
    expect(await copyDiagnostics()).toBe(false);
    expect(toasts().at(-1)).toContain("Could not copy diagnostics: offline");
  });
});
