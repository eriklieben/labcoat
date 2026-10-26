using Labcoat;

namespace Labcoat.Tests;

/// <summary>
/// <c>--min-coverage</c> / <c>--min-diff-coverage</c> (CoverageGate.cs): the options, the thresholds, the intersection of
/// git's changed lines with the executable lines of a coverage report, and the exit code.
/// </summary>
[TestClass]
public class CoverageGateTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gate-repo"));

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

    private static FileCoverage File(string relative, params (int Line, int Hits)[] lines) => new()
    {
        Path = Path.Combine(Root, relative),
        Module = "M",
        Lines = lines.Select(l => new CoverageLineHit { Line = l.Line, Hits = l.Hits }).ToList(),
    };

    private static CoverageReport Report(params FileCoverage[] files) => new() { RunId = "r", GeneratedAt = "now", ProjectIds = [], Files = [.. files] };

    [TestMethod]
    public void Threshold_options_parse()
    {
        var o = Ok("--run", "--min-coverage", "80", "--min-diff-coverage=92.5%");
        Assert.AreEqual(80.0, o.MinCoverage);
        Assert.AreEqual(92.5, o.MinDiffCoverage);
        Assert.AreEqual(0.0, Ok("--run", "--min-coverage", "0").MinCoverage);
        Assert.AreEqual(100.0, Ok("--run", "--min-diff-coverage", "100").MinDiffCoverage);
        Assert.IsNull(Ok("--run").MinCoverage);

        Assert.Contains("--min-coverage needs a percentage", Error("--run", "--min-coverage"));
        Assert.Contains("--min-coverage needs a percentage", Error("--run", "--min-coverage", "high"));
        Assert.Contains("--min-coverage needs a percentage", Error("--run", "--min-coverage", "101"));
        Assert.Contains("--min-diff-coverage needs a percentage", Error("--run", "--min-diff-coverage=-1"));
        Assert.Contains("need --run", Error("--min-coverage", "80"));
        Assert.Contains("need --run", Error("--min-diff-coverage", "80"));
        Assert.Contains("--min-coverage <percent>", Cli.Usage);
        Assert.Contains("--min-diff-coverage <percent>", Cli.Usage);
    }

    [TestMethod]
    public void Thresholds_compare_exactly_and_print_rounded_down()
    {
        Assert.IsTrue(CoverageGate.Meets(4, 5, 80));
        Assert.IsFalse(CoverageGate.Meets(79, 100, 80));
        Assert.IsTrue(CoverageGate.Meets(0, 0, 0));
        Assert.IsFalse(CoverageGate.Meets(0, 0, 1));
        Assert.AreEqual("79.9%", CoverageGate.Format(79.99));
        Assert.AreEqual("80.0%", CoverageGate.Format(80));
    }

    [TestMethod]
    public void Diff_coverage_counts_only_executable_changed_lines()
    {
        var report = Report(
            File("src/A.cs", (1, 1), (2, 0), (3, 0), (4, 2), (10, 0)),
            File("src/B.cs", (1, 3), (2, 3)));
        var changes = new[]
        {
            new GitChange("src/A.cs", "modified") { Lines = [new LineSpan(2, 4), new LineSpan(6, 8)] }, // 2, 3, 4 executable; 6-8 are not
            new GitChange("src/B.cs", "added"), // whole file
            new GitChange("README.md", "modified") { Lines = [new LineSpan(1, 5)] }, // not in the report
            new GitChange("src/Gone.cs", "deleted"),
        };

        var diff = CoverageGate.Diff(report, changes, Root);

        Assert.AreEqual(3, diff.Covered); // A.cs line 4, B.cs lines 1 and 2
        Assert.AreEqual(5, diff.Valid);
        Assert.AreEqual(60.0, diff.Percent, 0.001);
        Assert.HasCount(2, diff.Files);
        Assert.AreEqual("src/A.cs", diff.Files[0].Path);
        CollectionAssert.AreEqual(new[] { new LineSpan(2, 3) }, diff.Files[0].Uncovered);
        Assert.IsEmpty(diff.Files[1].Uncovered);
    }

    [TestMethod]
    public void Diff_coverage_of_only_non_executable_changes_is_empty()
    {
        var diff = CoverageGate.Diff(Report(File("src/A.cs", (5, 0))), [new GitChange("src/A.cs", "modified") { Lines = [new LineSpan(1, 3)] }], Root);
        Assert.AreEqual(0, diff.Valid);
        Assert.IsEmpty(diff.Files);
    }

    [TestMethod]
    public void Uncovered_lines_become_ranges()
    {
        CollectionAssert.AreEqual(
            new[] { new LineSpan(1, 3), new LineSpan(7, 7), new LineSpan(9, 10) },
            CoverageGate.Spans([1, 2, 3, 7, 9, 10]));
        Assert.IsEmpty(CoverageGate.Spans([]));
    }

    [TestMethod]
    public void A_missed_threshold_fails_the_run_but_not_over_a_build_error()
    {
        var run = new RunSummary { RunId = "r", StartedAt = "now", Trigger = CliRun.Trigger, Total = 1, Passed = 1 };
        Assert.AreEqual(CliRun.ExitPassed, CliRun.ExitCode(run, [], true, gatesOk: true));
        Assert.AreEqual(CliRun.ExitFailed, CliRun.ExitCode(run, [], true, gatesOk: false));
        Assert.AreEqual(CliRun.ExitBuildError, CliRun.ExitCode(null, [], true, gatesOk: false));
    }
}
