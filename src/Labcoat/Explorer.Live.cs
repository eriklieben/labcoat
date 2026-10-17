namespace Labcoat;

// Live console output of running tests (LiveOutput.cs; docs/contract.md "Live console output"): testOutput events, the
// test host options that make frameworks stream (xUnit --show-live-output, TUnit IDE streaming), GET /api/tests/live-output.
public sealed partial class Explorer
{
    private LiveOutput? _liveOutput;

    private LiveOutput Live => LazyInitializer.EnsureInitialized(ref _liveOutput, () => new LiveOutput(FlushLive));

    /// <summary>Flush timer: publish the buffered output under the lock, so it never overtakes the test's final result.</summary>
    private void FlushLive()
    {
        lock (Sync)
        {
            // Emit flushes pending testsUpdated first: a client sees the test running before its output.
            foreach (var e in Live.Drain()) Emit(e);
        }
    }

    /// <summary>A test started running (caller holds Sync): live output, and the running-now list / timeouts (Explorer.Hang.cs).</summary>
    private void LiveStarted(TestNode t)
    {
        if (LiveOutput.Enabled) Live.Started(t);
        HangStarted(t);
    }

    private void LivePartial(TestNode t, TestNode incoming)
    {
        if (LiveOutput.Enabled && (incoming.StdOut is not null || incoming.StdErr is not null))
            Live.Partial(t.ProjectId, t.Id, incoming.StdOut, incoming.StdErr);
    }

    private void LiveFinished(TestNode t)
    {
        if (t.Outcome != Outcome.Running) return;
        // publish what is already buffered for the test before it stops being live, so its last lines aren't lost
        // between two flushes (the caller holds Sync, and the final result is published after this)
        if (_liveOutput is { } live)
            foreach (var e in live.Drain()) Emit(e);
        _liveOutput?.Finished(t.ProjectId, t.Id);
        HangFinished(t);
    }

    private void LiveReset()
    {
        _liveOutput?.Reset();
        HangReset();
    }

    /// <summary>Adds the options that make the test framework stream output (detected from the output folder).</summary>
    private (string[] Args, IReadOnlyDictionary<string, string>? Env) LiveHostOptions(ProjectEntry p, string[] args, IReadOnlyDictionary<string, string>? env)
    {
        // A runner package's host streams the output so far in in-progress updates, like TUnit's IDE streaming.
        var support = p.Plugin is null ? LiveOutput.Detect(Path.GetDirectoryName(p.TargetPath)) : LiveOutputSupport.Streaming;
        Live.Configure(p.Project.Id, support);
        var (extraArgs, extraEnv) = LiveOutput.HostOptions(support);
        if (extraArgs.Length == 0 && extraEnv.Count == 0) return (args, env);
        var merged = new Dictionary<string, string>(extraEnv);
        if (env is not null) foreach (var (k, v) in env) merged[k] = v;
        return ([.. args, .. extraArgs], merged);
    }

    private void AttachLiveOutput(ProjectEntry p, MtpHost host)
    {
        if (!LiveOutput.Enabled) return;
        var projectId = p.Project.Id;
        host.OutputLine = (line, err) => Live.HostLine(projectId, line, err);
    }

    /// <summary>GET /api/tests/live-output: the output streamed so far for a running test; null when it is not running.</summary>
    public LiveOutputSnapshot? GetLiveOutput(string? projectId, string id)
    {
        lock (Sync) return _liveOutput?.Snapshot(projectId, id);
    }
}
