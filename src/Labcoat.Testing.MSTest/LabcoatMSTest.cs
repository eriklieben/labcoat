using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Labcoat.Testing;

/// <summary>
/// MSTest adapter of <see cref="LabcoatTracing"/>: per-test traces from MSTest's test hooks.
/// </summary>
/// <remarks>
/// Call <see cref="StartTest"/> / <see cref="EndTest"/> from <c>[GlobalTestInitialize]</c> / <c>[GlobalTestCleanup]</c>
/// to cover the whole assembly, or derive test classes from <see cref="LabcoatTracedTest"/>:
/// <code>
/// [TestClass]
/// public static class LabcoatHooks
/// {
///     [GlobalTestInitialize] public static void Start(TestContext c) => LabcoatMSTest.StartTest(c);
///     [GlobalTestCleanup]    public static void End(TestContext c)   => LabcoatMSTest.EndTest(c);
/// }
/// </code>
/// No-ops unless <see cref="LabcoatTracing.IsEnabled"/> (tests run under Labcoat with test tracing on).
/// </remarks>
public static class LabcoatMSTest
{
    /// <summary>The <c>test.framework</c> tag value.</summary>
    public const string Framework = "mstest";

    // The span of the test running in this execution context: MSTest runs [GlobalTestInitialize], the test and
    // [GlobalTestCleanup] in one flowing ExecutionContext.
    private static readonly AsyncLocal<LabcoatTestSpan?> s_current = new();

    /// <summary>
    /// Starts the span for the current test and records its trace id in the test output (the activity flows into the
    /// test method). Call from <c>[TestInitialize]</c> or <c>[GlobalTestInitialize]</c>, paired with <see cref="EndTest"/>.
    /// Calling it again for a test that already has a span (e.g. both hooks and <see cref="LabcoatTracedTest"/>) does nothing.
    /// </summary>
    public static void StartTest(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!LabcoatTracing.IsEnabled) return;
        var test = ToLabcoatTest(context);
        if (s_current.Value is { IsEnded: false } running && running.Test == test) return;
        s_current.Value = LabcoatTracing.BeginTest(test, Console.WriteLine);
    }

    /// <summary>Ends the span started by <see cref="StartTest"/>, tagging it with the test outcome.</summary>
    public static void EndTest(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var span = s_current.Value;
        if (span is null) return;
        s_current.Value = null;
        var outcome = context.CurrentTestOutcome;
        LabcoatTracing.EndTest(span, ToOutcome(outcome), outcome.ToString());
    }

    /// <summary>The test as Labcoat.Testing describes it (MSTest does not expose the test node uid, so no <see cref="LabcoatTest.Id"/>).</summary>
    public static LabcoatTest ToLabcoatTest(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new LabcoatTest(context.TestDisplayName ?? context.TestName ?? "test")
        {
            ClassName = context.FullyQualifiedTestClassName,
            MethodName = context.TestName,
            Framework = Framework,
        };
    }

    /// <summary>Maps MSTest's outcome to the span outcome.</summary>
    public static LabcoatTestOutcome ToOutcome(UnitTestOutcome outcome) => outcome switch
    {
        UnitTestOutcome.Passed => LabcoatTestOutcome.Passed,
        UnitTestOutcome.Failed => LabcoatTestOutcome.Failed,
        UnitTestOutcome.Error => LabcoatTestOutcome.Error,
        UnitTestOutcome.Timeout => LabcoatTestOutcome.TimedOut,
        UnitTestOutcome.Aborted => LabcoatTestOutcome.Cancelled,
        UnitTestOutcome.Inconclusive => LabcoatTestOutcome.Inconclusive,
        UnitTestOutcome.NotRunnable or UnitTestOutcome.Ignored => LabcoatTestOutcome.Skipped,
        _ => LabcoatTestOutcome.Unknown,
    };
}

/// <summary>
/// Optional base class: gives every test in the derived class its own trace (see <see cref="LabcoatMSTest"/>).
/// Alternatively call <see cref="LabcoatMSTest.StartTest"/> / <see cref="LabcoatMSTest.EndTest"/> from
/// <c>[GlobalTestInitialize]</c> / <c>[GlobalTestCleanup]</c> to cover the whole assembly.
/// </summary>
public abstract class LabcoatTracedTest
{
    /// <summary>The MSTest context of the running test.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Starts the test's span.</summary>
    [TestInitialize]
    public void LabcoatStartTrace() => LabcoatMSTest.StartTest(TestContext);

    /// <summary>Ends the test's span.</summary>
    [TestCleanup]
    public void LabcoatEndTrace() => LabcoatMSTest.EndTest(TestContext);
}
