namespace Labcoat;

// Test seams: state that normally only a build + discovery / coverage run produces, set directly (no test host).
public sealed partial class Explorer
{
    /// <summary>Tests only: adds (or replaces) tests as if a discovery had found them, in the given order.</summary>
    internal void SeedTests(IEnumerable<TestNode> nodes)
    {
        lock (Sync)
        {
            foreach (var n in nodes)
            {
                var key = (n.ProjectId, n.Id);
                if (!_tests.ContainsKey(key)) _order.Add(key);
                _tests[key] = n;
            }
        }
    }

    /// <summary>Tests only: the output assembly of a project (as the evaluation after a build would set it).</summary>
    internal void SetTargetPath(string projectId, string? targetPath)
    {
        lock (Sync) _projects.First(p => p.Project.Id == projectId).TargetPath = targetPath;
    }

    /// <summary>Tests only: the current coverage report (as a coverage run would set it).</summary>
    internal void SetCoverageReport(CoverageReport? report)
    {
        lock (Sync) _coverage = report;
    }

    /// <summary>Tests only: the per-test coverage cache and the impact map (callers lock <see cref="Sync"/>).</summary>
    internal CoverageCache CoverageCacheState => _coverageCache;
    /// <summary>Tests only: the full coverage report of the Coverage page (thread safe on its own).</summary>
    internal FullCoverage FullCoverageState => _fullCoverage;
    internal ImpactMap ImpactMapState => _impact;
    /// <summary>Tests only: the persisted history (thread safe on its own).</summary>
    internal HistoryStore HistoryState => _history;

    /// <summary>
    /// Tests only: replaces the <c>dotnet</c> child processes of this explorer (build, MSBuild evaluation, the
    /// <c>--help</c> coverage probe) with a fake; null = real processes.
    /// </summary>
    internal Func<string[], string, CancellationToken, Task<(int ExitCode, string Output)>>? DotnetOverride { get; set; }

    private Task<(int ExitCode, string Output)> RunDotnetAsync(string[] args, string cwd, CancellationToken ct) =>
        DotnetOverride is { } fake ? fake(args, cwd, ct) : DotnetAsync(args, cwd, ct);

    /// <summary>Tests only: holds the operation lock (as a running build / run would) until the result is disposed.</summary>
    internal async Task<IDisposable> HoldOperationAsync(string name)
    {
        await AcquireOpAsync(name, CancellationToken.None);
        return new OperationHold(ReleaseOp);
    }

    private sealed class OperationHold(Action release) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) release();
        }
    }
}
