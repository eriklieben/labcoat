namespace Labcoat;

// Watch mode: a FileSystemWatcher on each watched folder of every project; a change to a source or project file starts,
// 500 ms after the last one, that project's smart watch run (onChanges: RunWatchAsync, Explorer.ImpactAndWatch.cs).
internal sealed class WatchMode(WorkspaceState state, IExplorerEffects effects, Action publishSettings, Func<ProjectEntry, Task> onChanges)
{
    /// <summary>The files that count as a change (also what the build's "sources changed" check looks at).</summary>
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".fs", ".fsproj", ".vb", ".vbproj", ".props", ".targets", ".json", ".razor" };

    /// <summary>Watch mode is on (lock Sync).</summary>
    public bool Enabled { get; private set; }

    public void Set(bool enabled)
    {
        lock (state.Sync)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            foreach (var p in state.Projects)
            {
                if (enabled) Start(p);
                else Stop(p);
            }
            publishSettings();
        }
        effects.Log(null, LogLevels.Info, enabled ? "Watch mode enabled" : "Watch mode disabled");
    }

    /// <summary>(Re)starts the watchers of a project's folders.</summary>
    public void Start(ProjectEntry p)
    {
        Stop(p);
        var dirs = p.WatchDirs.Count > 0 ? p.WatchDirs : [Path.GetDirectoryName(p.Project.Path)!];
        foreach (var dir in dirs.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            FileSystemEventHandler h = (_, e) => OnFileChanged(p, e.FullPath);
            w.Changed += h;
            w.Created += h;
            w.Deleted += h;
            w.Renamed += (_, e) => OnFileChanged(p, e.FullPath);
            w.EnableRaisingEvents = true;
            p.Watchers.Add(w);
        }
    }

    public static void Stop(ProjectEntry p)
    {
        foreach (var w in p.Watchers) w.Dispose();
        p.Watchers.Clear();
        p.Debounce?.Dispose();
        p.Debounce = null;
    }

    private void OnFileChanged(ProjectEntry p, string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path))) return;
        var sep = Path.DirectorySeparatorChar;
        if (path.Contains($"{sep}bin{sep}") || path.Contains($"{sep}obj{sep}")) return;
        lock (state.Sync)
        {
            if (!Enabled) return;
            p.PendingChanges.Add(path);
            p.Debounce?.Dispose();
            p.Debounce = new Timer(_ => _ = onChanges(p), null, 500, Timeout.Infinite);
        }
    }
}
