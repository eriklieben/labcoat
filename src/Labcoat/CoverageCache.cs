namespace Labcoat;

/// <summary>
/// In-memory cache of per-test coverage (contract "Per-test coverage" → "Cached per-test coverage").
///
/// Every coverage run with explicit test ids (scope "subset") stores one report per project, keyed by
/// (projectId, sorted test ids), together with the output fingerprint of the test app at that time (the same content
/// fingerprint the warm host uses to detect rebuilds). <c>GET /api/coverage/test</c> looks reports up without running
/// anything; a report whose fingerprint differs from the current output is returned with <c>stale: true</c>.
/// Single-test reports are additionally indexed by (projectId, testId), so selecting a test finds its own report even
/// when it is looked up with one id. Bounded (LRU); lost on restart.
/// Not thread safe: callers lock (Explorer.Sync).
/// </summary>
public sealed class CoverageCache
{
    private const int MaxEntries = 256;

    private sealed record Entry(CoverageReport Report, string Stamp)
    {
        public long Touched { get; set; }
    }

    private readonly Dictionary<string, Entry> _bySet = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ProjectId, string TestId), string> _single = [];
    private long _clock;

    public static string Key(string projectId, IEnumerable<string> testIds) =>
        projectId + "\u001f" + string.Join("\u001e", testIds.Distinct().Order(StringComparer.Ordinal));

    public void Store(string projectId, CoverageReport report, string stamp)
    {
        if (report.TestIds is not { Count: > 0 } ids) return;
        var key = Key(projectId, ids);
        _bySet[key] = new Entry(report, stamp) { Touched = ++_clock };
        if (ids.Count == 1) _single[(projectId, ids[0])] = key;
        while (_bySet.Count > MaxEntries)
        {
            var oldest = _bySet.MinBy(kv => kv.Value.Touched).Key;
            _bySet.Remove(oldest);
            foreach (var k in _single.Where(kv => kv.Value == oldest).Select(kv => kv.Key).ToList()) _single.Remove(k);
        }
    }

    /// <summary>Every cached single-test report: (projectId, testId, report, build stamp).</summary>
    public IEnumerable<(string ProjectId, string TestId, CoverageReport Report, string Stamp)> SingleTestReports()
    {
        foreach (var ((projectId, testId), key) in _single)
            if (_bySet.TryGetValue(key, out var e)) yield return (projectId, testId, e.Report, e.Stamp);
    }

    /// <summary>The cached report for exactly <paramref name="testIds"/> of <paramref name="projectId"/>, plus its build stamp.</summary>
    public (CoverageReport Report, string Stamp)? Find(string projectId, IReadOnlyCollection<string> testIds)
    {
        string? key = testIds.Count == 1 ? _single.GetValueOrDefault((projectId, testIds.First())) : Key(projectId, testIds);
        if (key is null || !_bySet.TryGetValue(key, out var e)) return null;
        e.Touched = ++_clock;
        return (e.Report, e.Stamp);
    }
}

public sealed partial class Explorer
{
    private readonly CoverageCache _coverageCache = new();

    /// <summary>
    /// Hook from RunAsync, per project, while the Cobertura file still exists: caches the coverage of an explicit
    /// test-id run of that project (not run-all, not the class-granular impact-map pass).
    /// </summary>
    private void CacheTestCoverage(ProjectEntry p, IReadOnlyCollection<string>? ids, string coverageFile, string runId, CoveragePlan plan)
    {
        if (ids is not { Count: > 0 } || p.TargetPath is null) return;
        try
        {
            var acc = new CoverageAccumulator();
            acc.AddCobertura(coverageFile, plan.Filter);
            List<string> ordered;
            lock (Sync) ordered = _order.Where(k => k.ProjectId == p.Project.Id && ids.Contains(k.Id)).Select(k => k.Id).ToList();
            if (ordered.Count == 0) ordered = [.. ids];
            var report = acc.Build(runId, [p.Project.Id], ordered) with { Settings = [plan.Info] };
            var stamp = CodeStampOf(p);
            lock (Sync) _coverageCache.Store(p.Project.Id, report, stamp);
        }
        catch (Exception ex)
        {
            Log(p.Project.Id, LogLevels.Warning, "Per-test coverage cache: could not read coverage: " + ex.Message);
        }
    }

    /// <summary>
    /// Cached coverage of exactly <paramref name="testIds"/> (GET /api/coverage/test), with <c>Stale = true</c> when the
    /// test app's output changed since it was collected. Null when nothing is cached. Never starts a run.
    /// </summary>
    public CoverageReport? GetCachedTestCoverage(string? projectId, IReadOnlyCollection<string> testIds)
    {
        if (testIds.Count == 0) return null;
        (CoverageReport Report, string Stamp)? hit = null;
        ProjectEntry? project = null;
        lock (Sync)
        {
            foreach (var p in SelectProjects(projectId))
            {
                hit = _coverageCache.Find(p.Project.Id, testIds);
                if (hit is not null) { project = p; break; }
            }
        }
        if (hit is not { } h) return null;
        var stale = project?.TargetPath is not { } targetPath || !File.Exists(targetPath) || CodeStampOf(project) != h.Stamp;
        return stale ? h.Report with { Stale = true } : h.Report;
    }
}
