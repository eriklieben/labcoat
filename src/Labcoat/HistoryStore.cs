using System.Text.Json;

namespace Labcoat;

/// <summary>
/// Persists per-test run history (last <see cref="MaxRuns"/> results) and the last known result of every test to
/// <c>&lt;LocalApplicationData&gt;/labcoat/&lt;workspace&gt;-&lt;hash&gt;/history.json</c> (see <see cref="WorkspaceIdentity"/>), so a restarted host shows
/// the previous session's results (marked <see cref="TestNode.Stale"/>) before anything runs.
/// Callers hold <see cref="Explorer.Sync"/>; the store has its own lock for the debounced background save.
/// </summary>
public sealed partial class HistoryStore : IProjectKeys, IDisposable
{
    public const int MaxRuns = 20;
    /// <summary>Number of most recent runs considered for flaky detection.</summary>
    public const int FlakyWindow = 10;
    /// <summary>Number of past runs (summary + per-test results) kept for the run switcher.</summary>
    public const int MaxStoredRuns = 50;

    private sealed class Entry(string projectId, string id)
    {
        public string ProjectId { get; } = projectId;
        public string Id { get; } = id;
        /// <summary>Copy on write: replaced (never changed) by <see cref="Record"/>, so nodes and saves can share it.</summary>
        public List<HistoryEntry> Runs { get; set; } = [];
        public StoredResult? Last { get; set; }
    }

    public sealed record StoredResult
    {
        public Outcome Outcome { get; init; }
        public double? DurationMs { get; init; }
        public string? ErrorMessage { get; init; }
        public string? ErrorType { get; init; }
        public string? StackTrace { get; init; }
        public string? StdOut { get; init; }
        public string? StdErr { get; init; }
        public string? LastRunAt { get; init; }
    }

    /// <summary>
    /// A persisted run in columns (one slot per result) instead of one <see cref="RunResult"/> object per test: a full
    /// run of 20,000 tests is ~0.5 MB instead of ~2 MB of objects. <see cref="GetRun"/> materializes the
    /// <see cref="RunDetails"/>. Immutable once created.
    /// </summary>
    private sealed class StoredRun
    {
        public required RunSummary Run { get; init; }
        public required string[] ProjectIds { get; init; }
        public required string[] Ids { get; init; }
        public required Outcome[] Outcomes { get; init; }
        /// <summary>NaN = no duration.</summary>
        public required double[] Durations { get; init; }
        /// <summary>Error message / stack trace by result index (failed tests only).</summary>
        public Dictionary<int, (string? Message, string? StackTrace)>? Errors { get; init; }

        public int Count => Ids.Length;

        public static StoredRun From(RunSummary run, List<RunResult> results)
        {
            var n = results.Count;
            var r = new StoredRun
            {
                Run = run,
                ProjectIds = new string[n],
                Ids = new string[n],
                Outcomes = new Outcome[n],
                Durations = new double[n],
                Errors = results.Any(x => x.ErrorMessage is not null || x.StackTrace is not null) ? [] : null,
            };
            for (var i = 0; i < n; i++)
            {
                var x = results[i];
                r.ProjectIds[i] = x.ProjectId;
                r.Ids[i] = x.Id;
                r.Outcomes[i] = x.Outcome;
                r.Durations[i] = x.DurationMs ?? double.NaN;
                if (x.ErrorMessage is not null || x.StackTrace is not null) r.Errors![i] = (x.ErrorMessage, x.StackTrace);
            }
            return r;
        }

        public RunDetails ToDetails()
        {
            var list = new List<RunResult>(Count);
            for (var i = 0; i < Count; i++)
            {
                var error = Errors is not null && Errors.TryGetValue(i, out var e) ? e : default;
                list.Add(new RunResult(ProjectIds[i], Ids[i], Outcomes[i], double.IsNaN(Durations[i]) ? null : Durations[i], error.Message, error.StackTrace));
            }
            return new RunDetails(Run, list);
        }
    }

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<(string ProjectId, string Id), Entry> _tests;
    private readonly List<StoredRun> _runs; // oldest first
    private readonly List<string> _projects;
    private readonly Timer _saveTimer;
    private bool _dirty;
    private readonly Action<string, string>? _log;

    public string FilePath { get; }

    public HistoryStore(IEnumerable<string> projectPaths, Action<string, string>? log = null)
    {
        _log = log;
        var paths = projectPaths.ToList();
        _projects = paths.Select(p => PathUtil.CanonicalizeOrSelf(Path.GetFullPath(p))).OrderBy(p => p, StringComparer.Ordinal).ToList();
        // One folder per workspace (HistoryStore.Workspace.cs), per-test data keyed by the project's relative path.
        Workspace = WorkspaceIdentity.Resolve(paths);
        (_relativePathById, _idByRelativePath) = ProjectKeys(Workspace, paths);
        FilePath = Path.Combine(Workspace.DataDirectory, "history.json");
        _lease = AcquireWorkspace();
        (_tests, _runs) = Load();
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        if (_lease.Owner) MigrateLegacyFolders();
    }

    private (Dictionary<(string, string), Entry>, List<StoredRun>) Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return ([], []);
            FileModel? model;
            // streamed: the file can be tens of MB, never read it into one string
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
                model = JsonSerializer.Deserialize<FileModel>(stream, Json.Options);
            var (tests, runs) = model is null ? ([], []) : Adopt(FromModel(model), model);
            _log?.Invoke("info", $"Loaded test history for {tests.Count} tests and {runs.Count} runs from {FilePath}");
            return (tests, runs);
        }
        catch (Exception ex)
        {
            _log?.Invoke(LogLevels.Warning, $"Could not read test history ({FilePath}): {ex.Message}");
            return ([], []);
        }
    }

    /// <summary>Summaries of the persisted runs, newest first.</summary>
    public List<RunSummary> Runs()
    {
        lock (_gate)
        {
            var list = new List<RunSummary>(_runs.Count);
            for (var i = _runs.Count - 1; i >= 0; i--) list.Add(_runs[i].Run.Copy());
            return list;
        }
    }

    /// <summary>A persisted run with its per-test results, or null.</summary>
    public RunDetails? GetRun(string runId)
    {
        StoredRun? run;
        lock (_gate) run = _runs.LastOrDefault(r => r.Run.RunId == runId);
        return run?.ToDetails(); // stored runs never change: materialized outside the lock
    }

    /// <summary>
    /// A freshly discovered test that has no in-memory result yet: apply the last result of a previous session
    /// (marked stale) and its history.
    /// </summary>
    public void Restore(TestNode n)
    {
        lock (_gate)
        {
            if (!_tests.TryGetValue((n.ProjectId, n.Id), out var e)) return;
            if (e.Last is { } r && n.Outcome == Outcome.NotRun)
            {
                n.Outcome = r.Outcome;
                n.DurationMs = r.DurationMs;
                n.ErrorMessage = r.ErrorMessage;
                n.ErrorType = r.ErrorType;
                n.StackTrace = r.StackTrace;
                n.StdOut = r.StdOut;
                n.StdErr = r.StdErr;
                n.LastRunAt = r.LastRunAt;
                n.Stale = true;
            }
            Decorate(n, e);
        }
    }

    /// <summary>Re-discovery replaced a node: keep the history metadata of the old one.</summary>
    public static void CarryOver(TestNode from, TestNode to)
    {
        to.History = from.History;
        to.Flaky = from.Flaky;
        to.Stale = from.Stale;
    }

    /// <summary>
    /// Appends the final results of a run (per-test history + the run snapshot for the run switcher); returns the
    /// nodes whose history changed (to publish).
    /// </summary>
    /// <param name="mixed">Repeat runs: tests whose repetitions both passed and failed (their entry gets <c>flaky: true</c>).</param>
    public List<TestNode> Record(RunSummary run, IReadOnlyCollection<TestNode> finished, ISet<(string ProjectId, string Id)>? mixed = null)
    {
        var runId = run.RunId;
        var changed = new List<TestNode>();
        lock (_gate)
        {
            RecordRunLocked(run, finished);
            foreach (var t in finished)
            {
                if (t.Outcome is Outcome.NotRun or Outcome.Queued or Outcome.Running or Outcome.Cancelled) continue;
                if (RecordResultLocked(t, runId, mixed)) changed.Add(t);
            }
            if (changed.Count > 0) ScheduleSaveLocked();
        }
        return changed;
    }

    /// <summary>The run snapshot for the run switcher (once per run id; the oldest go beyond <see cref="MaxStoredRuns"/>).</summary>
    private void RecordRunLocked(RunSummary run, IReadOnlyCollection<TestNode> finished)
    {
        var results = finished
            .Where(t => t.Outcome is not (Outcome.NotRun or Outcome.Queued or Outcome.Running))
            .Select(t => t.Outcome is Outcome.Failed or Outcome.TimedOut
                ? new RunResult(t.ProjectId, t.Id, t.Outcome, t.DurationMs, Clip(t.ErrorMessage, 2048), Clip(t.StackTrace, 4096))
                : new RunResult(t.ProjectId, t.Id, t.Outcome, t.DurationMs))
            .ToList();
        if (results.Count == 0 || _runs.Any(r => r.Run.RunId == run.RunId)) return;
        _runs.Add(StoredRun.From(run.Copy(), results));
        if (_runs.Count > MaxStoredRuns) _runs.RemoveRange(0, _runs.Count - MaxStoredRuns);
        ScheduleSaveLocked();
    }

    /// <summary>Appends a test's result to its history; false when this run is recorded for it already.</summary>
    private bool RecordResultLocked(TestNode t, string runId, ISet<(string ProjectId, string Id)>? mixed)
    {
        var key = (t.ProjectId, t.Id);
        if (!_tests.TryGetValue(key, out var e)) _tests[key] = e = new Entry(t.ProjectId, t.Id);
        if (e.Runs.Count > 0 && e.Runs[^1].RunId == runId) return false;
        var runs = new List<HistoryEntry>(e.Runs) { new(t.Outcome, t.DurationMs, t.LastRunAt ?? Json.Now(), runId,
            mixed is not null && mixed.Contains((t.ProjectId, t.Id)) ? true : null) };
        if (runs.Count > MaxRuns) runs.RemoveRange(0, runs.Count - MaxRuns);
        e.Runs = runs;
        e.Last = new StoredResult
        {
            Outcome = t.Outcome,
            DurationMs = t.DurationMs,
            ErrorMessage = t.ErrorMessage,
            ErrorType = t.ErrorType,
            StackTrace = t.StackTrace,
            StdOut = Truncate(t.StdOut),
            StdErr = Truncate(t.StdErr),
            LastRunAt = t.LastRunAt,
        };
        t.Stale = null;
        Decorate(t, e);
        return true;
    }

    private static void Decorate(TestNode n, Entry e)
    {
        n.History = e.Runs.Count > 0 ? e.Runs : null;
        n.Flaky = IsFlaky(e.Runs) ? true : null;
    }

    /// <summary>
    /// Flaky = both passed and failed within the last <see cref="FlakyWindow"/> runs, with at least two pass/fail
    /// transitions — a plain regression (pass…pass, fail…fail) or fix is a single transition and is not flaky.
    /// Also flaky: an entry in that window came from a repeat run with mixed results (<see cref="HistoryEntry.Flaky"/>).
    /// </summary>
    public static bool IsFlaky(IReadOnlyList<HistoryEntry> runs)
    {
        for (var i = Math.Max(0, runs.Count - FlakyWindow); i < runs.Count; i++)
            if (runs[i].Flaky == true) return true;
        var flips = 0;
        bool? prev = null;
        for (var i = Math.Max(0, runs.Count - FlakyWindow); i < runs.Count; i++)
        {
            bool? ok = runs[i].Outcome switch
            {
                Outcome.Passed => true,
                Outcome.Failed or Outcome.TimedOut => false,
                _ => null,
            };
            if (ok is null) continue;
            if (prev is not null && prev != ok) flips++;
            prev = ok;
        }
        return flips >= 2;
    }

    private static string? Clip(string? s, int max) => s is not null && s.Length > max ? s[..max] + "\n… (truncated)" : s;

    private static string? Truncate(string? s) => s is { Length: > 64 * 1024 } ? s[..(64 * 1024)] + "\n… (truncated)" : s;

    /// <summary>
    /// Writes the history file if anything changed: streamed (<see cref="Write"/>) to a temp file that then replaces
    /// the old one, so a crash mid-write never leaves a truncated history. The data is captured under the lock (cheap:
    /// per-test lists and stored runs are immutable), the file is written outside it.
    /// </summary>
    public void Save()
    {
        lock (_saveGate)
        {
            if (!EnsureWriter()) return; // another Labcoat owns this workspace's data (HistoryStore.Workspace.cs)
            Snapshot snapshot;
            lock (_gate)
            {
                if (!_dirty) return;
                _dirty = false;
                // Copies: the project list can change while the file is written (RegisterProject / ForgetProject).
                var paths = new Dictionary<string, string>(_relativePathById, StringComparer.Ordinal);
                snapshot = new Snapshot([.. _projects], [.. _tests.Values.Select(e => new TestSnapshot(e.ProjectId, e.Id, e.Runs, e.Last))], [.. _runs])
                {
                    PathOf = id => RelativePathOf(paths, id),
                };
            }
            var tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                    Write(stream, snapshot);
                File.Move(tmp, FilePath, overwrite: true);
                _saveFailed = false;
            }
            catch (Exception ex)
            {
                _saveFailed = true;
                _log?.Invoke(LogLevels.Warning, $"Could not save test history ({FilePath}): {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort: a leftover temp file is overwritten by the next save */ }
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Save();
        _lease.Dispose();
    }
}
