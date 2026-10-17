using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// <c>testOutput</c>: console output of a test that is still running (see docs/contract.md, "Live console output").
/// <c>stream</c>: "stdout" / "stderr" = attributed exactly (MTP in-progress output, xUnit live output, Labcoat.Testing's
/// <c>LabcoatOutput</c>); "host" = a line the test host process wrote to its own stdout/stderr (<c>err</c>), attributed
/// by timing (best effort). <c>shared</c>: several tests of the project were running, the line may belong to another.
/// <c>seq</c> counts the events of one test execution from 1 (a new execution starts at 1 again).
/// </summary>
public sealed record TestOutputEvent(string ProjectId, string Id, string Stream, string Text, long Seq, bool? Shared = null, bool? Err = null)
    : ServerEvent;

/// <summary>GET /api/tests/live-output: what was streamed so far for a running test (for clients that join mid-test).</summary>
public sealed record LiveOutputSnapshot(string ProjectId, string Id, long Seq, List<LiveOutputChunk> Chunks, long? Omitted = null);

public sealed record LiveOutputChunk(string Stream, string Text, bool? Shared = null, bool? Err = null);

/// <summary>What the test app's output folder says about live output support (<see cref="LiveOutput.Detect"/>).</summary>
public sealed record LiveOutputSupport(bool XunitLiveOption, bool TUnit)
{
    /// <summary>A host that streams by itself (in-progress nodes with the output so far): no options to add, no host lines to attribute.</summary>
    public static LiveOutputSupport Streaming { get; } = new(false, TUnit: true);

    public static readonly LiveOutputSupport None = new(false, false);
}

/// <summary>
/// Live console output of running tests. Sources, most exact first:
/// <list type="number">
/// <item>MTP in-progress nodes that carry <c>standardOutput</c>/<c>standardError</c> (cumulative; TUnit with
/// <c>TUNIT_ENABLE_IDE_STREAMING=1</c>, about once a second) → <see cref="Partial"/>.</item>
/// <item>xUnit v3 <c>--show-live-output on</c>: <c>OUTPUT: [&lt;display name&gt;] text</c> lines on the host's stdout.</item>
/// <item>Labcoat.Testing's <c>LabcoatOutput</c>: <c>labcoat:live:&lt;base64 json&gt;</c> lines on the raw stdout, tagged with
/// the test's class / method / display name.</item>
/// <item>Any other line on the test host process's stdout/stderr ("host"), attributed by timing: MTP reports test starts
/// and ends in 200 ms batches, so a line is attributed <see cref="AttributionDelay"/> after it arrived, to the single
/// running test of that project, or to all of them (<c>shared</c>) when several run. A line that comes while no test of
/// its project is known to run waits up to <see cref="StartWait"/> for one (the start can be reported after the test's
/// first output); lines while no test runs are then dropped.</item>
/// </list>
/// Output is buffered per running test and flushed (<see cref="Drain"/>) at most every <see cref="FlushInterval"/>, at most
/// <see cref="MaxCharsPerFlush"/> characters per test and flush (the tail is kept). Thread safe; <see cref="Drain"/>,
/// <see cref="Started"/> and <see cref="Finished"/> are called by the Explorer under its lock, <see cref="HostLine"/> from
/// the process output readers.
/// </summary>
public sealed partial class LiveOutput
{
    public const string Marker = "labcoat:live:";
    public const string XunitPrefix = "OUTPUT: [";
    /// <summary>The stream of output attributed exactly (TestOutputEvent.stream).</summary>
    private const string StdOut = "stdout";
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan AttributionDelay = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(1);
    /// <summary>How long an exactly attributed line waits for its test to be reported as running.</summary>
    public static readonly TimeSpan IdentityWait = TimeSpan.FromSeconds(2);
    public const int MaxCharsPerFlush = 8 * 1024;
    /// <summary>Env var: "0" turns live output off (no extra test host arguments / variables, no events).</summary>
    public const string EnabledVariable = "LABCOAT_LIVE_OUTPUT";

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable(EnabledVariable) is not ("0" or "false");

    private sealed record Identity(string DisplayName, string? FullClassName, string? MethodName);

    private sealed class Segment(string stream, bool err, bool shared)
    {
        public string Stream { get; } = stream;
        public bool Err { get; } = err;
        public bool Shared { get; } = shared;
        public readonly StringBuilder Text = new();
    }

    private sealed class TestBuffer(string projectId, string id, Identity identity)
    {
        public string ProjectId { get; } = projectId;
        public string Id { get; } = id;
        public Identity Identity { get; } = identity;
        public long Seq;
        public int OutLen, ErrLen;               // cumulative MTP output already taken
        public readonly List<Segment> Pending = [];
        public int PendingChars;
        public readonly List<LiveOutputChunk> Sent = []; // snapshot: head + tail of what was sent
        public int SentChars, HeadChunks;
        public long Omitted;
    }

    /// <param name="TextFor">Exact lines: the text for this test, or null when the line is not this test's.</param>
    private sealed record QueuedLine(DateTime At, string ProjectId, string Text, bool Err, string? Stream, Func<TestBuffer, string?>? TextFor)
    {
        /// <summary>Host lines: no test of the project was known to run when it arrived (so it may wait for a start).</summary>
        public bool BeforeAnyTest { get; init; }
    }

    private sealed class ProjectMode
    {
        public bool ExactOnly;   // the framework streams itself (TUnit): ignore unattributed host lines
        public bool Xunit;       // parse "OUTPUT: [name] text"
    }

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private readonly Action? _flush;
    private readonly Timer? _timer;
    private bool _scheduled;
    private readonly Dictionary<(string ProjectId, string Id), TestBuffer> _running = [];
    private readonly Dictionary<string, ProjectMode> _modes = [];
    private readonly Queue<QueuedLine> _hostQueue = new();
    private readonly List<QueuedLine> _exactQueue = [];
    private const int MaxQueued = 10_000;

    /// <param name="flush">Called by the flush timer (the owner takes its lock and publishes <see cref="Drain"/>); null = no timer (tests).</param>
    /// <param name="clock">UTC clock (tests).</param>
    public LiveOutput(Action? flush = null, Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _flush = flush;
        if (flush is not null) _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ------------------------------------------------------------------ project / test lifecycle

    /// <summary>How a project's test host reports output (set when its process starts).</summary>
    public void Configure(string projectId, LiveOutputSupport support)
    {
        lock (_gate) _modes[projectId] = new ProjectMode { ExactOnly = support.TUnit, Xunit = support.XunitLiveOption };
    }

    private static string? FullClassNameOf(TestNode t)
    {
        if (t.ClassName is null) return null;
        return string.IsNullOrEmpty(t.Namespace) ? t.ClassName : t.Namespace + "." + t.ClassName;
    }

    /// <summary>A test started (first in-progress update): a new execution with an empty buffer.</summary>
    public void Started(TestNode t)
    {
        var full = FullClassNameOf(t);
        lock (_gate) _running[(t.ProjectId, t.Id)] = new TestBuffer(t.ProjectId, t.Id, new Identity(t.DisplayName, full, t.MethodName));
        Schedule(); // queued exact lines may be waiting for this test
    }

    /// <summary>The test reported its final result (or the run ended): its pending output is dropped, the final output is authoritative.</summary>
    public void Finished(string projectId, string id)
    {
        lock (_gate) _running.Remove((projectId, id));
    }

    /// <summary>Forget every running test and queued line (end of a run).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _running.Clear();
            _hostQueue.Clear();
            _exactQueue.Clear();
        }
    }

    public bool IsRunning(string projectId, string id)
    {
        lock (_gate) return _running.ContainsKey((projectId, id));
    }

    // ------------------------------------------------------------------ sources

    /// <summary>An in-progress node with output: MTP sends the output so far (cumulative), we take the new part.</summary>
    public void Partial(string projectId, string id, string? stdOut, string? stdErr)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue((projectId, id), out var b)) return;
            if (stdOut is { } o && o.Length > b.OutLen)
            {
                Append(b, StdOut, o[b.OutLen..], false, false);
                b.OutLen = o.Length;
            }
            if (stdErr is { } e && e.Length > b.ErrLen)
            {
                Append(b, "stderr", e[b.ErrLen..], false, false);
                b.ErrLen = e.Length;
            }
        }
        Schedule();
    }

    /// <summary>A line the test host process wrote to its stdout (<paramref name="err"/> false) or stderr.</summary>
    public void HostLine(string projectId, string line, bool err)
    {
        var now = _clock();
        lock (_gate)
        {
            var marker = line.IndexOf(Marker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                if (TryParseMarker(line[(marker + Marker.Length)..], out var stream, out var text, out var cls, out var method, out var name))
                    Enqueue(_exactQueue, new QueuedLine(now, projectId, text, false, stream, b => MatchesIdentity(b, cls, method, name) ? text : null));
                return;
            }
            _modes.TryGetValue(projectId, out var mode);
            if (mode?.Xunit == true && line.StartsWith(XunitPrefix, StringComparison.Ordinal))
            {
                var rest = line[XunitPrefix.Length..];
                Enqueue(_exactQueue, new QueuedLine(now, projectId, rest, false, StdOut,
                    b => rest.StartsWith(b.Identity.DisplayName + "] ", StringComparison.Ordinal) ? rest[(b.Identity.DisplayName.Length + 2)..] : null));
                return;
            }
            if (mode?.ExactOnly == true || IsNoise(line)) return;
            if (_hostQueue.Count < MaxQueued)
                _hostQueue.Enqueue(new QueuedLine(now, projectId, line, err, null, null)
                {
                    BeforeAnyTest = !_running.Values.Any(b => b.ProjectId == projectId),
                });
        }
        Schedule();
    }

    private static void Enqueue(List<QueuedLine> q, QueuedLine l)
    {
        if (q.Count < MaxQueued) q.Add(l);
    }

    private static bool MatchesIdentity(TestBuffer b, string? cls, string? method, string? name) =>
        (cls is null || b.Identity.FullClassName == cls) && (method is null || b.Identity.MethodName == method)
        && (name is null || b.Identity.DisplayName == name);

    /// <summary>MTP / adapter lines that are not test output (printed while a test may be reported as running).</summary>
    [GeneratedRegex(
        @"^(Connecting to client host|Test Parallelization enabled for |NUnit Adapter |Running (all|selected) tests in |\s*NUnit3TestExecutor |MSTest v\d|xUnit\.net v3 |Microsoft Testing Platform collects usage data)",
        RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Noise();

    internal static bool IsNoise(string line) => Noise().IsMatchOrFalse(line);

    // ------------------------------------------------------------------ Labcoat.Testing marker

    /// <summary>The marker line Labcoat.Testing's <c>LabcoatOutput</c> writes: <c>labcoat:live:</c> + base64(UTF-8 JSON).</summary>
    public static string FormatMarker(string stream, string text, string? fullClassName, string? methodName, string? displayName)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?>
        {
            ["s"] = stream, ["t"] = text, ["c"] = fullClassName, ["m"] = methodName, ["n"] = displayName,
        });
        return Marker + Convert.ToBase64String(json);
    }

    internal static bool TryParseMarker(string payload, out string stream, out string text, out string? cls, out string? method, out string? name)
    {
        stream = StdOut;
        text = "";
        cls = method = name = null;
        try
        {
            var end = payload.IndexOfAny([' ', '\t', '\r']);
            var bytes = Convert.FromBase64String(end < 0 ? payload : payload[..end]);
            using var doc = JsonDocument.Parse(bytes);
            var r = doc.RootElement;
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            stream = Str("s") == "err" || Str("s") == "stderr" ? "stderr" : StdOut;
            text = Str("t") ?? "";
            cls = Str("c");
            method = Str("m");
            name = Str("n");
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ flushing

    /// <summary>Attributes queued lines and returns the events to publish (one per test and stream run, in order).</summary>
    public List<TestOutputEvent> Drain()
    {
        var now = _clock();
        var events = new List<TestOutputEvent>();
        lock (_gate)
        {
            DrainExactLocked(now);
            DrainHostLocked(now);
            foreach (var b in _running.Values.Where(b => b.Pending.Count > 0)) FlushPendingLocked(b, events);
        }
        return events;
    }

    /// <summary>Exact lines: as soon as their test runs; dropped after <see cref="IdentityWait"/>.</summary>
    private void DrainExactLocked(DateTime now)
    {
        var i = 0;
        while (i < _exactQueue.Count)
        {
            var l = _exactQueue[i];
            var targets = _running.Values.Where(b => b.ProjectId == l.ProjectId)
                .Select(b => (Buffer: b, Text: l.TextFor!(b))).Where(x => x.Text is not null).ToList();
            if (targets.Count == 0 && now - l.At < IdentityWait)
            {
                i++;
                continue;
            }
            _exactQueue.RemoveAt(i);
            foreach (var (b, text) in targets) Append(b, l.Stream!, EnsureNewline(text!), false, targets.Count > 1);
        }
    }

    /// <summary>
    /// Host lines: after <see cref="AttributionDelay"/>, to the tests running then; with none running yet, wait up to
    /// <see cref="StartWait"/> for one (MTP may report the start after the test's first output), in order.
    /// </summary>
    private void DrainHostLocked(DateTime now)
    {
        List<QueuedLine>? waiting = null;
        while (_hostQueue.TryPeek(out var l) && now - l.At >= AttributionDelay)
        {
            _hostQueue.Dequeue();
            var targets = _running.Values.Where(b => b.ProjectId == l.ProjectId).ToList();
            // only a line from before any test started waits; one from a test that has since finished must not
            // move on to the next test
            if (targets.Count == 0 && l.BeforeAnyTest && now - l.At < StartWait)
            {
                (waiting ??= []).Add(l);
                continue;
            }
            foreach (var b in targets) Append(b, "host", l.Text + "\n", l.Err, targets.Count > 1);
        }
        if (waiting is null) return;
        var rest = _hostQueue.ToList();
        _hostQueue.Clear();
        foreach (var l in waiting.Concat(rest)) _hostQueue.Enqueue(l);
    }

    /// <summary>A test's pending output becomes events (one per stream run) and is remembered for late joiners.</summary>
    private static void FlushPendingLocked(TestBuffer b, List<TestOutputEvent> events)
    {
        CapPending(b);
        foreach (var s in b.Pending)
        {
            var text = s.Text.ToString();
            events.Add(new TestOutputEvent(b.ProjectId, b.Id, s.Stream, text, ++b.Seq, s.Shared ? true : null, s.Err ? true : null));
            Remember(b, new LiveOutputChunk(s.Stream, text, s.Shared ? true : null, s.Err ? true : null));
        }
        b.Pending.Clear();
        b.PendingChars = 0;
    }

    private static string EnsureNewline(string s) => s.EndsWith('\n') ? s : s + "\n";

    /// <summary>Something waits to be flushed (pending output or queued lines).</summary>
    public bool HasWork
    {
        get
        {
            lock (_gate) return _hostQueue.Count > 0 || _exactQueue.Count > 0 || _running.Values.Any(b => b.Pending.Count > 0);
        }
    }

    private static void Append(TestBuffer b, string stream, string text, bool err, bool shared)
    {
        text = WithoutTraceMarkers(text);
        if (text.Length == 0) return;
        var last = b.Pending.Count > 0 ? b.Pending[^1] : null;
        if (last is null || last.Stream != stream || last.Err != err || last.Shared != shared)
        {
            last = new Segment(stream, err, shared);
            b.Pending.Add(last);
        }
        last.Text.Append(text);
        b.PendingChars += text.Length;
    }

    /// <summary>
    /// Labcoat.Testing writes <c>labcoat:traceId=&lt;hex&gt;</c> into each test's output for the "View trace" link (read
    /// from the final result). It is plumbing, not output: the live view leaves those lines out.
    /// </summary>
    internal const string TraceIdMarker = "labcoat:traceId=";

    internal static string WithoutTraceMarkers(string text)
    {
        if (!text.Contains(TraceIdMarker, StringComparison.Ordinal)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(TraceIdMarker, StringComparison.Ordinal)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        // keep the trailing newline the chunk had (Split leaves an empty last item for it)
        return text.EndsWith('\n') && sb.Length > 0 && sb[^1] != '\n' ? sb.Append('\n').ToString() : sb.ToString();
    }

    /// <summary>More than <see cref="MaxCharsPerFlush"/> since the last flush: keep the tail, with a marker for what was skipped.</summary>
    private static void CapPending(TestBuffer b)
    {
        if (b.PendingChars <= MaxCharsPerFlush) return;
        var skip = b.PendingChars - MaxCharsPerFlush;
        var skipped = skip;
        while (skip > 0 && b.Pending.Count > 0)
        {
            var s = b.Pending[0];
            if (s.Text.Length <= skip)
            {
                skip -= s.Text.Length;
                b.Pending.RemoveAt(0);
            }
            else
            {
                s.Text.Remove(0, skip);
                skip = 0;
            }
        }
        var first = b.Pending.Count > 0 ? b.Pending[0] : new Segment("host", false, false);
        var marker = new Segment(first.Stream, first.Err, first.Shared);
        marker.Text.Append($"… [{skipped:N0} characters skipped: the test writes faster than the live view shows] …\n");
        b.Pending.Insert(0, marker);
        b.PendingChars = MaxCharsPerFlush + marker.Text.Length;
    }

    /// <summary>The snapshot keeps the first <see cref="TestOutput.HeadChars"/> and the last chunks up to <see cref="TestOutput.MaxChars"/>.</summary>
    private static void Remember(TestBuffer b, LiveOutputChunk c)
    {
        b.Sent.Add(c);
        b.SentChars += c.Text.Length;
        if (b.SentChars - c.Text.Length < TestOutput.HeadChars) b.HeadChunks = b.Sent.Count;
        while (b.SentChars > TestOutput.MaxChars && b.Sent.Count > b.HeadChunks + 1)
        {
            var drop = b.Sent[b.HeadChunks];
            b.Sent.RemoveAt(b.HeadChunks);
            b.SentChars -= drop.Text.Length;
            b.Omitted += drop.Text.Length;
        }
    }

    /// <summary>What was streamed so far for a running test; null when it is not running.</summary>
    public LiveOutputSnapshot? Snapshot(string? projectId, string id)
    {
        lock (_gate)
        {
            var b = _running.Values.FirstOrDefault(x => x.Id == id && (projectId is null || x.ProjectId == projectId));
            if (b is null) return null;
            var chunks = new List<LiveOutputChunk>(b.Sent);
            if (b.Omitted > 0)
                chunks.Insert(b.HeadChunks, new LiveOutputChunk("host", $"… [{b.Omitted:N0} characters omitted] …\n"));
            return new LiveOutputSnapshot(b.ProjectId, b.Id, b.Seq, chunks, b.Omitted > 0 ? b.Omitted : null);
        }
    }

    private void Schedule()
    {
        if (_timer is null) return;
        lock (_gate)
        {
            if (_scheduled) return;
            _scheduled = true;
        }
        _timer.Change(FlushInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        lock (_gate) _scheduled = false;
        try { _flush?.Invoke(); }
        catch { /* never let a timer callback crash the process */ }
        if (HasWork) Schedule();
    }

    // ------------------------------------------------------------------ detection

    private static readonly Dictionary<string, (DateTime Stamp, LiveOutputSupport Support)> s_detected = [];

    /// <summary>
    /// Looks at the test app's output folder: xUnit v3 runners that know <c>--show-live-output</c> (the option string in
    /// an <c>xunit.v3*.dll</c>) and TUnit (<c>TUnit.Engine.dll</c>). Cached per folder and file times.
    /// </summary>
    public static LiveOutputSupport Detect(string? outputDir)
    {
        if (!Enabled || outputDir is null || !Directory.Exists(outputDir)) return LiveOutputSupport.None;
        try
        {
            var xunit = Directory.GetFiles(outputDir, "xunit.v3*.dll");
            var tunit = Path.Combine(outputDir, "TUnit.Engine.dll");
            var stamp = xunit.Select(File.GetLastWriteTimeUtc).Append(File.Exists(tunit) ? File.GetLastWriteTimeUtc(tunit) : default)
                .DefaultIfEmpty().Max();
            lock (s_detected)
                if (s_detected.TryGetValue(outputDir, out var c) && c.Stamp == stamp) return c.Support;
            var needle = Encoding.Unicode.GetBytes("show-live-output");
            var support = new LiveOutputSupport(xunit.Any(f => Contains(File.ReadAllBytes(f), needle)), File.Exists(tunit));
            lock (s_detected) s_detected[outputDir] = (stamp, support);
            return support;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LiveOutputSupport.None;
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>TUnit: publish in-progress updates with the output so far (about once a second).</summary>
    public const string TUnitStreamingVariable = "TUNIT_ENABLE_IDE_STREAMING";

    /// <summary>Extra test host arguments / environment for live output.</summary>
    public static (string[] Args, Dictionary<string, string> Env) HostOptions(LiveOutputSupport s)
    {
        var env = new Dictionary<string, string>();
        if (s.TUnit) env[TUnitStreamingVariable] = "1";
        return (s.XunitLiveOption ? ["--show-live-output", "on"] : [], env);
    }
}
