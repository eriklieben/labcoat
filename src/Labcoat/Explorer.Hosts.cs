using System.Text.Json.Nodes;

namespace Labcoat;

// The warm test hosts (TestHostPool.cs): the pool's accessor, what it needs from the explorer, and the two operations that
// take the operation lock.
public sealed partial class Explorer : ITestHostOwner
{
    private TestHostPool? _hosts;

    private TestHostPool Hosts => _hosts ??= new TestHostPool(_state, this, this, _shutdownToken);

    MtpHost ITestHostOwner.StartHost(ProjectEntry p, string[] extraArgs, Action<string, JsonNode?> onUpdate, IReadOnlyDictionary<string, string>? environment, bool? tracing) =>
        StartHost(p, extraArgs, onUpdate, environment, tracing);

    bool ITestHostOwner.ColdForVerify(ProjectEntry p) => ColdForVerify(p);

    void ITestHostOwner.WarmHostRewritten(ProjectEntry p, WarmSession session, string assembly) => _ = RestartRewrittenWarmAsync(p, session, assembly);

    /// <summary>
    /// An assembly of the output folder was rewritten under an idle warm host (OutputWatch): replace the host now, so the
    /// next request finds a warm, intact one. Busy (a run, a build, a coverage run) = the operation handles it.
    /// </summary>
    private async Task RestartRewrittenWarmAsync(ProjectEntry p, WarmSession session, string assembly)
    {
        // A try-acquire that never waits (busy: RenewWarmIfRewrittenAsync / EnsureWarmAsync handle it).
        if (_shutdown.IsCancellationRequested || !await _opLock.WaitAsync(TimeSpan.Zero, CancellationToken.None)) return;
        _activeOp = "host restart";
        try
        {
            lock (Sync)
            {
                if (p.Warm != session) return;
            }
            await Hosts.RenewAsync(p, session, TestHostPool.RewrittenReason(assembly), _shutdownToken);
        }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
        finally { ReleaseOp(); }
    }

    /// <summary>
    /// restartHost command: stop the project's warm host (all projects when null) and start a fresh one. When an operation
    /// is in progress (e.g. a hung run), it is cancelled first and the selected test hosts are killed right away instead of
    /// waiting for the cancellation grace period.
    /// </summary>
    public async Task RestartHostAsync(string? projectId)
    {
        if (_activeOp is { } active)
        {
            Log(projectId, LogLevels.Info, $"Restart requested while a {active} is in progress: cancelling it and stopping the test host now.");
            List<MtpHost> hosts;
            lock (Sync)
                hosts = SelectProjects(projectId).SelectMany(p => new[] { p.Warm?.Host, p.ActiveSession }).OfType<MtpHost>().Distinct().ToList();
            Cancel();
            foreach (var h in hosts) h.Kill();
        }
        var ct = CurrentToken();
        if (!await AcquireOpAsync("host restart", ct)) return;
        try
        {
            foreach (var p in SelectProjects(projectId).ToList())
            {
                WarmSession? w;
                lock (Sync)
                {
                    w = p.Warm;
                    p.Warm = null;
                }
                p.WarmUnsupported = false;
                if (w is not null)
                {
                    w.Watch.Dispose();
                    Hosts.SetHostState(p, "cold");
                    Log(p.Project.Id, LogLevels.Info, "Restarting the test host");
                    await TestHostPool.StopHostAsync(w.Host);
                }
                if (ct.IsCancellationRequested) break;
                if (p.TargetPath is not null && File.Exists(p.TargetPath) && await Hosts.EnsureWarmAsync(p, ct) is not null)
                    Log(p.Project.Id, LogLevels.Info, "Test host is warm");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log(projectId, LogLevels.Error, "Could not restart the test host: " + ex.Message);
        }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
        finally { ReleaseOp(); }
    }
}
