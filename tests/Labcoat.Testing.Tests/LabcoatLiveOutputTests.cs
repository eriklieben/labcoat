using System.Text;
using System.Text.Json;

namespace Labcoat.Testing.Tests;

/// <summary>
/// LabcoatLiveOutput: under Labcoat (LABCOAT_TEST_HOST=1) a <c>labcoat:live:</c> marker on the raw stdout, nothing
/// otherwise. Process-wide state (environment): not in parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LabcoatLiveOutputTests
{
    private static readonly LabcoatTest Rows = new("Rows (2)") { Id = "uid-1", ClassName = "Shop.Tests.OrderTests", MethodName = "Rows" };

    private static List<JsonElement> Capture(string? host, string? live, Action write)
    {
        var raw = new MemoryStream();
        var oldHost = Environment.GetEnvironmentVariable("LABCOAT_TEST_HOST");
        var oldLive = Environment.GetEnvironmentVariable("LABCOAT_LIVE_OUTPUT");
        try
        {
            Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", host);
            Environment.SetEnvironmentVariable("LABCOAT_LIVE_OUTPUT", live);
            LabcoatLiveOutput.RawOverride = raw;
            write();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", oldHost);
            Environment.SetEnvironmentVariable("LABCOAT_LIVE_OUTPUT", oldLive);
            LabcoatLiveOutput.RawOverride = null;
        }
        return Encoding.UTF8.GetString(raw.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Decode).ToList();
    }

    private static JsonElement Decode(string line)
    {
        Assert.IsTrue(line.StartsWith(LabcoatLiveOutput.Marker, StringComparison.Ordinal), line);
        return JsonDocument.Parse(Convert.FromBase64String(line[LabcoatLiveOutput.Marker.Length..])).RootElement.Clone();
    }

    [TestMethod]
    public void Under_Labcoat_sends_a_tagged_marker()
    {
        var markers = Capture("1", null, () =>
        {
            LabcoatLiveOutput.Send(Rows, "Starting the app…\n");
            LabcoatLiveOutput.Send(Rows, "warning!\n", error: true);
        });
        Assert.HasCount(2, markers);
        Assert.AreEqual("out", markers[0].GetProperty("s").GetString());
        Assert.AreEqual("Starting the app…\n", markers[0].GetProperty("t").GetString());
        Assert.AreEqual("Shop.Tests.OrderTests", markers[0].GetProperty("c").GetString());
        Assert.AreEqual("Rows", markers[0].GetProperty("m").GetString());
        Assert.AreEqual("Rows (2)", markers[0].GetProperty("n").GetString());
        Assert.AreEqual("uid-1", markers[0].GetProperty("i").GetString());
        Assert.AreEqual("err", markers[1].GetProperty("s").GetString());
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("0", null)]
    [DataRow("1", "0")]
    [DataRow("1", "false")]
    public void Outside_Labcoat_or_switched_off_sends_nothing(string? host, string? live)
    {
        Assert.IsEmpty(Capture(host, live, () =>
        {
            Assert.IsFalse(LabcoatLiveOutput.IsEnabled);
            LabcoatLiveOutput.Send(Rows, "x\n");
        }));
    }

    [TestMethod]
    public void FormatMarker_leaves_out_an_unknown_id()
    {
        var json = Decode(LabcoatLiveOutput.FormatMarker(new LabcoatTest("T"), "x"));
        Assert.IsFalse(json.TryGetProperty("i", out _));
        Assert.AreEqual(JsonValueKind.Null, json.GetProperty("c").ValueKind);
        Assert.AreEqual("T", json.GetProperty("n").GetString());
    }

    /// <summary>The marker JSON as Labcoat.Testing 1.1.0 wrote it (JsonSerializer over a dictionary): the reference.</summary>
    private static byte[] PreviousJson(LabcoatTest test, string text, bool error)
    {
        var fields = new Dictionary<string, string?>
        {
            ["s"] = error ? "err" : "out",
            ["t"] = text,
            ["c"] = test.ClassName,
            ["m"] = test.MethodName,
            ["n"] = test.DisplayName,
        };
        if (test.Id is not null) fields["i"] = test.Id;
        return JsonSerializer.SerializeToUtf8Bytes(fields);
    }

    public static IEnumerable<object?[]> MarkerCases =>
    [
        [Rows, "Starting the app…\n", false],
        [Rows, "warning!\n", true],
        [new LabcoatTest("T"), "x", false],
        [new LabcoatTest("T") { ClassName = "Ns.C" }, "", true],
        // Escaping: quotes, backslash, control characters, HTML-sensitive characters, non-ASCII, a surrogate pair.
        [Rows, "say \"hi\" \\ path\\to\tfile\r\n\u0001\u001f\u007f", false],
        [Rows, "<b>a & b</b> 'x' + `y` = 1", false],
        [new LabcoatTest("Café \"d\" <n>") { Id = "id/\"1\"&", ClassName = "Ns.Ü<T>", MethodName = "M\u2028" }, "日本語 é 😀 \u00a0", true],
    ];

    [TestMethod]
    [DynamicData(nameof(MarkerCases))]
    public void FormatMarker_writes_the_same_bytes_as_before(LabcoatTest test, string text, bool error)
    {
        var marker = LabcoatLiveOutput.FormatMarker(test, text, error);
        Assert.StartsWith(LabcoatLiveOutput.Marker, marker);
        var expected = PreviousJson(test, text, error);
        var actual = Convert.FromBase64String(marker[LabcoatLiveOutput.Marker.Length..]);
        CollectionAssert.AreEqual(expected, actual, Encoding.UTF8.GetString(actual));
        Assert.AreEqual(LabcoatLiveOutput.Marker + Convert.ToBase64String(expected), marker);
    }

    [TestMethod]
    public void A_closed_stdout_is_ignored()
    {
        var closed = new MemoryStream();
        closed.Dispose();
        var oldHost = Environment.GetEnvironmentVariable("LABCOAT_TEST_HOST");
        var oldLive = Environment.GetEnvironmentVariable("LABCOAT_LIVE_OUTPUT");
        try
        {
            Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", "1");
            Environment.SetEnvironmentVariable("LABCOAT_LIVE_OUTPUT", null);
            LabcoatLiveOutput.RawOverride = closed;
            LabcoatLiveOutput.Send(Rows, "x\n"); // ObjectDisposedException: swallowed
            using var throwing = new ThrowingStream();
            LabcoatLiveOutput.RawOverride = throwing;
            LabcoatLiveOutput.Send(Rows, "x\n"); // IOException: swallowed
            Assert.AreEqual(1, throwing.Writes, "the marker was written (and the IOException swallowed)");

            // A failed write leaves nothing behind: the next marker goes out as usual.
            using var working = new MemoryStream();
            LabcoatLiveOutput.RawOverride = working;
            LabcoatLiveOutput.Send(Rows, "y\n");
            Assert.StartsWith(LabcoatLiveOutput.Marker, Encoding.UTF8.GetString(working.ToArray()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", oldHost);
            Environment.SetEnvironmentVariable("LABCOAT_LIVE_OUTPUT", oldLive);
            LabcoatLiveOutput.RawOverride = null;
        }
    }

    [TestMethod]
    public void Rejects_a_missing_test()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatLiveOutput.Send(null!, "x"));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatLiveOutput.FormatMarker(null!, "x"));
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            throw new IOException("pipe closed");
        }
    }
}
