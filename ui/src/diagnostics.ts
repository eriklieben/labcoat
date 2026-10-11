/**
 * "Copy diagnostics" (command palette, Settings → About): the host's diagnostics report (GET /api/diagnostics: Labcoat
 * version, OS, .NET, the workspace, the test projects and their test packages, settings with secrets redacted and the
 * last log lines) as markdown on the clipboard, for a bug report.
 */
import { toast } from "@eriklieben/labcoat-design-system";

/** The report as markdown. Throws when the host can't be reached or answers with an error. */
export async function fetchDiagnostics(): Promise<string> {
  const res = await fetch(new URL("./api/diagnostics", location.href));
  if (!res.ok) throw new Error(`the host answered ${res.status}`);
  const body = (await res.json()) as { markdown?: unknown };
  if (typeof body.markdown !== "string") throw new Error("the host sent no report");
  return body.markdown;
}

/** Copies the report; a toast says how it went. Resolves to whether it is on the clipboard. */
export async function copyDiagnostics(): Promise<boolean> {
  try {
    const markdown = await fetchDiagnostics();
    if (!navigator.clipboard) throw new Error("the clipboard is not available here (it needs https or localhost)");
    await navigator.clipboard.writeText(markdown);
    toast("Copied diagnostics: paste them into the bug report", { intent: "success", icon: "copy" });
    return true;
  } catch (e) {
    toast(`Could not copy diagnostics: ${e instanceof Error ? e.message : String(e)}`, { intent: "error" });
    return false;
  }
}
