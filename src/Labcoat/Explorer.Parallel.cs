namespace Labcoat;

// Several projects at once (docs/features.md "Running tests"): one build for every project that needs one
// (ProjectBuilder.cs), then discovery (TestDiscovery.cs) and runs (RunExecutor.cs) of up to MaxParallelProjects projects in
// parallel.
public sealed partial class Explorer
{
    /// <summary>
    /// Projects discovered / run at the same time (maxParallelProjects, <c>--max-parallel</c>, LABCOAT_MAX_PARALLEL;
    /// default min(processor count, 4)). 1 = one project after the other.
    /// </summary>
    public int MaxParallelProjects { get; init; } = RunLimits.DefaultMaxParallel;

    private ProjectBuilder? _builder;
    private TestDiscovery? _discovery;

    private ProjectBuilder Builder => LazyInitializer.EnsureInitialized(ref _builder, () => new ProjectBuilder(_state, this, PlatformCheck, Hosts, WatchMode,
        _settings, MaxParallelProjects, RunDotnetAsync));

    private TestDiscovery Discovery => LazyInitializer.EnsureInitialized(ref _discovery, () => new TestDiscovery(_state, this, Hosts, _history, PublishState));

    /// <summary>
    /// Builds the projects (ProjectBuilder.cs), then discovers them in parallel: always
    /// (<paramref name="forceDiscovery"/>, the discover command) or when their output changed since the last discovery (a
    /// run). Returns the projects that are ready to run, in the order given.
    /// </summary>
    private async Task<List<ProjectEntry>> BuildAndDiscoverAsync(IReadOnlyList<ProjectEntry> projects, bool onlyIfChanged, bool forceDiscovery,
        CancellationToken ct)
    {
        // projects that aren't on Microsoft.Testing.Platform are never built or started (PlatformCheck.cs)
        projects = await ProjectsOnMtpAsync(projects, ct);
        var built = await Builder.BuildProjectsAsync(projects, onlyIfChanged, ct);
        if (ct.IsCancellationRequested || built.Count == 0) return [];
        return await ForEachProjectAsync(built, p => forceDiscovery ? Discovery.DiscoverAsync(p, ct) : Discovery.EnsureDiscoveredAsync(p, ct), ct);
    }

    /// <summary>Runs <paramref name="work"/> for every item, at most <see cref="MaxParallelProjects"/> at a time (BoundedParallel.cs).</summary>
    private Task<List<T>> ForEachProjectAsync<T>(IReadOnlyList<T> items, Func<T, Task<bool>> work, CancellationToken ct) =>
        BoundedParallel.ForEachAsync(items, MaxParallelProjects, work, ct);
}
