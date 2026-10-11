using BenchmarkDotNet.Attributes;
using Labcoat.Benchmarks.Synthetic;

namespace Labcoat.Benchmarks;

/// <summary>
/// Coverage after a run (Coverage.cs, FullCoverage.cs): parsing the Cobertura reports of every test project
/// (<see cref="CoverageAccumulator.AddCobertura(string, ISet{string})"/>), building the <see cref="CoverageReport"/>,
/// and the full report: <see cref="FullCoverage.SetFull"/> per project (clone + SHA-256 stamp of every source file +
/// rebuild), merging a subset run (<see cref="FullCoverage.MergeSubset"/>: stale check of every file + union) and
/// <see cref="FullCoverage.RefreshStale"/>. The product files exist on disk (a temp directory), so stamps and stale
/// checks do real file IO. One product file per test class: ~80 files for 1,000 tests, ~1,550 for 20,000.
/// </summary>
[MemoryDiagnoser]
public class CoverageBenchmarks
{
    [Params(1_000, 20_000)]
    public int Tests { get; set; }

    private string _dir = "";
    private SyntheticSuite _suite = null!;
    private readonly List<(string ProjectId, string Xml)> _reports = [];
    private HashSet<string> _testAssemblies = [];
    private CoverageAccumulator _all = null!;
    private readonly Dictionary<string, CoverageAccumulator> _perProject = [];
    private CoverageAccumulator _subset = null!;
    private FullCoverage _full = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = SyntheticSuite.NewTempDir("coverage");
        _suite = SyntheticSuite.Create(Tests, Path.Combine(_dir, "repo"), historyRuns: 0);
        _suite.WriteProductFiles();
        _testAssemblies = [.. _suite.Projects.Select(p => p.Id)];
        foreach (var projectId in _suite.Projects.Select(p => p.Id))
        {
            var xml = Path.Combine(_dir, projectId + ".cobertura.xml");
            Cobertura.Write(xml, _suite.Root, _suite.Classes.Where(c => c.ProjectId == projectId));
            _reports.Add((projectId, xml));
        }
        // a run of selected tests of the first project: 10% of its classes, every method hit
        var first = _suite.Projects[0].Id;
        var subsetClasses = _suite.Classes.Where(c => c.ProjectId == first).Where((_, i) => i % 10 == 0).ToList();
        var subsetXml = Path.Combine(_dir, "subset.cobertura.xml");
        Cobertura.Write(subsetXml, _suite.Root, subsetClasses, hitFactor: 3, coverAll: true);

        _all = new CoverageAccumulator();
        foreach (var (projectId, xml) in _reports)
        {
            var acc = new CoverageAccumulator();
            acc.AddCobertura(xml, _testAssemblies);
            _perProject[projectId] = acc;
            _all.AddCobertura(xml, _testAssemblies);
        }
        _subset = new CoverageAccumulator();
        _subset.AddCobertura(subsetXml, _testAssemblies);

        _full = NewFull();
        var merged = _full.MergeSubset(first, _subset, "run-subset", subsetClasses.Sum(c => c.TestIds.Count)); // steady state: merged once
        if (merged.Result.FilesMerged == 0 || merged.Result.FilesChanged == 0 || _all.FileCount != _suite.Classes.Count)
            throw new InvalidOperationException("CoverageBenchmarks: the synthetic reports don't line up with the product files.");
    }

    [GlobalCleanup]
    public void Cleanup() => SyntheticSuite.DeleteTempDir(_dir);

    /// <summary>Parse every test project's report into one accumulator (a coverage run of everything).</summary>
    [Benchmark]
    public int ParseCobertura()
    {
        var acc = new CoverageAccumulator();
        foreach (var (_, xml) in _reports) acc.AddCobertura(xml, _testAssemblies);
        return acc.FileCount;
    }

    /// <summary>The report of a run (per-file totals, relative paths, sorted).</summary>
    [Benchmark]
    public int BuildReport() => _all.Build("run-1", _suite.Projects.Select(p => p.Id)).Files.Count;

    /// <summary>The full report after a full run of every project (each part replaced and stamped, report rebuilt).</summary>
    [Benchmark]
    public int FullCoverage_SetFull() => NewFull().Report!.Files.Count;

    /// <summary>A subset run merged into the full report (stale check of the project's files + union; no change in steady state).</summary>
    [Benchmark]
    public int FullCoverage_MergeSubset() =>
        _full.MergeSubset(_suite.Projects[0].Id, _subset, "run-subset-2", 10).Result.FilesMerged;

    /// <summary>The stale check of every file of the full report after a run.</summary>
    [Benchmark]
    public bool FullCoverage_RefreshStale() => _full.RefreshStale() is not null;

    private FullCoverage NewFull()
    {
        var full = new FullCoverage();
        foreach (var (projectId, acc) in _perProject) full.SetFull(projectId, acc, "run-full", null);
        return full;
    }
}
