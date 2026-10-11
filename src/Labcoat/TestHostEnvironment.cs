namespace Labcoat;

/// <summary>
/// The environment of the test processes Labcoat starts: the same as <c>dotnet test</c> in the terminal Labcoat was
/// started from. Labcoat hosted by an Aspire AppHost (<c>aspire run</c>, an IDE, <c>dotnet run</c> of the AppHost)
/// inherits that run's variables. A test host must not see them:
/// <list type="bullet">
/// <item><c>ASPIRE_BACKCHANNEL_PATH</c> + <c>ASPIRE_CLI_PID</c> make a nested AppHost (DistributedApplicationTestingBuilder,
/// a publish) talk to the outer <c>aspire</c> CLI: it hangs, and <c>aspire stop</c> lists the test host as an AppHost;</item>
/// <item><c>ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL</c>, <c>ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL</c>, … point a nested AppHost
/// at the outer dashboard (and end up in app-model snapshots);</item>
/// <item><c>OTEL_*</c> (from <c>WithOtlpExporter()</c>) describe the Labcoat resource itself;</item>
/// <item>Labcoat's own listen address, token and settings are for the Labcoat process only.</item>
/// </list>
/// Variables named in <see cref="PassThroughVariable"/> (set by <c>WithPassThroughEnvironment</c> /
/// <c>WithTestEnvironment</c> in the AppHost) are always kept. User / machine settings such as
/// <c>ASPIRE_CONTAINER_RUNTIME</c> are kept too: <c>dotnet test</c> would see them as well.
/// </summary>
public static class TestHostEnvironment
{
    /// <summary>Names (or <c>PREFIX*</c> patterns) of variables the test hosts keep, separated by <c>;</c> or <c>,</c>.</summary>
    public const string PassThroughVariable = "LABCOAT_TEST_ENV_PASSTHROUGH";

    /// <summary>Set by the <c>aspire</c> CLI, the AppHost, DCP or an IDE for one run (prefixes, case-insensitive).</summary>
    internal static readonly string[] AspireRunPrefixes =
    [
        "ASPIRE_",                   // ASPIRE_BACKCHANNEL_PATH, ASPIRE_CLI_PID, ASPIRE_DASHBOARD_*, ASPIRE_DCP_PATH, ASPIRE_EXTENSION_*, ...
        "AspireCli",                 // AspireCliPath, AspireCliBundlePath (MSBuild properties of the CLI bundle)
        "DOTNET_DASHBOARD_",         // the pre-9.0 names of ASPIRE_DASHBOARD_* in older launch profiles
        "DOTNET_RESOURCE_SERVICE_",  // DOTNET_RESOURCE_SERVICE_ENDPOINT_URL (old name)
        "DCP_",                      // DCP_SESSION_FOLDER, DCP_INSTANCE_ID_PREFIX, ...
        "DEBUG_SESSION_",            // the IDE's debug session (DEBUG_SESSION_PORT, DEBUG_SESSION_TOKEN, ...)
        "DOTNET_WATCH",              // an AppHost under dotnet watch: DOTNET_WATCH, DOTNET_WATCH_ITERATION, ...
        "DOTNET_HOTRELOAD_",         // ... and its hot reload pipe
    ];

    /// <summary>Exact names set for one run, or Labcoat's own listen address (case-insensitive).</summary>
    internal static readonly HashSet<string> AspireRunNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Labcoat's listen address: a test that starts Kestrel from configuration must not try to bind Labcoat's port.
        "PORT", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "ASPNETCORE_HTTPS_PORT",
        "DOTNET_URLS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS",
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
        "DOTNET_LAUNCH_PROFILE",       // the AppHost's launch profile, not the test project's
        "DOTNET_MODIFIABLE_ASSEMBLIES", // set with the hot reload startup hook
    };

    /// <summary>
    /// <c>ASPIRE_*</c> variables that are machine or user settings, not part of one run: a nested AppHost started by a
    /// test should use them like <c>dotnet test</c> does.
    /// </summary>
    internal static readonly HashSet<string> UserSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASPIRE_CONTAINER_RUNTIME", "ASPIRE_DEPENDENCY_CHECK_TIMEOUT", "ASPIRE_SERVICE_STARTUP_WATCH_TIMEOUT",
        "ASPIRE_ENABLE_CONTAINER_TUNNEL", "ASPIRE_LOCALE_OVERRIDE", "ASPIRE_HOME", "ASPIRE_VERSION_CHECK_DISABLED",
        "ASPIRE_CLI_TELEMETRY_OPTOUT",
    };

    /// <summary>Labcoat's own settings (the explorer process only). <c>LABCOAT_OTLP_*</c> is set again when tracing is on.</summary>
    private static bool IsLabcoatOwn(string name) =>
        name.StartsWith("LABCOAT_OTLP_", StringComparison.Ordinal)
        || name.StartsWith("LABCOAT_DASHBOARD_API_", StringComparison.Ordinal)
        || name.StartsWith("LABCOAT_ALLOWED_", StringComparison.Ordinal)
        || name is "LABCOAT_NAME" or "LABCOAT_TOKEN" or "LABCOAT_APPHOST_UI_SETTINGS" or "LABCOAT_AFFECTED_BASE" or "LABCOAT_IMPACT_MAP"
            or "LABCOAT_TRACE_FILTER"
            or RunLimits.MaxParallelEnv or RunLimits.HangThresholdEnv or RunLimits.TestTimeoutEnv
        || name == PassThroughVariable;

    /// <summary>True for a variable of the Aspire run (or Labcoat's listen address) that a test host does not get.</summary>
    public static bool IsAspireRunVariable(string name, bool hostedByAspire)
    {
        if (UserSettings.Contains(name)) return false;
        if (AspireRunNames.Contains(name)) return true;
        if (AspireRunPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        // OTEL_* of the Labcoat resource (WithOtlpExporter); outside Aspire they are the user's own, so they stay.
        return hostedByAspire && name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The pass-through list of this process (<see cref="PassThroughVariable"/>); settable for tests.</summary>
    public static IReadOnlyList<string> PassThrough { get; internal set; } = ParsePassThrough(Environment.GetEnvironmentVariable(PassThroughVariable));

    private static int s_reported;

    /// <summary>The removed names, once per process (they are the same for every test host): for the log.</summary>
    internal static string? DescribeOnce(IReadOnlyCollection<string> removed)
    {
        if (removed.Count == 0 || Interlocked.Exchange(ref s_reported, 1) == 1) return null;
        var variables = removed.Count == 1 ? "variable" : "variables";
        return $"Test hosts don't inherit {removed.Count} {variables} of the Aspire run / Labcoat itself: " +
               string.Join(", ", removed) + $". Keep one with {PassThroughVariable} (AppHost: WithPassThroughEnvironment).";
    }

    /// <summary>dotnet watch / hot reload loads its agent into every process that inherits this variable.</summary>
    private const string StartupHooks = "DOTNET_STARTUP_HOOKS";

    /// <summary>The pass-through list: names, or <c>PREFIX*</c> patterns, separated by <c>;</c> or <c>,</c>.</summary>
    public static IReadOnlyList<string> ParsePassThrough(string? value) =>
        (value ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n != "*") // "keep everything" would turn the cleanup off by accident; name the variables
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool Matches(string name, IReadOnlyList<string> patterns)
    {
        foreach (var p in patterns)
        {
            if (p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Removes the Aspire-run variables and Labcoat's own settings from <paramref name="env"/> (a copy of Labcoat's
    /// environment) unless <paramref name="passThrough"/> names them, and drops dotnet watch's hot reload startup hook.
    /// Returns the removed names, sorted.
    /// </summary>
    public static List<string> Sanitize(IDictionary<string, string?> env, bool hostedByAspire, IReadOnlyList<string> passThrough)
    {
        var removed = new List<string>();
        foreach (var name in env.Keys.ToList())
        {
            if (Matches(name, passThrough) && !IsLabcoatOwn(name)) continue;
            if (IsLabcoatOwn(name) || IsAspireRunVariable(name, hostedByAspire))
            {
                env.Remove(name);
                removed.Add(name);
            }
        }
        // dotnet watch / hot reload loads its agent into every process that inherits DOTNET_STARTUP_HOOKS; keep any other hook.
        if (env.TryGetValue(StartupHooks, out var hooks) && hooks is not null && !Matches(StartupHooks, passThrough))
        {
            var kept = hooks.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(h => !h.Contains("DeltaApplier", StringComparison.OrdinalIgnoreCase) && !h.Contains("HotReload", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (kept.Count == 0)
            {
                env.Remove(StartupHooks);
                removed.Add(StartupHooks);
            }
            else if (kept.Count != hooks.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Length)
                env[StartupHooks] = string.Join(Path.PathSeparator, kept);
        }
        removed.Sort(StringComparer.Ordinal);
        return removed;
    }
}
