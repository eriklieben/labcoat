namespace Labcoat;

/// <summary>One point of <see cref="SlowTest.Trend"/>: a past result with a duration.</summary>
public sealed record SlowTestPoint(string At, double DurationMs, Outcome Outcome);

/// <summary>One entry of GET /api/tests/slowest.</summary>
/// <param name="DurationMs">The latest duration.</param>
/// <param name="MedianMs">Median duration of the earlier results (null when there is none).</param>
/// <param name="Factor">DurationMs / MedianMs, null without a median.</param>
/// <param name="Trend">The recent durations (the history, max 20), oldest first.</param>
public sealed record SlowTest(string ProjectId, string Id, string Name, string? ClassName, string? Namespace, double DurationMs,
    double? MedianMs, double? Factor, bool? Quarantined, List<SlowTestPoint> Trend);

/// <summary>GET /api/tests/slowest response: <paramref name="Total"/> = tests with a duration (the list holds the slowest of them).</summary>
public sealed record SlowestTestsResult(string? ProjectId, int Total, List<SlowTest> Tests);

/// <summary>The slowest tests of a project (or the workspace) with their duration trend over time, from the persisted history.</summary>
public static class SlowestTests
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 200;

    /// <summary>The <paramref name="limit"/> tests with the longest latest duration, slowest first; skipped and unrun tests have none.</summary>
    public static SlowestTestsResult Compute(string? projectId, IEnumerable<TestNode> tests, ISet<string> quarantined, int limit = DefaultLimit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var timed = tests
            .Where(t => t.DurationMs is > 0 && t.Outcome is not (Outcome.Skipped or Outcome.NotRun or Outcome.Queued or Outcome.Running or Outcome.Cancelled))
            .ToList();
        var slowest = timed.OrderByDescending(t => t.DurationMs).Take(limit).Select(t => ToSlowTest(t, quarantined)).ToList();
        return new SlowestTestsResult(projectId, timed.Count, slowest);
    }

    private static SlowTest ToSlowTest(TestNode t, ISet<string> quarantined)
    {
        var trend = (t.History ?? [])
            .Where(e => e.Outcome != Outcome.Skipped && e.DurationMs is not null)
            .Select(e => new SlowTestPoint(e.At, e.DurationMs!.Value, e.Outcome))
            .ToList();
        // the latest result is the last history entry once it is recorded: it is not "earlier"
        var earlier = (trend.Count > 0 && trend[^1].At == t.LastRunAt ? trend[..^1] : trend).Select(p => p.DurationMs).Order().ToList();
        double? median = earlier.Count == 0 ? null : Median(earlier);
        var duration = t.DurationMs!.Value;
        return new SlowTest(t.ProjectId, t.Id, t.DisplayName, t.ClassName, t.Namespace, duration, median,
            median is > 0 ? Math.Round(duration / median.Value, 2) : null,
            quarantined.Contains(WorkspaceSettings.QuarantineKey(t.ProjectId, t.Id)) ? true : null, trend);
    }

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}
