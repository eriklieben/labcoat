using System.Diagnostics;

namespace Labcoat;

/// <summary>An <c>--export format=path</c> target of <c>--run</c>.</summary>
public sealed record ExportTarget(string Format, string Path);

/// <summary><c>--affected</c>: what the changes are compared with (<c>AffectedBases</c>) and the ref / range of "branch" and "range".</summary>
public sealed record AffectedSelector(string Base, string? Ref);

// --run, --export, --filter, --export-paths, --affected, --impact-map-file, --build-impact-map (CliRun.cs): the
// non-interactive mode for CI.
public sealed partial class StartOptions
{
    /// <summary>--run: build, run the tests once, write the exports and exit (no web server, no browser).</summary>
    public bool RunOnce { get; private set; }
    /// <summary>--export format=path (repeatable): junit, html, lcov or cobertura.</summary>
    public List<ExportTarget> Exports { get; } = [];
    /// <summary>--filter: only the tests whose name, class or namespace matches one of these patterns.</summary>
    public List<string> Filters { get; } = [];
    /// <summary>--export-paths absolute: absolute source paths in lcov / Cobertura (default: relative to the workspace root).</summary>
    public bool ExportAbsolutePaths { get; private set; }

    /// <summary>--affected [working|branch|&lt;range&gt;]: run only the tests the changes in git affect. Null = every test.</summary>
    public AffectedSelector? Affected { get; private set; }
    /// <summary>--impact-map-file: where the impact map is loaded from before planning and saved to after --build-impact-map.</summary>
    public string? ImpactMapFile { get; private set; }
    /// <summary>--build-impact-map: run the impact-map pass and save it to <see cref="ImpactMapFile"/>.</summary>
    public bool BuildImpactMap { get; private set; }

    private static readonly string[] AllExportFormats = [.. ExportFormats.RunFormats, .. ExportFormats.CoverageFormats];

    private string? ApplyRunOption(string option, ArgReader args)
    {
        switch (option)
        {
            case "--run":
                RunOnce = true;
                return null;
            case "--export":
                return AddExport(args.Value());
            case "--filter":
                var filter = args.Value();
                if (string.IsNullOrWhiteSpace(filter)) return "--filter needs a pattern, e.g. CalculatorTests or *Add*.";
                Filters.AddRange(RequestGuard.SplitList(filter.Replace(' ', ',')));
                return null;
            case "--affected":
                return SetAffected(args.OptionalValue(IsAffectedValue));
            case "--impact-map-file":
                var file = args.Value();
                if (string.IsNullOrWhiteSpace(file)) return "--impact-map-file needs a file, e.g. impact.json (or impact.json.gz).";
                if (Directory.Exists(file)) return $"--impact-map-file: '{file}' is a directory; give a file.";
                ImpactMapFile = file.Trim();
                return null;
            case "--build-impact-map":
                BuildImpactMap = true;
                return null;
            default: // --export-paths
                var mode = args.Value();
                if (mode is not ("relative" or "absolute")) return "--export-paths needs relative or absolute.";
                ExportAbsolutePaths = mode == "absolute";
                return null;
        }
    }

    private string? AddExport(string? value)
    {
        const string Usage = "--export needs format=file, e.g. junit=out/results.xml (formats: junit, html, lcov, cobertura).";
        var eq = value?.IndexOf('=') ?? -1;
        if (value is null || eq <= 0 || eq == value.Length - 1) return Usage;
        var format = value[..eq].Trim().ToLowerInvariant();
        if (!AllExportFormats.Contains(format)) return $"--export: unknown format '{value[..eq]}'. {Usage}";
        Exports.Add(new ExportTarget(format, value[(eq + 1)..].Trim()));
        return null;
    }

    /// <summary>--affected: working (uncommitted), branch (the default: against the base branch) or a commit range / commit.</summary>
    private string? SetAffected(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v) || v.Equals("branch", StringComparison.OrdinalIgnoreCase)) Affected = new AffectedSelector(AffectedBases.Branch, null);
        else if (v.ToLowerInvariant() is "working" or "uncommitted") Affected = new AffectedSelector(AffectedBases.Working, null);
        else if (GitRepo.IsValidRef(v)) Affected = new AffectedSelector(AffectedBases.Range, v);
        else return $"--affected needs working, branch or a commit range such as origin/main..HEAD (got '{v}').";
        return null;
    }

    /// <summary>The value of --affected when it follows the option: only a base name or a range, so a path after a bare --affected stays a path.</summary>
    internal static bool IsAffectedValue(string value) =>
        value.ToLowerInvariant() is "working" or "uncommitted" or "branch"
        || (value.Contains("..", StringComparison.Ordinal) && GitRepo.IsValidRef(value) && !Directory.Exists(value) && !File.Exists(value));

    /// <summary>The options that only make sense with --run, and the ones that need each other.</summary>
    private string? ValidateRunOptions()
    {
        if (!RunOnce && (Exports.Count > 0 || Filters.Count > 0 || ExportAbsolutePaths))
            return "--export, --filter and --export-paths need --run.";
        if (!RunOnce && (Affected is not null || ImpactMapFile is not null || BuildImpactMap))
            return "--affected, --impact-map-file and --build-impact-map need --run.";
        if (BuildImpactMap && ImpactMapFile is null && Affected is null)
            return "--build-impact-map needs --impact-map-file (to keep the map) or --affected (to use it now).";
        if (ImpactMapFile is not null && Affected is null && !BuildImpactMap)
            return "--impact-map-file needs --affected (to use the map) or --build-impact-map (to create it).";
        return null;
    }
}

/// <summary>
/// <c>labcoat --run</c>: the non-interactive mode for CI. Builds and discovers the test projects, runs every test (or
/// the <c>--filter</c>ed ones; with <c>--coverage</c>, which an lcov / Cobertura export implies), writes the
/// <c>--export</c> files and exits: <see cref="ExitPassed"/> when nothing failed, <see cref="ExitFailed"/> when a test
/// failed (or the run was aborted / cancelled), <see cref="ExitBuildError"/> when a project did not build or discover
/// (or nothing could run). No web server, no browser. Uses the same explorer, history and settings files as the UI.
/// </summary>
public static partial class CliRun
{
    public const int ExitPassed = 0;
    public const int ExitFailed = 1;
    public const int ExitBuildError = 2;

    /// <summary>RunSummary.trigger of a <c>--run</c> run.</summary>
    public const string Trigger = "cli";

    private static string ErrorLabel => Cli.Red("error");
    private static string WarningLabel => Cli.Yellow("warning");

    public static async Task<int> RunAsync(StartOptions options, LabcoatAppOptions app, bool coverage, IReadOnlyList<string> notes, CancellationToken ct)
    {
        Console.WriteLine($"{Cli.Bold("Labcoat " + Cli.Version)} {Cli.Dim("--run")}: {string.Join(", ", app.Paths)}");
        foreach (var note in notes) Console.WriteLine(Cli.Dim("  " + note));
        if (app.Projects.Count == 0)
        {
            await Console.Error.WriteLineAsync($"{ErrorLabel} No test projects found (see --help).");
            return ExitBuildError;
        }
        var explorer = LabcoatApp.CreateExplorer(app, new EventHub());
        await using (explorer)
        {
            explorer.ReleaseConsole();
            using var cancel = new CancelOnInterrupt(explorer, ct);
            return await RunAndExportAsync(explorer, options, coverage, cancel.Token);
        }
    }

    private static async Task<int> RunAndExportAsync(Explorer explorer, StartOptions options, bool coverage, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await explorer.DiscoverAsync(null);
        if (ct.IsCancellationRequested) return ExitFailed;
        var broken = BrokenProjects(explorer);
        if (options.Affected is not null) explorer.RefineImpactInBackground = false; // no coverage processes beside the run
        if (options.ImpactMapFile is { } mapFile) LoadImpactMap(explorer, mapFile, options);
        List<string>? ids = null;
        if (options.Filters.Count > 0)
        {
            ids = Matching(explorer.Snapshot().Tests, options.Filters);
            Console.WriteLine($"  --filter {string.Join(",", options.Filters)}: {ids.Count} test{(ids.Count == 1 ? "" : "s")}");
            if (ids.Count == 0)
            {
                await Console.Error.WriteLineAsync($"{ErrorLabel} No test matches --filter.");
                return broken.Count > 0 ? ExitBuildError : ExitFailed;
            }
        }
        var wantCoverage = coverage || options.Exports.Exists(e => ExportFormats.CoverageFormats.Contains(e.Format));
        AffectedSelection? affected = null;
        if (options.Affected is { } selector)
        {
            try { affected = await PlanAffectedRunAsync(explorer, options, selector, ids, ct); }
            catch (AffectedException ex)
            {
                await Console.Error.WriteLineAsync($"{ErrorLabel} --affected: {ex.Message}");
                WriteExports(explorer, options, null, nothingToRun: true);
                return ExitBuildError;
            }
            if (affected.Tests == 0) return await NothingAffectedAsync(explorer, options, broken);
        }
        var request = affected is null
            ? explorer.RequestRun(new Explorer.RunRequestOptions { TestIds = ids, Coverage = wantCoverage, Trigger = Trigger })
            : explorer.StartAffectedRun(affected.Plan, wantCoverage, Trigger, affected.Selection, affected.Tests)!;
        if (request.Status != 202)
        {
            await Console.Error.WriteLineAsync($"{ErrorLabel} Nothing to run: {request.Error}");
            WriteExports(explorer, options, null);
            return ExitBuildError;
        }
        await request.Completion!;
        var status = explorer.GetRunStatus(request.RunId!);
        var last = status?.Run ?? explorer.Summary().LastRun;
        var run = last?.RunId == request.RunId ? last : null; // null: nothing ran (e.g. every build failed)
        broken = BrokenProjects(explorer);
        PrintResult(run, status, broken, sw.Elapsed);
        var exportsOk = WriteExports(explorer, options, run?.RunId);
        var mapOk = await BuildAndSaveImpactMapAsync(explorer, options, affected is null ? ProjectsOf(explorer, ids).ToList() : [], ct);
        return ExitCode(run, broken, exportsOk && mapOk);
    }

    /// <summary>Projects whose build or discovery failed.</summary>
    private static List<TestProject> BrokenProjects(Explorer explorer) =>
        explorer.Snapshot().Projects.Where(p => p.Status == ProjectStatus.Error).ToList();

    internal static int ExitCode(RunSummary? run, IReadOnlyCollection<TestProject> broken, bool exportsOk)
    {
        if (broken.Count > 0 || run is null) return ExitBuildError;
        if (run.Failed > 0 || run.Aborted == true || run.Cancelled == true || !exportsOk) return ExitFailed;
        return ExitPassed;
    }

    private static void PrintResult(RunSummary? run, Explorer.RunRequestStatus? status, List<TestProject> broken, TimeSpan elapsed)
    {
        Console.WriteLine();
        foreach (var p in broken)
            Console.Error.WriteLine($"{ErrorLabel} {p.Name}: {p.LastError ?? "build or discovery failed"}");
        if (run is null)
        {
            Console.Error.WriteLine($"{ErrorLabel} The run did not start{(status?.Reason is { } reason ? ": " + reason : ".")}");
            return;
        }
        var verdict = run.Failed > 0 ? Cli.Red($"{run.Failed} failed") : Cli.Bold("no failures");
        var extra = (run.Quarantined is > 0 ? $", {run.Quarantined} quarantined" : "") + (run.NotRun is > 0 ? $", {run.NotRun} not run" : "");
        Console.WriteLine($"  Tests: {verdict}, {run.Passed} passed, {run.Skipped} skipped{extra} ({run.Total} total) in {HtmlReport.Duration(elapsed.TotalMilliseconds)}");
        if (run.Aborted == true) Console.Error.WriteLine($"{WarningLabel} Run aborted: {run.AbortReason}");
        if (run.Cancelled == true) Console.Error.WriteLine($"{WarningLabel} Run cancelled.");
    }

    /// <summary>Writes every --export target; false when one could not be written.</summary>
    private static bool WriteExports(Explorer explorer, StartOptions options, string? runId, bool nothingToRun = false)
    {
        var ok = true;
        foreach (var target in options.Exports)
        {
            try { ok &= WriteExport(explorer, options, target, runId, nothingToRun); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Console.Error.WriteLine($"{ErrorLabel} Could not write {target.Path}: {ex.Message}");
                ok = false;
            }
        }
        return ok;
    }

    private static bool WriteExport(Explorer explorer, StartOptions options, ExportTarget target, string? runId, bool nothingToRun)
    {
        var path = Path.GetFullPath(target.Path);
        if (ExportFormats.CoverageFormats.Contains(target.Format))
        {
            // Nothing to run (--affected): an empty report, so the file CI uploads exists and says there was no coverage.
            var report = nothingToRun
                ? new CoverageReport { RunId = "none", GeneratedAt = Json.Now(), ProjectIds = [], Files = [], Scope = "subset", NoCoverableLines = true }
                : explorer.LastCoverageReport;
            if (report is null || (!nothingToRun && report.RunId != runId))
            {
                Console.Error.WriteLine($"{WarningLabel} No coverage was collected in this run: {target.Path} not written " +
                                        "(do the test projects reference Microsoft.Testing.Extensions.CodeCoverage?).");
                return true;
            }
            var coverageOptions = new CoverageExportOptions(explorer.ExportRoot, options.ExportAbsolutePaths);
            Write(path, s => CoverageExport.Write(target.Format, report, coverageOptions, s));
            Console.WriteLine($"  Wrote {target.Format}: {target.Path} ({report.Files.Count} files, {report.LineRate:P1} of lines)");
            Console.WriteLine(Cli.Dim("    " + CoverageExport.Note(report, coverageOptions)));
            return true;
        }
        var export = (runId is null ? null : explorer.ExportRun(runId)) ?? EmptyExport(explorer);
        Write(path, s => LabcoatApp.WriteRunExport(export, target.Format, s));
        Console.WriteLine($"  Wrote {target.Format}: {target.Path} ({export.Tests.Count} tests)");
        return true;
    }

    private static void Write(string path, Action<Stream> write)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        write(stream);
    }

    /// <summary>An export without results (nothing ran): the reports still exist and say so.</summary>
    private static RunExport EmptyExport(Explorer explorer) => new()
    {
        WorkspaceName = explorer.ExportWorkspaceName,
        Version = Cli.Version,
        Run = new RunSummary { RunId = "none", StartedAt = Json.Now(), Trigger = Trigger },
        Projects = [],
        Tests = [],
        GeneratedAt = Json.Now(),
    };

    /// <summary>
    /// The ids of the tests matching a pattern: a case-insensitive substring of the display name, "Namespace.Class",
    /// "Namespace.Class.Method" or the project id; '*' matches anything (then the whole text must match).
    /// </summary>
    internal static List<string> Matching(IEnumerable<TestNode> tests, IReadOnlyList<string> patterns)
    {
        var matchers = patterns.Select(Matcher).ToList();
        return tests.Where(t => matchers.Exists(m => Candidates(t).Any(m))).Select(t => t.Id).Distinct().ToList();
    }

    private static IEnumerable<string> Candidates(TestNode t)
    {
        yield return t.DisplayName;
        var cls = string.Join('.', new[] { t.Namespace, t.ClassName }.Where(s => !string.IsNullOrEmpty(s)));
        if (cls.Length > 0) yield return cls;
        if (t.MethodName is { } m) yield return cls.Length > 0 ? $"{cls}.{m}" : m;
        yield return t.ProjectId;
    }

    private static Func<string, bool> Matcher(string pattern)
    {
        if (!pattern.Contains('*')) return s => s.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        var parts = pattern.Split('*');
        return s => WildcardMatch(s, parts);
    }

    /// <summary>'*'-wildcard match of the whole <paramref name="s"/>; <paramref name="parts"/> = the pattern split at '*'.</summary>
    private static bool WildcardMatch(string s, string[] parts)
    {
        if (!s.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        var at = parts[0].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var found = s.IndexOf(parts[i], at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            at = found + parts[i].Length;
        }
        var last = parts[^1];
        return s.Length - at >= last.Length && s.EndsWith(last, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ctrl+C (or <paramref name="outer"/>) cancels the run: the reports of what ran are still written; a second Ctrl+C ends the process.</summary>
    private sealed class CancelOnInterrupt : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly CancellationTokenRegistration _outer;
        private int _presses;

        public CancelOnInterrupt(Explorer explorer, CancellationToken outer)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _outer = _cts.Token.Register(() => explorer.Cancel());
            Console.CancelKeyPress += OnCancel;
        }

        public CancellationToken Token => _cts.Token;

        private void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            if (Interlocked.Increment(ref _presses) > 1) return; // a second Ctrl+C: let the process end
            e.Cancel = true;
            Console.Error.WriteLine($"{WarningLabel} Cancelling the run (Ctrl+C again to stop at once)...");
            _cts.Cancel();
        }

        public void Dispose()
        {
            Console.CancelKeyPress -= OnCancel;
            _outer.Dispose();
            _cts.Dispose();
        }
    }
}
