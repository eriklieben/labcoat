using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Labcoat.Testing.MSTest.Tests;

/// <summary>
/// The MSTest adapter: StartTest / EndTest from MSTest's hooks, the LabcoatTracedTest base class and the outcome
/// mapping. Process-wide state (LABCOAT_OTLP_*, Activity.Current): not in parallel, the environment is restored.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class LabcoatMSTestTests : IDisposable
{
    private static readonly string[] EnvVars = [LabcoatTracing.EndpointVariable, LabcoatTracing.ProtocolVariable, LabcoatTracing.HeadersVariable];
    private readonly Dictionary<string, string?> _savedEnv = EnvVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
    private readonly List<Activity> _started = [];
    private readonly List<Activity> _stopped = [];
    private readonly ActivityListener _listener;

    public LabcoatMSTestTests()
    {
        foreach (var v in EnvVars) Environment.SetEnvironmentVariable(v, null);
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LabcoatTracing.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => { lock (_started) _started.Add(a); },
            ActivityStopped = a => { lock (_stopped) _stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var (k, v) in _savedEnv) Environment.SetEnvironmentVariable(k, v);
        Activity.Current = null;
    }

    private static void Enable() => Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "http://127.0.0.1:4549");

    private static string CaptureOut(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }

    [TestMethod]
    public void Off_StartTest_and_EndTest_do_nothing()
    {
        var context = new FakeTestContext("PlacesOrder");
        var output = CaptureOut(() =>
        {
            LabcoatMSTest.StartTest(context);
            LabcoatMSTest.EndTest(context);
        });
        Assert.AreEqual("", output);
        Assert.IsEmpty(_started);
    }

    [TestMethod]
    public void Null_arguments_are_rejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatMSTest.StartTest(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatMSTest.EndTest(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatMSTest.ToLabcoatTest(null!));
    }

    [TestMethod]
    public void StartTest_starts_a_span_named_after_the_test_and_prints_its_trace_id()
    {
        Enable();
        var context = new FakeTestContext("PlacesOrder", "PlacesOrder (2 items)");
        var output = CaptureOut(() => LabcoatMSTest.StartTest(context));

        var activity = _started.Single();
        Assert.AreSame(activity, Activity.Current);
        Assert.AreEqual("PlacesOrder (2 items)", activity.DisplayName);
        Assert.AreEqual("PlacesOrder", activity.GetTagItem("test.name"));
        Assert.AreEqual("Shop.Tests.OrderTests", activity.GetTagItem("test.class"));
        Assert.AreEqual("mstest", activity.GetTagItem("test.framework"));
        Assert.AreEqual("Shop.Tests.OrderTests.PlacesOrder", activity.GetTagItem("code.function.name"));
        Assert.IsNull(activity.GetTagItem("test.id"), "MSTest does not expose the test node uid");
        var marker = TraceIdMarkerLine().Match(output);
        Assert.IsTrue(marker.Success, output);
        Assert.AreEqual(activity.TraceId.ToHexString(), marker.Groups[1].Value);

        CaptureOut(() => LabcoatMSTest.StartTest(context)); // a second hook for the same test: no second span
        Assert.HasCount(1, _started);

        LabcoatMSTest.EndTest(context);
        Assert.AreSame(activity, _stopped.Single());
        Assert.AreEqual(ActivityStatusCode.Ok, activity.Status);
        Assert.AreEqual("Passed", activity.GetTagItem("test.outcome"));
        Assert.IsNull(Activity.Current);
        LabcoatMSTest.EndTest(context); // already ended
        Assert.HasCount(1, _stopped);
    }

    [TestMethod]
    public void The_span_falls_back_to_the_test_name()
    {
        Enable();
        var context = new FakeTestContext("Cancels", displayName: null);
        CaptureOut(() => LabcoatMSTest.StartTest(context));
        LabcoatMSTest.EndTest(context);
        Assert.AreEqual("Cancels", _stopped.Single().DisplayName);
    }

    [TestMethod]
    [DataRow(UnitTestOutcome.Failed, ActivityStatusCode.Error, "Test failed")]
    [DataRow(UnitTestOutcome.Error, ActivityStatusCode.Error, "Test error")]
    [DataRow(UnitTestOutcome.Timeout, ActivityStatusCode.Error, "Test timeout")]
    [DataRow(UnitTestOutcome.Aborted, ActivityStatusCode.Error, "Test aborted")]
    [DataRow(UnitTestOutcome.Passed, ActivityStatusCode.Ok, null)]
    [DataRow(UnitTestOutcome.Inconclusive, ActivityStatusCode.Unset, null)]
    [DataRow(UnitTestOutcome.NotRunnable, ActivityStatusCode.Unset, null)]
    [DataRow(UnitTestOutcome.Unknown, ActivityStatusCode.Unset, null)]
    public void EndTest_tags_the_outcome(UnitTestOutcome outcome, ActivityStatusCode status, string? description)
    {
        Enable();
        var context = new FakeTestContext("T", outcome: outcome);
        CaptureOut(() => LabcoatMSTest.StartTest(context));
        LabcoatMSTest.EndTest(context);

        var activity = _stopped.Single();
        Assert.AreEqual(outcome.ToString(), activity.GetTagItem("test.outcome"));
        Assert.AreEqual(status, activity.Status);
        Assert.AreEqual(description, activity.StatusDescription);
    }

    [TestMethod]
    [DataRow(UnitTestOutcome.Passed, LabcoatTestOutcome.Passed)]
    [DataRow(UnitTestOutcome.Failed, LabcoatTestOutcome.Failed)]
    [DataRow(UnitTestOutcome.Error, LabcoatTestOutcome.Error)]
    [DataRow(UnitTestOutcome.Timeout, LabcoatTestOutcome.TimedOut)]
    [DataRow(UnitTestOutcome.Aborted, LabcoatTestOutcome.Cancelled)]
    [DataRow(UnitTestOutcome.Inconclusive, LabcoatTestOutcome.Inconclusive)]
    [DataRow(UnitTestOutcome.NotRunnable, LabcoatTestOutcome.Skipped)]
    [DataRow(UnitTestOutcome.Ignored, LabcoatTestOutcome.Skipped)]
    [DataRow(UnitTestOutcome.InProgress, LabcoatTestOutcome.Unknown)]
    [DataRow(UnitTestOutcome.Unknown, LabcoatTestOutcome.Unknown)]
    public void Outcomes_map_to_the_span_outcome(UnitTestOutcome outcome, LabcoatTestOutcome expected) =>
        Assert.AreEqual(expected, LabcoatMSTest.ToOutcome(outcome));

    [TestMethod]
    public void Traced_test_base_class_starts_and_ends_the_span()
    {
        Enable();
        var test = new TracedTest { TestContext = new FakeTestContext("Ships") };
        CaptureOut(test.LabcoatStartTrace);
        Assert.HasCount(1, _started);
        test.LabcoatEndTrace();
        Assert.AreEqual("Ships", _stopped.Single().DisplayName);
        Assert.IsNotNull(typeof(LabcoatTracedTest).GetMethod(nameof(LabcoatTracedTest.LabcoatStartTrace))!.GetCustomAttribute<TestInitializeAttribute>());
        Assert.IsNotNull(typeof(LabcoatTracedTest).GetMethod(nameof(LabcoatTracedTest.LabcoatEndTrace))!.GetCustomAttribute<TestCleanupAttribute>());
    }

    private sealed class TracedTest : LabcoatTracedTest;

    [GeneratedRegex("^" + LabcoatTracing.TraceIdMarker + @"([0-9a-f]{32})\r?\n$")]
    private static partial Regex TraceIdMarkerLine();
}
