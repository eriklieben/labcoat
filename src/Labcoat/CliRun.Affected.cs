namespace Labcoat;

// --run --affected [base] and the impact map file (--impact-map-file, --build-impact-map) of CliRun.cs.

public static partial class CliRun
{
    /// <summary>The planned run: the plan, the exact selection (projectId → test ids, null = the whole project) and how many tests it holds.</summary>
    private sealed record AffectedSelection(AffectedPlan Plan, Dictionary<string, HashSet<string>?> Selection, int Tests);

    /// <summary>Most changed files listed before the plan summary says "and N more".</summary>
    private const int MaxListedFiles = 30;

    private static void LoadImpactMap(Explorer explorer, string path, StartOptions options)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine(Cli.Dim($"  Impact map: {path} does not exist" + (options.BuildImpactMap ? ", it is built below." : "; without it a changed product file selects the tests of its projects.")));
            return;
        }
        var result = explorer.LoadImpactMap(path);
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
    private static async Task<AffectedSelection> PlanAffectedRunAsync(Explorer explorer, StartOptions options, AffectedSelector selector,
        List<string>? filterIds, CancellationToken ct)
    {
        var plan = await explorer.PlanAffectedAsync(selector.Base, selector.Ref, ct);
        if (options.BuildImpactMap && plan.ProjectsNeedingImpactMap.Count > 0)
        {
            foreach (var pid in plan.ProjectsNeedingImpactMap) await explorer.BuildImpactMapAsync(pid);
            plan = await explorer.PlanAffectedAsync(selector.Base, selector.Ref, ct);
        }

        var filter = filterIds?.ToHashSet(StringComparer.Ordinal);
        var tests = plan.Tests.Where(t => filter is null || filter.Contains(t.Id)).ToList();
        Dictionary<string, HashSet<string>?> selection;
        if (filter is null) selection = plan.Selection;
        else
        {
            selection = [];
            foreach (var group in tests.GroupBy(t => t.ProjectId)) selection[group.Key] = group.Select(t => t.Id).ToHashSet();
        }
        var count = filter is null ? plan.Counts.Tests : tests.Count;
        PrintPlan(explorer, plan, count, filter is not null);
        return new AffectedSelection(plan, selection, count);
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

    /// <summary>--affected found nothing to run: say so, write the exports (empty reports) and exit 0 (a broken project still exits 2).</summary>
    private static async Task<int> NothingAffectedAsync(Explorer explorer, StartOptions options, List<TestProject> broken)
    {
        Console.WriteLine();
        Console.WriteLine($"  {Cli.Bold("No affected tests")}: nothing to run.");
        foreach (var p in broken) await Console.Error.WriteLineAsync($"{ErrorLabel} {p.Name}: {p.LastError ?? "build or discovery failed"}");
        var exportsOk = WriteExports(explorer, options, null, nothingToRun: true);
        if (broken.Count > 0) return ExitBuildError;
        return exportsOk ? ExitPassed : ExitFailed;
    }

    private static IEnumerable<string> ProjectsOf(Explorer explorer, List<string>? filterIds)
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
    private static async Task<bool> BuildAndSaveImpactMapAsync(Explorer explorer, StartOptions options, IEnumerable<string> projects, CancellationToken ct)
    {
        if (options.ImpactMapFile is not { } file || !options.BuildImpactMap || ct.IsCancellationRequested) return true;
        if (options.Affected is null)
            foreach (var pid in projects) await explorer.BuildImpactMapAsync(pid);
        try
        {
            if (await explorer.SaveImpactMapAsync(file, ct) is not { } saved)
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
