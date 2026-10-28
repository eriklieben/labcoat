using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>Maps MTP server-mode test node JSON onto the contract's TestNode.</summary>
public static partial class MtpMapping
{
    public static TestNode ToTestNode(string projectId, JsonObject node)
    {
        string? Str(string key) => node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        int? Int(string key) => node[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

        var (ns, cls) = SplitTypeName(Str("location.type"));
        var method = MethodName(Str("location.method"));
        var traits = Traits(node["traits"]);

        double? duration = node["time.duration-ms"] is JsonValue d && d.TryGetValue<double>(out var ms) ? Math.Round(ms, 2) : null;

        return new TestNode
        {
            Id = Str("uid") ?? "",
            ProjectId = projectId,
            DisplayName = Str("display-name") ?? Str("uid") ?? "",
            Namespace = ns,
            ClassName = cls,
            MethodName = method,
            FilePath = Str("location.file"),
            Line = Int("location.line-start"),
            LineEnd = Int("location.line-end"),
            Traits = traits,
            DurationMs = duration,
            ErrorMessage = Str("error.message"),
            StackTrace = Str("error.stacktrace"),
            StdOut = Telemetry.ExtractTraceId(NullIfEmpty(Str("standardOutput")), out var traceId),
            TraceId = traceId,
            StdErr = NullIfEmpty(Str("standardError")),
        };
    }

    /// <summary>Namespace and class of a type name (nested classes are reported as Outer+Inner).</summary>
    private static (string? Namespace, string? Class) SplitTypeName(string? type)
    {
        var lastDot = type?.LastIndexOf('.') ?? -1;
        return lastDot > 0 ? (type![..lastDot], type[(lastDot + 1)..]) : (null, type);
    }

    /// <summary>Parameterized methods are reported with their signature: "Multiply(System.Int32,System.Int32)".</summary>
    private static string? MethodName(string? method)
    {
        var paren = method?.IndexOf('(') ?? -1;
        return paren > 0 ? method![..paren] : method;
    }

    /// <summary>Observed shape: [ { "&lt;key&gt;": "&lt;value&gt;" }, ... ]. MSTest [TestCategory("X")] arrives as { "X": "" }.</summary>
    private static List<Trait> Traits(JsonNode? traits)
    {
        var result = new List<Trait>();
        if (traits is not JsonArray arr) return result;
        foreach (var o in arr.OfType<JsonObject>())
        {
            foreach (var (k, v) in o)
            {
                var value = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "";
                result.Add(string.IsNullOrEmpty(value) ? new Trait("Category", k) : new Trait(k, value));
            }
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s.Replace("\r\n", "\n");

    /// <summary>MTP execution-state -> Outcome. null = ignore (e.g. "discovered").</summary>
    public static Outcome? MapState(string? state) => state switch
    {
        "in-progress" => Outcome.Running,
        "passed" => Outcome.Passed,
        "failed" or "error" => Outcome.Failed,
        "skipped" => Outcome.Skipped,
        "timed-out" => Outcome.TimedOut,
        "cancelled" => Outcome.Cancelled,
        _ => null,
    };

    public static void ApplyResult(TestNode t, TestNode incoming, Outcome outcome, string? rawState)
    {
        t.Outcome = outcome;
        t.DisplayName = incoming.DisplayName;
        t.DurationMs = incoming.DurationMs;
        t.ErrorMessage = incoming.ErrorMessage;
        t.StackTrace = incoming.StackTrace;
        t.StdOut = incoming.StdOut;
        t.TraceId = incoming.TraceId;
        t.StdErr = incoming.StdErr;
        t.ErrorType = ErrorTypeOf(incoming, outcome, rawState);
        t.LastRunAt = Json.Now();
        t.Stale = null;
        t.SnapshotsResolved = false;
        if (incoming.FilePath is not null) t.FilePath = incoming.FilePath;
        if (incoming.Line is not null) t.Line = incoming.Line;
        if (incoming.LineEnd is not null) t.LineEnd = incoming.LineEnd;
    }

    /// <summary>The error type of a failed / timed-out result (null otherwise).</summary>
    private static string? ErrorTypeOf(TestNode incoming, Outcome outcome, string? rawState)
    {
        if (outcome is not (Outcome.Failed or Outcome.TimedOut)) return null;
        return rawState == "timed-out" ? "Timeout" : GuessExceptionType(incoming.ErrorMessage, incoming.StackTrace);
    }

    [GeneratedRegex(@"threw exception:\s*([\w.`+]+(?:Exception|Error))", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ThrewRe();

    [GeneratedRegex(@"^([A-Za-z_][\w.`+]*\.\w*(?:Exception|Error)):", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex LeadingTypeRe();

    /// <summary>
    /// MTP server mode does not carry the exception type of a failure, so this is a best-effort guess:
    /// "...threw exception: System.X: msg" / "System.X: msg" in the message, or well-known assertion frames
    /// at the top of the stack trace.
    /// </summary>
    private static string? GuessExceptionType(string? message, string? stack)
    {
        if (message is not null)
        {
            var m = ThrewRe().MatchOrEmpty(message);
            if (m.Success) return m.Groups[1].Value;
            m = LeadingTypeRe().MatchOrEmpty(message);
            if (m.Success) return m.Groups[1].Value;
        }
        var first = stack?.TrimStart().Split('\n')[0] ?? "";
        if (first.Contains("Microsoft.VisualStudio.TestTools.UnitTesting.Assert", StringComparison.Ordinal))
            return "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException";
        if (first.Contains("Xunit.Assert", StringComparison.Ordinal)) return "Xunit.Sdk.XunitException";
        if (first.Contains("NUnit.Framework.Assert", StringComparison.Ordinal)) return "NUnit.Framework.AssertionException";
        return null;
    }
}
