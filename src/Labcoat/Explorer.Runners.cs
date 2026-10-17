namespace Labcoat;

// Projects of runner packages (Runners.cs, docs/runners.md), such as Labcoat.Vitest: no build and no MSBuild evaluation;
// the project file (package.json / the runner's config file) stands in for the test app, and the runner's test host
// speaks the same server protocol as a .NET test app, so discovery, runs, the warm host, live output and coverage are
// shared with .NET projects.
public sealed partial class Explorer
{
    private readonly TestRunners _runners;

    /// <summary>The display name (and id) of a project: the .NET project's file name, or the folder of a runner package's project.</summary>
    private static string ProjectNameOf(string path) => ProjectDiscovery.ProjectNameOf(path);

    private ProjectEntry NewProjectEntry(string path, string id)
    {
        var plugin = _runners.PluginFor(path);
        return new ProjectEntry(new TestProject { Id = id, Name = ProjectNameOf(path), Path = path, Runner = plugin?.Id }) { Plugin = plugin };
    }

    /// <summary>
    /// The "build" of a runner package's project: nothing to compile (the runner transforms sources itself), so only what
    /// a build sets up: the test app path, the watched folder and the build time the up-to-date checks compare with.
    /// </summary>
    private void PrepareRunnerProject(ProjectEntry p)
    {
        var dir = Path.GetDirectoryName(p.Project.Path)!;
        p.TargetPath = p.Project.Path;
        p.TargetPathStamp = File.GetLastWriteTimeUtc(p.Project.Path);
        var changedWatch = p.WatchDirs is not [var only] || only != dir;
        p.WatchDirs = [dir];
        p.LastBuildStartUtc = DateTime.UtcNow;
        if (changedWatch && _watch) StartWatching(p);
        InvalidateWarmIfStale(p);
        lock (Sync) p.Project.ResultsOutdated = null;
        SetStatus(p, ProjectStatus.Idle);
    }

    /// <summary>The test host of a project: the runner package's, or the .NET test app (<c>dotnet &lt;TargetPath&gt;</c>).</summary>
    private static HostCommand HostCommandOf(ProjectEntry p) =>
        p.Plugin?.HostCommand(p.Project.Path) ?? new HostCommand(DotnetPath, [p.TargetPath!], Path.GetDirectoryName(p.TargetPath!)!);

    /// <summary>
    /// When the tests may have changed (re-discover): the test app's write time; for a runner package's project the newest
    /// source file (its runner only loads the files that changed since, so asking often is cheap).
    /// </summary>
    private static DateTime OutputStampOf(ProjectEntry p) =>
        p.Plugin is null ? File.GetLastWriteTimeUtc(p.TargetPath!) : LatestSourceWrite(p);

    /// <summary>The newest write time of the project's source files (bin, obj, node_modules and dot-folders skipped).</summary>
    private static DateTime LatestSourceWrite(ProjectEntry p)
    {
        var latest = File.GetLastWriteTimeUtc(p.Project.Path);
        var extensions = SourceExtensionsOf(p);
        var stack = new Stack<string>([Path.GetDirectoryName(p.Project.Path)!]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(d).Where(f => extensions.Contains(Path.GetExtension(f))))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > latest) latest = t;
                }
                foreach (var sub in Directory.EnumerateDirectories(d).Where(sub => !IsIgnoredSourceDir(Path.GetFileName(sub)))) stack.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: skip it */ }
        }
        return latest;
    }

    /// <summary>The extensions of the files watch mode and the up-to-date checks look at.</summary>
    private static IReadOnlySet<string> SourceExtensionsOf(ProjectEntry p) =>
        p.Plugin is { } plugin ? plugin.SourceExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase) : WatchedExtensions;

    /// <summary>
    /// What the warm host's process depends on: the .NET test app's output (rebuilt = new process); for a runner package's
    /// project its configuration (package.json, lock files, tsconfig, the runner's config files). Source changes don't
    /// count there: the runner reloads changed files itself.
    /// </summary>
    private static Dictionary<string, long> HostHashes(ProjectEntry p)
    {
        var hashes = OutputHashes(p.TargetPath!);
        if (p.Plugin is { } plugin)
        {
            var dir = Path.GetDirectoryName(p.Project.Path)!;
            foreach (var config in plugin.ConfigFiles.Select(f => Path.Combine(dir, f)).Where(File.Exists))
                hashes[Path.GetFileName(config)] = ContentHash(config);
        }
        return hashes;
    }

    /// <summary>
    /// Changes when the code under test may have changed (cached per-test coverage and other results go stale): the
    /// test app's output; for a runner package's project its configuration plus its newest source file.
    /// </summary>
    private static string CodeStampOf(ProjectEntry p) =>
        p.Plugin is null ? OutputFingerprint(p.TargetPath!) : FingerprintOf(HostHashes(p)) + ":" + LatestSourceWrite(p).Ticks.ToString("x");
}
