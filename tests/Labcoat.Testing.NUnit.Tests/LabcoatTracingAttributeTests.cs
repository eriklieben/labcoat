using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace Labcoat.Testing.NUnit.Tests;

/// <summary>
/// Labcoat.Testing.NUnit, end to end inside NUnit: [assembly: LabcoatTracing] comes from the package's build targets,
/// tracing is on for the whole process (<see cref="Setup"/>), and every test here checks the span the action attribute
/// gave it. NUnit runs these one at a time (no [Parallelizable]); some tests switch tracing off for a moment.
/// </summary>
public sealed class LabcoatTracingAttributeTests
{
    private static Activity? s_passedActivity;

    /// <summary>NUnit's object for the running test (what NUnit passes to the action attribute).</summary>
    private static ITest CurrentTest => TestExecutionContext.CurrentContext.CurrentTest;

    /// <summary>The test's span, as the adapter keeps it.</summary>
    private static LabcoatTestSpan CurrentSpan() =>
        LabcoatTracingAttribute.SpanOf(CurrentTest) ?? throw new InvalidOperationException("The adapter did not start a span for this test.");

    [Test]
    public void The_assembly_attribute_is_registered_by_the_build_targets()
    {
        var attributes = typeof(LabcoatTracingAttributeTests).Assembly.GetCustomAttributes(typeof(LabcoatTracingAttribute), false);
        Assert.That(attributes, Has.Length.EqualTo(1));
        Assert.That(((LabcoatTracingAttribute)attributes[0]).Targets, Is.EqualTo(ActionTargets.Test));
    }

    [Test]
    public async Task The_test_body_runs_in_the_span_Labcoat_started()
    {
        await Task.Yield(); // also after an await
        var span = CurrentSpan();
        var activity = Activity.Current;

        Assert.That(activity, Is.SameAs(span.Activity));
        Assert.That(span.IsOwned, Is.True);
        Assert.That(activity!.Source.Name, Is.EqualTo(LabcoatTracing.ActivitySourceName));
        Assert.That(activity.Parent, Is.Null);
        Assert.That(activity.ParentSpanId, Is.EqualTo(default(ActivitySpanId)));
        Assert.That(activity.DisplayName, Is.EqualTo(TestContext.CurrentContext.Test.Name));
        Assert.That(LabcoatTracing.IsTestTrace(activity.TraceId), Is.True);
        // The trace id Labcoat reads from this test's output (the standard output of its result).
        Assert.That(TestExecutionContext.CurrentContext.CurrentResult.Output, Does.Contain(LabcoatTracing.TraceIdMarker + activity.TraceId.ToHexString()));
    }

    [Test]
    public void The_test_id_is_the_full_name_NUnit3TestAdapter_uses_as_the_test_node_uid()
    {
        var activity = CurrentSpan().Activity;
        var test = TestContext.CurrentContext.Test;

        Assert.That(activity.GetTagItem("test.id"), Is.EqualTo(test.FullName));
        Assert.That(activity.GetTagItem("test.class"), Is.EqualTo(typeof(LabcoatTracingAttributeTests).FullName));
        Assert.That(activity.GetTagItem("test.name"), Is.EqualTo(nameof(The_test_id_is_the_full_name_NUnit3TestAdapter_uses_as_the_test_node_uid)));
        Assert.That(activity.GetTagItem("test.framework"), Is.EqualTo("nunit"));
    }

    [TestCase(1, "a b")]
    [TestCase(2, "x\"y")]
    public void Data_rows_get_a_trace_and_id_each(int row, string text)
    {
        var span = CurrentSpan();
        Assert.That(span.Test.Id, Is.EqualTo(TestContext.CurrentContext.Test.FullName));
        Assert.That(span.Test.Id, Does.Contain(row.ToString(CultureInfo.InvariantCulture)));
        Assert.That(span.Test.DisplayName, Does.StartWith("Data_rows_get_a_trace_and_id_each(").And.Contains(text.Replace("\"", "\\\"", StringComparison.Ordinal)));
        Assert.That(span.Test.ClassName, Is.EqualTo(typeof(LabcoatTracingAttributeTests).FullName));
        Assert.That(span.Activity.TraceId, Is.EqualTo(Activity.Current!.TraceId));
    }

    [Test]
    public async Task Http_calls_carry_the_tests_traceparent()
    {
        // A real HttpClient (its DiagnosticsHandler adds the W3C headers) against a one-shot local server.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var request = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var text = new StringBuilder();
            while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            return text.ToString();
        });
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/orders"));
        var headers = await request;

        Assert.That(headers, Does.Contain($"traceparent: 00-{CurrentSpan().TraceId}-"));
    }

    [Test]
    public void Passes()
    {
        s_passedActivity = CurrentSpan().Activity;
        Assert.That(s_passedActivity.GetTagItem("test.outcome"), Is.Null);
    }

    [Test, DependsOnTest(nameof(Passes))]
    public void The_after_hook_tags_the_outcome_and_ends_the_span()
    {
        Assert.That(s_passedActivity, Is.Not.Null, "Passes runs first ([DependsOnTest])");
        Assert.That(s_passedActivity!.GetTagItem("test.outcome"), Is.EqualTo("Passed"));
        Assert.That(s_passedActivity.Status, Is.EqualTo(ActivityStatusCode.Ok));
        Assert.That(s_passedActivity.IsStopped, Is.True);
    }

    [Test]
    public void A_retry_ends_the_previous_attempts_span()
    {
        var first = CurrentSpan();

        new LabcoatTracingAttribute().BeforeTest(CurrentTest); // what NUnit does when [Retry] runs the test again

        var second = CurrentSpan();
        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(first.IsEnded, Is.True);
        Assert.That(first.Activity.GetTagItem("test.outcome"), Is.EqualTo("Retried"));
        Assert.That(second.IsEnded, Is.False);
        Assert.That(Activity.Current, Is.SameAs(second.Activity));
    }

    [Test]
    public void Ending_early_leaves_nothing_for_the_after_hook()
    {
        var span = CurrentSpan();
        var action = new LabcoatTracingAttribute();

        action.AfterTest(CurrentTest); // still running: no final result yet
        action.AfterTest(CurrentTest); // nothing left (like a test that never started)

        Assert.That(span.IsEnded, Is.True);
        Assert.That(span.Activity.GetTagItem("test.outcome"), Is.Not.Null);
        Assert.That(LabcoatTracingAttribute.SpanOf(CurrentTest), Is.Null);
    }

    [Test]
    public void Tracing_off_does_nothing()
    {
        var span = CurrentSpan();
        var endpoint = Environment.GetEnvironmentVariable(LabcoatTracing.EndpointVariable);
        try
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, null);
            new LabcoatTracingAttribute().BeforeTest(CurrentTest);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, endpoint);
        }
        Assert.That(CurrentSpan(), Is.SameAs(span));
        Assert.That(span.IsEnded, Is.False);
    }

    [Test]
    public void Null_tests_are_rejected()
    {
        var action = new LabcoatTracingAttribute();
        Assert.Throws<ArgumentNullException>(() => action.BeforeTest(null!));
        Assert.Throws<ArgumentNullException>(() => action.AfterTest(null!));
        Assert.Throws<ArgumentNullException>(() => LabcoatTracingAttribute.ToLabcoatTest(null!));
    }

    [Test]
    public void The_class_name_falls_back_to_NUnits_when_the_full_name_does_not_end_in_the_name()
    {
        var test = LabcoatTracingAttribute.Describe("Odd", "Ns.Fixture.Other", "Ns.Fixture", "Other");
        Assert.That(test.ClassName, Is.EqualTo("Ns.Fixture"));
        Assert.That(test.Id, Is.EqualTo("Ns.Fixture.Other"));
        Assert.That(LabcoatTracingAttribute.Describe("M", "M", "C", "M").ClassName, Is.EqualTo("C"));
        Assert.That(LabcoatTracingAttribute.Describe("M", "Ns.C.M", "Ns.C", "M").ClassName, Is.EqualTo("Ns.C"));
    }

    public static IEnumerable<TestCaseData> Outcomes()
    {
        yield return new TestCaseData(ResultState.Success, LabcoatTestOutcome.Passed);
        yield return new TestCaseData(ResultState.Failure, LabcoatTestOutcome.Failed);
        yield return new TestCaseData(ResultState.SetUpFailure, LabcoatTestOutcome.Failed);
        yield return new TestCaseData(ResultState.Error, LabcoatTestOutcome.Error);
        yield return new TestCaseData(ResultState.TearDownError, LabcoatTestOutcome.Error);
        yield return new TestCaseData(ResultState.Cancelled, LabcoatTestOutcome.Cancelled);
        yield return new TestCaseData(ResultState.NotRunnable, LabcoatTestOutcome.Skipped);
        yield return new TestCaseData(ResultState.Skipped, LabcoatTestOutcome.Skipped);
        yield return new TestCaseData(ResultState.Ignored, LabcoatTestOutcome.Skipped);
        yield return new TestCaseData(ResultState.Explicit, LabcoatTestOutcome.Skipped);
        yield return new TestCaseData(ResultState.Warning, LabcoatTestOutcome.Skipped);
        yield return new TestCaseData(ResultState.Inconclusive, LabcoatTestOutcome.Inconclusive);
        yield return new TestCaseData(null, LabcoatTestOutcome.Unknown);
        yield return new TestCaseData(new ResultState((TestStatus)42), LabcoatTestOutcome.Unknown);
    }

    [TestCaseSource(nameof(Outcomes))]
    public void Result_states_map_to_the_span_outcome(ResultState? state, LabcoatTestOutcome expected) =>
        Assert.That(LabcoatTracingAttribute.ToOutcome(state), Is.EqualTo(expected));
}

/// <summary>A parameterized fixture: its tests' class (as Labcoat shows it) includes the fixture arguments.</summary>
[TestFixture(1)]
[TestFixture("two")]
public sealed class ParameterizedFixtureTests<T>(T value)
{
    [Test]
    public void The_class_is_the_fixture_part_of_the_full_name()
    {
        var span = LabcoatTracingAttribute.SpanOf(TestExecutionContext.CurrentContext.CurrentTest)!;
        Assert.That(span, Is.Not.Null);
        Assert.That(span.Test.Id, Is.EqualTo(TestContext.CurrentContext.Test.FullName));
        Assert.That(span.Test.ClassName, Does.StartWith($"{typeof(ParameterizedFixtureTests<T>).Namespace}.ParameterizedFixtureTests<"));
        Assert.That(span.Test.ClassName, Does.EndWith(")"));
        Assert.That(span.Test.ClassName, Does.Contain(value!.ToString()));
        Assert.That(span.Test.Id, Is.EqualTo(span.Test.ClassName + "." + span.Test.DisplayName));
    }
}

/// <summary>Turns tracing on before the first test starts (exporting to a port nothing listens on).</summary>
internal static class Setup
{
    [ModuleInitializer]
    internal static void EnableTracing()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LabcoatTracing.EndpointVariable)))
            Environment.SetEnvironmentVariable(LabcoatTracing.EndpointVariable, "http://127.0.0.1:4658");
        Environment.SetEnvironmentVariable(LabcoatTracing.ProtocolVariable, "http/protobuf");
    }
}
