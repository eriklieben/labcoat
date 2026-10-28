namespace Labcoat;

// Run affected: the planning is in AffectedPlanner.cs; this part queues the run and handles the commands.
public sealed partial class Explorer
{
    /// <summary>Server option <c>affectedBase</c>: the default base ref of "branch vs …" (null = origin/HEAD → main/master).</summary>
    public string? AffectedBase { get; init; }

    private AffectedPlanner? _affected;

    private AffectedPlanner Affected => LazyInitializer.EnsureInitialized(ref _affected, () => new AffectedPlanner(_state, _inputRoots, AffectedBase, _impact, _coverageCache, QueueImpactRefinementLocked));

    /// <summary>GET /api/git: branch, HEAD, dirty count, default base (cached ~2 s).</summary>
    public Task<GitInfo> GitInfoAsync(CancellationToken ct = default) => Affected.GitInfoAsync(ct);

    /// <summary>GET /api/changed-tests: the tests added or modified on the current branch (AffectedPlanner.ChangedTestsAsync).</summary>
    public Task<ChangedTests> ChangedTestsAsync(CancellationToken ct = default) => Affected.ChangedTestsAsync(ct);

    /// <summary>Drops the cached answer of <see cref="ChangedTestsAsync"/> (the next call asks git again).</summary>
    internal void InvalidateChangedTests() => Affected.InvalidateChangedTests();

    internal static bool IsChanged(TestNode test, GitChange change) => AffectedPlanner.IsChanged(test, change);

    /// <summary>GET /api/affected: which tests the changes of <paramref name="baseKind"/> affect. Never runs anything.</summary>
    public Task<AffectedPlan> PlanAffectedAsync(string? baseKind, string? reference, CancellationToken ct = default) =>
        Affected.PlanAsync(baseKind, reference, ct);

    private static readonly Dictionary<string, string> BaseLabels = new()
    {
        [AffectedBases.Working] = "uncommitted changes", [AffectedBases.Branch] = "branch", [AffectedBases.Range] = "range",
    };

    /// <summary>Result of <see cref="RunAffectedAsync"/>.</summary>
    public sealed record RunAffectedResult(RunRequestResult? Run, AffectedPlan? Plan, bool ImpactMapFirst = false);

    /// <summary>
    /// runAffected / POST /api/run-affected: plan, then queue one run with the exact selection. With
    /// <paramref name="buildImpactMap"/> and product files without impact data, the impact-map pass runs first (in the
    /// background) and the plan is recomputed before the run. Throws <see cref="AffectedException"/>.
    /// </summary>
    public async Task<RunAffectedResult> RunAffectedAsync(string? baseKind, string? reference, bool? coverage, bool buildImpactMap, string trigger,
        CancellationToken ct = default)
    {
        var plan = await PlanAffectedAsync(baseKind, reference, ct);
        // impactMap "auto": "build impact map first" also refines the classes the plan selected through class-level data.
        List<(string ProjectId, string Id)> refine = ImpactMapMode == ImpactMapModes.Auto ? plan.ClassLevel : [];
        if (buildImpactMap && (plan.ProjectsNeedingImpactMap.Count > 0 || refine.Count > 0))
        {
            var projects = plan.ProjectsNeedingImpactMap.ToList();
            Log(null, LogLevels.Info, projects.Count > 0
                ? $"Run affected: building the impact map for {string.Join(", ", projects)} first…"
                : "Run affected: refining the impact map per test first…");
            // Deliberately outlives the request (it answers right away): the job stops on shutdown, not on ct.
            _ = Task.Run(() => ImpactMapThenRunAffectedAsync(projects, plan, baseKind, reference, coverage, trigger), CancellationToken.None);
            return new RunAffectedResult(null, plan, ImpactMapFirst: true);
        }
        return new RunAffectedResult(StartAffectedRun(plan, coverage, trigger), plan);
    }

    /// <summary>The background part of "build impact map first": the impact map, then a new plan and its run.</summary>
    private async Task ImpactMapThenRunAffectedAsync(List<string> projects, AffectedPlan plan, string? baseKind, string? reference, bool? coverage,
        string trigger)
    {
        try
        {
            foreach (var pid in projects) await BuildImpactMapAsync(pid);
            if (ImpactMapMode == ImpactMapModes.Auto)
            {
                var mid = projects.Count > 0 ? await PlanAffectedAsync(baseKind, reference, _gate.ShutdownToken) : plan;
                await RefineImpactMapAsync(mid.ClassLevel);
            }
            var again = await PlanAffectedAsync(baseKind, reference, _gate.ShutdownToken);
            StartAffectedRun(again, coverage, trigger);
        }
        catch (AffectedException ex) { Log(null, LogLevels.Warning, "Run affected: " + ex.Message); }
        catch (Exception ex) when (!_gate.IsShuttingDown) { Log(null, LogLevels.Error, "Run affected failed: " + ex.Message); }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
    }

    /// <summary>WS runAffected: errors become a commandRejected reply for the sending client.</summary>
    private async Task<ServerEvent?> RunAffectedCommandAsync(ClientCommand cmd)
    {
        try
        {
            var r = await RunAffectedAsync(cmd.Base, cmd.Ref, cmd.Coverage, cmd.BuildImpactMap ?? false, "user", _gate.ShutdownToken);
            if (r.Run is { Status: not 202 } run) return new CommandRejectedEvent(cmd.Type, run.Status, run.Error ?? "rejected", run.UnknownTestIds);
            return null;
        }
        catch (AffectedException ex)
        {
            Log(null, LogLevels.Warning, "runAffected: " + ex.Message);
            return new CommandRejectedEvent(cmd.Type, ex.Status, ex.Message);
        }
        catch (OperationCanceledException) { return null; }
    }

    private RunRequestResult? StartAffectedRun(AffectedPlan plan, bool? coverage, string trigger)
    {
        var what = plan.Base switch
        {
            AffectedBases.Branch => $"branch vs {plan.Ref}",
            AffectedBases.Range => plan.Ref ?? "range",
            _ => BaseLabels[plan.Base],
        };
        var fileCount = $"{plan.Counts.Files} file{(plan.Counts.Files == 1 ? "" : "s")}";
        if (plan.Selection.Count == 0)
        {
            Log(null, LogLevels.Info, $"Run affected ({what}): {fileCount} changed → no affected tests");
            return null;
        }
        Log(null, LogLevels.Info, $"Run affected ({what}): {fileCount} → {plan.Counts.Tests} test{(plan.Counts.Tests == 1 ? "" : "s")} in " +
            $"{plan.Counts.Projects} project{(plan.Counts.Projects == 1 ? "" : "s")} ({plan.Strategy})");
        var affected = new AffectedRun
        {
            Strategy = "git",
            ChangedFiles = plan.Files.Select(f => f.Path).ToList(),
            Base = plan.Base,
            Ref = plan.Ref,
            Tests = plan.Counts.Tests,
        };
        var r = RequestRun(new RunRequestOptions { Coverage = coverage, Trigger = trigger, Affected = affected, Selection = plan.Selection });
        if (r.Status != 202) Log(null, LogLevels.Warning, "Run affected: " + r.Error);
        return r;
    }
}
