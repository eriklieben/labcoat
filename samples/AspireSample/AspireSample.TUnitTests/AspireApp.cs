using Aspire.Hosting;
using Labcoat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AspireSample.TUnitTests;

/// <summary>
/// Starts the Aspire AppHost once and shares it between the tests (like AspireSample.Tests' AspireAppFixture).
/// Warm-mode friendly: Labcoat keeps the test host alive between runs (<c>LABCOAT_WARM_HOST=1</c>), so the app is then
/// kept for the next run and disposed when the process exits.
/// </summary>
public static class AspireApp
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DistributedApplication? _app;
    private static int _exitHookRegistered;

    private static bool WarmHost => Environment.GetEnvironmentVariable("LABCOAT_WARM_HOST") == "1";

    public static async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_app is not null) return;
        await Gate.WaitAsync(ct);
        try
        {
            if (_app is not null) return;
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AspireSample_AppHost>(ct);
            // Under Labcoat in an AppHost: send the services' traces/logs to that AppHost's dashboard (no-op otherwise).
            LabcoatTracing.ConfigureAspireTesting(builder);
            builder.Services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddFilter(builder.Environment.ApplicationName, LogLevel.Warning);
                logging.AddFilter("Aspire.", LogLevel.Warning);
            });

            var app = await builder.BuildAsync(ct).WaitAsync(StartupTimeout, ct);
            try
            {
                await app.StartAsync(ct).WaitAsync(StartupTimeout, ct);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("api", ct).WaitAsync(StartupTimeout, ct);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }

            _app = app;
            if (Interlocked.Exchange(ref _exitHookRegistered, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => _app?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Gate.Release();
        }
    }

    public static HttpClient CreateApiClient() =>
        (_app ?? throw new InvalidOperationException("Call EnsureStartedAsync first.")).CreateHttpClient("api");

    [After(TestSession)]
    public static async Task StopAsync()
    {
        if (WarmHost || _app is null) return; // warm: kept for the next run, disposed on process exit
        await _app.DisposeAsync();
        _app = null;
    }
}
