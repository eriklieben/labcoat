namespace Labcoat;

// Projects of runner packages (Runners.cs, docs/runners.md), such as Labcoat.Vitest: no build and no MSBuild evaluation;
// the project file (package.json / the runner's config file) stands in for the test app, and the runner's test host
// speaks the same server protocol as a .NET test app, so discovery, runs, the warm host, live output and coverage are
// shared with .NET projects. What differs is which files say that the tests or the code under test changed.
internal static class RunnerProjects
{
    /// <summary>The lock files that aren't *.json (those are in the output hashes already): a new one means new dependencies.</summary>
    private static readonly string[] LockFiles = ["pnpm-lock.yaml", "yarn.lock", "bun.lock", "bun.lockb"];

    /// <summary>The test host of a project: the runner package's, or the .NET test app (<c>dotnet &lt;TargetPath&gt;</c>).</summary>
    public static HostCommand HostCommandOf(ProjectEntry p) =>
        p.Plugin?.HostCommand(p.Project.Path) ?? new HostCommand(DotnetCli.Executable, [p.TargetPath!], Path.GetDirectoryName(p.TargetPath!)!);

    /// <summary>
    /// When the tests may have changed (re-discover): the test app's write time; for a runner package's project the newest
    /// source file (its runner only loads the files that changed since, so asking often is cheap).
    /// </summary>
    public static DateTime OutputStampOf(ProjectEntry p) =>
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
                foreach (var sub in Directory.EnumerateDirectories(d).Where(sub => !ProjectBuilder.IsIgnoredSourceDir(Path.GetFileName(sub)))) stack.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: skip it */ }
        }
        return latest;
    }

    /// <summary>The extensions of the files watch mode and the up-to-date checks look at.</summary>
    public static IReadOnlySet<string> SourceExtensionsOf(ProjectEntry p) =>
        p.Plugin is { } plugin ? plugin.SourceExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase) : WatchMode.Extensions;

    /// <summary>
    /// What the warm host's process depends on: the .NET test app's output (rebuilt = new process); for a runner package's
    /// project its configuration (the *.json files of its folder: package.json, package-lock.json, tsconfig.json; the other
    /// lock files and the runner's config files). Source changes don't count there: the runner reloads changed files itself.
    /// </summary>
    public static Dictionary<string, long> HostHashes(ProjectEntry p)
    {
        var hashes = OutputFingerprints.OutputHashes(p.TargetPath!);
        if (p.Plugin is { } plugin)
        {
            var dir = Path.GetDirectoryName(p.Project.Path)!;
            foreach (var config in plugin.ConfigFiles.Concat(LockFiles).Select(f => Path.Combine(dir, f)).Where(File.Exists))
                hashes[Path.GetFileName(config)] = OutputFingerprints.ContentHash(config);
        }
        return hashes;
    }

    /// <summary>
    /// Changes when the code under test may have changed (cached per-test coverage and other results go stale): the
    /// test app's output; for a runner package's project its configuration plus its newest source file.
    /// </summary>
    public static string CodeStampOf(ProjectEntry p) =>
        p.Plugin is null
            ? OutputFingerprints.Of(p.TargetPath!)
            : OutputFingerprints.FingerprintOf(HostHashes(p)) + ":" + LatestSourceWrite(p).Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}
