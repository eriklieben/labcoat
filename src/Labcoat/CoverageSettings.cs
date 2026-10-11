using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Labcoat;

/// <summary>
/// Coverage options from the command line, the settings files and the AppHost (docs/configuration.md "Code coverage"):
/// a settings file to use instead of the one Labcoat finds, and module / path globs to add to or drop from the report.
/// </summary>
public sealed record CoverageOptions
{
    /// <summary>A coverage settings file for every test project. Relative: to the test project's directory first, then <see cref="BaseDirectory"/>.</summary>
    public string? Settings { get; init; }
    /// <summary>Module names or path globs to count although Labcoat's defaults leave them out (e.g. <c>Labcoat.*</c>).</summary>
    public IReadOnlyList<string> Include { get; init; } = [];
    /// <summary>Module names or path globs never to count.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
    /// <summary>Where a relative <see cref="Settings"/> path is resolved when the project directory has no such file.</summary>
    public string? BaseDirectory { get; init; }

    public static CoverageOptions None { get; } = new();

    /// <summary>Later layers win for <see cref="Settings"/>; include / exclude lists add up.</summary>
    public CoverageOptions Merge(CoverageOptions? over)
    {
        if (over is null) return this;
        return new CoverageOptions
        {
            Settings = over.Settings ?? Settings,
            BaseDirectory = over.Settings is not null ? over.BaseDirectory : BaseDirectory,
            Include = [.. Include, .. over.Include.Where(g => !Include.Contains(g, StringComparer.OrdinalIgnoreCase))],
            Exclude = [.. Exclude, .. over.Exclude.Where(g => !Exclude.Contains(g, StringComparer.OrdinalIgnoreCase))],
        };
    }
}

/// <summary>Which coverage settings a project's coverage run used (CoverageReport.settings, the coverage tab and the log).</summary>
public sealed record CoverageSettingsInfo
{
    public required string ProjectId { get; init; }
    /// <summary>"file" (a coverage settings / runsettings file), "testconfig" (the codeCoverage section of the test app's
    /// testconfig.json, read by the extension itself) or "defaults" (Labcoat's built-in defaults).</summary>
    public required string Source { get; init; }
    /// <summary>The settings file (absolute); null for "defaults".</summary>
    public string? Path { get; init; }
    /// <summary>How the file was found: "option" (Coverage.Settings / --coverage-settings of Labcoat), "msbuild"
    /// (--coverage-settings in TestingPlatformCommandLineArguments) or "convention" (a file name Labcoat looks for).</summary>
    public string? Origin { get; init; }
    /// <summary>"dynamic" (the CLR profiler; no file is rewritten) or "static" (assemblies instrumented in place).</summary>
    public required string Instrumentation { get; init; }
    /// <summary>"defaults" only: the assemblies counted (the projects the test project references, transitively, minus
    /// Labcoat's own assemblies, test projects and test frameworks); null = every module except those.</summary>
    public List<string>? Modules { get; init; }
    /// <summary>Coverage.Include in effect (null when empty).</summary>
    public List<string>? Include { get; init; }
    /// <summary>Coverage.Exclude in effect (null when empty).</summary>
    public List<string>? Exclude { get; init; }

    /// <summary>One line for the log: "coverage.settings.xml (found next to the test project), dynamic instrumentation".</summary>
    public string Describe(string? root = null)
    {
        var what = Source switch
        {
            "file" => $"{Display(Path!, root)} ({Origin switch { "option" => "Coverage.Settings", "msbuild" => "--coverage-settings in TestingPlatformCommandLineArguments", _ => "found by name" }})",
            "testconfig" => $"the codeCoverage section of {System.IO.Path.GetFileName(Path)}",
            _ => "Labcoat's defaults (" + (Modules is { Count: > 0 } m ? "referenced projects: " + string.Join(", ", m) : "every module except tests, test frameworks and Labcoat") +
                 "; generated code and obj/ left out)",
        };
        var extra = (Include is { Count: > 0 } i ? "; include " + string.Join(", ", i) : "") + (Exclude is { Count: > 0 } e ? "; exclude " + string.Join(", ", e) : "");
        return $"{what}, {Instrumentation} instrumentation{extra}";
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> when it is under it. Both are normalized first, so
    /// '/'-separated paths compare on Windows too.</summary>
    private static string Display(string path, string? root)
    {
        if (root is null) return path;
        var (full, fullRoot) = (System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(root));
        return PathUtil.IsUnder(full, fullRoot) ? System.IO.Path.GetRelativePath(fullRoot, full) : path;
    }
}

/// <summary>What Labcoat knows about a test project when it prepares a coverage run.</summary>
public sealed record CoverageProject
{
    public required string ProjectId { get; init; }
    /// <summary>The .csproj (absolute).</summary>
    public required string ProjectPath { get; init; }
    /// <summary>The test app (TargetPath).</summary>
    public required string TargetPath { get; init; }
    /// <summary>The MSBuild property TestingPlatformCommandLineArguments (what <c>dotnet test</c> passes to the test app).</summary>
    public string? TestingPlatformArguments { get; init; }
    /// <summary>Assembly names of the projects the test project references, transitively.</summary>
    public IReadOnlyList<string> ReferencedAssemblies { get; init; } = [];
    /// <summary>Assembly names of every test project Labcoat runs (never counted by the defaults).</summary>
    public IReadOnlyCollection<string> TestAssemblies { get; init; } = [];
    /// <summary>Where the walk-up for a settings file stops (inclusive) when no solution / repository root comes first.</summary>
    public string? WorkspaceRoot { get; init; }
}

/// <summary>A coverage run's settings: the settings arguments for the test app and what the report keeps.</summary>
public sealed record CoveragePlan(IReadOnlyList<string> SettingsArguments, CoverageSettingsInfo Info, CoverageFilter Filter)
{
    /// <summary>The test app's arguments for a coverage run that writes Cobertura to <paramref name="coverageFile"/>.</summary>
    public string[] Arguments(string coverageFile) =>
        ["--coverage", "--coverage-output-format", "cobertura", "--coverage-output", coverageFile, .. SettingsArguments];

    /// <summary>True when the run instruments assemblies of the output folder in place (static instrumentation).</summary>
    public bool RewritesOutput => Info.Instrumentation == "static";
}

/// <summary>
/// Chooses the coverage settings of a test project and prepares the file passed with <c>--coverage-settings</c>
/// (docs/features.md "Code coverage"). Order:
/// <list type="number">
/// <item><see cref="CoverageOptions.Settings"/> (Coverage.Settings in .labcoat.json, <c>--coverage-settings</c>, the AppHost);</item>
/// <item><c>--coverage-settings</c> in the project's <c>TestingPlatformCommandLineArguments</c>;</item>
/// <item>the <c>codeCoverage</c> section of the test app's <c>testconfig.json</c>;</item>
/// <item>by name: <c>coverage.settings.xml</c>, <c>CodeCoverage.xml</c>, <c>*.coverage.xml</c>, <c>*.runsettings</c> with a
/// CodeCoverage section, next to the test project, in its output folder, then in each parent directory up to the
/// solution or repository root;</item>
/// <item>Labcoat's defaults.</item>
/// </list>
/// A file found is used as it is (the numbers match <c>dotnet test --coverage --coverage-settings</c>); the only change is the
/// instrumentation mode: where the extension has a CLR profiler for this platform, static instrumentation is turned off,
/// so no assembly of the output folder is rewritten in place (a warm test host that has them loaded keeps working, and a
/// file it holds open can't make the instrumentation skip a module). The results are the same.
/// </summary>
public static class CoverageSettingsResolver
{
    /// <summary>Settings file names looked for, in order (<c>*</c>: any file name with that ending).</summary>
    public static readonly string[] ConventionNames = ["coverage.settings.xml", "CodeCoverage.xml", "*.coverage.xml", ".runsettings", "*.runsettings"];

    /// <summary>Modules the defaults never count: Labcoat's own assemblies and test frameworks (prefixes, case-insensitive).</summary>
    public static readonly string[] LabcoatPrefixes = ["Labcoat"];
    public static readonly string[] FrameworkPrefixes =
        ["TUnit", "MSTest", "Microsoft.VisualStudio.TestPlatform", "Microsoft.Testing.", "Microsoft.TestPlatform.", "xunit", "NUnit", "nunit"];

    // CoverageSettingsInfo.instrumentation: the CLR profiler (no file is rewritten), or assemblies instrumented in place.
    private const string DynamicInstrumentation = "dynamic";
    private const string StaticInstrumentation = "static";

    /// <summary>What the extension uses unless told otherwise: dynamic on Windows, static elsewhere.</summary>
    private static string PlatformInstrumentation => OperatingSystem.IsWindows() ? DynamicInstrumentation : StaticInstrumentation;

    /// <summary>Where the version number of an informational version ends ("18.1.0-preview+abc").</summary>
    private static readonly char[] VersionSuffixSeparators = ['+', '-'];

    private static readonly string[] GeneratedSuffixes = [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs", ".g.fs", ".g.vb", ".designer.vb"];

    /// <summary>The settings for a coverage run of <paramref name="project"/>; writes the effective settings file into <paramref name="workDir"/>.</summary>
    public static CoveragePlan Plan(CoverageProject project, CoverageOptions options, string workDir, Action<string, string>? log = null)
    {
        var outputDir = Path.GetDirectoryName(project.TargetPath)!;
        var dynamicAvailable = DynamicInstrumentationAvailable(outputDir);
        List<string> args = [];
        var include = options.Include.Count > 0 ? options.Include.ToList() : null;
        var exclude = options.Exclude.Count > 0 ? options.Exclude.ToList() : null;

        var (path, origin) = Find(project, options, log);
        if (path is not null && origin == "testconfig")
        {
            // The extension reads it itself; passing --coverage-settings too would replace it.
            var info = new CoverageSettingsInfo
            {
                ProjectId = project.ProjectId, Source = "testconfig", Path = path, Instrumentation = PlatformInstrumentation,
                Include = include, Exclude = exclude,
            };
            return new CoveragePlan(args, info, CoverageFilter.ForSettingsFile(options));
        }
        if (path is not null)
        {
            var (effective, mode) = EffectiveSettingsFile(path, project.ProjectId, workDir, dynamicAvailable, log);
            args.AddRange(["--coverage-settings", effective]);
            var info = new CoverageSettingsInfo
            {
                ProjectId = project.ProjectId, Source = "file", Path = path, Origin = origin, Instrumentation = mode, Include = include, Exclude = exclude,
            };
            return new CoveragePlan(args, info, CoverageFilter.ForSettingsFile(options));
        }

        // Labcoat's defaults.
        var modules = DefaultModules(project, options);
        var dynamic = !OperatingSystem.IsWindows() && dynamicAvailable;
        var settings = DefaultSettings(modules, project, options, dynamic);
        args.AddRange(["--coverage-settings", WriteSettings(settings, workDir, project.ProjectId)]);
        var defaults = new CoverageSettingsInfo
        {
            ProjectId = project.ProjectId, Source = "defaults", Instrumentation = dynamic ? DynamicInstrumentation : PlatformInstrumentation,
            Modules = modules.Count > 0 ? modules : null, Include = include, Exclude = exclude,
        };
        return new CoveragePlan(args, defaults, CoverageFilter.ForDefaults(options, project.TestAssemblies, modules));
    }

    /// <summary>
    /// The settings file to pass and the instrumentation it gets: on Linux / macOS a copy that prefers dynamic
    /// instrumentation when that is available and the file allows it (<see cref="PreferDynamic"/>), else the file itself.
    /// </summary>
    private static (string Effective, string Mode) EffectiveSettingsFile(string path, string projectId, string workDir, bool dynamicAvailable,
        Action<string, string>? log)
    {
        string effective = path, mode = PlatformInstrumentation;
        try
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            if (!OperatingSystem.IsWindows() && dynamicAvailable && PreferDynamic(doc))
            {
                effective = WriteSettings(doc, workDir, projectId);
                mode = DynamicInstrumentation;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            log?.Invoke("warning", $"Coverage settings {path}: {ex.Message}; passed on as it is.");
        }
        return (effective, mode);
    }

    // ------------------------------------------------------------------ finding the settings file

    /// <summary>The settings file and how it was found ("option", "msbuild", "testconfig", "convention"); (null, null) = defaults.</summary>
    public static (string? Path, string? Origin) Find(CoverageProject project, CoverageOptions options, Action<string, string>? log = null)
    {
        var projectDir = Path.GetDirectoryName(project.ProjectPath)!;
        var outputDir = Path.GetDirectoryName(project.TargetPath)!;
        if (!string.IsNullOrWhiteSpace(options.Settings))
        {
            var s = options.Settings.Trim();
            var candidates = SettingsCandidates(s, projectDir, options.BaseDirectory);
            if (candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists) is { } explicitFile) return (explicitFile, "option");
            log?.Invoke("warning", $"Coverage.Settings: {s} was not found (looked in {string.Join(", ", candidates.Select(c => Path.GetDirectoryName(Path.GetFullPath(c))).Distinct())}); looking for the project's own settings instead.");
        }
        if (SettingsArgument(project.TestingPlatformArguments) is { } fromMsBuild)
        {
            var full = Path.GetFullPath(Path.Combine(projectDir, fromMsBuild));
            if (File.Exists(full)) return (full, "msbuild");
            log?.Invoke("warning", $"TestingPlatformCommandLineArguments names --coverage-settings {fromMsBuild}, which does not exist.");
        }
        var testConfig = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(project.TargetPath) + ".testconfig.json");
        if (HasCodeCoverageSection(testConfig, json: true)) return (testConfig, "testconfig");

        foreach (var dir in SearchDirectories(projectDir, outputDir, project.WorkspaceRoot))
            if (FindByName(dir) is { } found) return (found, "convention");
        return (null, null);
    }

    /// <summary>Where the Coverage.Settings file may be: as given when rooted, else under the project, then the base directory.</summary>
    private static string[] SettingsCandidates(string settings, string projectDir, string? baseDirectory)
    {
        if (Path.IsPathRooted(settings)) return [settings];
        return baseDirectory is null ? [Path.Combine(projectDir, settings)] : [Path.Combine(projectDir, settings), Path.Combine(baseDirectory, settings)];
    }

    /// <summary>The project directory, the output folder, then each parent of the project directory up to (and including)
    /// the first one with a solution or <c>.git</c>, or the workspace root; never the home directory or the file system root.</summary>
    internal static IEnumerable<string> SearchDirectories(string projectDir, string outputDir, string? workspaceRoot)
    {
        yield return projectDir;
        yield return outputDir;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stopAt = workspaceRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (IsRoot(projectDir)) yield break;
        for (var dir = Directory.GetParent(projectDir); dir is not null; dir = dir.Parent)
        {
            var path = Path.TrimEndingDirectorySeparator(dir.FullName);
            if (dir.Parent is null || (home.Length > 0 && string.Equals(path, Path.TrimEndingDirectorySeparator(home), PathUtil.Comparison))) yield break;
            yield return path;
            if (IsRoot(path) || (stopAt is not null && string.Equals(path, stopAt, PathUtil.Comparison))) yield break;
        }

        static bool IsRoot(string dir)
        {
            try
            {
                return Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))
                       || Directory.EnumerateFiles(dir, "*.sln").Any() || Directory.EnumerateFiles(dir, "*.slnx").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }
    }

    /// <summary>The first conventional settings file in <paramref name="dir"/> that has a CodeCoverage section.</summary>
    internal static string? FindByName(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var name in ConventionNames)
        {
            IEnumerable<string> files;
            try
            {
                files = name.StartsWith('*')
                    ? Directory.EnumerateFiles(dir, name).Order(StringComparer.Ordinal).ToList()
                    : Directory.EnumerateFiles(dir).Where(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            if (files.FirstOrDefault(f => HasCodeCoverageSection(f, json: false)) is { } found) return Path.GetFullPath(found);
        }
        return null;
    }

    /// <summary>XML: any element named CodeCoverage (a coverage configuration or a runsettings DataCollector). JSON: a top-level
    /// <c>codeCoverage</c> property (testconfig.json).</summary>
    internal static bool HasCodeCoverageSection(string path, bool json)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (json)
            {
                var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: LabcoatConfig.FileJsonOptions) as JsonObject;
                return root is not null && root.Any(kv => string.Equals(kv.Key, "codeCoverage", StringComparison.OrdinalIgnoreCase));
            }
            return XDocument.Load(path).Descendants().Any(e => e.Name.LocalName == "CodeCoverage");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or JsonException) { return false; }
    }

    /// <summary>The value of <c>--coverage-settings</c> in a command line (MSBuild's TestingPlatformCommandLineArguments).</summary>
    public static string? SettingsArgument(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var tokens = Tokenize(commandLine);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t is "--coverage-settings" && i + 1 < tokens.Count) return tokens[i + 1];
            if (t.StartsWith("--coverage-settings=", StringComparison.Ordinal) || t.StartsWith("--coverage-settings:", StringComparison.Ordinal))
                return t["--coverage-settings=".Length..];
        }
        return null;
    }

    private static List<string> Tokenize(string s)
    {
        var tokenizer = new Tokenizer();
        foreach (var c in s) tokenizer.Read(c);
        tokenizer.EndToken();
        return tokenizer.Tokens;
    }

    /// <summary>Splits a command line at whitespace; single and double quotes group (and an empty pair is a token).</summary>
    private sealed class Tokenizer
    {
        private readonly StringBuilder _current = new();
        private char? _quote;
        private bool _any;

        public List<string> Tokens { get; } = [];

        public void Read(char c)
        {
            if (_quote is not null) ReadQuoted(c);
            else if (c is '"' or '\'')
            {
                _quote = c;
                _any = true;
            }
            else if (char.IsWhiteSpace(c)) EndToken();
            else _current.Append(c);
        }

        private void ReadQuoted(char c)
        {
            if (c == _quote) _quote = null;
            else _current.Append(c);
        }

        public void EndToken()
        {
            if (_current.Length > 0 || _any) Tokens.Add(_current.ToString());
            _current.Clear();
            _any = false;
        }
    }

    // ------------------------------------------------------------------ instrumentation

    /// <summary>
    /// The oldest Microsoft.Testing.Extensions.CodeCoverage whose dynamic instrumentation Labcoat relies on on Linux / macOS.
    /// Observed: 18.11.2 and 18.12.0 collect the same data dynamically as statically; 18.1.0 (the MSTest 4.0 metapackage's)
    /// collects nothing dynamically when the test app runs in server mode, as it does under Labcoat.
    /// </summary>
    public static readonly Version MinimumDynamicExtensionVersion = new(18, 11);

    /// <summary>
    /// True when the test app's output folder has the coverage extension's CLR profiler for this OS and architecture
    /// (Microsoft.Testing.Extensions.CodeCoverage ships it for linux-x64, linux-musl-x64, osx-x64 and Windows) and the
    /// extension is at least <see cref="MinimumDynamicExtensionVersion"/>. Otherwise (e.g. Linux / macOS on arm64, or an
    /// older extension) only static instrumentation is used.
    /// </summary>
    public static bool DynamicInstrumentationAvailable(string outputDir)
    {
        if (OperatingSystem.IsWindows()) return true;
        if (ExtensionVersion(outputDir) is not { } version || version < MinimumDynamicExtensionVersion) return false;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64", Architecture.Arm64 => "arm64", Architecture.X86 => "x86", Architecture.Arm => "arm", var a => a.ToString().ToLowerInvariant(),
        };
        var (rids, file) = OperatingSystem.IsMacOS()
            ? (new[] { $"osx-{arch}" }, "libInstrumentationEngine.dylib")
            : (new[] { $"linux-{arch}", $"linux-musl-{arch}" }, "libInstrumentationEngine.so");
        if (File.Exists(Path.Combine(outputDir, file))) return true; // a RID-specific build: natives next to the app
        return rids.Any(rid => File.Exists(Path.Combine(outputDir, "runtimes", rid, "native", file)));
    }

    /// <summary>
    /// Version of Microsoft.Testing.Extensions.CodeCoverage in the output folder: from the test app's .deps.json (the
    /// package version), else the product version of the DLL; null when absent.
    /// </summary>
    internal static Version? ExtensionVersion(string outputDir)
    {
        const string Package = "Microsoft.Testing.Extensions.CodeCoverage";
        Version? best = null;
        try
        {
            foreach (var deps in Directory.EnumerateFiles(outputDir, "*.deps.json"))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(deps))?["libraries"] is not JsonObject libraries) continue;
                    foreach (var (key, _) in libraries)
                        if (key.StartsWith(Package + "/", StringComparison.OrdinalIgnoreCase) && ParseVersion(key[(Package.Length + 1)..]) is { } v && (best is null || v > best))
                            best = v;
                }
                catch (JsonException) { /* a malformed deps.json: try the next one */ }
            }
            if (best is not null) return best;
            var dll = Path.Combine(outputDir, Package + ".dll");
            return File.Exists(dll) ? ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return best; }

        static Version? ParseVersion(string? text) => Version.TryParse((text ?? "").Split(VersionSuffixSeparators)[0], out var v) ? v : null;
    }

    /// <summary>
    /// Turns static instrumentation off in <paramref name="doc"/> (adds EnableStaticManagedInstrumentation = False and
    /// EnableDynamicManagedInstrumentation = True to its CodeCoverage element) unless the file chose an instrumentation
    /// mode itself. False when the file keeps static instrumentation.
    /// </summary>
    internal static bool PreferDynamic(XDocument doc)
    {
        var cc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CodeCoverage");
        if (cc is null) return false;
        var ns = cc.Name.Namespace;
        var st = cc.Elements().FirstOrDefault(e => e.Name.LocalName == "EnableStaticManagedInstrumentation");
        var dy = cc.Elements().FirstOrDefault(e => e.Name.LocalName == "EnableDynamicManagedInstrumentation");
        if (st is not null) return false; // the file chose
        if (dy is not null && bool.TryParse(dy.Value.Trim(), out var dv) && !dv) return false;
        cc.AddFirst(new XElement(ns + "EnableStaticManagedInstrumentation", "False"));
        if (dy is null) cc.AddFirst(new XElement(ns + "EnableDynamicManagedInstrumentation", "True"));
        return true;
    }

    // ------------------------------------------------------------------ defaults

    /// <summary>The assemblies the defaults count: referenced projects minus Labcoat's, test projects and test frameworks
    /// (unless Coverage.Include names them).</summary>
    internal static List<string> DefaultModules(CoverageProject project, CoverageOptions options)
    {
        var includes = CoverageFilter.Globs(options.Include);
        return project.ReferencedAssemblies
            .Where(m => includes.Any(g => g.MatchesModule(m)) || !(IsLabcoat(m) || IsFramework(m) || project.TestAssemblies.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsLabcoat(string module) => LabcoatPrefixes.Any(p => module.Equals(p, StringComparison.OrdinalIgnoreCase) || module.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

    public static bool IsFramework(string module) => FrameworkPrefixes.Any(p => p.EndsWith('.')
        ? module.StartsWith(p, StringComparison.OrdinalIgnoreCase)
        : module.Equals(p, StringComparison.OrdinalIgnoreCase) || module.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>Generated source: a file under an <c>obj</c> folder, or *.g.cs, *.g.i.cs, *.designer.cs, *.generated.cs.</summary>
    public static bool IsGenerated(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)) return true;
        return GeneratedSuffixes.Any(s => p.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The settings file of the defaults. [CompilerGenerated] is deliberately not excluded: it also marks async state
    /// machines, lambdas and iterators, i.e. real code (their lines would vanish from the report).
    /// </summary>
    internal static XDocument DefaultSettings(List<string> modules, CoverageProject project, CoverageOptions options, bool dynamic)
    {
        var includes = CoverageFilter.Globs(options.Include);
        var moduleIncludes = modules.Select(m => ModuleRegex(Regex.Escape(m)))
            .Concat(includes.Where(g => !g.IsPath).Select(g => ModuleRegex(g.ModuleRegexBody)))
            .Distinct().ToList();
        var excludes = new List<string>();
        // Exclusions the include list does not undo (an include such as "Labcoat.*" brings Labcoat's assemblies back).
        if (!includes.Any(g => !g.IsPath && LabcoatPrefixes.Any(p => g.Pattern.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            excludes.AddRange(LabcoatPrefixes.Select(p => ModuleRegex(Regex.Escape(p) + @"(\..*)?")));
        if (!includes.Any(g => !g.IsPath && FrameworkPrefixes.Any(p => g.Pattern.StartsWith(p.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))))
            excludes.AddRange(FrameworkPrefixes.Select(p => ModuleRegex(p.EndsWith('.') ? Regex.Escape(p) + ".*" : Regex.Escape(p) + @"(\..*)?")));
        excludes.AddRange(project.TestAssemblies.Where(t => !includes.Any(g => g.MatchesModule(t))).Select(t => ModuleRegex(Regex.Escape(t))));

        var cc = new XElement("CodeCoverage");
        if (dynamic)
        {
            cc.Add(new XElement("EnableStaticManagedInstrumentation", "False"));
            cc.Add(new XElement("EnableDynamicManagedInstrumentation", "True"));
        }
        var modulePaths = new XElement("ModulePaths");
        if (moduleIncludes.Count > 0) modulePaths.Add(new XElement("Include", moduleIncludes.Select(r => new XElement("ModulePath", r))));
        modulePaths.Add(new XElement("Exclude", excludes.Distinct().Select(r => new XElement("ModulePath", r))));
        cc.Add(modulePaths);
        cc.Add(new XElement("Sources", new XElement("Exclude",
            new XElement("Source", @".*[\\/]obj[\\/].*"),
            GeneratedSuffixes.Select(s => new XElement("Source", ".*" + Regex.Escape(s) + "$")))));
        cc.Add(new XElement("Attributes", new XElement("Exclude",
            new XElement("Attribute", @"^System\.CodeDom\.Compiler\.GeneratedCodeAttribute$"),
            new XElement("Attribute", @"^System\.Diagnostics\.CodeAnalysis\.ExcludeFromCodeCoverageAttribute$"))));
        cc.Add(new XElement("IncludeTestAssembly", "False"));
        return new XDocument(
            new XComment(" Written by Labcoat: its default coverage settings (docs/features.md \"Code coverage\"). Put a coverage.settings.xml next to the test project to choose your own. "),
            new XElement("Configuration", cc));
    }

    private static string ModuleRegex(string nameRegex) => @".*[\\/]" + nameRegex + @"\.(dll|exe)$";

    private static string WriteSettings(XDocument doc, string workDir, string projectId)
    {
        Directory.CreateDirectory(workDir);
        var text = doc.Declaration is null ? doc.ToString() : doc.Declaration + Environment.NewLine + doc;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
        var path = Path.Combine(workDir, $"{SafeName(projectId)}-{hash}.coverage.xml");
        if (!File.Exists(path)) File.WriteAllText(path, text);
        return path;
    }

    private static string SafeName(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
}

/// <summary>
/// What a coverage report keeps (applied to every report Labcoat shows, merged or cached). With a settings file: everything
/// the file collected, minus Coverage.Exclude. With Labcoat's defaults: also without Labcoat's own assemblies, test
/// projects, test frameworks, generated code and obj/, and only the referenced projects' assemblies; Coverage.Include
/// brings any of these back.
/// </summary>
public sealed class CoverageFilter
{
    private readonly List<Glob> _include;
    private readonly List<Glob> _exclude;
    private readonly bool _defaults;
    private readonly HashSet<string> _testAssemblies;
    private readonly HashSet<string>? _modules;

    private CoverageFilter(CoverageOptions options, bool defaults, IEnumerable<string>? testAssemblies, IEnumerable<string>? modules)
    {
        _include = Globs(options.Include);
        _exclude = Globs(options.Exclude);
        _defaults = defaults;
        _testAssemblies = new HashSet<string>(testAssemblies ?? [], StringComparer.OrdinalIgnoreCase);
        _modules = modules is null ? null : new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
        if (_modules is { Count: 0 }) _modules = null;
    }

    public static CoverageFilter ForSettingsFile(CoverageOptions options) => new(options, defaults: false, null, null);

    public static CoverageFilter ForDefaults(CoverageOptions options, IEnumerable<string> testAssemblies, IEnumerable<string>? modules = null) =>
        new(options, defaults: true, testAssemblies, modules);

    /// <summary>Keep this file of <paramref name="module"/> in the report?</summary>
    public bool Keeps(string module, string path)
    {
        if (_exclude.Any(g => g.Matches(module, path))) return false;
        if (!_defaults) return true;
        if (_include.Any(g => g.Matches(module, path))) return true;
        if (CoverageSettingsResolver.IsLabcoat(module) || CoverageSettingsResolver.IsFramework(module) || _testAssemblies.Contains(module)) return false;
        if (_modules is not null && !_modules.Contains(module)) return false;
        return !CoverageSettingsResolver.IsGenerated(path);
    }

    internal static List<Glob> Globs(IEnumerable<string> patterns) =>
        patterns.Select(p => p.Trim()).Where(p => p.Length > 0).Select(p => new Glob(p)).ToList();

    /// <summary>
    /// A module name or path glob: <c>*</c> any characters but a separator, <c>**</c> anything, <c>?</c> one character.
    /// Without a <c>/</c> it matches a module (assembly) name or a file name; with one, a file path (relative patterns
    /// match at any depth).
    /// </summary>
    internal sealed class Glob
    {
        public string Pattern { get; }
        public bool IsPath { get; }
        private readonly Regex _regex;

        public Glob(string pattern)
        {
            Pattern = pattern.Replace('\\', '/');
            IsPath = Pattern.Contains('/');
            var body = ToRegex(Pattern);
            if (IsPath && !Pattern.StartsWith('/') && !Pattern.StartsWith("**", StringComparison.Ordinal) && !(Pattern.Length > 1 && Pattern[1] == ':'))
                body = "(.*/)?" + body;
            // Only literals, classes, groups and quantifiers (ToRegex): NonBacktracking matches in linear time.
            _regex = new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout.Value);
        }

        /// <summary>The pattern as a regex over a module name (for ModulePath entries of the collector settings).</summary>
        public string ModuleRegexBody => ToRegex(Pattern).Replace("[^/]", @"[^\\/]");

        public bool MatchesModule(string module) => !IsPath && _regex.IsMatchOrFalse(module);

        public bool Matches(string module, string path)
        {
            var p = path.Replace('\\', '/');
            return IsPath ? _regex.IsMatchOrFalse(p) : _regex.IsMatchOrFalse(module) || _regex.IsMatchOrFalse(Path.GetFileName(p));
        }

        private static string ToRegex(string glob)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < glob.Length)
            {
                var (regex, length) = Token(glob, i);
                sb.Append(regex);
                i += length;
            }
            return sb.ToString();
        }

        /// <summary>The regex of the glob token at <paramref name="i"/> and the number of characters it takes.</summary>
        private static (string Regex, int Length) Token(string glob, int i)
        {
            var c = glob[i];
            if (c == '?') return ("[^/]", 1);
            if (c != '*') return (Regex.Escape(c.ToString()), 1);
            if (!IsAt(glob, i + 1, '*')) return ("[^/]*", 1);
            return IsAt(glob, i + 2, '/') ? ("(.*/)?", 3) : (".*", 2);
        }

        private static bool IsAt(string s, int i, char c) => i < s.Length && s[i] == c;
    }
}
