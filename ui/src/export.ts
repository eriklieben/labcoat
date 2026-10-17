/**
 * Export downloads (contract "Export"): a run as JUnit XML, a self-contained HTML report, Markdown, TRX or SARIF
 * (`GET /api/runs/{runId}/export?format=junit|html|markdown|trx|sarif`, runId `latest` = the newest stored run) and the coverage report as
 * lcov or Cobertura (`GET /api/coverage/export?format=lcov|cobertura&scope=full|last`). The file is fetched (so a 404
 * becomes a toast, not a downloaded error page) and saved under the name the host's Content-Disposition gives.
 * Used by the run switcher's menu, the command palette and the Coverage page.
 */
import { toast, type IconName } from "@eriklieben/labcoat-design-system";

export type RunExportFormat = "junit" | "html" | "markdown" | "trx" | "sarif";
export type CoverageExportFormat = "lcov" | "cobertura";

export const RUN_EXPORTS: readonly { format: RunExportFormat; label: string; detail: string }[] = [
  { format: "html", label: "HTML report", detail: "a single file to open in any browser" },
  { format: "junit", label: "JUnit XML", detail: "for CI test reporters" },
  { format: "markdown", label: "Markdown", detail: "for a PR comment or a step summary" },
  { format: "trx", label: "TRX", detail: "for Visual Studio and Azure DevOps" },
  { format: "sarif", label: "SARIF", detail: "failures as results, for code scanning" },
];

export const COVERAGE_EXPORTS: readonly { format: CoverageExportFormat; label: string; detail: string }[] = [
  { format: "lcov", label: "lcov", detail: "lcov.info for Codecov, Coveralls, genhtml, SonarQube" },
  { format: "cobertura", label: "Cobertura XML", detail: "for ReportGenerator, Azure Pipelines, GitLab" },
];

/** The export URL of a run (undefined = the latest stored run). */
export function runExportUrl(runId: string | undefined, format: RunExportFormat): string {
  return `./api/runs/${encodeURIComponent(runId ?? "latest")}/export?format=${format}`;
}

/** The export URL of the coverage report: `full` = the Coverage page's report, `last` = the latest coverage run's own. */
export function coverageExportUrl(format: CoverageExportFormat, scope: "full" | "last" = "full"): string {
  return `./api/coverage/export?format=${format}&scope=${scope}`;
}

/** The file name of a Content-Disposition header (RFC 6266: `filename*` wins over `filename`), else `fallback`. */
export function fileNameOf(disposition: string | null, fallback: string): string {
  if (!disposition) return fallback;
  const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(disposition);
  if (star) {
    try {
      return decodeURIComponent(star[1].trim());
    } catch {
      // a malformed escape: fall back to the plain filename parameter
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/.exec(disposition);
  return plain ? plain[1].trim() : fallback;
}

async function errorOf(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { error?: unknown };
    if (typeof body.error === "string") return body.error;
  } catch {
    // not JSON: report the status instead
  }
  return `the host answered ${res.status}`;
}

/** Saves a Blob under `name` (a temporary object URL on a download link). */
export function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.rel = "noopener";
  a.style.display = "none";
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Fetches `url` and saves it; a toast says how it went. Resolves to whether the file was saved. */
export async function download(url: string, what: string, fallbackName: string): Promise<boolean> {
  try {
    const res = await fetch(new URL(url, location.href));
    if (!res.ok) throw new Error(await errorOf(res));
    const name = fileNameOf(res.headers.get("content-disposition"), fallbackName);
    saveBlob(await res.blob(), name);
    toast(`Exported ${what}: ${name}`, { intent: "success", icon: "arrowDownload" });
    return true;
  } catch (e) {
    toast(`Could not export ${what}: ${e instanceof Error ? e.message : String(e)}`, { intent: "error" });
    return false;
  }
}

const RUN_EXPORT_EXTENSIONS: Record<RunExportFormat, string> = {
  junit: "junit.xml",
  html: "html",
  markdown: "md",
  trx: "trx",
  sarif: "sarif",
};

export function exportRun(runId: string | undefined, format: RunExportFormat): Promise<boolean> {
  const label = RUN_EXPORTS.find((x) => x.format === format)?.label ?? format;
  const ext = RUN_EXPORT_EXTENSIONS[format];
  return download(runExportUrl(runId, format), `the run as ${label}`, `labcoat-run.${ext}`);
}

export function exportCoverage(format: CoverageExportFormat, scope: "full" | "last" = "full"): Promise<boolean> {
  const label = COVERAGE_EXPORTS.find((x) => x.format === format)?.label ?? format;
  const ext = format === "lcov" ? "info" : "cobertura.xml";
  return download(coverageExportUrl(format, scope), `coverage as ${label}`, `labcoat-coverage.${ext}`);
}

/** Command palette entries: export the shown run (the viewed past run, else the latest) and the coverage report. */
export function exportCommands(o: { viewingRunId: string | undefined; hasRuns: boolean; hasCoverage: boolean }): {
  id: string;
  label: string;
  detail?: string;
  icon: IconName;
  disabled?: boolean;
  run: () => void;
}[] {
  const which = o.viewingRunId ? "the viewed run" : "the latest run";
  return [
    ...RUN_EXPORTS.map((x) => ({
      id: `export-${x.format}`,
      label: `Export run as ${x.label}`,
      detail: o.hasRuns ? `${which}: ${x.detail}` : "no runs yet",
      icon: "arrowDownload" as IconName,
      disabled: !o.hasRuns,
      run: () => void exportRun(o.viewingRunId, x.format),
    })),
    ...COVERAGE_EXPORTS.map((x) => ({
      id: `export-${x.format}`,
      label: `Export coverage as ${x.label}`,
      detail: o.hasCoverage ? x.detail : "no coverage report yet",
      icon: "arrowDownload" as IconName,
      disabled: !o.hasCoverage,
      run: () => void exportCoverage(x.format),
    })),
  ];
}
