using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>A failed build's diagnostics and its error / warning counts (<see cref="IExplorerEffects.SetStatus"/>).</summary>
internal sealed record BuildFailure(List<BuildDiagnostic> Diagnostics, int ErrorCount, int WarningCount);

// Builds (docs/features.md "Running tests"): one build for every project that needs one (BuildBatch.cs), then each
// project's evaluation (TargetPath, frameworks, references) before it is discovered or run.
internal sealed class ProjectBuilder(WorkspaceState state, IExplorerEffects effects, PlatformCheck platform, TestHostPool hosts, WatchMode watch,
    WorkspaceSettings settings, int maxParallel, Func<string[], string, CancellationToken, Task<(int ExitCode, string Output)>> runDotnetAsync)
{
    // The JSON of `dotnet msbuild -getProperty:… -getItem:…`.
    private const string EvaluatedProperties = "Properties";
    private const string EvaluatedItems = "Items";

    /// <summary>
    /// Builds the projects; returns the ones that built (or were up to date), in the order given. A project without source
    /// changes since its last build is skipped (<paramref name="onlyIfChanged"/>); the others build together in one
    /// <c>dotnet build</c> of a generated solution (<see cref="BuildBatch"/>), so MSBuild builds them in parallel and a
    /// shared dependency once (never two builds of one project at the same time). A multi-targeted project that builds
    /// one framework (<c>-f</c>) builds on its own afterwards.
    /// </summary>
    public async Task<List<ProjectEntry>> BuildProjectsAsync(IReadOnlyList<ProjectEntry> projects, bool onlyIfChanged, CancellationToken ct)
    {
        if (projects.Count <= 1) return await BuildOneByOneAsync(projects, ct, onlyIfChanged);
        // Runner packages' projects have nothing to build: ready at once, never in the batch.
        foreach (var p in projects.Where(p => p.Plugin is not null)) PrepareRunnerProject(p);
        var (ready, batch, separate) = PlanBuilds(projects.Where(p => p.Plugin is null).ToList(), onlyIfChanged);
        ready.UnionWith(projects.Where(p => p.Plugin is not null));
        if (batch.Count > 0) ready.UnionWith(await BuildBatchAsync(batch, ct));
        ready.UnionWith(await BuildOneByOneAsync(separate, ct));
        return projects.Where(ready.Contains).ToList();
    }

    /// <summary>Builds one project (unless <paramref name="onlyIfChanged"/> and it is up to date); false when it can't run.</summary>
    public async Task<bool> BuildAsync(ProjectEntry p, CancellationToken ct, bool onlyIfChanged = false)
    {
        if (p.Plugin is not null)
        {
            PrepareRunnerProject(p);
            return true;
        }
        if (!await platform.CheckAsync(p, ct)) return false; // not on Microsoft.Testing.Platform: never built or started
        if (onlyIfChanged && IsBuildUpToDate(p))
        {
            effects.Log(p.Project.Id, LogLevels.Info, "No source changes since last build; skipping build");
            return true;
        }
        var buildStart = DateTime.UtcNow;
        await hosts.StopBeforeBuildAsync(p); // Windows only: loaded assemblies are locked
        effects.SetStatus(p, ProjectStatus.Building);
        effects.Log(p.Project.Id, LogLevels.Info, $"Building {p.Project.Path}");
        var sw = Stopwatch.StartNew();
        // Multi-targeted: build only the framework the tests run on (known after the first evaluation).
        string[] tfmArgs;
        lock (state.Sync) tfmArgs = p.Project.AvailableFrameworks is { Count: > 1 } && p.Project.TargetFramework is { } tfm ? ["-f", tfm] : [];
        var (code, output) = await runDotnetAsync(["build", p.Project.Path, "-nologo", "-tl:off", "-v:minimal", .. tfmArgs], Path.GetDirectoryName(p.Project.Path)!, ct);
        if (ct.IsCancellationRequested)
        {
            effects.SetStatus(p, ProjectStatus.Idle);
            return false;
        }
        if (code != 0)
        {
            ReportBuildFailure(p, code, output, sw.Elapsed);
            return false;
        }
        return await CompleteBuildAsync(p, buildStart, sw.Elapsed, ct);
    }

    /// <summary>
    /// The "build" of a runner package's project (RunnerProjects.cs): nothing to compile (the runner transforms sources
    /// itself), so only what a build sets up: the test app path, the watched folder and the build time the up-to-date
    /// checks compare with.
    /// </summary>
    private void PrepareRunnerProject(ProjectEntry p)
    {
        var dir = Path.GetDirectoryName(p.Project.Path)!;
        p.TargetPath = p.Project.Path;
        p.TargetPathStamp = File.GetLastWriteTimeUtc(p.Project.Path);
        var changedWatch = p.WatchDirs is not [var only] || only != dir;
        p.WatchDirs = [dir];
        p.LastBuildStartUtc = DateTime.UtcNow;
        if (changedWatch && watch.Enabled) watch.Start(p);
        hosts.InvalidateIfStale(p);
        lock (state.Sync) p.Project.ResultsOutdated = null;
        effects.SetStatus(p, ProjectStatus.Idle);
    }

    /// <summary>Up to date (no build), in the batch, or on their own (one framework; a batch of one).</summary>
    private (HashSet<ProjectEntry> UpToDate, List<ProjectEntry> Batch, List<ProjectEntry> Separate) PlanBuilds(IReadOnlyList<ProjectEntry> projects, bool onlyIfChanged)
    {
        var upToDate = new HashSet<ProjectEntry>();
        var batch = new List<ProjectEntry>();
        var separate = new List<ProjectEntry>();
        foreach (var p in projects)
        {
            if (onlyIfChanged && IsBuildUpToDate(p))
            {
                effects.Log(p.Project.Id, LogLevels.Info, "No source changes since last build; skipping build");
                upToDate.Add(p);
            }
            else if (BuildsOneFramework(p)) separate.Add(p);
            else batch.Add(p);
        }
        if (batch.Count == 1)
        {
            separate.Insert(0, batch[0]);
            batch.Clear();
        }
        return (upToDate, batch, separate);
    }

    /// <summary>The project's output exists and no watched source file changed since its last successful build started.</summary>
    private static bool IsBuildUpToDate(ProjectEntry p) =>
        p.LastBuildStartUtc is { } last && p.TargetPath is not null && File.Exists(p.TargetPath) && !SourcesChangedSince(p, last);

    /// <summary>A multi-targeted project whose tests run on one framework: built with <c>-f</c>, which a solution build can't pass.</summary>
    private bool BuildsOneFramework(ProjectEntry p)
    {
        lock (state.Sync) return p.Project.AvailableFrameworks is { Count: > 1 } && p.Project.TargetFramework is not null;
    }

    /// <summary>One <c>dotnet build</c> of a generated solution with <paramref name="batch"/>; returns the projects that built.</summary>
    private async Task<List<ProjectEntry>> BuildBatchAsync(List<ProjectEntry> batch, CancellationToken ct)
    {
        var buildStart = DateTime.UtcNow;
        foreach (var p in batch)
        {
            await hosts.StopBeforeBuildAsync(p); // Windows only: loaded assemblies are locked
            effects.SetStatus(p, ProjectStatus.Building);
        }
        var paths = batch.Select(p => p.Project.Path).ToList();
        effects.Log(null, LogLevels.Info, $"Building {batch.Count} projects in one build: {string.Join(", ", batch.Select(p => p.Project.Name))}");
        var cwd = Path.GetDirectoryName(paths[0])!;
        string solution;
        try { solution = BuildBatch.WriteSolution(paths, Path.Combine(cwd, "obj", "labcoat")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            effects.Log(null, LogLevels.Warning, $"Could not write the build solution ({ex.Message}); building the projects one by one.");
            return await BuildOneByOneAsync(batch, ct);
        }
        var sw = Stopwatch.StartNew();
        int code;
        string output;
        try { (code, output) = await runDotnetAsync(["build", solution, "-nologo", "-tl:off", "-v:minimal"], cwd, ct); }
        finally { TryDelete(solution); }
        if (ct.IsCancellationRequested)
        {
            foreach (var p in batch) effects.SetStatus(p, ProjectStatus.Idle);
            return [];
        }
        var elapsed = sw.Elapsed;
        var split = BuildBatch.Split(output, code, paths, BuildClosure);
        return await BoundedParallel.ForEachAsync(batch, maxParallel, async p =>
        {
            var r = split[p.Project.Path];
            if (!r.Succeeded)
            {
                ReportBuildFailure(p, code == 0 ? 1 : code, r.Output, elapsed);
                return false;
            }
            return await CompleteBuildAsync(p, buildStart, elapsed, ct);
        }, ct);
    }

    private async Task<List<ProjectEntry>> BuildOneByOneAsync(IReadOnlyList<ProjectEntry> projects, CancellationToken ct, bool onlyIfChanged = false)
    {
        var ok = new List<ProjectEntry>();
        foreach (var p in projects)
            if (!ct.IsCancellationRequested && await BuildAsync(p, ct, onlyIfChanged)) ok.Add(p);
        return ok;
    }

    /// <summary>After a successful build of the project: its evaluation (TargetPath, frameworks, references), then idle.</summary>
    private async Task<bool> CompleteBuildAsync(ProjectEntry p, DateTime buildStart, TimeSpan elapsed, CancellationToken ct)
    {
        effects.Log(p.Project.Id, LogLevels.Info, $"Build succeeded in {elapsed.TotalSeconds:0.0}s");
        lock (state.Sync) p.Project.ResultsOutdated = null; // published with the Idle status below
        try
        {
            await ResolveTargetPathAsync(p, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            effects.SetStatus(p, ProjectStatus.Idle);
            return false;
        }
        catch (Exception ex)
        {
            effects.SetStatus(p, ProjectStatus.Error, ex.Message);
            effects.Log(p.Project.Id, LogLevels.Error, ex.Message);
            return false;
        }
        if (!platform.AcceptsAfterBuild(p)) return false;
        p.LastBuildStartUtc = buildStart;
        hosts.InvalidateIfStale(p);
        effects.SetStatus(p, ProjectStatus.Idle);
        return true;
    }

    private void ReportBuildFailure(ProjectEntry p, int code, string output, TimeSpan elapsed)
    {
        var tail = DotnetCli.TailLines(output, 60);
        var diagnostics = BuildOutputParser.Parse(output, out var errorCount, out var warningCount);
        var firstError = FirstBuildError(diagnostics);
        lock (state.Sync)
        {
            // Results produced before this build are from older code now.
            var hadResults = state.Tests.Values.Any(t => t.ProjectId == p.Project.Id && t.Outcome is not (Outcome.NotRun or Outcome.Queued or Outcome.Running));
            if (hadResults) p.Project.ResultsOutdated = true;
        }
        effects.SetStatus(p, ProjectStatus.Error, firstError ?? $"Build failed (exit code {code})", tail,
            build: new BuildFailure(diagnostics, errorCount, warningCount));
        effects.Log(p.Project.Id, LogLevels.Error, $"Build failed after {elapsed.TotalSeconds:0.0}s ({errorCount} error{Words.Plural(errorCount)}, " +
            $"{warningCount} warning{Words.Plural(warningCount)})" + (firstError is null ? "" : ": " + firstError));
    }

    /// <summary>The first build error as one line: <c>file(line,column): error CODE: message</c>.</summary>
    private static string? FirstBuildError(List<BuildDiagnostic> diagnostics)
    {
        var first = diagnostics.FirstOrDefault(d => d.Severity == "error");
        if (first is null) return null;
        var location = first.File is null ? "" : first.File + LinePosition(first) + ": ";
        return location + "error " + (first.Code is null ? "" : first.Code + ": ") + first.Message;
    }

    private static string LinePosition(BuildDiagnostic d)
    {
        if (d.Line is not { } line) return "";
        return d.Column is { } column ? $"({line},{column})" : $"({line})";
    }

    /// <summary>
    /// Cheap up-to-date check: any watched source file (project dir + direct ProjectReference dirs, excluding bin/obj)
    /// modified after the last successful build started? Files outside those dirs (e.g. Directory.Build.props higher up)
    /// are not considered; use "discover" to force a build.
    /// </summary>
    private static bool SourcesChangedSince(ProjectEntry p, DateTime sinceUtc)
    {
        var dirs = p.WatchDirs.Count > 0 ? p.WatchDirs : [Path.GetDirectoryName(p.Project.Path)!];
        var extensions = RunnerProjects.SourceExtensionsOf(p);
        return dirs.Distinct().Any(dir => SourcesChangedUnder(dir, sinceUtc, extensions));
    }

    private static bool SourcesChangedUnder(string dir, DateTime sinceUtc, IReadOnlySet<string> extensions)
    {
        var stack = new Stack<string>([dir]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            try
            {
                if (Directory.EnumerateFiles(d).Any(f => extensions.Contains(Path.GetExtension(f)) && File.GetLastWriteTimeUtc(f) > sinceUtc))
                    return true;
                foreach (var sub in Directory.EnumerateDirectories(d).Where(sub => !IsIgnoredSourceDir(Path.GetFileName(sub))))
                    stack.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: skip it */ }
        }
        return false;
    }

    internal static bool IsIgnoredSourceDir(string name) => name is "bin" or "obj" or "node_modules" || name.StartsWith('.');

    private async Task ResolveTargetPathAsync(ProjectEntry p, CancellationToken ct)
    {
        var stamp = File.GetLastWriteTimeUtc(p.Project.Path);
        if (p.TargetPath is not null && stamp == p.TargetPathStamp && File.Exists(p.TargetPath)) return;

        var dir = Path.GetDirectoryName(p.Project.Path)!;
        string[] args = ["msbuild", p.Project.Path, "-getProperty:TargetPath", "-getProperty:TargetFrameworks", "-getProperty:TargetFramework", "-getItem:ProjectReference",
            "-getProperty:TestingPlatformCommandLineArguments", .. ProjectDiagnostics.Properties.Select(n => "-getProperty:" + n), "-getItem:PackageReference"];
        var (code, output) = await runDotnetAsync(args, dir, ct);
        if (code != 0) throw new InvalidOperationException("Could not evaluate project: " + DotnetCli.TailLines(output, 20));
        var json = JsonNode.Parse(output)!;
        var targetPath = json[EvaluatedProperties]?["TargetPath"]?.GetValue<string>();
        ApplyEvaluation(p, json);
        var single = json[EvaluatedProperties]?["TargetFramework"]?.GetValue<string>();
        var frameworks = (json[EvaluatedProperties]?["TargetFrameworks"]?.GetValue<string>() ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (frameworks.Count == 0 && !string.IsNullOrEmpty(single)) frameworks.Add(single);
        var chosen = single;
        if (string.IsNullOrEmpty(targetPath)) (targetPath, chosen) = await ResolveMultiTargetAsync(p, dir, frameworks, ct);
        lock (state.Sync)
        {
            p.Project.TargetFramework = chosen;
            p.Project.AvailableFrameworks = frameworks.Count > 0 ? frameworks : null;
        }

        var watchDirs = WatchDirsOf(json, dir);
        p.TargetPath = targetPath;
        p.TargetPathStamp = stamp;
        var changedWatch = !watchDirs.SequenceEqual(p.WatchDirs);
        p.WatchDirs = watchDirs;
        if (changedWatch && watch.Enabled) watch.Start(p);
    }

    /// <summary>The evaluated properties and items of <c>dotnet msbuild -getProperty/-getItem</c> kept for diagnostics and runs.</summary>
    private static void ApplyEvaluation(ProjectEntry p, JsonNode json)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in ProjectDiagnostics.Properties)
            if (json[EvaluatedProperties]?[n] is JsonValue v && v.TryGetValue<string>(out var sv)) props[n] = sv;
        p.Properties = props;
        p.TestingPlatformArguments = json[EvaluatedProperties]?["TestingPlatformCommandLineArguments"]?.GetValue<string>() is { Length: > 0 } tpa ? tpa : null;
        p.ReferencedAssemblies = ProjectReferences.Closure(json[EvaluatedItems]?["ProjectReference"], assembliesOnly: true).Select(CoverageService.AssemblyNameOf)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        p.PackageReferences = json[EvaluatedItems]?["PackageReference"] is JsonArray pkgs
            ? pkgs.Select(x => x?["Identity"]?.GetValue<string>()).OfType<string>().ToList()
            : [];
    }

    /// <summary>
    /// Multi-targeted project (no single TargetPath): the framework chosen with setTargetFramework, else the highest .NET
    /// version (not just the first), and the TargetPath for it.
    /// </summary>
    private async Task<(string TargetPath, string Framework)> ResolveMultiTargetAsync(ProjectEntry p, string dir, List<string> frameworks, CancellationToken ct)
    {
        var wanted = settings.TargetFramework(p.Project.Path);
        var chosen = wanted is not null && frameworks.Contains(wanted, StringComparer.OrdinalIgnoreCase) ? wanted : Explorer.PickTargetFramework(frameworks);
        if (chosen is null) throw new InvalidOperationException("Could not determine TargetPath of the project.");
        var (code, output) = await runDotnetAsync(["msbuild", p.Project.Path, "-getProperty:TargetPath", $"-p:TargetFramework={chosen}"], dir, ct);
        var targetPath = output.Trim();
        if (code != 0 || targetPath.Length == 0) throw new InvalidOperationException("Could not determine TargetPath of the project.");
        if (frameworks.Count > 1)
            effects.Log(p.Project.Id, LogLevels.Info, $"Multi-targeted project ({string.Join(", ", frameworks)}); running on {chosen}" +
                (wanted is null ? " (highest; pick another with setTargetFramework)" : ""));
        return (targetPath, chosen);
    }

    /// <summary>The project directory, its direct ProjectReference directories and the transitive ones.</summary>
    private static List<string> WatchDirsOf(JsonNode json, string dir)
    {
        var watchDirs = new List<string> { dir };
        if (json[EvaluatedItems]?["ProjectReference"] is JsonArray refs)
            watchDirs.AddRange(refs.Select(r => r?["FullPath"]?.GetValue<string>()).OfType<string>().Select(full => Path.GetDirectoryName(full)!));
        // Transitive references too (product code two hops away still affects the tests).
        foreach (var d in ProjectReferences.ClosureDirs(json[EvaluatedItems]?["ProjectReference"]).Where(d => !watchDirs.Contains(d)))
            watchDirs.Add(d);
        return watchDirs;
    }

    /// <summary>The project file and the project files it references, transitively (read from the files, for build error attribution).</summary>
    private static IReadOnlyCollection<string> BuildClosure(string project)
    {
        var seen = new HashSet<string>(PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([project]);
        while (queue.TryDequeue(out var proj))
        {
            if (!seen.Add(proj) || !File.Exists(proj)) continue;
            foreach (var r in ProjectReferences.Of(proj, assembliesOnly: false)) queue.Enqueue(r);
        }
        return seen;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover in obj/labcoat */ }
    }
}
