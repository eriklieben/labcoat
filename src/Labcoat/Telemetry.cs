using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// Links tests to their traces in the Aspire dashboard (see src/Labcoat.Testing).
/// <list type="bullet">
/// <item>Trace collection is opt-in (off by default; <c>--traces</c>, <c>WithTestTracing()</c> or the UI toggle).
/// When it is on and Labcoat is hosted by an Aspire AppHost (<c>ASPIRE_DASHBOARD_URL</c> set), test hosts get the
/// dashboard's OTLP endpoint as <c>LABCOAT_OTLP_*</c> (from the standard <c>OTEL_EXPORTER_OTLP_*</c> variables the
/// AppHost gives the Labcoat resource via <c>WithOtlpExporter()</c>). Those <c>OTEL_*</c> variables describe the
/// Labcoat resource itself, so they are never passed on: tests behave exactly as outside Labcoat unless tracing is on
/// and they opt in with Labcoat.Testing.</item>
/// <item>Labcoat.Testing writes <c>labcoat:traceId=&lt;hex&gt;</c> to the test output; it is stripped from stdOut and
/// becomes <see cref="TestNode.TraceId"/>.</item>
/// </list>
/// </summary>
public static partial class Telemetry
{
    public const string TraceIdMarker = "labcoat:traceId=";

    // Not readonly only for tests (Override); set once from the environment otherwise.
    private static bool s_hosted = Env("ASPIRE_DASHBOARD_URL") is not null;
    private static string? s_endpoint = s_hosted ? Env("OTEL_EXPORTER_OTLP_ENDPOINT") : null;
    private static string? s_protocol = Env("OTEL_EXPORTER_OTLP_PROTOCOL");
    private static string? s_headers = Env("OTEL_EXPORTER_OTLP_HEADERS");

    /// <summary>True when Labcoat has an OTLP endpoint it could hand to test hosts (ExplorerState.tracingAvailable).</summary>
    public static bool Available => s_endpoint is not null;

    /// <summary>Tests only: behave as if hosted by an AppHost with this OTLP endpoint until the result is disposed.</summary>
    internal static IDisposable Override(bool hosted, string? endpoint, string? protocol = null, string? headers = null)
    {
        var saved = (s_hosted, s_endpoint, s_protocol, s_headers);
        (s_hosted, s_endpoint, s_protocol, s_headers) = (hosted, endpoint, protocol, headers);
        return new Restore(() => (s_hosted, s_endpoint, s_protocol, s_headers) = saved);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>
    /// Sets up the environment of a test host process: without the Aspire run's variables and Labcoat's own settings
    /// (<see cref="TestHostEnvironment"/>), plus <c>LABCOAT_OTLP_*</c> when <paramref name="tracingEnabled"/>.
    /// Returns the names that were removed.
    /// </summary>
    public static List<string> ConfigureTestHost(IDictionary<string, string?> env, bool tracingEnabled)
    {
        var removed = TestHostEnvironment.Sanitize(env, s_hosted, TestHostEnvironment.PassThrough);
        if (s_endpoint is null || !tracingEnabled) return removed;
        env["LABCOAT_OTLP_ENDPOINT"] = s_endpoint;
        if (s_protocol is not null) env["LABCOAT_OTLP_PROTOCOL"] = s_protocol;
        if (s_headers is not null) env["LABCOAT_OTLP_HEADERS"] = s_headers;
        return removed;
    }

    /// <summary>Removes the trace id marker line(s) from a test's stdOut; returns the cleaned output (null if empty).</summary>
    public static string? ExtractTraceId(string? stdOut, out string? traceId)
    {
        traceId = null;
        if (stdOut is null || !stdOut.Contains(TraceIdMarker, StringComparison.Ordinal)) return stdOut;
        string? found = null;
        string cleaned;
        try
        {
            cleaned = MarkerLine().Replace(stdOut, m =>
            {
                found ??= m.Groups[1].Value.ToLowerInvariant();
                return "";
            });
        }
        catch (RegexMatchTimeoutException) { return stdOut; } // as if there was no marker: the output stays as it is
        traceId = found;
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    [GeneratedRegex(@"^labcoat:traceId=([0-9a-fA-F]{32})[ \t]*(?:\r?\n|$)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex MarkerLine();

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
