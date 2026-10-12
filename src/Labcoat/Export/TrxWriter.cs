using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Labcoat;

/// <summary>
/// Writes a run as a VSTest TRX file (Visual Studio, <c>dotnet test --logger trx</c>, Azure Pipelines
/// <c>PublishTestResults</c> "VSTest"). Namespace <c>http://microsoft.com/schemas/VisualStudio/TeamTest/2010</c>:
/// <code>
/// TestRun id name
///   Times creation queuing start finish
///   TestSettings name id
///   Results/UnitTestResult   executionId testId testName duration startTime endTime outcome testListId …
///     Output  StdOut StdErr ErrorInfo(Message StackTrace)
///   TestDefinitions/UnitTest name id  Execution id  TestMethod className name
///   TestEntries/TestEntry    testId executionId testListId
///   TestLists/TestList
///   ResultSummary outcome  Counters  Output  RunInfos
/// </code>
/// Outcomes: Passed, Failed (assertion failures and unexpected exceptions), Timeout, NotExecuted (skipped, not run,
/// cancelled, quarantined failures). Ids are derived from the run and test ids, so the same run gives the same file.
/// Text is cleaned like the JUnit report's; console output is cut to <see cref="JUnitWriter.MaxOutputChars"/> per stream.
/// </summary>
public static class TrxWriter
{
    private const string Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    /// <summary>The "unit test" test type of VSTest.</summary>
    private const string UnitTestType = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b";
    /// <summary>The built-in "Results Not in a List" list every result belongs to.</summary>
    private const string NotInAList = "8c84fa94-04c1-424b-9868-57a2d4851a1d";
    private const string AllLoaded = "19431567-8539-422a-85d7-44ee4e166bda";
    private const string Executor = "executor://labcoat/";

    private static readonly XmlWriterSettings Settings = new()
    {
        Indent = true,
        Encoding = new UTF8Encoding(false),
        NewLineChars = "\n",
        CheckCharacters = true,
    };

    /// <summary>Writes the report as UTF-8 to <paramref name="stream"/> (left open).</summary>
    public static void Write(RunExport export, Stream stream)
    {
        using var w = XmlWriter.Create(stream, Settings);
        var run = export.Run;
        var start = Time(run.StartedAt);
        var finish = run.FinishedAt is { } f ? Time(f) : start;
        var runId = Guid(run.RunId);
        w.WriteStartDocument();
        w.WriteStartElement("TestRun", Ns);
        w.WriteAttributeString("id", runId);
        w.WriteAttributeString("name", ExportText.Clean($"Labcoat {export.WorkspaceName} {start}"));
        w.WriteAttributeString("runUser", "");

        w.WriteStartElement("Times");
        w.WriteAttributeString("creation", start);
        w.WriteAttributeString("queuing", start);
        w.WriteAttributeString("start", start);
        w.WriteAttributeString("finish", finish);
        w.WriteEndElement();

        w.WriteStartElement("TestSettings");
        w.WriteAttributeString("name", "default");
        w.WriteAttributeString("id", Guid(run.RunId + "/settings"));
        w.WriteEndElement();

        var projects = export.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var rows = export.Tests.Select(t => (Test: t, Ids: Ids(run.RunId, t))).ToList();

        w.WriteStartElement("Results");
        foreach (var (t, ids) in rows) WriteResult(w, t, ids, start, finish);
        w.WriteEndElement();

        w.WriteStartElement("TestDefinitions");
        foreach (var (t, ids) in rows)
        {
            var assembly = projects.TryGetValue(t.ProjectId, out var p) ? p.Name : t.ProjectId;
            w.WriteStartElement("UnitTest");
            w.WriteAttributeString("name", ExportText.Clean(t.Name));
            w.WriteAttributeString("storage", ExportText.Clean(assembly + ".dll").ToLowerInvariant());
            w.WriteAttributeString("id", ids.Test);
            w.WriteStartElement("Execution");
            w.WriteAttributeString("id", ids.Execution);
            w.WriteEndElement();
            w.WriteStartElement("TestMethod");
            w.WriteAttributeString("codeBase", ExportText.Clean(assembly + ".dll"));
            w.WriteAttributeString("adapterTypeName", Executor + "mtp");
            w.WriteAttributeString("className", ExportText.Clean($"{(t.ClassFullName.Length > 0 ? t.ClassFullName : assembly)}, {assembly}"));
            w.WriteAttributeString("name", ExportText.Clean(t.Name));
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("TestEntries");
        foreach (var (_, ids) in rows)
        {
            w.WriteStartElement("TestEntry");
            w.WriteAttributeString("testId", ids.Test);
            w.WriteAttributeString("executionId", ids.Execution);
            w.WriteAttributeString("testListId", NotInAList);
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("TestLists");
        WriteList(w, "Results Not in a List", NotInAList);
        WriteList(w, "All Loaded Results", AllLoaded);
        w.WriteEndElement();

        WriteSummary(w, export);
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    /// <summary>The report as a string (tests, small exports).</summary>
    public static string ToXml(RunExport export)
    {
        using var ms = new MemoryStream();
        Write(export, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteList(XmlWriter w, string name, string id)
    {
        w.WriteStartElement("TestList");
        w.WriteAttributeString("name", name);
        w.WriteAttributeString("id", id);
        w.WriteEndElement();
    }

    private static void WriteResult(XmlWriter w, ExportTest t, (string Test, string Execution) ids, string start, string finish)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, t.DurationMs ?? 0));
        w.WriteStartElement("UnitTestResult");
        w.WriteAttributeString("executionId", ids.Execution);
        w.WriteAttributeString("testId", ids.Test);
        w.WriteAttributeString("testName", ExportText.Clean(t.Name));
        w.WriteAttributeString("computerName", Environment.MachineName);
        w.WriteAttributeString("duration", duration.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture));
        w.WriteAttributeString("startTime", start);
        w.WriteAttributeString("endTime", finish);
        w.WriteAttributeString("testType", UnitTestType);
        w.WriteAttributeString("outcome", Outcome(t));
        w.WriteAttributeString("testListId", NotInAList);
        w.WriteAttributeString("relativeResultsDirectory", ids.Execution);

        var failed = ExportKinds.IsFailed(t.Kind);
        var output = Cap(t.StdOut);
        var error = Cap(t.StdErr);
        var skipReason = !failed && t.Kind is not ExportKind.Passed ? ExportKinds.SkipReason(t) : null;
        if (output.Length > 0 || error.Length > 0 || failed || skipReason is not null)
        {
            w.WriteStartElement("Output");
            if (output.Length > 0) w.WriteElementString("StdOut", output);
            if (error.Length > 0) w.WriteElementString("StdErr", error);
            if (failed || skipReason is not null)
            {
                w.WriteStartElement("ErrorInfo");
                var message = failed ? ExportText.Cap(ExportText.Clean(t.ErrorMessage), JUnitWriter.MaxMessageChars) : skipReason!;
                w.WriteElementString("Message", message.Length > 0 ? message : t.Outcome == Labcoat.Outcome.TimedOut ? "Timed out" : "Failed");
                if (failed && !string.IsNullOrWhiteSpace(t.StackTrace))
                    w.WriteElementString("StackTrace", ExportText.Cap(ExportText.Clean(t.StackTrace), JUnitWriter.MaxStackChars));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteSummary(XmlWriter w, RunExport export)
    {
        var kinds = export.Tests.Select(t => t.Kind).ToList();
        var passed = kinds.Count(k => k == ExportKind.Passed);
        var failed = kinds.Count(ExportKinds.IsFailed) - export.Tests.Count(t => t.Outcome == Labcoat.Outcome.TimedOut && ExportKinds.IsFailed(t.Kind));
        var timeout = export.Tests.Count(t => t.Outcome == Labcoat.Outcome.TimedOut && ExportKinds.IsFailed(t.Kind));
        var notExecuted = kinds.Count - passed - failed - timeout;
        var bad = failed + timeout > 0 || export.Run.Aborted == true;

        w.WriteStartElement("ResultSummary");
        w.WriteAttributeString("outcome", bad ? "Failed" : "Completed");
        w.WriteStartElement("Counters");
        Counter(w, "total", kinds.Count);
        Counter(w, "executed", passed + failed + timeout);
        Counter(w, "passed", passed);
        Counter(w, "error", 0);
        Counter(w, "failed", failed);
        Counter(w, "timeout", timeout);
        Counter(w, "aborted", 0);
        Counter(w, "inconclusive", 0);
        Counter(w, "passedButRunAborted", 0);
        Counter(w, "notRunnable", 0);
        Counter(w, "notExecuted", notExecuted);
        Counter(w, "disconnected", 0);
        Counter(w, "warning", 0);
        Counter(w, "completed", 0);
        Counter(w, "inProgress", 0);
        Counter(w, "pending", 0);
        w.WriteEndElement();
        if (export.Run.Aborted == true || export.Run.Cancelled == true)
        {
            w.WriteStartElement("RunInfos");
            w.WriteStartElement("RunInfo");
            w.WriteAttributeString("computerName", Environment.MachineName);
            w.WriteAttributeString("outcome", "Warning");
            w.WriteAttributeString("timestamp", Time(export.Run.FinishedAt ?? export.Run.StartedAt));
            w.WriteElementString("Text", export.Run.Aborted == true
                ? ExportText.Clean("Run aborted: " + (export.Run.AbortReason ?? "a test host exited"))
                : "Run cancelled");
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void Counter(XmlWriter w, string name, int value) =>
        w.WriteAttributeString(name, value.ToString(CultureInfo.InvariantCulture));

    private static string Outcome(ExportTest t) => t.Kind switch
    {
        ExportKind.Passed => "Passed",
        ExportKind.Failure or ExportKind.Error => t.Outcome == Labcoat.Outcome.TimedOut ? "Timeout" : "Failed",
        _ => "NotExecuted",
    };

    private static string Cap(string? s) => ExportText.Cap(ExportText.Clean(s), JUnitWriter.MaxOutputChars);

    private static (string Test, string Execution) Ids(string runId, ExportTest t) =>
        (Guid($"{t.ProjectId}\n{t.Id}"), Guid($"{runId}\n{t.ProjectId}\n{t.Id}"));

    /// <summary>A stable GUID of <paramref name="seed"/> (the first 16 bytes of its SHA-256).</summary>
    private static string Guid(string seed) =>
        new System.Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16)).ToString();

    /// <summary>ISO 8601 with offset, as VSTest writes it ("2026-10-11T08:15:30.1230000+00:00").</summary>
    private static string Time(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)
            : iso;
}
