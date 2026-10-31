namespace Labcoat;

// Live console output of running tests (LiveOutput.cs; docs/contract.md "Live console output"): testOutput events, the
// test host options that make frameworks stream (xUnit --show-live-output, TUnit IDE streaming), GET /api/tests/live-output.
internal sealed class LiveOutputService(WorkspaceState state, IExplorerEffects effects) : IDisposable
{
    private LiveOutput? _liveOutput;

    private LiveOutput Live => LazyInitializer.EnsureInitialized(ref _liveOutput, () => new LiveOutput(Flush));

    /// <summary>Flush timer: publish the buffered output under the lock, so it never overtakes the test's final result.</summary>
    private void Flush()
    {
        lock (state.Sync)
        {
            // Emit flushes pending testsUpdated first: a client sees the test running before its output.
            foreach (var e in Live.Drain()) effects.Emit(e);
        }
    }

    /// <summary>A test started running (caller holds Sync).</summary>
    public void Started(TestNode t)
    {
        if (LiveOutput.Enabled) Live.Started(t);
    }

    public void Partial(TestNode t, TestNode incoming)
    {
        if (LiveOutput.Enabled && (incoming.StdOut is not null || incoming.StdErr is not null))
            Live.Partial(t.ProjectId, t.Id, incoming.StdOut, incoming.StdErr);
    }

    /// <summary>A running test stopped (caller holds Sync).</summary>
    public void Finished(TestNode t)
    {
        // publish what is already buffered for the test before it stops being live, so its last lines aren't lost
        // between two flushes (the caller holds Sync, and the final result is published after this)
        if (_liveOutput is { } live)
            foreach (var e in live.Drain()) effects.Emit(e);
        _liveOutput?.Finished(t.ProjectId, t.Id);
    }

    public void Reset() => _liveOutput?.Reset();

    /// <summary>Adds the options that make the test framework stream output (detected from the output folder).</summary>
    public (string[] Args, IReadOnlyDictionary<string, string>? Env) HostOptions(ProjectEntry p, string[] args, IReadOnlyDictionary<string, string>? env)
    {
        var support = LiveOutput.Detect(Path.GetDirectoryName(p.TargetPath));
        Live.Configure(p.Project.Id, support);
        var (extraArgs, extraEnv) = LiveOutput.HostOptions(support);
        if (extraArgs.Length == 0 && extraEnv.Count == 0) return (args, env);
        var merged = new Dictionary<string, string>(extraEnv);
        if (env is not null) foreach (var (k, v) in env) merged[k] = v;
        return ([.. args, .. extraArgs], merged);
    }

    public void Attach(ProjectEntry p, MtpHost host)
    {
        if (!LiveOutput.Enabled) return;
        var projectId = p.Project.Id;
        host.OutputLine = (line, err) => Live.HostLine(projectId, line, err);
    }

    /// <summary>Stops the flush timer; also when no test produced output yet, so a later access doesn't start one.</summary>
    public void Dispose() => Live.Dispose();

    /// <summary>GET /api/tests/live-output: the output streamed so far for a running test; null when it is not running.</summary>
    public LiveOutputSnapshot? Get(string? projectId, string id)
    {
        lock (state.Sync) return _liveOutput?.Snapshot(projectId, id);
    }
}
