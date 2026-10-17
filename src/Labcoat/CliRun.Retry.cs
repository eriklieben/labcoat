namespace Labcoat;

// --retry-failed (CliRun.cs): Labcoat reruns the failed tests itself, as ordinary runs of just those tests (MTP's
// --retry-failed-tests extension is not used: see docs/standalone.md).
public static partial class CliRun
{
    /// <summary>Most retries <c>--retry-failed</c> accepts.</summary>
    public const int MaxRetries = 10;

    /// <summary>RunSummary.trigger of the runs that rerun failed tests.</summary>
    public const string RetryTrigger = "cli-retry";

    /// <summary>
    /// Reruns the failed (not quarantined) tests of <paramref name="first"/> up to <paramref name="retries"/> times, each
    /// round only the tests that are still failing, and returns the first run's export with the outcome of the retries
    /// applied: a test that passed on a retry is passed with <see cref="ExportTest.Attempts"/> &gt; 1 (flaky), one that
    /// never passed stays failed. The run's counts move the flaky tests from failed to passed. The retries are ordinary
    /// runs, so they show up in the history as well.
    /// </summary>
    private static async Task<RunExport> RetryFailedAsync(Explorer explorer, RunExport first, int retries, CancellationToken ct)
    {
        var failures = first.Tests.Where(t => ExportKinds.IsFailed(t.Kind)).ToDictionary(Key, t => new List<ExportTest> { t });
        var passed = new Dictionary<(string, string), (ExportTest Test, int Attempt)>();
        var pending = failures.Keys.ToList();
        for (var attempt = 2; attempt <= retries + 1 && pending.Count > 0 && !ct.IsCancellationRequested; attempt++)
        {
            Console.WriteLine($"  Retrying {pending.Count} failed test{(pending.Count == 1 ? "" : "s")} (attempt {attempt} of {retries + 1})...");
            var request = explorer.RequestRun(new Explorer.RunRequestOptions { TestIds = pending.Select(k => k.Item2).Distinct().ToList(), Coverage = false, Trigger = RetryTrigger });
            if (request.Status != 202)
            {
                Console.Error.WriteLine($"{WarningLabel} Could not retry the failed tests: {request.Error}");
                break;
            }
            await request.Completion!;
            if (explorer.ExportRun(request.RunId!) is not { } export || export.Run is { Aborted: true } or { Cancelled: true }) break;
            var results = export.Tests.ToDictionary(Key);
            var stillFailing = new List<(string, string)>();
            foreach (var key in pending)
            {
                if (!results.TryGetValue(key, out var result)) continue; // not run: stays failed, not retried again
                if (result.Outcome == Outcome.Passed) passed[key] = (result, attempt);
                else if (ExportKinds.IsFailed(result.Kind))
                {
                    failures[key].Add(result);
                    stillFailing.Add(key);
                }
            }
            pending = stillFailing;
        }
        return Merge(first, failures, passed);
    }

    private static (string, string) Key(ExportTest t) => (t.ProjectId, t.Id);

    internal static RunExport Merge(RunExport first, Dictionary<(string, string), List<ExportTest>> failures, Dictionary<(string, string), (ExportTest Test, int Attempt)> passed)
    {
        var tests = first.Tests.Select(t =>
        {
            if (!failures.TryGetValue(Key(t), out var attempts)) return t;
            if (passed.TryGetValue(Key(t), out var pass))
                return t with
                {
                    Outcome = Outcome.Passed, DurationMs = pass.Test.DurationMs, ErrorMessage = null, ErrorType = null, StackTrace = null,
                    StdOut = pass.Test.StdOut, StdErr = pass.Test.StdErr, Attempts = pass.Attempt, FailedAttempts = [.. attempts.Select(ExportAttempt.Of)],
                };
            return attempts.Count == 1 ? t : t with { Attempts = attempts.Count, FailedAttempts = [.. attempts.Skip(1).Select(ExportAttempt.Of)] };
        }).ToList();
        var run = first.Run.Copy();
        run.Failed -= passed.Count;
        run.Passed += passed.Count;
        return first with { Run = run, Tests = tests };
    }

    /// <summary>The tests that were retried: which ones are flaky, which never passed.</summary>
    private static void PrintRetried(RunExport export)
    {
        foreach (var t in export.Tests.Where(t => t.IsFlaky))
            Console.WriteLine($"  {WarningLabel} Flaky: {t.Name} failed {t.Attempts - 1} time{(t.Attempts == 2 ? "" : "s")} and passed on attempt {t.Attempts}" +
                              (t.FailedAttempts?.FirstOrDefault() is { } f ? Cli.Dim($" (first failure: {ExportText.FirstLine(f.ErrorMessage, 160)})") : ""));
        foreach (var t in export.Tests.Where(t => t.Attempts > 1 && ExportKinds.IsFailed(t.Kind)))
            Console.WriteLine($"  Still failing after {t.Attempts} attempts: {t.Name}");
    }
}
