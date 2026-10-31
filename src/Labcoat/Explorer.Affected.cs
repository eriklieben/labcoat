namespace Labcoat;

// Run affected: the run and the runAffected command are in AffectedService.cs, the planning in AffectedPlanner.cs.
public sealed partial class Explorer
{
    /// <summary>Server option <c>affectedBase</c>: the default base ref of "branch vs …" (null = origin/HEAD → main/master).</summary>
    public string? AffectedBase { get; init; }

    private AffectedService? _affected;

    internal AffectedService Affected => LazyInitializer.EnsureInitialized(ref _affected, () => new AffectedService(this, _gate, _impact,
        new AffectedPlanner(_state, _inputRoots, AffectedBase, _impact, _coverageCache, QueueImpactRefinementLocked),
        pid => BuildImpactMapAsync(pid), RefineImpactMapAsync, RequestRun));
}
