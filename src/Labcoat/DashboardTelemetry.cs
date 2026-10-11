using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>
/// Reads a test's failure context from the Aspire dashboard's telemetry HTTP API (Aspire 13.6+):
/// <c>GET {dashboard}/api/telemetry/spans?traceId=</c> (filtered here to error spans and HTTP spans with a status
/// code &gt;= 400 — a 4xx server span is not an OpenTelemetry error, but it is what a failing test usually hit) and
/// <c>GET {dashboard}/api/telemetry/logs?traceId=&amp;severity=Warning</c> (OTLP/JSON in a
/// <c>{ data: { resourceSpans | resourceLogs }, totalCount, returnedCount }</c> envelope), authenticated with the
/// <c>x-api-key</c> header when the dashboard API is secured.
/// <list type="bullet">
/// <item>URL: <c>LABCOAT_DASHBOARD_API_URL</c>, else <c>ASPIRE_DASHBOARD_URL</c> (both set by Labcoat.Aspire.Hosting).</item>
/// <item>Key: <c>LABCOAT_DASHBOARD_API_KEY</c> (the AppHost's <c>AppHost:DashboardApiKey</c>); none = unsecured API.</item>
/// </list>
/// </summary>
public static class DashboardTelemetry
{
    // Not readonly only for tests (Override); set once from the environment otherwise.
    private static string? s_baseUrl = Env("LABCOAT_DASHBOARD_API_URL") ?? Env("ASPIRE_DASHBOARD_URL");
    private static string? s_apiKey = Env("LABCOAT_DASHBOARD_API_KEY");
    private const int Limit = 200;
    /// <summary>OTLP's key/value list of a resource, span, event or log record.</summary>
    private const string AttributesMember = "attributes";

    private static HttpClient s_http = new(new SocketsHttpHandler
    {
        // The dashboard runs on localhost with the ASP.NET Core dev certificate, which .NET often does not trust on
        // Linux. Accept certificate errors for loopback only.
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || IsLoopback(s_baseUrl),
        },
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>A dashboard URL is known (hosted under Aspire).</summary>
    public static bool Available => s_baseUrl is not null;

    /// <summary>Tests only: another dashboard URL / API key / HTTP handler until the result is disposed.</summary>
    internal static IDisposable Override(string? baseUrl, string? apiKey, HttpMessageHandler handler)
    {
        var (url, key, http) = (s_baseUrl, s_apiKey, s_http);
        (s_baseUrl, s_apiKey, s_http) = (baseUrl, apiKey, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) });
        return new Restore(() => (s_baseUrl, s_apiKey, s_http) = (url, key, http));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public static async Task<TestTelemetry> GetAsync(string traceId, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(traceId);
        var spansTask = GetJsonAsync($"/api/telemetry/spans?traceId={id}&limit={Limit}", ct);
        var logsTask = GetJsonAsync($"/api/telemetry/logs?traceId={id}&severity=Warning&limit={Limit}", ct);
        var spans = ParseSpans(await spansTask).Where(s => s.Status == "error" || s.HttpStatusCode >= 400).ToList();
        var logs = ParseLogs(await logsTask);
        return new TestTelemetry(traceId, spans, logs);
    }

    private static async Task<JsonNode?> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(s_baseUrl!), pathAndQuery));
        if (s_apiKey is not null) req.Headers.Add("x-api-key", s_apiKey);
        using var res = await s_http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null; // unknown trace / resource
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new DashboardTelemetryException("The Aspire dashboard rejected the API key (401).");
        if (!res.IsSuccessStatusCode)
            throw new DashboardTelemetryException($"The Aspire dashboard answered {(int)res.StatusCode} {res.ReasonPhrase}.");
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    public static List<TelemetrySpan> ParseSpans(JsonNode? envelope)
    {
        var list = new List<TelemetrySpan>();
        foreach (var rs in Items(envelope?["data"]?["resourceSpans"]))
        {
            var resource = ResourceName(rs?["resource"]);
            list.AddRange(Items(rs?["scopeSpans"]).SelectMany(ss => Items(ss?["spans"])).OfType<JsonNode>().Select(span => ToSpan(span, resource)));
        }
        return list.OrderBy(s => s.StartTime, StringComparer.Ordinal).ToList();
    }

    /// <summary>The elements of a JSON array; none for anything else.</summary>
    private static IEnumerable<JsonNode?> Items(JsonNode? node) => node as JsonArray ?? [];

    private static TelemetrySpan ToSpan(JsonNode span, string resource)
    {
        var start = Nanos(span["startTimeUnixNano"]);
        var end = Nanos(span["endTimeUnixNano"]);
        var code = span["status"]?["code"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : 0;
        var (exType, exMessage) = SpanException(span);
        return new TelemetrySpan
        {
            Name = Str(span["name"]) ?? "",
            Resource = resource,
            Status = code switch { 2 => "error", 1 => "ok", _ => "unset" },
            StatusMessage = Str(span["status"]?["message"]) is { Length: > 0 } sm ? sm : null,
            DurationMs = start is not null && end is not null ? Math.Round((end.Value - start.Value) / 1_000_000.0, 2) : null,
            StartTime = start is { } s ? FromNanos(s) : null,
            SpanId = Str(span["spanId"]),
            Attributes = Attributes(span[AttributesMember]) is { Count: > 0 } a ? a : null,
            ExceptionType = exType,
            ExceptionMessage = exMessage,
            HttpStatusCode = HttpStatus(span[AttributesMember]),
        };
    }

    /// <summary>Type and message of the span's first "exception" events (each from the first event that has it).</summary>
    private static (string? Type, string? Message) SpanException(JsonNode span)
    {
        string? exType = null, exMessage = null;
        foreach (var ev in Items(span["events"]).Where(ev => ev?["name"]?.GetValue<string>() == "exception"))
        {
            var attrs = Attributes(ev![AttributesMember]);
            exType ??= attrs.GetValueOrDefault("exception.type");
            exMessage ??= attrs.GetValueOrDefault("exception.message");
        }
        return (exType, exMessage);
    }

    public static List<TelemetryLog> ParseLogs(JsonNode? envelope)
    {
        var list = new List<TelemetryLog>();
        foreach (var rl in Items(envelope?["data"]?["resourceLogs"]))
        {
            var resource = ResourceName(rl?["resource"]);
            foreach (var sl in Items(rl?["scopeLogs"]))
                list.AddRange(Items(sl?["logRecords"]).OfType<JsonNode>().Select(rec => ToLog(rec, sl, resource)));
        }
        return list.OrderBy(l => l.Timestamp, StringComparer.Ordinal).ToList();
    }

    private static TelemetryLog ToLog(JsonNode rec, JsonNode? scopeLogs, string resource)
    {
        var severity = rec["severityNumber"] is JsonValue sv && sv.TryGetValue<int>(out var n) ? n : 0;
        var t = Nanos(rec["timeUnixNano"]) ?? Nanos(rec["observedTimeUnixNano"]);
        var attrs = Attributes(rec[AttributesMember]);
        attrs.Remove("aspire.log_id");
        return new TelemetryLog
        {
            Resource = resource,
            Level = LevelOf(severity, rec),
            Message = AnyValue(rec["body"]) ?? "",
            Timestamp = t is { } ts ? FromNanos(ts) : "",
            SpanId = Str(rec["spanId"]) is { Length: > 0 } sid ? sid : null,
            Category = Str(scopeLogs?["scope"]?["name"]) is { Length: > 0 } cat ? cat : null,
            Attributes = attrs.Count > 0 ? attrs : null,
        };
    }

    /// <summary>OTLP severity numbers: 13-16 warn, 17-20 error, 21-24 fatal; below that the record's severity text.</summary>
    private static string LevelOf(int severity, JsonNode rec) => severity switch
    {
        >= 21 => "critical",
        >= 17 => "error",
        >= 13 => "warning",
        _ => Str(rec["severityText"])?.ToLowerInvariant() ?? "info",
    };

    private static int? HttpStatus(JsonNode? attributes)
    {
        var a = Attributes(attributes);
        var v = a.GetValueOrDefault("http.response.status_code") ?? a.GetValueOrDefault("http.status_code");
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static string ResourceName(JsonNode? resource) =>
        Attributes(resource?[AttributesMember]).GetValueOrDefault("service.name") ?? "";

    private static Dictionary<string, string> Attributes(JsonNode? attributes)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in attributes as JsonArray ?? [])
        {
            var key = Str(a?["key"]);
            if (key is null || d.Count >= 40) continue;
            if (AnyValue(a?["value"]) is { } v) d[key] = v.Length > 2000 ? v[..2000] + "…" : v;
        }
        return d;
    }

    /// <summary>OTLP AnyValue → string (stringValue / intValue / doubleValue / boolValue; arrays and maps as JSON).</summary>
    private static string? AnyValue(JsonNode? value)
    {
        if (value is not JsonObject o) return null;
        if (Str(o["stringValue"]) is { } s) return s;
        if (o["intValue"] is { } i) return i.ToString().Trim('"');
        if (o["doubleValue"] is { } dbl) return dbl.ToString();
        if (o["boolValue"] is { } b) return b.ToString();
        if (o["arrayValue"] is { } arr) return arr.ToJsonString();
        if (o["kvlistValue"] is { } kv) return kv.ToJsonString();
        return null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Nanos(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static string FromNanos(long nanos) =>
        DateTimeOffset.UnixEpoch.AddTicks(nanos / 100).ToString("o");

    private static bool IsLoopback(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

public sealed class DashboardTelemetryException(string message) : Exception(message);

/// <summary>GET /api/tests/telemetry response.</summary>
public sealed record TestTelemetry(string TraceId, List<TelemetrySpan> Spans, List<TelemetryLog> Logs);

public sealed record TelemetrySpan
{
    public required string Name { get; init; }
    /// <summary>service.name of the resource that produced the span (e.g. "api").</summary>
    public required string Resource { get; init; }
    /// <summary>"error" | "ok" | "unset"</summary>
    public required string Status { get; init; }
    public string? StatusMessage { get; init; }
    public double? DurationMs { get; init; }
    public string? StartTime { get; init; }
    public string? SpanId { get; init; }
    public Dictionary<string, string>? Attributes { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    /// <summary>http.response.status_code (or legacy http.status_code) of HTTP spans.</summary>
    public int? HttpStatusCode { get; init; }
}

public sealed record TelemetryLog
{
    public required string Resource { get; init; }
    /// <summary>"warning" | "error" | "critical" (only warning and above are returned)</summary>
    public required string Level { get; init; }
    public required string Message { get; init; }
    public required string Timestamp { get; init; }
    public string? SpanId { get; init; }
    /// <summary>Logger category (the OTLP scope name), e.g. "Program".</summary>
    public string? Category { get; init; }
    public Dictionary<string, string>? Attributes { get; init; }
}
