using System.Net;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>The suite result shown as the resource state: the text for a summary and the polling loop that publishes it.</summary>
[TestClass]
public class SuiteStateTests
{
    public TestContext TestContext { get; set; }

    private static (string? Text, string? Style) Describe(string json) =>
        LabcoatResourceBuilderExtensions.DescribeSuite(JsonNode.Parse(json));

    private static string Summary(int total, int passed = 0, int failed = 0, int skipped = 0, string projects = """[{"status":"idle"}]""",
        string? currentRun = null, string? lastRun = null) =>
        $$"""{"tests":{"total":{{total}},"passed":{{passed}},"failed":{{failed}},"skipped":{{skipped}}},"projects":{{projects}}""" +
        (currentRun is null ? "" : $",\"currentRun\":{currentRun}") + (lastRun is null ? "" : $",\"lastRun\":{lastRun}") + "}";

    [TestMethod]
    public void Describe_suite_covers_every_state()
    {
        Assert.AreEqual((null, null), LabcoatResourceBuilderExtensions.DescribeSuite(null));
        Assert.AreEqual(("▶ Running 2/5", KnownResourceStateStyles.Info), Describe(Summary(5, currentRun: """{"total":5,"passed":1,"skipped":1}""")));
        Assert.AreEqual(("▶ Running 3/5 · 1 failed", KnownResourceStateStyles.Warn), Describe(Summary(5, currentRun: """{"total":5,"passed":2,"failed":1}""")));
        Assert.AreEqual(("▶ Running 0/0", KnownResourceStateStyles.Info), Describe(Summary(5, currentRun: "{}")));
        // After a cancel, while the test host stops (up to the cancel grace period): not "Running" any more.
        Assert.AreEqual(("◼ Cancelling…", KnownResourceStateStyles.Warn),
            Describe(Summary(5, currentRun: """{"total":5,"passed":1}""")[..^1] + ""","cancelling":true}"""));
        Assert.AreEqual(("◌ Building", KnownResourceStateStyles.Info), Describe(Summary(5, projects: """[{"status":"idle"},{"status":"discovering"}]""")));
        Assert.AreEqual(("◌ Building", KnownResourceStateStyles.Info), Describe(Summary(5, projects: """[{"status":"building"}]""")));
        Assert.AreEqual(("✕ Build / host error", KnownResourceStateStyles.Error), Describe(Summary(5, passed: 5, projects: """[{"status":"error"}]""")));
        Assert.AreEqual(("No test projects", KnownResourceStateStyles.Warn), Describe(Summary(0, projects: "[]")));
        Assert.AreEqual(("No test projects", KnownResourceStateStyles.Warn), Describe("""{"tests":{"total":0}}"""));
        Assert.AreEqual(("No tests", KnownResourceStateStyles.Warn), Describe(Summary(0)));
        Assert.AreEqual(("✕ 2 failed · 7 tests", KnownResourceStateStyles.Error), Describe(Summary(7, passed: 5, failed: 2)));
        Assert.AreEqual(("✕ Run aborted · 7 tests", KnownResourceStateStyles.Error), Describe(Summary(7, passed: 3, lastRun: """{"aborted":true}""")));
        Assert.AreEqual(("○ 7 tests · not run", KnownResourceStateStyles.Info), Describe(Summary(7, lastRun: """{"aborted":false}""")));
        Assert.AreEqual(("✓ 3 passed · 3 not run", KnownResourceStateStyles.Success), Describe(Summary(7, passed: 3, skipped: 1)));
        Assert.AreEqual(("✓ 6 passed · 1 skipped", KnownResourceStateStyles.Success), Describe(Summary(7, passed: 6, skipped: 1)));
        Assert.AreEqual(("✓ 7 passed", KnownResourceStateStyles.Success), Describe(Summary(7, passed: 7)));
        // partial answers: missing counts are 0, projects without a status are idle
        Assert.AreEqual(("✓ 2 passed", KnownResourceStateStyles.Success), Describe("""{"tests":{"total":2,"passed":2},"projects":[null,{}]}"""));
        Assert.AreEqual(("No test projects", KnownResourceStateStyles.Warn), Describe("{}"));
        Assert.AreEqual(("○ 3 tests · not run", KnownResourceStateStyles.Info), Describe("""{"tests":{"total":3},"lastRun":{}}"""));
    }

    /// <summary>A built (not started) app: its ResourceNotificationService and lifetime, with Labcoat talking to a fake.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public IDistributedApplicationBuilder Builder { get; } = Hosting.Builder();
        public FakeLabcoat Fake { get; } = new();
        public LabcoatResource Resource { get; }
        public DistributedApplication App { get; }
        public ResourceNotificationService Notifications { get; }

        public Harness(Action<IResourceBuilder<LabcoatResource>>? configure = null)
        {
            // Aspire's own BeforeStartEvent handlers want DCP's options; never started, so any existing file does.
            Builder.Configuration["DcpPublisher:CliPath"] = typeof(SuiteStateTests).Assembly.Location;
            Builder.Configuration["DcpPublisher:DashboardPath"] = typeof(SuiteStateTests).Assembly.Location;
            var rb = Builder.AddLabcoat("tests").WithFake(Fake);
            configure?.Invoke(rb);
            Resource = rb.Resource;
            App = Builder.Build();
            Notifications = App.Services.GetRequiredService<ResourceNotificationService>();
        }

        public Task SetStateAsync(string text) =>
            Notifications.PublishUpdateAsync(Resource, s => s with { State = new ResourceStateSnapshot(text, null) });

        public string? State => Notifications.TryGetCurrentState(Resource.Name, out var e) ? e.Snapshot.State?.Text : null;

        public async Task WaitForStateAsync(string expected)
        {
            for (var i = 0; i < 500 && State != expected; i++) await Task.Delay(10);
            Assert.AreEqual(expected, State);
        }

        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    [TestMethod]
    public async Task Suite_result_replaces_running_once_ready_and_comes_back_after_dcp_updates()
    {
        await using var h = new Harness();
        h.Fake.On("GET /api/summary", Summary(4, passed: 3, failed: 1));
        await h.SetStateAsync(KnownResourceStates.Running);
        using var cts = new CancellationTokenSource();
        var loop = LabcoatResourceBuilderExtensions.PublishSuiteStateAsync(h.Resource, h.Notifications, cts.Token);

        // Not ready yet (health checks pending): nothing is read or published.
        await Task.Delay(100, TestContext.CancellationToken);
        Assert.AreEqual(KnownResourceStates.Running, h.State);
        Assert.IsEmpty(h.Fake.Requests);

        h.Resource.IsReady = true;
        await h.WaitForStateAsync("✕ 1 failed · 4 tests");
        Assert.AreEqual("✕ 1 failed · 4 tests", h.Resource.SuiteStateText);
        Assert.IsTrue(h.Fake.Requests.All(r => r.Token == h.Resource.ApiToken));

        // DCP sets "Running" again: the next poll restores the result; a new result replaces the previous text.
        await h.SetStateAsync(KnownResourceStates.Running);
        await h.WaitForStateAsync("✕ 1 failed · 4 tests");
        h.Fake.On("GET /api/summary", Summary(4, passed: 4));
        h.Fake.On("GET /api/summary", Summary(4, passed: 4));
        await h.WaitForStateAsync("✓ 4 passed");

        // A terminal state is never overwritten; the resource has to become ready again after a restart.
        await h.SetStateAsync(KnownResourceStates.Exited);
        await Task.Delay(100, TestContext.CancellationToken);
        Assert.AreEqual(KnownResourceStates.Exited, h.State);
        Assert.IsFalse(h.Resource.IsReady);
        Assert.IsNull(h.Resource.SuiteStateText);

        cts.Cancel();
        await loop;
    }

    [TestMethod]
    public async Task Nothing_is_published_when_turned_off_or_when_labcoat_does_not_answer()
    {
        await using var h = new Harness(rb => rb.WithSuiteResultState(false));
        h.Resource.IsReady = true;
        await h.SetStateAsync(KnownResourceStates.Running);
        using var cts = new CancellationTokenSource();
        var loop = LabcoatResourceBuilderExtensions.PublishSuiteStateAsync(h.Resource, h.Notifications, cts.Token);
        await Task.Delay(100, TestContext.CancellationToken);
        Assert.IsEmpty(h.Fake.Requests);

        h.Resource.SuiteResultState = true;
        h.Fake.On("GET /api/summary", "", HttpStatusCode.ServiceUnavailable);
        for (var i = 0; i < 200 && h.Fake.Requests.Count < 2; i++) await Task.Delay(10, TestContext.CancellationToken);
        Assert.IsGreaterThanOrEqualTo(2, h.Fake.Requests.Count); // keeps polling
        Assert.AreEqual(KnownResourceStates.Running, h.State);

        cts.Cancel();
        await loop;
    }

    [TestMethod]
    public async Task Events_mark_the_resource_ready_and_start_the_suite_state_loop()
    {
        await using var h = new Harness();
        h.Fake.On("GET /api/summary", Summary(2, passed: 2));
        await h.Builder.Eventing.PublishAsync(new BeforeStartEvent(h.App.Services, h.App.Services.GetRequiredService<DistributedApplicationModel>()), TestContext.CancellationToken);
        await h.SetStateAsync(KnownResourceStates.Running); // what DCP reports once the process runs

        Assert.IsFalse(h.Resource.IsReady);
        await h.Builder.Eventing.PublishAsync(new ResourceReadyEvent(h.Resource, h.App.Services), TestContext.CancellationToken);
        Assert.IsTrue(h.Resource.IsReady);
        await h.WaitForStateAsync("✓ 2 passed");

        h.App.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication(); // ends the loop
    }
}
