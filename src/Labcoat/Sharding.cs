using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Labcoat;

/// <summary>What <c>--shard</c> splits on: <c>class</c> (default; fixtures start once per class), <c>test</c> or <c>project</c>.</summary>
public enum ShardBy { Class, Test, Project }

/// <summary><c>--shard 2/4</c>: this process runs shard <see cref="Index"/> (1-based) of <see cref="Count"/>.</summary>
public sealed record ShardSpec(int Index, int Count)
{
    /// <summary>Parses "2/4"; null unless 1 &lt;= index &lt;= count.</summary>
    public static ShardSpec? Parse(string? text)
    {
        var parts = text?.Split('/');
        if (parts is not [var i, var n]) return null;
        if (!int.TryParse(i, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return null;
        if (!int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) return null;
        return index >= 1 && index <= count ? new ShardSpec(index, count) : null;
    }

    public override string ToString() => $"{Index}/{Count}";
}

/// <summary>The tests of one shard, and what to print about the split.</summary>
/// <param name="TestIds">The tests this shard runs, in the order they were given.</param>
/// <param name="Units">How many classes (or tests, projects) this shard runs.</param>
/// <param name="TotalUnits">How many there are in all shards.</param>
/// <param name="EstimatedMs">The expected duration of this shard; null without timings.</param>
/// <param name="Fingerprint">Identifies the whole split: every shard of one run must print the same one.</param>
public sealed record ShardPlan(List<string> TestIds, int Units, int TotalUnits, double? EstimatedMs, string Fingerprint);

/// <summary>
/// <c>labcoat --run --shard 2/4</c>: splits the tests of a run over parallel CI jobs. Every shard discovers all tests
/// and computes the same split on its own, so the split may only depend on what all shards share: the test list and the
/// <c>--shard-timings</c> files. Labcoat's own history is per machine and is deliberately not used.
/// <list type="bullet">
/// <item>Without timings the units (sorted by key) are dealt round-robin.</item>
/// <item>With timings the longest unit goes first to the shard with the least work so far; a unit without a timing
/// counts as the median one.</item>
/// </list>
/// </summary>
public static class Sharding
{
    /// <summary>The split of <paramref name="tests"/>; <paramref name="projectName"/> names a test's project (the JUnit suite name).</summary>
    public static ShardPlan Plan(IReadOnlyList<TestNode> tests, Func<string, string> projectName, ShardSpec shard, ShardBy by, ShardTimings? timings)
    {
        var units = tests.GroupBy(t => UnitKey(t, projectName, by), StringComparer.Ordinal)
            .Select(g => new Unit(g.Key, g.ToList()))
            .OrderBy(u => u.Key, StringComparer.Ordinal)
            .ToList();

        var assigned = new int[units.Count];
        double[]? weights = null;
        if (timings is { IsEmpty: false })
        {
            weights = Weights(units, projectName, by, timings);
            var load = new double[shard.Count];
            foreach (var i in Enumerable.Range(0, units.Count).OrderByDescending(i => weights[i]).ThenBy(i => i)) // i is in key order
            {
                var lightest = 0;
                for (var s = 1; s < load.Length; s++) if (load[s] < load[lightest]) lightest = s;
                assigned[i] = lightest;
                load[lightest] += weights[i];
            }
        }
        else
        {
            for (var i = 0; i < units.Count; i++) assigned[i] = i % shard.Count;
        }

        var mine = shard.Index - 1;
        var mineIds = new HashSet<string>(units.Where((_, i) => assigned[i] == mine).SelectMany(u => u.Tests).Select(t => t.Id), StringComparer.Ordinal);
        var ids = tests.Where(t => mineIds.Contains(t.Id)).Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList();
        double? estimate = weights is null ? null : Enumerable.Range(0, units.Count).Where(i => assigned[i] == mine).Sum(i => weights[i]);
        return new ShardPlan(ids, assigned.Count(a => a == mine), units.Count, estimate, Fingerprint(units, assigned));
    }

    private sealed record Unit(string Key, List<TestNode> Tests);

    private static string UnitKey(TestNode t, Func<string, string> projectName, ShardBy by) => by switch
    {
        ShardBy.Project => projectName(t.ProjectId),
        ShardBy.Test => $"{ClassKey(t, projectName)}|{t.DisplayName}|{t.Id}",
        _ => ClassKey(t, projectName),
    };

    /// <summary>"Project|Namespace.Class" like the JUnit suite name and classname (the project name when the test has no class).</summary>
    internal static string ClassKey(TestNode t, Func<string, string> projectName)
    {
        var project = projectName(t.ProjectId);
        var cls = string.Join('.', new[] { t.Namespace, t.ClassName }.Where(s => !string.IsNullOrEmpty(s)));
        return $"{project}|{(cls.Length > 0 ? cls : project)}";
    }

    private static double[] Weights(List<Unit> units, Func<string, string> projectName, ShardBy by, ShardTimings timings)
    {
        var known = units.SelectMany(u => u.Tests).Select(t => ClassKey(t, projectName)).Distinct(StringComparer.Ordinal)
            .Select(timings.Class).Where(ms => ms is not null).Select(ms => ms!.Value).Order().ToList();
        var fallback = known.Count == 0 ? 1 : known[known.Count / 2]; // the median class
        double ClassWeight(string classKey) => timings.Class(classKey) ?? fallback;
        var perClass = by == ShardBy.Test
            ? units.GroupBy(u => ClassKey(u.Tests[0], projectName), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
            : [];

        return units.Select(u => by switch
        {
            ShardBy.Test => TestWeight(u.Tests[0]),
            _ => u.Tests.Select(t => ClassKey(t, projectName)).Distinct(StringComparer.Ordinal).Sum(ClassWeight),
        }).ToArray();

        double TestWeight(TestNode t)
        {
            var classKey = ClassKey(t, projectName);
            if (timings.Test(classKey, t.DisplayName) is { } ms) return ms;
            return ClassWeight(classKey) / Math.Max(1, perClass.GetValueOrDefault(classKey));
        }
    }

    private static string Fingerprint(List<Unit> units, int[] assigned)
    {
        var text = new StringBuilder();
        for (var i = 0; i < units.Count; i++) text.Append(units[i].Key).Append('\t').Append(assigned[i]).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..6].ToLowerInvariant();
    }
}

/// <summary>Test durations read from JUnit XML (the <c>--export junit</c> files of an earlier run), by project and class.</summary>
public sealed class ShardTimings
{
    private readonly Dictionary<string, double> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _tests = new(StringComparer.Ordinal);

    public bool IsEmpty => _classes.Count == 0;

    /// <summary>Milliseconds of a class ("Project|Namespace.Class"), summed over its tests; null when unknown.</summary>
    public double? Class(string classKey) => _classes.TryGetValue(classKey, out var ms) ? ms : null;

    public double? Test(string classKey, string name) => _tests.TryGetValue($"{classKey}|{name}", out var ms) ? ms : null;

    /// <summary>Reads every file; a later file's result for the same test replaces an earlier one.</summary>
    public static ShardTimings Load(IEnumerable<string> files)
    {
        var testTimes = new Dictionary<string, (string Class, double Ms)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (XmlException ex) { throw new InvalidDataException($"{file} is not a JUnit XML file: {ex.Message}", ex); }
            foreach (var suite in doc.Descendants("testsuite"))
            {
                var project = (string?)suite.Attribute("name") ?? "";
                foreach (var test in suite.Elements("testcase"))
                {
                    var cls = (string?)test.Attribute("classname") ?? project;
                    var key = $"{project}|{cls}|{(string?)test.Attribute("name")}";
                    var seconds = double.TryParse((string?)test.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
                    testTimes[key] = ($"{project}|{cls}", seconds * 1000);
                }
            }
        }
        var timings = new ShardTimings();
        foreach (var (key, (cls, ms)) in testTimes)
        {
            timings._tests[key] = ms;
            timings._classes[cls] = timings._classes.GetValueOrDefault(cls) + ms;
        }
        return timings;
    }

    /// <summary>
    /// The files behind <c>--shard-timings</c> values: a file name, or a name with <c>*</c> / <c>?</c> in its last
    /// segment (shells do not expand it inside quotes, and the CI artifact folders vary). A named file that does not
    /// exist is an error; a pattern that matches nothing gives an empty list.
    /// </summary>
    public static List<string> Resolve(IEnumerable<string> values)
    {
        var files = new List<string>();
        foreach (var value in values)
        {
            var path = Path.GetFullPath(value);
            var name = Path.GetFileName(path);
            if (name.IndexOfAny(['*', '?']) < 0)
            {
                if (!File.Exists(path)) throw new FileNotFoundException($"--shard-timings: {value} does not exist.");
                files.Add(path);
                continue;
            }
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, name).Order(StringComparer.Ordinal));
        }
        return files;
    }
}
