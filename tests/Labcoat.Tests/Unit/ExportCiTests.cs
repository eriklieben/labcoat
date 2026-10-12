using System.Text.Json;
using System.Xml.Linq;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The Markdown, TRX and SARIF exports, the GitHub Actions annotations and step summary of --run, and the source
/// locations they use. Shares the helpers of <see cref="ExportTests"/> (the partial class).
/// </summary>
public partial class ExportTests
{
    private static ExportTest At(ExportTest t, string file, int line) => t with { SourceFile = file, SourceLine = line };

    private static RunExport CiSample() => Export(
        T("p1", Outcome.Passed, "Add_Works", ms: 5),
        T("p2", Outcome.Passed, "Slow_One", ms: 2400),
        At(T("d2", Outcome.Failed, "Add (2,2,5)", cls: "MathTests", message: "Assert.AreEqual failed. Expected:<5>. Actual:<4>.\nsecond line", type: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException", stack: "at MathTests.Add() in /repo/tests/MathTests.cs:line 9"), "tests/MathTests.cs", 9),
        At(T("e1", Outcome.Failed, "Throws", message: "100% broken, see: here", type: "System.InvalidOperationException", stack: "at Cart.Pay()"), "src/Cart.cs", 31),
        T("t1", Outcome.TimedOut, "Slow"),
        T("s1", Outcome.Skipped, "Later", message: "Not ready yet"),
        T("q1", Outcome.Failed, "Flaky", message: "boom", quarantined: true));

    // ------------------------------------------------------------------ source locations

    [TestMethod]
    public void Locations_prefer_the_first_stack_frame_in_the_workspace_over_the_declaration()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcoat-loc");
        var inside = Path.Combine(root, "tests", "MathTests.cs");
        var stack = $"   at Assert.Fail() in /outside/Framework.cs:line 5\n   at MathTests.Add() in {inside}:line 9\n   at Other() in {Path.Combine(root, "x.cs")}:line 1";
        Assert.AreEqual(("tests/MathTests.cs", 9), ExportLocation.Of(stack, "/decl/File.cs", 3, root));
        Assert.AreEqual(("/decl/File.cs", 3), ExportLocation.Of("at Cart.Pay()", "/decl/File.cs", 3, root));
        Assert.AreEqual((null, null), ExportLocation.Of(null, null, null, root));
        Assert.AreEqual(("/outside/Framework.cs", 5), ExportLocation.Of("at A() in /outside/Framework.cs:line 5", null, null, null), "no root: any absolute path");
    }

    // ------------------------------------------------------------------ Markdown

    [TestMethod]
    public void Markdown_lists_totals_failures_first_slowest_and_coverage()
    {
        var md = MarkdownReport.ToMarkdown(CiSample(), Coverage(Path.GetTempPath()));
        Assert.StartsWith("## Test results: 3 failed\n", md);
        Assert.Contains("| Total | Passed | Failed | Skipped | Quarantined | Duration |", md);
        Assert.Contains("| 7 | 2 | 3 | 1 | 1 |", md);
        Assert.IsLessThan(md.IndexOf("### Slowest tests", StringComparison.Ordinal), md.IndexOf("### Failed tests", StringComparison.Ordinal));
        Assert.Contains("**Add (2,2,5)**", md);
        Assert.Contains("(`tests/MathTests.cs:9`)", md);
        Assert.Contains("```text\nAssert.AreEqual failed. Expected:<5>. Actual:<4>.\nsecond line\n```", md);
        Assert.Contains("<details><summary>Stack trace</summary>", md);
        Assert.Contains("| Shop.CartTests.Slow\\_One | 2.4 s |", md);
        Assert.Contains("### Coverage", md);
        Assert.Contains("<sub>Labcoat 9.9.9", md);
    }

    [TestMethod]
    public void Markdown_escapes_markup_and_mentions_and_survives_backticks_in_output()
    {
        var md = MarkdownReport.ToMarkdown(Export(T("x", Outcome.Failed, name: "a|b <i>@octocat</i> `c` *d*", message: "has ``` fence ```", stack: "s")));
        Assert.Contains("**a\\|b \\<i\\>@​octocat\\</i\\> \\`c\\` \\*d\\***", md);
        Assert.Contains("````text\nhas ``` fence ```\n````", md, "a fence longer than any backtick run");
        Assert.DoesNotContain("<i>", md);
    }

    [TestMethod]
    public void Markdown_is_cut_to_its_budget_with_a_note_about_the_failures_left_out()
    {
        var tests = Enumerable.Range(0, 200).Select(i => T($"f{i}", Outcome.Failed, $"Fails_{i}", message: new string('m', 1000))).ToArray();
        var md = MarkdownReport.ToMarkdown(Export(tests), null, 20_000);
        Assert.IsLessThanOrEqualTo(20_000, System.Text.Encoding.UTF8.GetByteCount(md));
        Assert.Contains("Fails\\_0", md);
        Assert.DoesNotContain("Fails\\_199", md);
        Assert.MatchesRegex(@"_\d+ more failed tests not shown here", md);
        Assert.StartsWith("## Test results: 200 failed", md);
        using var ms = new MemoryStream();
        MarkdownReport.Write(Export(tests), ms);
        Assert.IsLessThanOrEqualTo(MarkdownReport.MaxCommentBytes, ms.Length);
    }

    [TestMethod]
    public void Markdown_of_a_passing_or_empty_run()
    {
        Assert.StartsWith("## Test results: All tests passed", MarkdownReport.ToMarkdown(Export(T("p", Outcome.Passed))));
        var empty = MarkdownReport.ToMarkdown(Export());
        Assert.StartsWith("## Test results: No tests ran", empty);
        Assert.DoesNotContain("### Failed tests", empty);
    }

    // ------------------------------------------------------------------ TRX

    [TestMethod]
    public void Trx_has_the_vstest_structure_and_outcomes()
    {
        var doc = XDocument.Parse(TrxWriter.ToXml(CiSample()));
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var root = doc.Root!;
        Assert.AreEqual(ns + "TestRun", root.Name);
        CollectionAssert.AreEqual(new[] { "Times", "TestSettings", "Results", "TestDefinitions", "TestEntries", "TestLists", "ResultSummary" }, root.Elements().Select(e => e.Name.LocalName).ToArray());
        var results = root.Element(ns + "Results")!.Elements(ns + "UnitTestResult").ToList();
        CollectionAssert.AreEqual(new[] { "Passed", "Passed", "Failed", "Failed", "Timeout", "NotExecuted", "NotExecuted" }, results.Select(r => (string)r.Attribute("outcome")!).ToArray());
        var failed = results[2];
        Assert.AreEqual("00:00:00.0120000", (string)failed.Attribute("duration")!);
        Assert.AreEqual("Assert.AreEqual failed. Expected:<5>. Actual:<4>.\nsecond line", failed.Element(ns + "Output")!.Element(ns + "ErrorInfo")!.Element(ns + "Message")!.Value);
        Assert.Contains("MathTests.cs:line 9", failed.Element(ns + "Output")!.Element(ns + "ErrorInfo")!.Element(ns + "StackTrace")!.Value);
        Assert.AreEqual("Not ready yet", results[5].Element(ns + "Output")!.Element(ns + "ErrorInfo")!.Element(ns + "Message")!.Value);

        // every result has a definition and an entry, linked by the same ids
        var testIds = results.Select(r => (string)r.Attribute("testId")!).ToList();
        Assert.AreEqual(7, testIds.Distinct().Count());
        CollectionAssert.AreEqual(testIds, root.Element(ns + "TestDefinitions")!.Elements(ns + "UnitTest").Select(u => (string)u.Attribute("id")!).ToList());
        CollectionAssert.AreEqual(testIds, root.Element(ns + "TestEntries")!.Elements(ns + "TestEntry").Select(u => (string)u.Attribute("testId")!).ToList());
        Assert.AreEqual("Shop.CartTests, Shop.Tests", (string)root.Descendants(ns + "TestMethod").First().Attribute("className")!);
        foreach (var r in results) Assert.IsTrue(System.Guid.TryParse((string)r.Attribute("executionId")!, out _));

        var summary = root.Element(ns + "ResultSummary")!;
        Assert.AreEqual("Failed", (string)summary.Attribute("outcome")!);
        var c = summary.Element(ns + "Counters")!;
        Assert.AreEqual(("7", "5", "2", "2", "1", "2"), ((string)c.Attribute("total")!, (string)c.Attribute("executed")!, (string)c.Attribute("passed")!,
            (string)c.Attribute("failed")!, (string)c.Attribute("timeout")!, (string)c.Attribute("notExecuted")!));
        Assert.AreEqual(TrxWriter.ToXml(CiSample()), TrxWriter.ToXml(CiSample()), "ids are stable for the same run");
        Assert.AreEqual("Completed", (string)XDocument.Parse(TrxWriter.ToXml(Export(T("p", Outcome.Passed)))).Root!.Element(ns + "ResultSummary")!.Attribute("outcome")!);
    }

    [TestMethod]
    public void Trx_is_clean_xml_for_hostile_text()
    {
        var x = TrxWriter.ToXml(Export(T("x", Outcome.Failed, name: Xss + "]]>", cls: "C<&>\"", message: Xss + Controls, stack: Controls, stdout: Controls, stderr: "e\u0001")));
        var doc = XDocument.Parse(x);
        Assert.DoesNotContain("\u001b", x);
        Assert.DoesNotContain("<script>", x);
        Assert.AreEqual(Xss + "]]>", (string)doc.Descendants().First(e => e.Name.LocalName == "UnitTestResult").Attribute("testName")!);
    }

    // ------------------------------------------------------------------ SARIF

    [TestMethod]
    public void Sarif_reports_failures_with_locations_and_quarantined_failures_as_notes()
    {
        using var doc = JsonDocument.Parse(SarifWriter.ToJson(CiSample()));
        var root = doc.RootElement;
        Assert.AreEqual("2.1.0", root.GetProperty("version").GetString());
        Assert.Contains("sarif-2.1.0", root.GetProperty("$schema").GetString()!);
        var run = root.GetProperty("runs")[0];
        var driver = run.GetProperty("tool").GetProperty("driver");
        Assert.AreEqual("Labcoat", driver.GetProperty("name").GetString());
        Assert.AreEqual("9.9.9", driver.GetProperty("version").GetString());
        var ruleIds = driver.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.IsTrue(run.GetProperty("invocations")[0].GetProperty("executionSuccessful").GetBoolean());

        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.HasCount(4, results, "two failures, the timeout and the quarantined failure; no passed or skipped tests");
        foreach (var r in results)
            Assert.AreEqual(r.GetProperty("ruleId").GetString(), ruleIds[r.GetProperty("ruleIndex").GetInt32()]);

        var assertion = results[0];
        Assert.AreEqual(SarifWriter.AssertionRule, assertion.GetProperty("ruleId").GetString());
        Assert.AreEqual("error", assertion.GetProperty("level").GetString());
        Assert.StartsWith("Add (2,2,5): Assert.AreEqual failed.", assertion.GetProperty("message").GetProperty("text").GetString());
        var physical = assertion.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.AreEqual("tests/MathTests.cs", physical.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.AreEqual("%SRCROOT%", physical.GetProperty("artifactLocation").GetProperty("uriBaseId").GetString());
        Assert.AreEqual(9, physical.GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.AreEqual(SarifWriter.ErrorRule, results[1].GetProperty("ruleId").GetString());
        Assert.AreEqual("tests/Shop.Tests/Shop.Tests.csproj", results[2].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString(),
            "no source location: the project file");
        Assert.AreEqual("note", results[3].GetProperty("level").GetString());
        Assert.IsTrue(results[3].GetProperty("properties").GetProperty("quarantined").GetBoolean());
        Assert.AreNotEqual(results[0].GetProperty("partialFingerprints").GetProperty("labcoatTestId/v1").GetString(), results[1].GetProperty("partialFingerprints").GetProperty("labcoatTestId/v1").GetString());

        using var none = JsonDocument.Parse(SarifWriter.ToJson(Export(T("p", Outcome.Passed))));
        Assert.AreEqual(0, none.RootElement.GetProperty("runs")[0].GetProperty("results").GetArrayLength());
    }

    [TestMethod]
    public void Sarif_escapes_uris_and_writes_absolute_files_as_file_uris()
    {
        using var doc = JsonDocument.Parse(SarifWriter.ToJson(Export(At(T("x", Outcome.Failed), "my dir/a#b.cs", 2), At(T("y", Outcome.Failed), "/abs/C.cs", 4),
            At(T("z", Outcome.Failed), "C:\\proj dir\\Z.cs", 6), At(T("u", Outcome.Failed), "\\\\host\\share\\U.cs", 8))));
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");
        JsonElement Artifact(int i) => results[i].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation");
        Assert.AreEqual("my%20dir/a%23b.cs", Artifact(0).GetProperty("uri").GetString());
        Assert.AreEqual("%SRCROOT%", Artifact(0).GetProperty("uriBaseId").GetString());
        // absolute paths are file URIs of any platform's form, wherever Labcoat runs
        Assert.AreEqual("file:///abs/C.cs", Artifact(1).GetProperty("uri").GetString());
        Assert.AreEqual("file:///C:/proj%20dir/Z.cs", Artifact(2).GetProperty("uri").GetString());
        Assert.AreEqual("file://host/share/U.cs", Artifact(3).GetProperty("uri").GetString());
        for (var i = 1; i < 4; i++) Assert.IsFalse(Artifact(i).TryGetProperty("uriBaseId", out _));
    }

    // ------------------------------------------------------------------ formats

    [TestMethod]
    public void New_formats_have_extensions_and_content_types()
    {
        Assert.AreEqual("labcoat-shop-r1.md", ExportFormats.FileName("Shop", "r1", ExportFormats.Markdown));
        Assert.AreEqual("labcoat-shop-r1.trx", ExportFormats.FileName("Shop", "r1", ExportFormats.Trx));
        Assert.AreEqual("labcoat-shop-r1.sarif", ExportFormats.FileName("Shop", "r1", ExportFormats.Sarif));
        Assert.AreEqual("text/markdown; charset=utf-8", ExportFormats.ContentType(ExportFormats.Markdown));
        Assert.AreEqual("application/xml; charset=utf-8", ExportFormats.ContentType(ExportFormats.Trx));
        Assert.AreEqual("application/sarif+json; charset=utf-8", ExportFormats.ContentType(ExportFormats.Sarif));
        foreach (var format in ExportFormats.RunFormats)
        {
            using var ms = new MemoryStream();
            LabcoatApp.WriteRunExport(CiSample(), format, ms);
            Assert.IsGreaterThan(0, ms.Length, format);
        }
    }

    // ------------------------------------------------------------------ GitHub Actions

    [TestMethod]
    public void Annotations_point_at_the_source_line_and_escape_per_the_workflow_command_rules()
    {
        var lines = GitHubActions.Annotations(CiSample());
        Assert.HasCount(3, lines, "failures and errors, not the quarantined one");
        Assert.AreEqual("::error file=tests/MathTests.cs,line=9,title=Shop.MathTests.Add (2%2C2%2C5)::Assert.AreEqual failed. Expected:<5>. Actual:<4>.%0Asecond line", lines[0]);
        Assert.AreEqual("::error file=src/Cart.cs,line=31,title=Shop.CartTests.Throws::100%25 broken, see: here", lines[1], "'%' in data; ':' and ',' only escaped in properties");
        Assert.AreEqual("::error title=Shop.CartTests.Slow::Timed out", lines[2], "no location: no file");
        Assert.AreEqual("a%3Ab%2Cc%25%0D%0A", GitHubActions.Property("a:b,c%\r\n"));
        Assert.AreEqual("a:b,c%25%0D%0A", GitHubActions.Data("a:b,c%\r\n"));
        Assert.DoesNotContain("\u001b", GitHubActions.Data("\u001b[31mred"));
    }

    [TestMethod]
    public void Annotations_are_capped_with_a_notice_for_the_rest()
    {
        var tests = Enumerable.Range(0, 60).Select(i => T($"f{i}", Outcome.Failed, $"F{i}", message: "x")).ToArray();
        var lines = GitHubActions.Annotations(Export(tests));
        Assert.HasCount(GitHubActions.MaxAnnotations + 1, lines);
        Assert.AreEqual(GitHubActions.MaxAnnotations, lines.Count(l => l.StartsWith("::error ", StringComparison.Ordinal)));
        Assert.StartsWith("::notice title=More failed tests::10 more failed tests not annotated", lines[^1]);
        Assert.IsEmpty(GitHubActions.Annotations(Export(T("p", Outcome.Passed))));
    }

    [TestMethod]
    public void Environment_decides_what_is_reported_and_the_step_summary_is_appended_within_the_limit()
    {
        Assert.IsTrue(GitHubActions.IsActive(k => k == "GITHUB_ACTIONS" ? "true" : null));
        Assert.IsFalse(GitHubActions.IsActive(_ => null));
        Assert.IsFalse(GitHubActions.IsActive(_ => "false"));
        Assert.IsNull(GitHubActions.StepSummaryPath(_ => ""));
        Assert.AreEqual("/s.md", GitHubActions.StepSummaryPath(k => k == "GITHUB_STEP_SUMMARY" ? "/s.md" : null));

        var path = Path.Combine(Path.GetTempPath(), $"labcoat-summary-{System.Guid.NewGuid():N}.md");
        try
        {
            File.WriteAllText(path, "earlier step\n");
            Assert.IsTrue(GitHubActions.AppendStepSummary(path, CiSample(), null));
            var text = File.ReadAllText(path);
            Assert.StartsWith("earlier step\n## Test results: 3 failed", text);

            // a nearly full file: nothing more is written
            File.WriteAllBytes(path, new byte[MarkdownReport.MaxStepSummaryBytes - 100]);
            Assert.IsFalse(GitHubActions.AppendStepSummary(path, CiSample(), null));

            // a huge run stays under 1 MiB
            File.Delete(path);
            var tests = Enumerable.Range(0, 3000).Select(i => T($"f{i}", Outcome.Failed, $"F{i}", message: new string('m', 1400), stack: new string('s', 3000))).ToArray();
            Assert.IsTrue(GitHubActions.AppendStepSummary(path, Export(tests), null));
            var size = new FileInfo(path).Length;
            Assert.IsLessThanOrEqualTo(MarkdownReport.MaxStepSummaryBytes, size);
            Assert.IsGreaterThan(500_000, size, "the budget is used, not the PR comment's");
            Assert.Contains("more failed tests not shown", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
