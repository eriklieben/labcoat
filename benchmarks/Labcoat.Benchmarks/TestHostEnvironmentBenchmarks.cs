using BenchmarkDotNet.Attributes;

namespace Labcoat.Benchmarks;

/// <summary>
/// The environment of every test host Labcoat starts (TestHostEnvironment.cs): <see cref="TestHostEnvironment.Sanitize"/>
/// of a copy of the process environment, hosted by Aspire (~110 variables: the user's, the Aspire run's, DCP's, OTEL_*,
/// Labcoat's own, a hot reload startup hook) with a pass-through list, and outside Aspire (a plain terminal).
/// The copy is part of what is measured: the host makes one per test host start.
/// </summary>
[MemoryDiagnoser]
public class TestHostEnvironmentBenchmarks
{
    private Dictionary<string, string?> _aspire = [];
    private Dictionary<string, string?> _terminal = [];
    private IReadOnlyList<string> _passThrough = [];

    [GlobalSetup]
    public void Setup()
    {
        var user = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin:/home/dev/.dotnet:/home/dev/.dotnet/tools", ["HOME"] = "/home/dev",
            ["USER"] = "dev", ["SHELL"] = "/bin/zsh", ["LANG"] = "en_US.UTF-8", ["TERM"] = "xterm-256color",
            ["DOTNET_ROOT"] = "/home/dev/.dotnet", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["NUGET_PACKAGES"] = "/home/dev/.nuget/packages",
            ["ASPIRE_CONTAINER_RUNTIME"] = "podman", ["ASPIRE_HOME"] = "/home/dev/.aspire", ["EDITOR"] = "code --wait",
        };
        for (var i = 0; i < 40; i++) user[$"APP_SETTING_{i}"] = $"value-{i}";
        _terminal = new Dictionary<string, string?>(user, StringComparer.OrdinalIgnoreCase);

        _aspire = new Dictionary<string, string?>(user, StringComparer.OrdinalIgnoreCase)
        {
            ["ASPIRE_BACKCHANNEL_PATH"] = "/tmp/aspire/cli.sock", ["ASPIRE_CLI_PID"] = "4242",
            ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "https://localhost:21000", ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "https://localhost:22000",
            ["ASPIRE_DASHBOARD_URL"] = "https://localhost:17000", ["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true",
            ["DCP_SESSION_FOLDER"] = "/tmp/dcp", ["DCP_INSTANCE_ID_PREFIX"] = "labcoat-", ["DEBUG_SESSION_PORT"] = "9999",
            ["DOTNET_WATCH"] = "1", ["DOTNET_WATCH_ITERATION"] = "3", ["DOTNET_HOTRELOAD_NAMEDPIPE_NAME"] = "pipe",
            ["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug", ["DOTNET_LAUNCH_PROFILE"] = "http",
            ["DOTNET_STARTUP_HOOKS"] = "/home/dev/.dotnet/sdk/10.0.100/DotnetTools/dotnet-watch/Microsoft.Extensions.DotNetDeltaApplier.dll",
            ["ASPNETCORE_URLS"] = "http://localhost:4670", ["PORT"] = "4670",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://localhost:21000", ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
            ["OTEL_SERVICE_NAME"] = "labcoat", ["OTEL_RESOURCE_ATTRIBUTES"] = "service.instance.id=abc", ["OTEL_BSP_SCHEDULE_DELAY"] = "1000",
            ["LABCOAT_NAME"] = "tests", ["LABCOAT_TOKEN"] = "secret", ["LABCOAT_OTLP_ENDPOINT"] = "https://localhost:21000",
            ["LABCOAT_ALLOWED_HOSTS"] = "devbox", ["LABCOAT_TEST_ENV_PASSTHROUGH"] = "ConnectionStrings__*;ASPIRE_DASHBOARD_URL;MY_FLAG",
            ["ConnectionStrings__db"] = "Host=localhost;Database=app", ["ConnectionStrings__cache"] = "localhost:6379", ["MY_FLAG"] = "on",
        };
        for (var i = 0; i < 30; i++) _aspire[$"services__api__http__{i}"] = $"http://localhost:{5000 + i}";
        _passThrough = TestHostEnvironment.ParsePassThrough(_aspire["LABCOAT_TEST_ENV_PASSTHROUGH"]);
    }

    [Benchmark]
    public int Sanitize_HostedByAspire()
    {
        var env = new Dictionary<string, string?>(_aspire, StringComparer.OrdinalIgnoreCase);
        return TestHostEnvironment.Sanitize(env, hostedByAspire: true, _passThrough).Count;
    }

    [Benchmark]
    public int Sanitize_Terminal()
    {
        var env = new Dictionary<string, string?>(_terminal, StringComparer.OrdinalIgnoreCase);
        return TestHostEnvironment.Sanitize(env, hostedByAspire: false, []).Count;
    }
}
