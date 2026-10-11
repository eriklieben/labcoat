using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Aspire.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Labcoat.Testing;

/// <summary>
/// Per-test OpenTelemetry traces for integration tests, linked from Labcoat to the Aspire dashboard. Framework neutral:
/// the test framework adapters (Labcoat.Testing.MSTest, Labcoat.Testing.TUnit) call <see cref="BeginTest"/> /
/// <see cref="EndTest"/> from the framework's test hooks.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is opt-in and a no-op unless the tests run under Labcoat hosted by an Aspire AppHost
/// (<c>builder.AddLabcoat(...)</c>) with trace collection turned on (off by default: <c>WithTestTracing()</c>, the
/// "Toggle test tracing" dashboard command or the Labcoat UI toggle): Labcoat then passes the AppHost dashboard's OTLP
/// endpoint to the test host as <c>LABCOAT_OTLP_ENDPOINT</c> / <c>LABCOAT_OTLP_PROTOCOL</c> / <c>LABCOAT_OTLP_HEADERS</c>.
/// </para>
/// <para>
/// When enabled, <see cref="BeginTest"/> gives the test a span of its own trace (exported to the dashboard) and writes
/// <c>labcoat:traceId=&lt;hex&gt;</c> to the test's output, which Labcoat turns into a "View trace" link. HTTP calls
/// the test makes carry the W3C <c>traceparent</c> of that span, so the spans of the services the test calls end up in
/// the same trace. <see cref="ConfigureAspireTesting"/> makes the services started by
/// <c>DistributedApplicationTestingBuilder</c> export their telemetry to that same dashboard.
/// </para>
/// </remarks>
public static class LabcoatTracing
{
    /// <summary>The <see cref="ActivitySource"/> name of the per-test spans Labcoat starts.</summary>
    public const string ActivitySourceName = "Labcoat.Testing";

    /// <summary>Prefix of the test output line that carries the test's trace id (stripped from the output by Labcoat).</summary>
    public const string TraceIdMarker = "labcoat:traceId=";

    /// <summary>Environment variable with the OTLP endpoint (set by Labcoat when hosted by an Aspire AppHost).</summary>
    public const string EndpointVariable = "LABCOAT_OTLP_ENDPOINT";

    /// <summary>Environment variable with the OTLP protocol: <c>grpc</c> (default) or <c>http/protobuf</c>.</summary>
    public const string ProtocolVariable = "LABCOAT_OTLP_PROTOCOL";

    /// <summary>Environment variable with OTLP headers (<c>key=value,key2=value2</c>), e.g. the dashboard's <c>x-otlp-api-key</c>.</summary>
    public const string HeadersVariable = "LABCOAT_OTLP_HEADERS";

    private const string ApiKeyHeader = "x-otlp-api-key";

    private static readonly ActivitySource s_source = new(ActivitySourceName, typeof(LabcoatTracing).Assembly.GetName().Version?.ToString());
    private static readonly object s_gate = new(); // not System.Threading.Lock: this library targets net8.0
    private static readonly HashSet<string> s_frameworkSources = new(StringComparer.Ordinal);
    // Traces of tests (BeginTest): the only traces of the framework sources that are exported.
    private static readonly ConcurrentDictionary<ActivityTraceId, byte> s_testTraces = new();
    private static TracerProvider? s_provider;
    private static bool s_exitHook;

    /// <summary>True when Labcoat passed an OTLP endpoint to this test process (i.e. tracing is on).</summary>
    public static bool IsEnabled => !string.IsNullOrWhiteSpace(Endpoint);

    private static string? Endpoint => Environment.GetEnvironmentVariable(EndpointVariable);

    private static bool IsHttp =>
        Environment.GetEnvironmentVariable(ProtocolVariable) is { } p && p.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the resources of an Aspire app started by <c>DistributedApplicationTestingBuilder</c> export their
    /// telemetry (traces, logs, metrics) to the dashboard of the AppHost that hosts Labcoat, instead of the testing
    /// default (<c>http://localhost:4317</c>, usually nothing). Call it after <c>CreateAsync</c> and before
    /// <c>BuildAsync</c>. A no-op when <see cref="IsEnabled"/> is false.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder ConfigureAspireTesting<TBuilder>(TBuilder builder) where TBuilder : IDistributedApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!IsEnabled) return builder;

        // Read lazily by Aspire's OTLP environment callback when each resource starts (see OtlpConfigurationExtensions):
        // with the dashboard disabled (the testing default) it falls back to these configuration keys.
        var config = builder.Configuration;
        if (IsHttp)
        {
            config["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = Endpoint;
            config["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = null; // the testing default points gRPC at localhost:4317
            config["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"] = null;
        }
        else
        {
            config["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = Endpoint;
        }
        if (ApiKey() is { } key) config["AppHost:OtlpApiKey"] = key;
        return builder;
    }

    /// <summary>
    /// For framework adapters whose framework starts an <see cref="Activity"/> per test itself (TUnit): starts the
    /// exporter now, also listening to <paramref name="frameworkActivitySources"/>, so the framework's per-test
    /// activities are created (sampled) and exported. Only the traces of tests passed to <see cref="BeginTest"/> are
    /// exported from those sources (not the framework's session / class spans). Call it before the first test starts;
    /// a no-op when <see cref="IsEnabled"/> is false.
    /// </summary>
    public static void Initialize(params string[] frameworkActivitySources)
    {
        ArgumentNullException.ThrowIfNull(frameworkActivitySources);
        if (!IsEnabled) return;
        lock (s_gate)
        {
            var added = false;
            foreach (var name in frameworkActivitySources.Where(n => !string.IsNullOrWhiteSpace(n)))
                added |= s_frameworkSources.Add(name);
            if (added && s_provider is not null)
            {
                // A source after the exporter was built: rebuild it (flushes what the old one had).
                s_provider.Dispose();
                s_provider = null;
            }
            EnsureProviderLocked();
        }
    }

    /// <summary>
    /// Starts the span of a test and writes its trace id to the test's output. Call from the framework's "test starts"
    /// hook, in the test's execution context (the activity becomes <see cref="Activity.Current"/>), and pair with
    /// <see cref="EndTest"/>. A no-op returning null when <see cref="IsEnabled"/> is false.
    /// </summary>
    /// <param name="test">The test.</param>
    /// <param name="writeOutput">Writes a line to the test's own output (captured per test; default <see cref="Console.WriteLine(string)"/>).</param>
    /// <param name="frameworkActivity">
    /// The framework's own per-test activity (TUnit's <c>TestContext.Activity</c>), when the framework makes that
    /// <see cref="Activity.Current"/> in the test body: Labcoat then adopts it (tags it, uses its trace, never stops it)
    /// instead of starting a span the test body would not see. It must be a root (a trace per test).
    /// </param>
    /// <returns>The span, for <see cref="EndTest"/>; null when tracing is off.</returns>
    public static LabcoatTestSpan? BeginTest(LabcoatTest test, Action<string>? writeOutput = null, Activity? frameworkActivity = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!IsEnabled) return null;
        lock (s_gate) EnsureProviderLocked();

        Activity? activity;
        var owned = frameworkActivity is null || frameworkActivity.Parent is not null || frameworkActivity.ParentSpanId != default;
        if (owned)
        {
            // Always a new root: the test's trace should not hang off whatever was current (e.g. a fixture span).
            Activity.Current = null;
            activity = s_source.StartActivity(test.DisplayName, ActivityKind.Internal);
            if (activity is null) return null;
        }
        else
        {
            activity = frameworkActivity!;
            activity.DisplayName = test.DisplayName;
        }

        activity.SetTag("test.id", test.Id);
        activity.SetTag("test.name", test.MethodName ?? test.DisplayName);
        activity.SetTag("test.display_name", test.DisplayName);
        activity.SetTag("test.class", test.ClassName);
        activity.SetTag("test.framework", test.Framework);
        if (test.ClassName is not null && test.MethodName is not null)
            activity.SetTag("code.function.name", $"{test.ClassName}.{test.MethodName}");
        s_testTraces.TryAdd(activity.TraceId, 0);

        (writeOutput ?? Console.WriteLine)(TraceIdMarker + activity.TraceId.ToHexString());
        return new LabcoatTestSpan(test, activity, owned);
    }

    /// <summary>Ends the span started by <see cref="BeginTest"/>, tagging it with the test outcome. Null or already ended: a no-op.</summary>
    /// <param name="span">What <see cref="BeginTest"/> returned.</param>
    /// <param name="outcome">The outcome: Failed / Error / TimedOut / Cancelled set the span status to Error, Passed to Ok.</param>
    /// <param name="frameworkOutcome">The framework's own name of the outcome for the <c>test.outcome</c> tag (default: <paramref name="outcome"/>).</param>
    public static void EndTest(LabcoatTestSpan? span, LabcoatTestOutcome outcome, string? frameworkOutcome = null)
    {
        if (span is null || span.IsEnded) return;
        span.IsEnded = true;

        var activity = span.Activity;
        var name = frameworkOutcome ?? outcome.ToString();
        activity.SetTag("test.outcome", name);
        if (outcome is LabcoatTestOutcome.Failed or LabcoatTestOutcome.Error or LabcoatTestOutcome.TimedOut or LabcoatTestOutcome.Cancelled)
            activity.SetStatus(ActivityStatusCode.Error, $"Test {name.ToLowerInvariant()}");
        else if (outcome == LabcoatTestOutcome.Passed)
            activity.SetStatus(ActivityStatusCode.Ok);
        if (span.IsOwned) activity.Stop(); // also restores Activity.Current
    }

    /// <summary>Tests: whether spans of this trace are exported from framework sources.</summary>
    internal static bool IsTestTrace(ActivityTraceId traceId) => s_testTraces.ContainsKey(traceId);

    /// <summary>Tests: the framework sources the exporter listens to.</summary>
    internal static IReadOnlyCollection<string> FrameworkSources
    {
        get { lock (s_gate) return [.. s_frameworkSources]; }
    }

    private static void EnsureProviderLocked()
    {
        if (s_provider is not null) return;
        var serviceName = Assembly.GetEntryAssembly()?.GetName().Name ?? "tests";
        var http = IsHttp;
        var endpoint = Endpoint!.Trim().TrimEnd('/');
        var options = new OtlpExporterOptions
        {
            Protocol = http ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc,
            // Set in code, the HTTP endpoint must be the full signal path.
            Endpoint = new Uri(http && !endpoint.EndsWith("/v1/traces", StringComparison.Ordinal) ? endpoint + "/v1/traces" : endpoint),
        };
        if (Environment.GetEnvironmentVariable(HeadersVariable) is { Length: > 0 } headers) options.Headers = headers;
        // Test runs are short; don't sit on spans for the default 5s.
        var export = new BatchActivityExportProcessor(new OtlpTraceExporter(options), scheduledDelayMilliseconds: 500);

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName, serviceInstanceId: Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .AddSource(ActivitySourceName);
        foreach (var source in s_frameworkSources) builder.AddSource(source);
        s_provider = builder.AddProcessor(new TestTraceFilter(export)).Build();
        if (!s_exitHook)
        {
            s_exitHook = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => s_provider?.Dispose(); // flush on exit
        }
    }

    private static string? ApiKey()
    {
        var headers = Environment.GetEnvironmentVariable(HeadersVariable);
        if (string.IsNullOrEmpty(headers)) return null;
        foreach (var part in headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(ApiKeyHeader, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(eq + 1)..].Trim());
        }
        return null;
    }

    /// <summary>
    /// Exports Labcoat's own spans, and spans of the framework sources only when they belong to a test's trace
    /// (a framework like TUnit also traces the session, assemblies and classes: not interesting in the dashboard).
    /// A <see cref="CompositeProcessor{T}"/> so the export processor gets the provider (its resource) as usual.
    /// </summary>
    internal sealed class TestTraceFilter(BaseProcessor<Activity> export) : CompositeProcessor<Activity>([export])
    {
        public override void OnEnd(Activity data)
        {
            if (data.Source.Name == ActivitySourceName || s_testTraces.ContainsKey(data.TraceId)) base.OnEnd(data);
        }
    }
}
