using System.Diagnostics;
using Aspire.Hosting;
using Labcoat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AspireSample.Tests.Integration;

/// <summary>
/// Starts the Aspire AppHost once per test assembly run and shares it between all integration test classes.
/// Startup is slow (several seconds), so it is started lazily from [ClassInitialize] of the integration classes:
/// running only the fast unit tests never boots the distributed app.
///
/// Warm-mode friendly: Labcoat keeps the test host process alive between runs and sets <c>LABCOAT_WARM_HOST=1</c>.
/// MSTest runs [ClassInitialize] and [AssemblyCleanup] for every run request in that process, while statics survive.
/// So in warm mode [AssemblyCleanup] keeps the app running (the next run reuses it via the static field) and the app
/// is disposed when the process exits instead.
/// </summary>
[TestClass]
public static class AspireAppFixture
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DistributedApplication? _app;
    private static int _exitHookRegistered;

    /// <summary>True when the test host is kept alive between runs (Labcoat warm mode).</summary>
    private static bool WarmHost => Environment.GetEnvironmentVariable("LABCOAT_WARM_HOST") == "1";

    public static DistributedApplication App =>
        _app ?? throw new InvalidOperationException("The Aspire app has not been started. Call EnsureStartedAsync first.");

    public static async Task EnsureStartedAsync(TestContext context)
    {
        if (_app is not null)
            return;

        await Gate.WaitAsync(context.CancellationToken);
        try
        {
            if (_app is not null)
                return;

            var sw = Stopwatch.StartNew();
            context.WriteLine("Starting Aspire AppHost (shared for all integration tests)...");

            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AspireSample_AppHost>(context.CancellationToken);
            // Under Labcoat in an AppHost: send the services' traces/logs to that AppHost's dashboard (no-op otherwise).
            LabcoatTracing.ConfigureAspireTesting(builder);
            builder.Services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
                // Override the AppHost's appsettings so resource console logs don't flood test output.
                logging.AddFilter(builder.Environment.ApplicationName, LogLevel.Warning);
                logging.AddFilter("Aspire.", LogLevel.Warning);
            });

            var app = await builder.BuildAsync(context.CancellationToken).WaitAsync(StartupTimeout, context.CancellationToken);
            try
            {
                await app.StartAsync(context.CancellationToken).WaitAsync(StartupTimeout, context.CancellationToken);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("api", context.CancellationToken)
                    .WaitAsync(StartupTimeout, context.CancellationToken);
            }
            catch
            {
                // Cancelled or failed half-way: don't leave a half-started app (and its DCP) behind. This matters in a
                // warm test host, where the process - and anything leaked into it - lives on for the next run.
                await app.DisposeAsync();
                throw;
            }

            _app = app;
            if (Interlocked.Exchange(ref _exitHookRegistered, 1) == 0)
            {
                // Safety net (and the normal path in warm mode): dispose the app when the process goes away.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => _app?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30));
            }
            context.WriteLine($"Aspire AppHost started and 'api' is healthy after {sw.Elapsed.TotalSeconds:F1}s.");
        }
        finally
        {
            Gate.Release();
        }
    }

    public static HttpClient CreateApiClient() => App.CreateHttpClient("api");

    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        if (WarmHost)
        {
            // Labcoat keeps this process alive for the next run: keep the app warm too (disposed on process exit).
            return;
        }

        if (_app is not null)
        {
            var sw = Stopwatch.StartNew();
            await _app.DisposeAsync();
            Console.WriteLine($"Aspire AppHost stopped in {sw.Elapsed.TotalSeconds:F1}s.");
            _app = null;
        }
    }
}
