using System.Globalization;
using System.Text;
using System.Xml;

namespace Labcoat;

/// <summary>How coverage file paths are written.</summary>
/// <param name="Root">The workspace root (the common root of the input paths); paths under it are written relative to it.</param>
/// <param name="AbsolutePaths">Write every path absolute instead.</param>
public sealed record CoverageExportOptions(string? Root, bool AbsolutePaths = false)
{
    /// <summary>A source file's path as written: relative to <see cref="Root"/> with '/' separators, else absolute.</summary>
    public string PathOf(string path)
    {
        if (AbsolutePaths || Root is null) return path;
        var full = PathUtil.CanonicalizeOrSelf(path);
        var root = PathUtil.CanonicalizeOrSelf(Root);
        return PathUtil.IsUnder(full, root) ? Path.GetRelativePath(root, full).Replace('\\', '/') : path;
    }
}

/// <summary>
/// Writes a coverage report (CoverageReport: a run's own report or the full report of the Coverage page) as lcov
/// (<c>lcov.info</c>: SF / DA / BRDA / BRF / BRH / LF / LH / end_of_record, as genhtml, Codecov, Coveralls and SonarQube
/// read it) or as Cobertura XML. The report already holds only what the coverage settings counted (CoverageSettings.cs;
/// the Cobertura file names them in a comment). Cobertura and the report have per-line branch counts only, so lcov's
/// BRDA lines number the branches of a line 0..n-1 in one block: the first <c>branchesCovered</c> are taken.
/// </summary>
public static class CoverageExport
{
    /// <summary>Writes <paramref name="report"/> as <paramref name="format"/> (lcov or cobertura) to <paramref name="stream"/> (left open).</summary>
    public static void Write(string format, CoverageReport report, CoverageExportOptions options, Stream stream)
    {
        if (format == ExportFormats.Cobertura)
        {
            WriteCobertura(report, options, stream);
            return;
        }
        using var w = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
        WriteLcov(report, options, w);
    }

    public static string ToString(string format, CoverageReport report, CoverageExportOptions options)
    {
        using var ms = new MemoryStream();
        Write(format, report, options, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ------------------------------------------------------------------ lcov

    public static void WriteLcov(CoverageReport report, CoverageExportOptions options, TextWriter o)
    {
        foreach (var f in Files(report))
        {
            o.Write("TN:\n");
            o.Write($"SF:{OneLine(options.PathOf(f.Path))}\n");
            int linesFound = 0, linesHit = 0, branchesFound = 0, branchesHit = 0;
            foreach (var l in f.Lines.OrderBy(l => l.Line))
            {
                linesFound++;
                if (l.Hits > 0) linesHit++;
                if (l.BranchesTotal is not > 0) continue;
                var covered = Math.Clamp(l.BranchesCovered ?? 0, 0, l.BranchesTotal.Value);
                for (var b = 0; b < l.BranchesTotal; b++) o.Write($"BRDA:{Int(l.Line)},0,{Int(b)},{Taken(l.Hits, b < covered)}\n");
                branchesFound += l.BranchesTotal.Value;
                branchesHit += covered;
            }
            o.Write($"BRF:{Int(branchesFound)}\nBRH:{Int(branchesHit)}\n");
            foreach (var l in f.Lines.OrderBy(l => l.Line)) o.Write($"DA:{Int(l.Line)},{Int(Math.Max(0, l.Hits))}\n");
            o.Write($"LF:{Int(linesFound)}\nLH:{Int(linesHit)}\n");
            o.Write("end_of_record\n");
        }
    }

    /// <summary>BRDA's "taken": "-" when the line never ran, else 1 / 0.</summary>
    private static string Taken(int lineHits, bool covered)
    {
        if (lineHits <= 0) return "-";
        return covered ? "1" : "0";
    }

    /// <summary>A path on one line (an lcov record is line based).</summary>
    private static string OneLine(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

    // ------------------------------------------------------------------ Cobertura

    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Indent = true,
        Encoding = new UTF8Encoding(false),
        NewLineChars = "\n",
    };

    private const string LineRateAttr = "line-rate";
    private const string BranchRateAttr = "branch-rate";
    private const string ComplexityAttr = "complexity";
    private const string NameAttr = "name";

    public static void WriteCobertura(CoverageReport report, CoverageExportOptions options, Stream stream)
    {
        using var w = XmlWriter.Create(stream, XmlSettings);
        w.WriteStartDocument();
        w.WriteComment(" " + Comment(Note(report, options)) + " ");
        w.WriteStartElement("coverage");
        w.WriteAttributeString(LineRateAttr, Rate(report.LinesCovered, report.LinesValid));
        w.WriteAttributeString(BranchRateAttr, Rate(report.BranchesCovered, report.BranchesValid));
        w.WriteAttributeString("lines-covered", Int(report.LinesCovered));
        w.WriteAttributeString("lines-valid", Int(report.LinesValid));
        w.WriteAttributeString("branches-covered", Int(report.BranchesCovered));
        w.WriteAttributeString("branches-valid", Int(report.BranchesValid));
        w.WriteAttributeString(ComplexityAttr, "0");
        w.WriteAttributeString("version", "Labcoat " + Cli.Version);
        w.WriteAttributeString("timestamp", Int64(Timestamp(report.GeneratedAt)));
        w.WriteStartElement("sources");
        if (!options.AbsolutePaths && options.Root is { } root) w.WriteElementString("source", ExportText.Clean(root));
        w.WriteEndElement();
        w.WriteStartElement("packages");
        foreach (var module in Files(report).GroupBy(f => f.Module, StringComparer.Ordinal))
            WritePackage(w, module.Key, module.ToList(), options);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WritePackage(XmlWriter w, string module, List<FileCoverage> files, CoverageExportOptions options)
    {
        w.WriteStartElement("package");
        w.WriteAttributeString(NameAttr, ExportText.Clean(module));
        w.WriteAttributeString(LineRateAttr, Rate(files.Sum(f => f.LinesCovered), files.Sum(f => f.LinesValid)));
        w.WriteAttributeString(BranchRateAttr, Rate(files.Sum(f => f.BranchesCovered), files.Sum(f => f.BranchesValid)));
        w.WriteAttributeString(ComplexityAttr, "0");
        w.WriteStartElement("classes");
        foreach (var f in files)
        {
            var path = ExportText.Clean(options.PathOf(f.Path));
            w.WriteStartElement("class");
            w.WriteAttributeString(NameAttr, ClassName(path));
            w.WriteAttributeString("filename", path);
            w.WriteAttributeString(LineRateAttr, Rate(f.LinesCovered, f.LinesValid));
            w.WriteAttributeString(BranchRateAttr, Rate(f.BranchesCovered, f.BranchesValid));
            w.WriteAttributeString(ComplexityAttr, "0");
            w.WriteElementString("methods", "");
            w.WriteStartElement("lines");
            foreach (var l in f.Lines.OrderBy(l => l.Line)) WriteLine(w, l);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>"Dir.Sub.File" for a relative path, the file name for an absolute one (Cobertura has no class names here).</summary>
    internal static string ClassName(string path) =>
        Path.IsPathRooted(path) ? Path.GetFileNameWithoutExtension(path) : Path.ChangeExtension(path, null).Replace('/', '.').Replace('\\', '.');

    private static void WriteLine(XmlWriter w, CoverageLineHit l)
    {
        w.WriteStartElement("line");
        w.WriteAttributeString("number", Int(l.Line));
        w.WriteAttributeString("hits", Int(Math.Max(0, l.Hits)));
        var branch = l.BranchesTotal is > 0;
        w.WriteAttributeString("branch", branch ? "true" : "false");
        if (branch)
        {
            var covered = Math.Clamp(l.BranchesCovered ?? 0, 0, l.BranchesTotal!.Value);
            var percent = (int)Math.Round(100.0 * covered / l.BranchesTotal.Value);
            w.WriteAttributeString("condition-coverage", $"{Int(percent)}% ({Int(covered)}/{Int(l.BranchesTotal.Value)})");
        }
        w.WriteEndElement();
    }

    /// <summary>
    /// What the file holds: the report's scope and run, the coverage settings per project, stale files. Written as a
    /// comment into the Cobertura file and printed by the command line.
    /// </summary>
    public static string Note(CoverageReport report, CoverageExportOptions options)
    {
        var sb = new StringBuilder();
        sb.Append($"Labcoat {Cli.Version}: coverage of run {report.RunId} ({(report.Scope == "subset" ? "selected tests" : "all tests")}), generated {report.GeneratedAt}; ");
        sb.Append(options.AbsolutePaths || options.Root is null ? "absolute paths" : $"paths relative to {options.Root}");
        foreach (var s in report.Settings ?? []) sb.Append($". {s.ProjectId}: {s.Describe(options.Root)}");
        if (report.StaleFiles is { Count: > 0 } stale) sb.Append($". {stale.Count} file(s) changed since their coverage was collected");
        if (report.Merged is { Count: > 0 } merged) sb.Append($". Includes {merged.Count} later run(s) of selected tests");
        return sb.ToString();
    }

    /// <summary>Text for an XML comment: no "--" and no trailing '-' (both are invalid there).</summary>
    private static string Comment(string s) => ExportText.Clean(s).Replace("--", "- -").TrimEnd('-');

    private static IEnumerable<FileCoverage> Files(CoverageReport report) =>
        report.Files.OrderBy(f => f.Module, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal);

    internal static string Rate(int covered, int valid) =>
        (valid <= 0 ? 1.0 : (double)covered / valid).ToString("0.####", CultureInfo.InvariantCulture);

    private static long Timestamp(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeSeconds() : 0;

    private static string Int(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Int64(long n) => n.ToString(CultureInfo.InvariantCulture);
}
