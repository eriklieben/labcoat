namespace Labcoat;

/// <summary>The pass / fail / skip counts of a run summary.</summary>
internal static class RunCounts
{
    /// <summary>Run counts; results of quarantined tests ("projectId|id") go to <see cref="RunSummary.Quarantined"/> only.</summary>
    public static void Update(RunSummary run, Dictionary<(string, string), Outcome> results, IReadOnlySet<string> quarantined)
    {
        int passed = 0, failed = 0, skipped = 0, q = 0;
        foreach (var ((projectId, id), o) in results)
        {
            if (quarantined.Count > 0 && quarantined.Contains(WorkspaceSettings.QuarantineKey(projectId, id)))
            {
                if (o is not Outcome.Cancelled) q++;
                continue;
            }
            if (o == Outcome.Passed) passed++;
            else if (o is Outcome.Failed or Outcome.TimedOut) failed++;
            else if (o == Outcome.Skipped) skipped++;
        }
        run.Passed = passed;
        run.Failed = failed;
        run.Skipped = skipped;
        run.Quarantined = q > 0 ? q : null;
    }
}
