using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Labcoat;

/// <summary>
/// Parses Cobertura XML (as written by Microsoft.Testing.Extensions.CodeCoverage with
/// <c>--coverage-output-format cobertura</c>) and merges one or more reports into a <see cref="CoverageReport"/>.
///
/// Merge rules: per source file, class-level <c>&lt;lines&gt;</c> are used (method-level lines are duplicates).
/// The same line appearing in several classes of one report (partial classes, compiler-generated state machines)
/// takes the max hits; the same line across reports (several projects in one run) sums the hits.
/// Branch counts take the max covered/total.
/// </summary>
public sealed partial class CoverageAccumulator
{
    private readonly Dictionary<string, (string Module, Dictionary<int, CoverageLineHit> Lines)> _files =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    [GeneratedRegex(@"\((\d+)\s*/\s*(\d+)\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ConditionRe();

    public int FileCount => _files.Count;

    /// <param name="excludedModules">Assembly names to drop (the test assemblies themselves).</param>
    public void AddCobertura(string xmlPath, ISet<string> excludedModules) => AddCobertura(xmlPath, (module, _) => !excludedModules.Contains(module));

    /// <param name="filter">Which modules / files the report keeps (CoverageSettings.cs).</param>
    public void AddCobertura(string xmlPath, CoverageFilter filter) => AddCobertura(xmlPath, filter.Keeps);

    private void AddCobertura(string xmlPath, Func<string, string, bool> keep)
    {
        var doc = XDocument.Load(xmlPath);
        var sources = doc.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();

        foreach (var package in doc.Descendants("package"))
        {
            var module = (string?)package.Attribute("name") ?? "";
            // Merge into the accumulator (sum across reports).
            foreach (var (path, lines) in PackageLines(package, module, sources, keep)) SumLines(path, module, lines, copy: false);
        }
    }

    /// <summary>Lines of one package of a report, per file (max within the report).</summary>
    private static Dictionary<string, Dictionary<int, CoverageLineHit>> PackageLines(XElement package, string module, List<string> sources,
        Func<string, string, bool> keep)
    {
        var reportLines = new Dictionary<string, Dictionary<int, CoverageLineHit>>();
        foreach (var cls in package.Descendants("class"))
        {
            var fileName = (string?)cls.Attribute("filename");
            if (string.IsNullOrEmpty(fileName)) continue;
            var path = ResolvePath(fileName, sources);
            if (!keep(module, path)) continue;
            if (!reportLines.TryGetValue(path, out var lines)) reportLines[path] = lines = [];
            AddClassLines(cls, lines);
        }
        return reportLines;
    }

    private static void AddClassLines(XElement cls, Dictionary<int, CoverageLineHit> lines)
    {
        var lineElements = cls.Elements("lines").Elements("line");
        if (!lineElements.Any()) lineElements = cls.Descendants("line"); // no class-level lines: fall back to method lines
        foreach (var l in lineElements)
        {
            if (ParseLine(l) is not { } hit) continue;
            if (lines.TryGetValue(hit.Line, out var existing))
            {
                existing.Hits = Math.Max(existing.Hits, hit.Hits);
                MergeBranches(existing, hit.BranchesCovered, hit.BranchesTotal);
            }
            else lines[hit.Line] = hit;
        }
    }

    /// <summary>A Cobertura <c>&lt;line&gt;</c>; null without a valid line number.</summary>
    private static CoverageLineHit? ParseLine(XElement l)
    {
        if (!int.TryParse((string?)l.Attribute("number"), out var number)) return null;
        long.TryParse((string?)l.Attribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hitsLong);
        var hits = (int)Math.Min(hitsLong, int.MaxValue);
        var (covered, total) = Branches(l);
        return new CoverageLineHit { Line = number, Hits = hits, BranchesCovered = covered, BranchesTotal = total };
    }

    /// <summary>The branch counts of a line with <c>branch="true"</c>: <c>condition-coverage="50% (1/2)"</c>.</summary>
    private static (int? Covered, int? Total) Branches(XElement l)
    {
        if (!string.Equals((string?)l.Attribute("branch"), "true", StringComparison.OrdinalIgnoreCase)) return (null, null);
        var m = ConditionRe().MatchOrEmpty((string?)l.Attribute("condition-coverage") ?? "");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (null, null);
    }

    /// <summary>Adds lines of another report with the across-reports rule (hits summed).</summary>
    /// <param name="copy">Copy the added lines (they are mutable) instead of taking them over.</param>
    private void SumLines(string path, string module, Dictionary<int, CoverageLineHit> lines, bool copy)
    {
        if (!_files.TryGetValue(path, out var entry)) _files[path] = entry = (module, []);
        foreach (var (number, hit) in lines)
        {
            if (entry.Lines.TryGetValue(number, out var existing))
            {
                existing.Hits = (int)Math.Min((long)existing.Hits + hit.Hits, int.MaxValue);
                MergeBranches(existing, hit.BranchesCovered, hit.BranchesTotal);
            }
            else entry.Lines[number] = copy ? hit with { } : hit;
        }
    }

    /// <summary>
    /// Adds another accumulator's lines with the across-reports rule (hits summed), as if its Cobertura files had been
    /// added here: the reports of several projects of one run. Lines are copied (they are mutable).
    /// </summary>
    public void Add(CoverageAccumulator other)
    {
        foreach (var (path, (module, lines)) in other._files) SumLines(path, module, lines, copy: true);
    }

    /// <summary>A deep copy.</summary>
    public CoverageAccumulator Clone()
    {
        var copy = new CoverageAccumulator();
        copy.Add(this);
        return copy;
    }

    /// <summary>Every file with the module it belongs to.</summary>
    internal IEnumerable<(string Path, string Module)> Files => _files.Select(kv => (kv.Key, kv.Value.Module));

    internal bool HasFile(string path) => _files.ContainsKey(path);

    /// <summary>Every file with its module and lines (live objects: callers copy or read them under their own lock).</summary>
    internal IEnumerable<(string Path, string Module, IReadOnlyCollection<CoverageLineHit> Lines)> FileLines =>
        _files.Select(kv => (kv.Key, kv.Value.Module, (IReadOnlyCollection<CoverageLineHit>)kv.Value.Lines.Values));

    /// <summary>Adds one file's lines as read back from the persisted full report (FullCoverageStore.cs); replaces the file.</summary>
    internal void SetFile(string path, string module, IEnumerable<CoverageLineHit> lines) =>
        _files[path] = (module, lines.ToDictionary(l => l.Line));

    /// <summary>Covered lines of one file; -1 when the file is not in the report.</summary>
    internal int CoveredLines(string path) => _files.TryGetValue(path, out var e) ? e.Lines.Values.Count(l => l.Hits > 0) : -1;

    /// <summary>
    /// Union of one file of <paramref name="other"/> into this one (a line is covered when either covers it): hits and
    /// branch counts take the max, so lines that both runs executed are not counted twice.
    /// </summary>
    internal void UnionFile(CoverageAccumulator other, string path)
    {
        if (!other._files.TryGetValue(path, out var source)) return;
        if (!_files.TryGetValue(path, out var entry)) _files[path] = entry = (source.Module, []);
        foreach (var (number, hit) in source.Lines)
        {
            if (entry.Lines.TryGetValue(number, out var existing))
            {
                existing.Hits = Math.Max(existing.Hits, hit.Hits);
                MergeBranches(existing, hit.BranchesCovered, hit.BranchesTotal);
            }
            else entry.Lines[number] = hit with { };
        }
    }

    private static void MergeBranches(CoverageLineHit target, int? covered, int? total)
    {
        if (covered is null || total is null) return;
        target.BranchesCovered = Math.Max(target.BranchesCovered ?? 0, covered.Value);
        target.BranchesTotal = Math.Max(target.BranchesTotal ?? 0, total.Value);
    }

    internal static string ResolvePath(string fileName, List<string> sources)
    {
        if (Path.IsPathRooted(fileName)) return Path.GetFullPath(fileName);
        foreach (var s in sources)
        {
            var candidate = Path.GetFullPath(Path.Combine(s, fileName));
            if (File.Exists(candidate)) return candidate;
        }
        return sources.Count > 0 ? Path.GetFullPath(Path.Combine(sources[0], fileName)) : fileName;
    }

    public CoverageReport Build(string runId, IEnumerable<string> projectIds, IReadOnlyCollection<string>? testIds = null)
    {
        var files = new List<FileCoverage>();
        foreach (var (path, (module, lines)) in _files)
        {
            var list = lines.Values.OrderBy(l => l.Line).ToList();
            var linesValid = list.Count;
            var linesCovered = list.Count(l => l.Hits > 0);
            var branchesValid = list.Sum(l => l.BranchesTotal ?? 0);
            var branchesCovered = list.Sum(l => l.BranchesCovered ?? 0);
            files.Add(new FileCoverage
            {
                Path = path,
                Module = module,
                Lines = list,
                LinesValid = linesValid,
                LinesCovered = linesCovered,
                LineRate = Rate(linesCovered, linesValid),
                BranchesValid = branchesValid,
                BranchesCovered = branchesCovered,
                BranchRate = Rate(branchesCovered, branchesValid),
            });
        }

        var root = CommonRoot(files.Select(f => f.Path).ToList());
        foreach (var f in files)
            f.RelativePath = root.Length > 0 ? Path.GetRelativePath(root, f.Path) : f.Path;
        files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));

        int lc = files.Sum(f => f.LinesCovered), lv = files.Sum(f => f.LinesValid);
        int bc = files.Sum(f => f.BranchesCovered), bv = files.Sum(f => f.BranchesValid);
        return new CoverageReport
        {
            RunId = runId,
            GeneratedAt = Json.Now(),
            ProjectIds = projectIds.ToList(),
            Files = files,
            LinesCovered = lc,
            LinesValid = lv,
            LineRate = lv == 0 ? 0 : Rate(lc, lv),
            NoCoverableLines = lv == 0 ? true : null,
            BranchesCovered = bc,
            BranchesValid = bv,
            BranchRate = Rate(bc, bv),
            Scope = testIds is { Count: > 0 } ? "subset" : "all",
            TestIds = testIds is { Count: > 0 } ? testIds.ToList() : null,
        };
    }

    /// <summary>Rate in 0..1, rounded to 4 decimals; 1 when there is nothing to cover (Cobertura convention).</summary>
    private static double Rate(int covered, int valid) => valid == 0 ? 1 : Math.Round((double)covered / valid, 4);

    private static string CommonRoot(List<string> paths)
    {
        if (paths.Count == 0) return "";
        var sep = Path.DirectorySeparatorChar;
        var first = Path.GetDirectoryName(paths[0]) ?? "";
        var parts = first.Split(sep);
        var len = parts.Length;
        foreach (var p in paths.Skip(1))
        {
            var other = (Path.GetDirectoryName(p) ?? "").Split(sep);
            var i = 0;
            while (i < len && i < other.Length && string.Equals(parts[i], other[i], StringComparison.Ordinal)) i++;
            len = i;
        }
        var root = string.Join(sep, parts.Take(len));
        return root.Length == 0 && paths[0].StartsWith(sep) ? sep.ToString() : root;
    }
}
