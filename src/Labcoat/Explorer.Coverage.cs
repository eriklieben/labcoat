namespace Labcoat;

// Code coverage runs: CoverageService.cs (their settings, the warm test host meanwhile, the reports they publish).
public sealed partial class Explorer
{
    /// <summary>Coverage.Settings / Include / Exclude (command line, settings files, AppHost).</summary>
    public CoverageOptions CoverageOptions { get; init; } = CoverageOptions.None;

    /// <summary>The full report (Coverage page): GET /api/coverage.</summary>
    public CoverageReport? FullCoverageReport => _fullCoverage.Report;

    /// <summary>The latest coverage run's own report: GET /api/coverage?scope=last.</summary>
    public CoverageReport? LastCoverageReport
    {
        get { lock (Sync) return Coverage.Last; }
    }

    private CoverageService? _coverageService;

    private CoverageService Coverage => LazyInitializer.EnsureInitialized(ref _coverageService, () => new CoverageService(_state, this, _gate, Hosts,
        _fullCoverage, CoverageOptions, _inputRoots, TestAssemblyNames, RunDotnetAsync));
}
