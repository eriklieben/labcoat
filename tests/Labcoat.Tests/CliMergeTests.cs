using System.Xml.Linq;
using Labcoat;
using Labcoat.Tests.Unit;

namespace Labcoat.Tests;

/// <summary>
/// <c>labcoat merge</c> (CliMerge.cs, JUnitMerge.cs, CoverageMerge.cs): the per-shard JUnit, lcov and Cobertura files of
/// a split run combined into one. Runs the whole command in-process on scratch files (Console is process-wide: not parallel).
/// </summary>
[TestClass]
[DoNotParallelize]
public class CliMergeTests
{
    private static string Dir(string name = "") => Path.Combine(Scratch.FullName, name);

    private static readonly DirectoryInfo Scratch = Directory.CreateTempSubdirectory("labcoat-merge-");

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Scratch.Delete(recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static int _counter;

    /// <summary>A fresh folder for one test.</summary>
    private static string NewDir()
    {
        var dir = Dir("t" + Interlocked.Increment(ref _counter));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string name, string content, string newline = "\n")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content.ReplaceLineEndings(newline));
        return path;
    }

    /// <summary>Runs <c>labcoat merge</c>; returns the exit code and what it printed (stdout and stderr).</summary>
    private static async Task<(int Code, string Log)> Merge(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var console = new StringWriter();
        try
        {
            Console.SetOut(console);
            Console.SetError(console);
            var code = await LabcoatApp.RunAsync(["merge", .. args]);
            return (code, console.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    // ------------------------------------------------------------------ fixtures

    private const string DefaultProperties = """<property name="labcoat.version" value="1.1.1" /><property name="targetFramework" value="net10.0" />""";

    private static string JUnit(string suite, string cases, string? timestamp = "2026-10-11T08:00:00", string? properties = null) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites name="Labcoat shop" tests="0" failures="0" errors="0" time="0">
          <testsuite name="{suite}" tests="0" failures="0" errors="0" skipped="0" time="0"{Stamp(timestamp)} id="0" package="{suite}">
            <properties>{properties ?? DefaultProperties}</properties>
            {cases}
          </testsuite>
        </testsuites>
        """;

    private static string Stamp(string? timestamp) => timestamp is null ? "" : $" timestamp=\"{timestamp}\"";

    private const string Pass = """<testcase classname="Shop.CartTests" name="Adds" time="0.5" />""";
    private const string PassB = """<testcase classname="Shop.CartTests" name="Removes" time="0.25" />""";
    private const string Fail = """<testcase classname="Shop.OrderTests" name="Pays" time="1"><failure message="boom" type="AssertionFailure">at X</failure></testcase>""";
    private const string Error = """<testcase classname="Shop.OrderTests" name="Ships" time="2"><error message="oops" type="Exception">at Y</error></testcase>""";
    private const string Skip = """<testcase classname="Shop.OrderTests" name="Refunds" time="0"><skipped message="later" /></testcase>""";

    private const string LcovA = "TN:\nSF:src/Calc.cs\nBRDA:3,0,0,1\nBRDA:3,0,1,0\nBRF:2\nBRH:1\nDA:1,1\nDA:2,0\nDA:3,2\nLF:3\nLH:2\nend_of_record\nTN:\nSF:src/Only.cs\nDA:1,5\nLF:1\nLH:1\nend_of_record\n";
    private const string LcovB = "TN:\nSF:src/Calc.cs\nBRDA:3,0,0,0\nBRDA:3,0,1,4\nDA:1,0\nDA:2,0\nDA:3,1\nDA:4,7\nend_of_record\n";

    private static string Cobertura(string module, string filename, string lines, string source = "/ws") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="0.5" lines-covered="1" lines-valid="2" branches-covered="1" branches-valid="2" complexity="0" version="x" timestamp="1">
          <sources><source>{source}</source></sources>
          <packages>
            <package name="{module}" line-rate="0.5" branch-rate="0.5" complexity="0">
              <classes>
                <class name="C" filename="{filename}" line-rate="0.5" branch-rate="0.5" complexity="0">
                  <methods><method name="M" signature="()" line-rate="1" branch-rate="1"><lines><line number="1" hits="99" branch="false" /></lines></method></methods>
                  <lines>{lines}</lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    // ------------------------------------------------------------------ command line

    [TestMethod]
    public void Merge_arguments_parse()
    {
        var o = CliMerge.Parse(["--export", "junit=out/r.xml", "--export=LCOV=cov.info", "a.xml", "b/*.xml"], out var error)!;
        Assert.IsNull(error);
        CollectionAssert.AreEqual(new[] { new ExportTarget("junit", "out/r.xml"), new ExportTarget("lcov", "cov.info") }, o.Exports.ToArray());
        CollectionAssert.AreEqual(new[] { "a.xml", "b/*.xml" }, o.Inputs.ToArray());
        CollectionAssert.AreEqual(new[] { "-x.xml" }, CliMerge.Parse(["--export", "junit=r.xml", "--", "-x.xml"], out _)!.Inputs.ToArray());
        Assert.IsTrue(CliMerge.Parse(["--help"], out _)!.Help);

        string Error(params string[] args)
        {
            Assert.IsNull(CliMerge.Parse(args, out var e));
            return e!;
        }
        Assert.Contains("--export needs format=file", Error("--export", "junit", "a.xml"));
        Assert.Contains("--export needs format=file", Error("--export"));
        Assert.Contains("cannot write 'html'", Error("--export", "html=x.html", "a.xml"));
        Assert.Contains("unknown option '--run'", Error("--run", "a.xml"));
        Assert.Contains("needs at least one --export", Error("a.xml"));
        Assert.Contains("needs the report files", Error("--export", "junit=r.xml"));
        Assert.Contains("labcoat merge --export", Cli.Usage);
    }

    [TestMethod]
    public void Merge_is_only_the_first_argument_and_not_a_path_named_merge()
    {
        Assert.IsTrue(CliMerge.IsMerge(["merge", "--export", "junit=x.xml", "a.xml"]));
        Assert.IsFalse(CliMerge.IsMerge(["src", "merge"]));
        Assert.IsFalse(CliMerge.IsMerge(["--run", "merge"]));
        Assert.IsFalse(CliMerge.IsMerge([]));
        var cwd = Directory.GetCurrentDirectory();
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, "merge"));
        try
        {
            Directory.SetCurrentDirectory(dir);
            Assert.IsFalse(CliMerge.IsMerge(["merge"]), "a directory called merge stays a start path");
        }
        finally { Directory.SetCurrentDirectory(cwd); }
    }

    [TestMethod]
    public void Glob_matches_the_file_name_part()
    {
        Assert.IsTrue(CliMerge.GlobMatch("results-1.xml", "results-*.xml"));
        Assert.IsTrue(CliMerge.GlobMatch("a.xml", "?.xml"));
        Assert.IsTrue(CliMerge.GlobMatch("results.xml", "*"));
        Assert.IsTrue(CliMerge.GlobMatch("abcabc", "a*c"));
        Assert.IsFalse(CliMerge.GlobMatch("ab.xml", "?.xml"));
        Assert.IsFalse(CliMerge.GlobMatch("results.xml.bak", "*.xml"));
        Assert.IsTrue(CliMerge.GlobMatch("RESULTS.XML", "results*", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ JUnit

    [TestMethod]
    public async Task Junit_shards_merge_into_one_valid_report_and_fail_the_exit_code()
    {
        var dir = NewDir();
        var a = Write(dir, "shard-1.xml", JUnit("Shop.Tests", Pass + "\n" + Fail));
        var b = Write(dir, "shard-2.xml", JUnit("Shop.Tests", PassB + "\n" + Skip, "2026-10-11T07:59:00"));
        var c = Write(dir, "shard-3.xml", JUnit("Shop.Tests", Error));
        var d = Write(dir, "shard-3b.xml", JUnit("Billing.Tests", Pass));
        var output = Path.Combine(dir, "out", "results.xml");

        var (code, log) = await Merge("--export", $"junit={output}", a, b, c, d);

        Assert.AreEqual(CliMerge.ExitFailed, code, log);
        var doc = ExportTests.ValidJUnit(File.ReadAllText(output));
        var root = doc.Root!;
        Assert.AreEqual(("6", "1", "1"), (Attr(root, "tests"), Attr(root, "failures"), Attr(root, "errors")));
        Assert.AreEqual("4.25", Attr(root, "time"));
        Assert.AreEqual("Labcoat shop", Attr(root, "name"));
        var suites = root.Elements("testsuite").ToList();
        Assert.HasCount(2, suites);
        var shop = suites[0];
        Assert.AreEqual(("Shop.Tests", "5", "1", "1", "1"), (Attr(shop, "name"), Attr(shop, "tests"), Attr(shop, "failures"), Attr(shop, "errors"), Attr(shop, "skipped")));
        Assert.AreEqual("2026-10-11T07:59:00", Attr(shop, "timestamp"), "the earliest start");
        CollectionAssert.AreEqual(new[] { "Adds", "Pays", "Removes", "Refunds", "Ships" }, shop.Elements("testcase").Select(t => Attr(t, "name")).ToArray());
        CollectionAssert.AreEqual(new[] { "0", "1" }, suites.Select(s => Attr(s, "id")).ToArray());
        var properties = shop.Element("properties")!.Elements("property").ToDictionary(p => Attr(p, "name"), p => Attr(p, "value"));
        Assert.AreEqual("1.1.1", properties["labcoat.version"]);
        Assert.AreEqual("3", properties["labcoat.mergedFrom"]);
        Assert.AreEqual("1", suites[1].Element("properties")!.Elements("property").Single(p => Attr(p, "name") == "labcoat.mergedFrom").Attribute("value")!.Value);
        Assert.Contains("Tests: 2 failed, 3 passed, 1 skipped (6 total) in 2 suites", log.Replace("\u001b", ""));
        Assert.Contains("Read 4 junit", log);
        Assert.Contains("Wrote junit:", log);
    }

    [TestMethod]
    public async Task Passing_shards_exit_zero_and_a_merged_report_can_be_merged_again()
    {
        var dir = NewDir();
        var a = Write(dir, "a.xml", JUnit("Shop.Tests", Pass));
        var b = Write(dir, "b.xml", JUnit("Shop.Tests", PassB + Skip));
        var first = Path.Combine(dir, "merged.xml");
        Assert.AreEqual(CliMerge.ExitPassed, (await Merge("--export", $"junit={first}", a, b)).Code);
        var other = Write(dir, "c.xml", JUnit("Shop.Tests", """<testcase classname="Shop.X" name="Y" time="1" />"""));
        var second = Path.Combine(dir, "again.xml");
        var (code, log) = await Merge("--export", $"junit={second}", first, other);
        Assert.AreEqual(CliMerge.ExitPassed, code, log);
        var suite = ExportTests.ValidJUnit(File.ReadAllText(second)).Root!.Element("testsuite")!;
        Assert.AreEqual("4", Attr(suite, "tests"));
        Assert.AreEqual("3", suite.Element("properties")!.Elements("property").Single(p => Attr(p, "name") == "labcoat.mergedFrom").Attribute("value")!.Value);
    }

    [TestMethod]
    public async Task A_test_seen_twice_keeps_the_last_one_and_warns()
    {
        var dir = NewDir();
        var first = Write(dir, "1.xml", JUnit("Shop.Tests", Fail + Pass));
        var retry = Write(dir, "2.xml", JUnit("Shop.Tests", """<testcase classname="Shop.OrderTests" name="Pays" time="3" />"""));
        var output = Path.Combine(dir, "r.xml");

        var (code, log) = await Merge("--export", $"junit={output}", first, retry);

        Assert.AreEqual(CliMerge.ExitPassed, code, log);
        Assert.Contains("Test 'Shop.OrderTests.Pays' of suite 'Shop.Tests'", log);
        Assert.Contains("keeping the one of", log);
        var suite = ExportTests.ValidJUnit(File.ReadAllText(output)).Root!.Element("testsuite")!;
        Assert.AreEqual("2", Attr(suite, "tests"));
        Assert.AreEqual("0", Attr(suite, "failures"));
        Assert.AreEqual("3.5", Attr(suite, "time"));
        Assert.AreEqual("Pays", Attr(suite.Elements("testcase").First(), "name"), "keeps the position of the first");
    }

    [TestMethod]
    public async Task Windows_line_endings_and_a_bom_are_fine()
    {
        var dir = NewDir();
        var crlf = Write(dir, "crlf.xml", JUnit("Shop.Tests", Pass), "\r\n");
        var bom = Path.Combine(dir, "bom.xml");
        File.WriteAllText(bom, JUnit("Billing.Tests", PassB), new System.Text.UTF8Encoding(true));
        var lcov = Write(dir, "crlf.info", LcovA, "\r\n");
        var output = Path.Combine(dir, "r.xml");
        var cov = Path.Combine(dir, "cov.info");

        var (code, log) = await Merge("--export", $"junit={output}", "--export", $"lcov={cov}", crlf, bom, lcov);

        Assert.AreEqual(CliMerge.ExitPassed, code, log);
        Assert.AreEqual("2", Attr(ExportTests.ValidJUnit(File.ReadAllText(output)).Root!, "tests"));
        var text = File.ReadAllText(cov);
        Assert.DoesNotContain("\r", text, "written with \\n");
        Assert.Contains("SF:src/Calc.cs\n", text);
    }

    // ------------------------------------------------------------------ coverage

    [TestMethod]
    public async Task Lcov_shards_sum_hits_and_recompute_the_totals()
    {
        var dir = NewDir();
        var a = Write(dir, "a.info", LcovA);
        var b = Write(dir, "b.info", LcovB);
        var output = Path.Combine(dir, "merged.info");

        var (code, log) = await Merge("--export", $"lcov={output}", a, b);

        Assert.AreEqual(CliMerge.ExitPassed, code, log);
        var expected = "TN:\nSF:src/Calc.cs\nBRDA:3,0,0,1\nBRDA:3,0,1,4\nBRF:2\nBRH:2\nDA:1,1\nDA:2,0\nDA:3,3\nDA:4,7\nLF:4\nLH:3\nend_of_record\n" +
                       "TN:\nSF:src/Only.cs\nBRF:0\nBRH:0\nDA:1,5\nLF:1\nLH:1\nend_of_record\n";
        Assert.AreEqual(expected, File.ReadAllText(output).ReplaceLineEndings("\n"));
        Assert.Contains("Coverage: 80.0% of lines (4/5), 100.0% of branches (2/2) in 2 files", log);
        Assert.Contains("Read 2 lcov", log);
    }

    [TestMethod]
    public async Task Cobertura_shards_union_packages_and_sum_hits()
    {
        var dir = NewDir();
        var a = Write(dir, "a.xml", Cobertura("Lib", "src/Calc.cs", """<line number="1" hits="1" branch="false" /><line number="2" hits="0" branch="true" condition-coverage="50% (1/2)" />"""));
        var b = Write(dir, "b.xml", Cobertura("Lib", "src/Calc.cs", """<line number="2" hits="3" branch="true" condition-coverage="100% (2/2)" /><line number="5" hits="0" branch="false" />"""));
        var c = Write(dir, "c.xml", Cobertura("App", "src/App.cs", """<line number="1" hits="2" branch="false" />""", "/other"));
        var output = Path.Combine(dir, "merged.xml");

        var (code, log) = await Merge("--export", $"cobertura={output}", a, b, c);

        Assert.AreEqual(CliMerge.ExitPassed, code, log);
        var doc = XDocument.Load(output);
        var root = doc.Root!;
        Assert.AreEqual(("3", "4", "2", "2"), (Attr(root, "lines-covered"), Attr(root, "lines-valid"), Attr(root, "branches-covered"), Attr(root, "branches-valid")));
        Assert.AreEqual("0.75", Attr(root, "line-rate"));
        Assert.AreEqual("1", Attr(root, "branch-rate"));
        CollectionAssert.AreEqual(new[] { "/ws", "/other" }, root.Element("sources")!.Elements("source").Select(s => s.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "App", "Lib" }, root.Descendants("package").Select(p => Attr(p, "name")).ToArray());
        var calc = root.Descendants("class").Single(x => Attr(x, "filename") == "src/Calc.cs");
        var lines = calc.Element("lines")!.Elements("line").ToDictionary(l => Attr(l, "number"));
        CollectionAssert.AreEqual(new[] { "1", "2", "5" }, lines.Keys.ToArray(), "the lines of <methods> are not counted again");
        Assert.AreEqual("1", Attr(lines["1"], "hits"));
        Assert.AreEqual(("3", "true", "100% (2/2)"), (Attr(lines["2"], "hits"), Attr(lines["2"], "branch"), Attr(lines["2"], "condition-coverage")));
        Assert.AreEqual("0.6667", Attr(calc, "line-rate"));
        Assert.Contains("75.0% of lines (3/4)", log);
    }

    [TestMethod]
    public async Task Lcov_and_cobertura_merge_into_either_format()
    {
        var dir = NewDir();
        var lcov = Write(dir, "a.info", "SF:src/Calc.cs\nDA:1,1\nDA:2,0\nBRDA:2,0,0,-\nBRDA:2,0,1,-\nend_of_record\n");
        var cobertura = Write(dir, "b.xml", Cobertura("Lib", "src/Calc.cs", """<line number="1" hits="0" branch="false" /><line number="2" hits="2" branch="true" condition-coverage="50% (1/2)" />"""));
        var info = Path.Combine(dir, "m.info");
        var xml = Path.Combine(dir, "m.xml");

        var (code, log) = await Merge("--export", $"lcov={info}", "--export", $"cobertura={xml}", lcov, cobertura);

        Assert.AreEqual(CliMerge.ExitPassed, code, log);
        var text = File.ReadAllText(info).ReplaceLineEndings("\n");
        Assert.Contains("BRDA:2,0,0,2\nBRDA:2,0,1,0\n", text, "the first branch is taken in the Cobertura file, the second is not");
        Assert.Contains("DA:1,1\nDA:2,2\nLF:2\nLH:2\n", text);
        var root = XDocument.Load(xml).Root!;
        Assert.AreEqual("1", Attr(root, "line-rate"));
        Assert.AreEqual("0.5", Attr(root, "branch-rate"));
        Assert.AreEqual("Lib", Attr(root.Descendants("package").Single(), "name"));
    }

    [TestMethod]
    public async Task A_pattern_in_the_file_name_selects_the_inputs_and_never_the_outputs()
    {
        var dir = NewDir();
        Write(dir, "cov-2.info", LcovB);
        Write(dir, "cov-1.info", LcovA);
        Write(dir, "other.txt", "not a report");
        var output = Path.Combine(dir, "cov-merged.info");

        var (code, log) = await Merge("--export", $"lcov={output}", Path.Combine(dir, "cov-*.info"));
        Assert.AreEqual(CliMerge.ExitPassed, code, log);
        Assert.Contains("merge: 2 files", log);
        var again = await Merge("--export", $"lcov={output}", Path.Combine(dir, "cov-*.info"));
        Assert.AreEqual(CliMerge.ExitPassed, again.Code, "the output of the last run is not an input of this one");
        Assert.Contains("merge: 2 files", again.Log);
    }

    // ------------------------------------------------------------------ errors

    [TestMethod]
    public async Task Missing_unreadable_and_unusable_inputs_exit_with_2()
    {
        var dir = NewDir();
        var good = Write(dir, "good.xml", JUnit("Shop.Tests", Pass));
        var output = Path.Combine(dir, "r.xml");

        var (code, log) = await Merge("--export", $"junit={output}", good, Path.Combine(dir, "missing.xml"));
        Assert.AreEqual(CliMerge.ExitError, code);
        Assert.Contains("Input not found:", log);
        Assert.IsFalse(File.Exists(output), "nothing is written when an input is missing");

        (code, log) = await Merge("--export", $"junit={output}", Path.Combine(dir, "nothing-*.xml"));
        Assert.AreEqual(CliMerge.ExitError, code);
        Assert.Contains("No file matches", log);

        (code, log) = await Merge("--export", $"junit={output}", Path.Combine(dir, "*", "r.xml"));
        Assert.AreEqual(CliMerge.ExitError, code);
        Assert.Contains("only supported in the file name", log);

        (code, log) = await Merge("--export", $"junit={output}", dir);
        Assert.AreEqual(CliMerge.ExitError, code);
        Assert.Contains("is a directory", log);

        var text = Write(dir, "notes.txt", "hello");
        (code, log) = await Merge("--export", $"junit={output}", good, text);
        Assert.AreEqual(CliMerge.ExitError, code);
        Assert.Contains("Could not read", log);
        Assert.Contains("not a JUnit, lcov or Cobertura report", log);

        var html = Write(dir, "page.xml", "<html><body/></html>");
        Assert.AreEqual(CliMerge.ExitError, (await Merge("--export", $"junit={output}", html)).Code);
        var broken = Write(dir, "broken.xml", "<testsuites><testsuite name=\"a\">");
        Assert.AreEqual(CliMerge.ExitError, (await Merge("--export", $"junit={output}", broken)).Code);
        var empty = Write(dir, "empty.info", "");
        Assert.Contains("is empty", (await Merge("--export", $"junit={output}", empty)).Log);
        var badLcov = Write(dir, "bad.info", "SF:a.cs\nDA:x,1\nend_of_record\n");
        (code, log) = await Merge("--export", $"lcov={Path.Combine(dir, "c.info")}", badLcov);
        Assert.AreEqual(CliMerge.ExitError, code);
        Assert.Contains("line 2", log);

        // a format that none of the inputs can provide, and usage errors
        (code, log) = await Merge("--export", $"lcov={Path.Combine(dir, "c.info")}", good);
        Assert.AreEqual(CliMerge.ExitError, code);
        Assert.Contains("none of the inputs is an lcov or Cobertura file", log);
        Assert.AreEqual(CliMerge.ExitError, (await Merge("--export", $"junit={output}")).Code);
        Assert.AreEqual(CliMerge.ExitError, (await Merge(good)).Code);
        Assert.AreEqual(CliMerge.ExitError, (await Merge("--nope", good)).Code);
    }

    [TestMethod]
    public async Task An_output_that_cannot_be_written_exits_with_1()
    {
        var dir = NewDir();
        var good = Write(dir, "good.xml", JUnit("Shop.Tests", Pass));
        var blocker = Write(dir, "file", "x");
        var (code, log) = await Merge("--export", $"junit={Path.Combine(blocker, "r.xml")}", good);
        Assert.AreEqual(CliMerge.ExitFailed, code, log);
        Assert.Contains("Could not write", log);
    }

    [TestMethod]
    public async Task Help_prints_the_usage()
    {
        var (code, log) = await Merge("--help");
        Assert.AreEqual(0, code);
        Assert.Contains("Merge shard reports", log);
    }

    [TestMethod]
    public void Exit_codes_follow_the_tests_and_the_writes()
    {
        Assert.AreEqual(CliMerge.ExitPassed, CliMerge.ExitCode(null, true));
        Assert.AreEqual(CliMerge.ExitPassed, CliMerge.ExitCode(new JUnitTotals(1, 3, 0, 0, 3, 1), true));
        Assert.AreEqual(CliMerge.ExitFailed, CliMerge.ExitCode(new JUnitTotals(1, 3, 1, 0, 0, 1), true));
        Assert.AreEqual(CliMerge.ExitFailed, CliMerge.ExitCode(new JUnitTotals(1, 3, 0, 1, 0, 1), true));
        Assert.AreEqual(CliMerge.ExitFailed, CliMerge.ExitCode(null, false));
    }

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";
}
