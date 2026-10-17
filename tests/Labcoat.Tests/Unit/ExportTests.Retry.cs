using Labcoat;

namespace Labcoat.Tests.Unit;

// --retry-failed: how the exports show tests that were rerun (CliRun.Retry.cs).
public partial class ExportTests
{
    private const string AssertFailed = "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException";

    /// <summary>"Flaky" failed at attempt 1 (an assertion) and 2 (an exception) and passed on 3; "Broken" failed on all 2 attempts.</summary>
    private static RunExport Retried()
    {
        var flaky = T("f1", Outcome.Failed, "Flaky_Test", message: "Expected 5", type: AssertFailed, stack: "at A()");
        var broken = T("b1", Outcome.Failed, "Broken_Test", message: "first", type: AssertFailed, stack: "at B()");
        var first = Export(T("p1", Outcome.Passed, "Fine"), flaky, broken);
        first.Run.Passed = 1;
        first.Run.Failed = 2;
        var rerun = T("f1", Outcome.Failed, "Flaky_Test", message: "timeout talking to db", type: "System.TimeoutException", stack: "at C()");
        var rerunBroken = T("b1", Outcome.Failed, "Broken_Test", message: "second", type: AssertFailed, stack: "at B2()");
        var pass = T("f1", Outcome.Passed, "Flaky_Test", ms: 30, stdout: "ok this time");
        return CliRun.Merge(first,
            new() { [("Shop.Tests", "f1")] = [flaky, rerun], [("Shop.Tests", "b1")] = [broken, rerunBroken] },
            new() { [("Shop.Tests", "f1")] = (pass, 3) });
    }

    [TestMethod]
    public void Retried_tests_are_merged_into_the_first_run()
    {
        var export = Retried();
        Assert.AreEqual(1, export.Flaky);
        Assert.AreEqual((2, 1), (export.Run.Passed, export.Run.Failed), "a flaky test counts as passed");
        var flaky = export.Tests.Single(t => t.Name == "Flaky_Test");
        Assert.IsTrue(flaky.IsFlaky);
        Assert.AreEqual((Outcome.Passed, 3, 30d), (flaky.Outcome, flaky.Attempts, flaky.DurationMs));
        Assert.IsNull(flaky.ErrorMessage);
        Assert.AreEqual("ok this time", flaky.StdOut);
        Assert.HasCount(2, flaky.FailedAttempts!);
        var broken = export.Tests.Single(t => t.Name == "Broken_Test");
        Assert.IsFalse(broken.IsFlaky);
        Assert.AreEqual((Outcome.Failed, 2, "first"), (broken.Outcome, broken.Attempts, broken.ErrorMessage), "the first failure stays the failure");
        Assert.AreEqual("second", broken.FailedAttempts!.Single().ErrorMessage);
        Assert.AreEqual(1, export.Tests.Single(t => t.Name == "Fine").Attempts);
    }

    [TestMethod]
    public void Junit_writes_flaky_failures_and_reruns_valid_for_the_schema()
    {
        var doc = ValidJUnit(JUnitWriter.ToXml(Retried()));
        var flaky = doc.Descendants("testcase").Single(c => (string)c.Attribute("name")! == "Flaky_Test");
        Assert.IsNull(flaky.Element("failure"));
        Assert.IsNull(flaky.Element("error"));
        Assert.AreEqual("Expected 5", (string)flaky.Element("flakyFailure")!.Attribute("message")!);
        Assert.AreEqual("at A()", flaky.Element("flakyFailure")!.Element("stackTrace")!.Value);
        Assert.AreEqual("System.TimeoutException", (string)flaky.Element("flakyError")!.Attribute("type")!, "an exception is a flakyError");
        Assert.AreEqual("ok this time", flaky.Element("system-out")!.Value);

        var broken = doc.Descendants("testcase").Single(c => (string)c.Attribute("name")! == "Broken_Test");
        Assert.AreEqual("first", (string)broken.Element("failure")!.Attribute("message")!);
        Assert.AreEqual("second", (string)broken.Element("rerunFailure")!.Attribute("message")!);

        var suite = doc.Descendants("testsuite").Single();
        Assert.AreEqual("1", (string)suite.Attribute("failures")!, "only the test that never passed");
        Assert.AreEqual("1", suite.Descendants("property").Single(p => (string)p.Attribute("name")! == "labcoat.flaky").Attribute("value")!.Value);
        Assert.IsFalse(ValidJUnit(JUnitWriter.ToXml(Sample())).Descendants("property").Any(p => (string)p.Attribute("name")! == "labcoat.flaky"));
    }

    [TestMethod]
    public void Html_report_marks_flaky_tests_and_failures_that_stayed()
    {
        var html = HtmlReport.ToHtml(Retried());
        Assert.Contains("1 test failed", html);
        Assert.Contains("class=\"r p flaky\"", html);
        Assert.Contains("Flaky: passed on attempt 3 (first failure: Expected 5)", html);
        Assert.Contains("<dt>Flaky</dt><dd class=\"c-s\">1</dd>", html);
        Assert.Contains("Failed all 2 attempts (this is the first)", html);

        var onlyFlaky = Retried() with { Tests = Retried().Tests.Where(t => t.Name != "Broken_Test").ToList() };
        var page = HtmlReport.ToHtml(onlyFlaky);
        Assert.Contains("No failures, but 1 flaky test passed only on a retry", page);
        Assert.DoesNotContain("Flaky:", HtmlReport.ToHtml(Sample()));
    }
}
