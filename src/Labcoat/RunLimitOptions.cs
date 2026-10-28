namespace Labcoat;

/// <summary>
/// The run limits of a Labcoat host (RunLimits.cs): projects at once, the hang threshold and the per-test timeout. Null =
/// the default.
/// </summary>
public sealed record RunLimitOptions(int? MaxParallelProjects = null, TimeSpan? HangThreshold = null, TimeSpan? TestTimeout = null)
{
    public static readonly RunLimitOptions Default = new();

    public int EffectiveMaxParallel => MaxParallelProjects ?? RunLimits.DefaultMaxParallel;
    public TimeSpan EffectiveHangThreshold => HangThreshold ?? RunLimits.DefaultHangThreshold;
    public TimeSpan EffectiveTestTimeout => TestTimeout ?? TimeSpan.Zero;
}
