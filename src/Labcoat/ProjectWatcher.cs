namespace Labcoat;

/// <summary>
/// Watches the workspace roots for changes that can add or remove test projects: project files (<c>*.csproj</c>,
/// <c>*.fsproj</c>, <c>*.vbproj</c>), solutions (<c>*.sln</c>, <c>*.slnx</c>, <c>*.slnf</c>) and <c>Directory.*.props</c> /
/// <c>Directory.*.targets</c>. Changes are debounced: a burst (a branch switch, a <c>dotnet new</c>) is reported once,
/// after <see cref="Quiet"/> without further changes (at most <see cref="MaxDelay"/> after the first one). bin, obj,
/// node_modules and dot-folders are ignored. With runner packages (docs/runners.md) also their project files: package.json
/// and the runners' config files (<see cref="ProjectFileNames"/>).
/// </summary>
public sealed class ProjectWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action<IReadOnlyCollection<string>> _changed;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private DateTime? _firstPending;
    private bool _disposed;

    /// <summary>How long changes must settle before they are reported.</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>Longest delay after the first change of a burst.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>More file names that can add or remove a test project (runner packages' package.json and config files).</summary>
    public IReadOnlySet<string> ProjectFileNames { get; init; } = new HashSet<string>();

    /// <param name="roots">Directories watched recursively (missing ones are skipped).</param>
    /// <param name="changed">Called (on a timer thread) with the changed paths of a burst.</param>
    /// <param name="warn">A root that could not be watched (e.g. the inotify limit).</param>
    public ProjectWatcher(IEnumerable<string> roots, Action<IReadOnlyCollection<string>> changed, Action<string>? warn = null)
    {
        _changed = changed;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var root in roots.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                };
                w.Created += (_, e) => OnChange(e.FullPath, structural: true);
                w.Deleted += (_, e) => OnChange(e.FullPath, structural: true);
                w.Changed += (_, e) => OnChange(e.FullPath);
                w.Renamed += (_, e) =>
                {
                    OnChange(e.OldFullPath, structural: true);
                    OnChange(e.FullPath, structural: true);
                };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                warn?.Invoke($"Could not watch {root} for new or removed test projects ({ex.Message}); use Rediscover after adding one.");
            }
        }
    }

    public int WatchedRoots => _watchers.Count;

    /// <summary>A file that can change the set of test projects (a deleted folder counts too: it may have held one).</summary>
    public static bool IsRelevant(string relativePath)
    {
        if (IsIgnored(relativePath)) return false;
        var path = relativePath;
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx" or ".slnf") return true;
        return name.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase) && ext is ".props" or ".targets";
    }

    /// <summary>A solution or Directory.*.props/targets change: it can affect every project.</summary>
    public static bool AffectsAll(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".sln" or ".slnx" or ".slnf" or ".props" or ".targets";
    }

    /// <summary>Under bin, obj, node_modules or a dot-folder (of the path below the watched root).</summary>
    private static bool IsIgnored(string relativePath)
    {
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "bin" or "obj" or "node_modules") return true;
            if (i < parts.Length - 1 && parts[i].Length > 1 && parts[i][0] == '.' && parts[i] != "..") return true;
        }
        return false;
    }

    /// <summary>The path below the watched root that contains it (else the path itself).</summary>
    private string Relative(string path)
    {
        var root = _watchers.Select(w => w.Path).FirstOrDefault(r => PathUtil.IsUnder(path, r));
        return root is null ? path : Path.GetRelativePath(root, path);
    }

    /// <summary>Records a change (tests call it directly).</summary>
    /// <param name="structural">
    /// Created or renamed (or deleted): a folder that exists now counts too. It may hold a project whose file the watcher
    /// missed: on Linux a file written right after its folder is created is missed while the watch on the new folder is
    /// being added, and a moved folder reports no events for its files. (A deleted folder's files report their deletion.)
    /// </param>
    public void OnChange(string path, bool structural = false)
    {
        var relative = Relative(path);
        var folder = structural && Directory.Exists(path) && !IsIgnored(relative + Path.DirectorySeparatorChar + "x");
        if (!folder && !IsRelevant(relative) && !(ProjectFileNames.Contains(Path.GetFileName(relative)) && !IsIgnored(relative))) return;
        lock (_gate)
        {
            if (_disposed) return;
            _pending.Add(path);
            var now = DateTime.UtcNow;
            _firstPending ??= now;
            var due = Quiet;
            var latest = _firstPending.Value + MaxDelay - now;
            if (latest < due) due = latest < TimeSpan.Zero ? TimeSpan.Zero : latest;
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        List<string> changed;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            changed = [.. _pending];
            _pending.Clear();
            _firstPending = null;
        }
        _changed(changed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}
