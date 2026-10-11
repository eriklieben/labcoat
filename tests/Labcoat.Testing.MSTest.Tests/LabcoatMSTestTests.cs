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
    private static readonly string[] EnvVars = [LabcoatTracing.EndpointVariable, LabcoatTracing.ProtocolVariable, LabcoatTracing.HeadersVariable, LabcoatTracing.FilterVariable];
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

    // ------------------------------------------------------------------ trace filter

    private static FakeTestContext TraitsTest(string method) => new(method, className: typeof(MSTestTraitsTests).FullName!);

    [TestMethod]
    public void The_class_default_filter_leaves_out_tests_that_do_not_match()
    {
        Enable();
        var smoke = TraitsTest(nameof(MSTestTraitsTests.Traits_come_from_the_method_class_and_base_class)); // cat:Smoke
        var other = TraitsTest(nameof(MSTestTraitsTests.A_test_without_attributes_of_its_own_has_the_classes));

        var output = CaptureOut(() => LabcoatMSTest.StartTest(other));
        Assert.AreEqual("", output, "no trace id");
        Assert.IsEmpty(_started, "no span");
        Assert.IsNull(Activity.Current);
        LabcoatMSTest.EndTest(other);
        Assert.IsEmpty(_stopped);

        CaptureOut(() => LabcoatMSTest.StartTest(smoke));
        Assert.HasCount(1, _started);
        LabcoatMSTest.EndTest(smoke);
        Assert.HasCount(1, _stopped);
    }

    [TestMethod]
    public void Labcoats_filter_wins_over_the_class_default()
    {
        Enable();
        Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, "trait:Team=web cat:Base");
        var other = TraitsTest(nameof(MSTestTraitsTests.A_test_without_attributes_of_its_own_has_the_classes));
        CaptureOut(() => LabcoatMSTest.StartTest(other));
        Assert.HasCount(1, _started, "matches Labcoat's filter, not the class's cat:Smoke");
        LabcoatMSTest.EndTest(other);

        Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, "cat:Unit");
        var plain = new FakeTestContext("PlacesOrder");
        CaptureOut(() => LabcoatMSTest.StartTest(plain));
        Assert.HasCount(1, _started, "a test of an unknown class has no categories");
    }

    [TestMethod]
    public void A_trimmed_test_class_or_method_degrades_to_fewer_categories()
    {
        // In a trimmed or Native AOT test app the class (or the method) the attributes are read from may be gone.
        Enable();
        Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, "cat:Integration");
        var gone = new FakeTestContext("PlacesOrder", className: "Trimmed.Away.OrderTests");
        Assert.IsEmpty(LabcoatMSTest.ToLabcoatTest(gone).Categories);
        Assert.IsEmpty(LabcoatMSTest.ToLabcoatTest(gone).Traits);
        CaptureOut(() => LabcoatMSTest.StartTest(gone));
        Assert.IsEmpty(_started, "no categories: the category filter does not match, and nothing throws");
        LabcoatMSTest.EndTest(gone);

        var methodGone = LabcoatMSTest.ToLabcoatTest(TraitsTest("TrimmedAway"));
        CollectionAssert.AreEquivalent(new[] { "Integration", "Base" }, methodGone.Categories.ToArray(), "only the class's");
    }

    [GeneratedRegex("^" + LabcoatTracing.TraceIdMarker + @"([0-9a-f]{32})\r?\n$")]
    private static partial Regex TraceIdMarkerLine();
}

/// <summary>Base class of <see cref="MSTestTraitsTests"/>: its category is inherited, as MSTest reports it.</summary>
[TestCategory("Base")]
public abstract class TraitsTestsBase;

/// <summary>
/// The categories and traits LabcoatMSTest reads from the attributes (MSTest's TestContext does not expose them): the
/// ones MSTest reports to Labcoat, so a trace filter means what it means in the Labcoat UI. The class has a default
/// trace filter of its own (<see cref="LabcoatTracingAttribute"/>).
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestProperty("Team", "web")]
[LabcoatTracing(Filter = "cat:Smoke")]
public sealed class MSTestTraitsTests : TraitsTestsBase
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Smoke")]
    [TestProperty("Kind", "Slow")]
    [Owner("web-team")]
    [Priority(2)]
    [Description("Places an order")]
    public void Traits_come_from_the_method_class_and_base_class()
    {
        var test = LabcoatMSTest.ToLabcoatTest(TestContext);

        CollectionAssert.AreEquivalent(new[] { "Smoke", "Integration", "Base" }, test.Categories.ToArray());
        CollectionAssert.AreEquivalent(
            new KeyValuePair<string, string>[] { new("Team", "web"), new("Kind", "Slow"), new("Owner", "web-team"), new("Priority", "2"), new("Description", "Places an order") },
            test.Traits.ToArray());
        Assert.AreEqual(test, LabcoatMSTest.ToLabcoatTest(TestContext), "read once, equal every time");
    }

    [TestMethod]
    public void A_test_without_attributes_of_its_own_has_the_classes()
    {
        var test = LabcoatMSTest.ToLabcoatTest(TestContext);
        CollectionAssert.AreEquivalent(new[] { "Integration", "Base" }, test.Categories.ToArray());
        CollectionAssert.AreEqual(new KeyValuePair<string, string>[] { new("Team", "web") }, test.Traits.ToArray());
    }

    [TestMethod]
    public void Classes_that_cannot_be_found_have_no_traits()
    {
        Assert.IsNull(LabcoatMSTest.FindType("No.Such.Class"));
        Assert.IsNull(LabcoatMSTest.FindType("Bad[Name,"));
        Assert.AreSame(typeof(MSTestTraitsTests), LabcoatMSTest.FindType(typeof(MSTestTraitsTests).FullName!));
        var test = LabcoatMSTest.ToLabcoatTest(new FakeTestContext("PlacesOrder"));
        Assert.IsEmpty(test.Categories);
        Assert.IsEmpty(test.Traits);
        Assert.AreSame(LabcoatMSTest.TestAttributes.None, LabcoatMSTest.TestAttributes.Read(null, "x"));
        Assert.IsEmpty(LabcoatMSTest.ToLabcoatTest(new FakeTestContext("", className: "")).Categories);
    }
}
