namespace Labcoat;

/// <summary>
/// Per-test console output limits. Live results keep at most <see cref="MaxChars"/> per stream (the first
/// <see cref="HeadChars"/> and the last <see cref="MaxChars"/> - <see cref="HeadChars"/> characters, joined by a marker
/// line); the full text of cut outputs is kept in a bounded in-memory store (<see cref="OutputStore"/>) and served by
/// <c>GET /api/tests/output</c>.
/// </summary>
public static class TestOutput
{
    public const int MaxChars = 64 * 1024;
    public const int HeadChars = 16 * 1024;

    /// <summary>The text as is when it fits, else head + marker + tail.</summary>
    public static string? Cap(string? text, out bool truncated)
    {
        truncated = false;
        if (text is null || text.Length <= MaxChars) return text;
        truncated = true;
        var tail = MaxChars - HeadChars;
        var cut = text.Length - HeadChars - tail;
        return text[..HeadChars] + $"\n… [{cut:N0} characters omitted; full output: GET /api/tests/output] …\n" + text[^tail..];
    }
}

/// <summary>Full stdOut/stdErr of tests whose live output was cut; least recently stored entries go first beyond the budget.</summary>
public sealed class OutputStore(long maxChars = 32L * 1024 * 1024)
{
    private readonly object _gate = new();
    private readonly LinkedList<(string ProjectId, string Id)> _lru = new();
    private readonly Dictionary<(string, string), (string? StdOut, string? StdErr, LinkedListNode<(string, string)> Node)> _entries = [];
    private long _size;

    public void Put(string projectId, string id, string? stdOut, string? stdErr)
    {
        lock (_gate)
        {
            Remove((projectId, id));
            var size = (long)(stdOut?.Length ?? 0) + (stdErr?.Length ?? 0);
            if (size > maxChars) return; // a single giant output is not retained (the capped text is all there is)
            var node = _lru.AddLast((projectId, id));
            _entries[(projectId, id)] = (stdOut, stdErr, node);
            _size += size;
            while (_size > maxChars && _lru.First is { } oldest) Remove(oldest.Value);
        }
    }

    public void Remove(string projectId, string id) { lock (_gate) Remove((projectId, id)); }

    private void Remove((string, string) key)
    {
        if (!_entries.Remove(key, out var e)) return;
        _lru.Remove(e.Node);
        _size -= (long)(e.StdOut?.Length ?? 0) + (e.StdErr?.Length ?? 0);
    }

    public bool TryGet(string projectId, string id, out string? stdOut, out string? stdErr)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((projectId, id), out var e))
            {
                stdOut = e.StdOut;
                stdErr = e.StdErr;
                return true;
            }
        }
        stdOut = stdErr = null;
        return false;
    }

    public long Size { get { lock (_gate) return _size; } }
}
