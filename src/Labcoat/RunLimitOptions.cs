namespace Labcoat;

/// <summary>
/// The run limits of a Labcoat host (RunLimits.cs): projects at once, the hang threshold, the per-test timeout and the
/// <c>--run</c> duration budget. Null = the default.
/// </summary>
public sealed record RunLimitOptions(int? MaxParallelProjects = null, TimeSpan? HangThreshold = null, TimeSpan? TestTimeout = null,
    TimeSpan? MaxTestDuration = null)
{
    public static readonly RunLimitOptions Default = new();

    public int EffectiveMaxParallel => MaxParallelProjects ?? RunLimits.DefaultMaxParallel;
    public TimeSpan EffectiveHangThreshold => HangThreshold ?? RunLimits.DefaultHangThreshold;
    public TimeSpan EffectiveTestTimeout => TestTimeout ?? TimeSpan.Zero;
    /// <summary>The budget of <c>--run</c>: a test that took longer fails the run. Zero = no budget.</summary>
    public TimeSpan EffectiveMaxTestDuration => MaxTestDuration ?? TimeSpan.Zero;
}
