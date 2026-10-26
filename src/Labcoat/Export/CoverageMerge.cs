using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Labcoat;

/// <summary>The totals of merged coverage.</summary>
public sealed record CoverageTotals(int Files, int LinesCovered, int LinesValid, int BranchesCovered, int BranchesValid)
{
    public double LineRate => LinesValid > 0 ? (double)LinesCovered / LinesValid : 1;
    public double BranchRate => BranchesValid > 0 ? (double)BranchesCovered / BranchesValid : 1;
}

/// <summary>
/// Combines lcov and Cobertura files (the per-shard coverage of a split run) into one report that can be written as
/// either format. Files are matched by their path as written; per source file the hit counts of every line (lcov
/// <c>DA</c>) and branch (<c>BRDA</c>, by line, block and branch) are summed, and the line and branch totals are
/// recomputed. Cobertura only has branch counts per line, so its branches are read the way Labcoat writes them to
/// lcov: <c>condition-coverage="50% (1/2)"</c> is two branches of block 0, the first taken. Function records
/// (<c>FN</c> / <c>FNDA</c>) and Cobertura methods are not kept. Each input is read in one pass (a forward-only reader
/// for Cobertura); only the per-file line and branch counts stay in memory.
/// </summary>
public sealed partial class CoverageMerge
{
    /// <summary>The package of files that only came from lcov (which has no modules).</summary>
    public const string DefaultModule = "All files";

    /// <summary>A branch's taken count that stands for "-": the line never ran.</summary>
    private const long NotExecuted = -1;

    private sealed class FileData
    {
        public string? Module { get; set; }
        public SortedDictionary<int, long> Lines { get; } = [];
        public SortedDictionary<(int Line, int Block, int Branch), long> Branches { get; } = [];
    }

    private readonly Dictionary<string, FileData> _files = new(StringComparer.Ordinal);
    private readonly List<string> _sources = [];

    /// <summary>Files added so far.</summary>
    public int Inputs { get; private set; }

    // ------------------------------------------------------------------ reading

    /// <summary>Adds an lcov file. Throws <see cref="FormatException"/> (with the line number) for a record it cannot read.</summary>
    public void AddLcov(TextReader reader)
    {
        var input = new Dictionary<string, FileData>(StringComparer.Ordinal);
        FileData? current = null;
        var number = 0;
        while (reader.ReadLine() is { } raw)
        {
            number++;
            var line = raw.Trim();
            try
            {
                if (line.StartsWith("SF:", StringComparison.Ordinal))
                {
                    var path = line[3..];
                    if (!input.TryGetValue(path, out current)) input[path] = current = new FileData();
                }
                else if (line == "end_of_record") current = null;
                else if (current is not null && line.StartsWith("DA:", StringComparison.Ordinal)) AddLcovLine(current, line[3..]);
                else if (current is not null && line.StartsWith("BRDA:", StringComparison.Ordinal)) AddLcovBranch(current, line[5..]);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException)
            {
                throw new FormatException($"line {number}: cannot read '{ExportText.Cap(line, 60)}'");
            }
        }
        Fold(input);
    }

    private static void AddLcovLine(FileData file, string da)
    {
        var parts = da.Split(',');
        var at = Int(parts[0]);
        file.Lines[at] = file.Lines.GetValueOrDefault(at) + Math.Max(0, Long(parts[1]));
    }

    private static void AddLcovBranch(FileData file, string brda)
    {
        var parts = brda.Split(',');
        var key = (Int(parts[0]), Int(parts[1]), Int(parts[2]));
        var taken = parts[3].Trim() == "-" ? NotExecuted : Math.Max(0, Long(parts[3]));
        file.Branches[key] = Sum(file.Branches.GetValueOrDefault(key, NotExecuted), taken);
    }

    /// <summary>Adds a Cobertura file. Throws <see cref="XmlException"/> for invalid XML.</summary>
    public void AddCobertura(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
        using var r = XmlReader.Create(stream, settings);
        var input = new Dictionary<string, FileData>(StringComparer.Ordinal);
        var module = "";
        FileData? current = null;
        var more = r.Read();
        while (more)
        {
            if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.LocalName == "class") current = null;
                more = r.Read();
                continue;
            }
            if (r.NodeType != XmlNodeType.Element)
            {
                more = r.Read();
                continue;
            }
            var moved = false; // Skip / ReadElementContentAs* leave the reader on the next node already
            switch (r.LocalName)
            {
                case "source":
                    var source = r.ReadElementContentAsString().Trim();
                    if (source.Length > 0 && !_sources.Contains(source)) _sources.Add(source);
                    moved = true;
                    break;
                case "package":
                    module = r.GetAttribute("name") ?? "";
                    break;
                case "class":
                    var path = r.GetAttribute("filename") ?? "";
                    if (path.Length == 0) { r.Skip(); moved = true; break; }
                    if (!input.TryGetValue(path, out current)) input[path] = current = new FileData();
                    current.Module ??= module;
                    if (r.IsEmptyElement) current = null;
                    break;
                case "methods": // the same lines again, per method
                    r.Skip();
                    moved = true;
                    break;
                case "line" when current is not null:
                    AddCoberturaLine(current, r);
                    break;
            }
            if (!moved) more = r.Read();
            else more = !r.EOF;
        }
        Fold(input);
    }

    /// <summary>One line; a file with several classes (nested types) can list a line twice: the larger count counts once.</summary>
    private static void AddCoberturaLine(FileData file, XmlReader r)
    {
        int at;
        long hits;
        try
        {
            at = Int(r.GetAttribute("number") ?? "");
            hits = Math.Max(0, Long(r.GetAttribute("hits") ?? "0"));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new FormatException($"line {LineNumber(r)}: cannot read a <line> element");
        }
        file.Lines[at] = Math.Max(file.Lines.GetValueOrDefault(at), hits);
        if (r.GetAttribute("branch") != "true" || ConditionCoverage().Match(r.GetAttribute("condition-coverage") ?? "") is not { Success: true } m) return;
        var covered = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var total = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        for (var b = 0; b < total; b++)
        {
            var taken = b < covered ? Math.Max(hits, 1) : hits > 0 ? 0 : NotExecuted;
            var key = (at, 0, b);
            file.Branches[key] = Math.Max(file.Branches.GetValueOrDefault(key, NotExecuted), taken);
        }
    }

    private static int LineNumber(XmlReader r) => r is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    [GeneratedRegex(@"\((\d{1,9})/(\d{1,9})\)")]
    private static partial Regex ConditionCoverage();

    /// <summary>Adds one input's files to the merge: counts are summed.</summary>
    private void Fold(Dictionary<string, FileData> input)
    {
        Inputs++;
        foreach (var (path, from) in input)
        {
            if (!_files.TryGetValue(path, out var into)) _files[path] = into = new FileData();
            into.Module ??= from.Module;
            foreach (var (line, hits) in from.Lines) into.Lines[line] = into.Lines.GetValueOrDefault(line) + hits;
            foreach (var (key, taken) in from.Branches) into.Branches[key] = Sum(into.Branches.GetValueOrDefault(key, NotExecuted), taken);
        }
    }

    /// <summary>Taken counts add up; "-" (never executed) only stays when both are.</summary>
    private static long Sum(long a, long b) => a == NotExecuted && b == NotExecuted ? NotExecuted : Math.Max(0, a) + Math.Max(0, b);

    private static int Int(string s) => int.Parse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);

    private static long Long(string s) => long.Parse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ totals

    public CoverageTotals Totals()
    {
        int lines = 0, linesHit = 0, branches = 0, branchesHit = 0;
        foreach (var f in _files.Values)
        {
            lines += f.Lines.Count;
            linesHit += f.Lines.Values.Count(h => h > 0);
            branches += f.Branches.Count;
            branchesHit += f.Branches.Values.Count(t => t > 0);
        }
        return new CoverageTotals(_files.Count, linesHit, lines, branchesHit, branches);
    }

    private IEnumerable<KeyValuePair<string, FileData>> Ordered() => _files.OrderBy(f => f.Key, StringComparer.Ordinal);

    // ------------------------------------------------------------------ lcov

    /// <summary>Writes the merged coverage as lcov, the way <see cref="CoverageExport.WriteLcov"/> does (BRDA lines keep their block and branch numbers).</summary>
    public void WriteLcov(Stream stream)
    {
        using var o = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
        foreach (var (path, f) in Ordered())
        {
            o.Write("TN:\n");
            o.Write($"SF:{path.Replace('\n', ' ').Replace('\r', ' ')}\n");
            foreach (var ((line, block, branch), taken) in f.Branches)
                o.Write($"BRDA:{Fmt(line)},{Fmt(block)},{Fmt(branch)},{(taken == NotExecuted ? "-" : Fmt(taken))}\n");
            o.Write($"BRF:{Fmt(f.Branches.Count)}\nBRH:{Fmt(f.Branches.Values.Count(t => t > 0))}\n");
            foreach (var (line, hits) in f.Lines) o.Write($"DA:{Fmt(line)},{Fmt(hits)}\n");
            o.Write($"LF:{Fmt(f.Lines.Count)}\nLH:{Fmt(f.Lines.Values.Count(h => h > 0))}\n");
            o.Write("end_of_record\n");
        }
    }

    // ------------------------------------------------------------------ Cobertura

    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Indent = true,
        Encoding = new UTF8Encoding(false),
        NewLineChars = "\n",
    };

    /// <summary>Writes the merged coverage as Cobertura XML (one package per module, one class per file).</summary>
    public void WriteCobertura(Stream stream)
    {
        var totals = Totals();
        using var w = XmlWriter.Create(stream, XmlSettings);
        w.WriteStartDocument();
        w.WriteComment($" Labcoat {Cli.Version}: merged from {Inputs} coverage file(s) ");
        w.WriteStartElement("coverage");
        w.WriteAttributeString("line-rate", CoverageExport.Rate(totals.LinesCovered, totals.LinesValid));
        w.WriteAttributeString("branch-rate", CoverageExport.Rate(totals.BranchesCovered, totals.BranchesValid));
        w.WriteAttributeString("lines-covered", Fmt(totals.LinesCovered));
        w.WriteAttributeString("lines-valid", Fmt(totals.LinesValid));
        w.WriteAttributeString("branches-covered", Fmt(totals.BranchesCovered));
        w.WriteAttributeString("branches-valid", Fmt(totals.BranchesValid));
        w.WriteAttributeString("complexity", "0");
        w.WriteAttributeString("version", "Labcoat " + Cli.Version);
        w.WriteAttributeString("timestamp", Fmt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        w.WriteStartElement("sources");
        foreach (var source in _sources) w.WriteElementString("source", ExportText.Clean(source));
        w.WriteEndElement();
        w.WriteStartElement("packages");
        foreach (var module in Ordered().GroupBy(f => f.Value.Module is { Length: > 0 } m ? m : DefaultModule, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            WritePackage(w, module.Key, module.ToList());
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WritePackage(XmlWriter w, string module, List<KeyValuePair<string, FileData>> files)
    {
        w.WriteStartElement("package");
        w.WriteAttributeString("name", ExportText.Clean(module));
        w.WriteAttributeString("line-rate", CoverageExport.Rate(files.Sum(f => f.Value.Lines.Values.Count(h => h > 0)), files.Sum(f => f.Value.Lines.Count)));
        w.WriteAttributeString("branch-rate", CoverageExport.Rate(files.Sum(f => f.Value.Branches.Values.Count(t => t > 0)), files.Sum(f => f.Value.Branches.Count)));
        w.WriteAttributeString("complexity", "0");
        w.WriteStartElement("classes");
        foreach (var (rawPath, f) in files)
        {
            var path = ExportText.Clean(rawPath);
            w.WriteStartElement("class");
            w.WriteAttributeString("name", CoverageExport.ClassName(path));
            w.WriteAttributeString("filename", path);
            w.WriteAttributeString("line-rate", CoverageExport.Rate(f.Lines.Values.Count(h => h > 0), f.Lines.Count));
            w.WriteAttributeString("branch-rate", CoverageExport.Rate(f.Branches.Values.Count(t => t > 0), f.Branches.Count));
            w.WriteAttributeString("complexity", "0");
            w.WriteElementString("methods", "");
            w.WriteStartElement("lines");
            var byLine = f.Branches.ToLookup(b => b.Key.Line, b => b.Value);
            foreach (var (line, hits) in f.Lines)
            {
                var branches = byLine[line].ToList();
                w.WriteStartElement("line");
                w.WriteAttributeString("number", Fmt(line));
                w.WriteAttributeString("hits", Fmt(hits));
                w.WriteAttributeString("branch", branches.Count > 0 ? "true" : "false");
                if (branches.Count > 0)
                {
                    var covered = branches.Count(t => t > 0);
                    w.WriteAttributeString("condition-coverage", $"{Fmt((int)Math.Round(100.0 * covered / branches.Count))}% ({Fmt(covered)}/{Fmt(branches.Count)})");
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static string Fmt(long n) => n.ToString(CultureInfo.InvariantCulture);
}
