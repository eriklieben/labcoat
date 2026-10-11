using System.Text;
using System.Text.Json;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace Labcoat.Testing.NUnit.Tests;

/// <summary>LabcoatOutput for NUnit: the test's own output plus live markers tagged with the test's identity.</summary>
public sealed class LabcoatOutputTests
{
    private MemoryStream _raw = null!;
    private string? _testHost;
    private LabcoatTest? _outsideTest;

    private long _outsideMarkers = -1;

    [OneTimeSetUp]
    public void OutsideATest()
    {
        // The fixture is current, not a test: nothing to tag, so no live marker.
        _outsideTest = LabcoatOutput.CurrentTest();
        using var raw = new MemoryStream();
        var testHost = Environment.GetEnvironmentVariable("LABCOAT_TEST_HOST");
        LabcoatLiveOutput.RawOverride = raw;
        Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", "1");
        try
        {
            LabcoatOutput.WriteLine("fixture setup");
            using var writer = LabcoatOutput.CreateWriter();
            writer.WriteLine("fixture setup, via a writer");
        }
        finally
        {
            LabcoatLiveOutput.RawOverride = null;
            Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", testHost);
        }
        _outsideMarkers = raw.Length;
    }

    [SetUp]
    public void Capture()
    {
        _raw = new MemoryStream();
        LabcoatLiveOutput.RawOverride = _raw;
        _testHost = Environment.GetEnvironmentVariable("LABCOAT_TEST_HOST");
        Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", "1");
    }

    [TearDown]
    public void Restore()
    {
        LabcoatLiveOutput.RawOverride = null;
        Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", _testHost);
        _raw.Dispose();
    }

    /// <summary>The decoded JSON of each marker line written so far.</summary>
    private List<JsonElement> Markers() =>
        Encoding.UTF8.GetString(_raw.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                Assert.That(line, Does.StartWith(LabcoatLiveOutput.Marker));
                return JsonDocument.Parse(Convert.FromBase64String(line[LabcoatLiveOutput.Marker.Length..])).RootElement.Clone();
            })
            .ToList();

    private static string Output => TestExecutionContext.CurrentContext.CurrentResult.Output;

    [Test]
    public void WriteLine_goes_to_the_test_output_and_live_to_Labcoat()
    {
        Assert.That(LabcoatOutput.IsEnabled, Is.True);
        LabcoatOutput.WriteLine("hello");
        LabcoatOutput.WriteLine(null);

        Assert.That(Output, Does.Contain("hello" + Environment.NewLine));
        var markers = Markers();
        Assert.That(markers, Has.Count.EqualTo(2));
        var m = markers[0];
        Assert.That(m.GetProperty("s").GetString(), Is.EqualTo("out"));
        Assert.That(m.GetProperty("t").GetString(), Is.EqualTo("hello\n"));
        Assert.That(m.GetProperty("c").GetString(), Is.EqualTo(typeof(LabcoatOutputTests).FullName));
        Assert.That(m.GetProperty("m").GetString(), Is.EqualTo(nameof(WriteLine_goes_to_the_test_output_and_live_to_Labcoat)));
        Assert.That(m.GetProperty("n").GetString(), Is.EqualTo(TestContext.CurrentContext.Test.Name));
        Assert.That(m.GetProperty("i").GetString(), Is.EqualTo(TestContext.CurrentContext.Test.FullName));
        Assert.That(markers[1].GetProperty("t").GetString(), Is.EqualTo("\n"));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void Data_rows_are_tagged_with_their_own_id(int row)
    {
        LabcoatOutput.WriteLine($"row {row}");
        var m = Markers().Single();
        Assert.That(m.GetProperty("i").GetString(), Is.EqualTo(TestContext.CurrentContext.Test.FullName));
        Assert.That(m.GetProperty("n").GetString(), Is.EqualTo($"Data_rows_are_tagged_with_their_own_id({row})"));
    }

    [Test]
    public void WriteErrorLine_is_sent_as_error_output()
    {
        LabcoatOutput.WriteErrorLine("oops");
        var m = Markers().Single();
        Assert.That(m.GetProperty("s").GetString(), Is.EqualTo("err"));
        Assert.That(m.GetProperty("t").GetString(), Is.EqualTo("oops\n"));
    }

    [Test]
    public void The_writer_sends_a_line_per_completed_line_and_the_rest_on_dispose()
    {
        using (var writer = LabcoatOutput.CreateWriter())
        {
            Assert.That(writer.Encoding, Is.EqualTo(Encoding.UTF8));
            writer.Write("first\r\nsec");
            writer.Write((string?)null);
            writer.Write("ond\npartial");
            writer.Flush();
            writer.Flush(); // nothing left
            writer.Write("tail");
        }

        Assert.That(Markers().Select(m => m.GetProperty("t").GetString()), Is.EqualTo(new[] { "first\n", "second\n", "partial\n", "tail\n" }));
        Assert.That(Output, Does.Contain("first" + Environment.NewLine + "second" + Environment.NewLine + "partial" + Environment.NewLine + "tail"));
    }

    [Test]
    public void Outside_Labcoat_only_the_test_output_is_written()
    {
        Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", null);
        LabcoatOutput.WriteLine("quiet");
        using (var writer = LabcoatOutput.CreateWriter()) writer.WriteLine("also quiet");

        Assert.That(LabcoatOutput.IsEnabled, Is.False);
        Assert.That(_raw.Length, Is.Zero);
        Assert.That(Output, Does.Contain("quiet"));
    }

    [Test]
    public void Outside_a_test_there_is_no_test_to_tag()
    {
        Assert.That(_outsideTest, Is.Null);
        Assert.That(_outsideMarkers, Is.Zero);
    }
}
