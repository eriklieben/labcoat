using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Labcoat.Benchmarks;

/// <summary>
/// Entry point: BenchmarkDotNet's switcher (every command-line option of BenchmarkDotNet works: <c>--filter</c>,
/// <c>--list flat</c>, <c>--job short</c>, <c>--inProcess</c>, <c>--artifacts</c>, ...) plus <c>--quick</c>, a short job
/// (1 launch, 1 warm-up and 3 measured iterations) for a fast local check and the nightly CI run. See benchmarks/README.md.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
        args = [.. args.Where(a => !a.Equals("--quick", StringComparison.OrdinalIgnoreCase))];

        // Markdown (GitHub) for the job summary / a PR, JSON (brief: statistics and memory, no raw measurements) for
        // eng/bdn-compare.mjs. The default config's CSV and HTML exporters stay.
        IConfig config = DefaultConfig.Instance
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(MarkdownExporter.GitHub, JsonExporter.Brief);
        if (quick)
            config = config.AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(1).WithIterationCount(3).WithId("Quick"));

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        // Non-zero when a benchmark failed or did not validate, so a CI step shows it (the CI job itself doesn't gate).
        return summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success)) ? 1 : 0;
    }
}
