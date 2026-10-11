using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// What a test host inherits from a Labcoat started by an Aspire AppHost (TestHostEnvironment.cs). The lists are what
/// `aspire run` (13.6.1) gave the Labcoat resource of samples/AspireSample, plus the run variables of an IDE / older
/// launch profiles / dotnet watch.
/// </summary>
[TestClass]
public class TestHostEnvironmentTests
{
    /// <summary>Variables of the Aspire run and Labcoat's own settings: a test host never sees them.</summary>
    private static readonly string[] Removed =
    [
        // aspire run 13.6.1 (observed on the Labcoat resource of samples/AspireSample, http launch profile)
        "ASPIRE_ALLOW_UNSECURED_TRANSPORT", "ASPIRE_BACKCHANNEL_PATH", "ASPIRE_BUNDLE_VERSION_DIR", "ASPIRE_CLI_LOG_FILE",
        "ASPIRE_CLI_PID", "ASPIRE_CLI_STARTED", "ASPIRE_CLI_STARTED_STABLE", "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL",
        "ASPIRE_DASHBOARD_PATH", "ASPIRE_DASHBOARD_URL", "ASPIRE_DCP_PATH", "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL",
        "ASPIRE_TERMINAL_HOST_INVOCATION_ARGS", "ASPIRE_TERMINAL_HOST_PATH", "AspireCliBundlePath", "AspireCliPath",
        // WithOtlpExporter() on the Labcoat resource
        "OTEL_BLRP_SCHEDULE_DELAY", "OTEL_BSP_SCHEDULE_DELAY", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_HEADERS",
        "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "OTEL_METRIC_EXPORT_INTERVAL",
        "OTEL_METRICS_EXEMPLAR_FILTER", "OTEL_RESOURCE_ATTRIBUTES", "OTEL_SERVICE_NAME", "OTEL_TRACES_SAMPLER",
        // the https profile, older launch profiles (pre-9.0 names), an IDE, DCP
        "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL", "DOTNET_DASHBOARD_FRONTEND_BROWSERTOKEN",
        "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL", "ASPIRE_EXTENSION_ENDPOINT", "ASPIRE_EXTENSION_TOKEN", "DEBUG_SESSION_PORT",
        "DEBUG_SESSION_TOKEN", "DEBUG_SESSION_INFO", "DCP_SESSION_FOLDER", "DCP_INSTANCE_ID_PREFIX", "DOTNET_LAUNCH_PROFILE",
        // an AppHost under dotnet watch
        "DOTNET_WATCH", "DOTNET_WATCH_ITERATION", "DOTNET_HOTRELOAD_NAMEDPIPE_NAME", "DOTNET_MODIFIABLE_ASSEMBLIES",
        // Labcoat's listen address
        "PORT", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "ASPNETCORE_HTTPS_PORT", "DOTNET_URLS",
        // Labcoat's own settings (LABCOAT_OTLP_* comes back only while tracing is on)
        "LABCOAT_TOKEN", "LABCOAT_NAME", "LABCOAT_ALLOWED_HOSTS", "LABCOAT_DASHBOARD_API_URL", "LABCOAT_DASHBOARD_API_KEY",
        "LABCOAT_APPHOST_UI_SETTINGS", "LABCOAT_AFFECTED_BASE", "LABCOAT_IMPACT_MAP", "LABCOAT_OTLP_ENDPOINT", "LABCOAT_TEST_ENV_PASSTHROUGH",
        "LABCOAT_TRACE_FILTER", "LABCOAT_OTLP_FILTER",
    ];

    /// <summary>Kept: what `dotnet test` in the same terminal would see, and what Labcoat sets on purpose.</summary>
    private static readonly string[] Kept =
    [
        "PATH", "HOME", "DOTNET_ROOT", "DOTNET_NOLOGO", "DOTNET_CLI_UI_LANGUAGE", "SSL_CERT_DIR", "MSBUILDTERMINALLOGGER",
        "DOCKER_HOST", "ConnectionStrings__db", "services__api__http__0", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT",
        // machine / user settings a nested AppHost should use, like under dotnet test
        "ASPIRE_CONTAINER_RUNTIME", "DOTNET_ASPIRE_CONTAINER_RUNTIME", "ASPIRE_DEPENDENCY_CHECK_TIMEOUT",
        "ASPIRE_SERVICE_STARTUP_WATCH_TIMEOUT", "ASPIRE_LOCALE_OVERRIDE", "ASPIRE_HOME", "ASPIRE_VERSION_CHECK_DISABLED",
        "ASPIRE_ENABLE_CONTAINER_TUNNEL", "ASPIRE_CLI_TELEMETRY_OPTOUT",
        // Labcoat on purpose
        "LABCOAT_TEST_HOST", "LABCOAT_WARM_HOST", "LabcoatSkipBundle", "DiffEngine_Disabled", "LABCOAT_HISTORY_DIR",
    ];

    private static Dictionary<string, string?> Env(IEnumerable<string> names) => names.ToDictionary(n => n, n => (string?)("value of " + n));

    [TestMethod]
    public void Aspire_run_variables_and_labcoats_own_settings_are_removed_the_rest_is_kept()
    {
        var env = Env(Removed.Concat(Kept));

        var removed = TestHostEnvironment.Sanitize(env, hostedByAspire: true, passThrough: []);

        CollectionAssert.AreEquivalent(Kept, env.Keys.ToArray());
        CollectionAssert.AreEquivalent(Removed, removed);
        CollectionAssert.AreEqual(removed.Order(StringComparer.Ordinal).ToList(), removed, "sorted");
    }

    [TestMethod]
    public void Outside_aspire_the_users_otel_variables_stay()
    {
        var env = Env(["OTEL_SERVICE_NAME", "OTEL_EXPORTER_OTLP_ENDPOINT", "ASPIRE_CLI_PID", "PATH"]);
        TestHostEnvironment.Sanitize(env, hostedByAspire: false, passThrough: []);
        CollectionAssert.AreEquivalent(new[] { "OTEL_SERVICE_NAME", "OTEL_EXPORTER_OTLP_ENDPOINT", "PATH" }, env.Keys.ToArray());
    }

    [TestMethod]
    [DataRow("ASPIRE_BACKCHANNEL_PATH", true)]
    [DataRow("aspire_cli_pid", true)] // case-insensitive (.NET configuration reads it either way)
    [DataRow("ASPIRE_CONTAINER_RUNTIME", false)]
    [DataRow("OTEL_SERVICE_NAME", true)]
    [DataRow("OTELX", false)]
    [DataRow("DOTNET_ENVIRONMENT", false)]
    [DataRow("DOTNET_DASHBOARD_RESOURCESERVICE_APIKEY", true)]
    [DataRow("ConnectionStrings__api", false)]
    public void Aspire_run_variables_are_recognized_by_name(string name, bool aspireRun) =>
        Assert.AreEqual(aspireRun, TestHostEnvironment.IsAspireRunVariable(name, hostedByAspire: true));

    [TestMethod]
    public void Pass_through_keeps_named_variables_and_prefixes_but_never_labcoats_own()
    {
        var passThrough = TestHostEnvironment.ParsePassThrough(" ASPNETCORE_URLS ; OTEL_SERVICE_NAME,aspire_dashboard_* ,*, LABCOAT_TOKEN,ASPNETCORE_URLS");
        CollectionAssert.AreEqual(new[] { "ASPNETCORE_URLS", "OTEL_SERVICE_NAME", "aspire_dashboard_*", "LABCOAT_TOKEN" }, passThrough.ToArray(),
            "trimmed, de-duplicated, and a lone * (everything) is ignored");
        Assert.IsEmpty(TestHostEnvironment.ParsePassThrough(null));

        var env = Env(["ASPNETCORE_URLS", "OTEL_SERVICE_NAME", "OTEL_RESOURCE_ATTRIBUTES", "ASPIRE_DASHBOARD_URL", "ASPIRE_CLI_PID", "LABCOAT_TOKEN"]);
        var removed = TestHostEnvironment.Sanitize(env, hostedByAspire: true, passThrough);
        CollectionAssert.AreEquivalent(new[] { "ASPNETCORE_URLS", "OTEL_SERVICE_NAME", "ASPIRE_DASHBOARD_URL" }, env.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "ASPIRE_CLI_PID", "LABCOAT_TOKEN", "OTEL_RESOURCE_ATTRIBUTES" }, removed);
    }

    [TestMethod]
    public void The_hot_reload_startup_hook_of_dotnet_watch_is_dropped_other_hooks_stay()
    {
        var sep = Path.PathSeparator;
        var agent = "/sdk/DotnetTools/dotnet-watch/Microsoft.Extensions.DotNetDeltaApplier.dll";
        var env = new Dictionary<string, string?> { ["DOTNET_STARTUP_HOOKS"] = $"/my/Hook.dll{sep}{agent}" };
        Assert.IsEmpty(TestHostEnvironment.Sanitize(env, hostedByAspire: true, []));
        Assert.AreEqual("/my/Hook.dll", env["DOTNET_STARTUP_HOOKS"]);

        env["DOTNET_STARTUP_HOOKS"] = agent;
        CollectionAssert.AreEqual(new[] { "DOTNET_STARTUP_HOOKS" }, TestHostEnvironment.Sanitize(env, hostedByAspire: true, []));
        Assert.IsFalse(env.ContainsKey("DOTNET_STARTUP_HOOKS"));

        env["DOTNET_STARTUP_HOOKS"] = "/my/Hook.dll";
        TestHostEnvironment.Sanitize(env, hostedByAspire: true, []);
        Assert.AreEqual("/my/Hook.dll", env["DOTNET_STARTUP_HOOKS"]);

        env["DOTNET_STARTUP_HOOKS"] = agent; // passed through on request
        TestHostEnvironment.Sanitize(env, hostedByAspire: true, ["DOTNET_STARTUP_HOOKS"]);
        Assert.AreEqual(agent, env["DOTNET_STARTUP_HOOKS"]);
    }

    [TestMethod]
    [DoNotParallelize]
    public void The_removed_names_are_logged_once_per_process()
    {
        // Another test may have started a test host (and logged) already: only the "at most once" part is certain.
        var first = TestHostEnvironment.DescribeOnce(["ASPIRE_CLI_PID", "OTEL_SERVICE_NAME"]);
        if (first is not null)
        {
            Assert.Contains("2 variables", first);
            Assert.Contains("ASPIRE_CLI_PID, OTEL_SERVICE_NAME", first);
            Assert.Contains(TestHostEnvironment.PassThroughVariable, first);
        }
        Assert.IsNull(TestHostEnvironment.DescribeOnce(["ASPIRE_CLI_PID"]));
        Assert.IsNull(TestHostEnvironment.DescribeOnce([]));
    }
}
