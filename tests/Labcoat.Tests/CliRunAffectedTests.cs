using System.Xml.Linq;
using Labcoat;

namespace Labcoat.Tests;

/// <summary>
/// <c>labcoat --run --affected</c> with an <c>--impact-map-file</c> (CliRun.Affected.cs), end to end on a scratch git
/// repository of samples/SampleTests: the project fallback without a map, building the map on main, only the tests that
/// run the changed method with it, and nothing affected (exit 0, empty reports). Builds the sample several times
/// (~2 minutes); environment variables are process-wide: not parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CliRunAffectedTests
{
    public TestContext TestContext { get; set; }

    private static readonly string[] IsolatedEnv = ["LABCOAT_HISTORY_DIR", LabcoatConfig.NoUserConfigEnv, "LABCOAT_NAME", "NO_COLOR", "CI", "MSBUILDDISABLENODEREUSE"];

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

    private async Task GitAsync(string cwd, params string[] args)
    {
        var (code, _, err) = await GitRepo.RunAsync(cwd, args, TestContext.CancellationToken);
        Assert.AreEqual(0, code, $"git {string.Join(' ', args)}: {err}");
    }

    [TestMethod]
    [TestCategory("e2e")]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task Run_affected_uses_the_impact_map_of_main_and_exits_zero_when_nothing_is_affected()
    {
        var saved = IsolatedEnv.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var dir = Directory.CreateTempSubdirectory("labcoat-affected-");
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
            File.WriteAllText(Path.Combine(ws, ".gitignore"), "bin/\nobj/\nout/\n*.received.*\n");
            await GitAsync(ws, "init", "-q", "-b", "main");
            await GitAsync(ws, "config", "user.email", "t@example.com");
            await GitAsync(ws, "config", "user.name", "T");
            await GitAsync(ws, "config", "commit.gpgsign", "false");
            await GitAsync(ws, "add", "-A");
            await GitAsync(ws, "commit", "-q", "-m", "initial");
            await GitAsync(ws, "switch", "-q", "-c", "feature");
            var calculator = Path.Combine(ws, "SampleTests.Lib", "Calculator.cs");
            File.WriteAllText(calculator, File.ReadAllText(calculator).Replace("a - b;", "a - b + 0;"));
            await GitAsync(ws, "commit", "-q", "-am", "subtract");

            var project = Path.Combine(ws, "SampleTests");
            var outDir = Path.Combine(dir.FullName, "out");
            var mapFile = Path.Combine(outDir, "impact.json");
            Console.SetOut(console);

            // ---- no map: the changed product file selects every test of the projects that use it (the sample fails on purpose: 1)
            var code = await LabcoatApp.RunAsync(["--run", "--affected", "--export", $"junit={outDir}/fallback.xml", project], TestContext.CancellationToken);
            var log = console.ToString();
            Assert.AreEqual(CliRun.ExitFailed, code, log);
            Assert.Contains("1 changed file -> 17 of 17 tests", log);
            Assert.Contains("Calculator.cs: project fallback", log);
            Assert.AreEqual("17", (string)XDocument.Load(Path.Combine(outDir, "fallback.xml")).Root!.Attribute("tests")!);

            // ---- main builds the map (and runs its tests)
            await GitAsync(ws, "switch", "-q", "main");
            console.GetStringBuilder().Clear();
            code = await LabcoatApp.RunAsync(["--run", "--filter", "CalculatorTests", "--build-impact-map", "--impact-map-file", mapFile, project], TestContext.CancellationToken);
            log = console.ToString();
            Assert.AreEqual(CliRun.ExitFailed, code, log); // Multiply (3,3,10) fails on purpose
            Assert.Contains("Wrote impact map:", log);
            var map = File.ReadAllText(mapFile);
            Assert.Contains("\"SampleTests.Lib/Calculator.cs\"", map, "relative, forward slashes");
            Assert.DoesNotContain(ws, map);

            // ---- the pull request: only the tests that run Subtract
            await GitAsync(ws, "switch", "-q", "feature");
            console.GetStringBuilder().Clear();
            code = await LabcoatApp.RunAsync(["--run", "--affected", "--impact-map-file", mapFile, "--export", $"junit={outDir}/affected.xml", project], TestContext.CancellationToken);
            log = console.ToString();
            Assert.AreEqual(CliRun.ExitFailed, code, log);
            Assert.Contains("-> 6 of 17 tests", log);
            Assert.Contains("Calculator.cs: impact map (class-level data, method Subtract) -> 6 tests", log);
            var junit = XDocument.Load(Path.Combine(outDir, "affected.xml"));
            Assert.AreEqual("6", (string)junit.Root!.Attribute("tests")!);
            Assert.IsTrue(junit.Descendants("testcase").All(c => ((string)c.Attribute("classname")!).EndsWith("CalculatorTests", StringComparison.Ordinal)));

            // ---- with --filter: the intersection
            console.GetStringBuilder().Clear();
            code = await LabcoatApp.RunAsync(["--run", "--affected", "--filter", "Subtract", "--impact-map-file", mapFile, project], TestContext.CancellationToken);
            Assert.AreEqual(CliRun.ExitPassed, code, console.ToString());
            Assert.Contains("-> 1 of 17 tests", console.ToString());

            // ---- nothing affected: exit 0, the reports exist and say so
            await GitAsync(ws, "switch", "-q", "main");
            console.GetStringBuilder().Clear();
            code = await LabcoatApp.RunAsync(["--run", "--affected", "--impact-map-file", mapFile, "--export", $"junit={outDir}/none.xml",
                "--export", $"html={outDir}/none.html", "--export", $"lcov={outDir}/none.info", project], TestContext.CancellationToken);
            Assert.AreEqual(CliRun.ExitPassed, code, console.ToString());
            Assert.Contains("No affected tests", console.ToString());
            Assert.AreEqual("0", (string)XDocument.Load(Path.Combine(outDir, "none.xml")).Root!.Attribute("tests")!);
            Assert.IsTrue(File.Exists(Path.Combine(outDir, "none.html")));
            Assert.IsTrue(File.Exists(Path.Combine(outDir, "none.info")));

            // ---- git can't answer (an unknown range): 2
            code = await LabcoatApp.RunAsync(["--run", "--affected=nope..HEAD", project], TestContext.CancellationToken);
            Assert.AreEqual(CliRun.ExitBuildError, code);
        }
        finally
        {
            Console.SetOut(originalOut);
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            try
            {
                foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal; // .git objects are read-only
                dir.Delete(recursive: true);
            }
            catch (IOException) { /* best effort: a build server may still hold a file */ }
        }
    }
}
