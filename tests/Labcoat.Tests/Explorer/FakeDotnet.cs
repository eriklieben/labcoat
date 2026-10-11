using System.Text.Json.Nodes;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// Stands in for the `dotnet` processes an explorer starts (<see cref="Labcoat.Explorer.DotnetOverride"/>): build,
/// MSBuild evaluation and the `--help` coverage probe. Records the calls.
/// </summary>
internal sealed class FakeDotnet
{
    private readonly List<string[]> _calls = [];

    public int BuildExitCode { get; set; }
    public string BuildOutput { get; set; } = "Build succeeded.";
    /// <summary>When set, a build waits for cancellation (like a long build) and then reports it.</summary>
    public bool BuildHangs { get; set; }
    /// <summary>JSON of `dotnet msbuild -getProperty… -getItem…`; null = the evaluation fails.</summary>
    public string? Evaluation { get; set; }
    /// <summary>Output of the second evaluation (`-getProperty:TargetPath -p:TargetFramework=…`); null = it fails.</summary>
    public string? TargetPathForFramework { get; set; }
    public string Help { get; set; } = "--coverage --coverage-output-format <format>";

    public List<string[]> Calls { get { lock (_calls) return [.. _calls]; } }

    public async Task<(int ExitCode, string Output)> InvokeAsync(string[] args, string cwd, CancellationToken ct)
    {
        lock (_calls) _calls.Add(args);
        await Task.Yield();
        switch (args[0])
        {
            case "build":
                if (BuildHangs)
                {
                    try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                    return (-1, "cancelled");
                }
                return (BuildExitCode, BuildOutput);
            case "msbuild" when args.Any(a => a.StartsWith("-p:TargetFramework=", StringComparison.Ordinal)):
                return TargetPathForFramework is null ? (1, "error MSB1001: no") : (0, TargetPathForFramework + "\n");
            case "msbuild":
                return Evaluation is null ? (1, "error MSB4025: The project file could not be loaded.") : (0, Evaluation);
            default:
                return (0, Help);
        }
    }

    /// <summary>An evaluation result as `dotnet msbuild -getProperty:… -getItem:…` prints it.</summary>
    public static string Eval(string? targetPath, string? targetFrameworks = null, string targetFramework = "net10.0",
        IReadOnlyDictionary<string, string>? properties = null, IEnumerable<string>? projectReferences = null, IEnumerable<string>? packages = null)
    {
        var props = new JsonObject
        {
            ["TargetPath"] = targetPath ?? "",
            ["TargetFrameworks"] = targetFrameworks ?? "",
            ["TargetFramework"] = targetPath is null ? "" : targetFramework,
        };
        foreach (var (k, v) in properties ?? new Dictionary<string, string>()) props[k] = v;
        var items = new JsonObject
        {
            ["ProjectReference"] = new JsonArray([.. (projectReferences ?? []).Select(r => (JsonNode)new JsonObject { ["Identity"] = r, ["FullPath"] = r })]),
            ["PackageReference"] = new JsonArray([.. (packages ?? []).Select(p => (JsonNode)new JsonObject { ["Identity"] = p })]),
        };
        return new JsonObject { ["Properties"] = props, ["Items"] = items }.ToJsonString();
    }
}
