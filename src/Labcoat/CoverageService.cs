namespace Labcoat;

// Code coverage runs: which settings a project's run uses (CoverageSettings.cs), what happens to its warm test host, and
// the reports a run publishes (its own, and the full report of the Coverage page: FullCoverage.cs).
internal sealed class CoverageService(WorkspaceState state, IExplorerEffects effects, OperationGate gate, TestHostPool hosts,
    FullCoverage fullCoverage, CoverageOptions options, IReadOnlyList<string> inputRoots, Func<HashSet<string>> testAssemblyNames,
    Func<string[], string, CancellationToken, Task<(int ExitCode, string Output)>> runDotnetAsync)
{
    /// <summary>The latest coverage run's own report (lock Sync): GET /api/coverage?scope=last, state.coverage.</summary>
    public CoverageReport? Last { get; set; }

    private static string CoverageDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "labcoat", "coverage");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string FilePath(string projectId, string runId) => Path.Combine(CoverageDir, $"{projectId}-{runId}.cobertura.xml");

    /// <summary>The settings of a coverage run of <paramref name="p"/> (logged, and listed in the report).</summary>
    public CoveragePlan PlanFor(ProjectEntry p)
    {
        if (p.Plugin is { } plugin) return RunnerPlanFor(p, plugin);
        CoverageProject project;
        var tests = testAssemblyNames();
        lock (state.Sync)
        {
            project = new CoverageProject
            {
                ProjectId = p.Project.Id,
                ProjectPath = p.Project.Path,
                TargetPath = p.TargetPath!,
                TestingPlatformArguments = p.TestingPlatformArguments,
                ReferencedAssemblies = p.ReferencedAssemblies,
                TestAssemblies = tests,
                WorkspaceRoot = inputRoots.Count > 0 ? inputRoots[0] : null,
            };
        }
        var plan = CoverageSettingsResolver.Plan(project, options, Path.Combine(CoverageDir, "settings"), (level, message) => effects.Log(p.Project.Id, level, message));
        effects.Log(p.Project.Id, LogLevels.Info, "Coverage settings: " + plan.Info.Describe(Path.GetDirectoryName(p.Project.Path)));
        return plan;
    }

    /// <summary>
    /// A runner package's project: the runner collects coverage with its own settings (e.g. the coverage section of the
    /// Vitest config); Labcoat only picks the output and keeps its include / exclude globs for the report.
    /// </summary>
    private CoveragePlan RunnerPlanFor(ProjectEntry p, RunnerPlugin plugin)
    {
        var dir = Path.GetDirectoryName(p.Project.Path)!;
        var config = plugin.ConfigFiles.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists) ?? p.Project.Path;
        var info = new CoverageSettingsInfo
        {
            ProjectId = p.Project.Id, Source = CoverageSettingsInfo.RunnerSource, Path = config, Origin = plugin.DisplayName,
            Instrumentation = CoverageSettingsInfo.RunnerSource,
            Include = options.Include.Count > 0 ? [.. options.Include] : null,
            Exclude = options.Exclude.Count > 0 ? [.. options.Exclude] : null,
        };
        effects.Log(p.Project.Id, LogLevels.Info, "Coverage settings: " + info.Describe(dir));
        return new CoveragePlan([], info, CoverageFilter.ForSettingsFile(options));
    }

    /// <summary>Does the test app support <c>--coverage</c> (Microsoft.Testing.Extensions.CodeCoverage)? Cached per build.</summary>
    public async Task<bool> SupportsAsync(ProjectEntry p, CancellationToken ct)
    {
        if (p.Plugin is { } plugin) return plugin.Coverage; // runner.json says so
        var stamp = File.GetLastWriteTimeUtc(p.TargetPath!);
        if (p.SupportsCoverage is { } cached && p.SupportsCoverageStamp == stamp) return cached;
        var (_, output) = await runDotnetAsync([p.TargetPath!, "--help"], Path.GetDirectoryName(p.TargetPath!)!, ct);
        var supported = output.Contains("--coverage-output-format", StringComparison.Ordinal);
        p.SupportsCoverage = supported;
        p.SupportsCoverageStamp = stamp;
        return supported;
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
    public async Task RunProcessAsync(ProjectEntry p, CoveragePlan plan, Func<Task> run, CancellationToken ct)
    {
        var warm = hosts.CurrentWarm(p);
        if (!plan.RewritesOutput)
        {
            await run();
            await hosts.RenewIfRewrittenAsync(p, warm, ct);
            return;
        }
        if (warm is not null)
        {
            lock (state.Sync)
            {
                if (p.Warm == warm) p.Warm = null;
            }
            warm.Watch.Dispose();
            hosts.SetHostState(p, "cold");
            effects.Log(p.Project.Id, LogLevels.Info, "Stopping the warm test host for the coverage run: static instrumentation rewrites the assemblies it has loaded " +
                "(no CLR profiler of the coverage extension for this platform, or the settings ask for static instrumentation).");
            await TestHostPool.StopHostAsync(warm.Host); // wait: the files it holds open must be free before the instrumentation starts
        }
        try
        {
            await run();
        }
        finally
        {
            if (warm is not null && !ct.IsCancellationRequested && !gate.IsShuttingDown && p.TargetPath is not null && File.Exists(p.TargetPath))
            {
                try
                {
                    if (await hosts.EnsureWarmAsync(p, ct) is not null) effects.Log(p.Project.Id, LogLevels.Info, "Test host is warm");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    effects.Log(p.Project.Id, LogLevels.Warning, "Could not start a new warm test host: " + ex.Message);
                }
                catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
            }
        }
    }

    /// <summary>
    /// After a coverage run: the run's own report (state.coverage, <c>coverageUpdated</c>: a subset run gives that
    /// subset's coverage) and the full report of the Coverage page (state.fullCoverage, <c>fullCoverageUpdated</c>):
    /// a project that ran every test replaces its part of it, a project that ran selected tests is merged in where
    /// the code is unchanged (FullCoverage.cs).
    /// </summary>
    public void Publish(RunExecution x, string runId)
    {
        // Subset as soon as any covered project ran selected tests (testIds then lists every test of the run).
        var subset = x.ProjectCoverage.Any(c => c.Ids is not null);
        var subsetIds = subset ? x.Requested.Select(k => k.Id).Distinct().ToList() : null;
        var report = x.Accumulator.Build(runId, x.CoveredProjects, subsetIds) with { Settings = x.CoverageSettings };

        CoverageReport? full = null;
        var notes = new List<string>();
        foreach (var (projectId, ids, data, settings) in x.ProjectCoverage)
        {
            if (ids is null)
            {
                full = fullCoverage.SetFull(projectId, data, runId, settings);
                continue;
            }
            var (merge, merged) = fullCoverage.MergeSubset(projectId, data, runId, ids.Count);
            full = merged ?? full;
            notes.AddRange(MergeNotes(projectId, merge));
        }
        full = fullCoverage.RefreshStale() ?? full; // the other projects' files may have changed meanwhile

        lock (state.Sync)
        {
            Last = report;
            if (full is not null) effects.Emit(new FullCoverageUpdatedEvent(full));
            effects.Emit(new CoverageUpdatedEvent(report));
        }
        effects.Log(null, LogLevels.Info, CoverageLine(report, subsetIds, notes));
    }

    private static IEnumerable<string> MergeNotes(string projectId, FullCoverage.MergeResult merge)
    {
        if (!merge.HasFull)
        {
            yield return $"{projectId}: no full coverage run yet (Run all with coverage)";
            yield break;
        }
        if (merge.FilesChanged > 0) yield return $"{projectId}: merged into {merge.FilesChanged} unchanged file{Words.Plural(merge.FilesChanged)}";
        if (merge.ChangedFiles.Count > 0)
            yield return $"{projectId}: {merge.ChangedFiles.Count} file{Words.Plural(merge.ChangedFiles.Count)} changed since the full run " +
                         $"({string.Join(", ", merge.ChangedFiles.Select(Path.GetFileName).Distinct().Take(3))}), not merged: run all with coverage to refresh";
    }

    /// <summary>The log line of a run's coverage; <paramref name="subsetIds"/> set when the run covered selected tests.</summary>
    private string CoverageLine(CoverageReport report, List<string>? subsetIds, List<string> notes)
    {
        var what = report.NoCoverableLines == true
            ? $"Coverage: no coverable lines in {report.Files.Count} files (with Labcoat's defaults, test assemblies are left out; put the code under test in a separate project, or check the coverage settings)"
            : $"Coverage: {report.LineRate:P1} lines ({report.LinesCovered}/{report.LinesValid}), {report.BranchRate:P1} branches in {report.Files.Count} files";
        if (subsetIds is null) return what;
        what += $" of this run's {subsetIds.Count} test{Words.Plural(subsetIds.Count)}";
        if (fullCoverage.Report is { } current)
            return what + "; the Coverage page keeps the full report: " +
                   $"{current.LineRate:P1} lines ({current.LinesCovered}/{current.LinesValid})" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        return what + "; no full report yet for the Coverage page (Run all with coverage)";
    }

    /// <summary>After a run: marks the full report's files that changed since their full run (FullCoverage.cs).</summary>
    public void RefreshFullStale()
    {
        if (fullCoverage.RefreshStale() is not { } full) return;
        lock (state.Sync) effects.Emit(new FullCoverageUpdatedEvent(full));
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
