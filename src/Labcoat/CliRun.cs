using System.Diagnostics;
using Labcoat.Export;

namespace Labcoat;

/// <summary>An <c>--export format=path</c> target of <c>--run</c>.</summary>
public sealed record ExportTarget(string Format, string Path);

/// <summary><c>--affected</c>: what the changes are compared with (<c>AffectedBases</c>) and the ref / range of "branch" and "range".</summary>
public sealed record AffectedSelector(string Base, string? Ref);

// --run, --export, --filter, --export-paths, --order, --seed, --retry-failed, --affected, --impact-map-file,
// --build-impact-map (CliRun.cs): the non-interactive mode for CI.
public sealed partial class StartOptions
{
    /// <summary>--run: build, run the tests once, write the exports and exit (no web server, no browser).</summary>
    public bool RunOnce { get; private set; }
    /// <summary>--export format=path (repeatable): junit, html, markdown, trx, sarif, lcov or cobertura.</summary>
    public List<ExportTarget> Exports { get; } = [];
    /// <summary>--filter: only the tests whose name, class or namespace matches one of these patterns.</summary>
    public List<string> Filters { get; } = [];
    /// <summary>--retry-failed: how often a failed test is rerun after the run (0 = never); a pass on a retry makes it flaky, not failed.</summary>
    public int RetryFailed { get; private set; }
    /// <summary>--export-paths absolute: absolute source paths in lcov / Cobertura (default: relative to the workspace root).</summary>
    public bool ExportAbsolutePaths { get; private set; }
    /// <summary>--min-coverage: the lowest overall line coverage (percent) of this run; below it the run fails (CoverageGate.cs).</summary>
    public double? MinCoverage { get; private set; }
    /// <summary>--min-diff-coverage: the lowest line coverage (percent) of the lines changed versus the affected base.</summary>
    public double? MinDiffCoverage { get; private set; }

    /// <summary>--shard i/n: run only this shard of the tests (see <see cref="Sharding"/>).</summary>
    public ShardSpec? Shard { get; private set; }
    /// <summary>--shard-by: what a shard is made of.</summary>
    public ShardBy ShardBy { get; private set; }
    /// <summary>--shard-timings (repeatable): JUnit XML files (or patterns) of an earlier run that balance the shards.</summary>
    public List<string> ShardTimings { get; } = [];

    /// <summary>--order random: run the tests one at a time in a seeded random order (<see cref="RandomOrder"/>).</summary>
    public string? Order { get; private set; }
    /// <summary>--seed: reproduces the order of an earlier --order random run (default: a new seed, printed).</summary>
    public int? Seed { get; private set; }

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
                return AddFilter(args.Value());
            case "--shard":
                Shard = ShardSpec.Parse(args.Value());
                return Shard is null ? "--shard needs index/count with 1 <= index <= count, e.g. 2/4." : null;
            case "--shard-by":
                return SetShardBy(args.Value());
            case "--shard-timings":
                return AddShardTimings(args.Value());
            case "--min-coverage" or "--min-diff-coverage":
                return SetMinCoverage(option, args.Value());
            case "--order":
                var (order, orderError) = RandomOrder.ParseOrder(args.Value());
                Order = order;
                return orderError is null ? null : "--" + orderError;
            case "--seed":
                return SetSeed(args.Value());
            case "--retry-failed":
                return SetRetryFailed(args.Value());
            case "--affected":
                return SetAffected(args.OptionalValue(IsAffectedValue));
            case "--impact-map-file":
                return SetImpactMapFile(args.Value());
            case "--build-impact-map":
                BuildImpactMap = true;
                return null;
            default: // --export-paths
                return SetExportPaths(args.Value());
        }
    }

    private string? AddFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return "--filter needs a pattern, e.g. CalculatorTests or *Add*.";
        Filters.AddRange(RequestGuard.SplitList(filter.Replace(' ', ',')));
        return null;
    }

    private string? SetShardBy(string? value)
    {
        ShardBy? by = value?.ToLowerInvariant() switch
        {
            "class" => ShardBy.Class, "test" => ShardBy.Test, "project" => ShardBy.Project, _ => null,
        };
        if (by is null) return "--shard-by needs class, test or project.";
        ShardBy = by.Value;
        return null;
    }

    private string? AddShardTimings(string? timings)
    {
        if (string.IsNullOrWhiteSpace(timings)) return "--shard-timings needs a JUnit XML file (a pattern like out/*/results.xml also works).";
        ShardTimings.AddRange(RequestGuard.SplitList(timings));
        return null;
    }

    /// <summary>--min-coverage or --min-diff-coverage (<paramref name="option"/>).</summary>
    private string? SetMinCoverage(string option, string? value)
    {
        var percent = CoverageGate.ParsePercent(value);
        if (percent is null) return $"{option} needs a percentage from 0 to 100, e.g. 80.";
        if (option == "--min-coverage") MinCoverage = percent;
        else MinDiffCoverage = percent;
        return null;
    }

    private string? SetSeed(string? value)
    {
        if (!int.TryParse(value, out var seed) || seed <= 0) return "--seed needs a positive number, e.g. 12345.";
        Seed = seed;
        return null;
    }

    private string? SetRetryFailed(string? retries)
    {
        if (!int.TryParse(retries?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n is < 1 or > CliRun.MaxRetries)
            return $"--retry-failed needs a number from 1 to {CliRun.MaxRetries}.";
        RetryFailed = n;
        return null;
    }

    private string? SetImpactMapFile(string? value)
    {
        var file = value?.Trim();
        if (string.IsNullOrEmpty(file)) return "--impact-map-file needs a file, e.g. impact.json (or impact.json.gz).";
        if (Directory.Exists(file)) return $"--impact-map-file: '{file}' is a directory; give a file.";
        ImpactMapFile = file;
        return null;
    }

    private string? SetExportPaths(string? mode)
    {
        if (mode is not ("relative" or "absolute")) return "--export-paths needs relative or absolute.";
        ExportAbsolutePaths = mode == "absolute";
        return null;
    }

    private string? AddExport(string? value)
    {
        var usage = $"--export needs format=file, e.g. junit=out/results.xml (formats: {string.Join(", ", AllExportFormats)}).";
        var eq = value?.IndexOf('=') ?? -1;
        if (value is null || eq <= 0 || eq == value.Length - 1) return usage;
        var format = value[..eq].Trim().ToLowerInvariant();
        if (!AllExportFormats.Contains(format)) return $"--export: unknown format '{value[..eq]}'. {usage}";
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

    /// <summary>
    /// --export / --filter / --export-paths / --shard* / --min-coverage / --min-diff-coverage / --max-test-duration /
    /// --order / --seed / --retry-failed / --affected / --impact-map-file / --build-impact-map only make sense with --run;
    /// some need or exclude each other.
    /// </summary>
    private string? ValidateRunOptions() => NeedsRunError() ?? NeedsOptionError() ?? CombinationError();

    /// <summary>The options that only make sense with --run.</summary>
    private string? NeedsRunError()
    {
        if (!RunOnce && (Exports.Count > 0 || Filters.Count > 0 || ExportAbsolutePaths || Shard is not null || ShardTimings.Count > 0
                         || MinCoverage is not null || MinDiffCoverage is not null || MaxTestDuration is not null
                         || Order is not null || Seed is not null || RetryFailed > 0))
            return "--export, --filter, --export-paths, --shard, --shard-timings, --min-coverage, --min-diff-coverage, --max-test-duration, --order, --seed and --retry-failed need --run.";
        if (!RunOnce && (Affected is not null || ImpactMapFile is not null || BuildImpactMap))
            return "--affected, --impact-map-file and --build-impact-map need --run.";
        return null;
    }

    /// <summary>The options that need another option.</summary>
    private string? NeedsOptionError()
    {
        if (Shard is null && ShardTimings.Count > 0) return "--shard-timings needs --shard.";
        if (Seed is not null && Order is null) return "--seed needs --order random.";
        if (BuildImpactMap && ImpactMapFile is null && Affected is null)
            return "--build-impact-map needs --impact-map-file (to keep the map) or --affected (to use it now).";
        if (ImpactMapFile is not null && Affected is null && !BuildImpactMap)
            return "--impact-map-file needs --affected (to use the map) or --build-impact-map (to create it).";
        return null;
    }

    /// <summary>The options that exclude each other.</summary>
    private string? CombinationError()
    {
        // Every shard job would build the map of the same projects (the pass covers whole projects, not a shard).
        if (BuildImpactMap && Shard is not null)
            return "--build-impact-map can't be combined with --shard: build the impact map in one job.";
        // The affected tests are a part of the suite: their overall coverage says nothing about the suite's.
        if (Affected is not null && MinCoverage is not null)
            return "--min-coverage can't be combined with --affected: only a part of the tests runs, so the overall coverage is not the suite's (--min-diff-coverage checks the changed lines).";
        // A retry reruns the failed tests on their own: one that fails only in this order would pass and count as flaky.
        if (Order is not null && RetryFailed > 0)
            return "--order random can't be combined with --retry-failed: a test that fails only in this order would pass on its own and be reported as flaky.";
        return Order is not null && (Coverage || MinCoverage is not null || MinDiffCoverage is not null
                                     || Exports.Exists(e => ExportFormats.CoverageFormats.Contains(e.Format)))
            ? "--order random can't be combined with --coverage, --min-coverage, --min-diff-coverage or an lcov / cobertura --export: random-order runs do not collect coverage."
            : null;
    }
}

/// <summary>
/// <c>labcoat --run</c>: the non-interactive mode for CI. Builds and discovers the test projects, runs every test (or
/// the <c>--filter</c>ed / <c>--affected</c> ones; with <c>--coverage</c>, which an lcov / Cobertura export implies),
/// writes the <c>--export</c> files and exits: <see cref="ExitPassed"/> when nothing failed (or nothing was affected),
/// <see cref="ExitFailed"/> when a test failed or took longer than <c>--max-test-duration</c> (or the run was aborted /
/// cancelled, or a <c>--min-coverage</c> / <c>--min-diff-coverage</c> threshold was missed; a failure that passed on a
/// <c>--retry-failed</c> retry does not count), <see cref="ExitBuildError"/> when a project did not build or discover
/// (or nothing could run, or git could not answer for <c>--affected</c>). No web server, no browser. Uses the same explorer, history and settings files as the UI.
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
            return await RunAndExportAsync(explorer, options, app.Limits.EffectiveMaxTestDuration, coverage, cancel.Token);
        }
    }

    private static async Task<int> RunAndExportAsync(Explorer explorer, StartOptions options, TimeSpan budget, bool coverage, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await explorer.DiscoverAsync(null);
        if (ct.IsCancellationRequested) return ExitFailed;
        if (options.Affected is not null) explorer.Affected.RefineImpactInBackground = false; // no coverage processes beside the run
        if (options.ImpactMapFile is { } mapFile) CliAffected.LoadImpactMap(explorer, mapFile, options);
        var (selection, exit) = await SelectTestsAsync(explorer, options, ct);
        if (exit is { } exitCode) return exitCode;
        // The seed is chosen here (not by the explorer) so it is printed before anything can go wrong.
        int? seed = options.Order is null ? null : options.Seed ?? RandomOrder.NewSeed();
        if (seed is not null) Console.WriteLine($"  Random order, seed {seed}: the tests run one at a time (reproduce: --order random --seed {seed})");
        var runOptions = new Explorer.RunRequestOptions { Coverage = WantsCoverage(options, coverage), Trigger = Trigger, Order = options.Order, Seed = seed };
        var request = StartRun(explorer, selection, runOptions);
        if (request.Status != 202)
        {
            await Console.Error.WriteLineAsync($"{ErrorLabel} Nothing to run: {request.Error}");
            WriteExports(explorer, options, null);
            ReportToCi(explorer, null, null);
            return ExitBuildError;
        }
        await request.Completion!;
        var status = explorer.GetRunStatus(request.RunId!);
        var last = status?.Run ?? explorer.Summary().LastRun;
        var run = last?.RunId == request.RunId ? last : null; // null: nothing ran (e.g. every build failed)
        var broken = BrokenProjects(explorer);
        RunExport? retried = null; // the first run with its retries applied (--retry-failed)
        if (options.RetryFailed > 0 && run is { Failed: > 0, Aborted: not true, Cancelled: not true } && broken.Count == 0 && explorer.Exports.Run(run.RunId) is { } first)
        {
            retried = await RetryFailedAsync(explorer, first, options.RetryFailed, ct);
            run = retried.Run;
            broken = BrokenProjects(explorer);
        }
        PrintResult(run, status, broken, sw.Elapsed, retried);
        var exportsOk = WriteExports(explorer, options, run?.RunId, retried);
        ReportToCi(explorer, run?.RunId, retried);
        // The retries run without coverage and keep the first run's id: the gates read the first run's coverage. With
        // --affected the diff coverage is of the changes the plan was made of.
        var changes = PlannedChanges(selection.Plan);
        var gatesOk = run is null || broken.Count > 0 || await CoverageGate.CheckAsync(explorer, options, run.RunId, changes, ct);
        var overBudget = run is null || budget <= TimeSpan.Zero ? [] : ReportOverBudget(explorer, run.RunId, budget, retried);
        var mapOk = await CliAffected.BuildAndSaveImpactMapAsync(explorer, options, selection.Ids, ct);
        return ExitCode(run, broken, exportsOk && mapOk, gatesOk, overBudget.Count);
    }

    /// <summary>--coverage, or a coverage gate or a coverage export that needs it.</summary>
    private static bool WantsCoverage(StartOptions options, bool coverage) =>
        coverage || options.MinCoverage is not null || options.MinDiffCoverage is not null
        || options.Exports.Exists(e => ExportFormats.CoverageFormats.Contains(e.Format));

    /// <summary>Requests the run of <paramref name="selection"/>: of its affected plan (narrowed to its ids, if any) or of its ids.</summary>
    private static Explorer.RunRequestResult StartRun(Explorer explorer, TestSelection selection, Explorer.RunRequestOptions runOptions)
    {
        if (selection.Plan is not { } plan) return explorer.RequestRun(runOptions with { TestIds = selection.Ids });
        var narrowed = selection.Ids is null ? null : CliAffected.SelectionOf(plan, selection.Ids);
        return explorer.Affected.StartRun(plan, runOptions with { Selection = narrowed })!;
    }

    /// <summary>The changes an --affected plan was made of, and the git root they are relative to; null without a plan.</summary>
    private static (GitChanges, string)? PlannedChanges(AffectedPlan? plan) =>
        plan is { Changes: { } planned, Git.Root: { } root } ? (planned, root) : null;

    /// <summary>The tests to run: null ids = all (or, with a plan, the plan's own selection).</summary>
    private sealed record TestSelection(List<string>? Ids, AffectedPlan? Plan);

    /// <summary>
    /// The tests to run: null = all, else the --filter matches, of those the ones --affected selects, and of those this
    /// --shard's part. Or the exit code (after its message) when there is nothing to run.
    /// </summary>
    private static async Task<(TestSelection Selection, int? Exit)> SelectTestsAsync(Explorer explorer, StartOptions options, CancellationToken ct)
    {
        var none = new TestSelection(null, null);
        List<string>? ids = null;
        if (options.Filters.Count > 0)
        {
            ids = Matching(explorer.Snapshot().Tests, options.Filters);
            Console.WriteLine($"  --filter {string.Join(",", options.Filters)}: {ids.Count} test{Words.Plural(ids.Count)}");
            if (ids.Count == 0)
            {
                await Console.Error.WriteLineAsync($"{ErrorLabel} No test matches --filter.");
                return (none, BrokenProjects(explorer).Count > 0 ? ExitBuildError : ExitFailed);
            }
        }
        AffectedPlan? affected = null;
        if (options.Affected is { } selector)
        {
            try
            {
                var planned = await CliAffected.PlanAffectedRunAsync(explorer, options, selector, ids, ct);
                if (planned.Tests == 0) return (none, NothingAffected(explorer, options));
                (affected, ids) = (planned.Plan, planned.Ids);
            }
            catch (AffectedException ex)
            {
                await Console.Error.WriteLineAsync($"{ErrorLabel} --affected: {ex.Message}");
                WriteExports(explorer, options, null, nothingToRun: true);
                ReportToCi(explorer, null, null);
                return (none, ExitBuildError);
            }
        }
        if (options.Shard is not { } shard) return (new TestSelection(ids, affected), null);
        // The affected tests are sharded, not all of them: every shard job makes the same plan.
        var plan = PlanShard(explorer, options, shard, ids ?? affected?.Tests.Select(t => t.Id).ToList());
        if (plan is null) return (none, ExitBuildError);
        if (plan.TestIds.Count > 0) return (new TestSelection(plan.TestIds, affected), null);
        Console.WriteLine("  This shard has no tests: nothing to run.");
        WriteExports(explorer, options, null);
        ReportToCi(explorer, null, null);
        return (none, BrokenProjects(explorer).Count > 0 ? ExitBuildError : ExitPassed);
    }

    /// <summary>--affected found nothing to run: say so, write the exports (empty reports) and exit 0 (a broken project still exits 2).</summary>
    private static int NothingAffected(Explorer explorer, StartOptions options)
    {
        var broken = BrokenProjects(explorer);
        Console.WriteLine();
        Console.WriteLine($"  {Cli.Bold("No affected tests")}: nothing to run.");
        foreach (var p in broken) Console.Error.WriteLine($"{ErrorLabel} {p.Name}: {p.LastError ?? "build or discovery failed"}");
        var exportsOk = WriteExports(explorer, options, null, nothingToRun: true);
        ReportToCi(explorer, null, null);
        if (broken.Count > 0) return ExitBuildError;
        return exportsOk ? ExitPassed : ExitFailed;
    }

    /// <summary>This shard's tests of those that would run; null (after an error message) when the timings cannot be read.</summary>
    private static ShardPlan? PlanShard(Explorer explorer, StartOptions options, ShardSpec shard, List<string>? ids)
    {
        var snapshot = explorer.Snapshot();
        var names = snapshot.Projects.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var selected = ids is null ? snapshot.Tests : snapshot.Tests.Where(t => ids.Contains(t.Id)).ToList();
        ShardTimings? timings = null;
        if (options.ShardTimings.Count > 0)
        {
            try
            {
                var files = Labcoat.ShardTimings.Resolve(options.ShardTimings);
                if (files.Count == 0) Console.Error.WriteLine($"{WarningLabel} --shard-timings matched no file: the tests are dealt out without timings.");
                else timings = Labcoat.ShardTimings.Load(files);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{ErrorLabel} {ex.Message}");
                return null;
            }
        }
        var plan = Sharding.Plan(selected, id => names.GetValueOrDefault(id, id), shard, options.ShardBy, timings);
        var unit = options.ShardBy.ToString().ToLowerInvariant();
        var estimate = plan.EstimatedMs is { } ms ? $", about {Export.HtmlReport.Duration(ms)}" : "";
        Console.WriteLine($"  --shard {shard}: {plan.TestIds.Count} of {selected.Count} tests ({plan.Units} of {plan.TotalUnits} {unit}es{estimate}), split {plan.Fingerprint}");
        if (timings is null)
            Console.WriteLine(Cli.Dim("    No timings: the shards are dealt out evenly by name. --shard-timings <results.xml> balances them by duration."));
        return plan;
    }

    /// <summary>Projects whose build or discovery failed.</summary>
    private static List<TestProject> BrokenProjects(Explorer explorer) =>
        explorer.Snapshot().Projects.Where(p => p.Status == ProjectStatus.Error).ToList();

    /// <summary>Lists the tests of the run that took longer than the <c>maxTestDuration</c> budget (they fail the run).</summary>
    /// <param name="retried">The run with its --retry-failed retries applied: a flaky test counts with its passing attempt, as in the exports.</param>
    private static List<OverBudgetTest> ReportOverBudget(Explorer explorer, string runId, TimeSpan budget, RunExport? retried)
    {
        var over = (retried ?? explorer.Exports.Run(runId)) is { } export ? DurationBudget.OverBudget(export, budget) : [];
        if (over.Count == 0) return over;
        Console.Error.WriteLine($"{ErrorLabel} {over.Count} test{Words.Plural(over.Count)} took longer than maxTestDuration ({RunLimits.Describe(budget)}):");
        foreach (var t in over)
            Console.Error.WriteLine($"  {HtmlReport.Duration(t.DurationMs)}  {(t.ClassName is { Length: > 0 } c ? c + "." : "")}{t.Name} [{t.ProjectId}]" +
                                    (t.Outcome is Outcome.Passed ? "" : Cli.Dim($" ({t.Outcome})")));
        return over;
    }

    internal static int ExitCode(RunSummary? run, IReadOnlyCollection<TestProject> broken, bool exportsOk, bool gatesOk = true, int overBudget = 0)
    {
        if (broken.Count > 0 || run is null) return ExitBuildError;
        if (run.Failed > 0 || run.Aborted == true || run.Cancelled == true || !exportsOk || !gatesOk || overBudget > 0) return ExitFailed;
        return ExitPassed;
    }

    private static void PrintResult(RunSummary? run, Explorer.RunRequestStatus? status, List<TestProject> broken, TimeSpan elapsed, RunExport? retried = null)
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
        var extra = (retried is { Flaky: > 0 } ? $", {retried.Flaky} flaky" : "") + (run.Quarantined is > 0 ? $", {run.Quarantined} quarantined" : "") + (run.NotRun is > 0 ? $", {run.NotRun} not run" : "");
        Console.WriteLine($"  Tests: {verdict}, {run.Passed} passed, {run.Skipped} skipped{extra} ({run.Total} total) in {Export.HtmlReport.Duration(elapsed.TotalMilliseconds)}");
        if (retried is not null) PrintRetried(retried);
        if (run.Seed is { } seed)
            Console.WriteLine($"  Random order, seed {seed}: " + (run.Failed > 0 ? "reproduce this order" : "passed in this order; if a later run fails, reproduce it") + $" with --order random --seed {seed}");
        if (run.Aborted == true) Console.Error.WriteLine($"{WarningLabel} Run aborted: {run.AbortReason}");
        if (run.Cancelled == true) Console.Error.WriteLine($"{WarningLabel} Run cancelled.");
    }

    /// <summary>
    /// GitHub Actions: the step summary (when <c>$GITHUB_STEP_SUMMARY</c> is set) and the failed tests as annotations
    /// (when <c>GITHUB_ACTIONS=true</c>), of the run with its --retry-failed retries applied like the exports. Nothing
    /// here fails the run.
    /// </summary>
    private static void ReportToCi(Explorer explorer, string? runId, RunExport? retried)
    {
        var summaryPath = GitHubActions.StepSummaryPath(Environment.GetEnvironmentVariable);
        var annotate = GitHubActions.IsActive(Environment.GetEnvironmentVariable);
        if (summaryPath is null && !annotate) return;
        var export = retried ?? (runId is null ? null : explorer.Exports.Run(runId)) ?? EmptyExport(explorer);
        if (annotate)
            foreach (var line in GitHubActions.Annotations(export)) Console.WriteLine(line);
        if (summaryPath is null) return;
        var coverage = explorer.LastCoverageReport is { } report && report.RunId == runId ? report : null;
        try
        {
            if (GitHubActions.AppendStepSummary(summaryPath, export, coverage)) Console.WriteLine($"  Appended the summary to {Cli.Dim("$GITHUB_STEP_SUMMARY")}");
            else Console.Error.WriteLine($"{WarningLabel} No room left in $GITHUB_STEP_SUMMARY: summary not written.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"{WarningLabel} Could not write $GITHUB_STEP_SUMMARY: {ex.Message}");
        }
    }

    /// <summary>Writes every --export target; false when one could not be written.</summary>
    private static bool WriteExports(Explorer explorer, StartOptions options, string? runId, RunExport? retried = null, bool nothingToRun = false)
    {
        var ok = true;
        foreach (var target in options.Exports)
        {
            try { ok &= WriteExport(explorer, options, target, runId, retried, nothingToRun); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Console.Error.WriteLine($"{ErrorLabel} Could not write {target.Path}: {ex.Message}");
                ok = false;
            }
        }
        return ok;
    }

    private static bool WriteExport(Explorer explorer, StartOptions options, ExportTarget target, string? runId, RunExport? retried, bool nothingToRun)
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
            var coverageOptions = new CoverageExportOptions(explorer.Exports.Root, options.ExportAbsolutePaths);
            Write(path, s => CoverageExport.Write(target.Format, report, coverageOptions, s));
            Console.WriteLine($"  Wrote {target.Format}: {target.Path} ({report.Files.Count} files, {report.LineRate:P1} of lines)");
            Console.WriteLine(Cli.Dim("    " + CoverageExport.Note(report, coverageOptions)));
            return true;
        }
        var export = retried ?? (runId is null ? null : explorer.Exports.Run(runId)) ?? EmptyExport(explorer);
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
        WorkspaceName = explorer.Exports.WorkspaceName,
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
    internal static bool WildcardMatch(string s, string[] parts)
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

    /// <summary>Ctrl+C (or the caller's token) cancels the run: the reports of what ran are still written; a second Ctrl+C ends the process.</summary>
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
