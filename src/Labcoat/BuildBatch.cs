using System.Security.Cryptography;
using System.Text;

namespace Labcoat;

/// <summary>
/// One <c>dotnet build</c> for several test projects (Explorer.Parallel.cs): a generated solution lists them, so MSBuild
/// builds them in parallel (<c>-m</c>, the default of <c>dotnet build</c>), restores them as one graph and builds a
/// shared dependency once, instead of one build per project. The output is then split per project: which ones built
/// (<c>Name -&gt; path</c> lines, or exit code 0) and the diagnostics of each one and its ProjectReferences, so the UI
/// keeps showing build errors per project.
/// </summary>
public static class BuildBatch
{
    /// <summary>A project's part of a batch build.</summary>
    public sealed record Result(bool Succeeded, string Output);

    /// <summary>
    /// Writes the solution for <paramref name="projects"/> into <paramref name="dir"/> (classic .sln format, which every SDK
    /// reads; Debug|Any CPU like <c>dotnet build</c> of a project) and returns its path. The directory matters: the SDK
    /// resolver looks for global.json from the solution's directory, so it belongs in the workspace (an obj folder).
    /// </summary>
    public static string WriteSolution(IReadOnlyList<string> projects, string dir)
    {
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', projects)))).ToLowerInvariant()[..12];
        var path = Path.Combine(dir, $"labcoat-build-{hash}.sln");
        File.WriteAllText(path, SolutionText(projects));
        return path;
    }

    /// <summary>The text of the solution file.</summary>
    public static string SolutionText(IReadOnlyList<string> projects)
    {
        var sb = new StringBuilder();
        sb.Append("\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\n");
        sb.Append("VisualStudioVersion = 17.0.31903.59\r\nMinimumVisualStudioVersion = 10.0.40219.1\r\n");
        var guids = projects.Select(ProjectGuid).ToList();
        var names = UniqueNames(projects);
        for (var i = 0; i < projects.Count; i++)
            sb.Append($"Project(\"{TypeGuid(projects[i])}\") = \"{names[i]}\", \"{projects[i]}\", \"{guids[i]}\"\r\nEndProject\r\n");
        sb.Append("Global\r\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n\t\tDebug|Any CPU = Debug|Any CPU\r\n\tEndGlobalSection\r\n");
        sb.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n");
        foreach (var g in guids)
            sb.Append($"\t\t{g}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n\t\t{g}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n");
        sb.Append("\tEndGlobalSection\r\nEndGlobal\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// The projects' names in the solution, unique (MSBuild rejects a solution with two projects of the same name, e.g.
    /// a/X.csproj and b/X.csproj): repeats get "-2", "-3", … like Labcoat's project ids. Only the solution uses them.
    /// </summary>
    internal static List<string> UniqueNames(IReadOnlyList<string> projects)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(projects.Count);
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var unique = name;
            var suffix = 2;
            while (!used.Add(unique)) unique = $"{name}-{suffix++}";
            names.Add(unique);
        }
        return names;
    }

    private static string TypeGuid(string project) => Path.GetExtension(project).ToLowerInvariant() switch
    {
        ".fsproj" => "{F2A71F9B-5D33-465A-A702-920D77279786}",
        ".vbproj" => "{778DAE3C-4631-46EA-AA77-85C1314464D9}",
        _ => "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}",
    };

    private static string ProjectGuid(string project)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(project));
        return new Guid(bytes.AsSpan(0, 16)).ToString("B").ToUpperInvariant();
    }

    /// <summary>
    /// Splits a batch build's output per project. Exit code 0: every project built. Otherwise a project built when MSBuild
    /// printed its <c>Name -&gt; path</c> line and reported no error of the project itself. A project's output is the
    /// lines about it and its ProjectReferences (<paramref name="closureOf"/>: the project files it depends on, including
    /// itself) plus diagnostics that name no project; when nothing is about it (it failed because something it depends
    /// on did), it gets the whole output.
    /// </summary>
    public static Dictionary<string, Result> Split(string output, int exitCode, IReadOnlyList<string> projects,
        Func<string, IReadOnlyCollection<string>> closureOf)
    {
        var cmp = PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var lineProjects = lines.Select(ProjectOfLine).ToList();
        var result = new Dictionary<string, Result>(cmp);
        foreach (var project in projects)
            result[project] = exitCode == 0 ? new Result(true, "") : ResultOf(project, lines, lineProjects, closureOf, cmp);
        return result;
    }

    /// <summary>One project's part of a failed batch build.</summary>
    private static Result ResultOf(string project, List<string> lines, List<string?> lineProjects, Func<string, IReadOnlyCollection<string>> closureOf,
        StringComparer cmp)
    {
        var closure = new HashSet<string>(closureOf(project), cmp) { project };
        var mine = new List<string>();
        var ownError = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var lp = lineProjects[i];
            var error = IsError(lines[i]);
            if (lp is null ? error : closure.Contains(lp)) mine.Add(lines[i]); // no project: an error for all (MSBUILD : error …)
            if (lp is not null && error && cmp.Equals(lp, project)) ownError = true;
        }
        if (!ownError && HasOutputLine(lines, project)) return new Result(true, "");
        var output = mine.Count > 0 ? mine.Distinct() : lines;
        return new Result(false, string.Join('\n', output));
    }

    /// <summary>The project file a diagnostic line names (<c>… [path/x.csproj]</c> or <c>[x.csproj::TargetFramework=…]</c>).</summary>
    internal static string? ProjectOfLine(string line)
    {
        var trimmed = line.TrimEnd();
        if (!trimmed.EndsWith(']')) return null;
        var open = trimmed.LastIndexOf('[');
        if (open < 0) return null;
        var inner = trimmed[(open + 1)..^1];
        var sep = inner.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0) inner = inner[..sep];
        return inner.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ? inner.Trim() : null;
    }

    private static bool IsError(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase) && BuildOutputParser.Parse(line).Any(d => d.Severity == "error");

    /// <summary>MSBuild's <c>  Name -&gt; /path/Name.dll</c> line of the project (printed when its build succeeded).</summary>
    private static bool HasOutputLine(List<string> lines, string project)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var prefix = name + " -> ";
        return lines.Any(l => l.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
