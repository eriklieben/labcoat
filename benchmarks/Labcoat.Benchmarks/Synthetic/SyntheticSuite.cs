using System.Globalization;

namespace Labcoat.Benchmarks.Synthetic;

/// <summary>
/// A deterministic test suite of any size, shaped like the UI's <c>?mock=huge</c> scenario (ui/src/mock/scale.ts):
/// projects of up to 2,500 tests, 5-6 level namespaces, classes of ~8 methods, every third method data-driven with 3-8
/// rows, ~1% failed, ~0.5% skipped, ~0.3% flaky, ~0.05% timed out, durations mostly 1-30 ms with a long tail. Every
/// test class has one product file it exercises (<see cref="TestClass.ProductFile"/>), for coverage and impact data.
/// Same seed as the mock, so the 20,000-test suite is the same shape in the UI perf checks and here.
/// </summary>
public sealed class SyntheticSuite
{
    private static readonly string[] Products = ["Billing", "Catalog", "Identity", "Shipping", "Inventory", "Search", "Payments", "Notifications"];
    private static readonly string[] Areas = ["Api", "Domain", "Infrastructure", "Application", "Integration"];
    private static readonly string[] Subs = ["Commands", "Queries", "Handlers", "Validators", "Mappers", "Policies"];
    private static readonly string[] Features = ["Create", "Update", "Delete", "Lookup", "Export", "Import", "Sync", "Audit"];
    private static readonly string[] Subjects = ["Order", "Customer", "Invoice", "Product", "Account", "Address", "Token", "Shipment", "Price", "Report"];
    private static readonly string[] Verbs = ["Handle", "Validate", "Map", "Resolve", "Compute", "Persist", "Publish", "Retry", "Parse", "Format"];
    private static readonly string[] Endings = ["Succeeds", "ReturnsExpected", "ThrowsOnInvalidInput", "IsIdempotent", "RespectsCancellation", "LogsWarning"];

    /// <summary>Lines per generated product file; methods are <see cref="MethodLines"/> long, starting at line 10.</summary>
    public const int ProductFileLines = 240;
    public const int MethodLines = 12;

    public sealed record TestClass(string ProjectId, string Namespace, string Name, string TestFile, string ProductFile, string Module, List<string> TestIds);

    public required string Root { get; init; }
    public required List<TestProject> Projects { get; init; }
    public required List<TestNode> Tests { get; init; }
    public required List<TestClass> Classes { get; init; }
    /// <summary>Distinct product files (one per test class), absolute.</summary>
    public IEnumerable<string> ProductFiles => Classes.Select(c => c.ProductFile);

    /// <param name="count">Number of tests (1,000 / 20,000 in the benchmarks).</param>
    /// <param name="root">Absolute directory the paths are under (need not exist unless files are written).</param>
    /// <param name="historyRuns">History entries per test (the mock's previous session has 12, the store keeps 20).</param>
    public static SyntheticSuite Create(int count, string root, int historyRuns = 12)
    {
        var projectCount = Math.Clamp(count / 2500, 2, 40);
        var perProject = (count + projectCount - 1) / projectCount;
        long seed = 7;
        double Rnd() => (seed = seed * 16807 % 2147483647) / 2147483647.0;

        var projects = new List<TestProject>();
        var tests = new List<TestNode>(count);
        var classes = new List<TestClass>();
        var n = 0;
        for (var p = 0; p < projectCount && n < count; p++)
        {
            var product = Products[p % Products.Length] + (p >= Products.Length ? (p / Products.Length + 1).ToString(CultureInfo.InvariantCulture) : "");
            var pid = $"Contoso.{product}.Tests";
            projects.Add(new TestProject { Id = pid, Name = pid, Path = $"{root}/tests/{pid}/{pid}.csproj" });
            var inProject = 0;
            for (var c = 0; inProject < perProject && n < count; c++)
            {
                var area = Areas[c % Areas.Length];
                var sub = Subs[c / Areas.Length % Subs.Length];
                var deep = c % 3 != 0;
                var feature = Features[c / (Areas.Length * Subs.Length) % Features.Length];
                var ns = $"{pid}.{area}.{sub}{(deep ? "." + feature : "")}";
                var singular = sub.EndsWith("ies", StringComparison.Ordinal) ? sub[..^3] + "y" : sub.TrimEnd('s');
                var cls = $"{Subjects[c % Subjects.Length]}{singular}Tests{(c >= 30 ? c.ToString(CultureInfo.InvariantCulture) : "")}";
                var file = $"{root}/tests/{pid}/{area}/{sub}/{cls}.cs";
                var productModule = $"Contoso.{product}";
                var productFile = $"{root}/src/{productModule}/{area}/{sub}/{Subjects[c % Subjects.Length]}{singular}{c}.cs";
                var klass = new TestClass(pid, ns, cls, file, productFile, productModule, []);
                classes.Add(klass);
                for (var m = 0; m < 8 && inProject < perProject && n < count; m++)
                {
                    var method = $"{Verbs[(m + c) % Verbs.Length]}_{Subjects[(m * 3 + c) % Subjects.Length]}_{Endings[(m + p) % Endings.Length]}";
                    var rows = m % 3 == 0 ? 3 + (int)(Rnd() * 6) : 1;
                    for (var r = 0; r < rows && inProject < perProject && n < count; r++)
                    {
                        n++;
                        inProject++;
                        var x = Rnd();
                        var outcome = x < 0.01 ? Outcome.Failed : x < 0.015 ? Outcome.Skipped : x < 0.018 ? Outcome.Failed : x < 0.0185 ? Outcome.TimedOut : Outcome.Passed;
                        var flaky = x is >= 0.015 and < 0.018;
                        var tail = Rnd();
                        var (lo, hi) = tail < 0.03 ? (100, 2000) : tail < 0.15 ? (20, 90) : (1, 30);
                        var ms = Math.Round(lo + Rnd() * (hi - lo), 2);
                        var args = rows > 1 ? $"({r},\"{Subjects[r % Subjects.Length].ToLowerInvariant()}-{r * 7}\",{(r * 1.25).ToString("0.00", CultureInfo.InvariantCulture)})" : "";
                        var id = $"{ns}.{cls}.{method}{args}";
                        var failing = outcome is Outcome.Failed or Outcome.TimedOut;
                        var line = 14 + m * 11;
                        var node = new TestNode
                        {
                            Id = id,
                            ProjectId = pid,
                            DisplayName = method + args,
                            Namespace = ns,
                            ClassName = cls,
                            MethodName = method,
                            FilePath = file,
                            Line = line,
                            LineEnd = line + 8,
                            Traits = n % 5 == 0 ? [new Trait("TestCategory", n % 10 == 0 ? "Integration" : "Unit")] : [],
                            Outcome = outcome,
                            DurationMs = ms,
                            ErrorType = outcome == Outcome.TimedOut ? "Timeout" : failing ? "AssertFailedException" : null,
                            ErrorMessage = outcome == Outcome.TimedOut ? "Test exceeded the 5000 ms timeout."
                                : failing ? $"Assert.AreEqual failed. Expected:<{n}>. Actual:<{n + 1}>."
                                : outcome == Outcome.Skipped ? "Ignored: pending upstream fix" : null,
                            StackTrace = failing ? $"   at {ns}.{cls}.{method}() in {file}:line {line + 3}" : null,
                            StdOut = n % 50 == 0 ? $"Arrange {cls}.{method}\nAct\nAssert ok\n" : null,
                            LastRunAt = "2026-09-01T10:00:00.0000000+00:00",
                            Flaky = flaky ? true : null,
                        };
                        if (historyRuns > 0) node.History = History(historyRuns, outcome, ms, flaky);
                        tests.Add(node);
                        klass.TestIds.Add(id);
                    }
                }
            }
        }
        return new SyntheticSuite { Root = root, Projects = projects, Tests = tests, Classes = classes };
    }

    private static List<HistoryEntry> History(int runs, Outcome last, double ms, bool flaky)
    {
        var list = new List<HistoryEntry>(runs);
        for (var i = 0; i < runs; i++)
        {
            var outcome = i == runs - 1 ? last : flaky && i % 3 == 0 ? Outcome.Failed : last == Outcome.Skipped ? Outcome.Skipped : Outcome.Passed;
            list.Add(new HistoryEntry(outcome, Math.Round(ms * (0.8 + i % 5 * 0.1), 2),
                $"2026-08-{1 + i % 28:00}T10:00:00.0000000+00:00", $"run-{i:0000}"));
        }
        return list;
    }

    /// <summary>The method spans of a generated product file (what a test of its class executes: every other method).</summary>
    public static List<LineSpan> MethodSpans(int classIndex, int methods = ProductFileLines / MethodLines - 2)
    {
        var spans = new List<LineSpan>();
        for (var i = 0; i < methods; i++)
            if ((i + classIndex) % 2 == 0)
                spans.Add(new LineSpan(10 + i * MethodLines, 10 + i * MethodLines + MethodLines - 2) { Name = $"Method{i}" });
        return spans;
    }

    /// <summary>Writes every product file (<see cref="ProductFileLines"/> lines of plausible C#) under <see cref="Root"/>.</summary>
    public void WriteProductFiles()
    {
        foreach (var c in Classes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(c.ProductFile)!);
            File.WriteAllText(c.ProductFile, ProductSource(Path.GetFileNameWithoutExtension(c.ProductFile)));
        }
    }

    public static string ProductSource(string className)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("namespace Contoso;\n\n");
        sb.Append("public sealed class ").Append(className).Append('\n').Append("{\n");
        while (CountLines(sb) < 9) sb.Append("    // ...\n");
        for (var i = 0; CountLines(sb) < ProductFileLines - 1; i++)
        {
            sb.Append("    public int Method").Append(i).Append("(int value)\n    {\n");
            for (var k = 0; k < MethodLines - 5; k++) sb.Append("        value = value * 31 + ").Append(k).Append(";\n");
            sb.Append("        return value;\n    }\n\n");
        }
        sb.Append("}\n");
        return sb.ToString();

        static int CountLines(System.Text.StringBuilder sb)
        {
            var lines = 0;
            for (var i = 0; i < sb.Length; i++) if (sb[i] == '\n') lines++;
            return lines;
        }
    }

    /// <summary>A fresh temp directory for one benchmark class (deleted by <see cref="DeleteTempDir"/>).</summary>
    public static string NewTempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "labcoat-bench", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteTempDir(string? dir)
    {
        try { if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
