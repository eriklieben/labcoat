using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>What <see cref="LabcoatApp.Build"/> needs: the resolved workspace and host settings.</summary>
public sealed class LabcoatAppOptions
{
    /// <summary>The input paths (command line, or the resolved default).</summary>
    public required IReadOnlyList<string> Paths { get; init; }
    /// <summary>The test projects found for <see cref="Paths"/>.</summary>
    public required IReadOnlyList<string> Projects { get; init; }
    /// <summary>--root directories (absolute).</summary>
    public IReadOnlyList<string> ExtraRoots { get; init; } = [];
    /// <summary>Listen URL; null = leave ASPNETCORE_URLS as configured.</summary>
    public string? Url { get; init; }
    public required RequestGuard Guard { get; init; }
    public required string WebRoot { get; init; }
    /// <summary>Merged UI defaults (LabcoatConfig.UiDefaults).</summary>
    public JsonObject? UiDefaults { get; init; }
    public string? AspireDashboardUrl { get; init; }
    /// <summary>LABCOAT_NAME (set by Labcoat.Aspire.Hosting to the resource name).</summary>
    public string? WorkspaceName { get; init; }
    public bool Traces { get; init; }
    /// <summary>The trace filter passed to test hosts while tracing is on (--trace-filter > LABCOAT_TRACE_FILTER > settings files).</summary>
    public string? TraceFilter { get; init; }
    /// <summary>Server option affectedBase (CLI > LABCOAT_AFFECTED_BASE > settings files).</summary>
    public string? AffectedBase { get; init; }
    /// <summary>Server option impactMap (CLI > LABCOAT_IMPACT_MAP > settings files; default "auto").</summary>
    public string? ImpactMap { get; init; }
    /// <summary>Coverage settings file and include / exclude globs (settings files &lt; command line).</summary>
    public CoverageOptions Coverage { get; init; } = CoverageOptions.None;
    /// <summary>maxParallelProjects, hangThreshold, testTimeout, maxTestDuration (RunLimitOptions.cs).</summary>
    public RunLimitOptions Limits { get; init; } = RunLimitOptions.Default;
    /// <summary>The shared quarantine list (--quarantine-file > "quarantineFile" of the settings files), or null.</summary>
    public QuarantineFile? QuarantineFile { get; init; }
    /// <summary>The settings files in use (for the diagnostics report).</summary>
    public IReadOnlyList<string> SettingsFiles { get; init; } = [];
    /// <summary>The ASP.NET Core log level (--verbose / LABCOAT_LOG_LEVEL; default Warning).</summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Warning;
    /// <summary>The log file (full path), or null.</summary>
    public string? LogFile { get; init; }
}
