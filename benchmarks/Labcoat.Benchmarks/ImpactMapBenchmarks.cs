using BenchmarkDotNet.Attributes;
using Labcoat.Benchmarks.Synthetic;

namespace Labcoat.Benchmarks;

/// <summary>
/// The test impact map (ImpactMap.cs) of a fully mapped suite: every test class recorded with the method spans it
/// executes in its own product file and in a neighbour's (class-level data; the first class per project per test).
/// Lookups for a changed file: <see cref="ImpactMap.AffectedByLines"/> (run affected: a <c>git diff -U0</c> line range),
/// <see cref="ImpactMap.Affected"/> (smart watch: re-read the file, prefix/suffix diff against the snapshot, overlap) and
/// <see cref="ImpactMap.TestsCoveringLine"/> (the coverage gutter), plus <see cref="ImpactMap.ParseCobertura"/> of one
/// project's report (what a coverage process adds to the map).
/// </summary>
[MemoryDiagnoser]
public class ImpactMapBenchmarks
{
    [Params(1_000, 20_000)]
    public int Tests { get; set; }

    private string _dir = "";
    private SyntheticSuite _suite = null!;
    private ImpactMap _map = null!;
    private string _projectId = "";
    private string _file = "";
    private string _editedFile = "";
    private string _editedProjectId = "";
    private string _xml = "";
    private readonly HashSet<string> _excluded = [];
    private List<LineSpan> _changedInMethod = [];
    private List<LineSpan> _changedOutside = [];

    [GlobalSetup]
    public void Setup()
    {
        _dir = SyntheticSuite.NewTempDir("impact");
        _suite = SyntheticSuite.Create(Tests, PathUtil.CanonicalizeOrSelf(Path.Combine(_dir, "repo")), historyRuns: 0);
        _suite.WriteProductFiles();
        _map = new ImpactMap();
        foreach (var group in _suite.Classes.Select((c, i) => (Class: c, Index: i)).GroupBy(x => x.Class.ProjectId))
        {
            var classes = group.ToList();
            for (var k = 0; k < classes.Count; k++)
            {
                var (c, i) = classes[k];
                var neighbour = classes[(k + 1) % classes.Count];
                var coverage = new Dictionary<string, List<LineSpan>>
                {
                    [c.ProductFile] = SyntheticSuite.MethodSpans(i),
                    [neighbour.Class.ProductFile] = SyntheticSuite.MethodSpans(neighbour.Index).Take(3).ToList(),
                };
                if (k == 0)
                    foreach (var id in c.TestIds) _map.Record(c.ProjectId, [id], coverage); // per-test data
                else
                    _map.Record(c.ProjectId, c.TestIds, coverage);
            }
        }
        var target = _suite.Classes[_suite.Classes.Count / 2];
        _projectId = target.ProjectId;
        _file = target.ProductFile;
        var spans = SyntheticSuite.MethodSpans(_suite.Classes.Count / 2);
        _changedInMethod = [new LineSpan(spans[1].Start + 2, spans[1].Start + 4)];
        _changedOutside = [new LineSpan(1, 2)];

        // smart watch: a file edited after its coverage was recorded (one line in the middle of a method changed)
        var edited = _suite.Classes[_suite.Classes.Count / 3];
        _editedFile = edited.ProductFile;
        _editedProjectId = edited.ProjectId;
        var lines = File.ReadAllLines(_editedFile);
        var at = SyntheticSuite.MethodSpans(_suite.Classes.Count / 3)[2].Start + 3;
        lines[at - 1] += " // edited";
        File.WriteAllLines(_editedFile, lines);

        _xml = Path.Combine(_dir, "project.cobertura.xml");
        Cobertura.Write(_xml, _suite.Root, _suite.Classes.Where(c => c.ProjectId == _projectId));

        // every lookup must find something, or the benchmark would measure an early return
        if (AffectedByLines_InMethod() == 0 || AffectedByLines_OutsideMethods() == 0 || Affected_EditedFile() == 0
            || TestsCoveringLine() == 0 || ParseCobertura() == 0)
            throw new InvalidOperationException("ImpactMapBenchmarks: a lookup found no tests; the synthetic input is wrong.");
    }

    [GlobalCleanup]
    public void Cleanup() => SyntheticSuite.DeleteTempDir(_dir);

    [Benchmark]
    public int AffectedByLines_InMethod() => _map.AffectedByLines(_projectId, _file, _changedInMethod, out _)?.Count ?? 0;

    [Benchmark]
    public int AffectedByLines_OutsideMethods() => _map.AffectedByLines(_projectId, _file, _changedOutside, out _)?.Count ?? 0;

    /// <summary>Smart watch: the changed range comes from diffing the file against its snapshot.</summary>
    [Benchmark]
    public int Affected_EditedFile() => _map.Affected(_editedProjectId, _editedFile, out _)?.Count ?? 0;

    [Benchmark]
    public int TestsCoveringLine() => _map.TestsCoveringLine(_file, _changedInMethod[0].Start).Count();

    [Benchmark]
    public int ParseCobertura() => ImpactMap.ParseCobertura(_xml, _excluded).Count;
}
