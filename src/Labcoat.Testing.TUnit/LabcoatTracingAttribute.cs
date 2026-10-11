using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Labcoat.Testing;

/// <summary>
/// TUnit adapter of <see cref="LabcoatTracing"/>: gives every test it applies to its own trace, linked from Labcoat to
/// the Aspire dashboard. The package adds it to the test assembly (<c>[assembly: LabcoatTracing]</c>, see
/// <c>build/Labcoat.Testing.TUnit.targets</c>), so there is nothing to write; put it on classes or methods instead with
/// <c>&lt;LabcoatTracingAutoRegister&gt;false&lt;/LabcoatTracingAutoRegister&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// TUnit starts an <see cref="System.Diagnostics.Activity"/> per test itself (<see cref="TestContext.Activity"/>, source
/// <c>TUnit</c>, a new trace per test) and makes it <see cref="System.Diagnostics.Activity.Current"/> in the test body,
/// so HTTP calls the test makes carry its <c>traceparent</c>. This adapter adopts that activity: it names it after the
/// test, tags it (<c>test.id</c> = the test node uid Labcoat shows, = <c>TestDetails.TestId</c>), writes its trace id to
/// the test's output and exports it with the service spans of that trace. A no-op unless the tests run under Labcoat
/// with test tracing on.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LabcoatTracingAttribute : Attribute, ITestStartEventReceiver, ITestEndEventReceiver
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> of TUnit's own spans.</summary>
    public const string TUnitActivitySourceName = "TUnit";

    /// <summary>The <c>test.framework</c> tag value.</summary>
    public const string Framework = "tunit";

    /// <summary>Where the span is kept between start and end (<see cref="TestContext.StateBag"/>).</summary>
    internal const string StateKey = "Labcoat.Testing.Span";

    /// <summary>
    /// Creates the receiver. TUnit creates it when it discovers the tests, before any test starts: the exporter is
    /// started then, listening to TUnit's source, so TUnit creates (samples) the per-test activities.
    /// </summary>
    public LabcoatTracingAttribute() => LabcoatTracing.Initialize(TUnitActivitySourceName);

    /// <inheritdoc />
    public int Order => 0;

    /// <summary>
    /// The default trace filter of the tests this attribute applies to, in the Labcoat UI's search syntax
    /// (<c>cat:Integration</c>, <c>trait:Kind=Slow</c>, name text): only matching tests are traced. Used when Labcoat
    /// passes no filter of its own (the AppHost's wins). Set it from the project file with
    /// <c>&lt;LabcoatTracingFilter&gt;cat:Integration&lt;/LabcoatTracingFilter&gt;</c>. Null: every test.
    /// </summary>
    public string? Filter { get; set; }

    /// <inheritdoc />
    public ValueTask OnTestStart(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!LabcoatTracing.IsEnabled) return default;
        var items = context.StateBag.Items;
        // A retry starts the test again: end the span of the previous attempt.
        if (items.TryRemove(StateKey, out var previous) && previous is LabcoatTestSpan { IsEnded: false } stale)
            LabcoatTracing.EndTest(stale, LabcoatTestOutcome.Failed, "Retried");
        var test = ToLabcoatTest(context);
        // Left out by the trace filter: TUnit's own activity stays as TUnit made it (not adopted, not exported).
        if (!LabcoatTracing.ShouldTrace(test, Filter)) return default;
        var span = LabcoatTracing.BeginTest(test, context.Output.WriteLine, context.Activity);
        if (span is not null) items[StateKey] = span;
        return default;
    }

    /// <inheritdoc />
    public ValueTask OnTestEnd(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Skipped tests end without having started.
        if (!context.StateBag.Items.TryRemove(StateKey, out var value) || value is not LabcoatTestSpan span) return default;
        var state = context.Execution.Result?.State;
        LabcoatTracing.EndTest(span, ToOutcome(state), state?.ToString());
        return default;
    }

    /// <summary>
    /// The test as Labcoat.Testing describes it; <see cref="LabcoatTest.Id"/> is the test node uid TUnit reports to
    /// Labcoat. <see cref="LabcoatTest.Categories"/> are TUnit's categories (<c>[Category]</c> on the test, class and
    /// assembly), <see cref="LabcoatTest.Traits"/> its custom properties (<c>[Property]</c>), as TUnit reports them.
    /// </summary>
    public static LabcoatTest ToLabcoatTest(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var details = context.Metadata.TestDetails;
        return new LabcoatTest(context.Metadata.DisplayName)
        {
            Id = details.TestId,
            ClassName = details.ClassType.FullName,
            MethodName = details.MethodName,
            Framework = Framework,
            Categories = [.. details.Categories],
            Traits = [.. details.CustomProperties.SelectMany(p => p.Value.Select(v => new KeyValuePair<string, string>(p.Key, v)))],
        };
    }

    /// <summary>Maps TUnit's test state to the span outcome.</summary>
    public static LabcoatTestOutcome ToOutcome(TestState? state) => state switch
    {
        TestState.Passed => LabcoatTestOutcome.Passed,
        TestState.Failed => LabcoatTestOutcome.Failed,
        TestState.Timeout => LabcoatTestOutcome.TimedOut,
        TestState.Cancelled => LabcoatTestOutcome.Cancelled,
        TestState.Skipped => LabcoatTestOutcome.Skipped,
        _ => LabcoatTestOutcome.Unknown,
    };
}
