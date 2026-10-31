namespace Labcoat;

// Several projects at once (docs/features.md "Running tests"): one build for every project that needs one
// (ProjectBuilder.cs), then discovery (TestDiscovery.cs) and runs of up to MaxParallelProjects projects in parallel.
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

    // ------------------------------------------------------------------ runs

    /// <summary>
    /// One repetition of a run: the projects run at the same time (up to <see cref="MaxParallelProjects"/>), each with its
    /// own cancellation (linked to the run's) so a test timeout stops that project only (HangMonitor.cs). Results stream
    /// as they come; the tree keeps its order (discovery order).
    /// </summary>
    private async Task RunProjectsOfIterationAsync(RunExecution x, List<(ProjectEntry Project, HashSet<string>? Ids)> ready,
        Dictionary<(string, string), Outcome> iterationResults, CancellationToken ct)
    {
        await ForEachProjectAsync(ready, async item =>
        {
            var (p, ids) = item;
            if (ct.IsCancellationRequested) return false;
            var projectRequested = x.Requested.Where(k => k.ProjectId == p.Project.Id).ToList();
            var project = new ProjectRun(p, ids, x.Run!, iterationResults, x.Quarantined, projectRequested, x.Request.Debug);
            using var scope = BeginProjectRun(project, ct);
            await RunProjectOfIterationAsync(x, project, scope.Token);
            if (scope.TimeoutReason is { } why) lock (x.Aborts) x.Aborts.Add(why);
            return true;
        }, ct);
    }
}
