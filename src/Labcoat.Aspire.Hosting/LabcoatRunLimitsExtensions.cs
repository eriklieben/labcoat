using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Run limits of a Labcoat resource: how many test projects build, discover and run at once, when a running test is
/// shown as possibly hanging, and the optional per-test timeout. Passed to the explorer as <c>LABCOAT_MAX_PARALLEL</c>,
/// <c>LABCOAT_HANG_THRESHOLD</c> and <c>LABCOAT_TEST_TIMEOUT</c>.
/// </summary>
public static class LabcoatRunLimitsExtensions
{
    /// <summary>Environment variable that carries <see cref="WithMaxParallelProjects"/> to the explorer.</summary>
    public const string MaxParallelEnvironmentVariable = "LABCOAT_MAX_PARALLEL";
    /// <summary>Environment variable that carries <see cref="WithHangThreshold"/> to the explorer (seconds).</summary>
    public const string HangThresholdEnvironmentVariable = "LABCOAT_HANG_THRESHOLD";
    /// <summary>Environment variable that carries <see cref="WithTestTimeout"/> to the explorer (seconds).</summary>
    public const string TestTimeoutEnvironmentVariable = "LABCOAT_TEST_TIMEOUT";

    internal const int MaxParallelLimit = 64;

    /// <summary>Test projects discovered and run at the same time (1-64). Labcoat's default: min(processor count, 4).</summary>
    public static IResourceBuilder<LabcoatResource> WithMaxParallelProjects(this IResourceBuilder<LabcoatResource> builder, int projects)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(projects, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(projects, MaxParallelLimit);
        builder.Resource.MaxParallelProjects = projects;
        return builder.WithRunLimitsEnvironment();
    }

    /// <summary>
    /// A running test is shown as "possibly hanging" (with its elapsed time) after <paramref name="threshold"/>; Labcoat's
    /// default is 60 seconds, <see cref="TimeSpan.Zero"/> turns the mark off.
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithHangThreshold(this IResourceBuilder<LabcoatResource> builder, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, TimeSpan.Zero);
        builder.Resource.HangThreshold = threshold;
        return builder.WithRunLimitsEnvironment();
    }

    /// <summary>
    /// Per-test timeout: a test that runs longer is marked "timed out" and its project's run is cancelled (the test host is
    /// asked to stop, then killed after the cancel grace period). Off by default; <see cref="TimeSpan.Zero"/> turns it off.
    /// This is not MTP's <c>--timeout</c>, which limits a whole test session (see docs/configuration.md).
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithTestTimeout(this IResourceBuilder<LabcoatResource> builder, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        builder.Resource.TestTimeout = timeout;
        return builder.WithRunLimitsEnvironment();
    }

    /// <summary>The run limits of <see cref="LabcoatOptions"/> (AddLabcoat with options / configuration).</summary>
    internal static IResourceBuilder<LabcoatResource> WithRunLimits(this IResourceBuilder<LabcoatResource> builder, LabcoatOptions options)
    {
        if (options.MaxParallelProjects is { } max) builder.WithMaxParallelProjects(max);
        if (options.HangThreshold is { } hang) builder.WithHangThreshold(hang);
        if (options.TestTimeout is { } timeout) builder.WithTestTimeout(timeout);
        return builder;
    }

    /// <summary>Adds (once) the callback that passes the limits set when the resource starts.</summary>
    private static IResourceBuilder<LabcoatResource> WithRunLimitsEnvironment(this IResourceBuilder<LabcoatResource> builder)
    {
        if (builder.Resource.Annotations.OfType<RunLimitsAnnotation>().Any()) return builder;
        builder.WithAnnotation(new RunLimitsAnnotation());
        var resource = builder.Resource;
        return builder.WithEnvironment(ctx =>
        {
            if (resource.MaxParallelProjects is { } max) ctx.EnvironmentVariables[MaxParallelEnvironmentVariable] = max.ToString(CultureInfo.InvariantCulture);
            if (resource.HangThreshold is { } hang) ctx.EnvironmentVariables[HangThresholdEnvironmentVariable] = Seconds(hang);
            if (resource.TestTimeout is { } timeout) ctx.EnvironmentVariables[TestTimeoutEnvironmentVariable] = Seconds(timeout);
        });
    }

    private static string Seconds(TimeSpan d) => d.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Seconds, a TimeSpan ("00:01:30") or a number with ms / s / m / h; "0" / "off" = zero. Null when invalid.</summary>
    internal static TimeSpan? ParseDuration(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s is "off" or "none" or "false") return TimeSpan.Zero;
        if (s.Contains(':')) return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) && ts >= TimeSpan.Zero ? ts : null;
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0 || !double.TryParse(s[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        double? seconds = s[i..].Trim() switch
        {
            "" or "s" or "sec" => n,
            "ms" => n / 1000,
            "m" or "min" => n * 60,
            "h" => n * 3600,
            _ => null,
        };
        return seconds is { } sec ? TimeSpan.FromSeconds(sec) : null;
    }

    private sealed class RunLimitsAnnotation : IResourceAnnotation;
}
