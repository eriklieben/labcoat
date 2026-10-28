namespace Labcoat;

/// <summary>Where the server listens: <see cref="Url"/> null = leave ASPNETCORE_URLS as configured.</summary>
internal sealed record ListenAddress(string? Url, int? Port, string? Note)
{
    /// <summary>
    /// Port precedence: --port > $PORT > $ASPNETCORE_URLS > 4300. Only the default port falls back to the next free one:
    /// an explicit port is what the caller (or Aspire's proxy) will connect to. Null + <paramref name="error"/> when no
    /// port is free.
    /// </summary>
    public static ListenAddress? Resolve(int? port, LabcoatConfig config, out string? error)
    {
        error = null;
        var aspNetCoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (port is null && int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var envPort)) port = envPort;
        // "port" from a settings file: below the environment (Aspire sets PORT), used as an explicit port.
        if (port is null && config.Port is { } filePort && string.IsNullOrEmpty(aspNetCoreUrls)) port = filePort;
        if (port is null && !string.IsNullOrEmpty(aspNetCoreUrls)) return new ListenAddress(null, null, null); // keep ASPNETCORE_URLS as configured
        string? note = null;
        if (port is null)
        {
            var free = Cli.FindFreePort(Cli.DefaultPort);
            if (free is null)
            {
                error = $"Ports {Cli.DefaultPort}-{Cli.DefaultPort + Cli.PortAttempts - 1} are all in use. Pass a free one with --port.";
                return null;
            }
            if (free != Cli.DefaultPort) note = $"Port {Cli.DefaultPort} is in use, using {free} instead.";
            port = free;
        }
        return new ListenAddress($"http://localhost:{port}", port, note);
    }

    /// <summary>A listen address as a URL to open: wildcard hosts become localhost.</summary>
    public static string Display(string address)
    {
        const string Localhost = "://localhost";
        return address.Replace("://[::]", Localhost).Replace("://0.0.0.0", Localhost).Replace("://+", Localhost).Replace("://*", Localhost);
    }
}
