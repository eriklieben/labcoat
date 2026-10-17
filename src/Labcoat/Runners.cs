using System.Text.Json;
using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>
/// A test runner besides Labcoat's own (.NET test apps on Microsoft.Testing.Platform), installed as a package such as
/// Labcoat.Vitest: a folder with a <c>runner.json</c> and a test host that speaks MTP server mode (docs/runners.md).
/// Labcoat finds its projects (<see cref="IsProjectDirectory"/>), never builds them, and starts the host with
/// <c>--project &lt;project file&gt;</c> plus the usual <c>--server --client-port</c> arguments.
/// </summary>
public sealed record RunnerPlugin
{
    public const string ManifestFileName = "runner.json";
    /// <summary>The placeholder in <see cref="Command"/> for <see cref="Directory"/>.</summary>
    private const string RunnerDirToken = "{runner}";

    /// <summary>"vitest": the name in --runners / LABCOAT_RUNNERS / TestProject.runner.</summary>
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Version { get; init; }
    /// <summary>The folder of the runner.json.</summary>
    public required string Directory { get; init; }
    /// <summary>The test host's command line, e.g. ["node", "{runner}/bridge.mjs"]: the first item is looked up on PATH.</summary>
    public required IReadOnlyList<string> Command { get; init; }
    /// <summary>A folder with one of these files is a project of this runner (e.g. vitest.config.ts).</summary>
    public IReadOnlyList<string> ConfigFiles { get; init; } = [];
    /// <summary>A folder whose package.json depends on one of these packages is a project of this runner.</summary>
    public IReadOnlyList<string> PackageDependencies { get; init; } = [];
    /// <summary>Source files whose changes watch mode and the up-to-date checks look at (".ts", ".js", ...).</summary>
    public IReadOnlyList<string> SourceExtensions { get; init; } = [];
    /// <summary>The host collects Cobertura coverage with --coverage --coverage-output-format cobertura --coverage-output &lt;file&gt;.</summary>
    public bool Coverage { get; init; }

    /// <summary>The runner described by <paramref name="manifestPath"/>; null (and a warning) when it can't be read.</summary>
    public static RunnerPlugin? Load(string manifestPath, Action<string> warn)
    {
        JsonNode? json;
        try { json = JsonNode.Parse(File.ReadAllText(manifestPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warn($"Could not read the test runner {manifestPath}: {ex.Message}");
            return null;
        }
        var id = Str(json?["id"])?.Trim().ToLowerInvariant();
        var command = Strings(json?["command"]);
        if (string.IsNullOrEmpty(id) || command.Count == 0 || TestRunners.IsDotnetId(id))
        {
            warn($"Ignoring the test runner {manifestPath}: it needs an \"id\" (not 'dotnet') and a \"command\".");
            return null;
        }
        return new RunnerPlugin
        {
            Id = id,
            DisplayName = Str(json?["displayName"]) ?? id,
            Version = Str(json?["version"]),
            Directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!,
            Command = command,
            ConfigFiles = Strings(json?["configFiles"]),
            PackageDependencies = Strings(json?["packageDependencies"]),
            SourceExtensions = Strings(json?["sourceExtensions"]).Select(e => e.StartsWith('.') ? e : "." + e).ToList(),
            Coverage = json?["coverage"] is JsonValue v && v.TryGetValue<bool>(out var c) && c,
        };
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static List<string> Strings(JsonNode? n) =>
        n is JsonArray a ? a.Select(Str).OfType<string>().ToList() : [];

    /// <summary>
    /// <paramref name="dir"/> is a project of this runner: it has one of the <see cref="ConfigFiles"/>, or a package.json
    /// that depends on one of the <see cref="PackageDependencies"/>. The project file is the package.json when there is
    /// one, else the config file.
    /// </summary>
    public bool IsProjectDirectory(string dir, out string projectFile)
    {
        projectFile = "";
        var packageJson = Path.Combine(dir, "package.json");
        var hasPackage = File.Exists(packageJson);
        var config = ConfigFiles.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
        if (config is null && !(hasPackage && DependsOnRunner(packageJson))) return false;
        projectFile = hasPackage ? packageJson : config!;
        return true;
    }

    /// <summary>An explicitly given file that is a project of this runner: one of its config files, or such a package.json.</summary>
    public bool IsProjectFile(string path)
    {
        var name = Path.GetFileName(path);
        if (ConfigFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        return name.Equals("package.json", StringComparison.OrdinalIgnoreCase) && IsProjectDirectory(Path.GetDirectoryName(path)!, out _);
    }

    private bool DependsOnRunner(string packageJson)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(packageJson)) is not JsonObject pkg) return false;
            return new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" }
                .Any(section => pkg[section] is JsonObject deps && PackageDependencies.Any(deps.ContainsKey));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>The test host of the project <paramref name="projectPath"/> (without the --server / extra arguments).</summary>
    public HostCommand HostCommand(string projectPath)
    {
        var args = Command.Skip(1).Select(a => a.Replace(RunnerDirToken, Directory, StringComparison.Ordinal)).ToList();
        args.AddRange(["--project", projectPath]);
        var program = Command[0].Replace(RunnerDirToken, Directory, StringComparison.Ordinal);
        return new HostCommand(ExecutableOnPath(program) ?? program, args, Path.GetDirectoryName(projectPath)!);
    }

    /// <summary>The full path of <paramref name="program"/> when it is a bare name found on PATH (also with a PATHEXT extension).</summary>
    internal static string? ExecutableOnPath(string program)
    {
        if (program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar)) return null;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("")
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, program + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

/// <summary>A test host process to start: the program, its arguments and its working directory.</summary>
public sealed record HostCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>
/// The test runners of this Labcoat: its own .NET runner (<see cref="IncludesDotnet"/>) and the runner packages in use
/// (<see cref="Plugins"/>). Chosen with --runners / LABCOAT_RUNNERS / the "runners" setting; by default the .NET runner
/// plus every installed runner package (<see cref="Resolve"/>).
/// </summary>
public sealed class TestRunners
{
    public const string DotnetId = "dotnet";
    /// <summary>--runner-path's environment variable: folders with runner packages (separated like PATH).</summary>
    public const string RunnerPathEnv = "LABCOAT_RUNNER_PATH";
    /// <summary>The runners to use: like --runners (Labcoat.Aspire.Hosting's WithRunners sets it).</summary>
    public const string RunnersEnv = "LABCOAT_RUNNERS";

    /// <summary>Only the .NET runner: what Labcoat does without runner packages.</summary>
    public static TestRunners DotnetOnly { get; } = new(true, []);

    public TestRunners(bool includesDotnet, IReadOnlyList<RunnerPlugin> plugins)
    {
        IncludesDotnet = includesDotnet;
        Plugins = plugins;
    }

    /// <summary>.NET test projects (Microsoft.Testing.Platform) are found and run.</summary>
    public bool IncludesDotnet { get; }
    public IReadOnlyList<RunnerPlugin> Plugins { get; }

    /// <summary>"dotnet, vitest".</summary>
    public string Describe() => string.Join(", ", (IncludesDotnet ? [DotnetId] : Array.Empty<string>()).Concat(Plugins.Select(p => p.Id)));

    /// <summary>The runner package of a project file found by <see cref="ProjectDiscovery"/>; null = a .NET project.</summary>
    public RunnerPlugin? PluginFor(string projectPath)
    {
        if (ProjectDiscovery.IsDotnetProjectFile(projectPath)) return null;
        return Plugins.FirstOrDefault(p => p.IsProjectFile(projectPath));
    }

    /// <summary>"dotnet" and the names it is also known by (the .NET test frameworks Labcoat runs on MTP).</summary>
    public static bool IsDotnetId(string id) =>
        id.Trim().ToLowerInvariant() is DotnetId or ".net" or "mtp" or "mstest" or "nunit" or "xunit" or "tunit";

    /// <summary>"dotnet,vitest" / "dotnet vitest" into ids; null when it names nothing.</summary>
    public static List<string>? ParseList(string? value)
    {
        var ids = (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => IsDotnetId(id) ? DotnetId : id.ToLowerInvariant()).Distinct().ToList();
        return ids.Count > 0 ? ids : null;
    }

    /// <summary>
    /// The folders searched for runner packages, in order: <c>runners/</c> next to Labcoat (Labcoat.Aspire.Hosting copies
    /// the runner packages of the AppHost there), <paramref name="extraDirs"/> (--runner-path) and LABCOAT_RUNNER_PATH.
    /// A folder may hold a runner.json itself or one runner per subfolder.
    /// </summary>
    public static List<string> SearchDirectories(IEnumerable<string> extraDirs) =>
    [
        Path.Combine(AppContext.BaseDirectory, "runners"),
        .. extraDirs,
        .. (Environment.GetEnvironmentVariable(RunnerPathEnv) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];

    /// <summary>The runner packages in <paramref name="dirs"/>; the first one with an id wins.</summary>
    public static List<RunnerPlugin> FindPlugins(IEnumerable<string> dirs, Action<string> warn)
    {
        var found = new List<RunnerPlugin>();
        foreach (var dir in dirs.Select(Path.GetFullPath).Distinct(PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> manifests;
            try
            {
                var own = Path.Combine(dir, RunnerPlugin.ManifestFileName);
                manifests = File.Exists(own)
                    ? [own]
                    : Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal)
                        .Select(d => Path.Combine(d, RunnerPlugin.ManifestFileName)).Where(File.Exists).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var manifest in manifests)
            {
                if (RunnerPlugin.Load(manifest, warn) is { } plugin && found.All(p => p.Id != plugin.Id)) found.Add(plugin);
            }
        }
        return found;
    }

    /// <summary>
    /// The runners to use. <paramref name="requested"/> null: the .NET runner plus every installed runner package.
    /// Otherwise exactly those; a runner that isn't installed is fetched with <paramref name="fetch"/> (null when that
    /// fails too: <paramref name="error"/> says what to install).
    /// </summary>
    public static TestRunners? Resolve(IReadOnlyList<string>? requested, IReadOnlyList<RunnerPlugin> installed, Func<string, RunnerPlugin?> fetch,
        out string? error)
    {
        error = null;
        if (requested is null) return new TestRunners(true, installed);
        var plugins = new List<RunnerPlugin>();
        foreach (var id in requested.Where(id => id != DotnetId))
        {
            var plugin = installed.FirstOrDefault(p => p.Id == id) ?? fetch(id);
            if (plugin is null)
            {
                error = $"The test runner '{id}' isn't installed. Add the {RunnerPackages.PackageIdOf(id)} package to the Aspire AppHost, " +
                        $"or pass the folder of its runner with --runner-path (installed: {Describe(installed)}).";
                return null;
            }
            plugins.Add(plugin);
        }
        return new TestRunners(requested.Contains(DotnetId), plugins);
    }

    private static string Describe(IReadOnlyList<RunnerPlugin> installed) =>
        installed.Count == 0 ? "none besides dotnet" : string.Join(", ", installed.Select(p => p.Id));
}
