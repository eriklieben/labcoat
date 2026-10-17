namespace Labcoat;

// Round 3: discovery diagnostics, "which tests cover this line", Aspire failure context, open in editor.
public sealed partial class Explorer
{
    /// <summary>
    /// MSBuild properties read when the project is evaluated, for <see cref="DiagnoseProject"/> and
    /// <see cref="AcceptsPlatformAfterBuild"/> (the test platform settings and the restore output).
    /// </summary>
    private static readonly string[] DiagnosticProperties =
        ["OutputType", .. TestPlatform.Properties, ProjectAssetsFile];

    // ------------------------------------------------------------------ discovery diagnostics (TestProject.diagnostics)

    /// <summary>
    /// Why a project discovered no tests (<paramref name="failure"/> null) or its test host could not start/answer:
    /// host exit code + stderr tail, OutputType not Exe, no Microsoft.Testing.Platform runner enabled, no tests found.
    /// </summary>
    private static List<ProjectDiagnostic> DiagnoseProject(ProjectEntry p, Exception? failure)
    {
        var list = new List<ProjectDiagnostic>();
        switch (failure)
        {
            case TestHostExitedException th:
                list.Add(new ProjectDiagnostic(LogLevels.Error,
                    $"The test host exited{(th.ExitCode is { } code ? $" with code {code}" : "")} before it answered.",
                    p.Plugin is { } plugin
                        ? $"Check that the project's dependencies are installed (npm install) and that {plugin.DisplayName} runs in {Path.GetDirectoryName(p.Project.Path)} on its own."
                        : "Run the test app directly (dotnet <test dll> --list-tests) to see why it does not start.",
                    th.StderrTail.Count > 0 ? string.Join('\n', th.StderrTail) : null));
                break;
            case TimeoutException:
                list.Add(new ProjectDiagnostic(LogLevels.Error, "The test host did not connect within 60 s.",
                    "Labcoat starts the test app with --server; only Microsoft.Testing.Platform test apps support that."));
                break;
            case not null:
                list.Add(new ProjectDiagnostic(LogLevels.Error, "Discovery failed: " + FirstLine(failure.Message)));
                break;
        }

        if (p.Properties.Count > 0)
        {
            var outputType = p.Properties.GetValueOrDefault("OutputType");
            if (!string.IsNullOrEmpty(outputType) && !outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                && !outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new ProjectDiagnostic(LogLevels.Error,
                    $"OutputType is '{outputType}', but Microsoft.Testing.Platform test projects are executables.",
                    "Add <OutputType>Exe</OutputType> to the project (MSTest.Sdk and xunit.v3 set it for you)."));
            }
            if (!HasMtpRunner(p))
            {
                list.Add(new ProjectDiagnostic(LogLevels.Warning,
                    "No Microsoft.Testing.Platform runner is enabled for this project.",
                    "Use MSTest.Sdk, or set <EnableMSTestRunner>true</EnableMSTestRunner> (MSTest), " +
                    "<EnableNUnitRunner>true</EnableNUnitRunner> (NUnit), or reference xunit.v3 / TUnit."));
            }
        }

        if (failure is null)
        {
            list.Add(new ProjectDiagnostic(LogLevels.Warning, "No tests discovered.",
                "Check that the test classes are public and marked as tests (MSTest: [TestClass] + [TestMethod]) and that " +
                $"the build output is current{(p.TargetPath is null ? "" : $" ({Path.GetFileName(p.TargetPath)})")}."));
        }
        return list;

        static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();
    }

    private static bool HasMtpRunner(ProjectEntry p)
    {
        bool True(string name) => string.Equals(p.Properties.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);
        if (True("IsTestingPlatformApplication") || True("EnableMSTestRunner") || True("EnableNUnitRunner")
            || True("UseMicrosoftTestingPlatformRunner") || True("UsingMSTestSdk"))
            return true;
        if (p.PackageReferences.Any(r => r.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase)
                                         || r.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase)
                                         || r.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)))
            return true;
        try { return File.ReadAllText(p.Project.Path).Contains("MSTest.Sdk", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ which tests cover a line (GET /api/coverage/line)

    /// <summary>
    /// Tests covering <paramref name="line"/> of <paramref name="path"/>: exact (a cached single-test coverage report
    /// has hits on the line, source "perTest") or approximate (the impact map says the test executed a method whose
    /// span contains the line, source "impactMap"; class-granular for the impact-map pass). <c>Partial</c> = not every
    /// test has single-test coverage cached, so the list may be incomplete.
    /// </summary>
    public CoveringTestsResult CoveringTests(string path, int line)
    {
        var file = PathUtil.CanonicalizeOrSelf(path);
        var cmp = PathUtil.Comparison;
        var canonical = new Dictionary<string, string>();
        string Canon(string s)
        {
            if (canonical.TryGetValue(s, out var c)) return c;
            c = PathUtil.CanonicalizeOrSelf(s);
            canonical[s] = c;
            return c;
        }

        lock (Sync)
        {
            var fingerprints = new Dictionary<string, string?>();
            string? Fingerprint(string projectId)
            {
                if (fingerprints.TryGetValue(projectId, out var f)) return f;
                var project = _projects.FirstOrDefault(p => p.Project.Id == projectId);
                f = project?.TargetPath is { } target && File.Exists(target) ? CodeStampOf(project) : null;
                fingerprints[projectId] = f;
                return f;
            }

            var order = new Dictionary<(string, string), int>();
            for (var i = 0; i < _order.Count; i++) order[_order[i]] = i;

            var exact = new List<CoveringTest>();
            var seen = new HashSet<(string, string)>();
            var withPerTest = 0;
            foreach (var (projectId, testId, report, stamp) in _coverageCache.SingleTestReports())
            {
                if (!_tests.ContainsKey((projectId, testId))) continue;
                withPerTest++;
                var fc = report.Files.FirstOrDefault(f => string.Equals(Canon(f.Path), file, cmp));
                var hit = fc?.Lines.FirstOrDefault(l => l.Line == line);
                if (hit is not { Hits: > 0 }) continue;
                seen.Add((projectId, testId));
                exact.Add(new CoveringTest(projectId, testId, "perTest", hit.Hits, Fingerprint(projectId) != stamp ? true : null));
            }
            var approx = _impact.TestsCoveringLine(file, line)
                .Where(k => _tests.ContainsKey(k) && !seen.Contains(k))
                .Select(k => new CoveringTest(k.ProjectId, k.TestId, "impactMap"))
                .ToList();
            var all = exact.Concat(approx)
                .OrderBy(t => t.Source == "perTest" ? 0 : 1)
                .ThenBy(t => order.GetValueOrDefault((t.ProjectId, t.Id), int.MaxValue))
                .ToList();
            return new CoveringTestsResult(all, withPerTest < _tests.Count);
        }
    }

    // ------------------------------------------------------------------ Aspire failure context (GET /api/tests/telemetry)

    /// <summary>Trace id of a test (null when the test is unknown or has none).</summary>
    public (bool Found, string? TraceId) TestTraceId(string? projectId, string id)
    {
        lock (Sync)
        {
            var t = projectId is not null
                ? _tests.GetValueOrDefault((projectId, id))
                : _tests.Values.FirstOrDefault(x => x.Id == id);
            return (t is not null, t?.TraceId);
        }
    }

    // ------------------------------------------------------------------ open in editor (POST /api/open-in-editor)

    public (int Status, bool Ok, string? Message) OpenInEditor(string? editor, string? path, int? line, int? column)
    {
        if (string.IsNullOrEmpty(editor) || !EditorLauncher.Ids.Contains(editor))
            return (400, false, $"editor must be one of: {string.Join(", ", EditorLauncher.Ids)}");
        var access = CheckSourcePath(path);
        if (access.Result == SourceAccessResult.Forbidden) return (403, false, "The file is outside the served source roots.");
        if (access.Result == SourceAccessResult.NotFound) return (404, false, "File not found.");
        var (ok, message) = EditorLauncher.Open(editor, access.FullPath!, line is > 0 ? line : null, column is > 0 ? column : null);
        if (!ok) Log(null, LogLevels.Warning, $"Open in editor ({editor}): {message}");
        return (200, ok, message);
    }
}

/// <summary>One entry of GET /api/coverage/line.</summary>
/// <param name="Source">"perTest" (exact line hits from a cached single-test coverage run) | "impactMap" (method/class granular).</param>
/// <param name="Stale">perTest only: the test app was rebuilt since the coverage was collected.</param>
public sealed record CoveringTest(string ProjectId, string Id, string Source, int? Hits = null, bool? Stale = null);

/// <summary>GET /api/coverage/line response.</summary>
public sealed record CoveringTestsResult(List<CoveringTest> Tests, bool Partial);

/// <summary>Derives ExplorerState.workspaceName.</summary>
public static class WorkspaceName
{
    /// <summary>
    /// <paramref name="explicitName"/> (LABCOAT_NAME, the Aspire resource name) wins. Otherwise: a single solution
    /// input (or the only solution in a single input directory) gives the solution name; else a single test project
    /// gives the project name; else the name of the input directory / common root.
    /// </summary>
    public static string Resolve(string? explicitName, IEnumerable<string> inputPaths, IEnumerable<string> projectPaths)
    {
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName.Trim();
        var inputs = inputPaths.Select(Path.GetFullPath).ToList();
        var projects = projectPaths.ToList();
        if (inputs.Count == 1 && SolutionName(inputs[0]) is { } solution) return solution;
        if (projects.Count == 1) return Path.GetFileNameWithoutExtension(projects[0]);
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
