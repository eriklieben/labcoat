using System.Diagnostics;

namespace Labcoat;

// Several projects at once (docs/features.md "Running tests"): one build for every project that needs one
// (BuildBatch.cs), then discovery and runs of up to MaxParallelProjects projects in parallel.
public sealed partial class Explorer
{
    /// <summary>
    /// Projects discovered / run at the same time (maxParallelProjects, <c>--max-parallel</c>, LABCOAT_MAX_PARALLEL;
    /// default min(processor count, 4)). 1 = one project after the other.
    /// </summary>
    public int MaxParallelProjects { get; init; } = RunLimits.DefaultMaxParallel;

    /// <summary>
    /// Builds the projects (<see cref="BuildProjectsAsync"/>), then discovers them in parallel: always
    /// (<paramref name="forceDiscovery"/>, the discover command) or when their output changed since the last discovery (a
    /// run). Returns the projects that are ready to run, in the order given.
    /// </summary>
    private async Task<List<ProjectEntry>> BuildAndDiscoverAsync(IReadOnlyList<ProjectEntry> projects, bool onlyIfChanged, bool forceDiscovery,
        CancellationToken ct)
    {
        // projects that aren't on Microsoft.Testing.Platform are never built or started (Explorer.Platform.cs)
        projects = await ProjectsOnMtpAsync(projects, ct);
        var built = await BuildProjectsAsync(projects, ct, onlyIfChanged);
        if (ct.IsCancellationRequested || built.Count == 0) return [];
        return await ForEachProjectAsync(built, p => forceDiscovery ? DiscoverProjectAsync(p, ct) : EnsureDiscoveredAsync(p, ct), ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> for every item, at most <see cref="MaxParallelProjects"/> at a time, in the order given
    /// (a queued item starts when a running one ends). Returns the items whose work returned true, in the order given.
    /// Cancellation stops the items that have not started; an exception of one item is rethrown once all have ended.
    /// </summary>
    private async Task<List<T>> ForEachProjectAsync<T>(IReadOnlyList<T> items, Func<T, Task<bool>> work, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var results = new bool[items.Count];
        if (items.Count == 1 || MaxParallelProjects <= 1)
        {
            for (var i = 0; i < items.Count && !ct.IsCancellationRequested; i++) results[i] = await work(items[i]);
            return items.Where((_, i) => results[i]).ToList();
        }
        using var gate = new SemaphoreSlim(MaxParallelProjects);
        var tasks = items.Select(async (item, i) =>
        {
            try { await gate.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (!ct.IsCancellationRequested) results[i] = await work(item);
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
        return items.Where((_, i) => results[i]).ToList();
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Builds the projects; returns the ones that built (or were up to date), in the order given. A project without source
    /// changes since its last build is skipped (<paramref name="onlyIfChanged"/>); the others build together in one
    /// <c>dotnet build</c> of a generated solution (<see cref="BuildBatch"/>), so MSBuild builds them in parallel and a
    /// shared dependency once (never two builds of one project at the same time). A multi-targeted project that builds
    /// one framework (<c>-f</c>) builds on its own afterwards.
    /// </summary>
    private async Task<List<ProjectEntry>> BuildProjectsAsync(IReadOnlyList<ProjectEntry> projects, CancellationToken ct, bool onlyIfChanged)
    {
        if (projects.Count <= 1) return await BuildOneByOneAsync(projects, ct, onlyIfChanged);
        var (ready, batch, separate) = PlanBuilds(projects, onlyIfChanged);
        if (batch.Count > 0) ready.UnionWith(await BuildBatchAsync(batch, ct));
        ready.UnionWith(await BuildOneByOneAsync(separate, ct));
        return projects.Where(ready.Contains).ToList();
    }

    /// <summary>Up to date (no build), in the batch, or on their own (one framework; a batch of one).</summary>
    private (HashSet<ProjectEntry> UpToDate, List<ProjectEntry> Batch, List<ProjectEntry> Separate) PlanBuilds(IReadOnlyList<ProjectEntry> projects, bool onlyIfChanged)
    {
        var upToDate = new HashSet<ProjectEntry>();
        var batch = new List<ProjectEntry>();
        var separate = new List<ProjectEntry>();
        foreach (var p in projects)
        {
            if (onlyIfChanged && IsBuildUpToDate(p))
            {
                Log(p.Project.Id, "info", "No source changes since last build; skipping build");
                upToDate.Add(p);
            }
            else if (BuildsOneFramework(p)) separate.Add(p);
            else batch.Add(p);
        }
        if (batch.Count == 1)
        {
            separate.Insert(0, batch[0]);
            batch.Clear();
        }
        return (upToDate, batch, separate);
    }

    /// <summary>The project's output exists and no watched source file changed since its last successful build started.</summary>
    private static bool IsBuildUpToDate(ProjectEntry p) =>
        p.LastBuildStartUtc is { } last && p.TargetPath is not null && File.Exists(p.TargetPath) && !SourcesChangedSince(p, last);

    /// <summary>A multi-targeted project whose tests run on one framework: built with <c>-f</c>, which a solution build can't pass.</summary>
    private bool BuildsOneFramework(ProjectEntry p)
    {
        lock (Sync) return p.Project.AvailableFrameworks is { Count: > 1 } && p.Project.TargetFramework is not null;
    }

    /// <summary>One <c>dotnet build</c> of a generated solution with <paramref name="batch"/>; returns the projects that built.</summary>
    private async Task<List<ProjectEntry>> BuildBatchAsync(List<ProjectEntry> batch, CancellationToken ct)
    {
        var buildStart = DateTime.UtcNow;
        foreach (var p in batch)
        {
            await StopWarmBeforeBuildAsync(p); // Windows only: loaded assemblies are locked
            SetStatus(p, ProjectStatus.Building);
        }
        var paths = batch.Select(p => p.Project.Path).ToList();
        Log(null, "info", $"Building {batch.Count} projects in one build: {string.Join(", ", batch.Select(p => p.Project.Name))}");
        var cwd = Path.GetDirectoryName(paths[0])!;
        string solution;
        try { solution = BuildBatch.WriteSolution(paths, Path.Combine(cwd, "obj", "labcoat")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(null, "warning", $"Could not write the build solution ({ex.Message}); building the projects one by one.");
            return await BuildOneByOneAsync(batch, ct);
        }
        var sw = Stopwatch.StartNew();
        int code;
        string output;
        try { (code, output) = await RunDotnetAsync(["build", solution, "-nologo", "-tl:off", "-v:minimal"], cwd, ct); }
        finally { TryDelete(solution); }
        if (ct.IsCancellationRequested)
        {
            foreach (var p in batch) SetStatus(p, ProjectStatus.Idle);
            return [];
        }
        var elapsed = sw.Elapsed;
        var split = BuildBatch.Split(output, code, paths, BuildClosure);
        return await ForEachProjectAsync(batch, async p =>
        {
            var r = split[p.Project.Path];
            if (!r.Succeeded)
            {
                ReportBuildFailure(p, code == 0 ? 1 : code, r.Output, elapsed);
                return false;
            }
            return await CompleteBuildAsync(p, buildStart, elapsed, ct);
        }, ct);
    }

    private async Task<List<ProjectEntry>> BuildOneByOneAsync(IReadOnlyList<ProjectEntry> projects, CancellationToken ct, bool onlyIfChanged = false)
    {
        var ok = new List<ProjectEntry>();
        foreach (var p in projects)
            if (!ct.IsCancellationRequested && await BuildAsync(p, ct, onlyIfChanged)) ok.Add(p);
        return ok;
    }

    /// <summary>After a successful build of the project: its evaluation (TargetPath, frameworks, references), then idle.</summary>
    private async Task<bool> CompleteBuildAsync(ProjectEntry p, DateTime buildStart, TimeSpan elapsed, CancellationToken ct)
    {
        Log(p.Project.Id, "info", $"Build succeeded in {elapsed.TotalSeconds:0.0}s");
        lock (Sync) p.Project.ResultsOutdated = null; // published with the Idle status below
        try
        {
            await ResolveTargetPathAsync(p, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(p, ProjectStatus.Idle);
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(p, ProjectStatus.Error, ex.Message);
            Log(p.Project.Id, "error", ex.Message);
            return false;
        }
        if (!AcceptsPlatformAfterBuild(p)) return false;
        p.LastBuildStartUtc = buildStart;
        InvalidateWarmIfStale(p);
        SetStatus(p, ProjectStatus.Idle);
        return true;
    }

    /// <summary>The project file and the project files it references, transitively (read from the files, for build error attribution).</summary>
    private static IReadOnlyCollection<string> BuildClosure(string project)
    {
        var seen = new HashSet<string>(PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([project]);
        while (queue.TryDequeue(out var proj))
        {
            if (!seen.Add(proj) || !File.Exists(proj)) continue;
            foreach (var r in ProjectReferencesOf(proj, assembliesOnly: false)) queue.Enqueue(r);
        }
        return seen;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover in obj/labcoat */ }
    }

    // ------------------------------------------------------------------ runs

    /// <summary>
    /// One repetition of a run: the projects run at the same time (up to <see cref="MaxParallelProjects"/>), each with its
    /// own cancellation (linked to the run's) so a test timeout stops that project only (Explorer.Hang.cs). Results stream
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
