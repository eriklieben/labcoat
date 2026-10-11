using BenchmarkDotNet.Attributes;

namespace Labcoat.Benchmarks;

/// <summary>
/// Live console output while tests run (LiveOutput.cs): attributing what the test host writes to the tests running at
/// that moment and draining it into <c>testOutput</c> events. Per invocation: <see cref="Running"/> tests start,
/// 2,000 lines arrive and are drained in 100 ms flushes (a fake clock, past the attribution delay). Sources: plain host
/// stdout lines (time-based attribution, every running test gets them), Labcoat.Testing marker lines
/// (<c>labcoat:live:</c> + base64 JSON: exact attribution), xUnit's <c>OUTPUT: [name]</c> lines, and MTP's cumulative
/// in-progress output (<see cref="LiveOutput.Partial"/>).
/// </summary>
[MemoryDiagnoser]
public class LiveOutputBenchmarks
{
    private const int Lines = 2_000;
    private const int LinesPerFlush = 100;
    private const int LinesPerPartial = 10;

    /// <summary>Tests running in parallel.</summary>
    [Params(1, 8)]
    public int Running { get; set; }

    private const string ProjectId = "Contoso.Billing.Tests";
    private TestNode[] _tests = [];
    private string[] _hostLines = [];
    private string[] _markerLines = [];
    private string[] _xunitLines = [];
    private string[][] _partials = [];

    [GlobalSetup]
    public void Setup()
    {
        _tests = Enumerable.Range(0, Running).Select(i => new TestNode
        {
            Id = $"Contoso.Billing.Tests.Api.OrderTests.Handle_{i}", ProjectId = ProjectId, DisplayName = $"Handle_{i}",
            Namespace = "Contoso.Billing.Tests.Api", ClassName = "OrderTests", MethodName = $"Handle_{i}",
        }).ToArray();
        _hostLines = Enumerable.Range(0, Lines).Select(i => $"info: Contoso.Billing.OrderService[0] Processing order {i} for customer {i % 97} at step {i % 13}").ToArray();
        _markerLines = Enumerable.Range(0, Lines).Select(i =>
        {
            var t = _tests[i % _tests.Length];
            return LiveOutput.FormatMarker("stdout", $"Processing order {i} for customer {i % 97}", "Contoso.Billing.Tests.Api.OrderTests", t.MethodName, t.DisplayName);
        }).ToArray();
        _xunitLines = Enumerable.Range(0, Lines).Select(i => $"{LiveOutput.XunitPrefix}{_tests[i % _tests.Length].DisplayName}] Processing order {i}").ToArray();
        // cumulative stdout per test, as MTP resends it with every in-progress update (one update per 10 lines)
        _partials = _tests.Select(_ =>
        {
            var acc = "";
            return Enumerable.Range(0, Lines / LinesPerPartial / _tests.Length)
                .Select(u => acc += string.Concat(Enumerable.Range(0, LinesPerPartial).Select(k => $"Processing order {u * LinesPerPartial + k}\n")))
                .ToArray();
        }).ToArray();
    }

    [Benchmark(Baseline = true)]
    public int HostLines() => Run(LiveOutputSupport.None, (o, i) => o.HostLine(ProjectId, _hostLines[i], false));

    [Benchmark]
    public int MarkerLines() => Run(LiveOutputSupport.None, (o, i) => o.HostLine(ProjectId, _markerLines[i], false));

    [Benchmark]
    public int XunitLines() => Run(new LiveOutputSupport(XunitLiveOption: true, TUnit: false), (o, i) => o.HostLine(ProjectId, _xunitLines[i], false));

    [Benchmark]
    public int CumulativePartials() => Run(LiveOutputSupport.None, (o, i) =>
    {
        if ((i + 1) % LinesPerPartial != 0) return;
        var update = i / LinesPerPartial;
        var t = update % _tests.Length;
        o.Partial(ProjectId, _tests[t].Id, _partials[t][update / _tests.Length], null);
    });

    private int Run(LiveOutputSupport support, Action<LiveOutput, int> feed)
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var output = new LiveOutput(flush: null, clock: () => now);
        output.Configure(ProjectId, support);
        foreach (var t in _tests) output.Started(t);
        var events = 0;
        for (var i = 0; i < Lines; i++)
        {
            feed(output, i);
            if ((i + 1) % LinesPerFlush == 0)
            {
                now += LiveOutput.FlushInterval + LiveOutput.AttributionDelay;
                events += output.Drain().Count;
            }
        }
        return events;
    }
}
