using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// Parses MSBuild console output (canonical error format) into <see cref="BuildDiagnostic"/>s:
/// <c>path(line,col): error CS1234: message [project]</c>, <c>path : warning NU1603: message [project]</c>,
/// <c>CSC : error CS2001: ...</c>, <c>MSBUILD : error MSB1009: ...</c>. The location may also be <c>(line)</c> or
/// <c>(line,col,endLine,endCol)</c>. MSBuild repeats every diagnostic in its summary, so results are deduplicated.
/// Errors come first, then warnings, each in order of appearance.
/// </summary>
public static partial class BuildOutputParser
{
    public const int MaxDiagnostics = 200;

    // BuildDiagnostic.severity
    private const string Error = "error";
    private const string Warning = "warning";

    [GeneratedRegex(
        @"^\s*(?<origin>.*?)(?:\((?<loc>\d+(?:\s*,\s*\d+){0,3})\))?\s*:\s*(?<sev>error|warning)(?:\s+(?<code>[A-Za-z]+[A-Za-z0-9]*\d+))?\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\[\]]+)\])?\s*$",
        RegexOptions.IgnoreCase, RegexTimeout.Milliseconds)]
    private static partial Regex LineRe();

    public static List<BuildDiagnostic> Parse(string? output) => Parse(output, out _, out _);

    /// <param name="errorCount">Distinct errors (before capping at <see cref="MaxDiagnostics"/>).</param>
    /// <param name="warningCount">Distinct warnings (before capping).</param>
    public static List<BuildDiagnostic> Parse(string? output, out int errorCount, out int warningCount)
    {
        errorCount = warningCount = 0;
        var errors = new List<BuildDiagnostic>();
        var warnings = new List<BuildDiagnostic>();
        if (string.IsNullOrEmpty(output)) return errors;
        var seen = new HashSet<BuildDiagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            if (ParseLine(raw.TrimEnd('\r')) is not { } d) continue;
            // Same diagnostic with and without the trailing [project] (summary vs inline) counts once.
            if (!seen.Add(d with { Project = null })) continue;
            (d.Severity == Error ? errors : warnings).Add(d);
        }
        errorCount = errors.Count;
        warningCount = warnings.Count;
        errors.AddRange(warnings);
        return errors.Count > MaxDiagnostics ? errors.GetRange(0, MaxDiagnostics) : errors;
    }

    /// <summary>One line of build output; null when it is no diagnostic.</summary>
    private static BuildDiagnostic? ParseLine(string line)
    {
        if (line.Length == 0 || line.Length > 8000) return null;
        if (!line.Contains(Error, StringComparison.OrdinalIgnoreCase) && !line.Contains(Warning, StringComparison.OrdinalIgnoreCase)) return null;
        var m = LineRe().MatchOrEmpty(line);
        if (!m.Success) return null;

        var origin = m.Groups["origin"].Value.Trim();
        // "Build FAILED." / "0 Warning(s)" style lines and prose never match the "<sev> CODE:" shape, but guard
        // against origins that are clearly sentences.
        if (origin.Length > 0 && origin.Contains(' ') && !LooksLikePath(origin)) return null;
        var message = m.Groups["msg"].Value.Trim();
        if (message.Length == 0) return null;
        var (lineNo, col) = Location(m.Groups["loc"]);
        return new BuildDiagnostic
        {
            File = LooksLikePath(origin) ? origin : null,
            Line = lineNo,
            Column = col,
            Code = m.Groups["code"].Success ? m.Groups["code"].Value : null,
            Severity = m.Groups["sev"].Value.Equals(Error, StringComparison.OrdinalIgnoreCase) ? Error : Warning,
            Message = message,
            Project = ProjectOf(m.Groups["proj"]),
        };
    }

    /// <summary>"(line,column)" of a diagnostic (the first two of up to four numbers).</summary>
    private static (int? Line, int? Column) Location(Group loc)
    {
        if (!loc.Success) return (null, null);
        var parts = loc.Value.Split(',', StringSplitOptions.TrimEntries);
        int? lineNo = int.TryParse(parts[0], out var l) ? l : null;
        int? col = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : null;
        return (lineNo, col);
    }

    private static string? ProjectOf(Group proj)
    {
        if (!proj.Success) return null;
        var project = proj.Value.Trim();
        var sep = project.IndexOf("::", StringComparison.Ordinal); // "[x.csproj::TargetFramework=net10.0]"
        return sep > 0 ? project[..sep] : project;
    }

    private static bool LooksLikePath(string origin) =>
        origin.Length > 0 && (origin.Contains('/') || origin.Contains('\\') || Path.HasExtension(origin))
        && !origin.Equals("CSC", StringComparison.OrdinalIgnoreCase);
}
