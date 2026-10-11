namespace Labcoat;

// Export endpoints (docs/contract.md "Export"): a run as JUnit XML / HTML report, a coverage report as lcov / Cobertura.
// Plain GETs like the rest of the read API: the Host check applies (Security.cs); downloads are attachments that a
// cross-site page can trigger but never read (no CORS).
public static partial class LabcoatApp
{
    private static void MapExportEndpoints(WebApplication app, Explorer explorer)
    {
        // runId = a persisted run's id or "latest"; format = junit | html.
        app.MapGet("/api/runs/{runId}/export", (HttpContext ctx, string runId, string? format) => ExportRun(ctx, explorer, runId, format));
        // format = lcov | cobertura; scope = full (default, the Coverage page's report) | last (the latest coverage run's
        // own report); paths = relative (default: to the workspace root) | absolute.
        app.MapGet("/api/coverage/export", (HttpContext ctx, string? format, string? scope, string? paths) => ExportCoverage(ctx, explorer, format, scope, paths));
    }

    private static IResult ExportRun(HttpContext ctx, Explorer explorer, string runId, string? format)
    {
        if (format is not (ExportFormats.JUnit or ExportFormats.Html))
            return Results.BadRequest(new { error = "format must be junit or html" });
        if (explorer.ExportRun(runId) is not { } export)
            return Results.NotFound(new { error = runId == Explorer.LatestRun ? "No runs yet" : "Unknown run id", runId });
        using var ms = new MemoryStream();
        WriteRunExport(export, format, ms);
        return Download(ctx, ms.ToArray(), format, ExportFormats.FileName(export.WorkspaceName, export.Run.RunId, format));
    }

    private static IResult ExportCoverage(HttpContext ctx, Explorer explorer, string? format, string? scope, string? paths)
    {
        if (format is not (ExportFormats.Lcov or ExportFormats.Cobertura))
            return Results.BadRequest(new { error = "format must be lcov or cobertura" });
        if (scope is not (null or "" or "full" or "last")) return Results.BadRequest(new { error = "scope must be full or last" });
        if (paths is not (null or "" or "relative" or "absolute")) return Results.BadRequest(new { error = "paths must be relative or absolute" });
        var report = scope == "last" ? explorer.LastCoverageReport : explorer.FullCoverageReport;
        if (report is null) return Results.NotFound(new { error = "No coverage report yet: run the tests with coverage first" });
        using var ms = new MemoryStream();
        CoverageExport.Write(format, report, new CoverageExportOptions(explorer.ExportRoot, paths == "absolute"), ms);
        return Download(ctx, ms.ToArray(), format, ExportFormats.FileName(explorer.ExportWorkspaceName, report.RunId, format));
    }

    /// <summary>Writes a run export (JUnit XML or HTML report) as UTF-8 to <paramref name="stream"/>.</summary>
    internal static void WriteRunExport(RunExport export, string format, Stream stream)
    {
        if (format == ExportFormats.JUnit)
        {
            JUnitWriter.Write(export, stream);
            return;
        }
        using var w = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
        HtmlReport.Write(export, w);
    }

    /// <summary>An attachment (Content-Disposition with the file name), never cached.</summary>
    private static IResult Download(HttpContext ctx, byte[] bytes, string format, string fileName)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.File(bytes, ExportFormats.ContentType(format), fileName);
    }
}
