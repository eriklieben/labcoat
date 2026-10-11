namespace Labcoat;

// Host logging (--verbose, LABCOAT_LOG_LEVEL, --log-file; HostLog.cs) and the diagnostics report (Diagnostics.cs).
public static partial class LabcoatApp
{
    /// <summary>LABCOAT_LOG_LEVEL (default Warning); --verbose lowers it to Debug at least. An invalid value is a settings warning.</summary>
    private static LogLevel ResolveLogLevel(StartOptions options, LabcoatConfig config)
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
    private static string? ResolveLogFile(StartOptions options, IReadOnlyList<string> projects)
    {
        var value = options.LogFile ?? (Environment.GetEnvironmentVariable(HostLog.FileVariable) is { Length: > 0 } env ? env : null);
        return value is null ? null : HostLog.ResolveFile(value, WorkspaceIdentity.Resolve(projects).DataDirectory);
    }

    /// <summary>The ASP.NET Core log at <see cref="LabcoatAppOptions.LogLevel"/>, also into the <see cref="HostLog"/> (registered as a service).</summary>
    private static HostLog ConfigureLogging(WebApplicationBuilder builder, LabcoatAppOptions o)
    {
        var hostLog = new HostLog(o.LogLevel, o.LogFile);
        builder.Logging.SetMinimumLevel(o.LogLevel);
        // The lifetime's "Now listening on…" lines repeat the banner: shown from Information (--verbose) on.
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", o.LogLevel < LogLevel.Warning ? o.LogLevel : LogLevel.Warning);
        builder.Logging.AddProvider(hostLog);
        builder.Services.AddSingleton(hostLog);
        return hostLog;
    }

    /// <summary>The explorer's log lines go to the host log too; the file is closed when the app stops.</summary>
    private static void AttachHostLog(WebApplication app, HostLog hostLog)
    {
        app.Services.GetRequiredService<EventHub>().Logged += hostLog.Write;
        app.Lifetime.ApplicationStopped.Register(hostLog.Dispose);
    }
}
