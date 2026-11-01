namespace Labcoat;

/// <summary>Derives ExplorerState.workspaceName.</summary>
public static class WorkspaceName
{
    /// <summary>
    /// <paramref name="explicitName"/> (LABCOAT_NAME, the Aspire resource name) wins. Otherwise: a single solution
    /// input (or the only solution in a single input directory) gives the solution name; else a single test project
    /// gives the project name (a runner project's folder: every Vitest project file is a package.json); else the name of
    /// the input directory / common root.
    /// </summary>
    public static string Resolve(string? explicitName, IEnumerable<string> inputPaths, IEnumerable<string> projectPaths)
    {
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName.Trim();
        var inputs = inputPaths.Select(Path.GetFullPath).ToList();
        var projects = projectPaths.ToList();
        if (inputs.Count == 1 && SolutionName(inputs[0]) is { } solution) return solution;
        if (projects.Count == 1) return ProjectDiscovery.ProjectNameOf(projects[0]);
        var root = PathUtil.CommonRoot(inputs.Count > 0 ? inputs : projects);
        var name = root is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return string.IsNullOrEmpty(name) ? "Labcoat" : name;
    }

    /// <summary>The solution's name for a solution input, or for a directory with exactly one solution; else null.</summary>
    private static string? SolutionName(string input)
    {
        var ext = Path.GetExtension(input).ToLowerInvariant();
        if (File.Exists(input) && ext is ".sln" or ".slnx" or ".slnf") return Path.GetFileNameWithoutExtension(input);
        if (!Directory.Exists(input)) return null;
        try
        {
            var solutions = Directory.EnumerateFiles(input, "*.sln").Concat(Directory.EnumerateFiles(input, "*.slnx")).ToList();
            return solutions.Count == 1 ? Path.GetFileNameWithoutExtension(solutions[0]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; /* unreadable input folder: no name from it */ }
    }
}
