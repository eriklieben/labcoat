using System.Text;
using System.Text.Json;

namespace Labcoat.Testing.MSTest.Tests;

/// <summary>
/// LabcoatOutput: lines go to the test's console output and, under Labcoat (LABCOAT_TEST_HOST=1), also as a
/// <c>labcoat:live:</c> marker to the raw stdout. Process-wide state (environment, Console.Out): not in parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LabcoatOutputTests
{
    private static readonly FakeTestContext Context = new("Rows", "Rows (2)", "Shop.Tests.OrderTests");

    private static (string Console, List<JsonElement> Markers) Capture(bool underLabcoat, Action write)
    {
        var console = new StringWriter();
        var raw = new MemoryStream();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        var oldHost = Environment.GetEnvironmentVariable("LABCOAT_TEST_HOST");
        try
        {
            Console.SetOut(console);
            Console.SetError(console);
            Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", underLabcoat ? "1" : null);
            LabcoatLiveOutput.RawOverride = raw;
            write();
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
            Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", oldHost);
            LabcoatLiveOutput.RawOverride = null;
        }
        var markers = Encoding.UTF8.GetString(raw.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l =>
            {
                Assert.IsTrue(l.StartsWith(LabcoatOutput.Marker, StringComparison.Ordinal), l);
                return JsonDocument.Parse(Convert.FromBase64String(l[LabcoatOutput.Marker.Length..])).RootElement.Clone();
            }).ToList();
        return (console.ToString(), markers);
    }

    [TestMethod]
    public void Under_Labcoat_writes_the_line_and_a_tagged_marker()
    {
        var (console, markers) = Capture(true, () =>
        {
            LabcoatOutput.WriteLine(Context, "Starting the app…");
            LabcoatOutput.WriteErrorLine(Context, "warning!");
        });
        Assert.AreEqual("Starting the app…\nwarning!\n", console.Replace("\r\n", "\n"));
        Assert.HasCount(2, markers);
        Assert.AreEqual("out", markers[0].GetProperty("s").GetString());
        Assert.AreEqual("Starting the app…\n", markers[0].GetProperty("t").GetString());
        Assert.AreEqual("Shop.Tests.OrderTests", markers[0].GetProperty("c").GetString());
        Assert.AreEqual("Rows", markers[0].GetProperty("m").GetString());
        Assert.AreEqual("Rows (2)", markers[0].GetProperty("n").GetString());
        Assert.AreEqual("err", markers[1].GetProperty("s").GetString());
    }

    [TestMethod]
    public void Outside_Labcoat_is_plain_console_output()
    {
        var (console, markers) = Capture(false, () => LabcoatOutput.WriteLine(Context, "just a line"));
        Assert.AreEqual("just a line", console.TrimEnd());
        Assert.IsEmpty(markers);
        Assert.IsFalse(LabcoatOutput.IsEnabled);
    }

    [TestMethod]
    public void The_writer_sends_complete_lines()
    {
        var (console, markers) = Capture(true, () =>
        {
            using var w = LabcoatOutput.CreateWriter(Context);
            w.Write("par");
            w.Write("tial\r\nsecond");
            w.WriteLine(" line");
            w.Write("unterminated"); // flushed on dispose
        });
        CollectionAssert.AreEqual(new[] { "partial\n", "second line\n", "unterminated\n" }, markers.Select(m => m.GetProperty("t").GetString()).ToArray());
        Assert.Contains("second line", console);
        Assert.AreEqual(Encoding.UTF8, LabcoatOutput.CreateWriter(Context).Encoding);
    }

    [TestMethod]
    public void Rejects_a_missing_test_context()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatOutput.WriteLine(null!, "x"));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatOutput.WriteErrorLine(null!, "x"));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatOutput.CreateWriter(null!));
    }

    [TestMethod]
    public void FormatMarker_is_the_marker_prefix_plus_base64_json()
    {
        var m = LabcoatOutput.FormatMarker(Context, "x", error: true);
        Assert.StartsWith("labcoat:live:", m);
        var json = JsonDocument.Parse(Convert.FromBase64String(m["labcoat:live:".Length..])).RootElement;
        Assert.AreEqual("err", json.GetProperty("s").GetString());
    }
}
