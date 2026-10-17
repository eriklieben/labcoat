using System.Globalization;

namespace Labcoat;

/// <summary>One file's changed lines that are executable: how many are covered and the ones that are not.</summary>
public sealed record DiffCoverageFile(string Path, int Covered, int Valid, List<LineSpan> Uncovered);

/// <summary>Coverage of the changed lines only (<c>--min-diff-coverage</c>): files without an executable changed line are left out.</summary>
public sealed record DiffCoverage(int Covered, int Valid, List<DiffCoverageFile> Files)
{
    public double Percent => CoverageGate.Percent(Covered, Valid);
}

/// <summary>
/// <c>--min-coverage</c> / <c>--min-diff-coverage</c> of <c>labcoat --run</c>: compares the coverage of this run with a
/// threshold, prints the result and says whether the run may pass. The overall number is the run's own report; the diff
/// number intersects the lines git reports as changed versus the base with that report's executable lines.
/// </summary>
public static class CoverageGate
{
    private static string ErrorLabel => Cli.Red("error");

    /// <summary>"80", "80.5" or "80%" as 0-100; null when it is anything else.</summary>
    public static double? ParsePercent(string? value) =>
        double.TryParse(value?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 100 ? p : null;

    public static double Percent(int covered, int valid) => valid == 0 ? 0 : 100.0 * covered / valid;

    /// <summary>Whether <paramref name="covered"/> of <paramref name="valid"/> lines reach <paramref name="min"/> percent (exact: no rounding up); no lines at all only meets 0.</summary>
    public static bool Meets(int covered, int valid, double min) => valid == 0 ? min == 0 : 100.0 * covered >= min * valid;

    /// <summary>One decimal, rounded down: a number that misses the threshold never prints as the threshold.</summary>
    internal static string Format(double percent) => (Math.Floor(percent * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string FormatMin(double min) => min.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The coverage of the lines of <paramref name="changes"/> (paths relative to <paramref name="repoRoot"/>; no line
    /// ranges = the whole file is new) in <paramref name="report"/>. Lines the report has no entry for (comments,
    /// declarations, files outside the coverage settings) are not executable and don't count.
    /// </summary>
    public static DiffCoverage Diff(CoverageReport report, IEnumerable<GitChange> changes, string repoRoot)
    {
        var comparer = PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var byPath = new Dictionary<string, Dictionary<int, int>>(comparer);
        foreach (var file in report.Files)
        {
            var key = Path.GetFullPath(file.Path);
            if (!byPath.TryGetValue(key, out var hits)) byPath[key] = hits = [];
            foreach (var line in file.Lines) hits[line.Line] = Math.Max(hits.GetValueOrDefault(line.Line), line.Hits); // one file in several projects
        }

        var files = new List<DiffCoverageFile>();
        foreach (var change in changes.Where(c => c.Status != "deleted"))
        {
            if (!byPath.TryGetValue(Path.GetFullPath(Path.Combine(repoRoot, change.Path)), out var hits)) continue;
            var lines = hits.Keys.Where(l => change.Lines is null || change.Lines.Exists(s => l >= s.Start && l <= s.End)).Order().ToList();
            if (lines.Count == 0) continue;
            var uncovered = lines.Where(l => hits[l] == 0).ToList();
            files.Add(new DiffCoverageFile(change.Path, lines.Count - uncovered.Count, lines.Count, Spans(uncovered)));
        }
        return new DiffCoverage(files.Sum(f => f.Covered), files.Sum(f => f.Valid), files);
    }

    /// <summary>Consecutive line numbers as ranges.</summary>
    internal static List<LineSpan> Spans(List<int> sorted)
    {
        var spans = new List<LineSpan>();
        foreach (var line in sorted)
        {
            if (spans.Count > 0 && spans[^1].End + 1 == line) spans[^1] = new LineSpan(spans[^1].Start, line);
            else spans.Add(new LineSpan(line, line));
        }
        return spans;
    }

    private static string Describe(LineSpan s) => s.Start == s.End ? s.Start.ToString(CultureInfo.InvariantCulture) : $"{s.Start}-{s.End}";

    /// <summary>
    /// Checks the thresholds of <paramref name="options"/> against the coverage of run <paramref name="runId"/> and prints
    /// the numbers; false when one is missed, or cannot be checked (no coverage collected, no git base).
    /// </summary>
    public static async Task<bool> CheckAsync(Explorer explorer, StartOptions options, string runId, CancellationToken ct)
    {
        if (options.MinCoverage is null && options.MinDiffCoverage is null) return true;
        Console.WriteLine();
        if (explorer.LastCoverageReport is not { } report || report.RunId != runId)
        {
            await Console.Error.WriteLineAsync($"{ErrorLabel} No coverage was collected in this run, so the coverage threshold can't be checked " +
                                               "(do the test projects reference Microsoft.Testing.Extensions.CodeCoverage?).");
            return false;
        }
        var ok = true;
        if (options.MinCoverage is { } min) ok &= CheckOverall(report, min);
        if (options.MinDiffCoverage is { } minDiff) ok &= await CheckDiffAsync(explorer, report, minDiff, ct);
        return ok;
    }

    private static bool CheckOverall(CoverageReport report, double min)
    {
        var met = Meets(report.LinesCovered, report.LinesValid, min);
        Console.WriteLine($"  Coverage: {Verdict(met)} {Format(Percent(report.LinesCovered, report.LinesValid))} of lines " +
                          $"({report.LinesCovered}/{report.LinesValid}), minimum {FormatMin(min)}");
        if (!met && report.LinesValid == 0) Console.Error.WriteLine($"{ErrorLabel} The run has no coverable lines (test assemblies are not counted).");
        return met;
    }

    private static async Task<bool> CheckDiffAsync(Explorer explorer, CoverageReport report, double min, CancellationToken ct)
    {
        GitChanges changes;
        string root;
        try { (changes, root) = await explorer.ChangedFilesAsync(ct); }
        catch (AffectedException ex)
        {
            await Console.Error.WriteLineAsync($"{ErrorLabel} --min-diff-coverage: {ex.Message}");
            return false;
        }
        foreach (var warning in changes.Warnings) Console.Error.WriteLine($"{Cli.Yellow("warning")} {warning}");
        var diff = Diff(report, changes.Files, root);
        var against = $"changes vs {changes.Ref}{(changes.MergeBase is null ? "" : $" ({changes.MergeBase})")}";
        if (diff.Valid == 0)
        {
            Console.WriteLine($"  Diff coverage: {Verdict(true)} no executable changed lines ({against})");
            return true;
        }
        var met = Meets(diff.Covered, diff.Valid, min);
        Console.WriteLine($"  Diff coverage: {Verdict(met)} {Format(diff.Percent)} of changed lines " +
                          $"({diff.Covered}/{diff.Valid}), minimum {FormatMin(min)} ({against})");
        foreach (var file in diff.Files.Where(f => f.Uncovered.Count > 0))
            Console.WriteLine($"    {file.Path}: lines {string.Join(", ", file.Uncovered.Select(Describe))} not covered ({file.Covered}/{file.Valid})");
        return met;
    }

    private static string Verdict(bool met) => met ? Cli.Bold("ok") : Cli.Red("below");
}

public sealed partial class Explorer
{
    /// <summary>
    /// The files (with changed lines) that differ from the affected base, as the "branch" base of run affected: the merge
    /// base with <see cref="AffectedBase"/> (else origin/HEAD, main or master) plus uncommitted and untracked files; with
    /// the repository root their paths are relative to. Throws <see cref="AffectedException"/> when git can't answer.
    /// </summary>
    public async Task<(GitChanges Changes, string Root)> ChangedFilesAsync(CancellationToken ct = default)
    {
        var repo = await RepoAsync(ct);
        return (await repo.ChangesAsync(AffectedBases.Branch, null, AffectedBase, ct), repo.Root);
    }
}
