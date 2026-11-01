import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h } from "@eriklieben/labcoat-design-system";
import "../../src/components/coverage-page";
import { coverageExportUrl, download, exportCommands, exportCoverage, exportRun, fileNameOf, runExportUrl, saveBlob } from "../../src/export";
import { asFull, click, coverageReport, fileCoverage, flushFrame, key, makeStore, mount, text } from "../helpers/app";
import { flushMicrotasks } from "../helpers/dom";

const toasts = () => [...document.querySelectorAll(".lc-toast")].map((t) => text(t));

/** A download response: a body and a Content-Disposition header. */
function fileResponse(body: string, disposition: string | null, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: new Headers(disposition ? { "content-disposition": disposition } : {}),
    blob: async () => new Blob([body]),
    json: async () => JSON.parse(body) as unknown,
  } as unknown as Response;
}

let saved: { name: string; href: string }[] = [];
beforeEach(() => {
  saved = [];
  vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: vi.fn(() => "blob:x"), revokeObjectURL: vi.fn() }));
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    saved.push({ name: this.download, href: this.href });
  });
});
afterEach(() => {
  document.querySelectorAll(".lc-toaster").forEach((t) => t.remove());
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("export URLs and file names", () => {
  it("builds the run and coverage export URLs (latest when no run is given; ids are encoded)", () => {
    expect(runExportUrl(undefined, "junit")).toBe("./api/runs/latest/export?format=junit");
    expect(runExportUrl("a/b c", "html")).toBe("./api/runs/a%2Fb%20c/export?format=html");
    expect(coverageExportUrl("lcov")).toBe("./api/coverage/export?format=lcov&scope=full");
    expect(coverageExportUrl("cobertura", "last")).toBe("./api/coverage/export?format=cobertura&scope=last");
  });

  it("reads the file name from Content-Disposition (filename* first)", () => {
    expect(fileNameOf(null, "x")).toBe("x");
    expect(fileNameOf("attachment", "x")).toBe("x");
    expect(fileNameOf('attachment; filename="labcoat-shop-1.junit.xml"', "x")).toBe("labcoat-shop-1.junit.xml");
    expect(fileNameOf("attachment; filename=a.html; filename*=UTF-8''r%C3%A9sum%C3%A9.html", "x")).toBe("résumé.html");
    expect(fileNameOf("attachment; filename=plain.info; filename*=UTF-8''%E0%A4%A", "x")).toBe("plain.info");
  });
});

describe("download", () => {
  it("saves the file under the host's name and says so", async () => {
    const fetch = vi.fn(async () => fileResponse("<xml/>", 'attachment; filename="labcoat-shop-r1.junit.xml"'));
    vi.stubGlobal("fetch", fetch);
    expect(await exportRun("r1", "junit")).toBe(true);
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/runs/r1/export?format=junit");
    expect(saved).toEqual([{ name: "labcoat-shop-r1.junit.xml", href: "blob:x" }]);
    expect(toasts().at(-1)).toContain("Exported the run as JUnit XML: labcoat-shop-r1.junit.xml");
    expect(document.querySelector("a[download]")).toBeNull(); // the temporary link is gone
  });

  it("falls back to a default name and reports host errors (JSON error, or the status)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => fileResponse("data", null)));
    expect(await exportCoverage("cobertura")).toBe(true);
    expect(saved.at(-1)!.name).toBe("labcoat-coverage.cobertura.xml");
    expect(await exportCoverage("lcov", "last")).toBe(true);
    expect(saved.at(-1)!.name).toBe("labcoat-coverage.info");
    expect(await exportRun(undefined, "html")).toBe(true);
    expect(saved.at(-1)!.name).toBe("labcoat-run.html");

    vi.stubGlobal("fetch", vi.fn(async () => fileResponse(JSON.stringify({ error: "No runs yet" }), null, 404)));
    expect(await exportRun(undefined, "html")).toBe(false);
    expect(toasts().at(-1)).toContain("Could not export the run as HTML report: No runs yet");

    vi.stubGlobal("fetch", vi.fn(async () => fileResponse("not json", null, 500)));
    expect(await download("./api/x", "it", "x")).toBe(false);
    expect(toasts().at(-1)).toContain("Could not export it: the host answered 500");

    vi.stubGlobal("fetch", vi.fn(async () => fileResponse(JSON.stringify({ other: 1 }), null, 400)));
    expect(await download("./api/x", "it", "x")).toBe(false);
    expect(toasts().at(-1)).toContain("the host answered 400");

    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject("offline")));
    expect(await download("./api/x", "it", "x")).toBe(false);
    expect(toasts().at(-1)).toContain("Could not export it: offline");
  });

  it("revokes the object URL after the download started", () => {
    vi.useFakeTimers();
    try {
      saveBlob(new Blob(["x"]), "f.txt");
      expect(URL.revokeObjectURL).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1000);
      expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:x");
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("command palette entries", () => {
  it("export the viewed / latest run and the coverage report; disabled without data", () => {
    vi.stubGlobal("fetch", vi.fn(async () => fileResponse("x", null)));
    const none = exportCommands({ viewingRunId: undefined, hasRuns: false, hasCoverage: false });
    expect(none.map((c) => c.label)).toEqual([
      "Export run as HTML report",
      "Export run as JUnit XML",
      "Export run as Markdown",
      "Export run as TRX",
      "Export run as SARIF",
      "Export coverage as lcov",
      "Export coverage as Cobertura XML",
    ]);
    expect(none.every((c) => c.disabled && c.icon === "arrowDownload")).toBe(true);
    expect(none[0].detail).toBe("no runs yet");
    expect(none[5].detail).toBe("no coverage report yet");

    const some = exportCommands({ viewingRunId: "r2", hasRuns: true, hasCoverage: true });
    expect(some.some((c) => c.disabled)).toBe(false);
    expect(some[1].detail).toContain("the viewed run");
    some[1].run();
    some[5].run();
    const fetch = globalThis.fetch as unknown as ReturnType<typeof vi.fn>;
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/runs/r2/export?format=junit");
    expect(String((fetch.mock.calls[1] as unknown[])[0])).toContain("/api/coverage/export?format=lcov&scope=full");
    expect(exportCommands({ viewingRunId: undefined, hasRuns: true, hasCoverage: true })[0].detail).toContain("the latest run");
  });
});

describe("Coverage page Export menu", () => {
  it("is a labelled menu button that downloads lcov / Cobertura, keyboard operable", async () => {
    const fetch = vi.fn(async () => fileResponse("TN:", 'attachment; filename="labcoat-shop-full.info"'));
    vi.stubGlobal("fetch", fetch);
    makeStore({ state: asFull(coverageReport({ runId: "full", files: [fileCoverage({ path: "/src/A.cs", relativePath: "A.cs" })] })) });
    await flushFrame();
    const el = mount(h("lc-coverage-page"));
    const btn = el.querySelector<HTMLButtonElement>(".cov-export")!;
    expect(btn).not.toBeNull();
    expect(text(btn)).toContain("Export");
    expect(btn.getAttribute("aria-haspopup")).toBe("menu");
    key(btn, "ArrowDown");
    await flushMicrotasks();
    const menu = document.querySelector<HTMLElement>(".lc-menu [role=menu]")!;
    expect(menu.getAttribute("aria-label")).toBe("Export coverage");
    const items = [...menu.querySelectorAll<HTMLButtonElement>("[role=menuitem]")];
    expect(items.map((i) => i.dataset.text)).toEqual(["Export as lcov", "Export as Cobertura XML"]);
    click(items[0]);
    await flushMicrotasks();
    await flushMicrotasks();
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/coverage/export?format=lcov&scope=full");
    expect(saved.at(-1)?.name).toBe("labcoat-shop-full.info");
    el.remove();
  });

  it("is not shown on the Tests page's coverage tab (no page)", async () => {
    makeStore({ state: {} });
    await flushFrame();
    const el = mount(h("lc-coverage-overview", { report: coverageReport({ runId: "x" }) }));
    expect(el.querySelector(".cov-export")).toBeNull();
    el.remove();
  });
});
