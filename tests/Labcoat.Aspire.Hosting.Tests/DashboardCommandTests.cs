using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>The dashboard commands, executed against a fake Labcoat REST API.</summary>
[TestClass]
public class DashboardCommandTests
{
    private static (LabcoatResource Resource, FakeLabcoat Fake) Labcoat()
    {
        var fake = new FakeLabcoat();
        var rb = Hosting.Builder().AddLabcoat("tests").WithFake(fake);
        return (rb.Resource, fake);
    }

    private static string Finished(int total, int passed, int failed, int skipped = 0, int notRun = 0) =>
        $$$"""{"runId":"r1","state":"finished","run":{"total":{{{total}}},"passed":{{{passed}}},"failed":{{{failed}}},"skipped":{{{skipped}}}{{{(notRun > 0 ? $",\"notRun\":{notRun}" : "")}}}}}""";

    [TestMethod]
    public async Task Run_all_waits_for_its_run_and_reports_the_counts()
    {
        var (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"accepted":true,"runId":"r1","queued":false,"merged":false}""", HttpStatusCode.Accepted)
            .On("GET /api/runs/r1/status", """{"runId":"r1","state":"building"}""")
            .On("GET /api/runs/r1/status", """{"runId":"r1","state":"running","run":{"total":3,"passed":1}}""")
            .On("GET /api/runs/r1/status", Finished(3, 2, 0, skipped: 1));

        var result = await Hosting.ExecuteAsync(resource, "run-all-tests");

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("3 tests: 2 passed, 0 failed, 1 skipped.", result.Message);
        Assert.AreEqual("{}", fake.BodyOf("POST /api/run")!.ToJsonString()); // run all: no test ids
        Assert.HasCount(3, fake.Requests.Where(r => r.PathAndQuery == "/api/runs/r1/status"));
        Assert.IsTrue(fake.Requests.All(r => r.Token == resource.ApiToken), "every request carries the token");
    }

    [TestMethod]
    public async Task A_finished_run_with_failures_is_a_failure_and_a_merged_run_says_so()
    {
        var (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"runId":"r1"}""").On("GET /api/runs/r1/status", Finished(1, 0, 1, notRun: 2));
        var failed = await Hosting.ExecuteAsync(resource, "run-all-tests");
        Assert.IsFalse(failed.Success);
        Assert.AreEqual("1 test: 0 passed, 1 failed, 0 skipped, 2 not run.", failed.Message);

        (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"runId":"r1","merged":true}""").On("GET /api/runs/r1/status", Finished(2, 2, 0));
        var merged = await Hosting.ExecuteAsync(resource, "run-all-tests");
        Assert.AreEqual("2 tests: 2 passed, 0 failed, 0 skipped. (merged with a queued run)", merged.Message);
    }

    [TestMethod]
    [DataRow("""{"state":"aborted","reason":"Test host exited with code 3.","run":{"total":2,"passed":1,"failed":1,"skipped":0}}""",
        "Run aborted: Test host exited with code 3. 2 tests: 1 passed, 1 failed, 0 skipped.")]
    [DataRow("""{"state":"cancelled"}""", "The run was cancelled.")]
    [DataRow("""{"state":"cancelled","run":{"total":4,"passed":1,"failed":0,"skipped":0,"notRun":3}}""",
        "The run was cancelled. 4 tests: 1 passed, 0 failed, 0 skipped, 3 not run.")]
    [DataRow("""{"state":"skipped","reason":"The build or discovery failed."}""", "Nothing ran: The build or discovery failed.")]
    [DataRow("""{"state":"skipped"}""", "Nothing ran: see Labcoat for details.")]
    public async Task Runs_that_did_not_finish_are_failures_with_the_reason(string status, string expected)
    {
        var (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"runId":"r1"}""").On("GET /api/runs/r1/status", status);
        var result = await Hosting.ExecuteAsync(resource, "run-all-tests");
        Assert.IsFalse(result.Success);
        Assert.AreEqual(expected, result.Message);
    }

    [TestMethod]
    public async Task A_superseded_request_reports_the_run_that_replaced_it()
    {
        var (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"runId":"r1"}""")
            .On("GET /api/runs/r1/status", """{"state":"superseded","supersededBy":"r2"}""")
            .On("GET /api/runs/r2/status", """{"state":"finished","run":{"total":5,"passed":5,"failed":0,"skipped":0}}""");
        var result = await Hosting.ExecuteAsync(resource, "run-all-tests");
        Assert.AreEqual("5 tests: 5 passed, 0 failed, 0 skipped.", result.Message);
    }

    [TestMethod]
    public async Task Unknown_run_status_or_no_run_id_points_to_labcoat()
    {
        var (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"runId":"r1"}"""); // status endpoint answers 404
        var unknown = await Hosting.ExecuteAsync(resource, "run-all-tests");
        Assert.IsTrue(unknown.Success);
        Assert.AreEqual("Test run requested; open Labcoat for details.", unknown.Message);

        (resource, fake) = Labcoat();
        fake.On("POST /api/run", "");
        var noId = await Hosting.ExecuteAsync(resource, "run-all-tests");
        Assert.AreEqual("Test run requested; open Labcoat for details.", noId.Message);

        (resource, fake) = Labcoat();
        fake.On("POST /api/run", "not json");
        Assert.AreEqual("Test run requested; open Labcoat for details.", (await Hosting.ExecuteAsync(resource, "run-all-tests")).Message);
    }

    [TestMethod]
    public async Task Run_failed_sends_the_failed_and_timed_out_tests()
    {
        var (resource, fake) = Labcoat();
        fake.On("GET /api/state?lean=1", """
                {"tests":[{"id":"a","outcome":"passed"},{"id":"b","outcome":"failed"},{"id":"c","outcome":"timedOut"},
                          {"id":"b","outcome":"failed"},{"id":"d","outcome":"skipped"}]}
                """)
            .On("POST /api/run", """{"runId":"r1"}""")
            .On("GET /api/runs/r1/status", Finished(2, 2, 0));

        var result = await Hosting.ExecuteAsync(resource, "run-failed-tests");

        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { "b", "c" }, fake.BodyOf("POST /api/run")!["testIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task Run_failed_without_failed_tests_does_not_start_a_run()
    {
        var (resource, fake) = Labcoat();
        fake.On("GET /api/state?lean=1", """{"tests":[{"id":"a","outcome":"passed"}]}""");
        var result = await Hosting.ExecuteAsync(resource, "run-failed-tests");
        Assert.AreEqual("No failed tests to re-run.", result.Message);
        Assert.IsFalse(fake.Requests.Any(r => r.Method == HttpMethod.Post));
    }

    [TestMethod]
    public async Task Http_errors_become_failures_with_the_servers_message()
    {
        var (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"error":"Unknown project 'x'."}""", HttpStatusCode.NotFound);
        var result = await Hosting.ExecuteAsync(resource, "run-all-tests");
        Assert.IsFalse(result.Success);
        Assert.AreEqual("Labcoat answered 404: Unknown project 'x'.", result.Message);

        (resource, fake) = Labcoat();
        fake.On("POST /api/cancel", "<html>oops</html>", HttpStatusCode.InternalServerError);
        Assert.AreEqual("Labcoat answered 500", (await Hosting.ExecuteAsync(resource, "cancel-run")).Message);

        (resource, fake) = Labcoat();
        fake.On("GET /api/state?lean=1", "", HttpStatusCode.Forbidden);
        var toggle = await Hosting.ExecuteAsync(resource, "toggle-watch");
        Assert.IsFalse(toggle.Success);
        Assert.Contains("403", toggle.Message!);
        Assert.IsFalse((await Hosting.ExecuteAsync(resource, "run-failed-tests")).Success);
    }

    [TestMethod]
    [DataRow("""{"cancelledRunning":false,"clearedQueuedRequests":0}""", "Nothing was running.")]
    [DataRow("""{"cancelledRunning":true,"activeOperation":"run","clearedQueuedRequests":0}""", "Cancelled the current run.")]
    [DataRow("""{"cancelledRunning":true,"clearedQueuedRequests":0}""", "Cancelled the current operation.")]
    [DataRow("""{"cancelledRunning":false,"clearedQueuedRequests":1}""", "Cancelled the queued run (1 request).")]
    [DataRow("""{"cancelledRunning":true,"activeOperation":"discovery","clearedQueuedRequests":3}""", "Cancelled the current discovery and the queued run (3 requests).")]
    public async Task Cancel_describes_what_it_stopped(string response, string expected)
    {
        var (resource, fake) = Labcoat();
        fake.On("POST /api/cancel", response, HttpStatusCode.Accepted);
        var result = await Hosting.ExecuteAsync(resource, "cancel-run");
        Assert.IsTrue(result.Success);
        Assert.AreEqual(expected, result.Message);
    }

    [TestMethod]
    public async Task Restart_host_posts_and_reports_progress_or_the_error()
    {
        var (resource, fake) = Labcoat();
        fake.On("POST /api/restart-host", """{"accepted":true}""", HttpStatusCode.Accepted);
        var ok = await Hosting.ExecuteAsync(resource, "restart-test-host");
        Assert.IsTrue(ok.Success);
        Assert.StartsWith("Restarting the test host(s)", ok.Message);

        (resource, _) = Labcoat(); // 404 from the fake
        Assert.IsFalse((await Hosting.ExecuteAsync(resource, "restart-test-host")).Success);
    }

    [TestMethod]
    [DataRow("toggle-watch", "watch", "/api/watch", true, "Watch mode disabled.")]
    [DataRow("toggle-watch", "watch", "/api/watch", false, "Watch mode enabled.")]
    [DataRow("toggle-coverage", "coverageEnabled", "/api/coverage", false, "Code coverage enabled.")]
    [DataRow("toggle-tracing", "tracingEnabled", "/api/tracing", true, "Test tracing disabled.")]
    public async Task Toggles_flip_the_current_state(string command, string property, string path, bool current, string expected)
    {
        var (resource, fake) = Labcoat();
        fake.On("GET /api/state?lean=1", $$"""{"{{property}}":{{(current ? "true" : "false")}}}""").On("POST " + path, "{}");
        var result = await Hosting.ExecuteAsync(resource, command);
        Assert.AreEqual(expected, result.Message);
        Assert.AreEqual(!current, fake.BodyOf("POST " + path)!["enabled"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Toggle_of_a_missing_property_turns_it_on()
    {
        var (resource, fake) = Labcoat();
        fake.On("GET /api/state?lean=1", "{}").On("POST /api/coverage", "");
        Assert.AreEqual("Code coverage enabled.", (await Hosting.ExecuteAsync(resource, "toggle-coverage")).Message);
    }

    [TestMethod]
    public void Commands_are_enabled_while_running_or_showing_the_suite_result()
    {
        var (resource, _) = Labcoat();
        var services = new ServiceCollection().BuildServiceProvider();
        ResourceCommandState State(string? text) => Hosting.Command(resource, "run-all-tests").UpdateState(new UpdateCommandStateContext
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                ResourceType = "Labcoat",
                Properties = [],
                State = text is null ? null : new ResourceStateSnapshot(text, null),
            },
            ServiceProvider = services,
        });

        Assert.AreEqual(ResourceCommandState.Enabled, State(KnownResourceStates.Running));
        Assert.AreEqual(ResourceCommandState.Disabled, State(KnownResourceStates.Starting));
        Assert.AreEqual(ResourceCommandState.Disabled, State(null));
        Assert.AreEqual(ResourceCommandState.Disabled, State("✓ 3 passed"));
        resource.SuiteStateText = "✓ 3 passed";
        Assert.AreEqual(ResourceCommandState.Enabled, State("✓ 3 passed"));
    }
    [TestMethod]
    public async Task Run_status_without_a_state_keeps_waiting_and_odd_answers_are_still_reported()
    {
        // no state yet (e.g. an older Labcoat): keep polling until the run finishes
        var (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"runId":"r1"}""").On("GET /api/runs/r1/status", "{}").On("GET /api/runs/r1/status", "null")
            .On("GET /api/runs/r1/status", Finished(1, 1, 0));
        Assert.AreEqual("1 test: 1 passed, 0 failed, 0 skipped.", (await Hosting.ExecuteAsync(resource, "run-all-tests")).Message);
        Assert.HasCount(3, fake.Requests.Where(r => r.PathAndQuery == "/api/runs/r1/status"));

        // aborted without a reason or counts
        (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"runId":"r1"}""").On("GET /api/runs/r1/status", """{"state":"aborted"}""");
        var aborted = await Hosting.ExecuteAsync(resource, "run-all-tests");
        Assert.IsFalse(aborted.Success);
        Assert.AreEqual("Run aborted:  0 tests: 0 passed, 0 failed, 0 skipped.", aborted.Message);

        // superseded without saying by which run: keep following the own run id
        (resource, fake) = Labcoat();
        fake.On("POST /api/run", """{"runId":"r1"}""").On("GET /api/runs/r1/status", """{"state":"superseded"}""")
            .On("GET /api/runs/r1/status", Finished(2, 2, 0));
        Assert.AreEqual("2 tests: 2 passed, 0 failed, 0 skipped.", (await Hosting.ExecuteAsync(resource, "run-all-tests")).Message);
    }

    [TestMethod]
    public async Task Run_failed_tolerates_a_state_without_tests()
    {
        var (resource, fake) = Labcoat();
        fake.On("GET /api/state?lean=1", "null");
        Assert.AreEqual("No failed tests to re-run.", (await Hosting.ExecuteAsync(resource, "run-failed-tests")).Message);

        (resource, fake) = Labcoat();
        fake.On("GET /api/state?lean=1", """{"tests":[null,{"id":"a"},{"id":"b","outcome":null},{"id":"c","outcome":"failed"}]}""")
            .On("POST /api/run", """{"runId":"r1"}""").On("GET /api/runs/r1/status", Finished(1, 1, 0));
        Assert.IsTrue((await Hosting.ExecuteAsync(resource, "run-failed-tests")).Success);
        CollectionAssert.AreEqual(new[] { "c" }, fake.BodyOf("POST /api/run")!["testIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task Empty_or_null_answers()
    {
        // cancel answered without a body (an older Labcoat): nothing to report
        var (resource, fake) = Labcoat();
        fake.On("POST /api/cancel", "", HttpStatusCode.Accepted);
        Assert.AreEqual("Nothing was running.", (await Hosting.ExecuteAsync(resource, "cancel-run")).Message);

        // a null state: the toggle turns the setting on
        (resource, fake) = Labcoat();
        fake.On("GET /api/state?lean=1", "null").On("POST /api/tracing", "{}");
        Assert.AreEqual("Test tracing enabled.", (await Hosting.ExecuteAsync(resource, "toggle-tracing")).Message);

        // an error answer whose body is JSON without an error message
        (resource, fake) = Labcoat();
        fake.On("POST /api/restart-host", "null", HttpStatusCode.Conflict);
        Assert.AreEqual("Labcoat answered 409", (await Hosting.ExecuteAsync(resource, "restart-test-host")).Message);
        (resource, fake) = Labcoat();
        fake.On("POST /api/restart-host", """{"reason":"busy"}""", HttpStatusCode.Conflict);
        Assert.AreEqual("Labcoat answered 409", (await Hosting.ExecuteAsync(resource, "restart-test-host")).Message);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" * ")]
    [DataRow("A;B")]
    [DataRow("A,B")]
    [DataRow("A=1")]
    public void Pass_through_names_must_be_variable_names_or_prefix_patterns(string? name)
    {
        var rb = Hosting.Builder().AddLabcoat("tests");
        var ex = Assert.ThrowsExactly<ArgumentException>(() => rb.WithPassThroughEnvironment(name!));
        Assert.AreEqual("names", ex.ParamName);
        Assert.IsEmpty(rb.Resource.PassThroughEnvironment);
        rb.WithPassThroughEnvironment(" OTEL_* ", "MY_VAR");
        CollectionAssert.AreEquivalent(new[] { "OTEL_*", "MY_VAR" }, rb.Resource.PassThroughEnvironment.ToArray());
        Assert.ThrowsExactly<ArgumentNullException>(() => rb.WithPassThroughEnvironment(null!));
    }
}
