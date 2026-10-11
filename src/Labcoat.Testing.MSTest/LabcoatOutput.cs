using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Labcoat.Testing;

/// <summary>
/// Live console output for MSTest: lines written here appear in Labcoat's detail pane while the test is still running,
/// and in the test's normal output (MSTest captures <see cref="Console.Out"/> per test) once it finishes.
/// </summary>
/// <remarks>
/// <para>
/// MSTest attaches captured console output to the result only when the test ends (MSTest 4.0–4.3 has no live mode), so
/// a slow integration test shows nothing until it is done. Under Labcoat (the test host has <c>LABCOAT_TEST_HOST=1</c>)
/// each line is also sent live with <see cref="LabcoatLiveOutput"/>, tagged with the test's class, method and display
/// name; Labcoat shows the text under that exact test, also when tests run in parallel. Outside Labcoat this is just
/// <see cref="Console.WriteLine(string)"/>.
/// </para>
/// <code>
/// LabcoatOutput.WriteLine(TestContext, "Starting the Aspire app…");
/// var log = LabcoatOutput.CreateWriter(TestContext);   // a TextWriter, e.g. for a logger that writes to a TextWriter
/// </code>
/// </remarks>
public static class LabcoatOutput
{
    /// <summary>Prefix of the marker lines on the raw stdout (Labcoat's <c>LiveOutput.Marker</c>).</summary>
    public const string Marker = LabcoatLiveOutput.Marker;

    /// <summary>True when the tests run under Labcoat (the markers are written).</summary>
    public static bool IsEnabled => LabcoatLiveOutput.IsEnabled;

    /// <summary>Writes a line to the test's output, and live to Labcoat.</summary>
    public static void WriteLine(TestContext testContext, string? message)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        Console.Out.WriteLine(message);
        LabcoatLiveOutput.Send(LabcoatMSTest.ToLabcoatTest(testContext), (message ?? "") + "\n");
    }

    /// <summary>Writes a line to the test's error output (<see cref="Console.Error"/>), and live to Labcoat.</summary>
    public static void WriteErrorLine(TestContext testContext, string? message)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        Console.Error.WriteLine(message);
        LabcoatLiveOutput.Send(LabcoatMSTest.ToLabcoatTest(testContext), (message ?? "") + "\n", error: true);
    }

    /// <summary>A <see cref="TextWriter"/> that does <see cref="WriteLine(TestContext, string?)"/> per completed line.</summary>
    public static TextWriter CreateWriter(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        return new LineWriter(testContext);
    }

    /// <summary>The marker line for <paramref name="text"/> (exposed for tests).</summary>
    public static string FormatMarker(TestContext testContext, string text, bool error = false) =>
        LabcoatLiveOutput.FormatMarker(LabcoatMSTest.ToLabcoatTest(testContext), text, error);

    private sealed class LineWriter(TestContext testContext) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                {
                    var line = _line.ToString().TrimEnd('\r');
                    _line.Clear();
                    LabcoatOutput.WriteLine(testContext, line);
                }
                else _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void Flush()
        {
            lock (_line)
            {
                if (_line.Length == 0) return;
                var line = _line.ToString();
                _line.Clear();
                LabcoatOutput.WriteLine(testContext, line);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Flush();
            base.Dispose(disposing);
        }
    }
}
