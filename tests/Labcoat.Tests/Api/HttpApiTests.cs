using System.Net;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Api;

/// <summary>Every REST endpoint of the host (docs/contract.md), happy and error paths, on the real app in-process.</summary>
[TestClass]
public class HttpApiTests
{
    public TestContext TestContext { get; set; }

    private static async Task AssertStatus(HttpStatusCode expected, Task<HttpResponseMessage> request, string? errorContains = null)
    {
        using var res = await request;
        var body = await res.Content.ReadAsStringAsync();
        Assert.AreEqual(expected, res.StatusCode, body);
        if (errorContains is not null) Assert.Contains(errorContains, JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ read-only endpoints

    [TestMethod]
    public async Task Health_is_cheap_liveness()
    {
        await using var h = await ApiHost.StartAsync();
        var health = await h.GetJson("/api/health");
        Assert.AreEqual("ok", health["status"]!.GetValue<string>());
        Assert.AreEqual(Cli.Version, health["version"]!.GetValue<string>());
        Assert.IsFalse(health["running"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task State_is_the_full_snapshot_and_lean_drops_output_and_history()
    {
        await using var h = await ApiHost.StartAsync();
        var t = h.Test("t1", Outcome.Passed, stdOut: "hello");
        t.History = [new HistoryEntry(Outcome.Passed, 1, "2026-01-01T00:00:00Z", "r1")];
        h.Seed(t);
        h.Explorer.Log(null, "info", "a log line");

        var state = await h.GetJson("/api/state");
        Assert.AreEqual("api-tests", state["workspaceName"]!.GetValue<string>());
        Assert.AreEqual(ApiHost.ProjectId, state["projects"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual("passed", state["tests"]![0]!["outcome"]!.GetValue<string>());
        Assert.AreEqual("hello", state["tests"]![0]!["stdOut"]!.GetValue<string>());
        Assert.IsNotNull(state["tests"]![0]!["history"]);
        Assert.IsTrue(state["logs"]!.AsArray().Any(l => l!["message"]!.GetValue<string>() == "a log line"));
        Assert.IsNull(state["lean"]);
        Assert.IsNull(state["uiDefaults"]);

        foreach (var lean in new[] { "1", "true" })
        {
            var leanState = await h.GetJson("/api/state?lean=" + lean);
            Assert.IsTrue(leanState["lean"]!.GetValue<bool>());
            var test = leanState["tests"]![0]!;
            Assert.IsNull(test["stdOut"]);
            Assert.IsNull(test["history"]);
            Assert.IsTrue(test["hasOutput"]!.GetValue<bool>());
        }
    }

    [TestMethod]
    public async Task Summary_counts_tests_by_outcome_per_project()
    {
        await using var h = await ApiHost.StartAsync();
        h.Seed(h.Test("a", Outcome.Passed), h.Test("b", Outcome.Failed), h.Test("c", Outcome.TimedOut), h.Test("d", Outcome.Skipped), h.Test("e"));
        var s = await h.GetJson("/api/summary");
        Assert.AreEqual(Cli.Version, s["version"]!.GetValue<string>());
        Assert.AreEqual("api-tests", s["workspaceName"]!.GetValue<string>());
        var tests = s["tests"]!;
        Assert.AreEqual((5, 1, 2, 1, 1), (tests["total"]!.GetValue<int>(), tests["passed"]!.GetValue<int>(), tests["failed"]!.GetValue<int>(),
            tests["skipped"]!.GetValue<int>(), tests["notRun"]!.GetValue<int>()));
        Assert.AreEqual(5, s["projects"]![0]!["tests"]!.GetValue<int>());
        Assert.AreEqual("idle", s["projects"]![0]!["status"]!.GetValue<string>());
        Assert.IsFalse(s["watch"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Test_details_history_and_output()
    {
        await using var h = await ApiHost.StartAsync();
        var t = h.Test("t1", Outcome.Failed, stdOut: "out");
        t.StdErr = "err";
        t.History = [new HistoryEntry(Outcome.Failed, 2, "2026-01-01T00:00:00Z", "r1")];
        h.Seed(t, h.Test("t2"));

        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/tests/details", TestContext.CancellationToken), "id is required");
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/tests/details?id=nope", TestContext.CancellationToken), "Unknown test");
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/tests/details?projectId=Other&id=t1", TestContext.CancellationToken));
        var details = await h.GetJson($"/api/tests/details?projectId={ApiHost.ProjectId}&id=t1");
        Assert.AreEqual("Test_t1", details["displayName"]!.GetValue<string>());
        Assert.AreEqual("out", details["stdOut"]!.GetValue<string>());

        // history: every test that has history, one test, unknown project / test
        var all = await h.GetJson("/api/tests/history");
        Assert.HasCount(1, all["tests"]!.AsArray());
        Assert.AreEqual("t1", all["tests"]![0]!["id"]!.GetValue<string>());
        var one = await h.GetJson($"/api/tests/history?projectId={ApiHost.ProjectId}&id=t2");
        Assert.IsEmpty(one["tests"]![0]!["history"]!.AsArray()); // known test without history: empty list, not 404
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/tests/history?projectId=Other", TestContext.CancellationToken), "Unknown project 'Other'");
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/tests/history?id=nope", TestContext.CancellationToken), "Unknown test");

        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/tests/output?id=", TestContext.CancellationToken), "id is required");
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/tests/output?id=nope", TestContext.CancellationToken));
        var output = await h.GetJson("/api/tests/output?id=t1");
        Assert.AreEqual(("out", "err", false), (output["stdOut"]!.GetValue<string>(), output["stdErr"]!.GetValue<string>(), output["truncated"]!.GetValue<bool>()));
    }

    [TestMethod]
    public async Task Slowest_tests_come_with_their_duration_trend()
    {
        await using var h = await ApiHost.StartAsync();
        var slow = h.Test("slow", Outcome.Passed);
        slow.DurationMs = 900;
        slow.LastRunAt = "2026-01-03T00:00:00Z";
        slow.History = [new HistoryEntry(Outcome.Passed, 100, "2026-01-01T00:00:00Z"), new HistoryEntry(Outcome.Passed, 300, "2026-01-02T00:00:00Z"),
            new HistoryEntry(Outcome.Passed, 900, "2026-01-03T00:00:00Z")];
        var fast = h.Test("fast", Outcome.Passed);
        fast.DurationMs = 5;
        h.Seed(slow, fast, h.Test("unrun"));

        var all = await h.GetJson($"/api/tests/slowest?projectId={ApiHost.ProjectId}");
        Assert.AreEqual(2, all["total"]!.GetValue<int>());
        Assert.AreEqual("slow", all["tests"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual(200, all["tests"]![0]!["medianMs"]!.GetValue<double>());
        Assert.AreEqual(3, all["tests"]![0]!["trend"]!.AsArray().Count);
        Assert.AreEqual("fast", (await h.GetJson("/api/tests/slowest?limit=2"))["tests"]![1]!["id"]!.GetValue<string>());
        Assert.HasCount(1, (await h.GetJson("/api/tests/slowest?limit=1"))["tests"]!.AsArray());
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/tests/slowest?projectId=Other", TestContext.CancellationToken), "Unknown project 'Other'");
        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/tests/slowest?limit=0", TestContext.CancellationToken), "limit");
    }

    [TestMethod]
    public async Task Persisted_runs_are_listed_and_fetched()
    {
        await using (var empty = await ApiHost.StartAsync())
            Assert.IsEmpty((await empty.GetJson("/api/runs")).AsArray());

        // A previous session of the same workspace recorded a run (HistoryStore writes it on dispose).
        await using var h = await ApiHost.StartAsync(beforeStart: project =>
        {
            using var store = new HistoryStore([project]);
            var run = new RunSummary { RunId = "run-1", StartedAt = "2026-01-01T00:00:00Z", Total = 1, Failed = 1 };
            store.Record(run, [new TestNode { Id = "t1", ProjectId = ApiHost.ProjectId, DisplayName = "T1", Outcome = Outcome.Failed, ErrorMessage = "boom" }]);
        });
        var runs = (await h.GetJson("/api/runs")).AsArray();
        Assert.HasCount(1, runs);
        Assert.AreEqual("run-1", runs[0]!["runId"]!.GetValue<string>());
        var details = await h.GetJson("/api/runs/run-1");
        Assert.AreEqual("failed", details["results"]![0]!["outcome"]!.GetValue<string>());
        Assert.AreEqual("boom", details["results"]![0]!["errorMessage"]!.GetValue<string>());
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/runs/nope", TestContext.CancellationToken));
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/runs/nope/status", TestContext.CancellationToken), "Unknown run id");
    }

    // ------------------------------------------------------------------ run / debug / cancel

    [TestMethod]
    public async Task Run_requests_are_validated()
    {
        await using var h = await ApiHost.StartAsync();
        h.Seed(h.Test("t1"));
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/run", "{"), "Malformed JSON");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/run", """{"testIds":"t1"}"""), "Malformed JSON"); // wrong type

        using (var res = await h.PostJson("/api/run", """{"projectId":"Nope"}"""))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, res.StatusCode);
            var body = (await ApiHost.Json(res))!;
            Assert.Contains("Unknown project 'Nope'", body["error"]!.GetValue<string>());
            Assert.AreEqual(ApiHost.ProjectId, body["projects"]![0]!.GetValue<string>());
        }
        using (var res = await h.PostJson("/api/run", """{"testIds":["x","y"]}"""))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, res.StatusCode);
            CollectionAssert.AreEqual(new[] { "x", "y" }, (await ApiHost.Json(res))!["unknownTestIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        }

        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/debug", "{}"), "testIds are required");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/debug", """{"testIds":[]}"""), "testIds are required");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/debug", "["), "Malformed JSON");
        await AssertStatus(HttpStatusCode.NotFound, h.PostJson("/api/debug", """{"testIds":["zzz"]}"""));
        Assert.IsNull(h.Explorer.Summary().PendingRun);
    }

    [TestMethod]
    public async Task Run_without_test_projects_is_404()
    {
        await using var h = await ApiHost.StartAsync(withProject: false);
        await AssertStatus(HttpStatusCode.NotFound, h.Client.PostAsync("/api/run", null, TestContext.CancellationToken), "No test projects");
    }

    [TestMethod]
    public async Task Accepted_run_reports_its_status_until_it_is_settled()
    {
        // The project file has no targets: the build fails fast, so the run ends as "skipped" without a test host.
        await using var h = await ApiHost.StartAsync();
        h.Seed(h.Test("t1"), h.Test("t2"));
        using var res = await h.PostJson("/api/run", """{"testIds":["t1","ghost"]}""");
        Assert.AreEqual(HttpStatusCode.Accepted, res.StatusCode);
        var body = (await ApiHost.Json(res))!;
        Assert.IsTrue(body["accepted"]!.GetValue<bool>());
        Assert.IsFalse(body["merged"]!.GetValue<bool>());
        CollectionAssert.AreEqual(new[] { "ghost" }, body["unknownTestIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        var runId = body["runId"]!.GetValue<string>();

        JsonNode status;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            status = await h.GetJson($"/api/runs/{runId}/status");
            if (status["state"]!.GetValue<string>() is "skipped" or "finished" or "cancelled" or "aborted") break;
            await Task.Delay(100, TestContext.CancellationToken);
        } while (DateTime.UtcNow < deadline);
        Assert.AreEqual(runId, status["runId"]!.GetValue<string>());
        Assert.AreEqual("skipped", status["state"]!.GetValue<string>(), status.ToJsonString());
        Assert.Contains("build", status["reason"]!.GetValue<string>());
        // The failed build is visible on the project, and the test kept its previous (not run) result.
        var summary = await h.GetJson("/api/summary");
        Assert.AreEqual("error", summary["projects"]![0]!["status"]!.GetValue<string>());
        Assert.AreEqual("notRun", (await h.GetJson("/api/tests/details?id=t1"))["outcome"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Cancel_reports_what_was_cancelled()
    {
        await using var h = await ApiHost.StartAsync();
        using var res = await h.Client.PostAsync("/api/cancel", null, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Accepted, res.StatusCode);
        var body = (await ApiHost.Json(res))!;
        Assert.IsTrue(body["accepted"]!.GetValue<bool>());
        Assert.IsFalse(body["cancelledRunning"]!.GetValue<bool>());
        Assert.AreEqual(0, body["clearedQueuedRequests"]!.GetValue<int>());
        Assert.IsNull(body["clearedQueuedRunId"]);
    }

    // ------------------------------------------------------------------ toggles

    [TestMethod]
    [DataRow("/api/watch", "watch", "watch")]
    [DataRow("/api/coverage", "coverageEnabled", "coverageEnabled")]
    [DataRow("/api/tracing", "tracingEnabled", "tracingEnabled")]
    public async Task Toggles_need_a_boolean_and_change_the_state(string path, string responseKey, string stateKey)
    {
        await using var h = await ApiHost.StartAsync();
        await AssertStatus(HttpStatusCode.BadRequest, h.Client.PostAsync(path, null, TestContext.CancellationToken), "'enabled' (boolean) is required");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson(path, """{"enabled":"yes"}"""), "Malformed JSON");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson(path, "{}"), "'enabled' (boolean) is required");

        foreach (var enabled in new[] { true, false })
        {
            using var res = await h.PostJson(path, $$"""{"enabled":{{(enabled ? "true" : "false")}}}""");
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.AreEqual(enabled, (await ApiHost.Json(res))![responseKey]!.GetValue<bool>());
            Assert.AreEqual(enabled, (await h.GetJson("/api/state"))[stateKey]!.GetValue<bool>());
        }
    }

    [TestMethod]
    public async Task Quarantine_adds_and_releases_tests()
    {
        await using var h = await ApiHost.StartAsync();
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/quarantine", """{"projectId":"A.Tests"}"""), "projectId and id are required");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/quarantine", """{"id":"t1"}"""), "projectId and id are required");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/quarantine", "{x"), "Malformed JSON");

        using (var res = await h.PostJson("/api/quarantine", """{"projectId":"A.Tests","id":"t1"}"""))
            CollectionAssert.AreEqual(new[] { "A.Tests|t1" }, (await ApiHost.Json(res))!["quarantined"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.HasCount(1, (await h.GetJson("/api/state"))["quarantined"]!.AsArray());
        using (var res = await h.PostJson("/api/quarantine", """{"projectId":"A.Tests","id":"t1","quarantined":false}"""))
            Assert.IsEmpty((await ApiHost.Json(res))!["quarantined"]!.AsArray());
    }

    // ------------------------------------------------------------------ project commands

    [TestMethod]
    [DataRow("/api/discover")]
    [DataRow("/api/restart-host")]
    [DataRow("/api/impact-map")]
    public async Task Project_commands_reject_unknown_projects_and_accept_the_rest(string path)
    {
        // No projects: accepted commands have nothing to build, so nothing runs in the background.
        await using var h = await ApiHost.StartAsync(withProject: false);
        await AssertStatus(HttpStatusCode.NotFound, h.PostJson(path, """{"projectId":"Nope"}"""), "Unknown project 'Nope'");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson(path, "{"), "Malformed JSON");
        await AssertStatus(HttpStatusCode.Accepted, h.Client.PostAsync(path, null, TestContext.CancellationToken));
        await AssertStatus(HttpStatusCode.Accepted, h.PostJson(path, "  "));
    }

    [TestMethod]
    public async Task Target_framework_needs_a_known_evaluated_project()
    {
        await using var h = await ApiHost.StartAsync();
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/target-framework", "{}"), "projectId is required");
        await AssertStatus(HttpStatusCode.BadRequest, h.PostJson("/api/target-framework", "{"), "Malformed JSON");
        await AssertStatus(HttpStatusCode.NotFound, h.PostJson("/api/target-framework", """{"projectId":"Nope","targetFramework":"net9.0"}"""), "Unknown project");
        await AssertStatus(HttpStatusCode.Conflict, h.PostJson("/api/target-framework", """{"projectId":"A.Tests","targetFramework":"net9.0"}"""), "not been evaluated");
    }

    // ------------------------------------------------------------------ coverage queries

    [TestMethod]
    public async Task Cached_test_coverage_is_served_without_running_anything()
    {
        await using var h = await ApiHost.StartAsync();
        h.Seed(h.Test("t1"));
        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage/test", TestContext.CancellationToken), "id is required");
        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage/test?id=", TestContext.CancellationToken), "id is required");
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/coverage/test?id=t1", TestContext.CancellationToken));

        var report = new CoverageReport { RunId = "r1", GeneratedAt = "2026-01-01T00:00:00Z", ProjectIds = [ApiHost.ProjectId], Files = [], TestIds = ["t1"], Scope = "subset" };
        lock (h.Explorer.Sync) h.Explorer.CoverageCacheState.Store(ApiHost.ProjectId, report, "stamp");
        var cached = await h.GetJson("/api/coverage/test?id=t1&id=t1");
        Assert.AreEqual("r1", cached["runId"]!.GetValue<string>());
        Assert.IsTrue(cached["stale"]!.GetValue<bool>()); // the project has no build output any more
    }

    [TestMethod]
    public async Task Coverage_report_is_the_full_one_and_scope_last_is_the_latest_run()
    {
        await using var h = await ApiHost.StartAsync();
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/coverage", TestContext.CancellationToken));
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync("/api/coverage?scope=last", TestContext.CancellationToken));
        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage?scope=nope", TestContext.CancellationToken), "scope must be full or last");

        // a full run: the page's report; then a subset run: its own report is scope=last, the full one stays
        var outside = Directory.CreateDirectory(Path.Combine(h.Dir.FullName, "elsewhere")).FullName;
        var covered = Path.Combine(outside, "Covered.cs");
        File.WriteAllText(covered, "a\nb");
        var xml = Path.Combine(outside, "full.xml");
        File.WriteAllText(xml, $"<coverage><sources /><packages><package name=\"Shop\"><classes><class name=\"C\" filename=\"{covered}\"><lines>" +
            "<line number=\"1\" hits=\"1\" branch=\"False\" /><line number=\"2\" hits=\"0\" branch=\"False\" /></lines></class></classes></package></packages></coverage>");
        var acc = new CoverageAccumulator();
        acc.AddCobertura(xml, new HashSet<string>());
        h.Explorer.FullCoverageState.SetFull(ApiHost.ProjectId, acc, "full-run", null);
        h.Explorer.SetCoverageReport(new CoverageReport { RunId = "subset-run", GeneratedAt = "now", ProjectIds = [ApiHost.ProjectId], Files = [], Scope = "subset", TestIds = ["t1"] });

        var full = await h.GetJson("/api/coverage");
        Assert.AreEqual(("full-run", "all", 1, 2), (full["runId"]!.GetValue<string>(), full["scope"]!.GetValue<string>(), full["linesCovered"]!.GetValue<int>(), full["linesValid"]!.GetValue<int>()));
        Assert.AreEqual("full-run", (await h.GetJson("/api/coverage?scope=full"))["runId"]!.GetValue<string>());
        Assert.AreEqual("subset-run", (await h.GetJson("/api/coverage?scope=last"))["runId"]!.GetValue<string>());
        var state = await h.GetJson("/api/state");
        Assert.AreEqual(("subset-run", "full-run"), (state["coverage"]!["runId"]!.GetValue<string>(), state["fullCoverage"]!["runId"]!.GetValue<string>()));
        // the full report's files are served by /api/source
        Assert.AreEqual("a\nb", await h.Client.GetStringAsync("/api/source?path=" + Uri.EscapeDataString(covered), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task The_full_coverage_report_of_a_previous_session_is_restored()
    {
        string? covered = null;
        await using var h = await ApiHost.StartAsync(beforeStart: project =>
        {
            // the previous session: a full coverage run, saved next to the history (coverage.json)
            var outside = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(project)!, "..", "prev")).FullName;
            covered = Path.Combine(outside, "Covered.cs");
            File.WriteAllText(covered, "a\nb");
            var xml = Path.Combine(outside, "full.xml");
            File.WriteAllText(xml, $"<coverage><sources /><packages><package name=\"Shop\"><classes><class name=\"C\" filename=\"{covered}\"><lines>" +
                "<line number=\"1\" hits=\"1\" branch=\"False\" /><line number=\"2\" hits=\"0\" branch=\"False\" /></lines></class></classes></package></packages></coverage>");
            var acc = new CoverageAccumulator();
            acc.AddCobertura(xml, new HashSet<string>());
            using var history = new HistoryStore([project]);
            using var store = new FullCoverageStore(Path.GetDirectoryName(history.FilePath)!);
            new FullCoverage(store).SetFull(ApiHost.ProjectId, acc, "full-run", null);
        });
        var full = await h.GetJson("/api/coverage");
        Assert.AreEqual(("full-run", 1, 2), (full["runId"]!.GetValue<string>(), full["linesCovered"]!.GetValue<int>(), full["linesValid"]!.GetValue<int>()));
        Assert.AreEqual(full["generatedAt"]!.GetValue<string>(), full["restoredAt"]!.GetValue<string>());
        var state = await h.GetJson("/api/state");
        Assert.AreEqual("full-run", state["fullCoverage"]!["runId"]!.GetValue<string>());
        Assert.IsNull(state["coverage"], "the latest run's own report is not persisted");
        Assert.AreEqual("a\nb", await h.Client.GetStringAsync("/api/source?path=" + Uri.EscapeDataString(covered!), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Covering_tests_of_a_line()
    {
        await using var h = await ApiHost.StartAsync();
        h.Seed(h.Test("t1"), h.Test("t2"));
        var file = Path.Combine(h.Workspace, "Cart.cs");
        File.WriteAllLines(file, Enumerable.Range(1, 30).Select(i => $"// line {i}"));
        lock (h.Explorer.Sync)
            h.Explorer.ImpactMapState.Record(ApiHost.ProjectId, ["t2"], new Dictionary<string, List<LineSpan>> { [PathUtil.Canonicalize(file)] = [new LineSpan(5, 9)] });

        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage/line?path=" + Uri.EscapeDataString(file), TestContext.CancellationToken), "path and line");
        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage/line?line=3&path=%20", TestContext.CancellationToken), "path and line");
        await AssertStatus(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage/line?line=0&path=" + Uri.EscapeDataString(file), TestContext.CancellationToken), "path and line");

        var hit = await h.GetJson($"/api/coverage/line?line=7&path={Uri.EscapeDataString(file)}");
        Assert.HasCount(1, hit["tests"]!.AsArray());
        Assert.AreEqual("t2", hit["tests"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual("impactMap", hit["tests"]![0]!["source"]!.GetValue<string>());
        Assert.IsTrue(hit["partial"]!.GetValue<bool>());
        Assert.IsEmpty((await h.GetJson($"/api/coverage/line?line=20&path={Uri.EscapeDataString(file)}"))["tests"]!.AsArray());
    }

    // ------------------------------------------------------------------ source

    [TestMethod]
    public async Task Source_serves_text_files_under_the_roots_only()
    {
        await using var h = await ApiHost.StartAsync();
        string Url(string p) => "/api/source?path=" + Uri.EscapeDataString(p);
        var ok = Path.Combine(h.Workspace, "Cart.cs");
        File.WriteAllText(ok, "﻿class Cart {}", new System.Text.UTF8Encoding(false));
        using (var res = await h.Client.GetAsync(Url(ok), TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.AreEqual("text/plain", res.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("class Cart {}", await res.Content.ReadAsStringAsync(TestContext.CancellationToken)); // BOM stripped
        }

        await AssertStatus(HttpStatusCode.Forbidden, h.Client.GetAsync("/api/source", TestContext.CancellationToken));
        await AssertStatus(HttpStatusCode.Forbidden, h.Client.GetAsync(Url(Path.Combine(h.Dir.FullName, "wwwroot", "index.html")), TestContext.CancellationToken)); // outside the workspace
        await AssertStatus(HttpStatusCode.Forbidden, h.Client.GetAsync(Url(Path.Combine(h.Workspace, "..", "wwwroot", "index.html")), TestContext.CancellationToken));
        await AssertStatus(HttpStatusCode.NotFound, h.Client.GetAsync(Url(Path.Combine(h.Workspace, "Missing.cs")), TestContext.CancellationToken));

        var big = Path.Combine(h.Workspace, "Big.cs");
        File.WriteAllText(big, new string('x', 2 * 1024 * 1024 + 1));
        await AssertStatus(HttpStatusCode.RequestEntityTooLarge, h.Client.GetAsync(Url(big), TestContext.CancellationToken));

        var binary = Path.Combine(h.Workspace, "Blob.cs");
        File.WriteAllBytes(binary, [0x41, 0x00, 0x42]);
        await AssertStatus(HttpStatusCode.UnsupportedMediaType, h.Client.GetAsync(Url(binary), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Source_also_serves_files_named_by_tests_and_the_coverage_report()
    {
        await using var h = await ApiHost.StartAsync();
        var outside = Directory.CreateDirectory(Path.Combine(h.Dir.FullName, "elsewhere")).FullName;
        var testFile = Path.Combine(outside, "Located.cs");
        var coveredFile = Path.Combine(outside, "Covered.cs");
        File.WriteAllText(testFile, "test");
        File.WriteAllText(coveredFile, "covered");
        string Url(string p) => "/api/source?path=" + Uri.EscapeDataString(p);
        await AssertStatus(HttpStatusCode.Forbidden, h.Client.GetAsync(Url(testFile), TestContext.CancellationToken));
        await AssertStatus(HttpStatusCode.Forbidden, h.Client.GetAsync(Url(coveredFile), TestContext.CancellationToken));

        h.Seed(h.Test("t1") with { FilePath = testFile });
        h.Explorer.SetCoverageReport(new CoverageReport
        {
            RunId = "r", GeneratedAt = "now", ProjectIds = [ApiHost.ProjectId],
            Files = [new FileCoverage { Path = coveredFile, Module = "Shop" }],
        });
        Assert.AreEqual("test", await h.Client.GetStringAsync(Url(testFile), TestContext.CancellationToken));
        Assert.AreEqual("covered", await h.Client.GetStringAsync(Url(coveredFile), TestContext.CancellationToken));
    }

    // ------------------------------------------------------------------ fallback

    [TestMethod]
    public async Task Unknown_api_routes_are_404_and_other_routes_get_the_spa()
    {
        await using var h = await ApiHost.StartAsync();
        using (var res = await h.Client.GetAsync("/api/nope", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, res.StatusCode);
            Assert.AreEqual("", await res.Content.ReadAsStringAsync(TestContext.CancellationToken));
        }
        using (var res = await h.Client.PostAsync("/api/nope", null, TestContext.CancellationToken)) Assert.AreEqual(HttpStatusCode.NotFound, res.StatusCode);
        using (var res = await h.Client.GetAsync("/runs/42", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.AreEqual("no-cache", res.Headers.CacheControl!.ToString());
            Assert.Contains(ApiHost.InlineScript, await res.Content.ReadAsStringAsync(TestContext.CancellationToken));
        }
    }
}
