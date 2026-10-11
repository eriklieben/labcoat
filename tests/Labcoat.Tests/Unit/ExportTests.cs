using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The export writers (Export/): JUnit XML (validated against the Jenkins / Surefire junit-10.xsd in Fixtures/JUnit),
/// the HTML report (escaping, CSP, caps), lcov and Cobertura, and the text helpers they share.
/// </summary>
[TestClass]
public partial class ExportTests
{
    private const string Xss = "</script><script>alert(1)</script><img src=x onerror=alert(2)>";
    private static readonly string Controls = "bell\a esc\u001b[31mred\u001b[0m nul\0 vt\v ff\f lone\ud800 pair\U0001F600 ffff\uffff end";

    private static ExportTest T(string id, Outcome outcome, string? name = null, string? cls = "CartTests", string? ns = "Shop",
        string? message = null, string? type = null, string? stack = null, string? stdout = null, string? stderr = null,
        double? ms = 12, bool quarantined = false, string project = "Shop.Tests") => new()
    {
        ProjectId = project, Id = id, Name = name ?? id, Namespace = ns, ClassName = cls, Outcome = outcome, DurationMs = ms,
        ErrorMessage = message, ErrorType = type, StackTrace = stack, StdOut = stdout, StdErr = stderr, Quarantined = quarantined,
    };

    private static RunExport Export(params ExportTest[] tests) => new()
    {
        WorkspaceName = "shop",
        Version = "9.9.9",
        Run = new RunSummary { RunId = "run-1", StartedAt = "2026-10-11T08:15:30.123+00:00", FinishedAt = "2026-10-11T08:15:40Z", DurationMs = 10_500, Trigger = "cli", Scope = "all" },
        Projects = tests.Select(t => t.ProjectId).Distinct().Select(p => new ExportProject(p, p, "net10.0", $"tests/{p}/{p}.csproj")).ToList(),
        Tests = [.. tests],
        GeneratedAt = "2026-10-11T08:16:00Z",
    };

    private static RunExport Sample() => Export(
        T("p1", Outcome.Passed, "Add_Works", stdout: "hello"),
        T("d1", Outcome.Passed, "Add (1,2,3)", cls: "MathTests"),
        T("d2", Outcome.Failed, "Add (2,2,5)", cls: "MathTests", message: "Assert.AreEqual failed. Expected:<5>. Actual:<4>.", type: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException", stack: "at MathTests.Add() in MathTests.cs:line 9"),
        T("e1", Outcome.Failed, "Throws", message: "Test method threw exception: System.InvalidOperationException: nope", type: "System.InvalidOperationException", stack: "at Cart.Pay()"),
        T("t1", Outcome.TimedOut, "Slow", message: null),
        T("s1", Outcome.Skipped, "Later", message: "Not ready yet"),
        T("c1", Outcome.Cancelled, "Cancelled"),
        T("q1", Outcome.Failed, "Flaky", message: "boom", quarantined: true),
        T("x1", Outcome.Passed, "Other", cls: null, ns: null, project: "Other.Tests"));

    // ------------------------------------------------------------------ text helpers

    [TestMethod]
    public void Clean_strips_ansi_and_replaces_what_xml_cannot_carry()
    {
        var clean = ExportText.Clean(Controls);
        Assert.AreEqual("bell\uFFFD escred nul\uFFFD vt\uFFFD ff\uFFFD lone\uFFFD pair\U0001F600 ffff\uFFFD end", clean);
        Assert.AreEqual("", ExportText.Clean(null));
        Assert.AreEqual("tab\tlf\ncr\r", ExportText.Clean("tab\tlf\ncr\r"));
        Assert.AreEqual("link", ExportText.StripAnsi("\u001b]8;;http://x\u0007link\u001b]8;;\u0007"));
        Assert.AreSame("plain", ExportText.StripAnsi("plain"));
    }

    [TestMethod]
    public void Cap_keeps_head_and_tail_and_never_splits_a_surrogate_pair()
    {
        Assert.AreEqual("short", ExportText.Cap("short", 100));
        var s = new string('a', 1000) + new string('b', 1000);
        var capped = ExportText.Cap(s, 400);
        Assert.StartsWith(new string('a', 100), capped);
        Assert.EndsWith(new string('b', 300), capped);
        Assert.Contains("1,600 characters omitted by Labcoat", capped);
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 100));
        var cut = ExportText.Cap(emoji, 41);
        Assert.AreEqual(cut, ExportText.Clean(cut), "no lone surrogate at either cut");
    }

    [TestMethod]
    public void Html_escapes_markup_and_quotes()
    {
        Assert.AreEqual("&lt;/script&gt;&lt;script&gt;alert(1)&lt;/script&gt;&lt;img src=x onerror=alert(2)&gt;", ExportText.Html(Xss));
        Assert.AreEqual("a &amp; &quot;b&quot; &#39;c&#39;", ExportText.Html("a & \"b\" 'c'"));
        Assert.AreEqual("plain", ExportText.Html("plain"));
    }

    [TestMethod]
    public void Slugs_and_file_names()
    {
        Assert.AreEqual("my-shop-tests", ExportText.Slug(" My Shop/Tests! "));
        Assert.AreEqual("workspace", ExportText.Slug("  "));
        Assert.AreEqual("run", ExportText.Slug("../..", "run"));
        Assert.HasCount(60, ExportText.Slug(new string('x', 100)).ToCharArray());
        Assert.AreEqual("labcoat-shop-r1.junit.xml", ExportFormats.FileName("Shop", "r1", ExportFormats.JUnit));
        Assert.AreEqual("labcoat-shop-r1.html", ExportFormats.FileName("Shop", "r1", ExportFormats.Html));
        Assert.AreEqual("labcoat-shop-r1.info", ExportFormats.FileName("Shop", "r1", ExportFormats.Lcov));
        Assert.AreEqual("labcoat-shop-r1.cobertura.xml", ExportFormats.FileName("Shop", "r1", ExportFormats.Cobertura));
        Assert.AreEqual("text/html; charset=utf-8", ExportFormats.ContentType(ExportFormats.Html));
        Assert.AreEqual("text/plain; charset=utf-8", ExportFormats.ContentType(ExportFormats.Lcov));
        Assert.AreEqual("", ExportText.FirstLine(null));
        Assert.AreEqual("first", ExportText.FirstLine("first\nsecond"));
        Assert.AreEqual("ab…", ExportText.FirstLine("abc", 2));
    }

    [TestMethod]
    public void Kinds_tell_assertion_failures_from_errors()
    {
        Assert.AreEqual(ExportKind.Failure, T("a", Outcome.Failed).Kind); // unknown type: an assertion
        Assert.AreEqual(ExportKind.Failure, T("a", Outcome.Failed, type: "Xunit.Sdk.EqualException").Kind);
        Assert.AreEqual(ExportKind.Failure, T("a", Outcome.Failed, type: "NUnit.Framework.MultipleAssertException").Kind);
        Assert.AreEqual(ExportKind.Failure, T("a", Outcome.Failed, type: "Shouldly.ShouldAssertException").Kind);
        Assert.AreEqual(ExportKind.Failure, T("a", Outcome.Failed, type: "VerifyException").Kind);
        Assert.AreEqual(ExportKind.Error, T("a", Outcome.Failed, type: "System.NullReferenceException").Kind);
        Assert.AreEqual(ExportKind.Error, T("a", Outcome.Failed, type: Explorer.TestHostCrashedErrorType).Kind);
        Assert.AreEqual(ExportKind.Error, T("a", Outcome.TimedOut).Kind);
        Assert.AreEqual(ExportKind.QuarantinedFailure, T("a", Outcome.TimedOut, quarantined: true).Kind);
        Assert.AreEqual(ExportKind.Passed, T("a", Outcome.Passed, quarantined: true).Kind);
        Assert.AreEqual(ExportKind.NotRun, T("a", Outcome.NotRun).Kind);
        Assert.AreEqual("Not run", ExportKinds.SkipReason(T("a", Outcome.NotRun)));
        Assert.AreEqual("Cancelled", ExportKinds.SkipReason(T("a", Outcome.Cancelled)));
        Assert.AreEqual("Skipped", ExportKinds.SkipReason(T("a", Outcome.Skipped)));
        Assert.AreEqual("", T("a", Outcome.Passed, cls: null, ns: null).ClassFullName);
        Assert.AreEqual("Shop", T("a", Outcome.Passed, cls: null).ClassFullName);
        Assert.AreEqual("CartTests", T("a", Outcome.Passed, ns: null).ClassFullName);
    }

    // ------------------------------------------------------------------ JUnit

    /// <summary>Validates against the Jenkins xunit plugin's junit-10.xsd; returns the parsed document.</summary>
    internal static XDocument ValidJUnit(string xml)
    {
        var schemas = new XmlSchemaSet();
        schemas.Add(null, Path.Combine(AppContext.BaseDirectory, "Fixtures", "JUnit", "junit-10.xsd"));
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Severity}: {e.Message}");
        using (var reader = XmlReader.Create(new StringReader(xml), settings))
            while (reader.Read()) { /* validation happens while reading */ }
        Assert.IsEmpty(errors, string.Join("\n", errors));
        return XDocument.Parse(xml);
    }

    [TestMethod]
    public void JUnit_is_valid_and_maps_every_outcome()
    {
        var doc = ValidJUnit(JUnitWriter.ToXml(Sample()));
        var root = doc.Root!;
        Assert.AreEqual("testsuites", root.Name.LocalName);
        Assert.AreEqual(("9", "1", "2", "10.5"), ((string)root.Attribute("tests")!, (string)root.Attribute("failures")!, (string)root.Attribute("errors")!, (string)root.Attribute("time")!));
        var suites = root.Elements("testsuite").ToList();
        Assert.HasCount(2, suites);
        var shop = suites[0];
        Assert.AreEqual(("Shop.Tests", "8", "1", "2", "3"), ((string)shop.Attribute("name")!, (string)shop.Attribute("tests")!,
            (string)shop.Attribute("failures")!, (string)shop.Attribute("errors")!, (string)shop.Attribute("skipped")!));
        Assert.AreEqual("2026-10-11T08:15:30", (string)shop.Attribute("timestamp")!);
        var props = shop.Element("properties")!.Elements("property").ToDictionary(p => (string)p.Attribute("name")!, p => (string)p.Attribute("value")!);
        Assert.AreEqual("9.9.9", props["labcoat.version"]);
        Assert.AreEqual("run-1", props["labcoat.runId"]);
        Assert.AreEqual("cli", props["labcoat.trigger"]);
        Assert.AreEqual("net10.0", props["targetFramework"]);
        Assert.AreEqual("tests/Shop.Tests/Shop.Tests.csproj", props["labcoat.project"]);

        XElement Case(string name) => root.Descendants("testcase").Single(c => (string)c.Attribute("name")! == name);
        // data rows are separate test cases with their display names
        Assert.AreEqual("Shop.MathTests", (string)Case("Add (1,2,3)").Attribute("classname")!);
        Assert.IsFalse(Case("Add (1,2,3)").HasElements);
        var failure = Case("Add (2,2,5)").Element("failure")!;
        Assert.AreEqual("Assert.AreEqual failed. Expected:<5>. Actual:<4>.", (string)failure.Attribute("message")!);
        Assert.AreEqual("Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException", (string)failure.Attribute("type")!);
        Assert.AreEqual("at MathTests.Add() in MathTests.cs:line 9", failure.Value);
        Assert.AreEqual("0.012", (string)Case("Add (2,2,5)").Attribute("time")!);
        var error = Case("Throws").Element("error")!;
        Assert.AreEqual("System.InvalidOperationException", (string)error.Attribute("type")!);
        var timeout = Case("Slow").Element("error")!;
        Assert.AreEqual(("Timed out", "Timeout"), ((string)timeout.Attribute("message")!, (string)timeout.Attribute("type")!));
        Assert.AreEqual("Not ready yet", (string)Case("Later").Element("skipped")!.Attribute("message")!);
        Assert.AreEqual("Cancelled", (string)Case("Cancelled").Element("skipped")!.Attribute("message")!);
        Assert.AreEqual("Quarantined; failed: boom", (string)Case("Flaky").Element("skipped")!.Attribute("message")!);
        Assert.AreEqual("hello", Case("Add_Works").Element("system-out")!.Value);
        Assert.AreEqual("Other.Tests", (string)Case("Other").Attribute("classname")!, "no class: the project name");
    }

    [TestMethod]
    public void JUnit_escapes_names_and_output_and_drops_invalid_characters()
    {
        var x = JUnitWriter.ToXml(Export(T("x", Outcome.Failed, name: Xss + "]]>", cls: "C<&>\"", message: Xss + Controls, stack: Controls, stdout: Xss + Controls, stderr: "err\u0001")));
        var doc = ValidJUnit(x);
        var c = doc.Descendants("testcase").Single();
        Assert.AreEqual(Xss + "]]>", (string)c.Attribute("name")!);
        Assert.AreEqual("Shop.C<&>\"", (string)c.Attribute("classname")!);
        Assert.AreEqual(Xss + ExportText.Clean(Controls), c.Element("system-out")!.Value);
        Assert.AreEqual("err\uFFFD", c.Element("system-err")!.Value);
        Assert.DoesNotContain("<script>", x);
        Assert.DoesNotContain("\u001b", x);
    }

    [TestMethod]
    public void JUnit_caps_long_output_and_the_whole_reports_output_budget()
    {
        var big = new string('o', 100_000);
        var x = JUnitWriter.ToXml(Export(T("x", Outcome.Failed, message: new string('m', 10_000), stack: new string('s', 100_000), stdout: big)));
        var c = ValidJUnit(x).Descendants("testcase").Single();
        Assert.IsLessThan(JUnitWriter.MaxOutputChars + 100, c.Element("system-out")!.Value.Length);
        Assert.Contains("characters omitted by Labcoat", c.Element("system-out")!.Value);
        Assert.IsLessThan(JUnitWriter.MaxMessageChars + 100, ((string)c.Element("failure")!.Attribute("message")!).Length);
        Assert.IsLessThan(JUnitWriter.MaxStackChars + 100, c.Element("failure")!.Value.Length);

        // the budget: failed tests first, then the others in order, while it lasts
        var tests = new[]
        {
            T("p1", Outcome.Passed, stdout: new string('a', 60)),
            T("p2", Outcome.Passed, stdout: new string('b', 30), stderr: new string('c', 30)),
            T("f1", Outcome.Failed, stdout: new string('d', 60)),
            T("p3", Outcome.Passed),
        };
        var chosen = JUnitWriter.OutputBudget(tests, total: 130, perStream: 100);
        CollectionAssert.AreEquivalent(new[] { "f1", "p1" }, chosen.Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public void JUnit_of_an_empty_run_is_valid()
    {
        var doc = ValidJUnit(JUnitWriter.ToXml(Export()));
        Assert.AreEqual("0", (string)doc.Root!.Attribute("tests")!);
        Assert.IsFalse(doc.Root.HasElements);
        Assert.AreEqual("0", JUnitWriter.Seconds(-5));
        Assert.AreEqual("1.235", JUnitWriter.Seconds(1234.5));
        Assert.AreEqual("not a date", JUnitWriter.Timestamp("not a date"));
    }

    [TestMethod]
    public void JUnit_marks_aborted_and_cancelled_runs()
    {
        var export = Export(T("a", Outcome.Passed)) with
        {
            Run = new RunSummary { RunId = "r", StartedAt = "2026-01-01T00:00:00Z", Aborted = true, AbortReason = "Test host exited", Cancelled = true },
        };
        var props = ValidJUnit(JUnitWriter.ToXml(export)).Descendants("property").ToDictionary(p => (string)p.Attribute("name")!, p => (string)p.Attribute("value")!);
        Assert.AreEqual("Test host exited", props["labcoat.aborted"]);
        Assert.AreEqual("true", props["labcoat.cancelled"]);
        Assert.AreEqual("user", props["labcoat.trigger"]);
        Assert.IsFalse(props.ContainsKey("labcoat.finishedAt"));
    }

    // ------------------------------------------------------------------ HTML

    [GeneratedRegex("<script>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ScriptRe();

    [GeneratedRegex("<style>(.*?)</style>", RegexOptions.Singleline)]
    private static partial Regex StyleRe();

    [TestMethod]
    public void Html_report_is_self_contained_and_escapes_everything()
    {
        var html = HtmlReport.ToHtml(Export(
            T("x", Outcome.Failed, name: Xss, cls: Xss, message: Xss + Controls, stack: Xss, stdout: Xss, stderr: Xss),
            T("y", Outcome.Skipped, name: "skip " + Xss, message: Xss),
            T("z", Outcome.Passed, name: "ok", project: Xss)));
        // exactly one script and one style: the report's own (test data never adds markup)
        var scripts = ScriptRe().Matches(html);
        Assert.HasCount(1, scripts);
        Assert.AreEqual(HtmlReport.ReportJs, scripts[0].Groups[1].Value);
        Assert.HasCount(1, StyleRe().Matches(html));
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("alert(1)</script>", html);
        Assert.Contains("&lt;/script&gt;&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("\u001b", html);
        Assert.DoesNotContain("\0", html);
        // the CSP allows exactly that script and style, nothing external
        var style = StyleRe().Match(html).Groups[1].Value;
        Assert.Contains($"script-src 'sha256-{SecurityHeaders.ScriptHash(HtmlReport.ReportJs)}'", html);
        Assert.Contains($"style-src 'sha256-{SecurityHeaders.ScriptHash(style)}'", html);
        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("http://", html.Replace("http-equiv", ""));
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("url(", html);
        Assert.DoesNotContain("style=\"", html, "style attributes would be blocked by the CSP");
    }

    [TestMethod]
    public void Html_report_lists_failures_first_with_diff_stack_and_output()
    {
        var html = HtmlReport.ToHtml(Sample());
        var failed = html.IndexOf("id=\"h-failed\"", StringComparison.Ordinal);
        var all = html.IndexOf("id=\"h-all\"", StringComparison.Ordinal);
        Assert.IsTrue(failed > 0 && all > failed);
        Assert.Contains("Failed (3)", html);
        Assert.Contains("3 tests failed", html);
        Assert.Contains("<span class=\"del\">- 5</span><span class=\"add\">+ 4</span>", html);
        Assert.Contains("at MathTests.Add() in MathTests.cs:line 9", html);
        Assert.Contains("Assertion failed · Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException", html);
        Assert.Contains("Error · System.InvalidOperationException", html);
        Assert.Contains(">Timed out</pre>", html);
        // data rows each get a row; passed rows have no output; links to the failure details
        Assert.Contains("<span class=\"n\">Add (1,2,3)</span>", html);
        Assert.Contains("<a class=\"n\" href=\"#f0\">Add (2,2,5)</a>", html);
        Assert.Contains("<span class=\"why\">Not ready yet</span>", html);
        Assert.Contains("<span class=\"why\">Quarantined; failed: boom</span>", html);
        Assert.DoesNotContain(">hello<", html, "passed tests' output is not rendered");
        Assert.Contains("<option value=\"Other.Tests\">Other.Tests</option>", html);
        Assert.Contains("(no class)", html);
        Assert.Contains("<dt>Not run</dt>", html);
        Assert.Contains("<dt>Quarantined</dt>", html);
        Assert.Contains("Started 2026-10-11 08:15 UTC", html);
    }

    [TestMethod]
    public void Html_report_banners_for_passed_empty_aborted_and_cancelled_runs()
    {
        Assert.Contains("All 1 tests passed", HtmlReport.ToHtml(Export(T("a", Outcome.Passed))));
        Assert.Contains("No failures (1 passed)", HtmlReport.ToHtml(Export(T("a", Outcome.Passed), T("b", Outcome.Skipped))));
        var empty = HtmlReport.ToHtml(Export());
        Assert.Contains("No test results in this run", empty);
        Assert.Contains("This run has no test results.", empty);
        Assert.DoesNotContain("id=\"proj\"", empty);
        var aborted = Export(T("a", Outcome.Passed)) with { Run = new RunSummary { RunId = "r", StartedAt = "bad date", Aborted = true, AbortReason = "<host> crashed" } };
        var html = HtmlReport.ToHtml(aborted);
        Assert.Contains("The run was aborted <span class=\"muted\">&lt;host&gt; crashed</span>", html);
        Assert.Contains("Started bad date", html);
        var cancelled = Export(T("a", Outcome.Passed)) with { Run = new RunSummary { RunId = "r", StartedAt = "2026-01-01T00:00:00Z", Cancelled = true, Scope = "subset", Total = 1 } };
        var c = HtmlReport.ToHtml(cancelled);
        Assert.Contains("The run was cancelled", c);
        Assert.Contains("partial run (1 selected)", c);
    }

    [TestMethod]
    public void Html_report_caps_details_for_huge_runs()
    {
        var tests = new List<ExportTest>();
        for (var i = 0; i < 20_000; i++)
            tests.Add(i % 20 == 0
                ? T($"f{i}", Outcome.Failed, $"Fails_{i}", cls: $"C{i / 200}", message: $"first line {i}\nmore", stack: new string('s', 50_000), stdout: new string('o', 50_000))
                : T($"p{i}", Outcome.Passed, $"Passes_{i}", cls: $"C{i / 200}", stdout: new string('o', 1000)));
        var html = HtmlReport.ToHtml(Export([.. tests]));
        Assert.IsLessThan(25 * 1024 * 1024, html.Length, $"{html.Length / 1024 / 1024} MB");
        Assert.HasCount(HtmlReport.MaxDetailedFailures, Regex.Matches(html, "<details class=\"fail\""));
        Assert.Contains("500 more failed tests are listed below", html);
        Assert.Contains("<span class=\"why\">first line 19980</span>", html, "failures beyond the cap show their first message line");
        Assert.Contains("20000 tests", html);
    }

    [TestMethod]
    public void Html_durations_and_theme()
    {
        Assert.AreEqual("12 ms", HtmlReport.Duration(12.4));
        Assert.AreEqual("1.5 s", HtmlReport.Duration(1500));
        Assert.AreEqual("2 min 05 s", HtmlReport.Duration(125_000));
        // the embedded default theme: light on :root, dark under prefers-color-scheme
        var theme = HtmlReport.Theme;
        Assert.Contains("@media (prefers-color-scheme: dark)", theme);
        Assert.DoesNotContain("data-theme", theme);
        Assert.Contains("--lc-status-failed", theme);
        Assert.AreEqual(theme.Count(c => c == '{'), theme.Count(c => c == '}'));
        Assert.DoesNotContain("https://", theme, "comments are stripped");
        Assert.AreEqual("a  b c", HtmlReport.StripComments("a /* x */ b/**/ c/* open"));
        Assert.AreEqual(HtmlReport.FallbackTokens, HtmlReport.ThemeTokens(null));
        Assert.AreEqual(HtmlReport.FallbackTokens, HtmlReport.ThemeTokens(":root { --x: 1 }"));
        Assert.AreEqual(HtmlReport.FallbackTokens, HtmlReport.ThemeTokens(
            ":root[data-theme-family=\"catppuccin\"] { --a: url(x) }\n:root[data-theme-family=\"catppuccin\"][data-theme=\"dark\"] { }"));
    }

    [TestMethod]
    public void Assertion_diff_parses_the_common_forms()
    {
        Assert.AreEqual(new AssertionDiff.Values("10", "9"), AssertionDiff.Parse("Assert.AreEqual failed. Expected:<10>. Actual:<9>. 'expected' expression"));
        Assert.AreEqual(new AssertionDiff.Values("sent", "queued"), AssertionDiff.Parse("Assert failed\nExpected: \"sent\"\nBut was:  \"queued\""));
        Assert.AreEqual(new AssertionDiff.Values("10", "9"), AssertionDiff.Parse("Assert.Equal() Failure\r\nExpected: 10\r\nActual:   9"));
        Assert.IsNull(AssertionDiff.Parse(null));
        Assert.IsNull(AssertionDiff.Parse("Expected:<1"));
        Assert.IsNull(AssertionDiff.Parse("Expected:<1>. Actual:<2"));
        Assert.IsNull(AssertionDiff.Parse("Expected: 1\nno\nno\nno\nActual: 2"));
        var lines = AssertionDiff.Lines("a\nb\nc", "a\nx\nc\nd")!;
        Assert.AreEqual("  a|- b|+ x|  c|+ d", string.Join("|", lines.Select(l => $"{l.Op} {l.Text}")));
        Assert.AreEqual("- a|- b", string.Join("|", AssertionDiff.Lines("a\nb", "")!.Where(l => l.Op != '+').Select(l => $"{l.Op} {l.Text}")));
        Assert.IsNull(AssertionDiff.Lines(string.Join('\n', Enumerable.Range(0, 500)), "x"));
    }

    // ------------------------------------------------------------------ coverage

    private static CoverageReport Coverage(string root) => new()
    {
        RunId = "cov-1",
        GeneratedAt = "2026-10-11T08:00:00Z",
        ProjectIds = ["Shop.Tests"],
        LineRate = 0.75, BranchRate = 0.5, LinesCovered = 3, LinesValid = 4, BranchesCovered = 1, BranchesValid = 2,
        Files =
        [
            new FileCoverage
            {
                Path = Path.Combine(root, "src", "Shop", "Cart.cs"), Module = "Shop", LinesCovered = 2, LinesValid = 3, BranchesCovered = 1, BranchesValid = 2,
                Lines = [new() { Line = 3, Hits = 2 }, new() { Line = 1, Hits = 1, BranchesCovered = 1, BranchesTotal = 2 }, new() { Line = 5, Hits = 0, BranchesCovered = 0, BranchesTotal = 1 }],
            },
            new FileCoverage { Path = "/elsewhere/Gen<&>.cs", Module = "Other", LinesCovered = 1, LinesValid = 1, Lines = [new() { Line = 7, Hits = 4 }] },
        ],
        Settings = [new CoverageSettingsInfo { ProjectId = "Shop.Tests", Source = "defaults", Instrumentation = "dynamic", Exclude = ["*.Generated--x"] }],
        StaleFiles = ["/x"],
        Merged = [new CoverageMergedRun { RunId = "m", GeneratedAt = "2026-10-11T08:00:00Z", ProjectId = "Shop.Tests" }],
    };

    [TestMethod]
    public void Lcov_has_records_with_relative_paths_branches_and_totals()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcoat-lcov-root");
        var lcov = CoverageExport.ToString(ExportFormats.Lcov, Coverage(root), new CoverageExportOptions(root));
        Assert.AreEqual("""
            TN:
            SF:/elsewhere/Gen<&>.cs
            BRF:0
            BRH:0
            DA:7,4
            LF:1
            LH:1
            end_of_record
            TN:
            SF:src/Shop/Cart.cs
            BRDA:1,0,0,1
            BRDA:1,0,1,0
            BRDA:5,0,0,-
            BRF:3
            BRH:1
            DA:1,1
            DA:3,2
            DA:5,0
            LF:3
            LH:2
            end_of_record

            """.Replace("\r\n", "\n"), lcov);
        var absolute = CoverageExport.ToString(ExportFormats.Lcov, Coverage(root), new CoverageExportOptions(root, AbsolutePaths: true));
        Assert.Contains($"SF:{Path.Combine(root, "src", "Shop", "Cart.cs")}\n", absolute);
        Assert.Contains("SF:/elsewhere/Gen<&>.cs", CoverageExport.ToString(ExportFormats.Lcov, Coverage(root), new CoverageExportOptions(null)));
    }

    [TestMethod]
    public void Cobertura_is_well_formed_with_rates_conditions_and_the_settings_note()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcoat-cob-root");
        var xml = CoverageExport.ToString(ExportFormats.Cobertura, Coverage(root), new CoverageExportOptions(root));
        var doc = XDocument.Parse(xml);
        var cov = doc.Root!;
        Assert.AreEqual(("0.75", "0.5", "3", "4"), ((string)cov.Attribute("line-rate")!, (string)cov.Attribute("branch-rate")!, (string)cov.Attribute("lines-covered")!, (string)cov.Attribute("lines-valid")!));
        Assert.AreEqual(root, cov.Element("sources")!.Element("source")!.Value);
        var comment = doc.Nodes().OfType<XComment>().Single().Value;
        Assert.Contains("coverage of run cov-1 (all tests)", comment);
        Assert.Contains("Labcoat's defaults", comment);
        Assert.Contains("1 file(s) changed", comment);
        Assert.Contains("1 later run(s)", comment);
        Assert.DoesNotContain("--", comment);
        var classes = cov.Descendants("class").ToList();
        Assert.AreEqual(("Gen<&>", "/elsewhere/Gen<&>.cs"), ((string)classes[0].Attribute("name")!, (string)classes[0].Attribute("filename")!));
        Assert.AreEqual(("src.Shop.Cart", "src/Shop/Cart.cs"), ((string)classes[1].Attribute("name")!, (string)classes[1].Attribute("filename")!));
        var line1 = classes[1].Descendants("line").First();
        Assert.AreEqual(("1", "true", "50% (1/2)"), ((string)line1.Attribute("number")!, (string)line1.Attribute("branch")!, (string)line1.Attribute("condition-coverage")!));
        Assert.AreEqual("false", (string)classes[1].Descendants("line").ElementAt(1).Attribute("branch")!);
        Assert.AreEqual("1", (string)cov.Descendants("package").Single(p => (string)p.Attribute("name")! == "Other").Attribute("branch-rate")!, "no branches: rate 1");

        var abs = XDocument.Parse(CoverageExport.ToString(ExportFormats.Cobertura, Coverage(root) with { Scope = "subset", GeneratedAt = "bad" }, new CoverageExportOptions(root, true)));
        Assert.IsFalse(abs.Root!.Element("sources")!.HasElements);
        Assert.AreEqual("0", (string)abs.Root.Attribute("timestamp")!);
        Assert.Contains("selected tests", abs.Nodes().OfType<XComment>().Single().Value);
        Assert.Contains("absolute paths", abs.Nodes().OfType<XComment>().Single().Value);
    }
}
