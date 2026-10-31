namespace Labcoat;

// The running tests: live console output (LiveOutputService.cs) and slow / hanging tests and timeouts (HangMonitor.cs),
// and the options a project's TestingPlatformCommandLineArguments add to its test host.
public sealed partial class Explorer
{
    /// <summary>A running test is shown as possibly hanging after this long (hangThreshold; zero = never).</summary>
    public TimeSpan HangThreshold { get; init; } = RunLimits.DefaultHangThreshold;

    /// <summary>A running test times out after this long and its project's run is cancelled (testTimeout; zero = off, the default).</summary>
    public TimeSpan TestTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>How often the running tests are checked (tests: shorter).</summary>
    internal TimeSpan HangCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    private LiveOutputService? _liveOutput;
    private HangMonitor? _hang;

    private LiveOutputService LiveService => LazyInitializer.EnsureInitialized(ref _liveOutput, () => new LiveOutputService(_state, this));
    private HangMonitor Hang => LazyInitializer.EnsureInitialized(ref _hang, () => new HangMonitor(_state, this, LiveService, HangThreshold, TestTimeout, HangCheckInterval));

    void IExplorerEffects.Emit(ServerEvent evt) => Emit(evt);

    // ------------------------------------------------------------------ test lifecycle (caller holds Sync)

    private void LiveStarted(TestNode t)
    {
        LiveService.Started(t);
        Hang.Started(t);
    }

    private void LivePartial(TestNode t, TestNode incoming) => LiveService.Partial(t, incoming);

    private void LiveFinished(TestNode t)
    {
        if (t.Outcome != Outcome.Running) return;
        LiveService.Finished(t);
        Hang.Finished(t);
    }

    private void LiveReset()
    {
        LiveService.Reset();
        Hang.Reset();
    }

    /// <summary>GET /api/tests/live-output: the output streamed so far for a running test; null when it is not running.</summary>
    public LiveOutputSnapshot? GetLiveOutput(string? projectId, string id) => LiveService.Get(projectId, id);

    private HangMonitor.ProjectRunScope BeginProjectRun(ProjectRun run, CancellationToken ct)
    {
        var args = HostArgumentsOf(run.Project);
        return Hang.BeginProjectRun(run, args.SessionTimeout, args.SessionTimeoutText, ct);
    }

    /// <summary>ExplorerState.limits.</summary>
    private RunLimitsInfo LimitsInfo() => new()
    {
        MaxParallelProjects = MaxParallelProjects,
        HangThresholdMs = HangThreshold > TimeSpan.Zero ? HangThreshold.TotalMilliseconds : null,
        TestTimeoutMs = TestTimeout > TimeSpan.Zero ? TestTimeout.TotalMilliseconds : null,
    };

    // ------------------------------------------------------------------ TestingPlatformCommandLineArguments

    /// <summary>The project's TestingPlatformCommandLineArguments sorted for server mode; logged when they change.</summary>
    private TestingPlatformArguments HostArgumentsOf(ProjectEntry p)
    {
        string? raw;
        lock (Sync)
        {
            raw = p.TestingPlatformArguments;
            if (p.HostArguments is { } cached && p.HostArgumentsFrom == raw) return cached;
        }
        var parsed = raw is null ? TestingPlatformArguments.Empty : TestingPlatformArguments.Parse(raw, Path.GetDirectoryName(p.Project.Path));
        lock (Sync)
        {
            p.HostArguments = parsed;
            p.HostArgumentsFrom = raw;
        }
        if (parsed.Describe() is { } line) Log(p.Project.Id, LogLevels.Info, line);
        return parsed;
    }

    /// <summary>The test host arguments plus the project's own options from TestingPlatformCommandLineArguments.</summary>
    private string[] WithProjectArguments(ProjectEntry p, string[] extraArgs)
    {
        var own = HostArgumentsOf(p).Passed;
        return own.Count == 0 ? extraArgs : [.. extraArgs, .. own];
    }
}
