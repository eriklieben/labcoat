namespace Labcoat;

/// <summary>
/// Watches the output folder of a warm test host for assemblies that are rewritten in place while the process runs.
/// <para>
/// The runtime maps an assembly file into memory (Linux, macOS). When something then writes new bytes into the same
/// file, instead of replacing it, the process sees a mix of old and new metadata: tests fail with
/// <c>BadImageFormatException</c> "Index not found (0x80131124)" or load garbage type names, until the process restarts.
/// Two writers do that: a build that copies over a loaded file (MSBuild's copy truncates and writes), and code
/// coverage, which on Linux / macOS instruments the product assemblies in place (static instrumentation) and puts the
/// original back afterwards (delete + rename). After the restore the file content and time stamp are the original
/// again, so the content fingerprint of the folder can't tell; this watch can.
/// </para>
/// <para>
/// An in-place write counts once the writes have settled and the file's content differs from what it was when the
/// host started (a build that rewrites identical bytes keeps the host). In strict mode (<see cref="BeginStrict"/>,
/// while Labcoat runs coverage) any in-place write counts: the instrumented bytes are gone again by the end.
/// </para>
/// <para>
/// Windows doesn't let anyone write an assembly a process has loaded (a sharing violation; a build fails to copy over it,
/// and Labcoat stops the warm host before its own builds), and coverage instruments dynamically there. So the watch only
/// fires for assemblies the host hasn't loaded yet; restarting is right for those too (the host would load the new
/// bytes next to old ones).
/// </para>
/// </summary>
internal sealed class OutputWatch : IDisposable
{
    private readonly string _dir;
    private readonly IReadOnlyDictionary<string, long> _hashes;
    private readonly Func<string, long> _hash;
    private readonly FileSystemWatcher? _watcher;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal); // lock (_pending)
    private readonly Timer _settle;
    private readonly TimeSpan _settleDelay;
    private int _strict;
    private volatile string? _modified;
    private int _notified;
    private volatile bool _disposed;

    /// <summary>Called once, on a thread-pool thread, when a rewritten assembly is detected (its file name).</summary>
    public Action<string>? Modified { get; set; }

    /// <param name="directory">The test app's output directory.</param>
    /// <param name="hashes">Content hash of every top-level file by name, taken before the process started.</param>
    /// <param name="hash">Content hash of a file (path).</param>
    /// <param name="settleDelay">How long writes must have stopped before a file is compared (default 300 ms).</param>
    public OutputWatch(string directory, IReadOnlyDictionary<string, long> hashes, Func<string, long> hash, TimeSpan? settleDelay = null)
    {
        _dir = directory;
        _hashes = hashes;
        _hash = hash;
        _settleDelay = settleDelay ?? TimeSpan.FromMilliseconds(300);
        _settle = new Timer(_ => Evaluate(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(directory, "*.dll")
            {
                NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, e) => OnWrite(e.Name);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _watcher?.Dispose();
            _watcher = null; // no watch (e.g. out of inotify instances): the content fingerprint still catches rebuilds
        }
    }

    /// <summary>True when the folder is watched.</summary>
    public bool IsWatching => _watcher is not null;

    /// <summary>The first assembly found rewritten in place (file name), after settling pending writes; null when none.</summary>
    public string? Check()
    {
        Evaluate();
        return _modified;
    }

    /// <summary>The first assembly found rewritten in place so far (no evaluation).</summary>
    public string? Rewritten => _modified;

    /// <summary>Strict mode until disposed: every in-place write of an assembly counts (code coverage runs).</summary>
    public IDisposable BeginStrict()
    {
        Interlocked.Increment(ref _strict);
        return new EndStrict(this);
    }

    private sealed class EndStrict(OutputWatch watch) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref watch._strict);
        }
    }

    /// <summary>An in-place write of <paramref name="name"/> (watcher event; also called by tests).</summary>
    internal void OnWrite(string? name)
    {
        if (_disposed || name is null || _modified is not null) return;
        if (Volatile.Read(ref _strict) > 0 && _hashes.ContainsKey(name))
        {
            Flag(name);
            return;
        }
        lock (_pending) _pending.Add(name);
        try { _settle.Change(_settleDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { /* disposed: the watch has stopped */ }
    }

    private void Evaluate()
    {
        string[] names;
        lock (_pending)
        {
            names = [.. _pending];
            _pending.Clear();
        }
        foreach (var name in names)
        {
            // A new file is no rewrite of a loaded one (the content fingerprint covers it).
            if (_disposed || _modified is not null || !_hashes.TryGetValue(name, out var before)) continue;
            var path = Path.Combine(_dir, name);
            long now;
            try { now = File.Exists(path) ? _hash(path) : before ^ 1; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { now = before ^ 1; }
            if (now != before) Flag(name);
        }
    }

    private void Flag(string name)
    {
        _modified ??= name;
        var first = _modified; // set once: the first assembly rewritten
        if (Interlocked.Exchange(ref _notified, 1) == 0 && Modified is { } callback && !_disposed)
            ThreadPool.QueueUserWorkItem(_ => callback(first));
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _settle.Dispose();
    }
}
