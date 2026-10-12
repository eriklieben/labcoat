using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Labcoat;

// Warm test hosts (one long-lived MTP server process per project), the test impact map and smart watch.
public sealed partial class Explorer
{
    /// <summary>Set on warm test host processes so fixtures can keep expensive state alive across requests.</summary>
    public const string WarmHostEnvVar = "LABCOAT_WARM_HOST";

    private sealed class WarmSession(MtpHost host, string fingerprint, bool tracing, OutputWatch watch)
    {
        public MtpHost Host { get; } = host;
        /// <summary>Notices assemblies of the output folder rewritten in place while the process runs (OutputWatch.cs).</summary>
        public OutputWatch Watch { get; } = watch;
        /// <summary>Trace collection state the process was started with (its LABCOAT_OTLP_* environment).</summary>
        public bool Tracing { get; } = tracing;
        /// <summary><see cref="OutputFingerprint"/> of the output directory when the process was started.</summary>
        public string Fingerprint { get; } = fingerprint;
        public MtpConnection? Connection;
        /// <summary>Notification sink of the request in flight (requests are serialized by the op lock).</summary>
        public volatile Action<string, JsonNode?>? Handler;
    }

    private sealed partial class ProjectEntry
    {
        public WarmSession? Warm;
        /// <summary>The test app runs behind a test host controller (always-on out-of-process extension): no warm host.</summary>
        public bool WarmUnsupported;
        /// <summary>Changed files collected by the watcher during the debounce window.</summary>
        public readonly HashSet<string> PendingChanges = [];
    }

    private readonly List<Task> _retiring = [];

    /// <summary>
    /// How long a cancelled request on a warm host may take to finish before the host is killed: $LABCOAT_CANCEL_GRACE_SECONDS,
    /// default 10 s. MSTest finishes the test in progress unless it observes TestContext.CancellationToken.
    /// </summary>
    public static TimeSpan WarmCancelGrace { get; } =
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("LABCOAT_CANCEL_GRACE_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var g) && g > 0 ? g : 10);
    private readonly ImpactMap _impact = new();

    // ------------------------------------------------------------------ warm host

    private void SetHostState(ProjectEntry p, string state)
    {
        lock (Sync)
        {
            if (p.Project.HostState == state) return;
            p.Project.HostState = state;
            Emit(new ProjectStatusEvent(p.Project.Copy()));
        }
    }

    /// <summary>
    /// Content fingerprint of the test app's output directory (every top-level dll/json). Covers the test assembly and
    /// copied ProjectReference outputs, which change without the test dll being rewritten (reference assemblies keep the
    /// test project's compile up to date). Content-based, so a rebuild that rewrites identical bytes (deterministic
    /// compile after a touch) keeps the warm host. File hashes are cached by (path, size, write time).
    /// </summary>
    private static string OutputFingerprint(string targetPath) => FingerprintOf(OutputHashes(targetPath));

    private static string FingerprintOf(IReadOnlyDictionary<string, long> hashes)
    {
        var hash = new HashCode();
        foreach (var (name, h) in hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            hash.Add(name);
            hash.Add(h);
        }
        return hash.ToHashCode().ToString("x8");
    }

    /// <summary>Content hash of every top-level dll/json of the test app's output directory, by file name.</summary>
    private static Dictionary<string, long> OutputHashes(string targetPath)
    {
        var hashes = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(targetPath)!);
            foreach (var f in dir.EnumerateFiles("*.dll").Concat(dir.EnumerateFiles("*.json")))
                hashes[f.Name] = ContentHash(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial hashes: a later compare sees a change and renews */ }
        return hashes;
    }

    /// <summary>Content hash of one file of the output directory (cached by size + write time).</summary>
    internal static long ContentHash(string path) => ContentHash(new FileInfo(path));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Ticks, long Hash)> FileHashes = new();

    private static long ContentHash(FileInfo f)
    {
        var (length, ticks) = (f.Length, f.LastWriteTimeUtc.Ticks);
        if (FileHashes.TryGetValue(f.FullName, out var c) && c.Length == length && c.Ticks == ticks) return c.Hash;
        long h;
        try
        {
            using var stream = f.OpenRead();
            h = BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(stream), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { h = ticks ^ length; }
        FileHashes[f.FullName] = (length, ticks, h);
        return h;
    }

    /// <summary>
    /// Sends one request to the project's test host. Uses (and starts, when needed) the warm host; falls back to a
    /// one-off process when the project cannot be kept warm.
    /// </summary>
    private async Task<JsonNode?> RequestOnHostAsync(ProjectEntry p, Action<string, JsonNode?> onUpdate,
        Func<MtpConnection, CancellationToken, Task<JsonNode?>> request, CancellationToken ct)
    {
        var session = await EnsureWarmAsync(p, ct);
        if (session is null)
        {
            await using var host = StartHost(p, [], onUpdate);
            return await host.RunOperationAsync(async (conn, token) =>
            {
                await conn.InitializeAsync(token);
                return await request(conn, token);
            }, ct);
        }

        session.Handler = onUpdate;
        try
        {
            // Cancellation: $/cancelRequest, then wait WarmCancelGrace for the host to answer before killing it — a warm
            // host that survives keeps its fixtures (e.g. a running Aspire app) for the next run.
            var connection = session.Connection!;
            connection.CancelGrace = WarmCancelGrace;
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
    private async Task<WarmSession?> EnsureWarmAsync(ProjectEntry p, CancellationToken ct)
    {
        if (p.WarmUnsupported || ColdForVerify(p)) return null; // ColdForVerify: Explorer.Snapshots.cs
        var hashes = OutputHashes(p.TargetPath!);
        var fingerprint = FingerprintOf(hashes);
        WarmSession? existing;
        bool tracing;
        lock (Sync)
        {
            existing = p.Warm;
            tracing = _tracingEnabled;
        }
        if (existing is not null)
        {
            var rewritten = existing.Watch.Check();
            if (!existing.Host.HasExited && existing.Fingerprint == fingerprint && existing.Tracing == tracing && rewritten is null) return existing;
            RetireWarm(p, existing, RetireReason(existing, fingerprint, tracing, rewritten));
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
        var watch = new OutputWatch(Path.GetDirectoryName(p.TargetPath!)!, hashes, ContentHash);
        MtpHost host;
        try
        {
            host = StartHost(p, [], (method, prm) => session?.Handler?.Invoke(method, prm),
                new Dictionary<string, string> { [WarmHostEnvVar] = "1" }, tracing);
        }
        catch
        {
            watch.Dispose();
            SetHostState(p, "cold");
            throw;
        }
        session = new WarmSession(host, fingerprint, tracing, watch);
        if (!await ConnectWarmAsync(p, session, ct)) return null;

        lock (Sync) p.Warm = session;
        SetHostState(p, "warm");
        // Lifetime watch: outlives the request that started the host, so it takes no request token.
        _ = host.WaitForExitAsync(CancellationToken.None)
            .ContinueWith(_ => OnWarmExited(p, session), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        watch.Modified = name => _ = RestartRewrittenWarmAsync(p, session, name);
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
                Log(p.Project.Id, LogLevels.Info, "The test app runs behind a test host controller (out-of-process extension), so it cannot be " +
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

    private static string RewrittenReason(string assembly) =>
        $"{assembly} was rewritten in place while it ran (a build, or code coverage instrumenting it), so the copy it has loaded may be corrupt";

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
            await RenewWarmAsync(p, session, RewrittenReason(assembly), _shutdownToken);
        }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
        finally { ReleaseOp(); }
    }

    /// <summary>
    /// After an operation that may have rewritten assemblies under the warm host (a coverage run, a run during which a
    /// build wrote the output folder): replace the host when it saw an in-place write. The caller holds the op lock.
    /// </summary>
    private async Task RenewWarmIfRewrittenAsync(ProjectEntry p, WarmSession? session, CancellationToken ct)
    {
        if (session is null) return;
        // The watcher reports on its own thread: give a write at the very end a moment to arrive.
        if (session.Watch.Check() is null && session.Watch.IsWatching) await Task.Delay(150, CancellationToken.None);
        if (session.Watch.Check() is not { } assembly) return;
        lock (Sync)
        {
            if (p.Warm != session) return;
        }
        await RenewWarmAsync(p, session, RewrittenReason(assembly), ct);
    }

    /// <summary>Retires <paramref name="session"/> and starts a fresh warm host (logged; failures are logged, not thrown).</summary>
    private async Task RenewWarmAsync(ProjectEntry p, WarmSession session, string reason, CancellationToken ct)
    {
        RetireWarm(p, session, reason);
        if (ct.IsCancellationRequested || p.TargetPath is null || !File.Exists(p.TargetPath)) return;
        try
        {
            if (await EnsureWarmAsync(p, ct) is not null) Log(p.Project.Id, LogLevels.Info, "Test host is warm");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log(p.Project.Id, LogLevels.Warning, "Could not start a new warm test host: " + ex.Message);
        }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
    }

    /// <summary>The project's warm session, if any (for an operation that wants to check it afterwards).</summary>
    private WarmSession? CurrentWarm(ProjectEntry p)
    {
        lock (Sync) return p.Warm;
    }

    private void OnWarmExited(ProjectEntry p, WarmSession session)
    {
        bool current;
        lock (Sync)
        {
            current = p.Warm == session;
            if (current) p.Warm = null;
        }
        session.Watch.Dispose();
        if (!current) return;
        SetHostState(p, "cold");
        if (!_shutdown.IsCancellationRequested)
            Log(p.Project.Id, LogLevels.Info, $"Test host exited (code {session.Host.ExitCode?.ToString() ?? "?"}); the next request starts a new one.");
    }

    /// <summary>Detaches the warm session and stops its process in the background (graceful <c>exit</c>, then kill).</summary>
    private void RetireWarm(ProjectEntry p, WarmSession session, string? reason)
    {
        lock (Sync)
        {
            if (p.Warm == session) p.Warm = null;
        }
        session.Watch.Dispose();
        SetHostState(p, "cold");
        if (reason is not null) Log(p.Project.Id, LogLevels.Info, $"Stopping the warm test host: {reason}.");
        TrackRetiring(StopHostAsync(session.Host));
    }

    /// <summary>After a build: retire the warm host when the binaries it loaded are outdated.</summary>
    private void InvalidateWarmIfStale(ProjectEntry p)
    {
        WarmSession? w;
        lock (Sync) w = p.Warm;
        if (w is not null && p.TargetPath is not null && OutputFingerprint(p.TargetPath) != w.Fingerprint)
            RetireWarm(p, w, "the test app was rebuilt");
    }

    /// <summary>Windows locks loaded assemblies: stop the warm host before a build may overwrite them.</summary>
    private async Task StopWarmBeforeBuildAsync(ProjectEntry p)
    {
        if (!OperatingSystem.IsWindows()) return;
        WarmSession? w;
        lock (Sync)
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
    private static async Task StopHostAsync(MtpHost host)
    {
        try { await host.ShutdownAsync(TimeSpan.FromSeconds(30)); } catch { /* best effort: DisposeAsync below kills what's left */ }
        try { await host.DisposeAsync(); } catch { /* best effort at shutdown */ }
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
                    SetHostState(p, "cold");
                    Log(p.Project.Id, LogLevels.Info, "Restarting the test host");
                    await StopHostAsync(w.Host);
                }
                if (ct.IsCancellationRequested) break;
                if (p.TargetPath is not null && File.Exists(p.TargetPath) && await EnsureWarmAsync(p, ct) is not null)
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

    /// <summary>Explorer shutdown: gracefully stop every warm host (and the ones still being retired).</summary>
    private async Task ShutdownWarmHostsAsync()
    {
        var hosts = new List<MtpHost>();
        lock (Sync)
        {
            foreach (var p in _projects)
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

    // ------------------------------------------------------------------ impact map

    /// <summary>
    /// Option impactMap (ImpactMapModes): how finely the "build impact map" pass attributes coverage to tests. "class" =
    /// one coverage process per test class; "test" = one per test; "auto" (default) = per class, then the classes a change
    /// maps to through class-level data are refined per test in the background (<see cref="QueueImpactRefinementLocked"/>).
    /// </summary>
    public string ImpactMapMode
    {
        get => _impact.Mode;
        init => _impact.Mode = ImpactMapModes.Parse(value) ?? ImpactMapModes.Default;
    }

    /// <summary>
    /// Coverage processes the pass runs at once when the instrumentation leaves the output folder alone (dynamic). Static
    /// instrumentation rewrites the assemblies in place, so its processes run one after another.
    /// </summary>
    internal static int ImpactParallelism { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

    /// <summary>False for <c>--run --affected</c>: planning must not start coverage processes next to the run.</summary>
    internal bool RefineImpactInBackground { get; set; } = true;

    // impactMap "auto": classes waiting for per-test refinement, and the ones queued / done since the last pass.
    private readonly Queue<(string ProjectId, string ClassKey)> _refineQueue = new();
    private readonly HashSet<(string ProjectId, string ClassKey)> _refineSeen = [];
    private Task? _refineWorker;

    private static string ClassKeyOf(TestNode t) => (t.Namespace is { Length: > 0 } ns ? ns + "." : "") + (t.ClassName ?? "(no class)");

    /// <summary>Records which source files the tests <paramref name="testIds"/> covered (from a coverage run of exactly those tests).</summary>
    private void RecordImpact(ProjectEntry p, IEnumerable<string>? testIds, string coverageFile, bool replacePerTest = false)
    {
        var ids = testIds?.ToList();
        if (ids is not { Count: > 0 }) return;
        try
        {
            var coverage = ImpactMap.ParseCobertura(coverageFile, TestAssemblyNames());
            lock (Sync)
            {
                _impact.Record(p.Project.Id, ids, coverage, replacePerTest);
                PublishSettings();
            }
        }
        catch (Exception ex)
        {
            Log(p.Project.Id, LogLevels.Warning, "Impact map: could not read coverage: " + ex.Message);
        }
    }

    /// <summary>One coverage process of the impact-map pass: a label for the log and the tests it runs.</summary>
    internal sealed record ImpactGroup(string Label, List<(string Id, string DisplayName)> Tests);

    /// <summary>The coverage processes of a pass: one per test class, or one per test (<paramref name="perTest"/>).</summary>
    internal static List<ImpactGroup> ImpactGroups(IEnumerable<TestNode> tests, bool perTest) =>
        perTest
            ? tests.Select(t => new ImpactGroup($"{t.ClassName ?? "(no class)"}.{t.DisplayName}", [(t.Id, t.DisplayName)])).ToList()
            : tests.GroupBy(ClassKeyOf).Select(g => new ImpactGroup(g.First().ClassName ?? "(no class)", g.Select(t => (t.Id, t.DisplayName)).ToList())).ToList();

    /// <summary>
    /// buildImpactMap command: one coverage run per test class (impactMap "class" / "auto") or per test ("test") to learn
    /// which methods each test executes. <paramref name="mode"/> overrides the option for this pass.
    /// </summary>
    public async Task BuildImpactMapAsync(string? projectId, string? mode = null)
    {
        var granularity = ImpactMapModes.Parse(mode) ?? ImpactMapMode;
        var perTest = granularity == ImpactMapModes.Test;
        var ct = CurrentToken();
        if (!await AcquireOpAsync("impact map build", ct)) return;
        var sw = Stopwatch.StartNew();
        var recorded = 0;
        lock (Sync)
        {
            _impact.Building = true;
            PublishSettings();
        }
        Log(null, LogLevels.Info, perTest
            ? "Building the test impact map (one coverage run per test)…"
            : "Building the test impact map (one coverage run per test class)…");
        try
        {
            foreach (var p in SelectProjects(projectId).ToList())
            {
                if (ct.IsCancellationRequested) break;
                recorded += await BuildProjectImpactMapAsync(p, perTest, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log(null, LogLevels.Error, "Impact map failed: " + ex.Message);
        }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
        finally
        {
            ImpactMapInfo? info;
            lock (Sync)
            {
                _impact.Building = false;
                if (recorded > 0) _impact.BuiltAt = DateTime.UtcNow;
                info = _impact.Info();
                PublishSettings();
            }
            Log(null, LogLevels.Info, ct.IsCancellationRequested ? "Impact map: cancelled" : ImpactMapBuiltLine(info, sw.Elapsed, perTest));
            ReleaseOp();
        }
    }

    private static string ImpactMapBuiltLine(ImpactMapInfo? info, TimeSpan elapsed, bool perTest) =>
        $"Impact map built in {elapsed.TotalSeconds:0.0}s ({(perTest ? "per test" : "per test class")}): {info?.Files ?? 0} source files → " +
        $"{info?.Tests ?? 0} tests ({info?.PerTest ?? 0} with per-test data)";

    /// <summary>One project's part of the impact-map pass; returns the number of coverage processes recorded.</summary>
    private async Task<int> BuildProjectImpactMapAsync(ProjectEntry p, bool perTest, CancellationToken ct)
    {
        if (!await BuildAsync(p, ct, onlyIfChanged: true) || !await EnsureDiscoveredAsync(p, ct)) return 0;
        if (!await SupportsCoverageAsync(p, ct))
        {
            Log(p.Project.Id, LogLevels.Warning, "Impact map needs code coverage (Microsoft.Testing.Extensions.CodeCoverage); skipping this project.");
            return 0;
        }
        List<ImpactGroup> groups;
        lock (Sync)
        {
            groups = ImpactGroups(_order.Where(k => k.ProjectId == p.Project.Id).Select(k => _tests[k]), perTest);
            // A fresh pass: classes may be refined per test again (auto).
            _refineSeen.RemoveWhere(k => k.ProjectId == p.Project.Id);
        }
        SetStatus(p, ProjectStatus.Running);
        var recorded = await RunImpactGroupsAsync(p, groups, replacePerTest: true, ct);
        SetStatus(p, ProjectStatus.Idle);
        return recorded;
    }

    /// <summary>
    /// Runs one coverage process per group (several at once with dynamic instrumentation, see <see cref="ImpactParallelism"/>)
    /// and records what each covered. Returns the number of groups recorded. The caller holds the op lock.
    /// </summary>
    private async Task<int> RunImpactGroupsAsync(ProjectEntry p, List<ImpactGroup> groups, bool replacePerTest, CancellationToken ct)
    {
        if (groups.Count == 0) return 0;
        var plan = CoveragePlanFor(p);
        var parallel = plan.RewritesOutput ? 1 : Math.Max(1, ImpactParallelism);
        var progress = new ImpactProgress(groups.Count);
        // What happens to the warm host meanwhile: RunCoverageProcessAsync (Explorer.Coverage.cs).
        await RunCoverageProcessAsync(p, plan, async () =>
        {
            using var gate = new SemaphoreSlim(parallel);
            var tasks = groups.Select(group => RunImpactGroupGatedAsync(p, plan, group, gate, progress, replacePerTest, ct)).ToList();
            await Task.WhenAll(tasks);
        }, ct);
        return progress.Recorded;
    }

    /// <summary>Progress of the coverage processes of one impact-map pass (they run in parallel).</summary>
    private sealed class ImpactProgress(int total)
    {
        private readonly int _logEvery = Math.Max(1, total / 20);
        private int _started;
        private int _recorded;

        public int Total => total;
        public int Recorded => _recorded;

        /// <summary>Counts a started process; its number (1-based).</summary>
        public int Start() => Interlocked.Increment(ref _started);

        public void CountRecorded() => Interlocked.Increment(ref _recorded);

        /// <summary>Every process of a small pass is logged, about 20 of a large one.</summary>
        public bool ShouldLog(int n) => total <= 40 || n % _logEvery == 0 || n == total;
    }

    private async Task RunImpactGroupGatedAsync(ProjectEntry p, CoveragePlan plan, ImpactGroup group, SemaphoreSlim gate, ImpactProgress progress,
        bool replacePerTest, CancellationToken ct)
    {
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            var n = progress.Start();
            if (progress.ShouldLog(n))
                Log(p.Project.Id, LogLevels.Info, $"Impact map [{n}/{progress.Total}]: {group.Label}" +
                    (group.Tests.Count == 1 ? "" : $" ({group.Tests.Count} tests)"));
            if (await RunImpactGroupAsync(p, plan, group, replacePerTest, ct)) progress.CountRecorded();
        }
        finally { gate.Release(); }
    }

    private async Task<bool> RunImpactGroupAsync(ProjectEntry p, CoveragePlan plan, ImpactGroup group, bool replacePerTest, CancellationToken ct)
    {
        var file = CoverageFilePath(p.Project.Id, Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            await using (var host = StartHost(p, plan.Arguments(file), (_, _) => { }))
            {
                await host.RunOperationAsync(async (conn, token) =>
                {
                    await conn.InitializeAsync(token);
                    return await conn.RunAsync(runId, group.Tests, token);
                }, ct);
                await host.ShutdownAsync(TimeSpan.FromSeconds(60));
            }
            if (File.Exists(file))
            {
                RecordImpact(p, group.Tests.Select(t => t.Id), file, replacePerTest);
                return true;
            }
            Log(p.Project.Id, LogLevels.Warning, $"Impact map: no coverage file for {group.Label}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log(p.Project.Id, LogLevels.Warning, $"Impact map: {group.Label} failed: {ex.Message}");
        }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
        finally { try { File.Delete(file); } catch { /* best effort: a temp file left in the temp folder */ } }
        return false;
    }

    /// <summary>
    /// impactMap "auto": queues the classes of <paramref name="classLevel"/> (tests a change selected through class-level
    /// data) for per-test refinement in the background, once per class until the next pass. Classes of a single test are
    /// exact already. Caller holds <c>Sync</c>.
    /// </summary>
    private void QueueImpactRefinementLocked(IEnumerable<(string ProjectId, string Id)> classLevel)
    {
        if (ImpactMapMode != ImpactMapModes.Auto || !RefineImpactInBackground) return;
        var added = false;
        foreach (var key in classLevel)
        {
            if (!_tests.TryGetValue(key, out var t)) continue;
            var cls = (key.ProjectId, ClassKeyOf(t));
            if (_refineSeen.Contains(cls)) continue;
            if (_order.Count(k => k.ProjectId == key.ProjectId && ClassKeyOf(_tests[k]) == cls.Item2) < 2) continue;
            _refineSeen.Add(cls);
            _refineQueue.Enqueue(cls);
            added = true;
        }
        // The worker outlives the request that queued the classes: it stops on shutdown (it loops on _shutdown).
        if (added && _refineWorker is null) _refineWorker = Task.Run(RefineWorkerAsync, CancellationToken.None);
    }

    private async Task RefineWorkerAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            List<(string ProjectId, string ClassKey)> batch;
            lock (Sync)
            {
                if (_refineQueue.Count == 0)
                {
                    _refineWorker = null;
                    return;
                }
                batch = [.. _refineQueue];
                _refineQueue.Clear();
            }
            try { await RefineClassesAsync(batch); }
            catch (Exception ex) when (!_shutdown.IsCancellationRequested) { Log(null, LogLevels.Warning, "Impact map refinement failed: " + ex.Message); }
            catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
        }
        lock (Sync) _refineWorker = null;
    }

    /// <summary>
    /// Refines the classes of <paramref name="classLevel"/> per test now (run affected with "build impact map first").
    /// Tests that have per-test data already are skipped.
    /// </summary>
    public async Task RefineImpactMapAsync(IEnumerable<(string ProjectId, string Id)> classLevel)
    {
        List<(string ProjectId, string ClassKey)> classes;
        lock (Sync)
        {
            classes = classLevel.Where(_tests.ContainsKey).Select(k => (k.ProjectId, ClassKeyOf(_tests[k]))).Distinct().ToList();
            foreach (var c in classes) _refineSeen.Add(c);
        }
        if (classes.Count > 0) await RefineClassesAsync(classes);
    }

    /// <summary>One coverage process per test of the given classes (skipping tests with per-test data); takes the op lock.</summary>
    private async Task RefineClassesAsync(List<(string ProjectId, string ClassKey)> classes)
    {
        var ct = CurrentToken();
        if (!await AcquireOpAsync("impact map refinement", ct))
        {
            lock (Sync) foreach (var c in classes) _refineSeen.Remove(c); // cancelled before it started: may be queued again
            return;
        }
        var sw = Stopwatch.StartNew();
        var refined = 0;
        try
        {
            foreach (var byProject in classes.GroupBy(c => c.ProjectId))
            {
                if (ct.IsCancellationRequested) break;
                refined += await RefineProjectClassesAsync(byProject, ct);
            }
        }
        finally
        {
            lock (Sync)
            {
                _impact.Building = false;
                PublishSettings();
            }
            if (refined > 0) Log(null, LogLevels.Info, $"Impact map: {refined} test{Plural(refined)} refined to per-test data in {sw.Elapsed.TotalSeconds:0.0}s");
            ReleaseOp();
        }
    }

    /// <summary>The refinement of one project's classes; returns the number of tests refined.</summary>
    private async Task<int> RefineProjectClassesAsync(IGrouping<string, (string ProjectId, string ClassKey)> byProject, CancellationToken ct)
    {
        ProjectEntry? p;
        List<ImpactGroup> groups;
        lock (Sync)
        {
            p = _projects.FirstOrDefault(x => x.Project.Id == byProject.Key);
            var wanted = byProject.Select(c => c.ClassKey).ToHashSet();
            groups = ImpactGroups(_order.Where(k => k.ProjectId == byProject.Key && !_impact.IsPerTest(k.ProjectId, k.Id))
                .Select(k => _tests[k]).Where(t => wanted.Contains(ClassKeyOf(t))), perTest: true);
        }
        if (p?.TargetPath is null || !File.Exists(p.TargetPath) || groups.Count == 0) return 0;
        if (!await SupportsCoverageAsync(p, ct)) return 0;
        lock (Sync)
        {
            _impact.Building = true;
            PublishSettings();
        }
        var names = string.Join(", ", byProject.Select(c => c.ClassKey.Split('.')[^1]).Distinct().Take(3));
        Log(p.Project.Id, LogLevels.Info, $"Impact map: refining {names} per test ({groups.Count} test{Plural(groups.Count)})…");
        return await RunImpactGroupsAsync(p, groups, replacePerTest: true, ct);
    }

    // ------------------------------------------------------------------ smart watch

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb", ".razor" };

    /// <summary>
    /// Debounced watch trigger: build + re-discover, then run only the affected tests:
    /// changed test file -> its tests; product file known to the impact map -> the mapped tests; anything else -> all tests.
    /// </summary>
    private async Task RunWatchAsync(ProjectEntry p)
    {
        string[] changed;
        lock (Sync)
        {
            changed = [.. p.PendingChanges];
            p.PendingChanges.Clear();
        }
        if (changed.Length == 0) return;
        var names = string.Join(", ", changed.Select(Path.GetFileName).Distinct().Take(3)) + MoreSuffix(changed.Length, 3);

        // Build and re-discover first so test locations (and new tests in a changed test file) are current.
        var ct = CurrentToken();
        if (!await AcquireOpAsync("watch rebuild", ct)) return;
        var buildSw = Stopwatch.StartNew();
        var ready = await RebuildForWatchAsync(p, ct);
        var buildMs = Math.Round(buildSw.Elapsed.TotalMilliseconds, 1);
        if (!ready) return;

        WatchSelection selection;
        lock (Sync) selection = WatchSelectionLocked(p, changed);

        if (selection.Unknown.Count > 0)
        {
            var why = string.Join(", ", selection.Unknown.Select(Path.GetFileName).Distinct().Take(3));
            Log(p.Project.Id, LogLevels.Info, $"Watch: {names} changed → rerunning all tests (no impact data for {why})");
            await RunAsync(p.Project.Id, null, null, new AffectedRun { Strategy = "all", ChangedFiles = [.. changed] }, trigger: WatchTrigger,
                watchContext: new WatchContext { ChangedFiles = [.. changed], BuildMs = buildMs, Strategy = "all" });
            return;
        }
        var ids = selection.Ids;
        var strategy = selection.Strategies.Count == 1 ? selection.Strategies.First() : "mixed";
        var how = strategy switch { "testFile" => "tests in the changed file", "impactMap" => "impact map", _ => "test files + impact map" };
        if (selection.Details.Count > 0) how += "; " + string.Join(", ", selection.Details);
        if (ids.Count == 0)
        {
            Log(p.Project.Id, LogLevels.Info, $"Watch: {names} changed → no affected tests ({how})");
            return;
        }
        Log(p.Project.Id, LogLevels.Info, $"Watch: {names} changed → rerunning {ids.Count} affected test{Plural(ids.Count)} ({how})");
        await RunAsync(p.Project.Id, ids, null, new AffectedRun { Strategy = strategy, ChangedFiles = [.. changed] }, trigger: WatchTrigger,
            watchContext: new WatchContext { ChangedFiles = [.. changed], BuildMs = buildMs, Strategy = strategy });
    }

    /// <summary>Build + re-discover for a watch run; the caller acquired the op lock, released here.</summary>
    private async Task<bool> RebuildForWatchAsync(ProjectEntry p, CancellationToken ct)
    {
        try { return await BuildAsync(p, ct, onlyIfChanged: true) && await EnsureDiscoveredAsync(p, ct); }
        catch (Exception ex)
        {
            Log(p.Project.Id, LogLevels.Error, "Watch: " + ex.Message);
            return false;
        }
        finally { ReleaseOp(); }
    }

    /// <summary>The tests a watch run selects: test file → its tests, product file → the impact map; anything else is unknown.</summary>
    private sealed class WatchSelection
    {
        public HashSet<string> Ids { get; } = [];
        public HashSet<string> Strategies { get; } = [];
        /// <summary>Changed files with no test mapping: every test runs.</summary>
        public List<string> Unknown { get; } = [];
        public List<string> Details { get; } = [];
    }

    private WatchSelection WatchSelectionLocked(ProjectEntry p, string[] changed)
    {
        var byFile = new Dictionary<string, List<string>>(PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var t in _order.Where(k => k.ProjectId == p.Project.Id).Select(k => _tests[k]).Where(t => t.FilePath is not null))
        {
            var f = PathUtil.CanonicalizeOrSelf(t.FilePath!);
            if (!byFile.TryGetValue(f, out var list)) byFile[f] = list = [];
            list.Add(t.Id);
        }
        var selection = new WatchSelection();
        foreach (var path in changed)
        {
            var file = PathUtil.CanonicalizeOrSelf(path);
            if (byFile.TryGetValue(file, out var inFile))
            {
                selection.Ids.UnionWith(inFile);
                selection.Strategies.Add("testFile");
            }
            else if (CodeExtensions.Contains(Path.GetExtension(file)) && _impact.Affected(p.Project.Id, file, out var detail) is { } mapped)
            {
                selection.Ids.UnionWith(mapped.Where(id => _tests.ContainsKey((p.Project.Id, id))));
                QueueImpactRefinementLocked(mapped.Where(id => !_impact.IsPerTest(p.Project.Id, id)).Select(id => (p.Project.Id, id)));
                selection.Strategies.Add("impactMap");
                selection.Details.Add($"{Path.GetFileName(file)}: {detail}");
                _impact.AcceptChange(file);
            }
            else selection.Unknown.Add(path);
        }
        return selection;
    }

    /// <summary>
    /// Project files of the transitive ProjectReference closure (MSBuild gives us the direct references; deeper levels are
    /// read from the project files without evaluation, so conditional/property-based references are skipped).
    /// <paramref name="assembliesOnly"/>: only references whose assembly ends up in the test app (not
    /// ReferenceOutputAssembly="false", not an Aspire project resource).
    /// </summary>
    private static List<string> ProjectReferenceClosure(JsonNode? directRefs, bool assembliesOnly = false)
    {
        var cmp = PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(cmp);
        var queue = new Queue<string>(DirectReferences(directRefs, assembliesOnly));
        while (queue.TryDequeue(out var proj))
        {
            if (!File.Exists(proj) || !seen.Add(proj)) continue; // a missing project is no source root
            foreach (var r in ProjectReferencesOf(proj, assembliesOnly)) queue.Enqueue(r);
        }
        return [.. seen];
    }

    /// <summary>The project files of MSBuild's evaluated ProjectReference items.</summary>
    private static List<string> DirectReferences(JsonNode? directRefs, bool assembliesOnly)
    {
        var result = new List<string>();
        if (directRefs is not JsonArray arr) return result;
        foreach (var r in arr)
        {
            if (r?["FullPath"]?.GetValue<string>() is not { } full) continue;
            if (assembliesOnly && NotReferencedAssembly(r["ReferenceOutputAssembly"]?.GetValue<string>(), r["IsAspireProjectResource"]?.GetValue<string>())) continue;
            result.Add(full);
        }
        return result;
    }

    /// <summary>The ProjectReferences read from a project file (no evaluation: conditional/property-based ones are skipped).</summary>
    private static List<string> ProjectReferencesOf(string proj, bool assembliesOnly)
    {
        var refs = new List<string>();
        try
        {
            var dir = Path.GetDirectoryName(proj)!;
            var doc = XDocument.Load(proj);
            // An Aspire AppHost's ProjectReferences are resources (separate processes), unless marked otherwise.
            var appHost = IsAspireAppHost(doc);
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = (string?)el.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.Contains("$(")) continue;
                if (assembliesOnly && IsNotReferencedAssembly(el, appHost)) continue;
                refs.AddRange(include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part => Path.GetFullPath(Path.Combine(dir, part.Replace('\\', Path.DirectorySeparatorChar)))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { /* unreadable project file: no references from it */ }
        return refs;
    }

    private static bool IsNotReferencedAssembly(XElement projectReference, bool inAppHost) =>
        NotReferencedAssembly(Metadata(projectReference, "ReferenceOutputAssembly"),
            Metadata(projectReference, "IsAspireProjectResource") ?? (inAppHost ? "true" : null));

    private static bool NotReferencedAssembly(string? referenceOutputAssembly, string? aspireResource) =>
        string.Equals(referenceOutputAssembly?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
        || string.Equals(aspireResource?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsAspireAppHost(XDocument doc) =>
        ((string?)doc.Root?.Attribute("Sdk"))?.Contains("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase) == true
        || doc.Descendants().Any(e => (e.Name.LocalName == "Sdk" && ((string?)e.Attribute("Name"))?.Equals("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase) == true)
                                      || (e.Name.LocalName == "IsAspireHost" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));

    private static string? Metadata(XElement el, string name) =>
        (string?)el.Attribute(name) ?? el.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>Directories of <see cref="ProjectReferenceClosure"/>.</summary>
    private static List<string> ProjectReferenceClosureDirs(JsonNode? directRefs)
    {
        var cmp = PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        return ProjectReferenceClosure(directRefs).Select(p => Path.GetDirectoryName(p)!).Distinct(cmp).ToList();
    }
}

/// <summary>ExplorerState.impactMap</summary>
public sealed record ImpactMapInfo
{
    public int Files { get; init; }
    public int Tests { get; init; }
    /// <summary>Tests whose data comes from a coverage run of that test alone (the others have class-level data).</summary>
    public int PerTest { get; init; }
    /// <summary>The impactMap option: "class" | "test" | "auto".</summary>
    public string? Mode { get; init; }
    public string? BuiltAt { get; init; }
    public bool? Building { get; init; }
}

/// <summary>RunSummary.affected: set on watch-triggered runs and on "run affected" (git) runs.</summary>
public sealed record AffectedRun
{
    /// <summary>"testFile" | "impactMap" | "mixed" | "all" (watch); "git" (run affected, GitAffected.cs)</summary>
    public required string Strategy { get; init; }
    public required List<string> ChangedFiles { get; init; }
    /// <summary>git runs: "working" | "branch" | "range".</summary>
    public string? Base { get; init; }
    /// <summary>git runs: the base ref / range that was compared.</summary>
    public string? Ref { get; init; }
    /// <summary>git runs: number of tests the plan selected.</summary>
    public int? Tests { get; init; }
}
