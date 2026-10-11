using System.Xml.Linq;

namespace Labcoat;

/// <summary>A covered method's line range in a source file (1-based, inclusive).</summary>
public readonly record struct LineSpan(int Start, int End)
{
    /// <summary>The method's name (Cobertura <c>method/@name</c>), when known. Not part of equality.</summary>
    public string? Name { get; init; }

    public bool Equals(LineSpan other) => Start == other.Start && End == other.End;
    public override int GetHashCode() => HashCode.Combine(Start, End);
}

/// <summary>
/// Test impact map for smart watch and "run affected": which tests execute which methods of which source file.
///
/// Filled lazily from coverage runs that had explicit test ids (per-test coverage, CoverageReport scope "subset") and
/// by the "build impact map" pass. MTP's Cobertura output has no per-test attribution (Microsoft.Testing.Extensions.
/// CodeCoverage offers only --coverage, --coverage-output, --coverage-output-format and --coverage-settings; its
/// collector's per-test / snapshot-with-reset machinery is internal and not reachable from an MTP test app), so a
/// coverage process that ran several tests credits everything it covered to all of them: an over-approximation, the
/// safe direction for "which tests to re-run". Every test therefore carries its granularity: <see cref="IsPerTest"/>
/// = recorded from a coverage process that ran that test alone (exact); otherwise class-level (shared with the other
/// tests of that process, typically its test class). Per-test data is never downgraded by a later shared recording
/// (<see cref="Record"/>), except by an explicit rebuild.
///
/// The pass runs one coverage process per test class ("class"), per test ("test"), or per class first and then per
/// test for the classes a change maps to ("auto", the default; Explorer.Warm.cs "impact map").
///
/// Granularity of the data is the method: for every test we keep the line spans of the methods it executed, plus a
/// snapshot of each source file's text. When a file changes, the changed line range (common prefix/suffix diff against
/// the snapshot) selects the tests whose executed methods overlap it. A change outside every executed method (new
/// method, field, attribute, const...) falls back to all tests that touch the file.
/// In memory only (nothing is persisted, so there is no stored format to migrate).
/// Not thread safe: callers lock (Explorer.Sync).
/// </summary>
public sealed class ImpactMap
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // (projectId, testId) -> canonical source file -> spans of the methods that test executed.
    private readonly Dictionary<(string ProjectId, string TestId), Dictionary<string, List<LineSpan>>> _byTest = [];
    // The keys of _byTest recorded from a coverage process that ran that test alone.
    private readonly HashSet<(string ProjectId, string TestId)> _perTest = [];
    // canonical source file -> its lines when coverage was recorded (or the last change was accepted).
    private readonly Dictionary<string, string[]> _snapshots = new(PathComparer);

    public DateTime? BuiltAt { get; set; }
    public bool Building { get; set; }
    /// <summary>The pass granularity option ("class" | "test" | "auto"), reported in <see cref="Info"/>.</summary>
    public string Mode { get; set; } = ImpactMapModes.Default;

    /// <summary>
    /// Replaces what <paramref name="testIds"/> cover with <paramref name="coverage"/> (canonical file -> spans). A single
    /// test id = per-test data. Shared data (several ids) does not replace a test's per-test data unless
    /// <paramref name="replacePerTest"/> (an explicit rebuild).
    /// </summary>
    public void Record(string projectId, IEnumerable<string> testIds, Dictionary<string, List<LineSpan>> coverage, bool replacePerTest = false)
    {
        var ids = testIds.Distinct().ToList();
        var perTest = ids.Count == 1;
        foreach (var file in coverage.Keys)
            if (TryReadLines(file) is { } lines) _snapshots[file] = lines;
        foreach (var id in ids)
        {
            var key = (projectId, id);
            if (!perTest && !replacePerTest && _perTest.Contains(key)) continue;
            _byTest[key] = coverage.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), PathComparer);
            if (perTest) _perTest.Add(key);
            else _perTest.Remove(key);
        }
    }

    /// <summary>True when the test's data comes from a coverage process that ran it alone (exact); false = class-level (shared) or unknown.</summary>
    public bool IsPerTest(string projectId, string testId) => _perTest.Contains((projectId, testId));

    /// <summary>True when the map has data for the test (any granularity).</summary>
    public bool Has(string projectId, string testId) => _byTest.ContainsKey((projectId, testId));

    /// <summary>"per-test data" / "class-level data": the granularity note of reasons and logs.</summary>
    public string DataNote(string projectId, string testId) => IsPerTest(projectId, testId) ? "per-test data" : "class-level data";

    /// <summary>
    /// Tests of <paramref name="projectId"/> affected by a change of <paramref name="file"/> (canonical path).
    /// Returns null when the map knows no test that touches the file; an empty set when the content did not change.
    /// <paramref name="detail"/> describes the selection for the log.
    /// </summary>
    public HashSet<string>? Affected(string projectId, string file, out string detail)
    {
        detail = "";
        HashSet<string>? touching = null;
        foreach (var ((pid, tid), files) in _byTest)
            if (pid == projectId && files.ContainsKey(file)) (touching ??= []).Add(tid);
        if (touching is null) return null;

        if (!_snapshots.TryGetValue(file, out var old) || TryReadLines(file) is not { } now)
        {
            detail = "file";
            return touching;
        }
        var (start, oldEnd, _) = Diff(old, now);
        if (start > oldEnd && old.Length == now.Length)
        {
            detail = "content unchanged";
            return [];
        }
        // Pure insertion between old lines start-1 and start: treat both neighbours as touched.
        int lo = Math.Min(start, oldEnd), hi = Math.Max(start, oldEnd);
        var hits = new HashSet<string>();
        foreach (var ((pid, tid), files) in _byTest)
        {
            if (pid != projectId || !files.TryGetValue(file, out var spans)) continue;
            if (spans.Any(s => s.Start <= hi && s.End >= lo)) hits.Add(tid);
        }
        if (hits.Count == 0)
        {
            detail = $"lines {lo}–{hi} are outside executed methods, using every test that touches the file";
            return touching;
        }
        detail = (lo == hi ? $"line {lo}" : $"lines {lo}–{hi}") + Granularity(projectId, hits);
        return hits;
    }

    /// <summary>After a change was handled: shift spans below the edit and take the new text as the snapshot.</summary>
    public void AcceptChange(string file)
    {
        if (!_snapshots.TryGetValue(file, out var old) || TryReadLines(file) is not { } now) return;
        var (start, oldEnd, delta) = Diff(old, now);
        if (delta != 0)
        {
            foreach (var files in _byTest.Values)
                if (files.TryGetValue(file, out var spans)) ShiftSpans(spans, start, oldEnd, delta);
        }
        _snapshots[file] = now;
    }

    /// <summary>Spans below the edit move by <paramref name="delta"/> lines, spans reaching into it grow or shrink with it.</summary>
    private static void ShiftSpans(List<LineSpan> spans, int start, int oldEnd, int delta)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            if (s.Start > oldEnd) spans[i] = s with { Start = s.Start + delta, End = s.End + delta };
            else if (s.End >= start) spans[i] = s with { End = Math.Max(s.Start, s.End + delta) };
        }
    }

    /// <summary>Tests whose executed methods (spans) contain <paramref name="line"/> of <paramref name="file"/> (canonical path).</summary>
    public IEnumerable<(string ProjectId, string TestId)> TestsCoveringLine(string file, int line)
    {
        foreach (var (key, files) in _byTest)
            if (files.TryGetValue(file, out var spans) && spans.Any(s => s.Start <= line && s.End >= line))
                yield return key;
    }

    /// <summary>
    /// Run affected (git): tests of <paramref name="projectId"/> whose executed methods overlap <paramref name="changed"/>
    /// (line ranges of the current file, e.g. from <c>git diff -U0</c>), each with the names of the overlapped methods.
    /// <paramref name="changed"/> null (added / deleted / binary file) or a change outside every executed method → every
    /// test that touches the file, with no method names (<paramref name="outsideMethods"/> true for the latter).
    /// Null when no test of the project touches the file.
    /// </summary>
    public Dictionary<string, List<string>>? AffectedByLines(string projectId, string file, IReadOnlyList<LineSpan>? changed, out bool outsideMethods)
    {
        outsideMethods = false;
        Dictionary<string, List<string>>? touching = null;
        Dictionary<string, List<string>>? hits = null;
        foreach (var ((pid, tid), files) in _byTest)
        {
            if (pid != projectId || !files.TryGetValue(file, out var spans)) continue;
            (touching ??= [])[tid] = [];
            if (changed is not null && OverlappedMethods(spans, changed) is { } names) (hits ??= [])[tid] = names;
        }
        if (touching is null || changed is null) return touching;
        if (hits is not null) return hits;
        outsideMethods = true;
        return touching;
    }

    /// <summary>The names of the methods (spans) that overlap <paramref name="changed"/>; null when none does.</summary>
    private static List<string>? OverlappedMethods(List<LineSpan> spans, IReadOnlyList<LineSpan> changed)
    {
        List<string>? names = null;
        foreach (var s in spans.Where(s => changed.Any(c => s.Start <= c.End && s.End >= c.Start)))
        {
            var name = s.Name is { Length: > 0 } n ? n : $"lines {s.Start}–{s.End}";
            if (!(names ??= []).Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>" (per-test data)", " (class-level data)" or " (per-test and class-level data)" for a set of tests.</summary>
    private string Granularity(string projectId, IEnumerable<string> testIds)
    {
        bool any = false, all = true;
        foreach (var id in testIds)
        {
            if (IsPerTest(projectId, id)) any = true;
            else all = false;
        }
        if (!any) return " (class-level data)";
        return all ? " (per-test data)" : " (per-test and class-level data)";
    }

    public void ForgetTests(string projectId, ISet<string> existingIds)
    {
        foreach (var key in _byTest.Keys.Where(k => k.ProjectId == projectId && !existingIds.Contains(k.TestId)).ToList())
        {
            _byTest.Remove(key);
            _perTest.Remove(key);
        }
    }

    public ImpactMapInfo? Info()
    {
        if (_byTest.Count == 0 && !Building && BuiltAt is null) return null;
        var files = new HashSet<string>(PathComparer);
        foreach (var f in _byTest.Values) files.UnionWith(f.Keys);
        return new ImpactMapInfo
        {
            Files = files.Count,
            Tests = _byTest.Count,
            PerTest = _perTest.Count,
            Mode = Mode,
            BuiltAt = BuiltAt?.ToString("o"),
            Building = Building ? true : null,
        };
    }

    /// <summary>
    /// Covered method spans per canonical source file from a Cobertura report (modules in
    /// <paramref name="excludedModules"/>, i.e. the test assemblies, are skipped).
    /// </summary>
    public static Dictionary<string, List<LineSpan>> ParseCobertura(string xmlPath, ISet<string> excludedModules)
    {
        var doc = XDocument.Load(xmlPath);
        var sources = doc.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();
        var result = new Dictionary<string, List<LineSpan>>(PathComparer);
        foreach (var package in doc.Descendants("package"))
        {
            if (excludedModules.Contains((string?)package.Attribute("name") ?? "")) continue;
            foreach (var cls in package.Descendants("class"))
            {
                var fileName = (string?)cls.Attribute("filename");
                if (string.IsNullOrEmpty(fileName)) continue;
                var spans = CoveredSpans(cls);
                if (spans.Count == 0) continue;
                var path = PathUtil.CanonicalizeOrSelf(CoverageAccumulator.ResolvePath(fileName, sources));
                if (!result.TryGetValue(path, out var list)) result[path] = list = [];
                list.AddRange(spans);
            }
        }
        return result;
    }

    /// <summary>The covered methods of a Cobertura class (each line without method data, when it has none).</summary>
    private static List<LineSpan> CoveredSpans(XElement cls)
    {
        var spans = new List<LineSpan>();
        var methods = cls.Element("methods")?.Elements("method").ToList() ?? [];
        foreach (var m in methods)
        {
            var lines = Lines(m).ToList();
            if (lines.Any(l => l.Hits > 0))
                spans.Add(new LineSpan(lines.Min(l => l.Number), lines.Max(l => l.Number)) { Name = MethodName((string?)cls.Attribute("name"), (string?)m.Attribute("name")) });
        }
        if (methods.Count == 0)
            spans.AddRange(Lines(cls).Where(l => l.Hits > 0).Select(l => new LineSpan(l.Number, l.Number)));
        return spans;

        static IEnumerable<(int Number, long Hits)> Lines(XElement e) =>
            e.Descendants("line").Select(l => (
                int.TryParse((string?)l.Attribute("number"), out var n) ? n : 0,
                long.TryParse((string?)l.Attribute("hits"), out var h) ? h : 0)).Where(l => l.Item1 > 0);
    }

    /// <summary>
    /// Readable method name from a Cobertura class/method pair: async / iterator state machines
    /// (<c>Orders/&lt;ValidateAsync&gt;d__3</c> + <c>MoveNext</c>) and lambdas (<c>&lt;Validate&gt;b__0</c>) map to the
    /// declaring method, constructors to the class name.
    /// </summary>
    internal static string? MethodName(string? className, string? method)
    {
        if (string.IsNullOrEmpty(method)) return null;
        static string? Inner(string s)
        {
            var lt = s.IndexOf('<');
            var gt = lt < 0 ? -1 : s.IndexOf('>', lt + 1);
            return gt > lt + 1 ? s[(lt + 1)..gt] : null;
        }
        var cls = className ?? "";
        var nested = cls.LastIndexOf('/');
        if (nested >= 0 && Inner(cls[(nested + 1)..]) is { } declaring && method is "MoveNext" or "SetStateMachine") return declaring;
        if (method.StartsWith('<') && Inner(method) is { } outer) return outer;
        if (method is ".ctor" or ".cctor")
        {
            var simple = (nested >= 0 ? cls[..nested] : cls).Split('.').LastOrDefault();
            return string.IsNullOrEmpty(simple) ? method : simple;
        }
        return method;
    }

    /// <summary>Common prefix/suffix diff: old lines [Start..OldEnd] (1-based; OldEnd = Start-1 for a pure insertion) were replaced; Delta = line count change.</summary>
    private static (int Start, int OldEnd, int Delta) Diff(string[] old, string[] now)
    {
        var prefix = 0;
        while (prefix < old.Length && prefix < now.Length && old[prefix] == now[prefix]) prefix++;
        var suffix = 0;
        while (suffix < old.Length - prefix && suffix < now.Length - prefix && old[^(suffix + 1)] == now[^(suffix + 1)]) suffix++;
        return (prefix + 1, old.Length - suffix, now.Length - old.Length);
    }

    private static string[]? TryReadLines(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 2 * 1024 * 1024) return null;
            return File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Values of the impactMap option (settings file "impactMap", --impact-map, LABCOAT_IMPACT_MAP, Aspire ImpactMap).</summary>
public static class ImpactMapModes
{
    /// <summary>One coverage process per test class: fastest pass, every test of a class shares the class's data.</summary>
    public const string Class = "class";
    /// <summary>One coverage process per test (several at once with dynamic instrumentation): exact, slowest pass.</summary>
    public const string Test = "test";
    /// <summary>Class pass, then the classes a change maps to are refined per test in the background (cached).</summary>
    public const string Auto = "auto";
    public const string Default = Auto;
    public static readonly string[] All = [Class, Test, Auto];

    /// <summary>The normalized mode, or null when <paramref name="value"/> is not one.</summary>
    public static string? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() is { } v && All.Contains(v) ? v : null;
}
