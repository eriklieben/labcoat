using BenchmarkDotNet.Attributes;
using Labcoat.Benchmarks.Synthetic;

namespace Labcoat.Benchmarks;

/// <summary>
/// coverage.json (FullCoverageStore.cs): <see cref="FullCoverageStore.Save"/> of the full report (capture under the
/// lock + streamed columnar write), <c>Load</c> (streamed read + model → persisted report) and the startup restore
/// (<c>new FullCoverage(store)</c>: load + rebuild the report + stale check of every file against its stamp).
/// </summary>
[MemoryDiagnoser]
public class FullCoverageStoreBenchmarks
{
    [Params(1_000, 20_000)]
    public int Tests { get; set; }

    private string _dir = "";
    private FullCoverageStore _store = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = SyntheticSuite.NewTempDir("fullcoverage");
        var suite = SyntheticSuite.Create(Tests, Path.Combine(_dir, "repo"), historyRuns: 0);
        suite.WriteProductFiles();
        var testAssemblies = suite.Projects.Select(p => p.Id).ToHashSet();
        _store = new FullCoverageStore(Path.Combine(_dir, "state"));
        var full = new FullCoverage(_store);
        foreach (var projectId in suite.Projects.Select(p => p.Id))
        {
            var xml = Path.Combine(_dir, projectId + ".cobertura.xml");
            Cobertura.Write(xml, suite.Root, suite.Classes.Where(c => c.ProjectId == projectId));
            var acc = new CoverageAccumulator();
            acc.AddCobertura(xml, testAssemblies);
            full.SetFull(projectId, acc, "run-full", null);
        }
        _store.Save();
        Console.WriteLine($"// coverage.json for {Tests} tests: {new FileInfo(_store.FilePath).Length / 1024.0:0} KB");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        SyntheticSuite.DeleteTempDir(_dir);
    }

    [Benchmark]
    public void Save()
    {
        _store.Schedule(); // marks it changed (the debounce timer is pushed back on every call, so it never fires here)
        _store.Save();
    }

    [Benchmark]
    public int Load() => _store.Load()?.Parts.Count ?? 0;

    /// <summary>What the host does at startup: load, restore the report, mark files changed since as stale.</summary>
    [Benchmark]
    public int Restore()
    {
        using var store = new FullCoverageStore(Path.Combine(_dir, "state"));
        return new FullCoverage(store).Report?.Files.Count ?? 0;
    }
}
