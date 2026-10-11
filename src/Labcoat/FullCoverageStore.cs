using System.Text.Json;

namespace Labcoat;

// The full report across restarts: what FullCoverage captures / restores, and FullCoverageStore (coverage.json).

public sealed partial class FullCoverage
{
    private readonly FullCoverageStore? _store;

    /// <summary>In memory only (tests, tools).</summary>
    public FullCoverage() { }

    /// <summary>
    /// Persisted: restores the report <paramref name="store"/> holds (files changed since are marked stale) and saves it
    /// there, debounced, whenever it changes.
    /// </summary>
    public FullCoverage(FullCoverageStore store)
    {
        _store = store;
        if (store.Load() is { } persisted) Restore(persisted);
        store.Attach(Capture);
    }

    /// <summary>One file of a project's full coverage, in columns: line numbers, hits and branches (-1 = none) per line.</summary>
    internal sealed record PersistedFile(string Path, string Module, FileStamp? ContentStamp, int[] Lines, int[] Hits, int[]? BranchesCovered, int[]? BranchesTotal);

    /// <summary>A project's part of the report.</summary>
    /// <param name="Added">Files added to one of its modules since its full run (stale, not in <paramref name="Files"/>).</param>
    internal sealed record PersistedPart(string ProjectId, string RunId, string GeneratedAt, CoverageSettingsInfo? Settings,
        List<PersistedFile> Files, List<string> Added);

    internal sealed record PersistedReport(string RunId, List<PersistedPart> Parts, List<CoverageMergedRun> Merged);

    /// <summary>A copy of the report's state for saving (null: no report yet). Copies under the lock; the caller writes it.</summary>
    internal PersistedReport? Capture()
    {
        lock (_gate)
        {
            if (_report is null) return null;
            var parts = new List<PersistedPart>(_order.Count);
            foreach (var id in _order)
            {
                var p = _projects[id];
                var files = new List<PersistedFile>(p.Data.FileCount);
                foreach (var (path, module, lines) in p.Data.FileLines)
                    files.Add(ToPersistedFile(path, module, p.Stamps.GetValueOrDefault(path), lines));
                var added = _stale.TryGetValue(id, out var stale) ? stale.Where(f => !p.Stamps.ContainsKey(f)).Order(StringComparer.Ordinal).ToList() : [];
                parts.Add(new PersistedPart(id, p.RunId, p.GeneratedAt, p.Settings, files, added));
            }
            return new PersistedReport(_runId, parts, [.. _merged]);
        }
    }

    /// <summary>A file's lines in columns, sorted by line number; the branch columns only when any line has branches.</summary>
    private static PersistedFile ToPersistedFile(string path, string module, FileStamp? stamp, IEnumerable<CoverageLineHit> lines)
    {
        var sorted = lines.OrderBy(l => l.Line).ToArray();
        var n = sorted.Length;
        int[] numbers = new int[n], hits = new int[n];
        int[]? bc = null, bt = null;
        for (var i = 0; i < n; i++)
        {
            numbers[i] = sorted[i].Line;
            hits[i] = sorted[i].Hits;
            if (sorted[i].BranchesTotal is null && sorted[i].BranchesCovered is null) continue;
            bc ??= NoBranches(n);
            bt ??= NoBranches(n);
            bc[i] = sorted[i].BranchesCovered ?? -1;
            bt[i] = sorted[i].BranchesTotal ?? -1;
        }
        return new PersistedFile(path, module, stamp, numbers, hits, bc, bt);
    }

    private static int[] NoBranches(int n)
    {
        var column = new int[n];
        Array.Fill(column, -1);
        return column;
    }

    /// <summary>The report of a previous session: its parts are marked restored and files changed since are stale.</summary>
    internal void Restore(PersistedReport persisted)
    {
        if (persisted.Parts.Count == 0) return;
        var projects = persisted.Parts.Select(RestorePart).ToList();
        lock (_gate)
        {
            foreach (var (id, full, stale) in projects)
            {
                _projects[id] = full;
                if (!_order.Contains(id)) _order.Add(id);
                _stale[id] = stale;
            }
            _merged.AddRange(persisted.Merged.Where(m => _projects.ContainsKey(m.ProjectId)));
            RebuildLocked(persisted.RunId);
        }
    }

    private static (string Id, ProjectFull Full, HashSet<string> Stale) RestorePart(PersistedPart part)
    {
        var data = new CoverageAccumulator();
        var stamps = new Dictionary<string, FileStamp?>(FileComparer);
        var stale = new HashSet<string>(part.Added, FileComparer);
        foreach (var f in part.Files)
        {
            data.SetFile(f.Path, f.Module, ToLines(f));
            stamps[f.Path] = f.ContentStamp;
            if (Changed(f.Path, f.ContentStamp)) stale.Add(f.Path); // edited while the host was down
        }
        var full = new ProjectFull
        {
            RunId = part.RunId, GeneratedAt = part.GeneratedAt, Data = data, Stamps = stamps, Settings = part.Settings, Restored = true,
        };
        return (part.ProjectId, full, stale);
    }

    private static CoverageLineHit[] ToLines(PersistedFile f)
    {
        var lines = new CoverageLineHit[f.Lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = new CoverageLineHit
            {
                Line = f.Lines[i],
                Hits = f.Hits[i],
                BranchesCovered = Branches(f.BranchesCovered, i),
                BranchesTotal = Branches(f.BranchesTotal, i),
            };
        }
        return lines;
    }

    /// <summary>A branch column's value; null without the column or for -1 (no branches).</summary>
    private static int? Branches(int[]? column, int i) => column is not null && column[i] >= 0 ? column[i] : null;
}

/// <summary>
/// Persists the full coverage report (<see cref="FullCoverage"/>) to <c>coverage.json</c> next to <c>history.json</c>
/// (<see cref="HistoryStore"/>), so the Coverage page shows it again after a restart. Per project: its part of the
/// report, the stamp (length, write time, SHA-256) of every file for the stale check, and the time of its full run.
/// Like history.json version 2 the lines are columns (delta-encoded line numbers, hits, branches) instead of one object
/// per line. Saved debounced (<see cref="SaveDelay"/>) only when the report changes, streamed to a temp file that then
/// replaces the old one; a file over <see cref="MaxBytes"/> is neither written nor read. A missing, corrupt or newer
/// file is ignored with a warning (the report starts empty, as before).
/// With <see cref="Keys"/> each part also carries the project's path relative to the workspace root (<c>projectPath</c>),
/// which decides its project when the file is read (ids can change across restarts, see <see cref="IProjectKeys"/>): the
/// current id of that path, else the path itself (a project that is not part of the workspace now; such a part is
/// dropped once its full run is older than <see cref="HistoryStore.OrphanRetention"/>). Parts written before (no
/// <c>projectPath</c>) keep their project id.
/// </summary>
public sealed class FullCoverageStore : IDisposable
{
    public const int FileVersion = 1;
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly Action<string, string>? _log;
    private readonly object _saveGate = new();
    private readonly Timer _timer;
    private Func<FullCoverage.PersistedReport?>? _capture;
    private volatile bool _dirty;

    public string FilePath { get; }

    /// <summary>The project paths of the current ids (see the class remarks); null: parts are kept by project id only.</summary>
    public IProjectKeys? Keys { get; init; }

    /// <summary>Size cap of the file (writing a bigger one is abandoned; a bigger one is not read).</summary>
    public long MaxBytes { get; init; } = 128L * 1024 * 1024;

    public FullCoverageStore(string directory, Action<string, string>? log = null)
    {
        _log = log;
        FilePath = Path.Combine(directory, "coverage.json");
        _timer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal void Attach(Func<FullCoverage.PersistedReport?> capture) => _capture = capture;

    /// <summary>The report changed: save it after <see cref="SaveDelay"/> (later changes push the save back).</summary>
    internal void Schedule()
    {
        _dirty = true;
        try { _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* disposed: Dispose saved the report already */ }
    }

    // ------------------------------------------------------------------ the file model (what is read; Write produces it)

    // Internal, not private: the properties are set by System.Text.Json only (init), which code analysis can't see.
    internal sealed class FileModel
    {
        public int Version { get; init; }
        public string? RunId { get; init; }
        public List<CoverageMergedRun>? Merged { get; init; }
        public List<PartModel>? Parts { get; init; }
    }

    internal sealed class PartModel
    {
        public string ProjectId { get; init; } = "";
        /// <summary>The project's path relative to the workspace root (absent in files written before it, or without <see cref="Keys"/>).</summary>
        public string? ProjectPath { get; init; }
        public string RunId { get; init; } = "";
        public string GeneratedAt { get; init; } = "";
        public CoverageSettingsInfo? Settings { get; init; }
        /// <summary>Module names, referenced by index from <see cref="FileEntry.Module"/>.</summary>
        public List<string> Modules { get; init; } = [];
        public List<string>? Added { get; init; }
        public List<FileEntry> Files { get; init; } = [];
    }

    internal sealed class FileEntry
    {
        public string Path { get; init; } = "";
        public int Module { get; init; }
        /// <summary>The stamp at the full run; absent when the file did not exist / could not be read then.</summary>
        public long? Length { get; init; }
        /// <summary>Last write time (UTC ticks).</summary>
        public long? Written { get; init; }
        public string? Sha256 { get; init; }
        /// <summary>Line numbers, each as the difference to the previous one (ascending).</summary>
        public int[] Lines { get; init; } = [];
        public int[] Hits { get; init; } = [];
        /// <summary>Branches per line, -1 = not a branch line (absent when the file has none).</summary>
        public int[]? BranchesCovered { get; init; }
        public int[]? BranchesTotal { get; init; }
    }

    // ------------------------------------------------------------------ read

    /// <summary>The persisted report, or null (none, unreadable, too big or of another version: logged).</summary>
    internal FullCoverage.PersistedReport? Load()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) return null;
            if (info.Length > MaxBytes)
            {
                _log?.Invoke(LogLevels.Warning, $"Not restoring the coverage report: {FilePath} is {info.Length / (1024 * 1024)} MB (more than {MaxBytes / (1024 * 1024)} MB)");
                return null;
            }
            FileModel? model;
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
                model = JsonSerializer.Deserialize<FileModel>(stream, Json.Options);
            if (model is null) return null;
            if (model.Version != FileVersion)
            {
                _log?.Invoke(LogLevels.Warning, $"Not restoring the coverage report: {FilePath} has version {model.Version} (this Labcoat reads version {FileVersion})");
                return null;
            }
            var report = FromModel(model);
            var files = report.Parts.Sum(p => p.Files.Count);
            _log?.Invoke("info", $"Restored the coverage report of {string.Join(", ", report.Parts.Select(p => p.ProjectId))} ({files} files, " +
                                 $"full run {string.Join(", ", report.Parts.Select(p => p.GeneratedAt).Distinct())}) from {FilePath}");
            return report;
        }
        catch (Exception ex)
        {
            _log?.Invoke(LogLevels.Warning, $"Could not read the coverage report ({FilePath}): {ex.Message}");
            return null;
        }
    }

    private FullCoverage.PersistedReport FromModel(FileModel model)
    {
        var parts = new List<FullCoverage.PersistedPart>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal); // stored id → current key, for the merged runs
        var cutoff = DateTimeOffset.UtcNow - HistoryStore.OrphanRetention;
        foreach (var p in model.Parts ?? [])
        {
            if (p.ProjectId.Length == 0) throw new JsonException("A part without a project id.");
            var id = CurrentKey(p);
            if (id.Contains('/') && HistoryStore.Time(p.GeneratedAt) < cutoff) continue; // a removed project's old part
            ids[p.ProjectId] = id;
            var files = new List<FullCoverage.PersistedFile>(p.Files.Count);
            foreach (var f in p.Files) files.Add(FromModel(f, p.Modules));
            parts.Add(new FullCoverage.PersistedPart(id, p.RunId, p.GeneratedAt, p.Settings, files, p.Added ?? []));
        }
        var merged = (model.Merged ?? []).Where(m => ids.ContainsKey(m.ProjectId)).Select(m => m with { ProjectId = ids[m.ProjectId] }).ToList();
        return new FullCoverage.PersistedReport(model.RunId ?? "", parts, merged);
    }

    /// <summary>The key a stored part goes under: the current id of its path, else its path; its stored id without a path.</summary>
    private string CurrentKey(PartModel p)
    {
        if (p.ProjectPath is not { Length: > 0 } rel || Keys is null) return p.ProjectId;
        return Keys.ProjectIdOf(rel) ?? rel;
    }

    private static FullCoverage.PersistedFile FromModel(FileEntry f, List<string> modules)
    {
        var n = f.Lines.Length;
        if (f.Hits.Length != n || (f.BranchesCovered?.Length ?? n) != n || (f.BranchesTotal?.Length ?? n) != n || (f.BranchesCovered is null) != (f.BranchesTotal is null))
            throw new JsonException($"{f.Path}: the line columns differ in length.");
        if ((uint)f.Module >= (uint)modules.Count) throw new JsonException($"{f.Path}: unknown module {f.Module}.");
        var lines = new int[n];
        for (int i = 0, line = 0; i < n; i++) lines[i] = line += f.Lines[i];
        FullCoverage.FileStamp? stamp = f.Length is { } len && f.Written is { } ticks && f.Sha256 is { } hash
            ? new FullCoverage.FileStamp(len, new DateTime(ticks, DateTimeKind.Utc), hash)
            : null;
        return new FullCoverage.PersistedFile(f.Path, modules[f.Module], stamp, lines, f.Hits, f.BranchesCovered, f.BranchesTotal);
    }

    // ------------------------------------------------------------------ write

    private const int FlushBytes = 32 * 1024;
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = Json.Options.Encoder };

    /// <summary>Writes the file if the report changed since the last save (temp file + rename; never a truncated file).</summary>
    public void Save()
    {
        lock (_saveGate)
        {
            if (!_dirty || _capture is null) return;
            // Another Labcoat owns this workspace's data folder (WorkspaceLock): kept dirty, saved once this one may write.
            if (!WorkspaceLock.CanWrite(Path.GetDirectoryName(FilePath)!)) return;
            _dirty = false;
            FullCoverage.PersistedReport? report;
            try { report = _capture(); }
            catch (Exception ex)
            {
                _log?.Invoke(LogLevels.Warning, $"Could not save the coverage report: {ex.Message}");
                return;
            }
            if (report is null) return;
            var tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                    Write(stream, report, ProjectPathOf, MaxBytes);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log?.Invoke(LogLevels.Warning, $"Could not save the coverage report ({FilePath}): {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort: a leftover temp file is overwritten by the next save */ }
            }
        }
    }

    private static void Write(Stream stream, FullCoverage.PersistedReport report, Func<string, string?> pathOf, long maxBytes)
    {
        using var w = new Utf8JsonWriter(stream, WriterOptions);
        void MaybeFlush()
        {
            if (w.BytesPending <= FlushBytes) return;
            w.Flush();
            if (stream.Position > maxBytes) throw new IOException($"the report is larger than {maxBytes / (1024 * 1024)} MB; not saved");
        }

        w.WriteStartObject();
        w.WriteNumber("version", FileVersion);
        w.WriteString("runId", report.RunId);
        if (report.Merged.Count > 0)
        {
            w.WritePropertyName("merged");
            JsonSerializer.Serialize(w, report.Merged, Json.Options);
        }
        w.WriteStartArray("parts");
        foreach (var part in report.Parts) WritePart(w, part, pathOf(part.ProjectId), MaybeFlush);
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        if (stream.Position > maxBytes) throw new IOException($"the report is larger than {maxBytes / (1024 * 1024)} MB; not saved");
    }

    /// <summary>A project's part: its modules once, then its files referring to them by index.</summary>
    /// <summary>The relative path a part is written with: its project's, or its key when that is a path (a removed project); null without <see cref="Keys"/>.</summary>
    private string? ProjectPathOf(string projectId)
    {
        if (Keys is null) return null;
        return Keys.ProjectPathOf(projectId) ?? (projectId.Contains('/') ? projectId : null);
    }

    private static void WritePart(Utf8JsonWriter w, FullCoverage.PersistedPart part, string? projectPath, Action maybeFlush)
    {
        w.WriteStartObject();
        w.WriteString("projectId", part.ProjectId);
        if (projectPath is not null) w.WriteString("projectPath", projectPath);
        w.WriteString("runId", part.RunId);
        w.WriteString("generatedAt", part.GeneratedAt);
        if (part.Settings is not null)
        {
            w.WritePropertyName("settings");
            JsonSerializer.Serialize(w, part.Settings, Json.Options);
        }
        var moduleIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        w.WriteStartArray("modules");
        foreach (var module in part.Files.Select(f => f.Module).Distinct(StringComparer.Ordinal))
        {
            moduleIndex[module] = moduleIndex.Count;
            w.WriteStringValue(module);
        }
        w.WriteEndArray();
        if (part.Added.Count > 0)
        {
            w.WriteStartArray("added");
            foreach (var a in part.Added) w.WriteStringValue(a);
            w.WriteEndArray();
        }
        w.WriteStartArray("files");
        foreach (var f in part.Files)
        {
            WriteFile(w, f, moduleIndex[f.Module]);
            maybeFlush();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteFile(Utf8JsonWriter w, FullCoverage.PersistedFile f, int module)
    {
        w.WriteStartObject();
        w.WriteString("path", f.Path);
        w.WriteNumber("module", module);
        if (f.ContentStamp is { } s)
        {
            w.WriteNumber("length", s.Length);
            w.WriteNumber("written", s.WriteUtc.Ticks);
            w.WriteString("sha256", s.Hash);
        }
        w.WriteStartArray("lines");
        for (int i = 0, prev = 0; i < f.Lines.Length; prev = f.Lines[i], i++) w.WriteNumberValue(f.Lines[i] - prev);
        w.WriteEndArray();
        WriteInts(w, "hits", f.Hits);
        if (f.BranchesCovered is not null && f.BranchesTotal is not null)
        {
            WriteInts(w, "branchesCovered", f.BranchesCovered);
            WriteInts(w, "branchesTotal", f.BranchesTotal);
        }
        w.WriteEndObject();
    }

    private static void WriteInts(Utf8JsonWriter w, string name, int[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    /// <summary>Stops the timer and writes a pending save.</summary>
    public void Dispose()
    {
        _timer.Dispose();
        Save();
    }
}
