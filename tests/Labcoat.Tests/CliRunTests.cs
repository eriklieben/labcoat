using System.Xml.Linq;
using Labcoat;
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
            "--filter", "Cart*, Orders", "--filter=Math", "--export-paths", "absolute", "src");
        Assert.IsTrue(o.RunOnce);
        CollectionAssert.AreEqual(new[] { new ExportTarget("junit", "out/r.xml"), new ExportTarget("html", "report.html"), new ExportTarget("lcov", "cov.info"), new ExportTarget("cobertura", "c.xml") }, o.Exports);
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
        Assert.Contains("--run", Cli.Usage);
        Assert.Contains("--export <format>=<file>", Cli.Usage);
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
        Assert.AreEqual("random", Ok("--run", "--order", "random", "--export", "junit=r.xml").Order);
        Assert.Contains("--order random", Cli.Usage);
        Assert.Contains("--seed <n>", Cli.Usage);
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

    // ------------------------------------------------------------------ end to end

    private static readonly string[] IsolatedEnv = ["LABCOAT_HISTORY_DIR", LabcoatConfig.NoUserConfigEnv, "LABCOAT_NAME", "NO_COLOR", "CI"];

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

            // ---- everything: the sample has deliberate failures → 1
            var code = await LabcoatApp.RunAsync(["--run", "--export", $"junit={outDir}/results.xml", "--export", $"html={outDir}/report.html",
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
