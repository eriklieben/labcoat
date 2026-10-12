namespace Labcoat;

// The project list after startup (docs/features.md "The test tree"): new and removed test projects are picked up when
// project / solution / Directory.*.props files change under the workspace roots (ProjectWatcher.cs), and by Rediscover.
public sealed partial class Explorer
{
    /// <summary>
    /// Finds the workspace's test projects again (the same search as at startup; the argument receives warnings). Null:
    /// the project list is fixed (tests, embedding).
    /// </summary>
    public Func<Action<string>, List<string>>? ProjectFinder { get; init; }

    /// <summary>Directories watched for new or removed test projects (<see cref="StartProjectWatcher"/>).</summary>
    public IReadOnlyList<string> WorkspaceRoots { get; init; } = [];

    private ProjectWatcher? _projectWatcher;

    /// <summary>Starts watching <see cref="WorkspaceRoots"/> for project changes (no-op without a <see cref="ProjectFinder"/>).</summary>
    public void StartProjectWatcher()
    {
        if (ProjectFinder is null || WorkspaceRoots.Count == 0 || _projectWatcher is not null) return;
        _projectWatcher = new ProjectWatcher(WorkspaceRoots, OnProjectFilesChanged, warning => Log(null, LogLevels.Warning, warning))
        {
            ProjectFileNames = _runners.Plugins.Count == 0 ? new HashSet<string>()
                : _runners.Plugins.SelectMany(r => r.ConfigFiles).Append("package.json").ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// discover without a project (the Rediscover button): look for test projects again (new ones are added, removed ones
    /// dropped), then build and discover every project.
    /// </summary>
    public async Task RediscoverAsync()
    {
        await RefreshProjectListAsync(CurrentToken());
        await DiscoverAsync(null);
    }

    /// <summary>The discover command / POST /api/discover: one project, or (null) <see cref="RediscoverAsync"/>.</summary>
    public Task DiscoverCommandAsync(string? projectId) => projectId is null ? RediscoverAsync() : DiscoverAsync(projectId);

    /// <summary>Project, solution or Directory.*.props files changed: refresh the list, then build + discover what it affects.</summary>
    private void OnProjectFilesChanged(IReadOnlyCollection<string> changed) => _ = OnProjectFilesChangedAsync(changed);

    internal async Task OnProjectFilesChangedAsync(IReadOnlyCollection<string> changed)
    {
        if (_shutdownToken.IsCancellationRequested) return;
        var shown = string.Join(", ", changed.Select(Path.GetFileName).Distinct().Take(3)) + MoreSuffix(changed.Count, 3);
        Log(null, LogLevels.Info, $"Project files changed ({shown}); looking for test projects again");
        var ct = CurrentToken();
        var (added, _) = await RefreshProjectListAsync(ct);
        if (ct.IsCancellationRequested) return;
        if (changed.Any(ProjectWatcher.AffectsAll))
        {
            await DiscoverAsync(null);
            return;
        }
        var touched = new List<ProjectEntry>(added);
        lock (Sync)
        {
            // Watch mode rebuilds a project whose file changed by itself.
            if (!_watch)
                touched.AddRange(_projects.Where(p => !added.Contains(p) && changed.Any(c => string.Equals(PathUtil.CanonicalizeOrSelf(c), PathUtil.CanonicalizeOrSelf(p.Project.Path), PathUtil.Comparison))));
        }
        if (touched.Count > 0) await DiscoverProjectsAsync(touched);
    }

    /// <summary>Build + discover these projects (one operation).</summary>
    private async Task DiscoverProjectsAsync(IReadOnlyList<ProjectEntry> projects)
    {
        var ct = CurrentToken();
        if (!await AcquireOpAsync("discovery", ct)) return;
        try { await BuildAndDiscoverAsync(projects, onlyIfChanged: false, forceDiscovery: true, ct); }
        catch (Exception ex) { Log(null, "error", "Discovery failed: " + ex.Message); }
        finally { ReleaseOp(); }
    }

    /// <summary>
    /// Runs the project search again and applies the difference: new projects are added (and announced with a state
    /// snapshot), removed ones are dropped with their tests and their warm test host. Returns the added projects.
    /// </summary>
    private async Task<(List<ProjectEntry> Added, int Removed)> RefreshProjectListAsync(CancellationToken ct)
    {
        if (ProjectFinder is not { } finder) return ([], 0);
        if (!await AcquireOpAsync("project discovery", ct)) return ([], 0);
        var warnings = new List<string>();
        try
        {
            var found = await Task.Run(() => finder(warnings.Add), ct);
            var (added, removed) = ApplyProjectList(found);
            if (added.Count > 0 || removed.Count > 0)
            {
                Log(null, LogLevels.Info, ProjectListLine(added, removed));
                PublishState();
            }
            foreach (var p in removed) StopProject(p);
            return (added, removed.Count);
        }
        catch (OperationCanceledException) { return ([], 0); }
        catch (Exception ex)
        {
            Log(null, LogLevels.Warning, "Looking for test projects failed: " + ex.Message);
            return ([], 0);
        }
        finally
        {
            ReleaseOp();
            foreach (var w in warnings.Distinct()) Log(null, LogLevels.Warning, w);
        }
    }

    private static string ProjectListLine(List<ProjectEntry> added, List<ProjectEntry> removed)
    {
        var parts = new List<string>();
        if (added.Count > 0) parts.Add($"new test project{Plural(added.Count)}: {string.Join(", ", added.Select(p => p.Project.Name))}");
        if (removed.Count > 0) parts.Add($"removed: {string.Join(", ", removed.Select(p => p.Project.Name))}");
        return "Test projects changed: " + string.Join("; ", parts);
    }

    /// <summary>Adds the new paths and drops the projects that are gone (under Sync); the project order follows <paramref name="found"/>.</summary>
    private (List<ProjectEntry> Added, List<ProjectEntry> Removed) ApplyProjectList(List<string> found)
    {
        var cmp = PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var foundSet = found.Select(PathUtil.CanonicalizeOrSelf).ToHashSet(cmp);
        lock (Sync)
        {
            var removed = _projects.Where(p => !foundSet.Contains(PathUtil.CanonicalizeOrSelf(p.Project.Path))).ToList();
            foreach (var p in removed) RemoveProjectLocked(p);
            var known = _projects.Select(p => PathUtil.CanonicalizeOrSelf(p.Project.Path)).ToHashSet(cmp);
            var added = new List<ProjectEntry>();
            foreach (var path in found.Where(f => !known.Contains(PathUtil.CanonicalizeOrSelf(f))))
            {
                var p = NewProjectEntry(path, UniqueProjectIdLocked(ProjectNameOf(path)));
                _projects.Add(p);
                added.Add(p);
                AdoptProjectDataLocked(p);
                if (_watch) StartWatching(p);
            }
            if (added.Count > 0)
            {
                // Keep the order of the search (solution / folder order), as at startup.
                var order = found.Select(PathUtil.CanonicalizeOrSelf).ToList();
                var sorted = _projects.OrderBy(p => order.FindIndex(o => cmp.Equals(o, PathUtil.CanonicalizeOrSelf(p.Project.Path)))).ToList();
                _projects.Clear();
                _projects.AddRange(sorted);
            }
            return (added, removed);
        }
    }

    /// <summary>
    /// A new project's workspace data: its history (and quarantine entries) are saved under its relative path from now on,
    /// and what was kept under that path — the project was here before — is its own again, the full coverage part too.
    /// </summary>
    private void AdoptProjectDataLocked(ProjectEntry p)
    {
        var id = p.Project.Id;
        _history.RegisterProject(id, p.Project.Path);
        if (_history.ProjectPathOf(id) is { } rel && _fullCoverage.RenameProject(rel, id) is not null)
            Log(null, LogLevels.Info, $"Restored the full coverage of {p.Project.Name}");
    }

    /// <summary>A removed project's data stays under its relative path (it ages out there), so its id can go to another project.</summary>
    private void ReleaseProjectDataLocked(ProjectEntry p)
    {
        var id = p.Project.Id;
        if (_history.ProjectPathOf(id) is { } rel) _fullCoverage.RenameProject(id, rel);
        _history.ForgetProject(id);
    }

    /// <summary>A free id for a new project: its file name, "-2", "-3", … when taken (case-insensitive, as at startup).</summary>
    private string UniqueProjectIdLocked(string name)
    {
        var taken = _projects.Select(p => p.Project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range(1, taken.Count + 1).Select(i => i == 1 ? name : $"{name}-{i}").First(id => !taken.Contains(id));
    }

    /// <summary>Drops a project and its tests (caller holds Sync); its processes are stopped by <see cref="StopProject"/>.</summary>
    private void RemoveProjectLocked(ProjectEntry p)
    {
        _projects.Remove(p);
        var id = p.Project.Id;
        foreach (var key in _order.Where(k => k.ProjectId == id).ToList()) _tests.Remove(key);
        _order.RemoveAll(k => k.ProjectId == id);
        StopWatching(p);
        ReleaseProjectDataLocked(p);
    }

    /// <summary>A removed project's warm test host stops in the background.</summary>
    private void StopProject(ProjectEntry p)
    {
        WarmSession? w;
        lock (Sync) w = p.Warm;
        if (w is not null) RetireWarm(p, w, "the project is no longer part of the workspace");
    }

    /// <summary>Where a project's first discovered tests go in the test order (caller holds Sync): after the tests of the
    /// projects before it, so the tree keeps the project order however discoveries finish in parallel.</summary>
    private int FirstTestIndexLocked(ProjectEntry p)
    {
        var existing = _order.FindIndex(k => k.ProjectId == p.Project.Id);
        if (existing >= 0) return existing;
        var before = _projects.TakeWhile(x => x != p).Select(x => x.Project.Id).ToHashSet(StringComparer.Ordinal);
        var last = _order.FindLastIndex(k => before.Contains(k.ProjectId));
        return last + 1;
    }

    private void StopProjectWatcher()
    {
        _projectWatcher?.Dispose();
        _projectWatcher = null;
    }
}
