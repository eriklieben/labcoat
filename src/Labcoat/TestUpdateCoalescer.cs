namespace Labcoat;

/// <summary>
/// Buffers <c>testsUpdated</c> payloads so a burst of per-test results goes out as one event at most every
/// <see cref="Interval"/>: updates are merged per (projectId, id), last write wins, in order of first appearance.
/// The owner flushes before publishing any other ordered event (runStarted, runFinished, projectStatus, ...) so the
/// client never sees a run finish before its last results. Not thread safe: the owner serializes access (Explorer.Sync).
/// </summary>
public sealed class TestUpdateCoalescer(TimeSpan interval)
{
    public TimeSpan Interval { get; } = interval;

    private readonly Dictionary<(string ProjectId, string Id), int> _index = [];
    private readonly List<TestNode> _pending = [];

    public int Count => _pending.Count;

    /// <summary>The (projectId, id) of the buffered updates.</summary>
    public IEnumerable<(string ProjectId, string Id)> Keys => _index.Keys;

    /// <summary>Adds (copies of) updated nodes. Returns true when the buffer was empty before (= schedule a flush).</summary>
    public bool Add(IEnumerable<TestNode> nodes)
    {
        var wasEmpty = _pending.Count == 0;
        foreach (var n in nodes)
        {
            var key = (n.ProjectId, n.Id);
            if (_index.TryGetValue(key, out var i)) _pending[i] = n;
            else
            {
                _index[key] = _pending.Count;
                _pending.Add(n);
            }
        }
        return wasEmpty && _pending.Count > 0;
    }

    /// <summary>Takes the buffered updates (empty list when nothing is pending).</summary>
    public List<TestNode> Drain()
    {
        if (_pending.Count == 0) return [];
        var list = new List<TestNode>(_pending);
        _pending.Clear();
        _index.Clear();
        return list;
    }

    /// <summary>Drops pending updates (a full state snapshot supersedes them).</summary>
    public void Clear()
    {
        _pending.Clear();
        _index.Clear();
    }
}
