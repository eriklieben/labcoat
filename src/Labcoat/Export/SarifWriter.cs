using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Labcoat;

/// <summary>
/// Writes the failures of a run as SARIF 2.1.0 (GitHub code scanning, Azure DevOps SARIF viewer, VS Code SARIF viewer):
/// one <c>run</c> with the Labcoat tool, two rules (<see cref="AssertionRule"/> and <see cref="ErrorRule"/>, the JUnit
/// failure / error split) and one <c>result</c> per failed test with its message and the source location of the
/// assertion (else of the test, else of its project file). Quarantined failures are <c>note</c>s: reported, but not
/// failures of the run. Passed and skipped tests are not results. Locations are relative to <c>%SRCROOT%</c> (the
/// workspace root) when the file is under it.
/// </summary>
public static class SarifWriter
{
    public const string AssertionRule = "labcoat/assertion-failure";
    public const string ErrorRule = "labcoat/test-error";
    private const string SrcRoot = "%SRCROOT%";

    /// <summary>The message of a result.</summary>
    public const int MaxMessageChars = 4096;

    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Writes the report as UTF-8 to <paramref name="stream"/> (left open).</summary>
    public static void Write(RunExport export, Stream stream)
    {
        using var w = new Utf8JsonWriter(stream, Options);
        var projects = export.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        w.WriteStartObject();
        w.WriteString("$schema", "https://json.schemastore.org/sarif-2.1.0.json");
        w.WriteString("version", "2.1.0");
        w.WriteStartArray("runs");
        w.WriteStartObject();

        w.WriteStartObject("tool");
        w.WriteStartObject("driver");
        w.WriteString("name", "Labcoat");
        w.WriteString("version", export.Version);
        w.WriteString("informationUri", "https://github.com/eriklieben/labcoat");
        w.WriteStartArray("rules");
        WriteRule(w, AssertionRule, "AssertionFailure", "A test failed an assertion.");
        WriteRule(w, ErrorRule, "TestError", "A test threw an unexpected exception, timed out or crashed its test host.");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartArray("invocations");
        w.WriteStartObject();
        w.WriteBoolean("executionSuccessful", export.Run.Aborted != true && export.Run.Cancelled != true);
        w.WriteString("startTimeUtc", Utc(export.Run.StartedAt));
        if (export.Run.FinishedAt is { } finished) w.WriteString("endTimeUtc", Utc(finished));
        w.WriteEndObject();
        w.WriteEndArray();

        w.WriteStartArray("results");
        foreach (var t in export.Tests)
        {
            if (t.Kind is not (ExportKind.Failure or ExportKind.Error or ExportKind.QuarantinedFailure)) continue;
            projects.TryGetValue(t.ProjectId, out var project);
            WriteResult(w, t, project);
        }
        w.WriteEndArray();

        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>The report as a string (tests, small exports).</summary>
    public static string ToJson(RunExport export)
    {
        using var ms = new MemoryStream();
        Write(export, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteRule(Utf8JsonWriter w, string id, string name, string description)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("name", name);
        w.WriteStartObject("shortDescription");
        w.WriteString("text", description);
        w.WriteEndObject();
        w.WriteStartObject("defaultConfiguration");
        w.WriteString("level", "error");
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteResult(Utf8JsonWriter w, ExportTest t, ExportProject? project)
    {
        var quarantined = t.Kind == ExportKind.QuarantinedFailure;
        var assertion = t.Kind == ExportKind.Failure || (quarantined && ExportKinds.IsAssertion(t.ErrorType));
        w.WriteStartObject();
        w.WriteString("ruleId", assertion ? AssertionRule : ErrorRule);
        w.WriteNumber("ruleIndex", assertion ? 0 : 1);
        w.WriteString("level", quarantined ? "note" : "error");
        w.WriteStartObject("message");
        var message = ExportText.Cap(ExportText.Clean(t.ErrorMessage).Trim(), MaxMessageChars);
        if (message.Length == 0) message = t.Outcome == Outcome.TimedOut ? "Timed out" : "Failed";
        w.WriteString("text", quarantined ? $"{t.Name} (quarantined): {message}" : $"{t.Name}: {message}");
        w.WriteEndObject();

        var file = t.SourceFile ?? project?.Path;
        var line = t.SourceFile is null ? null : t.SourceLine;
        w.WriteStartArray("locations");
        w.WriteStartObject();
        if (file is not null)
        {
            w.WriteStartObject("physicalLocation");
            w.WriteStartObject("artifactLocation");
            var absolute = Path.IsPathRooted(file) || file.Contains(':');
            w.WriteString("uri", absolute ? new Uri(file).AbsoluteUri : Uri(file));
            if (!absolute) w.WriteString("uriBaseId", SrcRoot);
            w.WriteEndObject();
            w.WriteStartObject("region");
            w.WriteNumber("startLine", line ?? 1);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteStartArray("logicalLocations");
        w.WriteStartObject();
        w.WriteString("name", ExportText.Clean(t.Name));
        w.WriteString("fullyQualifiedName", ExportText.Clean(t.ClassFullName.Length > 0 ? $"{t.ClassFullName}.{t.Name}" : t.Name));
        w.WriteString("kind", "function");
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndArray();

        // GitHub code scanning uses this to recognise the same failure across runs.
        w.WriteStartObject("partialFingerprints");
        w.WriteString("labcoatTestId/v1", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{t.ProjectId}\n{t.Id}")))[..32].ToLowerInvariant());
        w.WriteEndObject();

        w.WriteStartObject("properties");
        w.WriteString("project", t.ProjectId);
        w.WriteString("testId", t.Id);
        if (t.ErrorType is { Length: > 0 }) w.WriteString("errorType", t.ErrorType);
        if (quarantined) w.WriteBoolean("quarantined", true);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>A relative path as a URI reference: '/' separators, each segment escaped.</summary>
    private static string Uri(string path) => string.Join('/', path.Split('/').Select(System.Uri.EscapeDataString));

    private static string Utc(string iso) =>
        DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)
            : iso;
}
