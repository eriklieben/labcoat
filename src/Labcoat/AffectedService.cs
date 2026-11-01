namespace Labcoat;

/// <summary>Result of <see cref="AffectedService.RunAsync"/>.</summary>
public sealed record RunAffectedResult(Explorer.RunRequestResult? Run, AffectedPlan? Plan, bool ImpactMapFirst = false);

// Run affected (docs/features.md "Run affected"): the planning is in AffectedPlanner.cs; this queues the run (with the
// impact map built first when asked) and handles the runAffected command.
internal sealed class AffectedService(IExplorerEffects effects, OperationGate gate, ImpactMap impact, AffectedPlanner planner,
    Func<string, Task> buildImpactMapAsync, Func<IEnumerable<(string ProjectId, string Id)>, Task> refineImpactMapAsync,
    Func<Explorer.RunRequestOptions, Explorer.RunRequestResult> requestRun)
{
    private static readonly Dictionary<string, string> BaseLabels = new()
    {
        [AffectedBases.Working] = "uncommitted changes", [AffectedBases.Branch] = "branch", [AffectedBases.Range] = "range",
    };

    /// <summary>GET /api/git: branch, HEAD, dirty count, default base (cached ~2 s).</summary>
    public Task<GitInfo> GitInfoAsync(CancellationToken ct = default) => planner.GitInfoAsync(ct);

    /// <summary>GET /api/changed-tests: the tests added or modified on the current branch (AffectedPlanner.ChangedTestsAsync).</summary>
    public Task<ChangedTests> ChangedTestsAsync(CancellationToken ct = default) => planner.ChangedTestsAsync(ct);

    /// <summary>The changed files and lines versus the affected base (<c>--min-diff-coverage</c>, CoverageGate.cs).</summary>
    public Task<(GitChanges Changes, string Root)> ChangedFilesAsync(CancellationToken ct = default) => planner.ChangedFilesAsync(ct);

    /// <summary>Drops the cached answer of <see cref="ChangedTestsAsync"/> (the next call asks git again).</summary>
    public void InvalidateChangedTests() => planner.InvalidateChangedTests();

    /// <summary>GET /api/affected: which tests the changes of <paramref name="baseKind"/> affect. Never runs anything.</summary>
    public Task<AffectedPlan> PlanAsync(string? baseKind, string? reference, CancellationToken ct = default) =>
        planner.PlanAsync(baseKind, reference, ct);

    /// <summary>
    /// runAffected / POST /api/run-affected: plan, then queue one run with the exact selection. With
    /// <paramref name="buildImpactMap"/> and product files without impact data, the impact-map pass runs first (in the
    /// background) and the plan is recomputed before the run. Throws <see cref="AffectedException"/>.
    /// </summary>
    public async Task<RunAffectedResult> RunAsync(string? baseKind, string? reference, bool? coverage, bool buildImpactMap, string trigger,
        CancellationToken ct = default)
    {
        var plan = await PlanAsync(baseKind, reference, ct);
        // impactMap "auto": "build impact map first" also refines the classes the plan selected through class-level data.
        List<(string ProjectId, string Id)> refine = impact.Mode == ImpactMapModes.Auto ? plan.ClassLevel : [];
        if (buildImpactMap && (plan.ProjectsNeedingImpactMap.Count > 0 || refine.Count > 0))
        {
            var projects = plan.ProjectsNeedingImpactMap.ToList();
            effects.Log(null, LogLevels.Info, projects.Count > 0
                ? $"Run affected: building the impact map for {string.Join(", ", projects)} first…"
                : "Run affected: refining the impact map per test first…");
            // Deliberately outlives the request (it answers right away): the job stops on shutdown, not on ct.
            _ = Task.Run(() => ImpactMapThenRunAsync(projects, plan, baseKind, reference, coverage, trigger), CancellationToken.None);
            return new RunAffectedResult(null, plan, ImpactMapFirst: true);
        }
        return new RunAffectedResult(StartRun(plan, coverage, trigger), plan);
    }

    /// <summary>The background part of "build impact map first": the impact map, then a new plan and its run.</summary>
    private async Task ImpactMapThenRunAsync(List<string> projects, AffectedPlan plan, string? baseKind, string? reference, bool? coverage,
        string trigger)
    {
        try
        {
            foreach (var pid in projects) await buildImpactMapAsync(pid);
            if (impact.Mode == ImpactMapModes.Auto)
            {
                var mid = projects.Count > 0 ? await PlanAsync(baseKind, reference, gate.ShutdownToken) : plan;
                await refineImpactMapAsync(mid.ClassLevel);
            }
            var again = await PlanAsync(baseKind, reference, gate.ShutdownToken);
            StartRun(again, coverage, trigger);
        }
        catch (AffectedException ex) { effects.Log(null, LogLevels.Warning, "Run affected: " + ex.Message); }
        catch (Exception ex) when (!gate.IsShuttingDown) { effects.Log(null, LogLevels.Error, "Run affected failed: " + ex.Message); }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
    }

    /// <summary>WS runAffected: errors become a commandRejected reply for the sending client.</summary>
    public async Task<ServerEvent?> CommandAsync(ClientCommand cmd)
    {
        try
        {
            var r = await RunAsync(cmd.Base, cmd.Ref, cmd.Coverage, cmd.BuildImpactMap ?? false, "user", gate.ShutdownToken);
            if (r.Run is { Status: not 202 } run) return new CommandRejectedEvent(cmd.Type, run.Status, run.Error ?? "rejected", run.UnknownTestIds);
            return null;
        }
        catch (AffectedException ex)
        {
            effects.Log(null, LogLevels.Warning, "runAffected: " + ex.Message);
            return new CommandRejectedEvent(cmd.Type, ex.Status, ex.Message);
        }
        catch (OperationCanceledException) { return null; }
    }

    private Explorer.RunRequestResult? StartRun(AffectedPlan plan, bool? coverage, string trigger)
    {
        var what = plan.Base switch
        {
            AffectedBases.Branch => $"branch vs {plan.Ref}",
            AffectedBases.Range => plan.Ref ?? "range",
            _ => BaseLabels[plan.Base],
        };
        var fileCount = $"{plan.Counts.Files} file{Words.Plural(plan.Counts.Files)}";
        if (plan.Selection.Count == 0)
        {
            effects.Log(null, LogLevels.Info, $"Run affected ({what}): {fileCount} changed → no affected tests");
            return null;
        }
        effects.Log(null, LogLevels.Info, $"Run affected ({what}): {fileCount} → {plan.Counts.Tests} test{Words.Plural(plan.Counts.Tests)} in " +
            $"{plan.Counts.Projects} project{Words.Plural(plan.Counts.Projects)} ({plan.Strategy})");
        var affected = new AffectedRun
        {
            Strategy = "git",
            ChangedFiles = plan.Files.Select(f => f.Path).ToList(),
            Base = plan.Base,
            Ref = plan.Ref,
            Tests = plan.Counts.Tests,
        };
        var r = requestRun(new Explorer.RunRequestOptions { Coverage = coverage, Trigger = trigger, Affected = affected, Selection = plan.Selection });
        if (r.Status != 202) effects.Log(null, LogLevels.Warning, "Run affected: " + r.Error);
        return r;
    }
}
