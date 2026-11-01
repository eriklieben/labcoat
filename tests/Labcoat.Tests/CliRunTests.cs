using System.Xml.Linq;
using Labcoat;
using Labcoat.Export;
using Labcoat.Tests.Unit;

namespace Labcoat.Tests;

/// <summary>
/// <c>labcoat --run</c> (CliRun.cs): its options, test selection and exit codes, and end to end on a scratch copy of
/// samples/SampleTests (build, run, the JUnit / HTML / lcov files, exit codes 0 / 1 / 2). The end-to-end test builds
/// the sample (~30 s); environment variables are process-wide: not parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CliRunTests
{
    public TestContext TestContext { get; set; }

    private static StartOptions Ok(params string[] args)
    {
        var o = StartOptions.Parse(args, out var error);
        Assert.IsNull(error, error);
        return o!;
    }

    private static string Error(params string[] args)
    {
        Assert.IsNull(StartOptions.Parse(args, out var error), "expected an error for: " + string.Join(' ', args));
        return error!;
    }

    [TestMethod]
    public void Run_options_parse()
    {
        var o = Ok("--run", "--export", "junit=out/r.xml", "--export=HTML=report.html", "--export", "lcov=cov.info", "--export", "cobertura=c.xml",
            "--export", "markdown=r.md", "--export", "TRX=r.trx", "--export", "sarif=r.sarif", "--filter", "Cart*, Orders", "--filter=Math", "--export-paths", "absolute", "src");
        Assert.IsTrue(o.RunOnce);
        CollectionAssert.AreEqual(new[] { new ExportTarget("junit", "out/r.xml"), new ExportTarget("html", "report.html"), new ExportTarget("lcov", "cov.info"), new ExportTarget("cobertura", "c.xml"),
            new ExportTarget("markdown", "r.md"), new ExportTarget("trx", "r.trx"), new ExportTarget("sarif", "r.sarif") }, o.Exports);
        CollectionAssert.AreEqual(new[] { "Cart*", "Orders", "Math" }, o.Filters);
        Assert.IsTrue(o.ExportAbsolutePaths);
        Assert.IsFalse(Ok("--run", "--export-paths=relative").ExportAbsolutePaths);
        CollectionAssert.AreEqual(new[] { "src" }, o.Paths);

        Assert.Contains("--export needs format=file", Error("--run", "--export", "junit"));
        Assert.Contains("--export needs format=file", Error("--run", "--export", "junit="));
        Assert.Contains("--export needs format=file", Error("--run", "--export"));
        Assert.Contains("unknown format 'pdf'", Error("--run", "--export", "pdf=x.pdf"));
        Assert.Contains("--filter needs a pattern", Error("--run", "--filter", " "));
        Assert.Contains("--export-paths needs relative or absolute", Error("--run", "--export-paths", "x"));
        Assert.Contains("need --run", Error("--export", "junit=x.xml"));
        Assert.Contains("need --run", Error("--filter", "x"));
        Assert.Contains("need --run", Error("--retry-failed", "2"));
        Assert.Contains("--run", Cli.Usage);
        Assert.Contains("--export <format>=<file>", Cli.Usage);
        Assert.Contains("markdown, trx", Cli.Usage);
        Assert.Contains("$GITHUB_STEP_SUMMARY", Cli.Usage);
    }

    [TestMethod]
    public void Order_and_seed_parse()
    {
        var o = Ok("--run", "--order", "random", "--seed=77");
        Assert.AreEqual(("random", 77), (o.Order, o.Seed));
        var plain = Ok("--run", "--order=random");
        Assert.AreEqual(("random", (int?)null), (plain.Order, plain.Seed));
        Assert.IsNull(Ok("--run").Order);

        Assert.Contains("--order must be \"random\"", Error("--run", "--order", "alphabetical"));
        Assert.Contains("--seed needs a positive number", Error("--run", "--order", "random", "--seed", "x"));
        Assert.Contains("--seed needs a positive number", Error("--run", "--order", "random", "--seed", "0"));
        Assert.Contains("--seed needs --order random", Error("--run", "--seed", "5"));
        Assert.Contains("need --run", Error("--order", "random"));
        Assert.Contains("do not collect coverage", Error("--run", "--order", "random", "--coverage"));
        Assert.Contains("do not collect coverage", Error("--run", "--order", "random", "--export", "lcov=x.info"));
        Assert.Contains("do not collect coverage", Error("--run", "--order", "random", "--export", "cobertura=c.xml"));
        Assert.Contains("do not collect coverage", Error("--run", "--order", "random", "--min-coverage", "80"));
        Assert.Contains("do not collect coverage", Error("--run", "--order", "random", "--min-diff-coverage", "80"));
        Assert.AreEqual("random", Ok("--run", "--order", "random", "--export", "junit=r.xml").Order);
        Assert.Contains("--order random", Cli.Usage);
        Assert.Contains("--seed <n>", Cli.Usage);
    }

    [TestMethod]
    public void Retry_failed_option_is_validated()
    {
        Assert.AreEqual(0, Ok("--run").RetryFailed);
        Assert.AreEqual(2, Ok("--run", "--retry-failed", "2").RetryFailed);
        Assert.AreEqual(10, Ok("--run", "--retry-failed=10").RetryFailed);
        foreach (var bad in new[] { "0", "11", "-1", "two", "", "1.5" })
            Assert.Contains("--retry-failed needs a number from 1 to 10", Error("--run", "--retry-failed", bad));
        Assert.Contains("--retry-failed needs a number", Error("--run", "--retry-failed"));
        Assert.Contains("--order random can't be combined with --retry-failed", Error("--run", "--order", "random", "--retry-failed", "2"));
        Assert.Contains("--retry-failed <n>", Cli.Usage);
    }

    [TestMethod]
    public void A_failure_that_passed_on_a_retry_does_not_fail_the_run()
    {
        // CliRun.Merge moves flaky tests from failed to passed in the summary the exit code reads.
        var run = new RunSummary { RunId = "r", StartedAt = "x", Passed = 3, Failed = 2 };
        var merged = CliRun.Merge(new RunExport { WorkspaceName = "w", Version = "1", Run = run, Projects = [], Tests = [], GeneratedAt = "x" },
            new() { [("P", "a")] = [], [("P", "b")] = [] },
            new() { [("P", "a")] = (new ExportTest { ProjectId = "P", Id = "a", Name = "a" }, 2), [("P", "b")] = (new ExportTest { ProjectId = "P", Id = "b", Name = "b" }, 2) });
        Assert.AreEqual(CliRun.ExitPassed, CliRun.ExitCode(merged.Run, [], true));
        Assert.AreEqual(CliRun.ExitFailed, CliRun.ExitCode(run, [], true), "the first run's summary is not changed");
    }

    [TestMethod]
    public void Affected_and_impact_map_options_parse_and_validate()
    {
        Assert.IsNull(Ok("--run").Affected);
        Assert.AreEqual(new AffectedSelector("branch", null), Ok("--run", "--affected").Affected);
        Assert.AreEqual(new AffectedSelector("branch", null), Ok("--run", "--affected", "branch").Affected);
        Assert.AreEqual(new AffectedSelector("working", null), Ok("--run", "--affected", "working").Affected);
        Assert.AreEqual(new AffectedSelector("working", null), Ok("--run", "--affected=uncommitted").Affected);
        Assert.AreEqual(new AffectedSelector("range", "origin/main..HEAD"), Ok("--run", "--affected", "origin/main..HEAD").Affected);
        Assert.AreEqual(new AffectedSelector("range", "abc1234"), Ok("--run", "--affected=abc1234").Affected);
        // a path after a bare --affected stays a path
        var withPath = Ok("--run", "--affected", "src/MyApp.slnx");
        Assert.AreEqual(new AffectedSelector("branch", null), withPath.Affected);
        CollectionAssert.AreEqual(new[] { "src/MyApp.slnx" }, withPath.Paths);
        Assert.Contains("--affected needs", Error("--run", "--affected=--bad"));

        var o = Ok("--run", "--affected", "--impact-map-file", "ci/impact.json");
        Assert.AreEqual("ci/impact.json", o.ImpactMapFile);
        Assert.IsFalse(o.BuildImpactMap);
        var build = Ok("--run", "--build-impact-map", "--impact-map-file=impact.json.gz");
        Assert.AreEqual((true, "impact.json.gz"), (build.BuildImpactMap, build.ImpactMapFile));
        Assert.IsTrue(Ok("--run", "--affected", "--build-impact-map").BuildImpactMap, "build in this process, no file");

        Assert.Contains("need --run", Error("--affected"));
        Assert.Contains("need --run", Error("--impact-map-file", "x.json"));
        Assert.Contains("need --run", Error("--build-impact-map"));
        Assert.Contains("--impact-map-file needs a file", Error("--run", "--impact-map-file", " ", "--affected"));
        Assert.Contains("--impact-map-file needs a file", Error("--run", "--impact-map-file"));
        Assert.Contains("is a directory", Error("--run", "--affected", "--impact-map-file", Path.GetTempPath()));
        Assert.Contains("--build-impact-map needs --impact-map-file", Error("--run", "--build-impact-map"));
        Assert.Contains("--impact-map-file needs --affected", Error("--run", "--impact-map-file", "x.json"));
        Assert.Contains("--build-impact-map can't be combined with --shard", Error("--run", "--build-impact-map", "--impact-map-file", "x.json", "--shard", "1/2"));
        Assert.Contains("--min-coverage can't be combined with --affected", Error("--run", "--affected", "--min-coverage", "80"));
        Assert.IsNotNull(Ok("--run", "--affected", "--min-diff-coverage", "80", "--shard", "1/2", "--retry-failed", "2").Affected);
        Assert.IsNotNull(Ok("--run", "--affected", "--order", "random").Affected);
        Assert.Contains("--affected [", Cli.Usage);
        Assert.Contains("--impact-map-file <file>", Cli.Usage);
        Assert.Contains("--build-impact-map", Cli.Usage);
    }

    [TestMethod]
    public void Filters_match_names_classes_methods_and_projects()
    {
        TestNode N(string id, string name, string? ns, string? cls, string? method, string project = "P") =>
            new() { Id = id, ProjectId = project, DisplayName = name, Namespace = ns, ClassName = cls, MethodName = method };
        var tests = new[]
        {
            N("1", "Add_ReturnsSum", "Shop.Math", "CalculatorTests", "Add_ReturnsSum"),
            N("2", "Multiply (2,3,6)", "Shop.Math", "CalculatorTests", "Multiply"),
            N("3", "PlaceOrder", "Shop.Orders", "OrderServiceTests", "PlaceOrder", "Orders.Tests"),
            N("4", "Bare", null, null, null),
        };
        string Ids(params string[] patterns) => string.Join(",", CliRun.Matching(tests, patterns));
        Assert.AreEqual("1,2", Ids("calculator"));
        Assert.AreEqual("2", Ids("Shop.Math.CalculatorTests.Multiply"));
        Assert.AreEqual("2", Ids("Multiply (2*"));
        Assert.AreEqual("1,2", Ids("Shop.Math.*Tests"));
        Assert.AreEqual("", Ids("Shop.Math.*Tests.Nope"));
        Assert.AreEqual("1,3", Ids("*Sum", "Orders.Tests"));
        Assert.AreEqual("1,2,3,4", Ids("*"));
        Assert.AreEqual("4", Ids("Bare"));
        Assert.AreEqual("", Ids("A*Z*Q"));
        Assert.AreEqual("", Ids("Add*x"));
    }

    [TestMethod]
    public void Exit_codes()
    {
        var ok = new RunSummary { RunId = "r", StartedAt = "x", Passed = 3 };
        TestProject broken = new() { Id = "p", Name = "p", Path = "p.csproj", Status = ProjectStatus.Error };
        Assert.AreEqual(CliRun.ExitPassed, CliRun.ExitCode(ok, [], exportsOk: true));
        Assert.AreEqual(CliRun.ExitFailed, CliRun.ExitCode(ok with { Failed = 1 }, [], true));
        Assert.AreEqual(CliRun.ExitFailed, CliRun.ExitCode(ok with { Aborted = true }, [], true));
        Assert.AreEqual(CliRun.ExitFailed, CliRun.ExitCode(ok with { Cancelled = true }, [], true));
        Assert.AreEqual(CliRun.ExitFailed, CliRun.ExitCode(ok, [], exportsOk: false));
        Assert.AreEqual(CliRun.ExitBuildError, CliRun.ExitCode(ok with { Failed = 1 }, [broken], true));
        Assert.AreEqual(CliRun.ExitBuildError, CliRun.ExitCode(null, [], true));
    }

    [TestMethod]
    public void Tests_over_the_duration_budget_fail_the_run()
    {
        var ok = new RunSummary { RunId = "r", StartedAt = "x", Passed = 3 };
        Assert.AreEqual(CliRun.ExitFailed, CliRun.ExitCode(ok, [], true, overBudget: 1));
        Assert.AreEqual(CliRun.ExitPassed, CliRun.ExitCode(ok, [], true, overBudget: 0));
    }

    [TestMethod]
    public void The_duration_budget_lists_slow_tests_but_not_quarantined_or_skipped_ones()
    {
        static ExportTest T(string id, Outcome outcome, double? ms, bool quarantined = false) => new()
        {
            ProjectId = "P", Id = id, Name = id, Outcome = outcome, DurationMs = ms, Quarantined = quarantined,
        };
        var export = new RunExport
        {
            WorkspaceName = "w", Version = "1", Run = new RunSummary { RunId = "r", StartedAt = "x" }, Projects = [], GeneratedAt = "x",
            Tests =
            [
                T("fast", Outcome.Passed, 100), T("edge", Outcome.Passed, 1000), T("slow", Outcome.Passed, 1500),
                T("slowest-failed", Outcome.Failed, 9000), T("quarantined", Outcome.Passed, 8000, quarantined: true),
                T("skipped", Outcome.Skipped, 5000), T("unknown", Outcome.Passed, null),
            ],
        };
        var over = DurationBudget.OverBudget(export, TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(new[] { "slowest-failed", "slow" }, over.Select(t => t.Id).ToArray());
        Assert.AreEqual(9000, over[0].DurationMs);
        Assert.IsEmpty(DurationBudget.OverBudget(export, TimeSpan.Zero), "a zero budget is off");
        Assert.IsEmpty(DurationBudget.OverBudget(export, TimeSpan.FromMinutes(1)));
    }

    // ------------------------------------------------------------------ end to end

    private static readonly string[] IsolatedEnv = ["LABCOAT_HISTORY_DIR", LabcoatConfig.NoUserConfigEnv, "LABCOAT_NAME", "NO_COLOR", "CI", "GITHUB_ACTIONS", "GITHUB_STEP_SUMMARY"];

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "samples", "SampleTests"))) return d.FullName;
        throw new InvalidOperationException("samples/SampleTests not found above " + AppContext.BaseDirectory);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    [TestMethod]
    [TestCategory("e2e")]
    [Timeout(480_000, CooperativeCancellation = true)]
    public async Task Run_once_on_the_sample_writes_the_reports_and_exits_with_the_outcome()
    {
        var saved = IsolatedEnv.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var dir = Directory.CreateTempSubdirectory("labcoat-clirun-");
        var originalOut = Console.Out;
        var console = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
            Environment.SetEnvironmentVariable("LABCOAT_HISTORY_DIR", Path.Combine(dir.FullName, "history"));
            Environment.SetEnvironmentVariable(LabcoatConfig.NoUserConfigEnv, "1");
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            var ws = Path.Combine(dir.FullName, "ws");
            var samples = Path.Combine(RepoRoot(), "samples");
            CopyTree(Path.Combine(samples, "SampleTests"), Path.Combine(ws, "SampleTests"));
            CopyTree(Path.Combine(samples, "SampleTests.Lib"), Path.Combine(ws, "SampleTests.Lib"));
            File.Copy(Path.Combine(samples, "Directory.Packages.props"), Path.Combine(ws, "Directory.Packages.props"));
            Directory.CreateDirectory(Path.Combine(ws, ".git")); // the workspace root for coverage paths
            var project = Path.Combine(ws, "SampleTests");
            var outDir = Path.Combine(dir.FullName, "out");
            Console.SetOut(console);

            // ---- everything: the sample has deliberate failures → 1 (reported to GitHub Actions as well)
            var summary = Path.Combine(dir.FullName, "step-summary.md");
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", summary);
            var code = await LabcoatApp.RunAsync(["--run", "--export", $"junit={outDir}/results.xml", "--export", $"html={outDir}/report.html",
                "--export", $"markdown={outDir}/report.md", "--export", $"trx={outDir}/results.trx", "--export", $"sarif={outDir}/results.sarif",
                "--export", $"lcov={outDir}/coverage.info", project], TestContext.CancellationToken);
            var log = console.ToString();
            Assert.AreEqual(CliRun.ExitFailed, code, log);
            Assert.Contains("7 failed", log);
            Assert.Contains("9 passed, 1 skipped (17 total)", log);
            var junit = ExportTests.ValidJUnit(File.ReadAllText(Path.Combine(outDir, "results.xml")));
            Assert.AreEqual("17", (string)junit.Root!.Attribute("tests")!);
            Assert.HasCount(7, junit.Descendants("testcase").Where(c => c.Element("failure") is not null || c.Element("error") is not null));
            Assert.AreEqual("cli", junit.Descendants("property").Single(p => (string)p.Attribute("name")! == "labcoat.trigger").Attribute("value")!.Value);
            Assert.IsTrue(junit.Descendants("testcase").Any(c => (string)c.Attribute("name")! == "Multiply_ReturnsProduct (3,3,10)"), "data rows by display name");
            var html = File.ReadAllText(Path.Combine(outDir, "report.html"));
            Assert.Contains("7 tests failed", html);
            Assert.Contains("Multiply_ReturnsProduct (3,3,10)", html);
            Assert.Contains("## Test results: 7 failed", File.ReadAllText(Path.Combine(outDir, "report.md")));
            Assert.AreEqual(7, XDocument.Load(Path.Combine(outDir, "results.trx")).Descendants().Count(e => e.Name.LocalName == "UnitTestResult" && (string?)e.Attribute("outcome") == "Failed"));
            Assert.HasCount(7, System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "results.sarif"))).RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList());
            var annotations = log.Split('\n').Where(l => l.StartsWith("::error ", StringComparison.Ordinal)).ToList();
            Assert.HasCount(7, annotations);
            Assert.IsTrue(annotations.All(a => a.Contains("file=") && a.Contains("line=")), "at their source location: " + string.Join("\n", annotations));
            Assert.Contains("## Test results: 7 failed", File.ReadAllText(summary));
            Assert.Contains("### Coverage", File.ReadAllText(summary));
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
            var lcov = File.ReadAllText(Path.Combine(outDir, "coverage.info"));
            Assert.Contains("SF:SampleTests.Lib/Calculator.cs\n", lcov, "relative to the repository root");
            Assert.Contains("end_of_record", lcov);

            // ---- a filter that selects only passing tests → 0
            console.GetStringBuilder().Clear();
            code = await LabcoatApp.RunAsync(["--run", "--filter", "*CalculatorTests.Add*,Subtract", "--export", $"junit={outDir}/passing.xml", project], TestContext.CancellationToken);
            Assert.AreEqual(CliRun.ExitPassed, code, console.ToString());
            Assert.AreEqual("2", (string)XDocument.Load(Path.Combine(outDir, "passing.xml")).Root!.Attribute("tests")!);

            // ---- a filter that matches nothing → 1, an unknown option → 2
            Assert.AreEqual(CliRun.ExitFailed, await LabcoatApp.RunAsync(["--run", "--filter", "NoSuchTest", project], TestContext.CancellationToken));
            Assert.AreEqual(CliRun.ExitBuildError, await LabcoatApp.RunAsync(["--run", "--nope"], TestContext.CancellationToken));

            // ---- a build error → 2; the reports are still written (empty)
            File.WriteAllText(Path.Combine(project, "Broken.cs"), "this is not C#");
            console.GetStringBuilder().Clear();
            code = await LabcoatApp.RunAsync(["--run", "--export", $"junit={outDir}/broken.xml", "--export", $"html={outDir}/broken.html", project], TestContext.CancellationToken);
            Assert.AreEqual(CliRun.ExitBuildError, code, console.ToString());
            Assert.AreEqual("0", (string)ExportTests.ValidJUnit(File.ReadAllText(Path.Combine(outDir, "broken.xml"))).Root!.Attribute("tests")!);
            Assert.Contains("No test results in this run", File.ReadAllText(Path.Combine(outDir, "broken.html")));
        }
        finally
        {
            Console.SetOut(originalOut);
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort: a build server may still hold a file */ }
        }
    }
}
