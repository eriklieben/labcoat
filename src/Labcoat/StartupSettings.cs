namespace Labcoat;

/// <summary>
/// Resolves each startup setting from its sources, highest first: the command line, the environment
/// (Labcoat.Aspire.Hosting sets the LABCOAT_* variables), the workspace settings file, the user settings file, the default.
/// An invalid environment value is a settings warning (<see cref="LabcoatConfig.Warnings"/>).
/// </summary>
internal static class StartupSettings
{
    /// <summary>Server option affectedBase from the environment (Labcoat.Aspire.Hosting's WithAffectedBase sets it).</summary>
    public const string AffectedBaseEnv = "LABCOAT_AFFECTED_BASE";
    /// <summary>Server option impactMap from the environment (Labcoat.Aspire.Hosting's WithImpactMap sets it).</summary>
    public const string ImpactMapEnv = "LABCOAT_IMPACT_MAP";
    /// <summary>The trace filter from the environment (Labcoat.Aspire.Hosting's WithTestTracing(filter:) sets it).</summary>
    public const string TraceFilterEnv = "LABCOAT_TRACE_FILTER";

    internal static LabcoatConfig? LoadConfig(StartOptions options, List<string> paths, out string? error)
    {
        error = null;
        try
        {
            return LabcoatConfig.Load(LabcoatConfig.Inputs.FromEnvironment(PathUtil.CommonRoot(paths), options.ConfigFile, options.NoUserConfig, options.CliUi));
        }
        catch (ConfigException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>affectedBase: --affected-base > LABCOAT_AFFECTED_BASE (Aspire WithAffectedBase) > workspace file > user file.</summary>
    internal static string? ResolveAffectedBase(StartOptions options, LabcoatConfig config)
    {
        var affectedBase = options.AffectedBase;
        if (affectedBase is null && Environment.GetEnvironmentVariable(AffectedBaseEnv) is { Length: > 0 } envBase)
        {
            if (GitRepo.IsValidRef(envBase.Trim())) affectedBase = envBase.Trim();
            else config.Warnings.Add($"{AffectedBaseEnv}: '{envBase}' is not a git ref; ignored.");
        }
        return affectedBase ?? config.AffectedBase;
    }

    /// <summary>
    /// The trace filter: --trace-filter > LABCOAT_TRACE_FILTER (Aspire WithTestTracing(filter:)) > workspace file > user
    /// file. An invalid one from the environment is an error (<paramref name="error"/>): Labcoat does not start.
    /// </summary>
    internal static string? ResolveTraceFilter(StartOptions options, LabcoatConfig config, out string? error)
    {
        error = null;
        if (options.TraceFilter is not null) return options.TraceFilter;
        if (Environment.GetEnvironmentVariable(TraceFilterEnv) is { } env && !string.IsNullOrWhiteSpace(env))
        {
            error = Labcoat.Testing.TraceFilterSyntax.Validate(env) is { } invalid ? $"{TraceFilterEnv}: {invalid}" : null;
            return error is null ? env.Trim() : null;
        }
        return config.TraceFilter;
    }

    /// <summary>impactMap: --impact-map > LABCOAT_IMPACT_MAP (Aspire WithImpactMap) > workspace file > user file > "auto".</summary>
    internal static string? ResolveImpactMap(StartOptions options, LabcoatConfig config)
    {
        var impactMap = options.ImpactMap;
        if (impactMap is null && Environment.GetEnvironmentVariable(ImpactMapEnv) is { Length: > 0 } envImpact)
        {
            impactMap = ImpactMapModes.Parse(envImpact);
            if (impactMap is null) config.Warnings.Add($"{ImpactMapEnv}: '{envImpact}' is not class, test or auto; ignored.");
        }
        return impactMap ?? config.ImpactMap;
    }

    /// <summary>
    /// maxParallelProjects / hangThreshold / testTimeout: the command line (--max-parallel, --hang-threshold, --test-timeout)
    /// &gt; LABCOAT_MAX_PARALLEL / LABCOAT_HANG_THRESHOLD / LABCOAT_TEST_TIMEOUT (Labcoat.Aspire.Hosting sets them) &gt; workspace
    /// file &gt; user file &gt; the defaults. An invalid environment value is a settings warning.
    /// </summary>
    internal static RunLimitOptions ResolveRunLimits(StartOptions options, LabcoatConfig config, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var maxParallel = options.MaxParallel ?? FromEnv(RunLimits.MaxParallelEnv, RunLimits.ParseMaxParallel, $"a number from 1 to {RunLimits.MaxParallelLimit}")
            ?? config.MaxParallelProjects;
        var hang = options.HangThreshold ?? FromEnv(RunLimits.HangThresholdEnv, RunLimits.ParseDuration, "a duration such as 90s or 2m") ?? config.HangThreshold;
        var timeout = options.TestTimeout ?? FromEnv(RunLimits.TestTimeoutEnv, RunLimits.ParseDuration, "a duration such as 30s or 5m") ?? config.TestTimeout;
        return new RunLimitOptions(maxParallel, hang, timeout);

        T? FromEnv<T>(string name, Func<string?, T?> parse, string expected) where T : struct
        {
            if (env(name) is not { Length: > 0 } raw) return null;
            if (parse(raw) is { } value) return value;
            config.Warnings.Add($"{name}: '{raw}' is not {expected}; ignored.");
            return null;
        }
    }

    /// <summary>LABCOAT_LOG_LEVEL (default Warning); --verbose lowers it to Debug at least. An invalid value is a settings warning.</summary>
    internal static LogLevel ResolveLogLevel(StartOptions options, LabcoatConfig config)
    {
        var level = LogLevel.Warning;
        if (Environment.GetEnvironmentVariable(HostLog.LevelVariable) is { Length: > 0 } env)
        {
            if (HostLog.ParseLevel(env) is { } parsed) level = parsed;
            else config.Warnings.Add($"{HostLog.LevelVariable}: '{env}' is not trace, debug, information, warning, error, critical or none; ignored.");
        }
        if (options.Verbose && level > LogLevel.Debug) level = LogLevel.Debug;
        return level;
    }

    /// <summary>--log-file, else LABCOAT_LOG_FILE, as a full path ("auto" = labcoat.log in the workspace data folder); null = none.</summary>
    internal static string? ResolveLogFile(StartOptions options, IReadOnlyList<string> projects)
    {
        var value = options.LogFile ?? (Environment.GetEnvironmentVariable(HostLog.FileVariable) is { Length: > 0 } env ? env : null);
        return value is null ? null : HostLog.ResolveFile(value, WorkspaceIdentity.Resolve(projects).DataDirectory);
    }
}
