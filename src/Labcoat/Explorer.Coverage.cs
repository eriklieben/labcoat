namespace Labcoat;

// Code coverage runs: which settings a project's run uses (CoverageSettings.cs) and what happens to its warm test host.
public sealed partial class Explorer
{
    /// <summary>Coverage.Settings / Include / Exclude (command line, settings files, AppHost).</summary>
    public CoverageOptions CoverageOptions { get; init; } = CoverageOptions.None;

    private sealed partial class ProjectEntry
    {
        /// <summary>The MSBuild property TestingPlatformCommandLineArguments (may name --coverage-settings).</summary>
        public string? TestingPlatformArguments;
        /// <summary>Assembly names of the projects the test project references, transitively.</summary>
        public List<string> ReferencedAssemblies = [];
    }

    /// <summary>The full report (Coverage page): GET /api/coverage.</summary>
    public CoverageReport? FullCoverageReport => _fullCoverage.Report;

    /// <summary>The latest coverage run's own report: GET /api/coverage?scope=last.</summary>
    public CoverageReport? LastCoverageReport
    {
        get { lock (Sync) return _coverage; }
    }

    private static string CoverageDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "labcoat", "coverage");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>The settings of a coverage run of <paramref name="p"/> (logged, and listed in the report).</summary>
    private CoveragePlan CoveragePlanFor(ProjectEntry p)
    {
        if (p.Plugin is { } plugin) return RunnerCoveragePlan(p, plugin);
        CoverageProject project;
        var tests = TestAssemblyNames();
        lock (Sync)
        {
            project = new CoverageProject
            {
                ProjectId = p.Project.Id,
                ProjectPath = p.Project.Path,
                TargetPath = p.TargetPath!,
                TestingPlatformArguments = p.TestingPlatformArguments,
                ReferencedAssemblies = p.ReferencedAssemblies,
                TestAssemblies = tests,
                WorkspaceRoot = _inputRoots.FirstOrDefault(),
            };
        }
        var plan = CoverageSettingsResolver.Plan(project, CoverageOptions, Path.Combine(CoverageDir, "settings"), (level, message) => Log(p.Project.Id, level, message));
        Log(p.Project.Id, LogLevels.Info, "Coverage settings: " + plan.Info.Describe(Path.GetDirectoryName(p.Project.Path)));
        return plan;
    }

    /// <summary>
    /// A runner package's project: the runner collects coverage with its own settings (e.g. the coverage section of the
    /// Vitest config); Labcoat only picks the output and keeps its include / exclude globs for the report.
    /// </summary>
    private CoveragePlan RunnerCoveragePlan(ProjectEntry p, RunnerPlugin plugin)
    {
        var dir = Path.GetDirectoryName(p.Project.Path)!;
        var config = plugin.ConfigFiles.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists) ?? p.Project.Path;
        var info = new CoverageSettingsInfo
        {
            ProjectId = p.Project.Id, Source = CoverageSettingsInfo.RunnerSource, Path = config, Origin = plugin.DisplayName,
            Instrumentation = CoverageSettingsInfo.RunnerSource,
            Include = CoverageOptions.Include.Count > 0 ? [.. CoverageOptions.Include] : null,
            Exclude = CoverageOptions.Exclude.Count > 0 ? [.. CoverageOptions.Exclude] : null,
        };
        Log(p.Project.Id, LogLevels.Info, "Coverage settings: " + info.Describe(dir));
        return new CoveragePlan([], info, CoverageFilter.ForSettingsFile(CoverageOptions));
    }

    /// <summary>
    /// Runs a one-off coverage process (<paramref name="run"/>) next to the project's warm test host.
    /// <para>
    /// Dynamic instrumentation (the CLR profiler; the default wherever the extension ships one for this platform) leaves
    /// the output folder alone: the warm host stays and serves the next run. It is only replaced when an assembly
    /// really changed under it meanwhile (a build).
    /// </para>
    /// <para>
    /// Static instrumentation rewrites the assemblies of the output folder in place, which corrupts a warm host that has
    /// them loaded; and a file that host holds open (the runtime reads a module's .pdb for stack traces with line
    /// numbers and keeps it open with a shared lock) makes the instrumentation skip that module, which then shows 0
    /// lines covered. So the warm host is stopped first, and a fresh one is started afterwards.
    /// </para>
    /// </summary>
    private async Task RunCoverageProcessAsync(ProjectEntry p, CoveragePlan plan, Func<Task> run, CancellationToken ct)
    {
        var warm = CurrentWarm(p);
        if (!plan.RewritesOutput)
        {
            await run();
            await RenewWarmIfRewrittenAsync(p, warm, ct);
            return;
        }
        if (warm is not null)
        {
            lock (Sync)
            {
                if (p.Warm == warm) p.Warm = null;
            }
            warm.Watch.Dispose();
            SetHostState(p, "cold");
            Log(p.Project.Id, LogLevels.Info, "Stopping the warm test host for the coverage run: static instrumentation rewrites the assemblies it has loaded " +
                "(no CLR profiler of the coverage extension for this platform, or the settings ask for static instrumentation).");
            await StopHostAsync(warm.Host); // wait: the files it holds open must be free before the instrumentation starts
        }
        try
        {
            await run();
        }
        finally
        {
            if (warm is not null && !ct.IsCancellationRequested && !_shutdown.IsCancellationRequested && p.TargetPath is not null && File.Exists(p.TargetPath))
            {
                try
                {
                    if (await EnsureWarmAsync(p, ct) is not null) Log(p.Project.Id, LogLevels.Info, "Test host is warm");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Log(p.Project.Id, LogLevels.Warning, "Could not start a new warm test host: " + ex.Message);
                }
                catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
            }
        }
    }

    /// <summary>Assembly name of a project file: a literal &lt;AssemblyName&gt;, else the file name.</summary>
    internal static string AssemblyNameOf(string projectPath)
    {
        try
        {
            var name = System.Xml.Linq.XDocument.Load(projectPath).Descendants().Where(e => e.Name.LocalName == "AssemblyName")
                .Select(e => e.Value.Trim()).LastOrDefault(v => v.Length > 0 && !v.Contains("$("));
            if (name is not null) return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { /* unreadable project file: fall back below */ }
        return Path.GetFileNameWithoutExtension(projectPath);
    }
}
