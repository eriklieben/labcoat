using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Labcoat.Testing.XUnit.Tests;

/// <summary>
/// Labcoat.Testing.XUnit, end to end inside xUnit v3: [assembly: LabcoatTracing] comes from the package's build
/// targets, tracing is on for the whole process (<see cref="Setup"/>), and every test here checks the span the
/// before / after attribute gave it. One class, so these run one at a time (some switch tracing off for a moment).
/// </summary>
public sealed class LabcoatTracingAttributeTests
{
    private static ITestContext Context => TestContext.Current;

    /// <summary>xUnit's object for the running test (what xUnit passes to the attribute).</summary>
    private static IXunitTest CurrentTest => (IXunitTest)Context.Test!;

    private static System.Reflection.MethodInfo CurrentMethod => CurrentTest.TestMethod.Method;

    /// <summary>The test's span, as the adapter keeps it.</summary>
    private static LabcoatTestSpan CurrentSpan() =>
        LabcoatTracingAttribute.SpanOf(Context.Test!.UniqueID) ?? throw new InvalidOperationException("The adapter did not start a span for this test.");

    [Fact]
    public void The_assembly_attribute_is_registered_by_the_build_targets()
    {
        var attributes = typeof(LabcoatTracingAttributeTests).Assembly.GetCustomAttributes(typeof(LabcoatTracingAttribute), false);
        Assert.Single(attributes);
    }

    [Fact]
    public async Task The_test_body_runs_in_the_span_Labcoat_started()
    {
        await Task.Yield(); // also after an await
        var span = CurrentSpan();
        var activity = Activity.Current;

        Assert.Same(span.Activity, activity);
        Assert.True(span.IsOwned);
        Assert.Equal(LabcoatTracing.ActivitySourceName, activity!.Source.Name);
        Assert.Null(activity.Parent);
        Assert.Equal(default, activity.ParentSpanId);
        Assert.Equal(Context.Test!.TestDisplayName, activity.DisplayName);
        Assert.True(LabcoatTracing.IsTestTrace(activity.TraceId));
        // The trace id Labcoat reads from this test's output (the standard output of its result).
        Assert.Contains(LabcoatTracing.TraceIdMarker + activity.TraceId.ToHexString(), Context.TestOutputHelper!.Output);
    }

    [Fact]
    public void The_test_id_is_the_test_case_unique_id_xUnit_uses_as_the_test_node_uid()
    {
        var activity = CurrentSpan().Activity;

        Assert.Equal(Context.TestCase!.UniqueID, activity.GetTagItem("test.id"));
        Assert.Equal(typeof(LabcoatTracingAttributeTests).FullName, activity.GetTagItem("test.class"));
        Assert.Equal(nameof(The_test_id_is_the_test_case_unique_id_xUnit_uses_as_the_test_node_uid), activity.GetTagItem("test.name"));
        Assert.Equal("xunit", activity.GetTagItem("test.framework"));
    }

    [Theory]
    [InlineData(1, "a b")]
    [InlineData(2, "x\"y")]
    public void Data_rows_get_a_trace_and_id_each(int row, string text)
    {
        var span = CurrentSpan();
        Assert.Equal(Context.TestCase!.UniqueID, span.Test.Id);
        Assert.Contains(row.ToString(CultureInfo.InvariantCulture), span.Test.DisplayName, StringComparison.Ordinal);
        Assert.Contains(text.Replace("\"", "\\\"", StringComparison.Ordinal), span.Test.DisplayName, StringComparison.Ordinal);
        Assert.Equal(span.Activity.TraceId, Activity.Current!.TraceId);
    }

    [Fact]
    public async Task Http_calls_carry_the_tests_traceparent()
    {
        // A real HttpClient (its DiagnosticsHandler adds the W3C headers) against a one-shot local server.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var ct = Context.CancellationToken;
        var request = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var text = new StringBuilder();
            while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
            return text.ToString();
        }, ct);
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/orders"), ct);
        var headers = await request;

        Assert.Contains($"traceparent: 00-{CurrentSpan().TraceId}-", headers, StringComparison.Ordinal);
    }

    [Fact]
    public void Starting_again_ends_the_previous_attempts_span()
    {
        var first = CurrentSpan();

        new LabcoatTracingAttribute().Before(CurrentMethod, CurrentTest); // what a retrying extension would do

        var second = CurrentSpan();
        Assert.NotSame(first, second);
        Assert.True(first.IsEnded);
        Assert.Equal("Retried", first.Activity.GetTagItem("test.outcome"));
        Assert.False(second.IsEnded);
        Assert.Same(second.Activity, Activity.Current);
    }

    [Fact]
    public void Ending_early_leaves_nothing_for_the_after_hook()
    {
        var span = CurrentSpan();
        var attribute = new LabcoatTracingAttribute();

        attribute.After(CurrentMethod, CurrentTest); // still running: the result is not known yet
        attribute.After(CurrentMethod, CurrentTest); // nothing left

        Assert.True(span.IsEnded);
        Assert.NotNull(span.Activity.GetTagItem("test.outcome"));
        Assert.Null(LabcoatTracingAttribute.SpanOf(Context.Test!.UniqueID));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Smoke")]
    [Trait("Kind", "Slow")]
    public void Traits_are_passed_with_Category_values_as_categories()
    {
        var test = LabcoatTracingAttribute.ToLabcoatTest(CurrentTest);

        Assert.Equal(["Integration", "Smoke"], test.Categories.Order());
        Assert.Equal([new KeyValuePair<string, string>("Kind", "Slow")], test.Traits);
        Assert.Equal(test.Categories, CurrentSpan().Test.Categories);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void A_test_the_attributes_filter_leaves_out_gets_no_span()
    {
        var own = CurrentSpan();

        // Started again with a filter this test does not match: the first attempt's span ends, no new one starts.
        new LabcoatTracingAttribute { Filter = "cat:Unit" }.Before(CurrentMethod, CurrentTest);

        Assert.True(own.IsEnded);
        Assert.Null(LabcoatTracingAttribute.SpanOf(Context.Test!.UniqueID));
        Assert.Null(Activity.Current);

        new LabcoatTracingAttribute { Filter = "cat:integ" }.Before(CurrentMethod, CurrentTest);
        Assert.NotNull(LabcoatTracingAttribute.SpanOf(Context.Test!.UniqueID));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Labcoats_filter_wins_over_the_attributes()
    {
        var saved = Environment.GetEnvironmentVariable(LabcoatTracing.FilterVariable);
        try
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, "cat:Unit");
            new LabcoatTracingAttribute { Filter = "cat:Integration" }.Before(CurrentMethod, CurrentTest);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, saved);
        }
        Assert.Null(LabcoatTracingAttribute.SpanOf(Context.Test!.UniqueID));
    }

    [Fact]
    public void Tracing_off_does_nothing()
    {
        var span = CurrentSpan();
        var endpoint = Environment.GetEnvironmentVariable(LabcoatTracing.EndpointVariable);
        try
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, null);
            new LabcoatTracingAttribute().Before(CurrentMethod, CurrentTest);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, endpoint);
        }
        Assert.Same(span, CurrentSpan());
        Assert.False(span.IsEnded);
    }

    [Fact]
    public void Null_tests_are_rejected()
    {
        var attribute = new LabcoatTracingAttribute();
        Assert.Throws<ArgumentNullException>(() => attribute.Before(CurrentMethod, null!));
        Assert.Throws<ArgumentNullException>(() => attribute.After(CurrentMethod, null!));
        Assert.Throws<ArgumentNullException>(() => LabcoatTracingAttribute.ToLabcoatTest(null!));
        Assert.Throws<ArgumentNullException>(() => LabcoatTracingAttribute.FrameworkOutcome(null!));
    }

    public static TheoryData<string, LabcoatTestOutcome, string?> Outcomes => new()
    {
        { "passed", LabcoatTestOutcome.Passed, "Passed" },
        { "assertion", LabcoatTestOutcome.Failed, "Failed:Assertion" },
        { "exception", LabcoatTestOutcome.Error, "Failed:Exception" },
        { "timeout", LabcoatTestOutcome.TimedOut, "Failed:Timeout" },
        { "skipped", LabcoatTestOutcome.Skipped, "Skipped" },
        { "notrun", LabcoatTestOutcome.Skipped, "NotRun" },
        { "null", LabcoatTestOutcome.Unknown, null },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Result_states_map_to_the_span_outcome(string state, LabcoatTestOutcome expected, string? frameworkOutcome)
    {
        var result = state switch
        {
            "passed" => TestResultState.ForPassed(),
            "assertion" => TestResultState.FromException(0m, new XunitException("assert")),
            "exception" => TestResultState.FromException(0m, new InvalidOperationException("boom")),
            "timeout" => TestResultState.FromException(0m, TestTimeoutException.ForTimedOutTest(10)),
            "skipped" => TestResultState.ForSkipped(),
            "notrun" => TestResultState.ForNotRun(),
            _ => null,
        };
        Assert.Equal(expected, LabcoatTracingAttribute.ToOutcome(result));
        if (result is not null) Assert.Equal(frameworkOutcome, LabcoatTracingAttribute.FrameworkOutcome(result));
    }
}

/// <summary>
/// The after hook, seen from the test class's <see cref="IDisposable.Dispose"/>: xUnit disposes the test class after
/// the <c>After</c> attributes ran (a failed assertion there fails the test).
/// </summary>
public sealed class AfterHookTests : IDisposable
{
    private LabcoatTestSpan? _span;

    [Fact]
    public void Passes()
    {
        _span = LabcoatTracingAttribute.SpanOf(TestContext.Current.Test!.UniqueID);
        Assert.NotNull(_span);
        Assert.Null(_span.Activity.GetTagItem("test.outcome"));
    }

    public void Dispose()
    {
        Assert.NotNull(_span);
        Assert.True(_span.IsEnded);
        Assert.Equal("Passed", _span.Activity.GetTagItem("test.outcome"));
        Assert.Equal(ActivityStatusCode.Ok, _span.Activity.Status);
        Assert.True(_span.Activity.IsStopped);
        Assert.Null(LabcoatTracingAttribute.SpanOf(TestContext.Current.Test!.UniqueID));
    }
}

/// <summary>Turns tracing on before the first test starts (exporting to a port nothing listens on).</summary>
internal static class Setup
{
    [ModuleInitializer]
    internal static void EnableTracing()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LabcoatTracing.EndpointVariable)))
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "http://127.0.0.1:4657");
        Environment.SetEnvironmentVariable(LabcoatTracing.ProtocolVariable, "http/protobuf");
    }
}
