using System.Text;
using NUnit.Framework;

namespace Labcoat.Testing;

/// <summary>
/// Live console output for NUnit: lines written here appear in Labcoat's detail pane while the test is still running,
/// and in the test's normal output (<see cref="TestContext.Out"/>) once it finishes.
/// </summary>
/// <remarks>
/// <para>
/// NUnit attaches a test's output to its result only when the test ends; <see cref="TestContext.Progress"/> is
/// immediate but goes to the test host's console without saying which test wrote it (Labcoat then guesses by timing).
/// Under Labcoat (the test host has <c>LABCOAT_TEST_HOST=1</c>) each line written here is also sent live with
/// <see cref="LabcoatLiveOutput"/>, tagged with the test's id (its fully qualified name), class, method and name, so
/// Labcoat shows it under that exact test, also for data rows and parallel tests. Outside Labcoat this is just
/// <see cref="TestContext.Out"/>. Works without the tracing setup.
/// </para>
/// <code>
/// LabcoatOutput.WriteLine("Starting the Aspire app…");
/// using var log = LabcoatOutput.CreateWriter();   // a TextWriter, e.g. for a logger that writes to a TextWriter
/// </code>
/// </remarks>
public static class LabcoatOutput
{
    /// <summary>True when the tests run under Labcoat (the live markers are written).</summary>
    public static bool IsEnabled => LabcoatLiveOutput.IsEnabled;

    /// <summary>Writes a line to the current test's output, and live to Labcoat.</summary>
    public static void WriteLine(string? message)
    {
        TestContext.Out.WriteLine(message);
        Send(message, error: false);
    }

    /// <summary>Writes a line to the current test's error output (<see cref="TestContext.Error"/>), and live to Labcoat.</summary>
    public static void WriteErrorLine(string? message)
    {
        TestContext.Error.WriteLine(message);
        Send(message, error: true);
    }

    /// <summary>
    /// A <see cref="TextWriter"/> that does <see cref="WriteLine(string?)"/> per completed line, for the test that is
    /// current when it is created (also from other threads the test starts).
    /// </summary>
    public static TextWriter CreateWriter() => new LineWriter(CurrentTest(), TestContext.Out);

    /// <summary>The current test as Labcoat.Testing describes it (null outside a test).</summary>
    internal static LabcoatTest? CurrentTest()
    {
        var test = TestContext.CurrentContext.Test;
        if (string.IsNullOrEmpty(test.FullName) || test.MethodName is null) return null;
        return LabcoatTracingAttribute.Describe(test.Name, test.FullName, test.ClassName, test.MethodName);
    }

    private static void Send(string? message, bool error)
    {
        if (!LabcoatLiveOutput.IsEnabled || CurrentTest() is not { } test) return;
        LabcoatLiveOutput.Send(test, (message ?? "") + "\n", error);
    }

    private sealed class LineWriter(LabcoatTest? test, TextWriter output) : TextWriter
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
                    Emit(line);
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
                Emit(line);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Flush();
            base.Dispose(disposing);
        }

        private void Emit(string line)
        {
            output.WriteLine(line);
            if (test is not null) LabcoatLiveOutput.Send(test, line + "\n");
        }
    }
}
