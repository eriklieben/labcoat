using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Labcoat.Testing.TUnit.Tests;

/// <summary>
/// Labcoat.Testing.TUnit, end to end inside TUnit: [assembly: LabcoatTracing] comes from the package's build targets,
/// tracing is on for the whole process (<see cref="Setup"/>), and every test here checks the span TUnit's own hooks
/// gave it. Not in parallel: some tests switch tracing off for a moment.
/// </summary>
[NotInParallel]
public sealed class LabcoatTracingAttributeTests
{
    private static Activity? s_passedActivity;

    /// <summary>The test's span, as the adapter left it in the state bag.</summary>
    private static LabcoatTestSpan CurrentSpan()
    {
        Assert.NotNull(TestContext.Current);
        var items = TestContext.Current!.StateBag.Items;
        return items.TryGetValue(LabcoatTracingAttribute.StateKey, out var value) && value is LabcoatTestSpan span
            ? span
            : throw new InvalidOperationException("The adapter did not start a span for this test.");
    }

    [Test]
    public async Task The_assembly_attribute_is_registered_by_the_build_targets()
    {
        var attributes = typeof(LabcoatTracingAttributeTests).Assembly.GetCustomAttributes(typeof(LabcoatTracingAttribute), false);
        await Assert.That(attributes.Length).IsEqualTo(1);
        await Assert.That(((LabcoatTracingAttribute)attributes[0]).Filter).IsEqualTo("LabcoatTracingAttributeTests"); // <LabcoatTracingFilter>
        await Assert.That(LabcoatTracing.FrameworkSources).Contains(LabcoatTracingAttribute.TUnitActivitySourceName);
    }

    [Test]
    [Category("Integration")]
    [Property("Kind", "Slow")]
    [Property("Kind", "Fast")]
    public async Task Categories_and_properties_are_passed()
    {
        var test = LabcoatTracingAttribute.ToLabcoatTest(TestContext.Current!);

        await Assert.That(test.Categories).Contains("Integration");
        await Assert.That(test.Traits).Contains(new KeyValuePair<string, string>("Kind", "Slow"));
        await Assert.That(test.Traits).Contains(new KeyValuePair<string, string>("Kind", "Fast"));
        await Assert.That(CurrentSpan().Test.Categories).Contains("Integration");
    }

    [Test]
    public async Task Labcoats_filter_wins_over_the_attributes()
    {
        var context = TestContext.Current!;
        var own = CurrentSpan();
        var saved = Environment.GetEnvironmentVariable(LabcoatTracing.FilterVariable);
        try
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, "cat:Unit");
            await new LabcoatTracingAttribute { Filter = "LabcoatTracingAttributeTests" }.OnTestStart(context); // started again
        }
        finally
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.FilterVariable, saved);
        }
        await Assert.That(own.IsEnded).IsTrue();
        await Assert.That(context.StateBag.Items.ContainsKey(LabcoatTracingAttribute.StateKey)).IsFalse();
        await Assert.That(LabcoatTracing.IsTestTrace(context.Activity!.TraceId)).IsTrue(); // registered by this test's own start
    }

    [Test]
    public async Task The_test_body_runs_in_TUnits_per_test_trace_which_Labcoat_adopts()
    {
        var context = TestContext.Current!;
        var span = CurrentSpan();
        var activity = Activity.Current;

        await Assert.That(activity).IsNotNull();
        // TUnit's "test case" span (a new root per test), or a span beneath it (TUnit 1.73 adds "test body").
        var root = activity!;
        while (root.Parent is not null) root = root.Parent;
        await Assert.That(root).IsSameReferenceAs(span.Activity);
        await Assert.That(root.Source.Name).IsEqualTo("TUnit");
        await Assert.That(context.Activity).IsSameReferenceAs(root);
        await Assert.That(span.IsOwned).IsFalse();
        await Assert.That(root.ParentSpanId).IsEqualTo(default(ActivitySpanId));
        await Assert.That(root.DisplayName).IsEqualTo(context.Metadata.DisplayName);
        await Assert.That(LabcoatTracing.IsTestTrace(activity.TraceId)).IsTrue();
        // The trace id Labcoat reads from this test's output (the stdOut of its result).
        await Assert.That(context.GetStandardOutput()).Contains(LabcoatTracing.TraceIdMarker + activity.TraceId.ToHexString());
    }

    [Test]
    public async Task The_test_id_is_the_test_node_uid_TUnit_reports()
    {
        var context = TestContext.Current!;
        var activity = CurrentSpan().Activity;
        var uid = context.Metadata.TestDetails.TestId;

        await Assert.That(activity.GetTagItem("test.id")).IsEqualTo(uid);
        // TUnit's engine tags its span with the uid of the TestNode it sends over Microsoft.Testing.Platform.
        await Assert.That(activity.GetTagItem("tunit.test.node_uid")).IsEqualTo(uid);
        await Assert.That(activity.GetTagItem("test.class")).IsEqualTo(typeof(LabcoatTracingAttributeTests).FullName);
        await Assert.That(activity.GetTagItem("test.name")).IsEqualTo(nameof(The_test_id_is_the_test_node_uid_TUnit_reports));
        await Assert.That(activity.GetTagItem("test.framework")).IsEqualTo("tunit");
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Data_rows_get_a_trace_and_id_each(int row)
    {
        var span = CurrentSpan();
        await Assert.That(span.Test.Id).IsEqualTo(TestContext.Current!.Metadata.TestDetails.TestId);
        await Assert.That(span.Test.DisplayName).Contains(row.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(span.Activity.TraceId).IsEqualTo(Activity.Current!.TraceId);
    }

    [Test]
    public async Task Http_calls_carry_the_tests_traceparent()
    {
        // A real HttpClient (its DiagnosticsHandler adds the W3C headers) against a one-shot local server.
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        var request = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var text = new System.Text.StringBuilder();
            while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                text.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, n));
            }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            return text.ToString();
        });
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/orders"));
        var headers = await request;

        await Assert.That(headers).Contains($"traceparent: 00-{CurrentSpan().TraceId}-");
    }

    [Test]
    public async Task Passes()
    {
        s_passedActivity = CurrentSpan().Activity;
        await Assert.That(s_passedActivity.GetTagItem("test.outcome")).IsNull();
    }

    [Test]
    [DependsOn(nameof(Passes))]
    public async Task The_end_hook_tags_the_outcome()
    {
        await Assert.That(s_passedActivity).IsNotNull();
        await Assert.That(s_passedActivity!.GetTagItem("test.outcome")).IsEqualTo("Passed");
        await Assert.That(s_passedActivity.Status).IsEqualTo(ActivityStatusCode.Ok);
    }

    [Test]
    public async Task A_retry_ends_the_previous_attempts_span()
    {
        var context = TestContext.Current!;
        var first = CurrentSpan();

        await new LabcoatTracingAttribute().OnTestStart(context); // what TUnit does when it retries the test

        var second = CurrentSpan();
        await Assert.That(second).IsNotSameReferenceAs(first);
        await Assert.That(first.IsEnded).IsTrue();
        await Assert.That(second.IsEnded).IsFalse();
        await Assert.That(second.Activity).IsSameReferenceAs(first.Activity); // TUnit's activity of this attempt
    }

    [Test]
    public async Task Ending_early_leaves_nothing_for_the_end_hook()
    {
        var context = TestContext.Current!;
        var span = CurrentSpan();
        var receiver = new LabcoatTracingAttribute();

        await receiver.OnTestEnd(context); // still running: no result yet
        await receiver.OnTestEnd(context); // nothing left (like a skipped test)

        await Assert.That(span.IsEnded).IsTrue();
        await Assert.That(span.Activity.GetTagItem("test.outcome")).IsEqualTo("Unknown");
        await Assert.That(context.StateBag.Items.ContainsKey(LabcoatTracingAttribute.StateKey)).IsFalse();
    }

    [Test]
    public async Task Tracing_off_does_nothing()
    {
        var context = TestContext.Current!;
        var span = CurrentSpan();
        var endpoint = Environment.GetEnvironmentVariable(LabcoatTracing.EndpointVariable);
        try
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, null);
            await new LabcoatTracingAttribute().OnTestStart(context);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, endpoint);
        }
        await Assert.That(CurrentSpan()).IsSameReferenceAs(span);
        await Assert.That(span.IsEnded).IsFalse();
    }

    [Test]
    public async Task Null_contexts_are_rejected()
    {
        var receiver = new LabcoatTracingAttribute();
        await Assert.That(() => receiver.OnTestStart(null!).AsTask()).Throws<ArgumentNullException>();
        await Assert.That(() => receiver.OnTestEnd(null!).AsTask()).Throws<ArgumentNullException>();
        await Assert.That(() => LabcoatTracingAttribute.ToLabcoatTest(null!)).Throws<ArgumentNullException>();
        await Assert.That(receiver.Order).IsEqualTo(0);
    }

    [Test]
    [Arguments(TestState.Passed, LabcoatTestOutcome.Passed)]
    [Arguments(TestState.Failed, LabcoatTestOutcome.Failed)]
    [Arguments(TestState.Timeout, LabcoatTestOutcome.TimedOut)]
    [Arguments(TestState.Cancelled, LabcoatTestOutcome.Cancelled)]
    [Arguments(TestState.Skipped, LabcoatTestOutcome.Skipped)]
    [Arguments(TestState.Running, LabcoatTestOutcome.Unknown)]
    public async Task States_map_to_the_span_outcome(TestState state, LabcoatTestOutcome expected) =>
        await Assert.That(LabcoatTracingAttribute.ToOutcome(state)).IsEqualTo(expected);
}

/// <summary>
/// Tests the assembly's default trace filter (<c>&lt;LabcoatTracingFilter&gt;</c>, free text that these tests'
/// names don't contain) leaves out: no span and no trace id; TUnit's own per-test activity is left as TUnit made it
/// (not adopted, so not exported).
/// </summary>
[NotInParallel]
public sealed class FilteredOutTests
{
    [Test]
    public async Task A_test_the_filter_leaves_out_gets_no_span_and_no_trace_id()
    {
        var context = TestContext.Current!;
        await Assert.That(context.StateBag.Items.ContainsKey(LabcoatTracingAttribute.StateKey)).IsFalse();
        await Assert.That(context.GetStandardOutput()).DoesNotContain(LabcoatTracing.TraceIdMarker);
        await Assert.That(context.Activity).IsNotNull();
        await Assert.That(context.Activity!.DisplayName).IsNotEqualTo(context.Metadata.DisplayName); // not renamed: not adopted
        await Assert.That(LabcoatTracing.IsTestTrace(context.Activity.TraceId)).IsFalse(); // not exported
    }
}

/// <summary>Turns tracing on before TUnit discovers the tests (exporting to a port nothing listens on).</summary>
internal static class Setup
{
    [ModuleInitializer]
    internal static void EnableTracing()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LabcoatTracing.EndpointVariable)))
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "http://127.0.0.1:4549");
        Environment.SetEnvironmentVariable(LabcoatTracing.ProtocolVariable, "http/protobuf");
    }
}
