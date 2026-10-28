using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>What <see cref="TestHostPool"/> needs from the explorer: starting a test host process, whether tracing is on, the
/// Verify policy and what to do when a warm host's assemblies were rewritten under it.</summary>
internal interface ITestHostOwner
{
    MtpHost StartHost(ProjectEntry p, string[] extraArgs, Action<string, JsonNode?> onUpdate, IReadOnlyDictionary<string, string>? environment = null,
        bool? tracing = null);
    bool TracingEnabled { get; }
    /// <summary>Verify projects start a new test host for every request (see Explorer.Snapshots.cs).</summary>
    bool ColdForVerify(ProjectEntry p);
    /// <summary>An assembly of the output folder was rewritten under an idle warm host.</summary>
    void WarmHostRewritten(ProjectEntry p, WarmSession session, string assembly);
}

/// <summary>
/// Warm test hosts: one long-lived MTP server process per project, started on the first request and reused until the
/// binaries change, tracing is turned on or off, or the process dies. Also runs the requests of projects that cannot be kept
/// warm on a one-off process.
/// </summary>
internal sealed class TestHostPool(WorkspaceState state, IExplorerEffects effects, ITestHostOwner owner, CancellationToken shutdownToken)
{
    /// <summary>Set on warm test host processes so fixtures can keep expensive state alive across requests.</summary>
    public const string HostEnvVar = "LABCOAT_WARM_HOST";

    private readonly List<Task> _retiring = [];

    /// <summary>
    /// How long a cancelled request on a warm host may take to finish before the host is killed: $LABCOAT_CANCEL_GRACE_SECONDS,
    /// default 10 s. MSTest finishes the test in progress unless it observes TestContext.CancellationToken.
    /// </summary>
    public static TimeSpan CancelGrace { get; } =
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("LABCOAT_CANCEL_GRACE_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var g) && g > 0 ? g : 10);

    public void SetHostState(ProjectEntry p, string hostState)
    {
        lock (state.Sync)
        {
            if (p.Project.HostState == hostState) return;
            p.Project.HostState = hostState;
            effects.Emit(new ProjectStatusEvent(p.Project.Copy()));
        }
    }

    /// <summary>
    /// Sends one request to the project's test host. Uses (and starts, when needed) the warm host; falls back to a
    /// one-off process when the project cannot be kept warm.
    /// </summary>
    public async Task<JsonNode?> RequestAsync(ProjectEntry p, Action<string, JsonNode?> onUpdate,
        Func<MtpConnection, CancellationToken, Task<JsonNode?>> request, CancellationToken ct)
    {
        var session = await EnsureWarmAsync(p, ct);
        if (session is null)
        {
            await using var host = owner.StartHost(p, [], onUpdate);
            return await host.RunOperationAsync(async (conn, token) =>
            {
                await conn.InitializeAsync(token);
                return await request(conn, token);
            }, ct);
        }

        session.Handler = onUpdate;
        try
        {
            // Cancellation: $/cancelRequest, then wait CancelGrace for the host to answer before killing it — a warm
            // host that survives keeps its fixtures (e.g. a running Aspire app) for the next run.
            var connection = session.Connection!;
            connection.CancelGrace = CancelGrace;
            return await request(connection, ct);
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        {
            // Connection closed: the process died mid-request (crash, Environment.Exit in a test, killed...).
            // Bounded by the 2 s timeout, not by ct: the crash report needs the exit code even when the request is being cancelled.
            try { await session.Host.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch { /* still running after 2 s: reported as is below */ }
            var tail = session.Host.OutputTail(3000);
            throw new TestHostExitedException(
                $"Test host exited{(session.Host.ExitCode is { } c ? $" with code {c}" : "")} during the request." + (tail.Length > 0 ? "\n" + tail : ""),
                session.Host.ExitCode, session.Host.StderrTail());
        }
        finally
        {
            session.Handler = null;
        }
    }

    /// <summary>Returns the warm session (starting one when there is none or the binaries changed); null = cannot be kept warm.</summary>
    public async Task<WarmSession?> EnsureWarmAsync(ProjectEntry p, CancellationToken ct)
    {
        if (p.WarmUnsupported || owner.ColdForVerify(p)) return null; // ColdForVerify: Explorer.Snapshots.cs
        var hashes = OutputFingerprints.OutputHashes(p.TargetPath!);
        var fingerprint = OutputFingerprints.FingerprintOf(hashes);
        WarmSession? existing;
        bool tracing;
        lock (state.Sync)
        {
            existing = p.Warm;
            tracing = owner.TracingEnabled;
        }
        if (existing is not null)
        {
            var rewritten = existing.Watch.Check();
            if (!existing.Host.HasExited && existing.Fingerprint == fingerprint && existing.Tracing == tracing && rewritten is null) return existing;
            Retire(p, existing, RetireReason(existing, fingerprint, tracing, rewritten));
        }
        return await StartWarmAsync(p, hashes, fingerprint, tracing, ct);
    }

    /// <summary>Why an existing warm host can't serve the next request (null: it exited).</summary>
    private static string? RetireReason(WarmSession existing, string fingerprint, bool tracing, string? rewritten)
    {
        if (existing.Host.HasExited) return null;
        if (existing.Fingerprint != fingerprint) return "the test app was rebuilt";
        if (rewritten is not null) return RewrittenReason(rewritten);
        return tracing ? "trace collection was turned on" : "trace collection was turned off";
    }

    /// <summary>Starts and connects a new warm host; null when the test app can't be kept warm.</summary>
    private async Task<WarmSession?> StartWarmAsync(ProjectEntry p, Dictionary<string, long> hashes, string fingerprint, bool tracing, CancellationToken ct)
    {
        SetHostState(p, "starting");
        WarmSession? session = null;
        // Watching starts before the process does: a write while it loads its assemblies counts too.
        var watch = new OutputWatch(Path.GetDirectoryName(p.TargetPath!)!, hashes, OutputFingerprints.ContentHash);
        MtpHost host;
        try
        {
            host = owner.StartHost(p, [], (method, prm) => session?.Handler?.Invoke(method, prm),
                new Dictionary<string, string> { [HostEnvVar] = "1" }, tracing);
        }
        catch
        {
            watch.Dispose();
            SetHostState(p, "cold");
            throw;
        }
        session = new WarmSession(host, fingerprint, tracing, watch);
        if (!await ConnectWarmAsync(p, session, ct)) return null;

        lock (state.Sync) p.Warm = session;
        SetHostState(p, "warm");
        // Lifetime watch: outlives the request that started the host, so it takes no request token.
        _ = host.WaitForExitAsync(CancellationToken.None)
            .ContinueWith(_ => OnWarmExited(p, session), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        watch.Modified = name => owner.WarmHostRewritten(p, session, name);
        return session;
    }

    /// <summary>Connects to a new warm host; false (host stopped) when the test app runs behind a test host controller.</summary>
    private async Task<bool> ConnectWarmAsync(ProjectEntry p, WarmSession session, CancellationToken ct)
    {
        var host = session.Host;
        try
        {
            var conn = await host.ConnectAsync(ct);
            if (conn.InitializeResult?["capabilities"]?["testing"]?["supportsDiscovery"] is JsonValue v && v.TryGetValue<bool>(out var sd) && !sd)
            {
                effects.Log(p.Project.Id, LogLevels.Info, "The test app runs behind a test host controller (out-of-process extension), so it cannot be " +
                    "kept warm; every request starts a new process.");
                p.WarmUnsupported = true;
                session.Watch.Dispose();
                TrackRetiring(StopHostAsync(host));
                SetHostState(p, "cold");
                return false;
            }
            session.Connection = conn;
            return true;
        }
        catch
        {
            if (ct.IsCancellationRequested) host.Kill();
            session.Watch.Dispose();
            TrackRetiring(StopHostAsync(host));
            SetHostState(p, "cold");
            throw;
        }
    }

    public static string RewrittenReason(string assembly) =>
        $"{assembly} was rewritten in place while it ran (a build, or code coverage instrumenting it), so the copy it has loaded may be corrupt";

    /// <summary>
    /// After an operation that may have rewritten assemblies under the warm host (a coverage run, a run during which a
    /// build wrote the output folder): replace the host when it saw an in-place write. The caller holds the op lock.
    /// </summary>
    public async Task RenewIfRewrittenAsync(ProjectEntry p, WarmSession? session, CancellationToken ct)
    {
        if (session is null) return;
        // The watcher reports on its own thread: give a write at the very end a moment to arrive.
        if (session.Watch.Check() is null && session.Watch.IsWatching) await Task.Delay(150, CancellationToken.None);
        if (session.Watch.Check() is not { } assembly) return;
        lock (state.Sync)
        {
            if (p.Warm != session) return;
        }
        await RenewAsync(p, session, RewrittenReason(assembly), ct);
    }

    /// <summary>Retires <paramref name="session"/> and starts a fresh warm host (logged; failures are logged, not thrown).</summary>
    public async Task RenewAsync(ProjectEntry p, WarmSession session, string reason, CancellationToken ct)
    {
        Retire(p, session, reason);
        if (ct.IsCancellationRequested || p.TargetPath is null || !File.Exists(p.TargetPath)) return;
        try
        {
            if (await EnsureWarmAsync(p, ct) is not null) effects.Log(p.Project.Id, LogLevels.Info, "Test host is warm");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            effects.Log(p.Project.Id, LogLevels.Warning, "Could not start a new warm test host: " + ex.Message);
        }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
    }

    /// <summary>The project's warm session, if any (for an operation that wants to check it afterwards).</summary>
    public WarmSession? CurrentWarm(ProjectEntry p)
    {
        lock (state.Sync) return p.Warm;
    }

    private void OnWarmExited(ProjectEntry p, WarmSession session)
    {
        bool current;
        lock (state.Sync)
        {
            current = p.Warm == session;
            if (current) p.Warm = null;
        }
        session.Watch.Dispose();
        if (!current) return;
        SetHostState(p, "cold");
        if (!shutdownToken.IsCancellationRequested)
            effects.Log(p.Project.Id, LogLevels.Info, $"Test host exited (code {session.Host.ExitCode?.ToString() ?? "?"}); the next request starts a new one.");
    }

    /// <summary>Detaches the warm session and stops its process in the background (graceful <c>exit</c>, then kill).</summary>
    public void Retire(ProjectEntry p, WarmSession session, string? reason)
    {
        lock (state.Sync)
        {
            if (p.Warm == session) p.Warm = null;
        }
        session.Watch.Dispose();
        SetHostState(p, "cold");
        if (reason is not null) effects.Log(p.Project.Id, LogLevels.Info, $"Stopping the warm test host: {reason}.");
        TrackRetiring(StopHostAsync(session.Host));
    }

    /// <summary>After a build: retire the warm host when the binaries it loaded are outdated.</summary>
    public void InvalidateIfStale(ProjectEntry p)
    {
        WarmSession? w;
        lock (state.Sync) w = p.Warm;
        if (w is not null && p.TargetPath is not null && OutputFingerprints.Of(p.TargetPath) != w.Fingerprint)
            Retire(p, w, "the test app was rebuilt");
    }

    /// <summary>Windows locks loaded assemblies: stop the warm host before a build may overwrite them.</summary>
    public async Task StopBeforeBuildAsync(ProjectEntry p)
    {
        if (!OperatingSystem.IsWindows()) return;
        WarmSession? w;
        lock (state.Sync)
        {
            w = p.Warm;
            p.Warm = null;
        }
        if (w is null) return;
        w.Watch.Dispose();
        SetHostState(p, "cold");
        await StopHostAsync(w.Host);
    }

    private void TrackRetiring(Task t)
    {
        lock (_retiring)
        {
            _retiring.RemoveAll(x => x.IsCompleted);
            _retiring.Add(t);
        }
    }

    /// <summary>
    /// <c>exit</c> + wait. Generous timeout: fixtures dispose expensive resources on the way out (an Aspire app takes
    /// ~12s to stop), and killing the process would orphan them.
    /// </summary>
    public static async Task StopHostAsync(MtpHost host)
    {
        try { await host.ShutdownAsync(TimeSpan.FromSeconds(30)); } catch { /* best effort: DisposeAsync below kills what's left */ }
        try { await host.DisposeAsync(); } catch { /* best effort at shutdown */ }
    }

    /// <summary>Explorer shutdown: gracefully stop every warm host (and the ones still being retired).</summary>
    public async Task ShutdownAsync()
    {
        var hosts = new List<MtpHost>();
        lock (state.Sync)
        {
            foreach (var p in state.Projects)
            {
                if (p.Warm is { } w)
                {
                    hosts.Add(w.Host);
                    w.Watch.Dispose();
                }
                p.Warm = null;
            }
        }
        List<Task> tasks = [.. hosts.Select(StopHostAsync)];
        lock (_retiring) tasks.AddRange(_retiring);
        if (tasks.Count == 0) return;
        if (hosts.Count > 0) Console.WriteLine($"Stopping {hosts.Count} warm test host(s)…");
        // Shutdown: bounded by the timeout only; no run token may cut the graceful stop short.
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(45), CancellationToken.None); }
        catch (TimeoutException) { foreach (var h in hosts) h.Kill(); }
        catch { /* StopHostAsync swallows its own errors; nothing left to do at shutdown */ }
    }
}
