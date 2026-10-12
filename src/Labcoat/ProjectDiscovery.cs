using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Labcoat;

/// <summary>
/// Finds test projects from the CLI paths.
///  - An explicitly passed .csproj/.fsproj/.vbproj is always used (a missing path, an unsupported file or an unreadable
///    solution is an error: see <see cref="FindProjects"/>).
///  - Projects found via a directory scan or a .sln/.slnx/.slnf are test projects (on Microsoft.Testing.Platform or on
///    VSTest: <see cref="TestPlatform.Classify(ProjectFacts)"/>; Labcoat lists VSTest projects with how to switch them, and
///    never starts them) when
///      1. the project file text says so (fast path, <see cref="IsTestProject"/>): MSTest.Sdk / TUnit.Sdk, a PackageReference
///         to MSTest / Microsoft.Testing.Platform(.*) / xunit.v3(.*) (incl. xunit.v3.mtp-v2) / TUnit(.*), a VSTest test
///         project's Microsoft.NET.Test.Sdk / MSTest.TestAdapter / NUnit3TestAdapter / xunit.runner.visualstudio, or
///         EnableMSTestRunner / EnableNUnitRunner / UseMicrosoftTestingPlatformRunner / IsTestProject /
///         IsTestingPlatformApplication / TestingPlatformDotnetTestSupport set to true; else
///      2. MSBuild evaluation says so (<see cref="IsMtpTestApp"/>): one <c>dotnet msbuild -getProperty</c> per remaining
///         candidate (its name contains "test"/"spec", or a Directory.Build.props/.targets or Directory.Packages.props above
///         it mentions a test framework), run in parallel and cached by file timestamps. This sees settings that come from
///         Directory.Build.props, central package management and package props (IsTestingPlatformApplication is set by the
///         Microsoft.Testing.Platform.MSBuild package once the project is restored).
/// Directory scans are recursive and skip bin, obj, node_modules and dot-folders.
/// With runner packages (<see cref="TestRunners"/>, docs/runners.md) a scan also finds their projects: a folder with the
/// runner's config file or a package.json that depends on it (the scan doesn't go below such a folder). A solution
/// doesn't list them, so for a solution its folder is scanned for them. An explicitly given package.json / config file
/// of a runner is used as is. Without the .NET runner (--runners without dotnet) no .NET projects are looked for.
/// </summary>
public static partial class ProjectDiscovery
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

    /// <summary>
    /// Test projects for <paramref name="paths"/>. Problems with explicitly given paths go to <paramref name="errors"/>.
    /// <paramref name="runners"/>: the test runners in use (null = only the .NET runner).
    /// </summary>
    public static List<string> FindProjects(IEnumerable<string> paths, Action<string> warn, List<string>? errors = null, Action<string>? info = null,
        TestRunners? runners = null)
    {
        runners ??= TestRunners.DotnetOnly;
        var result = new List<string>();
        void Error(string message)
        {
            if (errors is not null) errors.Add(message);
            else warn(message);
        }

        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            List<string> found;
            if (Directory.Exists(path))
                found = [.. runners.IncludesDotnet ? Classify(ScanDirectory(path).ToList(), info) : [], .. ScanRunnerProjects(path, runners.Plugins)];
            else if (File.Exists(path)) found = ProjectsOfFile(path, runners, warn, Error, info);
            else
            {
                Error($"Path not found: {path}");
                continue;
            }
            result.AddRange(found.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Where(proj => !result.Contains(proj, StringComparer.Ordinal)).ToList());
        }
        return result;
    }

    /// <summary>
    /// The name (and project id) of a test project: the file name without extension, or for a runner package's project
    /// (package.json, vitest.config.ts) the name of its folder.
    /// </summary>
    public static string ProjectNameOf(string path) =>
        IsDotnetProjectFile(path) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? Path.GetFileNameWithoutExtension(path);

    /// <summary>A .csproj / .fsproj / .vbproj (a project of the .NET runner).</summary>
    public static bool IsDotnetProjectFile(string path) => ProjectExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The test projects of an explicitly given file: the project itself, or the test projects of a solution.</summary>
    private static List<string> ProjectsOfFile(string path, TestRunners runners, Action<string> warn, Action<string> error, Action<string>? info)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ProjectExtensions.Contains(ext))
        {
            if (runners.IncludesDotnet) return [path];
            warn($"Skipping {Path.GetFileName(path)}: the dotnet test runner isn't in use (runners: {runners.Describe()}).");
            return [];
        }
        if (runners.PluginFor(path) is not null) return [path];
        if (ext is not (".sln" or ".slnx" or ".slnf"))
        {
            error($"Unsupported path (expected a project, solution or directory): {path}");
            return [];
        }
        // A solution lists .NET projects only: the projects of runner packages are looked for in its folder.
        var runnerProjects = ScanRunnerProjects(Path.GetDirectoryName(path)!, runners.Plugins);
        if (!runners.IncludesDotnet) return runnerProjects;
        return [.. SolutionProjects(path, warn, error, info), .. runnerProjects];
    }

    /// <summary>
    /// The projects of runner packages under <paramref name="root"/>: folders with a runner's config file or a package.json
    /// that depends on it (skipping bin, obj, node_modules and dot-folders; not below a project found).
    /// </summary>
    internal static List<string> ScanRunnerProjects(string root, IReadOnlyList<RunnerPlugin> plugins)
    {
        var found = new List<string>();
        if (plugins.Count == 0) return found;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var project = plugins.Select(p => p.IsProjectDirectory(dir, out var file) ? file : null).FirstOrDefault(f => f is not null);
            if (project is not null)
            {
                found.Add(project);
                continue;
            }
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir).Where(d => !IsSkippedDir(Path.GetFileName(d))).OrderByDescending(d => d, StringComparer.Ordinal))
                    stack.Push(d);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* unreadable folder: skip it */ }
        }
        return found;
    }

    /// <summary>The test projects listed in a solution.</summary>
    private static List<string> SolutionProjects(string path, Action<string> warn, Action<string> error, Action<string>? info)
    {
        List<string> listed;
        try { listed = ReadSolution(path).ToList(); }
        catch (Exception ex) when (ex is System.Xml.XmlException or JsonException or IOException or UnauthorizedAccessException
                                       or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            error($"Could not read the solution {path}: {FirstLine(ex.Message)}");
            return [];
        }
        var existing = new List<string>();
        foreach (var proj in listed)
        {
            if (File.Exists(proj)) existing.Add(proj);
            else warn($"Project in solution not found: {proj}");
        }
        return Classify(existing, info);
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    /// <summary>Text heuristic first; MSBuild evaluation (parallel, cached) for the candidates it could not decide.</summary>
    private static List<string> Classify(List<string> projects, Action<string>? info)
    {
        var verdict = new bool[projects.Count];
        var toEvaluate = new List<int>();
        for (var i = 0; i < projects.Count; i++)
        {
            if (IsTestProject(projects[i])) verdict[i] = true;
            else if (IsEvaluationCandidate(projects[i])) toEvaluate.Add(i);
        }
        if (toEvaluate.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            var cache = DetectionCache.Load();
            var evaluated = 0;
            Parallel.ForEach(toEvaluate, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) }, i =>
            {
                var path = projects[i];
                var stamp = DetectionCache.Stamp(path);
                if (cache.TryGet(path, stamp, out var cached)) { verdict[i] = cached; return; }
                var result = EvaluateAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                Interlocked.Increment(ref evaluated);
                if (result is null) return; // evaluation failed: not a test project (as before)
                verdict[i] = result.IsTestProject;
                cache.Set(path, stamp, result.IsTestProject);
            });
            if (evaluated > 0)
            {
                cache.Save();
                info?.Invoke($"Evaluated {evaluated} project(s) with MSBuild to find test projects ({sw.Elapsed.TotalSeconds:0.0}s)");
            }
        }
        return projects.Where((_, i) => verdict[i]).ToList();
    }

    private static IEnumerable<string> ScanDirectory(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
                dirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var f in files.Where(f => ProjectExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.Ordinal))
                yield return f;
            foreach (var d in dirs.Where(d => !IsSkippedDir(Path.GetFileName(d))).OrderByDescending(d => d))
                stack.Push(d);
        }
    }

    private static bool IsSkippedDir(string name) => SkipDirs.Contains(name) || name.StartsWith('.');

    /// <summary>Project paths listed in a .sln / .slnx / .slnf. Throws on malformed files (XML / JSON errors).</summary>
    public static IEnumerable<string> ReadSolution(string sln)
    {
        var dir = Path.GetDirectoryName(sln)!;
        return Path.GetExtension(sln).ToLowerInvariant() switch
        {
            ".slnx" => ReadSlnx(sln, dir),
            ".slnf" => ReadSlnf(sln, dir),
            _ => ReadSln(sln, dir),
        };
    }

    private static string FullPath(string dir, string relative) =>
        Path.GetFullPath(Path.Combine(dir, relative.Replace('\\', Path.DirectorySeparatorChar)));

    private static List<string> ReadSlnx(string sln, string dir)
    {
        var doc = XDocument.Load(sln);
        if (doc.Root?.Name.LocalName != "Solution") throw new InvalidOperationException("The root element is not <Solution>.");
        return doc.Descendants().Where(e => e.Name.LocalName == "Project")
            .Select(p => (string?)p.Attribute("Path")).OfType<string>()
            .Select(rel => FullPath(dir, rel)).ToList();
    }

    /// <summary>Solution filter: JSON { solution: { path, projects: [] } }.</summary>
    private static List<string> ReadSlnf(string sln, string dir)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(sln));
        var solution = json.RootElement.GetProperty("solution");
        var slnPath = solution.GetProperty("path").GetString()!;
        var slnDir = Path.GetDirectoryName(FullPath(dir, slnPath))!;
        return solution.GetProperty("projects").EnumerateArray().Select(p => FullPath(slnDir, p.GetString()!)).ToList();
    }

    [GeneratedRegex(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""([^""]+\.(?:cs|fs|vb)proj)""", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex SlnProjectRe();

    private static List<string> ReadSln(string sln, string dir)
    {
        var text = File.ReadAllText(sln);
        if (!text.Contains("Microsoft Visual Studio Solution File", StringComparison.Ordinal) && !text.Contains("Project(", StringComparison.Ordinal))
            throw new InvalidOperationException("Not a Visual Studio solution file (no 'Microsoft Visual Studio Solution File' header).");
        return SlnProjectRe().MatchesOrEmpty(text).Select(m => FullPath(dir, m.Groups[1].Value)).ToList();
    }

    // ------------------------------------------------------------------ text heuristic

    /// <summary>Fast check of the project file's text (see the class summary): a test project on MTP or on VSTest.</summary>
    public static bool IsTestProject(string projectPath)
    {
        string text;
        try { text = File.ReadAllText(projectPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; /* unreadable: not a test project */ }
        return IsTestProjectText(text);
    }

    public static bool IsTestProjectText(string text) => TestPlatform.Classify(TestPlatform.ReadText(text)).IsTestProject;

    [GeneratedRegex(@"test|spec", RegexOptions.IgnoreCase, RegexTimeout.Milliseconds)]
    private static partial Regex CandidateNameRe();

    [GeneratedRegex(
        @"MSTest|xunit|TUnit|NUnit|Microsoft\.Testing\.Platform|TestingPlatform|IsTestProject|EnableMSTestRunner|Microsoft\.NET\.Test\.Sdk", RegexOptions.IgnoreCase, RegexTimeout.Milliseconds)]
    private static partial Regex FrameworkMentionRe();

    /// <summary>Worth an MSBuild evaluation: test-like name, or a Directory.* file above it that mentions a test framework.</summary>
    public static bool IsEvaluationCandidate(string projectPath)
    {
        if (CandidateNameRe().IsMatchOrFalse(Path.GetFileNameWithoutExtension(projectPath))) return true;
        foreach (var f in DirectoryFiles(projectPath))
        {
            try { if (FrameworkMentionRe().IsMatchOrFalse(File.ReadAllText(f))) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable file: try the next */ }
        }
        return false;
    }

    private static readonly string[] DirectoryFileNames = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    /// <summary>Directory.Build.props / .targets / Directory.Packages.props in the project's directory and above (up to the root).</summary>
    internal static IEnumerable<string> DirectoryFiles(string projectPath)
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectPath))!); d is not null; d = d.Parent)
            foreach (var name in DirectoryFileNames)
            {
                var f = Path.Combine(d.FullName, name);
                if (File.Exists(f)) yield return f;
            }
    }

    // ------------------------------------------------------------------ MSBuild evaluation

    /// <summary>Properties read by <see cref="EvaluateAsync"/>.</summary>
    public static readonly string[] DetectionProperties = TestPlatform.Properties;

    /// <summary>The <c>dotnet msbuild</c> evaluation of <see cref="EvaluateAsync"/>: English output (Labcoat parses it).</summary>
    internal static ProcessStartInfo EvaluationStartInfo(string projectPath)
    {
        var psi = new ProcessStartInfo(Explorer.DotnetPath)
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -nodeReuse:false and no MSBuild server: nothing outlives the evaluation (a lingering node keeps the project's
        // folder as its working directory, which Windows then can't delete or rename)
        foreach (var a in new[] { "msbuild", projectPath, "-nologo", "-nodeReuse:false" }.Concat(DetectionProperties.Select(p => "-getProperty:" + p)).Append("-getItem:PackageReference"))
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDTERMINALLOGGER"] = "off";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        return psi;
    }

    /// <summary>One <c>dotnet msbuild -getProperty … -getItem:PackageReference</c>; null when the evaluation fails.</summary>
    public static async Task<TestPlatformInfo?> EvaluateAsync(string projectPath, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;
        var psi = EvaluationStartInfo(projectPath);
        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort: the process may have exited already */ }
                // Kill only starts the termination: wait for it, or the process may still hold its working directory
                // (the project's folder, which can't be deleted or renamed on Windows until it has exited).
                try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); } catch (TimeoutException) { /* gave it 10 s: go on */ }
                return null;
            }
            var output = await stdout;
            await stderr;
            if (proc.ExitCode != 0) return null;
            return ParseEvaluation(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    /// <summary>Decision on the JSON printed by <c>dotnet msbuild -getProperty … -getItem:PackageReference</c>.</summary>
    public static TestPlatformInfo? ParseEvaluation(string json) => EvaluationFacts(json) is { } facts ? TestPlatform.Classify(facts) : null;

    /// <summary>The properties and PackageReference ids of an evaluation's JSON; null when it isn't JSON.</summary>
    public static ProjectFacts? EvaluationFacts(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node?["Properties"] is JsonObject po)
            foreach (var (k, v) in po)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) props[k] = s;
        var packages = node?["Items"]?["PackageReference"] is JsonArray arr
            ? arr.Select(x => x?["Identity"] is JsonValue id && id.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : [];
        return new ProjectFacts(props, packages, []);
    }

    /// <summary>
    /// Evaluation results by project path, valid while the project file, the Directory.* files above it and
    /// obj/project.assets.json keep their timestamps. Stored as <c>project-detection-v2.json</c> in Labcoat's data directory
    /// ($LABCOAT_HISTORY_DIR or LocalApplicationData/labcoat).
    /// </summary>
    /// <summary>The file name of the MSBuild detection cache in Labcoat's data directory.</summary>
    internal const string DetectionCacheFileName = "project-detection-v2.json";

    /// <summary>
    /// What a detection depends on: the write times of the project file, its obj/project.assets.json and the Directory.*
    /// files above it ("-" for a missing file).
    /// </summary>
    internal static string DetectionStamp(string projectPath)
    {
        var files = new List<string> { projectPath, Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json") };
        files.AddRange(DirectoryFiles(projectPath));
        return string.Join("|", files.Select(f => File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks.ToString() : "-"));
    }

    private sealed class DetectionCache
    {
        private readonly Dictionary<string, (string Stamp, bool IsTest)> _entries;
        private readonly string _file;
        private bool _dirty;

        private DetectionCache(string file, Dictionary<string, (string, bool)> entries)
        {
            _file = file;
            _entries = entries;
        }

        public static DetectionCache Load()
        {
            var baseDir = Environment.GetEnvironmentVariable("LABCOAT_HISTORY_DIR") is { Length: > 0 } d
                ? d
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "labcoat");
            // v2: VSTest test projects count as test projects too (listed with how to switch them)
            var file = Path.Combine(baseDir, DetectionCacheFileName);
            var entries = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject root)
                    foreach (var (path, v) in root)
                        if (v?["stamp"]?.GetValue<string>() is { } stamp && v["isTest"]?.GetValue<bool>() is { } isTest)
                            entries[path] = (stamp, isTest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { /* no usable cache: everything is evaluated again */ }
            return new DetectionCache(file, entries);
        }

        public static string Stamp(string projectPath) => DetectionStamp(projectPath);

        public bool TryGet(string path, string stamp, out bool isTest)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(path, out var e) && e.Stamp == stamp) { isTest = e.IsTest; return true; }
            }
            isTest = false;
            return false;
        }

        public void Set(string path, string stamp, bool isTest)
        {
            lock (_entries)
            {
                _entries[path] = (stamp, isTest);
                _dirty = true;
            }
        }

        public void Save()
        {
            if (!_dirty) return;
            try
            {
                var root = new JsonObject();
                lock (_entries)
                    foreach (var (path, (stamp, isTest)) in _entries.OrderBy(e => e.Key, StringComparer.Ordinal).TakeLast(2000))
                        root[path] = new JsonObject { ["stamp"] = stamp, ["isTest"] = isTest };
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var tmp = $"{_file}.{Environment.ProcessId}.tmp";
                File.WriteAllText(tmp, root.ToJsonString());
                File.Move(tmp, _file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only a cache: the next start evaluates again */ }
        }
    }
}
