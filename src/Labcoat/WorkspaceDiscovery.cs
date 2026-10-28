namespace Labcoat;

/// <summary>Which test projects and directories make up the workspace.</summary>
internal static class WorkspaceDiscovery
{
    /// <summary>The test projects of <paramref name="paths"/>; <paramref name="error"/> is the first path error (the others are printed).</summary>
    internal static List<string> FindProjects(List<string> paths, List<string> startupNotes, out string? error)
    {
        var pathErrors = new List<string>();
        var projects = ProjectDiscovery.FindProjects(paths, Cli.Warn, pathErrors, info: startupNotes.Add);
        error = null;
        if (pathErrors.Count == 0) return projects;
        foreach (var e in pathErrors.Skip(1)) Console.Error.WriteLine($"{Cli.Red("error")} {e}");
        error = pathErrors[0];
        return projects;
    }

    /// <summary>
    /// The directories watched for new or removed test projects: each input folder, or the folder of an input solution
    /// (a project file given on the command line is the whole workspace: nothing to watch).
    /// </summary>
    internal static List<string> WorkspaceRootsOf(IEnumerable<string> paths) =>
        paths.Select(Path.GetFullPath)
            .Where(p => Directory.Exists(p) || Path.GetExtension(p).ToLowerInvariant() is ".sln" or ".slnx" or ".slnf")
            .Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p))
            .OfType<string>()
            .Distinct(PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();
}
