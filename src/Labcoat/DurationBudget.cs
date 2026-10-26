namespace Labcoat;

/// <summary>A test that took longer than the <c>maxTestDuration</c> budget.</summary>
public sealed record OverBudgetTest(string ProjectId, string Id, string Name, string? ClassName, double DurationMs, Outcome Outcome);

/// <summary>
/// The duration budget of <c>--run</c> (<c>maxTestDuration</c> / <c>--max-test-duration</c>, docs/configuration.md): unlike
/// <c>testTimeout</c> it cancels nothing; after the run the tests that took longer fail it. Quarantined tests and tests
/// without a duration (skipped, not run) are left out.
/// </summary>
public static class DurationBudget
{
    /// <summary>The tests of <paramref name="export"/> over <paramref name="budget"/>, slowest first; empty when the budget is zero (off).</summary>
    public static List<OverBudgetTest> OverBudget(RunExport export, TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero) return [];
        var limit = budget.TotalMilliseconds;
        return export.Tests
            .Where(t => !t.Quarantined && t.DurationMs is { } d && d > limit && t.Outcome is not (Outcome.Skipped or Outcome.NotRun or Outcome.Cancelled))
            .OrderByDescending(t => t.DurationMs)
            .Select(t => new OverBudgetTest(t.ProjectId, t.Id, t.Name, t.ClassName, t.DurationMs!.Value, t.Outcome))
            .ToList();
    }
}
