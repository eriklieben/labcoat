using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;

namespace Labcoat.Testing.Tests;

/// <summary>
/// Per-test traces, framework neutral (what the adapters call). LabcoatTracing reads process-wide state
/// (LABCOAT_OTLP_* environment variables, Activity.Current, one TracerProvider per process), so these tests don't run
/// in parallel and restore the environment.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class LabcoatTracingTests : IDisposable
{
    private const string FrameworkSource = "Fake.Framework";
    private static readonly string[] EnvVars = [LabcoatTracing.EndpointVariable, LabcoatTracing.ProtocolVariable, LabcoatTracing.HeadersVariable];
    private static readonly LabcoatTest PlacesOrder = new("PlacesOrder (2 items)")
    {
        Id = "Shop.Tests.OrderTests.PlacesOrder(2)",
        ClassName = "Shop.Tests.OrderTests",
        MethodName = "PlacesOrder",
        Framework = "fake",
    };

    private readonly Dictionary<string, string?> _savedEnv = EnvVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
    private readonly List<Activity> _started = [];
    private readonly List<Activity> _stopped = [];
    private readonly List<string> _output = [];
    private readonly ActivityListener _listener;
    private readonly ActivitySource _framework = new(FrameworkSource);

    public LabcoatTracingTests()
    {
        foreach (var v in EnvVars) Environment.SetEnvironmentVariable(v, null);
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is LabcoatTracing.ActivitySourceName or "Fixture" or FrameworkSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => { if (a.Source.Name == LabcoatTracing.ActivitySourceName) lock (_started) _started.Add(a); },
            ActivityStopped = a => { if (a.Source.Name == LabcoatTracing.ActivitySourceName) lock (_stopped) _stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _framework.Dispose();
        foreach (var (k, v) in _savedEnv) Environment.SetEnvironmentVariable(k, v);
        Activity.Current = null;
    }

    /// <summary>Tracing on, exporting to a port nothing listens on (the exporter fails quietly in the background).</summary>
    private static void Enable(string? protocol = "http/protobuf")
    {
        Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "http://127.0.0.1:4549");
        Environment.SetEnvironmentVariable(LabcoatTracing.ProtocolVariable, protocol);
        Environment.SetEnvironmentVariable(LabcoatTracing.HeadersVariable, "x-otlp-api-key=secret");
    }

    private LabcoatTestSpan? Begin(LabcoatTest test, Activity? frameworkActivity = null) =>
        LabcoatTracing.BeginTest(test, _output.Add, frameworkActivity);

    private static string CaptureOut(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }

    // ------------------------------------------------------------------ off

    [TestMethod]
    public void Without_an_endpoint_everything_is_a_no_op()
    {
        Assert.IsFalse(LabcoatTracing.IsEnabled);
        Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "  ");
        Assert.IsFalse(LabcoatTracing.IsEnabled, "blank");

        var output = CaptureOut(() =>
        {
            LabcoatTracing.Initialize(FrameworkSource);
            var span = LabcoatTracing.BeginTest(PlacesOrder);
            Assert.IsNull(span);
            LabcoatTracing.EndTest(span, LabcoatTestOutcome.Passed);
        });

        Assert.AreEqual("", output);
        Assert.IsEmpty(_started);
        Assert.IsNull(Activity.Current);
        var builder = FakeBuilder.Create(out var config);
        Assert.AreSame(builder, LabcoatTracing.ConfigureAspireTesting(builder));
        Assert.IsEmpty(config.AsEnumerable().ToList());
    }

    [TestMethod]
    public void Null_arguments_are_rejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatTracing.BeginTest(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatTracing.Initialize(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatTracing.ConfigureAspireTesting<IDistributedApplicationBuilder>(null!));
    }

    // ------------------------------------------------------------------ spans Labcoat starts (MSTest, NUnit, xUnit)

    [TestMethod]
    public void BeginTest_starts_a_root_span_named_after_the_test_and_writes_its_trace_id()
    {
        Enable();
        Assert.IsTrue(LabcoatTracing.IsEnabled);
        using var fixtureSource = new ActivitySource("Fixture");
        using var fixture = fixtureSource.StartActivity("ClassInitialize");
        Assert.IsNotNull(fixture);

        var span = Begin(PlacesOrder);

        Assert.IsNotNull(span);
        var activity = _started.Single();
        Assert.AreSame(activity, span.Activity);
        Assert.AreSame(PlacesOrder, span.Test);
        Assert.IsTrue(span.IsOwned);
        Assert.AreSame(activity, Activity.Current);
        Assert.AreEqual("PlacesOrder (2 items)", activity.DisplayName);
        Assert.AreEqual(ActivityKind.Internal, activity.Kind);
        Assert.IsNull(activity.Parent, "a new root, not a child of the fixture's span");
        Assert.AreNotEqual(fixture.TraceId, activity.TraceId);
        Assert.AreEqual("Shop.Tests.OrderTests.PlacesOrder(2)", activity.GetTagItem("test.id"));
        Assert.AreEqual("PlacesOrder", activity.GetTagItem("test.name"));
        Assert.AreEqual("PlacesOrder (2 items)", activity.GetTagItem("test.display_name"));
        Assert.AreEqual("Shop.Tests.OrderTests", activity.GetTagItem("test.class"));
        Assert.AreEqual("fake", activity.GetTagItem("test.framework"));
        Assert.AreEqual("Shop.Tests.OrderTests.PlacesOrder", activity.GetTagItem("code.function.name"));
        Assert.AreEqual(LabcoatTracing.TraceIdMarker + span.TraceId, _output.Single());
        Assert.AreEqual(activity.TraceId.ToHexString(), span.TraceId);
        Assert.IsTrue(LabcoatTracing.IsTestTrace(activity.TraceId));

        LabcoatTracing.EndTest(span, LabcoatTestOutcome.Passed);
        Assert.AreSame(activity, _stopped.Single());
        Assert.IsTrue(span.IsEnded);
        Assert.AreEqual(ActivityStatusCode.Ok, activity.Status);
        Assert.AreEqual("Passed", activity.GetTagItem("test.outcome"));
        Assert.IsNull(Activity.Current, "the test span was a root");
    }

    [TestMethod]
    public void The_trace_id_goes_to_the_console_by_default()
    {
        Enable("grpc");
        LabcoatTestSpan? span = null;
        var output = CaptureOut(() => span = LabcoatTracing.BeginTest(new LabcoatTest("Cancels")));
        Assert.IsNotNull(span);
        Assert.IsTrue(TraceIdMarkerLine().IsMatch(output), output);
        LabcoatTracing.EndTest(span, LabcoatTestOutcome.Unknown);

        var activity = _stopped.Single();
        Assert.AreEqual("Cancels", activity.GetTagItem("test.name"), "no method name: the display name");
        Assert.IsNull(activity.GetTagItem("test.id"));
        Assert.IsNull(activity.GetTagItem("code.function.name"));
        Assert.AreEqual(ActivityStatusCode.Unset, activity.Status);
    }

    [TestMethod]
    [DataRow(LabcoatTestOutcome.Failed, null, ActivityStatusCode.Error, "Failed", "Test failed")]
    [DataRow(LabcoatTestOutcome.Error, null, ActivityStatusCode.Error, "Error", "Test error")]
    [DataRow(LabcoatTestOutcome.TimedOut, "Timeout", ActivityStatusCode.Error, "Timeout", "Test timeout")]
    [DataRow(LabcoatTestOutcome.Cancelled, "Aborted", ActivityStatusCode.Error, "Aborted", "Test aborted")]
    [DataRow(LabcoatTestOutcome.Passed, null, ActivityStatusCode.Ok, "Passed", null)]
    [DataRow(LabcoatTestOutcome.Skipped, "Ignored", ActivityStatusCode.Unset, "Ignored", null)]
    [DataRow(LabcoatTestOutcome.Inconclusive, null, ActivityStatusCode.Unset, "Inconclusive", null)]
    [DataRow(LabcoatTestOutcome.Unknown, null, ActivityStatusCode.Unset, "Unknown", null)]
    public void EndTest_tags_the_outcome(LabcoatTestOutcome outcome, string? frameworkOutcome, ActivityStatusCode status, string tag, string? description)
    {
        Enable();
        LabcoatTracing.EndTest(Begin(new LabcoatTest("T")), outcome, frameworkOutcome);

        var activity = _stopped.Single();
        Assert.AreEqual(tag, activity.GetTagItem("test.outcome"));
        Assert.AreEqual(status, activity.Status);
        Assert.AreEqual(description, activity.StatusDescription);
    }

    [TestMethod]
    public void EndTest_without_a_span_or_twice_does_nothing()
    {
        Enable();
        LabcoatTracing.EndTest(null, LabcoatTestOutcome.Passed);
        var span = Begin(new LabcoatTest("T"));
        LabcoatTracing.EndTest(span, LabcoatTestOutcome.Passed);
        LabcoatTracing.EndTest(span, LabcoatTestOutcome.Failed); // already ended
        Assert.HasCount(1, _stopped);
        Assert.AreEqual("Passed", _stopped[0].GetTagItem("test.outcome"));
    }

    // ------------------------------------------------------------------ the framework's own per-test activity (TUnit)

    [TestMethod]
    public void A_framework_root_activity_is_adopted_not_replaced()
    {
        Enable();
        LabcoatTracing.Initialize(FrameworkSource);
        CollectionAssert.Contains(LabcoatTracing.FrameworkSources.ToList(), FrameworkSource);
        using var testCase = _framework.StartActivity("test case");
        Assert.IsNotNull(testCase);
        Assert.IsFalse(LabcoatTracing.IsTestTrace(testCase.TraceId));

        var span = Begin(PlacesOrder, testCase);

        Assert.IsNotNull(span);
        Assert.AreSame(testCase, span.Activity);
        Assert.IsFalse(span.IsOwned);
        Assert.IsEmpty(_started, "no span of its own");
        Assert.AreSame(testCase, Activity.Current, "still the framework's");
        Assert.AreEqual("PlacesOrder (2 items)", testCase.DisplayName);
        Assert.AreEqual("test case", testCase.OperationName);
        Assert.AreEqual("Shop.Tests.OrderTests.PlacesOrder(2)", testCase.GetTagItem("test.id"));
        Assert.AreEqual(LabcoatTracing.TraceIdMarker + testCase.TraceId.ToHexString(), _output.Single());
        Assert.IsTrue(LabcoatTracing.IsTestTrace(testCase.TraceId), "exported from the framework source");

        LabcoatTracing.EndTest(span, LabcoatTestOutcome.Failed);
        Assert.AreEqual(ActivityStatusCode.Error, testCase.Status);
        Assert.IsFalse(testCase.IsStopped, "the framework stops its own activity");
    }

    [TestMethod]
    public void A_framework_activity_that_is_not_a_root_gets_a_span_of_its_own()
    {
        Enable();
        using var suite = _framework.StartActivity("test suite");
        using var testCase = _framework.StartActivity("test case");
        Assert.IsNotNull(testCase);
        Assert.AreSame(suite, testCase.Parent);

        var span = Begin(PlacesOrder, testCase);

        Assert.IsNotNull(span);
        Assert.IsTrue(span.IsOwned);
        Assert.AreNotEqual(suite!.TraceId, span.Activity.TraceId, "a trace per test");
        LabcoatTracing.EndTest(span, LabcoatTestOutcome.Passed);
        Assert.HasCount(1, _stopped);
    }

    [TestMethod]
    public void Initialize_with_a_new_source_rebuilds_the_exporter_for_either_protocol()
    {
        Enable("grpc");
        LabcoatTracing.Initialize("Fake.Grpc." + Guid.NewGuid());
        Enable("http/protobuf");
        Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "http://127.0.0.1:4549/v1/traces");
        Environment.SetEnvironmentVariable(LabcoatTracing.HeadersVariable, null);
        var name = "Fake.Http." + Guid.NewGuid();
        LabcoatTracing.Initialize(name, " ", "");
        LabcoatTracing.Initialize(name); // known: nothing to do
        CollectionAssert.Contains(LabcoatTracing.FrameworkSources.ToList(), name);
        Assert.IsFalse(LabcoatTracing.FrameworkSources.Contains(" "));
    }

    [TestMethod]
    public void The_exporter_gets_test_traces_only()
    {
        Enable();
        var exported = new Recorder();
        using var filter = new LabcoatTracing.TestTraceFilter(exported);
        using var labcoatSource = new ActivitySource(LabcoatTracing.ActivitySourceName);

        using var session = _framework.StartActivity("test session")!;
        filter.OnStart(session);
        session.Stop();
        filter.OnEnd(session);
        Activity.Current = null;
        var span = Begin(new LabcoatTest("T"), _framework.StartActivity("test case"));
        using (var child = _framework.StartActivity("step")!)
        {
            child.Stop();
            filter.OnEnd(child);
        }
        span!.Activity.Stop();
        filter.OnEnd(span.Activity);
        Activity.Current = null;
        var own = labcoatSource.StartActivity("own")!;
        own.Stop();
        filter.OnEnd(own);

        CollectionAssert.AreEqual(new[] { "step", "T", "own" }, exported.Ended.Select(a => a.DisplayName).ToArray());
        Assert.HasCount(1, exported.Started);
    }

    private sealed class Recorder : BaseProcessor<Activity>
    {
        public List<Activity> Started { get; } = [];
        public List<Activity> Ended { get; } = [];
        public override void OnStart(Activity data) => Started.Add(data);
        public override void OnEnd(Activity data) => Ended.Add(data);
    }

    // ------------------------------------------------------------------ Aspire.Hosting.Testing

    [TestMethod]
    public void Aspire_testing_exports_to_the_dashboard_over_grpc_by_default()
    {
        Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "https://localhost:21001");
        var builder = FakeBuilder.Create(out var config);

        Assert.AreSame(builder, LabcoatTracing.ConfigureAspireTesting(builder));

        Assert.AreEqual("https://localhost:21001", config["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.IsNull(config["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"]);
        Assert.IsNull(config["AppHost:OtlpApiKey"], "no headers: no key");
    }

    [TestMethod]
    public void Aspire_testing_over_http_replaces_the_grpc_defaults()
    {
        Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "http://localhost:4318");
        Environment.SetEnvironmentVariable(LabcoatTracing.ProtocolVariable, "HTTP/protobuf");
        var builder = FakeBuilder.Create(out var config);
        config["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:4317"; // the testing builder's default
        config["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:4317";

        LabcoatTracing.ConfigureAspireTesting(builder);

        Assert.AreEqual("http://localhost:4318", config["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"]);
        Assert.IsTrue(string.IsNullOrEmpty(config["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]));
        Assert.IsTrue(string.IsNullOrEmpty(config["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"]));
    }

    [TestMethod]
    [DataRow("x-otlp-api-key=abc", "abc")]
    [DataRow("X-OTLP-API-KEY = a%2Bb%20c ", "a+b c")]
    [DataRow("Authorization=Bearer x, x-otlp-api-key=k2", "k2")]
    [DataRow("Authorization=Bearer x", null)]
    [DataRow("=x-otlp-api-key,x-otlp-api-key", null)]
    [DataRow("", null)]
    public void Dashboard_api_key_comes_from_the_otlp_headers(string headers, string? key)
    {
        Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "https://localhost:21001");
        Environment.SetEnvironmentVariable(LabcoatTracing.HeadersVariable, headers);
        var builder = FakeBuilder.Create(out var config);

        LabcoatTracing.ConfigureAspireTesting(builder);

        Assert.AreEqual(key, config["AppHost:OtlpApiKey"]);
    }

    /// <summary>An IDistributedApplicationBuilder that only has a Configuration (all ConfigureAspireTesting uses).</summary>
    public class FakeBuilder : DispatchProxy
    {
        private ConfigurationManager _config = null!;

        public static IDistributedApplicationBuilder Create(out ConfigurationManager config)
        {
            var proxy = Create<IDistributedApplicationBuilder, FakeBuilder>();
            config = ((FakeBuilder)(object)proxy)._config = new ConfigurationManager();
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_Configuration" ? _config : throw new NotSupportedException(targetMethod?.Name);
    }

    [GeneratedRegex("^" + LabcoatTracing.TraceIdMarker + @"[0-9a-f]{32}\r?\n$")]
    private static partial Regex TraceIdMarkerLine();
}
