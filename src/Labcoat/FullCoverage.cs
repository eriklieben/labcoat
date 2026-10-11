using System.Security.Cryptography;

namespace Labcoat;

/// <summary>
/// The full coverage report: what the Coverage page shows (contract "Coverage (added)" → "The full report").
///
/// Per project it keeps the coverage of the project's last <b>full</b> run (every test of the project, with or without
/// the other projects), together with a stamp (length, write time, SHA-256) of every source file in it. The report is
/// those projects combined, like the report of one run of all of them. A run of selected tests never replaces it; its
/// coverage is merged in per project and file, only where that is sound:
/// <list type="bullet">
/// <item>A file whose content is unchanged since the full run: a line is covered when the full run or the subset
/// covers it (hits and branches take the max, so a line both executed is not counted twice). The subset ran the
/// current code and the line numbers still match, so the union only adds lines that really are executed now; this
/// picks up test edits that cover more.</item>
/// <item>A file of a module the full run did not load at all (no test reached it): the subset's coverage is added.</item>
/// <item>A changed (or deleted) file, or a file added to a module since the full run, is left as the full run saw it
/// (an added one stays out): its line numbers no longer match and a subset cannot tell what the other tests cover
/// now. Such files are listed as stale (<c>staleFiles</c>, <c>files[].stale</c>) until the next full run of the
/// project. A stale report as a whole is approximate: a change in one file can change what runs in another.</item>
/// </list>
/// Staleness is also refreshed after every run (with or without coverage). With a <see cref="FullCoverageStore"/> the
/// report survives a restart (coverage.json next to history.json, saved debounced whenever the report itself changes):
/// the restored parts keep their stamps, so files changed while the host was down are marked stale at startup, and the
/// report carries <see cref="CoverageReport.RestoredAt"/> until every restored part is replaced by a new full run.
/// Thread safe.
/// </summary>
public sealed partial class FullCoverage
{
    internal sealed record FileStamp(long Length, DateTime WriteUtc, string Hash);

    private sealed class ProjectFull
    {
        public required string RunId { get; init; }
        public required string GeneratedAt { get; init; }
        public required CoverageAccumulator Data { get; init; }
        /// <summary>Per file: the stamp at the full run (null: the file did not exist / could not be read).</summary>
        public required Dictionary<string, FileStamp?> Stamps { get; init; }
        public CoverageSettingsInfo? Settings { get; init; }
        /// <summary>Loaded from disk at startup (collected before the host restarted).</summary>
        public bool Restored { get; init; }
    }

    /// <summary>Result of <see cref="MergeSubset"/>.</summary>
    public sealed record MergeResult(bool HasFull, int FilesMerged, int FilesChanged, List<string> ChangedFiles)
    {
        public static readonly MergeResult NoFull = new(false, 0, 0, []);
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProjectFull> _projects = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly List<CoverageMergedRun> _merged = [];
    /// <summary>Per project: its files that changed since its full run.</summary>
    private readonly Dictionary<string, HashSet<string>> _stale = new(StringComparer.Ordinal);
    private string _runId = "";
    private CoverageReport? _report;

    /// <summary>The current full report; null until any project had a full coverage run.</summary>
    public CoverageReport? Report
    {
        get { lock (_gate) return _report; }
    }

    /// <summary>A full run of <paramref name="projectId"/>: its coverage replaces the project's part of the report.</summary>
    public CoverageReport SetFull(string projectId, CoverageAccumulator data, string runId, CoverageSettingsInfo? settings)
    {
        var copy = data.Clone();
        var stamps = copy.Files.ToDictionary(f => f.Path, f => Stamp(f.Path), FileComparer);
        lock (_gate)
        {
            _projects[projectId] = new ProjectFull { RunId = runId, GeneratedAt = Json.Now(), Data = copy, Stamps = stamps, Settings = settings };
            if (!_order.Contains(projectId)) _order.Add(projectId);
            _merged.RemoveAll(m => m.ProjectId == projectId);
            _stale[projectId] = new HashSet<string>(FileComparer); // just stamped
            var report = RebuildLocked(runId);
            _store?.Schedule();
            return report;
        }
    }

    /// <summary>
    /// A run of selected tests of <paramref name="projectId"/>: merges its coverage into the project's full coverage
    /// where the code is unchanged (see the class remarks). Null report when nothing changed.
    /// </summary>
    public (MergeResult Result, CoverageReport? Report) MergeSubset(string projectId, CoverageAccumulator subset, string runId, int testCount)
    {
        ProjectFull? full;
        lock (_gate) full = _projects.GetValueOrDefault(projectId);
        if (full is null) return (MergeResult.NoFull, null);

        List<KeyValuePair<string, FileStamp?>> stamps;
        Dictionary<string, List<string>> fullModules;
        lock (_gate)
        {
            stamps = [.. full.Stamps];
            fullModules = full.Data.Files.GroupBy(f => f.Module).ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList());
        }
        // File IO outside the lock: which of the full run's files changed since.
        var changed = stamps.Where(kv => Changed(kv.Key, kv.Value)).Select(kv => kv.Key).ToHashSet(FileComparer);
        var newStamps = new Dictionary<string, FileStamp?>(FileComparer);

        int filesMerged, filesChanged;
        lock (_gate)
        {
            if (!ReferenceEquals(_projects.GetValueOrDefault(projectId), full)) return (MergeResult.NoFull, null); // a full run replaced it meanwhile
            (filesMerged, filesChanged) = UnionSubsetLocked(full, subset, changed, fullModules, newStamps);
        }
        foreach (var p in newStamps.Keys.ToList()) newStamps[p] = Stamp(p);
        lock (_gate)
        {
            foreach (var (p, s) in newStamps) full.Stamps[p] = s;
            var (staleChanged, addedChanged) = UpdateStaleLocked(projectId, full, changed);
            if (filesChanged > 0 || addedChanged) _store?.Schedule(); // a staleness change alone is re-checked at startup

            var result = new MergeResult(true, filesMerged, filesChanged, changed.Order(StringComparer.Ordinal).ToList());
            if (filesChanged == 0 && !staleChanged) return (result, null);
            if (filesChanged > 0)
                _merged.Add(new CoverageMergedRun { RunId = runId, GeneratedAt = Json.Now(), ProjectId = projectId, TestCount = testCount, FilesChanged = filesChanged });
            return (result, RebuildLocked(filesChanged > 0 ? runId : _runId));
        }
    }

    /// <summary>
    /// Merges the subset's files into the full coverage where the code is unchanged; returns the files merged and the
    /// ones of them whose covered lines changed. Files added since the full run go to <paramref name="changed"/>, files of
    /// modules the full run never loaded to <paramref name="newStamps"/> (stamped by the caller, outside the lock).
    /// </summary>
    private static (int Merged, int Changed) UnionSubsetLocked(ProjectFull full, CoverageAccumulator subset, HashSet<string> changed,
        Dictionary<string, List<string>> fullModules, Dictionary<string, FileStamp?> newStamps)
    {
        var filesMerged = 0;
        var filesChanged = 0;
        foreach (var (path, module) in subset.Files)
        {
            if (full.Data.HasFile(path))
            {
                if (changed.Contains(path)) continue; // other line numbers now, and the other tests' coverage of it is unknown
            }
            else if (fullModules.ContainsKey(module))
            {
                changed.Add(path); // a file added since the full run: only the subset's tests measured it
                continue;
            }
            else newStamps[path] = null; // a module no test of the full run loaded: stamped below, outside the lock
            var before = full.Data.CoveredLines(path);
            full.Data.UnionFile(subset, path);
            if (full.Data.CoveredLines(path) != before) filesChanged++;
            filesMerged++;
        }
        return (filesMerged, filesChanged);
    }

    /// <summary>The project's stale files become <paramref name="changed"/> (plus files added since its full run).</summary>
    private (bool StaleChanged, bool AddedChanged) UpdateStaleLocked(string projectId, ProjectFull full, HashSet<string> changed)
    {
        if (_stale.TryGetValue(projectId, out var before))
            changed.UnionWith(before.Where(p => !full.Stamps.ContainsKey(p))); // files added since the full run stay listed
        var staleChanged = before is null || !before.SetEquals(changed);
        var addedChanged = !changed.Where(p => !full.Stamps.ContainsKey(p)).ToHashSet(FileComparer)
            .SetEquals(before?.Where(p => !full.Stamps.ContainsKey(p)) ?? []);
        _stale[projectId] = changed;
        return (staleChanged, addedChanged);
    }

    /// <summary>Re-checks which files of the report changed since their full run; the new report when that changed.</summary>
    public CoverageReport? RefreshStale()
    {
        List<(string ProjectId, Dictionary<string, FileStamp?> Stamps)> projects;
        lock (_gate)
        {
            if (_report is null) return null;
            projects = _projects.Select(kv => (kv.Key, kv.Value.Stamps)).ToList();
        }
        var changed = new Dictionary<string, HashSet<string>>();
        foreach (var (projectId, stamps) in projects)
        {
            var set = changed[projectId] = new HashSet<string>(FileComparer);
            List<KeyValuePair<string, FileStamp?>> entries;
            lock (_gate) entries = stamps.ToList();
            foreach (var (path, stamp) in entries)
                if (Changed(path, stamp)) set.Add(path);
        }
        lock (_gate)
        {
            if (_report is null) return null;
            var any = false;
            foreach (var (projectId, set) in changed)
            {
                if (!_projects.TryGetValue(projectId, out var project)) continue;
                if (_stale.TryGetValue(projectId, out var before))
                    set.UnionWith(before.Where(p => !project.Stamps.ContainsKey(p))); // files added since the full run stay listed
                if (before is not null && before.SetEquals(set)) continue;
                _stale[projectId] = set;
                any = true;
            }
            return any ? RebuildLocked(_runId) : null;
        }
    }

    /// <summary>
    /// Moves a project's part of the report to another key: a project that joined the workspace while running gets the
    /// part kept under its relative path (<see cref="FullCoverageStore"/>), and a removed one's part moves to its path so
    /// its id can go to another project. Null when there is no such part (nothing changed).
    /// </summary>
    public CoverageReport? RenameProject(string from, string to)
    {
        lock (_gate)
        {
            if (from == to || !_projects.Remove(from, out var full)) return null;
            if (_projects.ContainsKey(to)) _order.Remove(to); // the part already under the new key is kept
            else _projects[to] = full;
            var i = _order.IndexOf(from);
            if (i >= 0) _order[i] = to;
            if (_stale.Remove(from, out var stale) && !_stale.ContainsKey(to)) _stale[to] = stale;
            for (var m = 0; m < _merged.Count; m++)
                if (_merged[m].ProjectId == from) _merged[m] = _merged[m] with { ProjectId = to };
            var report = RebuildLocked(_runId);
            _store?.Schedule();
            return report;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static StringComparer FileComparer => PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private CoverageReport RebuildLocked(string runId)
    {
        var acc = new CoverageAccumulator();
        foreach (var id in _order) acc.Add(_projects[id].Data);
        var latest = _order.Select(id => _projects[id]).MaxBy(p => p.GeneratedAt, StringComparer.Ordinal)!;
        _runId = runId;
        var stale = _stale.Values.SelectMany(p => p).ToHashSet(FileComparer);
        var report = acc.Build(runId, _order) with
        {
            GeneratedAt = latest.GeneratedAt,
            Settings = _order.Select(id => _projects[id].Settings).OfType<CoverageSettingsInfo>().ToList() is { Count: > 0 } s ? s : null,
            Merged = _merged.Count > 0 ? [.. _merged] : null,
            StaleFiles = stale.Count > 0 ? [.. stale.Order(StringComparer.Ordinal)] : null,
            RestoredAt = _order.Select(id => _projects[id]).Where(p => p.Restored).Select(p => p.GeneratedAt).Max(StringComparer.Ordinal),
        };
        foreach (var f in report.Files.Where(f => stale.Contains(f.Path))) f.Stale = true;
        return _report = report;
    }

    internal static FileStamp? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            using var stream = File.OpenRead(path);
            return new FileStamp(info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Did the file change since <paramref name="stamp"/>? Same length and write time = unchanged; else the content decides.</summary>
    internal static bool Changed(string path, FileStamp? stamp)
    {
        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return stamp is not null; }
        if (!info.Exists) return stamp is not null;
        if (stamp is null) return true;
        if (info.Length != stamp.Length) return true;
        if (info.LastWriteTimeUtc == stamp.WriteUtc) return false;
        return Stamp(path)?.Hash != stamp.Hash;
    }
}
