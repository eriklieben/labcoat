namespace Labcoat;

/// <summary>The planned run of --run --affected: the plan, the tests to run of it (null = the plan's own selection) and how many tests that is.</summary>
internal sealed record AffectedSelection(AffectedPlan Plan, List<string>? Ids, int Tests);

/// <summary>
/// <c>--run --affected [base]</c> and the impact map file (<c>--impact-map-file</c>, <c>--build-impact-map</c>) of
/// <see cref="CliRun"/>: loads the map, plans and prints which tests the changes affect, and saves the map after the run.
/// </summary>
internal static class CliAffected
{
    private static string ErrorLabel => Cli.Red("error");
    private static string WarningLabel => Cli.Yellow("warning");

    /// <summary>Most changed files listed before the plan summary says "and N more".</summary>
    private const int MaxListedFiles = 30;

    public static void LoadImpactMap(Explorer explorer, string path, StartOptions options)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine(Cli.Dim($"  Impact map: {path} does not exist" + (options.BuildImpactMap ? ", it is built below." : "; without it a changed product file selects the tests of its projects.")));
            return;
        }
        var result = explorer.ImpactMapFiles.Load(path);
        if (!result.Loaded)
        {
            Console.Error.WriteLine($"{WarningLabel} Impact map ignored: {result.Error}");
            return;
        }
        var built = result.BuiltAt is { } at && DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? $", built {when.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : "";
        var commit = result.Commit is { Length: >= 7 } sha ? $" at {sha[..7]}" : "";
        var skipped = result.Unmatched > 0 ? $"; {result.Unmatched} entr{(result.Unmatched == 1 ? "y" : "ies")} for tests that no longer exist ignored" : "";
        Console.WriteLine(Cli.Dim($"  Impact map: {result.Tests} test{(result.Tests == 1 ? "" : "s")} over {result.Files} source file{(result.Files == 1 ? "" : "s")} from {path}{built}{commit}{skipped}"));
    }

    /// <summary>
    /// Plans --affected: the changed files of the base mapped to tests (building the impact map for the projects that
    /// have none yet when asked), intersected with <paramref name="filterIds"/>, and prints the plan.
    /// </summary>
    public static async Task<AffectedSelection> PlanAffectedRunAsync(Explorer explorer, StartOptions options, AffectedSelector selector,
        List<string>? filterIds, CancellationToken ct)
    {
        var plan = await explorer.Affected.PlanAsync(selector.Base, selector.Ref, ct);
        if (options.BuildImpactMap && plan.ProjectsNeedingImpactMap.Count > 0)
        {
            foreach (var pid in plan.ProjectsNeedingImpactMap) await explorer.BuildImpactMapAsync(pid);
            plan = await explorer.Affected.PlanAsync(selector.Base, selector.Ref, ct);
        }

        var filter = filterIds?.ToHashSet(StringComparer.Ordinal);
        var ids = filter is null ? null : plan.Tests.Where(t => filter.Contains(t.Id)).Select(t => t.Id).Distinct().ToList();
        var count = ids?.Count ?? plan.Counts.Tests;
        PrintPlan(explorer, plan, count, filter is not null);
        return new AffectedSelection(plan, ids, count);
    }

    /// <summary>The plan's selection narrowed to <paramref name="ids"/> (--filter, --shard): projectId → test ids.</summary>
    public static Dictionary<string, HashSet<string>?> SelectionOf(AffectedPlan plan, List<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var selection = new Dictionary<string, HashSet<string>?>();
        foreach (var group in plan.Tests.Where(t => wanted.Contains(t.Id)).GroupBy(t => t.ProjectId))
            selection[group.Key] = group.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return selection;
    }

    private static void PrintPlan(Explorer explorer, AffectedPlan plan, int selected, bool filtered)
    {
        var total = explorer.Snapshot().Tests.Count;
        var against = plan.Base switch
        {
            AffectedBases.Branch => $"branch vs {plan.Ref}" + (plan.MergeBase is { } mb ? $", merge base {mb}" : ""),
            AffectedBases.Range => plan.Ref ?? "range",
            _ => "uncommitted changes",
        };
        var files = $"{plan.Counts.Files} changed file{(plan.Counts.Files == 1 ? "" : "s")}";
        var projects = plan.Projects.Count(p => p.Tests > 0);
        Console.WriteLine($"  {Cli.Bold("Affected")} ({against}): {files} -> {selected} of {total} test{(total == 1 ? "" : "s")}" +
                          (selected > 0 ? $" in {projects} project{(projects == 1 ? "" : "s")}" : "") + (filtered ? " (after --filter)" : ""));
        foreach (var file in plan.Files.Take(MaxListedFiles)) Console.WriteLine($"    {file.RelativePath}: {Describe(file, plan)}");
        if (plan.Files.Count > MaxListedFiles) Console.WriteLine(Cli.Dim($"    ... and {plan.Files.Count - MaxListedFiles} more"));
        foreach (var warning in plan.Warnings ?? []) Console.Error.WriteLine($"{WarningLabel} {warning}");
    }

    /// <summary>What a changed file selected and how precise that is: test file, impact map (per-test or class-level data), cached coverage or the project fallback.</summary>
    private static string Describe(AffectedFile file, AffectedPlan plan)
    {
        var n = file.Tests;
        var count = $"{n} test{(n == 1 ? "" : "s")}";
        switch (file.Mapping)
        {
            case "testFile": return $"test file -> {count}";
            case "impactMap":
                var methods = file.Methods is { Count: > 0 } m ? $", method{(m.Count == 1 ? "" : "s")} {string.Join(", ", m)}" : ", outside executed methods";
                return $"impact map ({file.DataQuality ?? "class-level"} data{methods}) -> {count}";
            case "coverage": return $"cached coverage -> {count}";
            case "project":
                var names = string.Join(", ", (file.ProjectIds ?? []).Select(id => plan.Projects.FirstOrDefault(p => p.ProjectId == id)?.Name ?? id));
                var why = file.Kind == "project" ? "build file" : "project fallback, no impact data for it";
                return $"{why} -> all {count} of {names}";
            default: return file.Kind == "other" ? "not code, no tests" : "no tests";
        }
    }

    public static IEnumerable<string> ProjectsOf(Explorer explorer, List<string>? filterIds)
    {
        var snapshot = explorer.Snapshot();
        if (filterIds is null) return snapshot.Projects.Select(p => p.Id);
        var ids = filterIds.ToHashSet(StringComparer.Ordinal);
        return snapshot.Tests.Where(t => ids.Contains(t.Id)).Select(t => t.ProjectId).Distinct();
    }

    /// <summary>
    /// --build-impact-map: the impact-map pass for <paramref name="projects"/> (run before: the ones a plan needed), then
    /// the map is saved to --impact-map-file. False when it could not be written.
    /// </summary>
    public static async Task<bool> BuildAndSaveImpactMapAsync(Explorer explorer, StartOptions options, IEnumerable<string> projects, CancellationToken ct)
    {
        if (options.ImpactMapFile is not { } file || !options.BuildImpactMap || ct.IsCancellationRequested) return true;
        if (options.Affected is null)
            foreach (var pid in projects) await explorer.BuildImpactMapAsync(pid);
        try
        {
            if (await explorer.ImpactMapFiles.SaveAsync(file, ct) is not { } saved)
            {
                Console.Error.WriteLine($"{WarningLabel} No impact data was recorded: {file} not written " +
                                        "(do the test projects reference Microsoft.Testing.Extensions.CodeCoverage?).");
                return true;
            }
            Console.WriteLine($"  Wrote impact map: {file} ({saved.Tests} tests, {saved.Files} source files, {saved.Bytes / 1024.0:0.#} KB)");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"{ErrorLabel} Could not write {file}: {ex.Message}");
            return false;
        }
    }
}
