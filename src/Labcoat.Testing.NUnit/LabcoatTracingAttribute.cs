using System.Collections.Concurrent;
using System.Globalization;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Labcoat.Testing;

/// <summary>
/// NUnit adapter of <see cref="LabcoatTracing"/>: gives every test it applies to its own trace, linked from Labcoat to
/// the Aspire dashboard. The package adds it to the test assembly (<c>[assembly: LabcoatTracing]</c>, see
/// <c>build/Labcoat.Testing.NUnit.targets</c>), so there is nothing to write; put it on fixtures or methods instead with
/// <c>&lt;LabcoatTracingAutoRegister&gt;false&lt;/LabcoatTracingAutoRegister&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// An NUnit action attribute (<see cref="ITestAction"/>, <see cref="ActionTargets.Test"/>): NUnit calls
/// <see cref="BeforeTest"/> right before each test's <c>[SetUp]</c> and <see cref="AfterTest"/> after its
/// <c>[TearDown]</c>, synchronously on the test's thread, so the span <see cref="BeforeTest"/> starts is
/// <see cref="System.Diagnostics.Activity.Current"/> in the test body and HTTP calls the test makes carry its
/// <c>traceparent</c>. NUnit has no per-test activity of its own, so Labcoat starts one.
/// </para>
/// <para>
/// <c>test.id</c> is the test node uid Labcoat shows: NUnit3TestAdapter's Microsoft.Testing.Platform runner uses the
/// test's fully qualified name (<see cref="ITest.FullName"/>, e.g. <c>Ns.Fixture.Method(1,2)</c>) as the uid. A no-op
/// unless the tests run under Labcoat with test tracing on.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LabcoatTracingAttribute : Attribute, ITestAction
{
    /// <summary>The <c>test.framework</c> tag value.</summary>
    public const string Framework = "nunit";

    // The span of each running test (by NUnit's test object): BeforeTest and AfterTest of one test run on one thread,
    // tests may run in parallel.
    private static readonly ConcurrentDictionary<ITest, LabcoatTestSpan> s_spans = new();

    /// <summary>NUnit's property of a category (<c>PropertyNames.Category</c>); Labcoat shows it as the trait <c>Category</c>.</summary>
    private const string CategoryProperty = "Category";

    // NUnit's own properties, which NUnit3TestAdapter does not report as traits (nor names starting with "_").
    private static readonly HashSet<string> s_notTraits = new(StringComparer.Ordinal)
    {
        CategoryProperty, "Author", "ApartmentState", "Description", "IgnoreUntilDate", "LevelOfParallelism", "MaxTime", "Order",
        "ParallelScope", "Repeat", "RequiresThread", "SetCulture", "SetUICulture", "TestOf", "Timeout",
    };

    /// <inheritdoc />
    public ActionTargets Targets => ActionTargets.Test;

    /// <summary>
    /// The default trace filter of the tests this attribute applies to, in the Labcoat UI's search syntax
    /// (<c>cat:Integration</c>, <c>trait:Kind=Slow</c>, name text): only matching tests are traced. Used when Labcoat
    /// passes no filter of its own (the AppHost's wins). Set it from the project file with
    /// <c>&lt;LabcoatTracingFilter&gt;cat:Integration&lt;/LabcoatTracingFilter&gt;</c>. Null: every test.
    /// </summary>
    public string? Filter { get; set; }

    /// <inheritdoc />
    public void BeforeTest(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!LabcoatTracing.IsEnabled) return;
        // A retry ([Retry]) runs the test again: end the span of the previous attempt.
        if (s_spans.TryRemove(test, out var stale))
            LabcoatTracing.EndTest(stale, LabcoatTestOutcome.Failed, "Retried");
        var labcoatTest = ToLabcoatTest(test);
        if (!LabcoatTracing.ShouldTrace(labcoatTest, Filter)) return;
        var span = LabcoatTracing.BeginTest(labcoatTest, TestContext.Out.WriteLine);
        if (span is not null) s_spans[test] = span;
    }

    /// <inheritdoc />
    public void AfterTest(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!s_spans.TryRemove(test, out var span)) return;
        var state = TestContext.CurrentContext.Result.Outcome;
        LabcoatTracing.EndTest(span, ToOutcome(state), state.ToString());
    }

    /// <summary>Tests: the span of a running test.</summary>
    internal static LabcoatTestSpan? SpanOf(ITest test) => s_spans.TryGetValue(test, out var span) ? span : null;

    /// <summary>
    /// The test as Labcoat.Testing describes it; <see cref="LabcoatTest.Id"/> is the test node uid NUnit3TestAdapter
    /// reports to Labcoat (the fully qualified name, data rows included). <see cref="LabcoatTest.Categories"/> and
    /// <see cref="LabcoatTest.Traits"/> are the categories and properties of the test and its fixture, assembly and
    /// other parents, as NUnit3TestAdapter reports them (without NUnit's own properties such as <c>Description</c>; an
    /// explicit test also has the category <c>Explicit</c>).
    /// </summary>
    public static LabcoatTest ToLabcoatTest(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var categories = new List<string>();
        var traits = new List<KeyValuePair<string, string>>();
        for (var t = test; t is not null; t = t.Parent) AddProperties(t.Properties, categories, traits);
        if (test.RunState == RunState.Explicit) categories.Add("Explicit");
        return Describe(test.Name, test.FullName, test.ClassName, test.MethodName) with
        {
            Categories = categories.Distinct(StringComparer.Ordinal).ToList(),
            Traits = traits,
        };
    }

    private static void AddProperties(IPropertyBag properties, List<string> categories, List<KeyValuePair<string, string>> traits)
    {
        foreach (var key in properties.Keys)
        {
            var values = properties[key].Cast<object?>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
            if (key == CategoryProperty) categories.AddRange(values);
            else if (!key.StartsWith('_') && !s_notTraits.Contains(key)) traits.AddRange(values.Select(v => new KeyValuePair<string, string>(key, v)));
        }
    }

    /// <summary>
    /// <see cref="LabcoatTest.ClassName"/> is the fixture part of the full name (<c>Ns.Fixture</c>, or
    /// <c>Ns.Fixture&lt;Int32&gt;(1)</c> for a parameterized fixture), as Labcoat shows it, so live output markers
    /// match the test's row.
    /// </summary>
    internal static LabcoatTest Describe(string name, string fullName, string? className, string? methodName) => new(name)
    {
        Id = fullName,
        ClassName = fullName.Length > name.Length + 1 && fullName.EndsWith("." + name, StringComparison.Ordinal)
            ? fullName[..^(name.Length + 1)]
            : className,
        MethodName = methodName,
        Framework = Framework,
    };

    /// <summary>Maps NUnit's result state to the span outcome.</summary>
    public static LabcoatTestOutcome ToOutcome(ResultState? state) => state?.Status switch
    {
        TestStatus.Passed => LabcoatTestOutcome.Passed,
        TestStatus.Failed => state.Label switch
        {
            "Error" => LabcoatTestOutcome.Error,
            "Cancelled" => LabcoatTestOutcome.Cancelled,
            "Invalid" => LabcoatTestOutcome.Skipped, // not runnable
            _ => LabcoatTestOutcome.Failed,
        },
        // Warning: NUnit3TestAdapter reports it as skipped by default (MapWarningTo).
        TestStatus.Skipped or TestStatus.Warning => LabcoatTestOutcome.Skipped,
        TestStatus.Inconclusive => LabcoatTestOutcome.Inconclusive,
        _ => LabcoatTestOutcome.Unknown,
    };
}
