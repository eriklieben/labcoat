using System.Net;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>Trace ids in test output, the test host's OTLP environment and the Aspire dashboard telemetry client.</summary>
[TestClass]
public class TelemetryTests
{
    private const string TraceId = "0123456789ABCDEF0123456789abcdef";

    // ------------------------------------------------------------------ trace id marker

    [TestMethod]
    public void Trace_id_marker_lines_are_stripped_from_the_output()
    {
        Assert.AreEqual("plain", Telemetry.ExtractTraceId("plain", out var none));
        Assert.IsNull(none);
        Assert.IsNull(Telemetry.ExtractTraceId(null, out _));

        var cleaned = Telemetry.ExtractTraceId($"before\nlabcoat:traceId={TraceId}  \r\nafter\nlabcoat:traceId={new string('1', 32)}", out var id);
        Assert.AreEqual(TraceId.ToLowerInvariant(), id); // the first one, lower-cased
        Assert.AreEqual("before\nafter\n", cleaned);

        Assert.IsNull(Telemetry.ExtractTraceId($"labcoat:traceId={TraceId}\n", out id));
        Assert.IsNotNull(id);

        // Not a trace id (too short): left as it is.
        Assert.AreEqual("labcoat:traceId=abc", Telemetry.ExtractTraceId("labcoat:traceId=abc", out id));
        Assert.IsNull(id);
    }

    // ------------------------------------------------------------------ test host environment

    private static Dictionary<string, string?> Env() => new()
    {
        ["PATH"] = "/bin",
        ["LABCOAT_OTLP_ENDPOINT"] = "inherited",
        ["LABCOAT_DASHBOARD_API_KEY"] = "secret",
        ["LABCOAT_NAME"] = "tests",
        ["LABCOAT_ALLOWED_HOSTS"] = "x",
        ["LABCOAT_TOKEN"] = "t",
        ["PORT"] = "4300",
        ["ASPNETCORE_URLS"] = "http://localhost:4300",
        ["ASPNETCORE_HTTP_PORTS"] = "4300",
        ["ASPNETCORE_HTTPS_PORTS"] = "4301",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://labcoat-itself",
        ["LABCOAT_TEST_HOST"] = "1",
    };

    [TestMethod]
    [DoNotParallelize]
    public void Test_hosts_never_inherit_labcoats_own_settings()
    {
        using (Telemetry.Override(hosted: false, endpoint: null))
        {
            var env = Env();
            Telemetry.ConfigureTestHost(env, tracingEnabled: true);
            CollectionAssert.AreEquivalent(new[] { "PATH", "OTEL_EXPORTER_OTLP_ENDPOINT", "LABCOAT_TEST_HOST" }, env.Keys.ToArray());
            Assert.IsFalse(Telemetry.Available);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void Hosted_by_aspire_the_otlp_endpoint_is_passed_only_when_tracing_is_on()
    {
        using (Telemetry.Override(hosted: true, endpoint: "https://localhost:21000", protocol: "grpc", headers: "x-otlp-api-key=k"))
        {
            Assert.IsTrue(Telemetry.Available);
            var env = Env();
            Telemetry.ConfigureTestHost(env, tracingEnabled: true);
            Assert.AreEqual("https://localhost:21000", env["LABCOAT_OTLP_ENDPOINT"]);
            Assert.AreEqual("grpc", env["LABCOAT_OTLP_PROTOCOL"]);
            Assert.AreEqual("x-otlp-api-key=k", env["LABCOAT_OTLP_HEADERS"]);
            Assert.IsFalse(env.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT")); // describes the Labcoat resource, not the tests

            env = Env();
            Telemetry.ConfigureTestHost(env, tracingEnabled: false);
            Assert.IsFalse(env.Keys.Any(k => k.StartsWith("LABCOAT_OTLP_") || k.StartsWith("OTEL_")));
        }
        using (Telemetry.Override(hosted: true, endpoint: "http://localhost:4318"))
        {
            var env = Env();
            Telemetry.ConfigureTestHost(env, tracingEnabled: true);
            Assert.AreEqual("http://localhost:4318", env["LABCOAT_OTLP_ENDPOINT"]);
            Assert.IsFalse(env.ContainsKey("LABCOAT_OTLP_PROTOCOL"));
            Assert.IsFalse(env.ContainsKey("LABCOAT_OTLP_HEADERS"));
        }
        using (Telemetry.Override(hosted: true, endpoint: null))
        {
            var env = Env();
            Telemetry.ConfigureTestHost(env, tracingEnabled: true);
            Assert.IsFalse(env.ContainsKey("LABCOAT_OTLP_ENDPOINT")); // hosted, but no OTLP endpoint known
        }
    }

    // ------------------------------------------------------------------ dashboard telemetry API

    private sealed class FakeDashboard(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private const string Spans = """
        {"data":{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"api"}}]},
         "scopeSpans":[{"spans":[
           {"spanId":"a1","name":"GET /ok","startTimeUnixNano":3000000,"endTimeUnixNano":4000000,"status":{"code":1}},
           {"spanId":"a2","name":"GET /missing","startTimeUnixNano":"2000000","endTimeUnixNano":"2500000",
            "attributes":[{"key":"http.status_code","value":{"intValue":404}}]},
           {"spanId":"a3","name":"boom","startTimeUnixNano":"1000000","status":{"code":2,"message":""},
            "events":[{"name":"log","attributes":[]},{"name":"exception","attributes":[
              {"key":"exception.type","value":{"stringValue":"System.Exception"}},{"key":"exception.message","value":{"stringValue":"bad"}}]}]},
           null]}]}]}}
        """;

    private const string Logs = """
        {"data":{"resourceLogs":[{"resource":{"attributes":[]},"scopeLogs":[{"scope":{"name":""},"logRecords":[
          {"observedTimeUnixNano":"1000","severityNumber":17,"body":{"stringValue":"failed"},"spanId":""},
          {"timeUnixNano":"2000","severityNumber":21,"body":{"intValue":"42"}},
          null]}]}]}}
        """;

    [TestMethod]
    [DoNotParallelize]
    public async Task Failure_context_keeps_error_and_http_error_spans_and_sends_the_api_key()
    {
        var dashboard = new FakeDashboard(req => Json(req.RequestUri!.AbsolutePath.EndsWith("/spans") ? Spans : Logs));
        using (DashboardTelemetry.Override("http://localhost:18888", "key-1", dashboard))
        {
            Assert.IsTrue(DashboardTelemetry.Available);
            var t = await DashboardTelemetry.GetAsync("abc123", CancellationToken.None);

            Assert.AreEqual("abc123", t.TraceId);
            CollectionAssert.AreEqual(new[] { "boom", "GET /missing" }, t.Spans.Select(s => s.Name).ToArray()); // ordered by start time
            var boom = t.Spans[0];
            Assert.AreEqual(("error", "System.Exception", "bad"), (boom.Status, boom.ExceptionType, boom.ExceptionMessage));
            Assert.IsNull(boom.StatusMessage);
            Assert.IsNull(boom.DurationMs); // no end time
            Assert.IsNull(boom.Attributes);
            Assert.AreEqual(404, t.Spans[1].HttpStatusCode);
            Assert.AreEqual(0.5, t.Spans[1].DurationMs);

            CollectionAssert.AreEqual(new[] { "error", "critical" }, t.Logs.Select(l => l.Level).ToArray());
            Assert.AreEqual("42", t.Logs[1].Message);
            Assert.IsNull(t.Logs[0].SpanId);
            Assert.IsNull(t.Logs[0].Category);
            Assert.AreEqual("", t.Logs[0].Resource);

            Assert.HasCount(2, dashboard.Requests);
            Assert.IsTrue(dashboard.Requests.All(r => r.Headers.GetValues("x-api-key").Single() == "key-1"));
            var logsUrl = dashboard.Requests.Single(r => r.RequestUri!.AbsolutePath == "/api/telemetry/logs").RequestUri!.Query;
            Assert.Contains("traceId=abc123", logsUrl);
            Assert.Contains("severity=Warning", logsUrl);
            Assert.Contains("limit=200", logsUrl);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Unknown_traces_are_empty_and_dashboard_errors_are_reported()
    {
        var status = HttpStatusCode.NotFound;
        var dashboard = new FakeDashboard(_ => new HttpResponseMessage(status));
        using (DashboardTelemetry.Override("http://localhost:18888", null, dashboard))
        {
            var t = await DashboardTelemetry.GetAsync("abc", CancellationToken.None);
            Assert.IsEmpty(t.Spans);
            Assert.IsEmpty(t.Logs);
            Assert.IsFalse(dashboard.Requests.Any(r => r.Headers.Contains("x-api-key"))); // unsecured dashboard

            status = HttpStatusCode.Unauthorized;
            var e = await Assert.ThrowsExactlyAsync<DashboardTelemetryException>(() => DashboardTelemetry.GetAsync("abc", CancellationToken.None));
            Assert.Contains("rejected the API key (401)", e.Message);

            status = HttpStatusCode.BadGateway;
            e = await Assert.ThrowsExactlyAsync<DashboardTelemetryException>(() => DashboardTelemetry.GetAsync("abc", CancellationToken.None));
            Assert.AreEqual("The Aspire dashboard answered 502 Bad Gateway.", e.Message);
        }
        using (DashboardTelemetry.Override(null, null, dashboard)) Assert.IsFalse(DashboardTelemetry.Available);
    }

    [TestMethod]
    public void Attribute_values_of_every_otlp_type_become_strings_capped_in_count_and_length()
    {
        var attrs = new JsonArray
        {
            Attr("s", new JsonObject { ["stringValue"] = new string('x', 2500) }),
            Attr("i", new JsonObject { ["intValue"] = 7 }),
            Attr("d", new JsonObject { ["doubleValue"] = 1.5 }),
            Attr("b", new JsonObject { ["boolValue"] = true }),
            Attr("a", new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray() } }),
            Attr("k", new JsonObject { ["kvlistValue"] = new JsonObject { ["values"] = new JsonArray() } }),
            Attr("unknown", new JsonObject { ["bytesValue"] = "AA==" }),
            Attr("notAnObject", JsonValue.Create("x")),
            new JsonObject { ["value"] = new JsonObject { ["stringValue"] = "no key" } },
        };
        for (var i = 0; i < 50; i++) attrs.Add(Attr($"extra{i}", new JsonObject { ["stringValue"] = "v" }));
        var envelope = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["resourceSpans"] = new JsonArray(new JsonObject
                {
                    ["scopeSpans"] = new JsonArray(new JsonObject
                    {
                        ["spans"] = new JsonArray(new JsonObject { ["name"] = "s", ["attributes"] = attrs, ["status"] = new JsonObject { ["code"] = 0 } }),
                    }),
                }),
            },
        };

        var span = DashboardTelemetry.ParseSpans(envelope).Single();
        Assert.AreEqual("unset", span.Status);
        Assert.IsNull(span.StartTime);
        var a = span.Attributes!;
        Assert.HasCount(40, a);
        Assert.AreEqual(2001, a["s"].Length);
        Assert.EndsWith("…", a["s"]);
        Assert.AreEqual("7", a["i"]);
        Assert.AreEqual("1.5", a["d"]);
        Assert.AreEqual("true", a["b"]);
        Assert.AreEqual("""{"values":[]}""", a["a"]);
        Assert.AreEqual("""{"values":[]}""", a["k"]);
        Assert.IsFalse(a.ContainsKey("unknown"));
        Assert.IsFalse(a.ContainsKey("notAnObject"));

        Assert.IsEmpty(DashboardTelemetry.ParseSpans(null));
        Assert.IsEmpty(DashboardTelemetry.ParseLogs(JsonNode.Parse("""{"data":{}}""")));

        static JsonObject Attr(string key, JsonNode value) => new() { ["key"] = key, ["value"] = value };
    }

    [TestMethod]
    public void Log_levels_come_from_the_severity_number_or_text()
    {
        var logs = DashboardTelemetry.ParseLogs(JsonNode.Parse("""
            {"data":{"resourceLogs":[{"scopeLogs":[{"logRecords":[
              {"timeUnixNano":"1","severityNumber":13,"body":{"stringValue":"w"}},
              {"timeUnixNano":"2","severityText":"Information","body":{"stringValue":"i"}},
              {"timeUnixNano":"3","body":{"stringValue":"plain"}},
              {"timeUnixNano":"bogus","body":{}},
              {"severityNumber":"x"}
            ]}]}]}}
            """));
        CollectionAssert.AreEqual(new[] { "", "", "1970-01-01T00:00:00.0000000+00:00", "1970-01-01T00:00:00.0000000+00:00", "1970-01-01T00:00:00.0000000+00:00" },
            logs.Select(l => l.Timestamp).ToArray());
        CollectionAssert.AreEquivalent(new[] { "warning", "information", "info", "info", "info" }, logs.Select(l => l.Level).ToArray());
        Assert.AreEqual(2, logs.Count(l => l.Message == "")); // no / unknown body
    }
}
