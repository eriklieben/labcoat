namespace Labcoat;

/// <summary>
/// Turns coalesced test updates into wire events (contract "Round 7"): a full entry (<c>testsUpdated</c>) for a test
/// whose structure changed (name, location, traits) or that the clients don't know yet, and a compact patch
/// (<c>testResults</c>) with only the result fields that changed for everything else — during a run that is nearly
/// every update, so the bulk of the run traffic shrinks to ids + outcome / duration / time.
/// </summary>
/// <remarks>
/// Patches are computed against a mirror: the copy of every test that all clients have, i.e. the one last broadcast
/// (in a <c>state</c> snapshot or by a flush). A client that connects in between gets a snapshot of the current
/// tests, which can be ahead of the mirror only for tests with updates pending in the coalescer; those are sent as
/// full entries in the next flush (<see cref="ForceFull"/>), so a client never applies a patch to a different base.
/// Not thread safe: the owner serializes access (Explorer.Sync).
/// </remarks>
public sealed class TestUpdateEncoder
{
    /// <summary>History entries a client keeps per test (the host's cap).</summary>
    public const int MaxHistory = HistoryStore.MaxRuns;

    private Dictionary<(string ProjectId, string Id), TestNode> _sent = [];
    private readonly HashSet<(string ProjectId, string Id)> _forceFull = [];

    public int Count => _sent.Count;

    /// <summary>A <c>state</c> snapshot with these (immutable) copies was broadcast to every client.</summary>
    public void Reset(IEnumerable<TestNode> snapshot)
    {
        var sent = new Dictionary<(string, string), TestNode>(_sent.Count);
        foreach (var t in snapshot) sent[(t.ProjectId, t.Id)] = t;
        _sent = sent;
        _forceFull.Clear();
    }

    /// <summary>A client got its own snapshot while these tests had updates pending: send them in full next time.</summary>
    public void ForceFull(IEnumerable<(string ProjectId, string Id)> keys) => _forceFull.UnionWith(keys);

    /// <summary>
    /// Splits one flush into full entries and (previous, current) pairs for patches; the mirror then holds
    /// <paramref name="nodes"/> (copies that are never mutated).
    /// </summary>
    public (List<TestNode> Full, List<(TestNode Prev, TestNode Next)> Changed) Split(List<TestNode> nodes)
    {
        var full = new List<TestNode>();
        var changed = new List<(TestNode, TestNode)>(nodes.Count);
        foreach (var n in nodes)
        {
            var key = (n.ProjectId, n.Id);
            if (_sent.TryGetValue(key, out var prev) && !_forceFull.Contains(key) && prev.SameShape(n)) changed.Add((prev, n));
            else full.Add(n);
            _sent[key] = n;
        }
        _forceFull.Clear();
        return (full, changed);
    }

    /// <summary>The <c>testResults</c> event for <paramref name="changed"/> (null when nothing changed for the client).</summary>
    /// <param name="lean">Lean clients: no console output (<c>hasOutput</c> instead), like <see cref="TestNode.LeanCopy"/>.</param>
    public static TestResultsEvent? Patches(List<(TestNode Prev, TestNode Next)> changed, bool lean)
    {
        var projects = new List<ProjectResults>();
        Dictionary<string, List<TestResultPatch>>? byProject = null;
        string? runId = null;
        string? lastProject = null;
        List<TestResultPatch>? current = null;
        foreach (var (prev, next) in changed)
        {
            var p = Diff(prev, next, lean, ref runId);
            if (p is null) continue;
            if (next.ProjectId != lastProject)
            {
                byProject ??= [];
                if (!byProject.TryGetValue(next.ProjectId, out current))
                {
                    byProject[next.ProjectId] = current = [];
                    projects.Add(new ProjectResults(next.ProjectId, current));
                }
                lastProject = next.ProjectId;
            }
            current!.Add(p);
        }
        return projects.Count == 0 ? null : new TestResultsEvent(projects, runId);
    }

    /// <summary>The patch from <paramref name="a"/> to <paramref name="b"/>; null when nothing a client sees changed.</summary>
    /// <param name="runId">The event's run id for <see cref="TestResultPatch.Recorded"/> (set by the first one).</param>
    public static TestResultPatch? Diff(TestNode a, TestNode b, bool lean, ref string? runId)
    {
        var d = new FieldDiff();
        var outcome = a.Outcome == b.Outcome ? (Outcome?)null : b.Outcome;
        var duration = d.PickV(a.DurationMs, b.DurationMs, "durationMs");
        var message = d.Pick(a.ErrorMessage, b.ErrorMessage, "errorMessage");
        var errorType = d.Pick(a.ErrorType, b.ErrorType, "errorType");
        var stack = d.Pick(a.StackTrace, b.StackTrace, "stackTrace");
        string? stdOut = null, stdErr = null;
        bool? hasOutput = null;
        if (lean) hasOutput = d.PickV(HasOutputOf(a), HasOutputOf(b), "hasOutput");
        else
        {
            stdOut = d.Pick(a.StdOut, b.StdOut, "stdOut");
            stdErr = d.Pick(a.StdErr, b.StdErr, "stdErr");
        }
        var truncated = d.PickV(a.OutputTruncated, b.OutputTruncated, "outputTruncated");
        var lastRunAt = d.Pick(a.LastRunAt, b.LastRunAt, "lastRunAt");
        var traceId = d.Pick(a.TraceId, b.TraceId, "traceId");
        var stale = d.PickV(a.Stale, b.Stale, "stale");
        var flaky = d.PickV(a.Flaky, b.Flaky, "flaky");
        var snapshot = d.PickV(a.HasSnapshotMismatch, b.HasSnapshotMismatch, "hasSnapshotMismatch");
        var (history, add, recorded) = DiffHistory(a, b, d, ref runId);
        var clear = d.Clear;

        if (outcome is null && duration is null && message is null && errorType is null && stack is null && stdOut is null && stdErr is null
            && hasOutput is null && truncated is null && lastRunAt is null && traceId is null && stale is null && flaky is null && snapshot is null
            && history is null && add is null && recorded is null && clear is null)
            return null;
        return new TestResultPatch
        {
            Id = b.Id,
            Outcome = outcome,
            DurationMs = duration,
            ErrorMessage = message,
            ErrorType = errorType,
            StackTrace = stack,
            StdOut = stdOut,
            StdErr = stdErr,
            HasOutput = hasOutput,
            OutputTruncated = truncated,
            LastRunAt = lastRunAt,
            TraceId = traceId,
            Stale = stale,
            Flaky = flaky,
            HasSnapshotMismatch = snapshot,
            History = history,
            HistoryAdd = add,
            Recorded = recorded,
            Clear = clear,
        };
    }

    /// <summary>The changed fields of a patch: the new value, null when unchanged; a field that became null is listed in <see cref="Clear"/>.</summary>
    private sealed class FieldDiff
    {
        public List<string>? Clear { get; private set; }

        public T? Pick<T>(T? x, T? y, string name) where T : class
        {
            if (Equals(x, y)) return null;
            if (y is null) (Clear ??= []).Add(name);
            return y;
        }

        public T? PickV<T>(T? x, T? y, string name) where T : struct
        {
            if (Nullable.Equals(x, y)) return null;
            if (y is null) (Clear ??= []).Add(name);
            return y;
        }

        public void Cleared(string name) => (Clear ??= []).Add(name);
    }

    /// <summary>
    /// The history change: the whole history, one appended entry, or <c>recorded</c> (the entry is the test's own result
    /// of run <paramref name="runId"/>, so the client derives it).
    /// </summary>
    private static (List<HistoryEntry>? History, HistoryEntry? Add, bool? Recorded) DiffHistory(TestNode a, TestNode b, FieldDiff d, ref string? runId)
    {
        if (ReferenceEquals(a.History, b.History) || SameHistory(a.History, b.History)) return (null, null, null);
        if (b.History is not { Count: > 0 } h)
        {
            d.Cleared("history");
            return (null, null, null);
        }
        if (!IsAppend(a.History, h)) return (h, null, null);
        var e = h[^1];
        if (e.RunId is not null && e.Flaky is null && e.Outcome == b.Outcome && SameDuration(e.DurationMs, b.DurationMs) && e.At == b.LastRunAt
            && (runId is null || runId == e.RunId))
        {
            runId = e.RunId;
            return (null, null, true);
        }
        return (null, e, null);
    }

    /// <summary>
    /// The same duration value: an identity check (is this history entry the test's own result?), not a tolerance. Same
    /// as <c>==</c> on <c>double?</c>: both null, or equal values; NaN never equals anything.
    /// </summary>
    private static bool SameDuration(double? a, double? b)
    {
        if (a is not { } x || b is not { } y) return a is null && b is null;
        return !double.IsNaN(x) && x.CompareTo(y) == 0;
    }

    private static bool? HasOutputOf(TestNode t) => t.StdOut is { Length: > 0 } || t.StdErr is { Length: > 0 } ? true : null;

    private static bool SameHistory(List<HistoryEntry>? a, List<HistoryEntry>? b) =>
        (a is not { Count: > 0 } && b is not { Count: > 0 }) || (a is not null && b is not null && a.SequenceEqual(b));

    /// <summary><paramref name="b"/> is <paramref name="a"/> plus one entry (the oldest dropped at the cap).</summary>
    public static bool IsAppend(List<HistoryEntry>? a, List<HistoryEntry> b)
    {
        var p = a?.Count ?? 0;
        if (b.Count == p + 1) return p == 0 || b.Take(p).SequenceEqual(a!);
        // at the cap: a[1..] == b[..^1]
        return p > 0 && b.Count == p && b.Count >= MaxHistory && a!.Skip(1).SequenceEqual(b.Take(p - 1));
    }
}
