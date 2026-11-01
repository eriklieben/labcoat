namespace Labcoat;

/// <summary>Which test projects and directories make up the workspace.</summary>
internal static class WorkspaceDiscovery
{
    /// <summary>The test projects of <paramref name="paths"/>, and the first path error (the others are printed).</summary>
    internal static async Task<(List<string> Projects, string? Error)> FindProjectsAsync(List<string> paths, TestRunners runners, List<string> startupNotes,
        CancellationToken ct)
    {
        var pathErrors = new List<string>();
        var projects = await ProjectDiscovery.FindProjectsAsync(paths, Cli.Warn, pathErrors, info: startupNotes.Add, runners, ct);
        if (pathErrors.Count == 0) return (projects, null);
        foreach (var e in pathErrors.Skip(1)) await Console.Error.WriteLineAsync($"{Cli.Red("error")} {e}");
        return (projects, pathErrors[0]);
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
