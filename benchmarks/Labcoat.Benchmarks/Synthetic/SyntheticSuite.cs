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
        var generator = new Generator(count, root, historyRuns);
        generator.Run();
        return new SyntheticSuite { Root = root, Projects = generator.Projects, Tests = generator.Tests, Classes = generator.Classes };
    }

    /// <summary>The mock's Park-Miller generator (ui/src/mock/scale.ts): the same sequence for the same seed.</summary>
    private sealed class Lcg(long seed)
    {
        private long _seed = seed;

        public double Next()
        {
            _seed = _seed * 16807 % 2147483647;
            return _seed / 2147483647.0;
        }
    }

    /// <summary>
    /// Fills projects with classes, classes with methods and methods with rows until <c>count</c> tests exist. The
    /// order of the draws from the generator is part of the output: one per data-driven method (its row count), then
    /// three per test (outcome, duration bucket, duration).
    /// </summary>
    private sealed class Generator(int count, string root, int historyRuns)
    {
        private const int MethodsPerClass = 8;

        private readonly int _projectCount = Math.Clamp(count / 2500, 2, 40);
        private readonly Lcg _random = new(7);
        private int _n;
        private int _inProject;
        private int _perProject;

        public List<TestProject> Projects { get; } = [];
        public List<TestNode> Tests { get; } = new(count);
        public List<TestClass> Classes { get; } = [];

        /// <summary>Room for another test in the current project (and in the suite).</summary>
        private bool ProjectHasRoom => _inProject < _perProject && _n < count;

        public void Run()
        {
            _perProject = (count + _projectCount - 1) / _projectCount;
            for (var p = 0; p < _projectCount && _n < count; p++) AddProject(p);
        }

        private void AddProject(int p)
        {
            var product = Products[p % Products.Length] + (p >= Products.Length ? (p / Products.Length + 1).ToString(CultureInfo.InvariantCulture) : "");
            var pid = $"Contoso.{product}.Tests";
            Projects.Add(new TestProject { Id = pid, Name = pid, Path = $"{root}/tests/{pid}/{pid}.csproj" });
            _inProject = 0;
            var c = 0;
            while (ProjectHasRoom)
            {
                var klass = NewClass(pid, product, c);
                Classes.Add(klass);
                for (var m = 0; m < MethodsPerClass && ProjectHasRoom; m++) AddMethod(klass, p, c, m);
                c++;
            }
        }

        private TestClass NewClass(string pid, string product, int c)
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
            return new TestClass(pid, ns, cls, file, productFile, productModule, []);
        }

        private void AddMethod(TestClass klass, int p, int c, int m)
        {
            var method = $"{Verbs[(m + c) % Verbs.Length]}_{Subjects[(m * 3 + c) % Subjects.Length]}_{Endings[(m + p) % Endings.Length]}";
            var rows = m % 3 == 0 ? 3 + (int)(_random.Next() * 6) : 1;
            for (var r = 0; r < rows && ProjectHasRoom; r++)
            {
                _n++;
                _inProject++;
                var args = rows > 1 ? Arguments(r) : "";
                var node = NewTest(klass, method, args, line: 14 + m * 11);
                Tests.Add(node);
                klass.TestIds.Add(node.Id);
            }
        }

        private TestNode NewTest(TestClass klass, string method, string args, int line)
        {
            var x = _random.Next();
            var outcome = OutcomeFor(x);
            var flaky = x is >= 0.015 and < 0.018;
            var (lo, hi) = DurationRange(_random.Next());
            var ms = Math.Round(lo + _random.Next() * (hi - lo), 2);
            var failing = outcome is Outcome.Failed or Outcome.TimedOut;
            var node = new TestNode
            {
                Id = $"{klass.Namespace}.{klass.Name}.{method}{args}",
                ProjectId = klass.ProjectId,
                DisplayName = method + args,
                Namespace = klass.Namespace,
                ClassName = klass.Name,
                MethodName = method,
                FilePath = klass.TestFile,
                Line = line,
                LineEnd = line + 8,
                Traits = TraitsFor(_n),
                Outcome = outcome,
                DurationMs = ms,
                ErrorType = ErrorType(outcome),
                ErrorMessage = ErrorMessage(outcome, _n),
                StackTrace = failing ? $"   at {klass.Namespace}.{klass.Name}.{method}() in {klass.TestFile}:line {line + 3}" : null,
                StdOut = _n % 50 == 0 ? $"Arrange {klass.Name}.{method}\nAct\nAssert ok\n" : null,
                LastRunAt = "2026-09-01T10:00:00.0000000+00:00",
                Flaky = flaky ? true : null,
            };
            if (historyRuns > 0) node.History = History(historyRuns, outcome, ms, flaky);
            return node;
        }

        /// <summary>~1% failed, ~0.5% skipped, ~0.3% failed and flaky, ~0.05% timed out.</summary>
        private static Outcome OutcomeFor(double x) => x switch
        {
            < 0.01 => Outcome.Failed,
            < 0.015 => Outcome.Skipped,
            < 0.018 => Outcome.Failed,
            < 0.0185 => Outcome.TimedOut,
            _ => Outcome.Passed,
        };

        /// <summary>Durations: 3% 100-2000 ms, 12% 20-90 ms, the rest 1-30 ms.</summary>
        private static (int Lo, int Hi) DurationRange(double tail) => tail switch
        {
            < 0.03 => (100, 2000),
            < 0.15 => (20, 90),
            _ => (1, 30),
        };

        private static string Arguments(int r) =>
            $"({r},\"{Subjects[r % Subjects.Length].ToLowerInvariant()}-{r * 7}\",{(r * 1.25).ToString("0.00", CultureInfo.InvariantCulture)})";

        /// <summary>Every fifth test has a category: every tenth is an integration test, the others unit tests.</summary>
        private static List<Trait> TraitsFor(int n)
        {
            if (n % 5 != 0) return [];
            var category = n % 10 == 0 ? "Integration" : "Unit";
            return [new Trait("TestCategory", category)];
        }

        private static string? ErrorType(Outcome outcome) => outcome switch
        {
            Outcome.TimedOut => "Timeout",
            Outcome.Failed => "AssertFailedException",
            _ => null,
        };

        private static string? ErrorMessage(Outcome outcome, int n) => outcome switch
        {
            Outcome.TimedOut => "Test exceeded the 5000 ms timeout.",
            Outcome.Failed => $"Assert.AreEqual failed. Expected:<{n}>. Actual:<{n + 1}>.",
            Outcome.Skipped => "Ignored: pending upstream fix",
            _ => null,
        };

        private static List<HistoryEntry> History(int runs, Outcome last, double ms, bool flaky)
        {
            var list = new List<HistoryEntry>(runs);
            for (var i = 0; i < runs; i++)
            {
                list.Add(new HistoryEntry(HistoryOutcome(i, runs, last, flaky), Math.Round(ms * (0.8 + i % 5 * 0.1), 2),
                    $"2026-08-{1 + i % 28:00}T10:00:00.0000000+00:00", $"run-{i:0000}"));
            }
            return list;
        }

        /// <summary>The last run is the current outcome; a flaky test failed every third run before; the rest passed
        /// (or were skipped, for a skipped test).</summary>
        private static Outcome HistoryOutcome(int i, int runs, Outcome last, bool flaky)
        {
            if (i == runs - 1) return last;
            if (flaky && i % 3 == 0) return Outcome.Failed;
            return last == Outcome.Skipped ? Outcome.Skipped : Outcome.Passed;
        }
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
        foreach (var file in ProductFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, ProductSource(Path.GetFileNameWithoutExtension(file)));
        }
    }

    public static string ProductSource(string className)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("namespace Contoso;\n\n");
        sb.Append("public sealed class ").Append(className).Append('\n').Append("{\n");
        while (CountLines(sb) < 9) sb.Append("    // ...\n");
        var method = 0;
        while (CountLines(sb) < ProductFileLines - 1)
        {
            sb.Append("    public int Method").Append(method).Append("(int value)\n    {\n");
            for (var k = 0; k < MethodLines - 5; k++) sb.Append("        value = value * 31 + ").Append(k).Append(";\n");
            sb.Append("        return value;\n    }\n\n");
            method++;
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
        catch (IOException) { /* Best effort: a file still in use leaves the directory for the OS's temp cleanup. */ }
        catch (UnauthorizedAccessException) { /* Best effort, as above. */ }
    }
}
