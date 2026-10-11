using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests;

[TestClass]
public class HelperTests
{
    [TestMethod]
    public void Workspace_name_prefers_explicit_then_solution_then_single_project()
    {
        Assert.AreEqual("tests-failing", WorkspaceName.Resolve("tests-failing", ["/x"], ["/x/A/A.csproj", "/x/B/B.csproj"]));

        var dir = Directory.CreateTempSubdirectory("labcoat-ws-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Shop.slnx"), "<Solution />");
            Assert.AreEqual("Shop", WorkspaceName.Resolve(null, [dir.FullName], [Path.Combine(dir.FullName, "A", "A.csproj")]));
            File.Delete(Path.Combine(dir.FullName, "Shop.slnx"));
            Assert.AreEqual("A", WorkspaceName.Resolve(null, [dir.FullName], [Path.Combine(dir.FullName, "A", "A.csproj")]));
            Assert.AreEqual(dir.Name, WorkspaceName.Resolve(" ", [dir.FullName],
                [Path.Combine(dir.FullName, "A", "A.csproj"), Path.Combine(dir.FullName, "B", "B.csproj")]));
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public void Repeat_entry_with_mixed_results_marks_flaky()
    {
        List<HistoryEntry> runs = [new(Outcome.Passed, 1, "t1"), new(Outcome.Failed, 1, "t2", "r", Flaky: true), new(Outcome.Failed, 1, "t3")];
        Assert.IsTrue(HistoryStore.IsFlaky(runs));
        Assert.IsFalse(HistoryStore.IsFlaky([new(Outcome.Passed, 1, "t1"), new(Outcome.Failed, 1, "t2")])); // one transition
    }

    [TestMethod]
    public void Parses_dashboard_spans_and_logs()
    {
        var spans = JsonNode.Parse("""
            {"data":{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"api"}}]},
             "scopeSpans":[{"scope":{"name":"Microsoft.AspNetCore"},"spans":[
               {"traceId":"0123","spanId":"aa","name":"POST /orders","startTimeUnixNano":"1791497799005619900","endTimeUnixNano":"1791497799010619900",
                "attributes":[{"key":"http.response.status_code","value":{"intValue":"400"}}]},
               {"traceId":"0123","spanId":"bb","name":"boom","startTimeUnixNano":"1791497799005619900","endTimeUnixNano":"1791497799006619900",
                "events":[{"name":"exception","attributes":[{"key":"exception.type","value":{"stringValue":"System.InvalidOperationException"}}]}],
                "status":{"code":2,"message":"kaboom"}}]}]}]},"totalCount":2,"returnedCount":2}
            """);
        var parsed = DashboardTelemetry.ParseSpans(spans);
        Assert.HasCount(2, parsed);
        var post = parsed.Single(s => s.Name == "POST /orders");
        Assert.AreEqual("api", post.Resource);
        Assert.AreEqual("unset", post.Status);
        Assert.AreEqual(400, post.HttpStatusCode);
        Assert.AreEqual(5.0, post.DurationMs);
        var boom = parsed.Single(s => s.Name == "boom");
        Assert.AreEqual("error", boom.Status);
        Assert.AreEqual("kaboom", boom.StatusMessage);
        Assert.AreEqual("System.InvalidOperationException", boom.ExceptionType);

        var logs = DashboardTelemetry.ParseLogs(JsonNode.Parse("""
            {"data":{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"api"}}]},
             "scopeLogs":[{"scope":{"name":"Program"},"logRecords":[
               {"timeUnixNano":"1791497758243716900","severityNumber":13,"severityText":"Warning","body":{"stringValue":"Rejected order"},
                "attributes":[{"key":"aspire.log_id","value":{"stringValue":"5"}},{"key":"Customer","value":{"stringValue":"Contoso"}}],"spanId":"e1"}]}]}]}}
            """));
        Assert.HasCount(1, logs);
        Assert.AreEqual("warning", logs[0].Level);
        Assert.AreEqual("Rejected order", logs[0].Message);
        Assert.AreEqual("Program", logs[0].Category);
        Assert.IsFalse(logs[0].Attributes!.ContainsKey("aspire.log_id"));
        Assert.StartsWith("2026-", logs[0].Timestamp);
    }
}
