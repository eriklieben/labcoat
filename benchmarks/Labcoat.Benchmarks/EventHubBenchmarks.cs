using BenchmarkDotNet.Attributes;
using Labcoat.Benchmarks.Synthetic;

namespace Labcoat.Benchmarks;

/// <summary>
/// Serialization of the WebSocket events (EventHub.cs → <see cref="PooledMessage.Serialize(ServerEvent)"/>): the
/// <c>state</c> snapshot a client gets on connect (full: history and console output per test; lean: the <c>?lean=1</c>
/// shape the UI uses, with <c>files</c> + <c>fileIndex</c>), a <c>testsUpdated</c> batch (every test, as after a
/// discovery) and the result-only <c>testResults</c> patches of a finished run (<see cref="TestUpdateEncoder"/>).
/// The lean snapshot is built here like <c>Explorer.Snapshot(lean: true)</c> does; the payload shapes follow
/// docs/contract.md and may change with it (benchmarks/README.md, "When the product code changes").
/// </summary>
[MemoryDiagnoser]
public class EventHubBenchmarks
{
    [Params(1_000, 20_000)]
    public int Tests { get; set; }

    private StateEvent _full = null!;
    private StateEvent _lean = null!;
    private TestsUpdatedEvent _updatedFull = null!;
    private TestsUpdatedEvent _updatedLean = null!;
    private List<(TestNode Prev, TestNode Next)> _changed = [];

    [GlobalSetup]
    public void Setup()
    {
        var suite = SyntheticSuite.Create(Tests, "/home/dev/src/contoso");
        _full = new StateEvent(State(suite, suite.Tests, lean: false, files: null));

        var files = new List<string>();
        var fileIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var leanTests = new List<TestNode>(suite.Tests.Count);
        foreach (var t in suite.Tests)
        {
            var l = t.LeanCopy();
            if (l.FilePath is { } f)
            {
                if (!fileIndex.TryGetValue(f, out var i)) { fileIndex[f] = i = files.Count; files.Add(f); }
                l.FilePath = null;
                l.FileIndex = i;
            }
            leanTests.Add(l);
        }
        _lean = new StateEvent(State(suite, leanTests, lean: true, files));

        _updatedFull = new TestsUpdatedEvent(suite.Tests);
        _updatedLean = new TestsUpdatedEvent(suite.Tests.Select(t => t.LeanCopy()).ToList());

        // A finished run: every test got a new result (duration changed, one history entry appended).
        _changed = suite.Tests.Select(prev =>
        {
            var next = prev.Copy();
            next.DurationMs = Math.Round((prev.DurationMs ?? 1) * 1.07, 2);
            next.LastRunAt = "2026-09-02T10:00:00.0000000+00:00";
            next.History = [.. prev.History ?? [], new HistoryEntry(next.Outcome, next.DurationMs, next.LastRunAt, "run-next")];
            return (prev, next);
        }).ToList();
        if (TestUpdateEncoder.Patches(_changed, lean: true) is not { } patches || patches.Projects.Sum(p => p.Results.Count) != Tests)
            throw new InvalidOperationException("EventHubBenchmarks: expected one result patch per test.");
    }

    [Benchmark]
    public long StateSnapshot_Full() => Serialize(_full);

    [Benchmark]
    public long StateSnapshot_Lean() => Serialize(_lean);

    [Benchmark]
    public long TestsUpdated_Full() => Serialize(_updatedFull);

    [Benchmark]
    public long TestsUpdated_Lean() => Serialize(_updatedLean);

    /// <summary>Diff (prev → next per test) and serialize the result-only patches of a whole run (lean clients).</summary>
    [Benchmark]
    public long TestResults_Lean()
    {
        var evt = TestUpdateEncoder.Patches(_changed, lean: true);
        return evt is null ? 0 : Serialize(evt);
    }

    private static long Serialize(ServerEvent evt)
    {
        var m = PooledMessage.Serialize(evt);
        var length = m.Length;
        m.Release(); // the segments go back to the pool, as after the last client sent it
        return length;
    }

    private static ExplorerState State(SyntheticSuite suite, List<TestNode> tests, bool lean, List<string>? files) => new()
    {
        Projects = suite.Projects,
        Tests = tests,
        LastRun = new RunSummary
        {
            RunId = "run-0011", StartedAt = "2026-09-01T09:59:00.0000000+00:00", FinishedAt = "2026-09-01T10:00:00.0000000+00:00",
            Total = tests.Count, Passed = tests.Count(t => t.Outcome == Outcome.Passed), Failed = tests.Count(t => t.Outcome == Outcome.Failed),
            Skipped = tests.Count(t => t.Outcome == Outcome.Skipped), DurationMs = 61_000, Scope = "all", Trigger = "user",
        },
        Watch = true,
        WorkspaceName = "Contoso",
        SourceRoots = [suite.Root],
        Lean = lean ? true : null,
        Files = files,
    };
}
