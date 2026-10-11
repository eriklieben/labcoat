using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Labcoat.Testing;

/// <summary>
/// Live console output, framework neutral: sends a line to Labcoat's detail pane while the test is still running.
/// Framework adapters build on it (Labcoat.Testing.MSTest's <c>LabcoatOutput</c>); it writes only the marker, not the
/// test's normal output.
/// </summary>
/// <remarks>
/// Under Labcoat (the test host has <c>LABCOAT_TEST_HOST=1</c>) each line goes to the process's real stdout as a
/// <c>labcoat:live:&lt;base64 json&gt;</c> marker tagged with the test's class, method and display name (and its id when
/// known); Labcoat strips the marker and shows the text under that exact test, also when tests run in parallel.
/// Outside Labcoat nothing is written.
/// </remarks>
public static class LabcoatLiveOutput
{
    /// <summary>Prefix of the marker lines on the raw stdout (Labcoat's <c>LiveOutput.Marker</c>).</summary>
    public const string Marker = "labcoat:live:";

    private static readonly object s_gate = new();
    private static Stream? s_raw;

    /// <summary>True when the tests run under Labcoat (the markers are written).</summary>
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("LABCOAT_TEST_HOST") == "1" && Environment.GetEnvironmentVariable("LABCOAT_LIVE_OUTPUT") is not ("0" or "false");

    /// <summary>Tests: where the markers go instead of the process's raw stdout.</summary>
    internal static Stream? RawOverride { get; set; }

    /// <summary>Sends <paramref name="text"/> (usually one line ending in <c>\n</c>) live to Labcoat; a no-op outside Labcoat.</summary>
    public static void Send(LabcoatTest test, string text, bool error = false)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!IsEnabled) return;
        var bytes = Encoding.UTF8.GetBytes(FormatMarker(test, text, error) + "\n");
        lock (s_gate)
        {
            try
            {
                // The raw stdout, not Console.Out: test frameworks redirect Console.Out to the test's captured output.
                var raw = RawOverride ?? (s_raw ??= Console.OpenStandardOutput());
                raw.Write(bytes, 0, bytes.Length);
                raw.Flush();
            }
            catch (IOException) { /* live output is best effort: never fail a test because stdout is gone */ }
            catch (ObjectDisposedException) { /* same */ }
        }
    }

    /// <summary>The marker line for <paramref name="text"/>: <see cref="Marker"/> + base64(UTF-8 JSON <c>{s, t, c, m, n, i?}</c>).</summary>
    public static string FormatMarker(LabcoatTest test, string text, bool error = false)
    {
        ArgumentNullException.ThrowIfNull(test);
        // Utf8JsonWriter, not JsonSerializer: reflection-based serialization is disabled in trimmed and Native AOT test
        // apps. The bytes are the ones JsonSerializer wrote for a Dictionary<string, string?> of the same fields (same
        // order, same escaping with the default encoder, null for a missing class / method name).
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("s", error ? "err" : "out");
            json.WriteString("t", text);
            json.WriteString("c", test.ClassName);
            json.WriteString("m", test.MethodName);
            json.WriteString("n", test.DisplayName);
            if (test.Id is not null) json.WriteString("i", test.Id);
            json.WriteEndObject();
        }
        return Marker + Convert.ToBase64String(buffer.WrittenSpan);
    }
}
