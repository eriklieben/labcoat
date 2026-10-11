using System.Collections.Concurrent;
using System.Reflection;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Labcoat.Testing;

/// <summary>
/// xUnit v3 adapter of <see cref="LabcoatTracing"/>: gives every test it applies to its own trace, linked from Labcoat
/// to the Aspire dashboard. The package adds it to the test assembly (<c>[assembly: LabcoatTracing]</c>, see
/// <c>build/Labcoat.Testing.XUnit.targets</c>), so there is nothing to write; put it on classes or methods instead with
/// <c>&lt;LabcoatTracingAutoRegister&gt;false&lt;/LabcoatTracingAutoRegister&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="BeforeAfterTestAttribute"/>: xUnit calls <see cref="Before"/> after the test class is created and
/// right before the test method, and <see cref="After"/> right after it (the result is known by then), in the
/// execution context the test method runs in, so the span <see cref="Before"/> starts is
/// <see cref="System.Diagnostics.Activity.Current"/> in the test body and HTTP calls the test makes carry its
/// <c>traceparent</c>. xUnit has no per-test activity of its own, so Labcoat starts one.
/// </para>
/// <para>
/// <c>test.id</c> is the test node uid Labcoat shows: xUnit's Microsoft.Testing.Platform runner uses the test case's
/// unique id (<c>TestCase.UniqueID</c>) as the uid. The trace id goes to the test's output
/// (<see cref="TestContext.TestOutputHelper"/>). A no-op unless the tests run under Labcoat with test tracing on.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LabcoatTracingAttribute : BeforeAfterTestAttribute
{
    /// <summary>The <c>test.framework</c> tag value.</summary>
    public const string Framework = "xunit";

    // The span of each running test (by xUnit's test unique id; tests may run in parallel).
    private static readonly ConcurrentDictionary<string, LabcoatTestSpan> s_spans = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!LabcoatTracing.IsEnabled) return;
        var id = ((ITestMetadata)test).UniqueID;
        // Started again (a retrying extension): end the span of the previous attempt.
        if (s_spans.TryRemove(id, out var stale))
            LabcoatTracing.EndTest(stale, LabcoatTestOutcome.Failed, "Retried");
        // xUnit does not capture Console: the trace id goes to the test's output helper (the result's standard output).
        var output = TestContext.Current.TestOutputHelper;
        var span = LabcoatTracing.BeginTest(ToLabcoatTest(test), line => output?.WriteLine(line));
        if (span is not null) s_spans[id] = span;
    }

    /// <inheritdoc />
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!s_spans.TryRemove(((ITestMetadata)test).UniqueID, out var span)) return;
        var state = TestContext.Current.TestState;
        LabcoatTracing.EndTest(span, ToOutcome(state), state is null ? null : FrameworkOutcome(state));
    }

    /// <summary>Tests: the span of a running test.</summary>
    internal static LabcoatTestSpan? SpanOf(string testUniqueId) => s_spans.TryGetValue(testUniqueId, out var span) ? span : null;

    /// <summary>
    /// The test as Labcoat.Testing describes it; <see cref="LabcoatTest.Id"/> is the test node uid xUnit reports to
    /// Labcoat (the test case's unique id).
    /// </summary>
    /// <remarks>
    /// Reads only the <c>Xunit.Sdk.ITest</c> / <c>ITestCaseMetadata</c> contract of xunit.v3.common, which is the same in
    /// xUnit v3 3.x and 4.x (<c>IXunitTestCase.TestClassName</c> moved to another interface in 4.0).
    /// </remarks>
    public static LabcoatTest ToLabcoatTest(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        ITestCaseMetadata testCase = test.TestCase;
        return new LabcoatTest(test.TestDisplayName)
        {
            Id = testCase.UniqueID,
            ClassName = testCase.TestClassName,
            MethodName = testCase.TestMethodName,
            Framework = Framework,
        };
    }

    /// <summary>Maps xUnit's test result state to the span outcome.</summary>
    public static LabcoatTestOutcome ToOutcome(TestResultState? state) => state?.Result switch
    {
        TestResult.Passed => LabcoatTestOutcome.Passed,
        TestResult.Failed => state.FailureCause switch
        {
            FailureCause.Exception => LabcoatTestOutcome.Error,
            FailureCause.Timeout => LabcoatTestOutcome.TimedOut,
            _ => LabcoatTestOutcome.Failed,
        },
        TestResult.Skipped or TestResult.NotRun => LabcoatTestOutcome.Skipped,
        _ => LabcoatTestOutcome.Unknown,
    };

    /// <summary>The <c>test.outcome</c> tag: xUnit's result, with the failure cause for failures (<c>Failed:Assertion</c>).</summary>
    public static string FrameworkOutcome(TestResultState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Result == TestResult.Failed && state.FailureCause is { } cause ? $"Failed:{cause}" : state.Result.ToString();
    }
}
