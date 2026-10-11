namespace Labcoat;

/// <summary>
/// The tests running right now and since when (from their first in-progress update), for the run status's "running now"
/// list (<see cref="RunningNowEvent"/>), the "possibly hanging" mark after <see cref="HangThreshold"/> and the optional
/// per-test timeout. Not thread safe: the explorer calls it under its lock.
/// </summary>
public sealed class RunningTests(Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<(string ProjectId, string Id), Entry> _running = [];
    /// <summary>Tests timed out in this run: a late result (the host reporting it cancelled) does not replace "timed out".</summary>
    private readonly HashSet<(string ProjectId, string Id)> _timedOut = [];
    private string? _lastSignature;

    private sealed class Entry(string displayName, DateTime started)
    {
        public string DisplayName { get; } = displayName;
        public DateTime Started { get; } = started;
        public bool ReportedSlow;
    }

    /// <summary>A running test counts as slow ("possibly hanging") after this long. Zero = never.</summary>
    public TimeSpan HangThreshold { get; init; } = RunLimits.DefaultHangThreshold;
    /// <summary>A running test times out after this long. Zero = no timeout (the default).</summary>
    public TimeSpan TestTimeout { get; init; } = TimeSpan.Zero;
    /// <summary>Most tests listed in a <see cref="RunningNowInfo"/> (the longest-running ones).</summary>
    public int MaxListed { get; init; } = 10;

    public int Count => _running.Count;

    public void Started(string projectId, string id, string displayName) => _running[(projectId, id)] = new Entry(displayName, _clock());

    public void Finished(string projectId, string id) => _running.Remove((projectId, id));

    /// <summary>The end of a run: nothing runs any more.</summary>
    public void Reset()
    {
        _running.Clear();
        _timedOut.Clear();
    }

    public bool IsTimedOut((string ProjectId, string Id) key) => _timedOut.Contains(key);

    /// <summary>How long the test has been running; null when it is not running.</summary>
    public TimeSpan? Elapsed(string projectId, string id) =>
        _running.TryGetValue((projectId, id), out var e) ? _clock() - e.Started : null;

    /// <summary>One check (about once a second): the tests that just became slow, and the ones over the timeout (now marked timed out).</summary>
    public (List<(string ProjectId, string Id, string DisplayName, TimeSpan Elapsed)> NewlySlow,
        List<(string ProjectId, string Id, string DisplayName, TimeSpan Elapsed)> TimedOut) Check()
    {
        var now = _clock();
        var slow = new List<(string, string, string, TimeSpan)>();
        var timedOut = new List<(string, string, string, TimeSpan)>();
        foreach (var ((projectId, id), e) in _running)
        {
            var elapsed = now - e.Started;
            if (TestTimeout > TimeSpan.Zero && elapsed >= TestTimeout && _timedOut.Add((projectId, id)))
                timedOut.Add((projectId, id, e.DisplayName, elapsed));
            else if (HangThreshold > TimeSpan.Zero && elapsed >= HangThreshold && !e.ReportedSlow)
            {
                e.ReportedSlow = true;
                slow.Add((projectId, id, e.DisplayName, elapsed));
            }
        }
        foreach (var (projectId, id, _, _) in timedOut) _running.Remove((projectId, id));
        return (slow, timedOut);
    }

    /// <summary>Marks a test as timed out by another rule (the project's session timeout).</summary>
    public void MarkTimedOut(string projectId, string id)
    {
        _timedOut.Add((projectId, id));
        _running.Remove((projectId, id));
    }

    /// <summary>The running tests of a project.</summary>
    public List<(string Id, TimeSpan Elapsed)> OfProject(string projectId)
    {
        var now = _clock();
        return _running.Where(kv => kv.Key.ProjectId == projectId).Select(kv => (kv.Key.Id, now - kv.Value.Started)).ToList();
    }

    /// <summary>The longest-running tests (at most <see cref="MaxListed"/>), the total and the thresholds.</summary>
    public RunningNowInfo Info()
    {
        var now = _clock();
        var tests = _running
            .OrderBy(kv => kv.Value.Started).ThenBy(kv => kv.Key.ProjectId, StringComparer.Ordinal).ThenBy(kv => kv.Key.Id, StringComparer.Ordinal)
            .Take(MaxListed)
            .Select(kv =>
            {
                var elapsed = now - kv.Value.Started;
                return new RunningTestInfo
                {
                    ProjectId = kv.Key.ProjectId,
                    Id = kv.Key.Id,
                    DisplayName = kv.Value.DisplayName,
                    ElapsedMs = Math.Round(elapsed.TotalMilliseconds),
                    Slow = HangThreshold > TimeSpan.Zero && elapsed >= HangThreshold ? true : null,
                };
            })
            .ToList();
        return new RunningNowInfo
        {
            Count = _running.Count,
            Tests = tests,
            HangThresholdMs = HangThreshold > TimeSpan.Zero ? HangThreshold.TotalMilliseconds : null,
            TestTimeoutMs = TestTimeout > TimeSpan.Zero ? TestTimeout.TotalMilliseconds : null,
        };
    }

    /// <summary>
    /// The info when what it lists changed since the last call that returned one (other tests, a test became slow), else
    /// null: elapsed times alone don't count, the client advances them itself.
    /// </summary>
    public RunningNowInfo? Changed()
    {
        var info = Info();
        var signature = info.Count + "|" + string.Join(",", info.Tests.Select(t => $"{t.ProjectId}/{t.Id}/{t.Slow}"));
        if (signature == _lastSignature) return null;
        _lastSignature = signature;
        return info;
    }
}

/// <summary>ExplorerState.runningNow / the <c>runningNow</c> event: what runs now and for how long.</summary>
public sealed record RunningNowInfo
{
    /// <summary>Number of tests running now (may be more than listed).</summary>
    public int Count { get; init; }
    /// <summary>The longest-running tests, longest first (at most 10).</summary>
    public List<RunningTestInfo> Tests { get; init; } = [];
    /// <summary>A test running this long is shown as possibly hanging; null = never.</summary>
    public double? HangThresholdMs { get; init; }
    /// <summary>The per-test timeout (testTimeout); null = none.</summary>
    public double? TestTimeoutMs { get; init; }
}

/// <summary>One running test: elapsed time when the event was sent (the client advances it).</summary>
public sealed record RunningTestInfo
{
    public required string ProjectId { get; init; }
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public double ElapsedMs { get; init; }
    /// <summary>Running longer than the hang threshold ("possibly hanging").</summary>
    public bool? Slow { get; init; }
}

/// <summary>ExplorerState.limits: the run limits in effect.</summary>
public sealed record RunLimitsInfo
{
    public int MaxParallelProjects { get; init; }
    public double? HangThresholdMs { get; init; }
    public double? TestTimeoutMs { get; init; }
}

/// <summary>The running tests changed (sent at most about once a second during a run; null tests = the run ended).</summary>
public sealed record RunningNowEvent(RunningNowInfo? RunningNow) : ServerEvent;
