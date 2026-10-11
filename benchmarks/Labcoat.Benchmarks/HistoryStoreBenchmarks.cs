using System.Reflection;
using BenchmarkDotNet.Attributes;
using Labcoat.Benchmarks.Synthetic;

namespace Labcoat.Benchmarks;

/// <summary>
/// history.json, version 2 (columnar, HistoryStore.File.cs): <see cref="HistoryStore.Save"/> (streamed write of a full
/// store: 20 history entries per test and <see cref="HistoryStore.MaxStoredRuns"/> past runs) and the load in the
/// constructor (streamed <c>JsonSerializer.Deserialize</c> + materializing the entries), plus
/// <see cref="HistoryStore.Record"/> of one finished run. Files go to a temp <c>LABCOAT_HISTORY_DIR</c>.
/// </summary>
[MemoryDiagnoser]
public class HistoryStoreBenchmarks
{
    // The store saves only when something changed; a save benchmark marks it changed again. The debounce timer is
    // stopped so a background save never runs during a measurement. (Private fields: no product seam needed.)
    private static readonly FieldInfo DirtyField = typeof(HistoryStore).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("HistoryStore._dirty not found: update HistoryStoreBenchmarks.");
    private static readonly FieldInfo TimerField = typeof(HistoryStore).GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("HistoryStore._saveTimer not found: update HistoryStoreBenchmarks.");

    [Params(1_000, 20_000)]
    public int Tests { get; set; }

    private string _dir = "";
    private string? _previousDir;
    private string[] _projectPaths = [];
    private HistoryStore _store = null!;
    private List<TestNode> _tests = [];
    private int _run;

    public long FileBytes { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        _dir = SyntheticSuite.NewTempDir("history");
        _previousDir = Environment.GetEnvironmentVariable("LABCOAT_HISTORY_DIR");
        Environment.SetEnvironmentVariable("LABCOAT_HISTORY_DIR", _dir);
        var suite = SyntheticSuite.Create(Tests, Path.Combine(_dir, "repo"), historyRuns: 0);
        _projectPaths = [.. suite.Projects.Select(p => p.Path)];
        _tests = suite.Tests;
        _store = new HistoryStore(_projectPaths);
        // A full store: MaxStoredRuns runs of every test (the per-test history keeps the last MaxRuns of them).
        for (var r = 0; r < HistoryStore.MaxStoredRuns; r++) RecordRun(r);
        StopTimer(_store);
        _store.Save();
        FileBytes = new FileInfo(_store.FilePath).Length;
        Console.WriteLine($"// history.json for {Tests} tests: {FileBytes / 1024.0 / 1024.0:0.0} MB");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        StopTimer(_store);
        Environment.SetEnvironmentVariable("LABCOAT_HISTORY_DIR", _previousDir);
        SyntheticSuite.DeleteTempDir(_dir);
    }

    [Benchmark]
    public void Save()
    {
        DirtyField.SetValue(_store, true);
        _store.Save();
    }

    [Benchmark]
    public int Load()
    {
        using var store = new HistoryStore(_projectPaths); // Dispose saves only when something changed: nothing did
        return store.Runs().Count;
    }

    /// <summary>One finished run of every test (steady state: history and past runs are already at their caps).</summary>
    [Benchmark]
    public int Record()
    {
        var changed = RecordRun(_run++ + HistoryStore.MaxStoredRuns);
        StopTimer(_store);
        return changed;
    }

    private int RecordRun(int r)
    {
        var at = $"2026-09-{1 + r % 28:00}T10:{r % 60:00}:00.0000000+00:00";
        foreach (var t in _tests)
        {
            t.LastRunAt = at;
            t.DurationMs = Math.Round(1 + (r * 7 + t.Id.Length) % 30 + 0.25, 2);
        }
        return _store.Record(new RunSummary { RunId = $"run-{r:00000}", StartedAt = at, FinishedAt = at, Total = _tests.Count }, _tests).Count;
    }

    private static void StopTimer(HistoryStore store) => ((Timer)TimerField.GetValue(store)!).Change(Timeout.Infinite, Timeout.Infinite);
}
