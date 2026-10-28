namespace Labcoat;

/// <summary>Host logging (--verbose, LABCOAT_LOG_LEVEL, --log-file; HostLog.cs).</summary>
internal static class HostLogging
{
    /// <summary>The ASP.NET Core log at <see cref="LabcoatAppOptions.LogLevel"/>, also into the <see cref="HostLog"/> (registered as a service).</summary>
    internal static HostLog Configure(WebApplicationBuilder builder, LabcoatAppOptions o)
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
    internal static void Attach(WebApplication app, HostLog hostLog)
    {
        app.Services.GetRequiredService<EventHub>().Logged += hostLog.Write;
        app.Lifetime.ApplicationStopped.Register(hostLog.Dispose);
    }
}
