namespace Labcoat;

// Slow / hanging tests and timeouts (docs/features.md "Slow and hanging tests"): which tests run now and for how long
// (runningNow, RunningTests.cs), "possibly hanging" after HangThreshold, the optional per-test timeout (testTimeout) and
// the session timeout of a project's TestingPlatformCommandLineArguments (--timeout), which cancel that project's run.
internal sealed class HangMonitor(WorkspaceState state, IExplorerEffects effects, LiveOutputService live,
    TimeSpan hangThreshold, TimeSpan testTimeout, TimeSpan checkInterval) : IDisposable
{
    private RunningTests? _runningTests;
    private Timer? _hangTimer;
    private int _disposed;
    /// <summary>The projects running now (lock Sync), by project id: their cancellation and their part of the run.</summary>
    private readonly Dictionary<string, ProjectRunScope> _projectRuns = [];

    private object Sync => state.Sync;

    private RunningTests Running => LazyInitializer.EnsureInitialized(ref _runningTests,
        () => new RunningTests { HangThreshold = hangThreshold, TestTimeout = testTimeout });

    /// <summary>One project's part of a run that is in progress: its own cancellation (linked to the run's).</summary>
    public sealed class ProjectRunScope(HangMonitor owner, ProjectRun run, CancellationTokenSource cts, TimeSpan? sessionTimeout, string? sessionTimeoutText)
        : IDisposable
    {
        public ProjectRun Run { get; } = run;
        public CancellationToken Token { get; } = cts.Token;
        public DateTime Started { get; } = DateTime.UtcNow;
        public TimeSpan? SessionTimeout { get; } = sessionTimeout;
        public string? SessionTimeoutText { get; } = sessionTimeoutText;
        /// <summary>Set when a timeout cancelled the project's run (lock Sync): the abort reason of the run.</summary>
        public string? TimeoutReason { get; set; }

        public void Cancel()
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* the project's run ended meanwhile */ }
        }

        public void Dispose()
        {
            lock (owner.Sync)
            {
                if (owner._projectRuns.TryGetValue(Run.Project.Project.Id, out var current) && current == this)
                    owner._projectRuns.Remove(Run.Project.Project.Id);
            }
            cts.Dispose();
        }
    }

    public ProjectRunScope BeginProjectRun(ProjectRun run, TimeSpan? sessionTimeout, string? sessionTimeoutText, CancellationToken ct)
    {
        var scope = new ProjectRunScope(this, run, CancellationTokenSource.CreateLinkedTokenSource(ct), sessionTimeout, sessionTimeoutText);
        lock (state.Sync)
        {
            _projectRuns[run.Project.Project.Id] = scope;
            if (scope.SessionTimeout is not null) EnsureTimerLocked();
        }
        return scope;
    }

    // ------------------------------------------------------------------ test lifecycle

    public void Started(TestNode t)
    {
        Running.Started(t.ProjectId, t.Id, t.DisplayName);
        EnsureTimerLocked();
    }

    public void Finished(TestNode t) => _runningTests?.Finished(t.ProjectId, t.Id);

    /// <summary>The run ended (caller holds Sync): nothing runs, the timer stops, clients drop their list.</summary>
    public void Reset()
    {
        _hangTimer?.Dispose();
        _hangTimer = null;
        if (_runningTests is null) return;
        _runningTests.Reset();
        _runningTests.Changed(); // the next run's first list is a change again
        effects.Emit(new RunningNowEvent(null));
    }

    /// <summary>A late result of a test that timed out (the host reporting it cancelled) keeps "timed out" (caller holds Sync).</summary>
    public bool IsTimedOutLocked((string ProjectId, string Id) key) => _runningTests?.IsTimedOut(key) == true;

    /// <summary>ExplorerState.runningNow (caller holds Sync).</summary>
    public RunningNowInfo? RunningNowLocked() => state.Run.IsRunning && _runningTests is { Count: > 0 } r ? r.Info() : null;

    private void EnsureTimerLocked()
    {
        if (_hangTimer is not null || _disposed != 0) return;
        _hangTimer = new Timer(_ => Tick(), null, checkInterval, checkInterval);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        lock (state.Sync) _hangTimer?.Dispose();
    }

    /// <summary>
    /// Once a second during a run: publish the running tests when they changed, log tests that became slow, and time out
    /// tests over testTimeout / projects over their session timeout (cancelling that project's run).
    /// </summary>
    internal void Tick()
    {
        if (_disposed != 0) return;
        try { CheckRunningTests(); }
        catch (Exception ex) { effects.Log(null, LogLevels.Warning, "Checking the running tests failed: " + ex.Message); }
    }

    private void CheckRunningTests()
    {
        var cancel = new List<ProjectRunScope>();
        var slowLines = new List<string>();
        lock (state.Sync)
        {
            var running = Running; // also when no test started yet (a session timeout)
            var (slow, timedOut) = running.Check();
            foreach (var (projectId, _, name, elapsed) in slow)
                slowLines.Add($"[{projectId}] {name} has been running for {RunLimits.Describe(elapsed)}: possibly hanging" +
                    (testTimeout > TimeSpan.Zero ? $" (it times out after {RunLimits.Describe(testTimeout)})" : " (Stop cancels the run; a testTimeout stops such tests automatically)"));
            foreach (var (projectId, id, name, elapsed) in timedOut)
            {
                // a run that is being cancelled already (Stop) is not timed out on top of that
                if (!_projectRuns.TryGetValue(projectId, out var scope) || scope.Token.IsCancellationRequested) continue;
                var message = $"Timed out after {RunLimits.Describe(elapsed)}: the test ran longer than the test timeout ({RunLimits.Describe(testTimeout)}, testTimeout). " +
                    $"Labcoat cancelled the run of {scope.Run.Project.Project.Name}; a test host that does not stop within {RunLimits.Describe(TestHostPool.CancelGrace)} is killed.";
                MarkTimedOutLocked(scope.Run, (projectId, id), elapsed, message);
                scope.TimeoutReason ??= $"{name} timed out after {RunLimits.Describe(elapsed)} (testTimeout); the rest of {scope.Run.Project.Project.Name}'s run was cancelled.";
                if (!cancel.Contains(scope)) cancel.Add(scope);
            }
            foreach (var scope in _projectRuns.Values.Where(s => s.TimeoutReason is null && !s.Token.IsCancellationRequested && s.SessionTimeout is { } st && DateTime.UtcNow - s.Started >= st))
            {
                TimeOutSessionLocked(scope);
                cancel.Add(scope);
            }
            if (state.Run.IsRunning && running.Changed() is { } info) effects.Emit(new RunningNowEvent(info));
        }
        foreach (var line in slowLines) effects.Log(null, LogLevels.Warning, line);
        foreach (var scope in cancel)
        {
            effects.Log(scope.Run.Project.Project.Id, LogLevels.Warning, scope.TimeoutReason!);
            scope.Cancel();
        }
    }

    /// <summary>The project's run went over the --timeout of its TestingPlatformCommandLineArguments: the tests running now time out.</summary>
    private void TimeOutSessionLocked(ProjectRunScope scope)
    {
        var p = scope.Run.Project;
        var limit = RunLimits.Describe(scope.SessionTimeout!.Value);
        var message = $"Timed out: the run of {p.Project.Name} took longer than its --timeout {scope.SessionTimeoutText} ({limit}, from " +
            "TestingPlatformCommandLineArguments). Labcoat cancelled the project's run while this test was running.";
        var runningNow = _runningTests?.OfProject(p.Project.Id) ?? [];
        foreach (var (id, elapsed) in runningNow) MarkTimedOutLocked(scope.Run, (p.Project.Id, id), elapsed, message);
        var tests = runningNow.Count == 1 ? "test" : "tests";
        scope.TimeoutReason = $"The run of {p.Project.Name} exceeded its --timeout {scope.SessionTimeoutText} ({limit}) and was cancelled" +
            (runningNow.Count > 0 ? $" while {runningNow.Count} {tests} ran." : ".");
    }

    /// <summary>Marks a running test as timed out (caller holds Sync).</summary>
    private void MarkTimedOutLocked(ProjectRun pr, (string ProjectId, string Id) key, TimeSpan elapsed, string message)
    {
        _runningTests?.MarkTimedOut(key.ProjectId, key.Id);
        if (!state.Tests.TryGetValue(key, out var t) || t.Outcome != Outcome.Running) return;
        live.Finished(t);
        Finished(t);
        t.ClearResult();
        t.Outcome = Outcome.TimedOut;
        t.ErrorType = "Timeout";
        t.ErrorMessage = message;
        t.DurationMs = Math.Round(elapsed.TotalMilliseconds, 1);
        t.LastRunAt = Json.Now();
        t.Stale = null;
        pr.Results[key] = Outcome.TimedOut;
        RunCounts.Update(pr.Run, pr.Results, pr.Quarantined);
        effects.PublishTests([t]);
    }
}
