using System.Text.Json;
using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>
/// The workspace data folder (<see cref="WorkspaceIdentity"/>): per-test data is keyed by the project's path relative to
/// the workspace root (written as <c>projectPaths</c> next to <c>projectIds</c>), so adding, removing or renaming a project
/// keeps every other project's history; a removed project's entries age out after <see cref="OrphanRetention"/>.
/// The first start in a workspace folder merges the folders of earlier Labcoat versions (one per set of project paths)
/// that belong to it. One process at a time writes the folder (<see cref="WorkspaceLock"/>).
/// </summary>
public sealed partial class HistoryStore
{
    /// <summary>How long the history of a project that is no longer part of the workspace is kept.</summary>
    public static readonly TimeSpan OrphanRetention = TimeSpan.FromDays(30);

    /// <summary>workspace.json: what the folder belongs to; its presence also means the migration ran.</summary>
    public const string WorkspaceFileName = "workspace.json";

    /// <summary>Left in a folder of an earlier version once its data was merged into a workspace folder.</summary>
    public const string MigratedFileName = "migrated.json";

    private readonly Dictionary<string, string> _relativePathById;
    private readonly Dictionary<string, string> _idByRelativePath;
    private readonly WorkspaceLock.Lease _lease;
    private bool _saveFailed;

    /// <summary>The workspace this history belongs to.</summary>
    public WorkspaceIdentity Workspace { get; }

    /// <summary>The workspace data folder (history.json, settings.json, coverage.json).</summary>
    public string DataDirectory => Path.GetDirectoryName(FilePath)!;

    /// <summary>Another Labcoat process owns the data folder: this store reads it but doesn't save.</summary>
    public bool ReadOnly => !_lease.Owner;

    private static (Dictionary<string, string> RelById, Dictionary<string, string> IdByRel) ProjectKeys(WorkspaceIdentity workspace, List<string> paths)
    {
        var ids = WorkspaceIdentity.ProjectIds(paths);
        var relById = new Dictionary<string, string>(StringComparer.Ordinal);
        var idByRel = new Dictionary<string, string>(WorkspaceIdentity.PathComparer);
        for (var i = 0; i < paths.Count; i++)
        {
            var rel = workspace.RelativeProjectPath(paths[i]);
            relById[ids[i]] = rel;
            idByRel.TryAdd(rel, ids[i]);
        }
        return (relById, idByRel);
    }

    /// <summary>
    /// The relative path of a project id: a current project's, or the id itself for the entries of a project that is no
    /// longer in the workspace (they are kept under their relative path, which contains '/'); null when unknown.
    /// </summary>
    private static string? RelativePathOf(Dictionary<string, string> relativePathById, string projectId)
    {
        if (relativePathById.TryGetValue(projectId, out var rel)) return rel;
        return projectId.Contains('/') ? projectId : null;
    }

    /// <summary>The current project id of a relative path, else the path itself (a project that is not in the workspace now).</summary>
    private string IdOf(string relativePath)
    {
        lock (_gate) return _idByRelativePath.TryGetValue(relativePath, out var id) ? id : relativePath;
    }

    /// <inheritdoc/>
    public string? ProjectPathOf(string projectId)
    {
        lock (_gate) return _relativePathById.GetValueOrDefault(projectId);
    }

    /// <inheritdoc/>
    public string? ProjectIdOf(string relativePath)
    {
        lock (_gate) return _idByRelativePath.GetValueOrDefault(relativePath);
    }

    // ------------------------------------------------------------------ the project list while running

    /// <summary>
    /// A project that joined the workspace while running (live discovery, Rediscover): its results are saved under its
    /// relative path from now on, and what is kept under that path (the project was part of the workspace before, within
    /// <see cref="OrphanRetention"/>) is its history again. Callers forget a project's previous id first
    /// (<see cref="ForgetProject"/>) when they give it to another one.
    /// </summary>
    public void RegisterProject(string projectId, string projectPath)
    {
        var rel = Workspace.RelativeProjectPath(projectPath);
        lock (_gate)
        {
            if (_relativePathById.TryGetValue(projectId, out var current) && WorkspaceIdentity.PathComparer.Equals(current, rel)) return;
            if (current is not null) ForgetProjectLocked(projectId);
            if (_idByRelativePath.TryGetValue(rel, out var other)) ForgetProjectLocked(other);
            _relativePathById[projectId] = rel;
            _idByRelativePath[rel] = projectId;
            var full = PathUtil.CanonicalizeOrSelf(Path.GetFullPath(projectPath));
            if (!_projects.Contains(full, StringComparer.Ordinal))
            {
                _projects.Add(full);
                _projects.Sort(StringComparer.Ordinal);
            }
            if (RekeyLocked(rel, projectId)) ScheduleSaveLocked();
        }
    }

    /// <summary>
    /// A project that left the workspace while running: its entries are kept under its relative path (where a restart
    /// keeps them too) and age out after <see cref="OrphanRetention"/>; its id may go to another project.
    /// </summary>
    public void ForgetProject(string projectId)
    {
        lock (_gate)
        {
            if (ForgetProjectLocked(projectId)) ScheduleSaveLocked();
        }
    }

    private bool ForgetProjectLocked(string projectId)
    {
        if (!_relativePathById.Remove(projectId, out var rel)) return false;
        if (_idByRelativePath.TryGetValue(rel, out var id) && id == projectId) _idByRelativePath.Remove(rel);
        var full = Workspace.Root.Length > 0 && !Path.IsPathRooted(rel) ? Path.GetFullPath(Path.Combine(Workspace.Root, rel)) : rel;
        _projects.RemoveAll(p => WorkspaceIdentity.PathComparer.Equals(p, PathUtil.CanonicalizeOrSelf(full)));
        RekeyLocked(projectId, rel);
        return true; // the project list changed: saved even without entries to move
    }

    /// <summary>Moves the per-test entries and past-run results of project key <paramref name="from"/> to <paramref name="to"/>; true when any moved.</summary>
    private bool RekeyLocked(string from, string to)
    {
        var moved = false;
        foreach (var e in _tests.Values.Where(e => e.ProjectId == from).ToList())
        {
            _tests.Remove((from, e.Id));
            var entry = new Entry(to, e.Id) { Runs = e.Runs, Last = e.Last };
            _tests[(to, e.Id)] = _tests.TryGetValue((to, e.Id), out var other) ? MergeEntries(other, entry) : entry;
            moved = true;
        }
        for (var i = 0; i < _runs.Count; i++)
        {
            if (!_runs[i].ProjectIds.Contains(from, StringComparer.Ordinal)) continue;
            _runs[i] = RemapRun(_runs[i], p => p == from ? to : p);
            moved = true;
        }
        return moved;
    }

    private void ScheduleSaveLocked()
    {
        _dirty = true;
        try { _saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* disposed: Dispose saved already; a change after it is not saved */ }
    }

    private WorkspaceLock.Lease AcquireWorkspace()
    {
        var lease = WorkspaceLock.Acquire(DataDirectory);
        if (!lease.Owner)
            _log?.Invoke(LogLevels.Warning, $"Another Labcoat is using this workspace's history: {(lease.OtherPid is { } pid ? $"pid {pid}" : "unknown pid")}. " +
                                    $"This one shows it but doesn't save history, settings or the coverage report until that one ends ({DataDirectory}).");
        return lease;
    }

    /// <summary>
    /// Saving needs the lock: an instance that didn't get it at startup takes it over once the other one ended, merging
    /// what that one saved meanwhile (newest wins) before it writes.
    /// </summary>
    private bool EnsureWriter()
    {
        if (_lease.Owner) return true;
        if (!_lease.TryUpgrade()) return false;
        var saved = ReadForMerge(FilePath);
        lock (_gate)
        {
            MergeLocked(saved);
            _dirty = true;
        }
        _log?.Invoke("info", $"The other Labcoat ended: this one saves the workspace history again ({DataDirectory}).");
        return true;
    }

    // ------------------------------------------------------------------ project keys of a loaded file

    /// <summary>
    /// Moves the entries of a loaded file to the current project ids (via the relative paths stored with it, or for an
    /// older file, its absolute project paths) and drops the entries of removed projects older than <see cref="OrphanRetention"/>.
    /// </summary>
    private (Dictionary<(string, string), Entry>, List<StoredRun>) Adopt((Dictionary<(string, string), Entry> Tests, List<StoredRun> Runs) data, FileModel model)
    {
        var map = StoredIdMap(model);
        string Map(string id) => map.TryGetValue(id, out var rel) ? IdOf(rel) : id;
        var cutoff = DateTimeOffset.UtcNow - OrphanRetention;
        var tests = new Dictionary<(string, string), Entry>();
        foreach (var e in data.Tests.Values)
        {
            var projectId = Map(e.ProjectId);
            var moved = new Entry(projectId, e.Id) { Runs = e.Runs, Last = e.Last };
            if (projectId.Contains('/') && LatestTime(moved) < cutoff) continue; // a removed project's old history
            tests[(projectId, e.Id)] = tests.TryGetValue((projectId, e.Id), out var other) ? MergeEntries(other, moved) : moved;
        }
        var runs = data.Runs.Select(r => r.ProjectIds.Any(map.ContainsKey) ? RemapRun(r, Map) : r).ToList();
        return (tests, runs);
    }

    private static StoredRun RemapRun(StoredRun r, Func<string, string> map) => new()
    {
        Run = r.Run, ProjectIds = [.. r.ProjectIds.Select(map)], Ids = r.Ids, Outcomes = r.Outcomes, Durations = r.Durations, Errors = r.Errors,
    };

    /// <summary>Stored project id → relative path.</summary>
    private Dictionary<string, string> StoredIdMap(FileModel model)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (model.ProjectPaths is { } paths && model.ProjectIds is { } ids)
        {
            for (var i = 0; i < ids.Count && i < paths.Count; i++)
                if (paths[i] is { } rel) map[ids[i]] = rel;
            return map;
        }
        // Before workspace folders: the ids came from the absolute project paths (file name, "-2" for repeats).
        var legacyIds = WorkspaceIdentity.ProjectIds(model.Projects);
        for (var i = 0; i < model.Projects.Count; i++) map.TryAdd(legacyIds[i], Workspace.RelativeProjectPath(model.Projects[i]));
        return map;
    }

    /// <summary>A stored ISO 8601 time; <see cref="DateTimeOffset.MinValue"/> when missing or unreadable.</summary>
    internal static DateTimeOffset Time(string? s) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t : DateTimeOffset.MinValue;

    private static DateTimeOffset LatestTime(Entry e)
    {
        var latest = Time(e.Last?.LastRunAt);
        if (e.Runs.Count > 0 && Time(e.Runs[^1].At) is var at && at > latest) latest = at;
        return latest;
    }

    // ------------------------------------------------------------------ merging (migration, taking the lock over)

    /// <summary>Merges per-test histories (by run, in time order, capped) and keeps the newer last result.</summary>
    private static Entry MergeEntries(Entry a, Entry b)
    {
        var runs = a.Runs.Concat(b.Runs)
            .GroupBy(h => h.RunId ?? "@" + h.At, StringComparer.Ordinal).Select(g => g.Last())
            .OrderBy(h => Time(h.At)).ToList();
        if (runs.Count > MaxRuns) runs.RemoveRange(0, runs.Count - MaxRuns);
        var last = Time(b.Last?.LastRunAt) >= Time(a.Last?.LastRunAt) && b.Last is not null ? b.Last : a.Last ?? b.Last;
        return new Entry(a.ProjectId, a.Id) { Runs = runs, Last = last };
    }

    /// <summary>Adds <paramref name="other"/> to the in-memory data (callers hold <see cref="_gate"/>); on conflicts the newer result wins.</summary>
    private void MergeLocked((Dictionary<(string, string), Entry> Tests, List<StoredRun> Runs) other)
    {
        foreach (var (key, e) in other.Tests)
            _tests[key] = _tests.TryGetValue(key, out var mine) ? MergeEntries(mine, e) : e;
        var known = _runs.Select(r => r.Run.RunId).ToHashSet(StringComparer.Ordinal);
        var all = _runs.Concat(other.Runs.Where(r => known.Add(r.Run.RunId))).OrderBy(r => Time(r.Run.StartedAt)).ToList();
        if (all.Count > MaxStoredRuns) all.RemoveRange(0, all.Count - MaxStoredRuns);
        _runs.Clear();
        _runs.AddRange(all);
    }

    /// <summary>A history file, adopted into this workspace; empty when missing or unreadable (logged).</summary>
    private (Dictionary<(string, string), Entry>, List<StoredRun>) ReadForMerge(string file)
    {
        try
        {
            if (!File.Exists(file)) return ([], []);
            FileModel? model;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
                model = JsonSerializer.Deserialize<FileModel>(stream, Json.Options);
            return model is null ? ([], []) : Adopt(FromModel(model), model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                       or ArgumentException or IndexOutOfRangeException)
        {
            _log?.Invoke(LogLevels.Warning, $"Could not read test history ({file}): {ex.Message}");
            return ([], []);
        }
    }

    // ------------------------------------------------------------------ migration

    /// <summary>
    /// First start of this workspace folder (no workspace.json yet): merges the folders of earlier versions whose projects
    /// all lie under the workspace root (history: newest wins on conflicts; settings.json and coverage.json: the newest
    /// file), then marks each of them with migrated.json (they are left in place; delete them at will) and writes
    /// workspace.json.
    /// </summary>
    private void MigrateLegacyFolders()
    {
        var marker = Path.Combine(DataDirectory, WorkspaceFileName);
        if (File.Exists(marker)) return;
        var sources = Workspace.Kind == "none" || Workspace.IsBroad ? [] : LegacyFoldersOfThisWorkspace();
        if (sources.Count > 0)
        {
            foreach (var dir in sources)
            {
                var data = ReadForMerge(Path.Combine(dir, "history.json"));
                lock (_gate) MergeLocked(data);
            }
            lock (_gate) _dirty = true;
            Save();
            if (_saveFailed) return; // nothing marked: the next start tries again
            CopyNewest(sources, "settings.json");
            CopyNewest(sources, "coverage.json");
            foreach (var dir in sources)
                TryWrite(Path.Combine(dir, MigratedFileName), new JsonObject { ["migratedTo"] = Workspace.FolderName, ["at"] = Json.Now() });
            _log?.Invoke("info", $"Merged the test history of {sources.Count} earlier folder(s) into {DataDirectory}");
        }
        var description = Workspace.Describe();
        description["createdAt"] = Json.Now();
        if (sources.Count > 0) description["migratedFrom"] = new JsonArray([.. sources.Select(s => (JsonNode)Path.GetFileName(s))]);
        TryWrite(marker, description);
    }

    /// <summary>Folders of earlier versions (16 hex characters, not migrated yet) whose history lists only projects under the workspace root.</summary>
    private List<string> LegacyFoldersOfThisWorkspace()
    {
        var baseDir = Path.GetDirectoryName(DataDirectory)!;
        var found = new List<(string Dir, DateTime Written)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
            {
                var name = Path.GetFileName(dir);
                if (name.Length != 16 || !name.All(char.IsAsciiHexDigitLower)) continue;
                var history = Path.Combine(dir, "history.json");
                if (File.Exists(Path.Combine(dir, MigratedFileName)) || !File.Exists(history)) continue;
                var projects = ReadProjects(history) ?? ReadProjectsSlowly(history);
                if (projects is { Count: > 0 } && projects.All(p => PathUtil.IsUnder(p, Workspace.Root)))
                    found.Add((dir, File.GetLastWriteTimeUtc(history)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke(LogLevels.Warning, $"Could not look for earlier test history in {baseDir}: {ex.Message}");
        }
        return [.. found.OrderBy(f => f.Written).Select(f => f.Dir)]; // oldest first: later merges are newer
    }

    /// <summary>The "projects" list at the start of a history file, without reading the rest; null when unreadable.</summary>
    internal static List<string>? ReadProjects(string historyFile)
    {
        try
        {
            using var stream = new FileStream(historyFile, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            var buffer = new byte[256 * 1024];
            var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), isFinalBlock: length < buffer.Length, default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            return FindProjects(ref reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The top-level "projects" value, skipping the properties before it; null when it isn't in the buffer.</summary>
    private static List<string>? FindProjects(ref Utf8JsonReader reader)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isProjects = reader.ValueTextEquals("projects");
            if (!reader.Read()) return null;
            if (isProjects) return ReadStringArray(ref reader);
            if (!reader.TrySkip()) return null;
        }
        return null;
    }

    /// <summary>A complete array of strings at the reader's position; null for anything else.</summary>
    private static List<string>? ReadStringArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return null;
        var list = new List<string>();
        while (reader.Read() && reader.TokenType == JsonTokenType.String) list.Add(reader.GetString()!);
        return reader.TokenType == JsonTokenType.EndArray ? list : null;
    }

    /// <summary>The "projects" list of a history file whose start <see cref="ReadProjects"/> could not read.</summary>
    private static List<string>? ReadProjectsSlowly(string historyFile)
    {
        try
        {
            using var stream = new FileStream(historyFile, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            return JsonSerializer.Deserialize<FileModel>(stream, Json.Options)?.Projects;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Copies the newest <paramref name="fileName"/> of <paramref name="sources"/> into the workspace folder, unless it has one.</summary>
    private void CopyNewest(List<string> sources, string fileName)
    {
        var target = Path.Combine(DataDirectory, fileName);
        if (File.Exists(target)) return;
        var newest = sources.Select(d => Path.Combine(d, fileName)).Where(File.Exists).OrderBy(File.GetLastWriteTimeUtc).LastOrDefault();
        if (newest is null) return;
        try { File.Copy(newest, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke(LogLevels.Warning, $"Could not copy {newest} to {target}: {ex.Message}");
        }
    }

    private void TryWrite(string file, JsonObject content)
    {
        try { File.WriteAllText(file, content.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Json.Options.Encoder })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke(LogLevels.Warning, $"Could not write {file}: {ex.Message}");
        }
    }
}
