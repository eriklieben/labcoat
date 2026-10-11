using System.Diagnostics;

namespace Labcoat.Testing;

/// <summary>
/// A test as a framework adapter (Labcoat.Testing.MSTest, Labcoat.Testing.TUnit, ...) describes it to
/// <see cref="LabcoatTracing.BeginTest"/> and <see cref="LabcoatLiveOutput"/>.
/// </summary>
/// <param name="DisplayName">The name Labcoat shows for the test (the span is named after it).</param>
public sealed record LabcoatTest(string DisplayName)
{
    /// <summary>
    /// The Microsoft.Testing.Platform test node uid of the test: the id Labcoat uses for the test's row. Null when the
    /// framework does not expose it to the test (MSTest). Recorded as the <c>test.id</c> span tag.
    /// </summary>
    /// <remarks>
    /// Linking a test to its trace does not depend on it: the trace id goes into the test's own output, which arrives
    /// in Labcoat with that test's result.
    /// </remarks>
    public string? Id { get; init; }

    /// <summary>The fully qualified name of the test class.</summary>
    public string? ClassName { get; init; }

    /// <summary>The name of the test method.</summary>
    public string? MethodName { get; init; }

    /// <summary>The test framework, lower case (<c>mstest</c>, <c>tunit</c>, ...): the <c>test.framework</c> span tag.</summary>
    public string? Framework { get; init; }
}

/// <summary>The outcome of a test, as far as its span is concerned (<see cref="LabcoatTracing.EndTest"/>).</summary>
public enum LabcoatTestOutcome
{
    /// <summary>Not known (the span status stays unset).</summary>
    Unknown,
    /// <summary>Passed: span status Ok.</summary>
    Passed,
    /// <summary>An assertion failed: span status Error.</summary>
    Failed,
    /// <summary>The test (or its setup) threw: span status Error.</summary>
    Error,
    /// <summary>Timed out: span status Error.</summary>
    TimedOut,
    /// <summary>Cancelled or aborted: span status Error.</summary>
    Cancelled,
    /// <summary>Skipped / ignored / not runnable (the span status stays unset).</summary>
    Skipped,
    /// <summary>Inconclusive (the span status stays unset).</summary>
    Inconclusive,
}

/// <summary>The span of one test: returned by <see cref="LabcoatTracing.BeginTest"/>, passed to <see cref="LabcoatTracing.EndTest"/>.</summary>
public sealed class LabcoatTestSpan
{
    internal LabcoatTestSpan(LabcoatTest test, Activity activity, bool owned)
    {
        Test = test;
        Activity = activity;
        IsOwned = owned;
    }

    /// <summary>The test.</summary>
    public LabcoatTest Test { get; }

    /// <summary>The test's span: one Labcoat started, or the framework's own per-test activity it adopted.</summary>
    public Activity Activity { get; }

    /// <summary>The W3C trace id (32 lower-case hex characters) that Labcoat links the test to.</summary>
    public string TraceId => Activity.TraceId.ToHexString();

    /// <summary>True when Labcoat started the activity (and stops it in <see cref="LabcoatTracing.EndTest"/>).</summary>
    public bool IsOwned { get; }

    /// <summary>True once <see cref="LabcoatTracing.EndTest"/> ran for it.</summary>
    public bool IsEnded { get; internal set; }
}
