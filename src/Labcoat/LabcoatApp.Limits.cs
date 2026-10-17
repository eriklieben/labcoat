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

public static partial class LabcoatApp
{
    /// <summary>
    /// maxParallelProjects / hangThreshold / testTimeout / maxTestDuration: the command line (--max-parallel, --hang-threshold,
    /// --test-timeout, --max-test-duration) &gt; LABCOAT_MAX_PARALLEL / LABCOAT_HANG_THRESHOLD / LABCOAT_TEST_TIMEOUT /
    /// LABCOAT_MAX_TEST_DURATION (Labcoat.Aspire.Hosting sets them) &gt; workspace
    /// file &gt; user file &gt; the defaults. An invalid environment value is a settings warning.
    /// </summary>
    internal static RunLimitOptions ResolveRunLimits(StartOptions options, LabcoatConfig config, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var maxParallel = options.MaxParallel ?? FromEnv(RunLimits.MaxParallelEnv, RunLimits.ParseMaxParallel, $"a number from 1 to {RunLimits.MaxParallelLimit}")
            ?? config.MaxParallelProjects;
        var hang = options.HangThreshold ?? FromEnv(RunLimits.HangThresholdEnv, RunLimits.ParseDuration, "a duration such as 90s or 2m") ?? config.HangThreshold;
        var timeout = options.TestTimeout ?? FromEnv(RunLimits.TestTimeoutEnv, RunLimits.ParseDuration, "a duration such as 30s or 5m") ?? config.TestTimeout;
        var budget = options.MaxTestDuration ?? FromEnv(RunLimits.MaxTestDurationEnv, RunLimits.ParseDuration, "a duration such as 2s or 500ms") ?? config.MaxTestDuration;
        return new RunLimitOptions(maxParallel, hang, timeout, budget);

        T? FromEnv<T>(string name, Func<string?, T?> parse, string expected) where T : struct
        {
            if (env(name) is not { Length: > 0 } raw) return null;
            if (parse(raw) is { } value) return value;
            config.Warnings.Add($"{name}: '{raw}' is not {expected}; ignored.");
            return null;
        }
    }

    /// <summary>
    /// The directories watched for new or removed test projects: each input folder, or the folder of an input solution
    /// (a project file given on the command line is the whole workspace: nothing to watch).
    /// </summary>
    internal static List<string> WorkspaceRootsOf(IEnumerable<string> paths) =>
        paths.Select(Path.GetFullPath)
            .Where(p => Directory.Exists(p) || Path.GetExtension(p).ToLowerInvariant() is ".sln" or ".slnx" or ".slnf")
            .Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p))
            .OfType<string>()
            .Distinct(PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();
}
