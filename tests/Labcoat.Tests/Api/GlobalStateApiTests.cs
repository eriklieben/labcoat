using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Api;

/// <summary>Endpoints that depend on process-wide state (the dashboard client, the detected editors): not parallel.</summary>
[TestClass]
[DoNotParallelize]
public class GlobalStateApiTests
{
    public TestContext TestContext { get; set; }

    private sealed class FakeDashboard(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string TraceId = "0123456789abcdef0123456789abcdef";

    private const string Spans = """
        {"data":{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"api"}}]},
         "scopeSpans":[{"spans":[
           {"spanId":"aa","name":"POST /orders","startTimeUnixNano":"1791497799005619900","endTimeUnixNano":"1791497799010619900",
            "attributes":[{"key":"http.response.status_code","value":{"intValue":"500"}}]},
           {"spanId":"bb","name":"GET /health","startTimeUnixNano":"1791497799005619900","endTimeUnixNano":"1791497799006619900",
            "attributes":[{"key":"http.response.status_code","value":{"intValue":"200"}}]}]}]}]}}
        """;

    private const string Logs = """
        {"data":{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"api"}}]},
         "scopeLogs":[{"scope":{"name":"Orders"},"logRecords":[{"timeUnixNano":"1791497758243716900","severityNumber":17,"body":{"stringValue":"Order failed"}}]}]}]}}
        """;

    [TestMethod]
    public async Task Test_telemetry_needs_tracing_and_a_dashboard_and_proxies_its_error_spans_and_logs()
    {
        await using var h = await ApiHost.StartAsync();
        h.Seed(h.Test("traced", Outcome.Failed, traceId: TraceId), h.Test("untraced", Outcome.Failed));
        async Task<(HttpStatusCode, JsonNode?)> Get(string query)
        {
            using var res = await h.Client.GetAsync("/api/tests/telemetry?" + query, TestContext.CancellationToken);
            return (res.StatusCode, await ApiHost.Json(res));
        }

        var (status, body) = await Get("id=traced");
        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.Contains("Trace collection is off", body!["error"]!.GetValue<string>());

        await h.PostJson("/api/tracing", """{"enabled":true}""");
        var dashboard = new FakeDashboard(r => Ok(r.RequestUri!.AbsolutePath.EndsWith("/spans") ? Spans : Logs));
        using (DashboardTelemetry.Override(null, null, dashboard))
        {
            (status, body) = await Get("id=traced");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.Contains("No Aspire dashboard is known", body!["error"]!.GetValue<string>());
        }

        using (DashboardTelemetry.Override("http://dashboard.local:18888", "api-key", dashboard))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Get("projectId=A.Tests")).Item1);
            Assert.AreEqual(HttpStatusCode.NotFound, (await Get("id=ghost")).Item1);
            (status, body) = await Get("id=untraced");
            Assert.AreEqual(HttpStatusCode.NotFound, status);
            Assert.Contains("no trace id", body!["error"]!.GetValue<string>());
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Get("traceId=xyz-not-hex")).Item1);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Get("traceId=abc")).Item1); // too short
            Assert.IsEmpty(dashboard.Requests);

            (status, body) = await Get("projectId=A.Tests&id=traced");
            Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
            Assert.AreEqual(TraceId, body!["traceId"]!.GetValue<string>());
            Assert.HasCount(1, body["spans"]!.AsArray()); // only the HTTP 500 span
            Assert.AreEqual(500, body["spans"]![0]!["httpStatusCode"]!.GetValue<int>());
            Assert.AreEqual("error", body["logs"]![0]!["level"]!.GetValue<string>());
            Assert.IsTrue(dashboard.Requests.All(r => r.Headers.GetValues("x-api-key").Single() == "api-key"));
            Assert.Contains("traceId=" + TraceId, dashboard.Requests[0].RequestUri!.Query);

            // An explicit trace id is normalized to lower case.
            dashboard.Requests.Clear();
            Assert.AreEqual(HttpStatusCode.OK, (await Get("traceId=" + TraceId.ToUpperInvariant())).Item1);
            Assert.Contains("traceId=" + TraceId, dashboard.Requests[0].RequestUri!.Query);
        }

        using (DashboardTelemetry.Override("http://dashboard.local:18888", null, new FakeDashboard(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))))
        {
            (status, body) = await Get("id=traced");
            Assert.AreEqual(HttpStatusCode.BadGateway, status);
            Assert.Contains("401", body!["error"]!.GetValue<string>());
        }
    }

    private static string FakeEditor(string dir, string argsFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(dir, "fake-editor.cmd");
            File.WriteAllText(cmd, $"@echo %* > \"{argsFile}\"\r\n");
            return cmd;
        }
        var script = Path.Combine(dir, "fake-editor");
        File.WriteAllText(script, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{argsFile}'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [TestMethod]
    public async Task Open_in_editor_checks_the_editor_and_the_path_then_launches_it()
    {
        await using var h = await ApiHost.StartAsync();
        var file = Path.Combine(h.Workspace, "Cart.cs");
        File.WriteAllText(file, "class Cart {}");
        var argsFile = Path.Combine(h.Dir.FullName, "editor-args.txt");
        async Task<(HttpStatusCode, JsonNode?)> Open(string json)
        {
            using var res = await h.PostJson("/api/open-in-editor", json);
            return (res.StatusCode, await ApiHost.Json(res));
        }
        string Body(string editor, string path, string extra = "") =>
            $$"""{"editor":"{{editor}}","path":{{JsonValue.Create(path).ToJsonString()}}{{extra}}}""";

        try
        {
            EditorLauncher.SetDetectedPaths(new Dictionary<string, string?> { ["vscode"] = FakeEditor(h.Dir.FullName, argsFile) });

            var (status, body) = await Open("{");
            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.StartsWith("Malformed JSON", body!["error"]!.GetValue<string>());
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Open("null")).Item1);
            (status, body) = await Open(Body("notepad", file));
            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.Contains("editor must be one of: vscode, rider, visualstudio", body!["message"]!.GetValue<string>());
            Assert.AreEqual(HttpStatusCode.Forbidden, (await Open(Body("vscode", Path.Combine(h.Dir.FullName, "wwwroot", "index.html")))).Item1);
            Assert.AreEqual(HttpStatusCode.NotFound, (await Open(Body("vscode", Path.Combine(h.Workspace, "Missing.cs")))).Item1);

            (status, body) = await Open(Body("rider", file));
            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsFalse(body!["ok"]!.GetValue<bool>());
            Assert.AreEqual("JetBrains Rider was not found on this machine", body["message"]!.GetValue<string>());

            (status, body) = await Open(Body("vscode", file, ""","line":12,"column":3"""));
            Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
            Assert.IsTrue(body!["ok"]!.GetValue<bool>(), body.ToJsonString());
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(argsFile) && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.CancellationToken);
            var args = File.ReadAllText(argsFile);
            Assert.Contains("--goto", args);
            Assert.Contains(PathUtil.Canonicalize(file) + ":12:3", args);
        }
        finally
        {
            EditorLauncher.SetDetectedPaths(null);
        }
    }
}
