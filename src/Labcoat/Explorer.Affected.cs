namespace Labcoat;

// Run affected: the run and the runAffected command are in AffectedService.cs, the planning in AffectedPlanner.cs, the
// impact map file of --run --affected in ImpactMapFile.cs.
public sealed partial class Explorer
{
    /// <summary>Server option <c>affectedBase</c>: the default base ref of "branch vs …" (null = origin/HEAD → main/master).</summary>
    public string? AffectedBase { get; init; }

    private AffectedPlanner? _planner;
    private AffectedService? _affected;
    private ImpactMapFileService? _impactMapFiles;

    private AffectedPlanner Planner => LazyInitializer.EnsureInitialized(ref _planner,
        () => new AffectedPlanner(_state, _inputRoots, AffectedBase, _impact, _coverageCache, QueueImpactRefinementLocked));

    internal AffectedService Affected => LazyInitializer.EnsureInitialized(ref _affected, () => new AffectedService(this, _gate, _impact, Planner,
        pid => BuildImpactMapAsync(pid), RefineImpactMapAsync, RequestRun));

    /// <summary>--impact-map-file: stored paths are relative to the workspace root of the exports (git root, else the common root).</summary>
    internal ImpactMapFileService ImpactMapFiles => LazyInitializer.EnsureInitialized(ref _impactMapFiles, () => new ImpactMapFileService(_state, _impact,
        () => PathUtil.CanonicalizeOrSelf(Exports.Root ?? Planner.WorkspaceDir() ?? Directory.GetCurrentDirectory()), PublishSettings, Planner.HeadCommitAsync));
}
