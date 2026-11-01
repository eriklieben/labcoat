using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>The slowest tests of a project with their duration trend (SlowestTests.cs, GET /api/tests/slowest).</summary>
[TestClass]
public class SlowestTestsTests
{
    private static TestNode T(string id, double? ms, Outcome outcome = Outcome.Passed, string project = "P", params double[] earlier) => new()
    {
        Id = id, ProjectId = project, DisplayName = id, ClassName = "Cls", Namespace = "Ns", Outcome = outcome, DurationMs = ms,
        LastRunAt = "2026-10-10T00:00:00Z",
        History = [.. earlier.Select((d, i) => new HistoryEntry(Outcome.Passed, d, $"2026-10-0{i + 1}T00:00:00Z")),
            .. ms is { } m ? [new HistoryEntry(outcome, m, "2026-10-10T00:00:00Z")] : Array.Empty<HistoryEntry>()],
    };

    [TestMethod]
    public void Lists_the_slowest_first_with_median_factor_and_trend()
    {
        var result = SlowestTests.Compute("P", [T("a", 100), T("b", 900, Outcome.Passed, "P", 100, 300, 200), T("c", 500), T("skip", 700, Outcome.Skipped), T("none", null)], new HashSet<string>());
        Assert.AreEqual(3, result.Total);
        CollectionAssert.AreEqual(new[] { "b", "c", "a" }, result.Tests.Select(t => t.Id).ToArray());
        var b = result.Tests[0];
        Assert.AreEqual((200d, 4.5), (b.MedianMs, b.Factor));
        CollectionAssert.AreEqual(new[] { 100d, 300, 200, 900 }, b.Trend.Select(p => p.DurationMs).ToArray(), "the latest result is the last point");
        Assert.IsNull(result.Tests[1].MedianMs, "no earlier results, no median");
        Assert.IsNull(result.Tests[1].Factor);
    }

    [TestMethod]
    public void Limits_the_list_and_flags_quarantined_tests()
    {
        var tests = Enumerable.Range(1, 5).Select(i => T("t" + i, i * 10)).ToList();
        var result = SlowestTests.Compute("P", tests, new HashSet<string> { WorkspaceSettings.QuarantineKey("P", "t5") }, limit: 2);
        Assert.AreEqual(5, result.Total);
        CollectionAssert.AreEqual(new[] { "t5", "t4" }, result.Tests.Select(t => t.Id).ToArray());
        Assert.AreEqual((true, (bool?)null), (result.Tests[0].Quarantined, result.Tests[1].Quarantined));
        Assert.HasCount(SlowestTests.MaxLimit, SlowestTests.Compute(null, Enumerable.Range(1, 300).Select(i => T("t" + i, i)), new HashSet<string>(), 10_000).Tests);
    }
}
