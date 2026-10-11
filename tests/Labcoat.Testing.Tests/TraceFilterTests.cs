using System.Diagnostics;

namespace Labcoat.Testing.Tests;

/// <summary>
/// The trace filter: its syntax (the Labcoat UI's search, minus what depends on a result), how it matches a test's
/// names and traits, and how <see cref="LabcoatTracing.ShouldTrace"/> / <see cref="LabcoatTracing.BeginTest"/> use it
/// (Labcoat's filter from the environment wins over an adapter's default; parsed once; an invalid one traces nothing).
/// Process-wide state (environment, Activity.Current): not in parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TraceFilterTests : IDisposable
{
    private static readonly string[] EnvVars = [LabcoatTracing.EndpointVariable, LabcoatTracing.ProtocolVariable, LabcoatTracing.HeadersVariable, LabcoatTracing.FilterVariable];

    /// <summary>An integration test as an adapter describes it.</summary>
    private static readonly LabcoatTest PlacesOrder = new("PlacesOrder (2 items)")
    {
        Id = "Shop.Tests.Integration.OrderTests.PlacesOrder(2)",
        ClassName = "Shop.Tests.Integration.OrderTests",
        MethodName = "PlacesOrder",
        Framework = "fake",
        Categories = ["Integration", "Smoke"],
        Traits = [new("Kind", "Slow"), new("Owner", "web-team")],
    };

    /// <summary>A unit test without traits.</summary>
    private static readonly LabcoatTest AddsTax = new("AddsTax") { ClassName = "Shop.Tests.Unit.PricingTests", MethodName = "AddsTax" };

    private readonly Dictionary<string, string?> _savedEnv = EnvVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
    private readonly string? _savedProject = LabcoatTracing.ProjectName;
    private readonly TextWriter _savedErrors = LabcoatTracing.ErrorOutput;
    private readonly StringWriter _errors = new();
    private readonly List<Activity> _started = [];
    private readonly ActivityListener _listener;

    public TraceFilterTests()
    {
        foreach (var v in EnvVars) Environment.SetEnvironmentVariable(v, null);
        LabcoatTracing.ProjectName = "Shop.Tests";
        LabcoatTracing.ErrorOutput = _errors;
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LabcoatTracing.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => { lock (_started) _started.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var (k, v) in _savedEnv) Environment.SetEnvironmentVariable(k, v);
        LabcoatTracing.ProjectName = _savedProject;
        LabcoatTracing.ErrorOutput = _savedErrors;
        _errors.Dispose();
        Activity.Current = null;
    }

    private static void Enable(string? filter = null)
    {
        Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "http://127.0.0.1:4549");
        Environment.SetEnvironmentVariable(LabcoatTracing.ProtocolVariable, "http/protobuf");
        Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, filter);
    }

    private static bool Matches(string filter, LabcoatTest test, string? project = "Shop.Tests")
    {
        var compiled = TraceFilter.Compile(filter);
        Assert.IsNull(compiled.Error, filter);
        Assert.AreEqual(filter, compiled.Text);
        return compiled.Matches(test, project);
    }

    // ------------------------------------------------------------------ syntax

    [TestMethod]
    public void A_filter_is_a_list_of_terms_like_the_UIs_search()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                new TraceFilterTerm(TraceFilterTermKind.Category, "integration"),
                new TraceFilterTerm(TraceFilterTermKind.Trait, "kind", "slow"),
                new TraceFilterTerm(TraceFilterTermKind.Trait, "owner"),
                new TraceFilterTerm(TraceFilterTermKind.Project, "shop"),
                new TraceFilterTerm(TraceFilterTermKind.Text, "orders"),
                new TraceFilterTerm(TraceFilterTermKind.Category, "slow tests"),
                new TraceFilterTerm(TraceFilterTermKind.Text, "a b"),
                new TraceFilterTerm(TraceFilterTermKind.Text, "x"),
                new TraceFilterTerm(TraceFilterTermKind.Text, "other:thing"),
                new TraceFilterTerm(TraceFilterTermKind.Text, ":lead"),
            },
            TraceFilterSyntax.Parse("CAT:Integration  trait:Kind=Slow\ttrait:Owner Proj:Shop Orders cat:\"Slow tests\" \"a b\"x \"\" other:thing :lead").ToArray());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void A_blank_filter_has_no_terms(string? filter)
    {
        Assert.IsEmpty(TraceFilterSyntax.Parse(filter));
        Assert.IsNull(TraceFilterSyntax.Validate(filter));
    }

    [TestMethod]
    public void Trait_values_end_at_a_second_equals_sign_as_in_the_UI()
    {
        Assert.AreEqual(new TraceFilterTerm(TraceFilterTermKind.Trait, "k", "a"), TraceFilterSyntax.Parse("trait:k=a=b").Single());
        Assert.AreEqual(new TraceFilterTerm(TraceFilterTermKind.Trait, "k", ""), TraceFilterSyntax.Parse("trait:k=").Single());
    }

    [TestMethod]
    [DataRow("is:failed", "'is:' depends on the test's result")]
    [DataRow("cat:Integration msg:timeout", "'msg:' depends on the test's result")]
    [DataRow("cat:", "'cat:' needs a value, e.g. cat:Integration.")]
    [DataRow("trait:", "'trait:' needs a value, e.g. trait:Kind=Slow.")]
    [DataRow("proj:", "'proj:' needs a value, e.g. proj:Orders.Tests.")]
    [DataRow("trait:=Slow", "'trait:' needs a key, e.g. trait:Kind=Slow.")]
    [DataRow("cat:\"slow tests", "The quote at position 5 is not closed.")]
    public void An_invalid_filter_says_why(string filter, string error)
    {
        StringAssert.StartsWith(TraceFilterSyntax.Validate(filter), error);
        Assert.ThrowsExactly<FormatException>(() => TraceFilterSyntax.Parse(filter));
        var compiled = TraceFilter.Compile(filter);
        StringAssert.StartsWith(compiled.Error, error);
        Assert.IsFalse(compiled.Matches(PlacesOrder, "Shop.Tests"), "an invalid filter matches nothing");
    }

    // ------------------------------------------------------------------ matching

    [TestMethod]
    [DataRow("cat:Integration", true)]
    [DataRow("cat:integ", true)] // a substring, case-insensitive
    [DataRow("cat:smoke", true)]
    [DataRow("cat:Slow", true)] // any trait value, like the UI
    [DataRow("cat:web-team", true)]
    [DataRow("cat:Unit", false)]
    [DataRow("trait:Category=Integration", true)] // categories are Category traits
    [DataRow("trait:category", true)]
    [DataRow("trait:Kind=Slow", true)]
    [DataRow("trait:kind=SL", true)]
    [DataRow("trait:Kind", true)]
    [DataRow("trait:Kind=Fast", false)]
    [DataRow("trait:Kin=Slow", false)] // the key must be equal
    [DataRow("trait:Owner=web", true)]
    [DataRow("proj:shop", true)]
    [DataRow("proj:Billing", false)]
    [DataRow("OrderTests", true)] // class
    [DataRow("Shop.Tests.Integration", true)] // namespace
    [DataRow("placesorder", true)] // method
    [DataRow("(2 items)", true)] // display name
    [DataRow("Integration.OrderTests.PlacesOrder", true)]
    [DataRow("web-team", true)] // free text also finds trait values
    [DataRow("Pricing", false)]
    [DataRow("cat:Integration trait:Kind=Slow OrderTests", true)] // every term must match
    [DataRow("cat:Integration trait:Kind=Fast", false)]
    [DataRow("cat:Integration Pricing", false)]
    [DataRow("\"items)\" cat:\"integ\"", true)]
    public void A_filter_matches_names_categories_and_traits(string filter, bool expected) =>
        Assert.AreEqual(expected, Matches(filter, PlacesOrder), filter);

    [TestMethod]
    public void A_test_without_traits_or_project_matches_names_only()
    {
        Assert.IsTrue(Matches("PricingTests", AddsTax, project: null));
        Assert.IsFalse(Matches("cat:Unit", AddsTax, project: null));
        Assert.IsFalse(Matches("trait:Category", AddsTax, project: null));
        Assert.IsFalse(Matches("proj:shop", AddsTax, project: null));
        Assert.IsTrue(Matches("addstax", new LabcoatTest("AddsTax"), project: null), "the display name alone");
        Assert.IsTrue(Matches("", AddsTax), "no terms: every test");
    }

    // ------------------------------------------------------------------ LabcoatTest

    [TestMethod]
    public void Categories_and_traits_default_to_empty_and_take_part_in_equality()
    {
        var test = new LabcoatTest("T") { Categories = null!, Traits = null! };
        Assert.IsEmpty(test.Categories);
        Assert.IsEmpty(test.Traits);

        var a = new LabcoatTest("T") { Categories = ["A"], Traits = [new("K", "V")] };
        var b = new LabcoatTest("T") { Categories = ["A"], Traits = [new("K", "V")] };
        Assert.AreEqual(a, b, "the same values in other lists");
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.AreNotEqual(a, b with { Categories = ["B"] });
        Assert.AreNotEqual(a, b with { Traits = [new("K", "W")] });
        Assert.AreNotEqual(a, b with { Id = "x" });
        Assert.IsFalse(a.Equals(null));
        Assert.IsTrue(a.Equals(a));
    }

    // ------------------------------------------------------------------ ShouldTrace / BeginTest

    [TestMethod]
    public void Tracing_off_traces_nothing_whatever_the_filter()
    {
        Assert.IsFalse(LabcoatTracing.ShouldTrace(PlacesOrder));
        Assert.IsFalse(LabcoatTracing.ShouldTrace(PlacesOrder, "cat:Integration"));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatTracing.ShouldTrace(null!));
    }

    [TestMethod]
    public void Without_a_filter_every_test_is_traced()
    {
        Enable();
        Assert.IsTrue(LabcoatTracing.ShouldTrace(AddsTax));
        Assert.IsTrue(LabcoatTracing.ShouldTrace(AddsTax, " "));
        Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, "  ");
        Assert.IsTrue(LabcoatTracing.ShouldTrace(AddsTax), "a blank filter from Labcoat is no filter");
    }

    [TestMethod]
    public void Labcoats_filter_wins_over_the_adapters_default()
    {
        Enable();
        Assert.IsTrue(LabcoatTracing.ShouldTrace(PlacesOrder, "cat:Integration"));
        Assert.IsFalse(LabcoatTracing.ShouldTrace(AddsTax, "cat:Integration"), "the default, without one from Labcoat");

        Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, "PricingTests");
        Assert.IsTrue(LabcoatTracing.ShouldTrace(AddsTax, "cat:Integration"));
        Assert.IsFalse(LabcoatTracing.ShouldTrace(PlacesOrder, "cat:Integration"));
    }

    [TestMethod]
    public void A_test_the_filter_leaves_out_gets_no_span_no_trace_id_and_no_current_activity()
    {
        Enable("cat:Integration");
        var output = new List<string>();

        var skipped = LabcoatTracing.BeginTest(AddsTax, output.Add);

        Assert.IsNull(skipped);
        Assert.IsEmpty(output, "no trace id marker");
        Assert.IsEmpty(_started, "no span");
        Assert.IsNull(Activity.Current, "nothing for HttpClient to send as traceparent");
        LabcoatTracing.EndTest(skipped, LabcoatTestOutcome.Passed); // what an adapter does next: nothing

        var traced = LabcoatTracing.BeginTest(PlacesOrder, output.Add);
        Assert.IsNotNull(traced);
        Assert.AreSame(traced.Activity, _started.Single());
        Assert.AreEqual(LabcoatTracing.TraceIdMarker + traced.TraceId, output.Single());
        LabcoatTracing.EndTest(traced, LabcoatTestOutcome.Passed);
    }

    [TestMethod]
    public void An_invalid_filter_is_reported_once_and_traces_nothing()
    {
        var filter = "is:failed " + Guid.NewGuid().ToString("N"); // a filter text this process has not seen yet
        Enable(filter);

        Assert.IsFalse(LabcoatTracing.ShouldTrace(PlacesOrder));
        Assert.IsNull(LabcoatTracing.BeginTest(AddsTax, _ => Assert.Fail("no output")));
        Assert.IsFalse(LabcoatTracing.ShouldTrace(AddsTax));

        var lines = _errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(1, lines, "reported once, not per test");
        StringAssert.StartsWith(lines[0], $"Labcoat.Testing: {LabcoatTracing.FilterVariable} '{filter}' is not a valid trace filter: 'is:'");
        StringAssert.Contains(lines[0], "No test is traced.");

        Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, null);
        var invalidDefault = "cat: " + Guid.NewGuid().ToString("N");
        Assert.IsFalse(LabcoatTracing.ShouldTrace(PlacesOrder, invalidDefault));
        StringAssert.Contains(_errors.ToString(), $"LabcoatTracing(Filter) '{invalidDefault}' is not a valid trace filter: 'cat:' needs a value");
    }
}
