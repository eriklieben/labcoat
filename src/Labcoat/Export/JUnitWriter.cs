using System.Globalization;
using System.Text;
using System.Xml;

namespace Labcoat;

/// <summary>
/// Writes a run as JUnit XML, the de-facto CI test report format (GitHub dorny/test-reporter "java-junit", Azure
/// Pipelines PublishTestResults "JUnit", GitLab <c>artifacts:reports:junit</c>, Jenkins). Valid against the Jenkins
/// xunit plugin's junit-10.xsd (the Surefire schema; tests/Labcoat.Tests/Fixtures/JUnit):
/// <code>
/// testsuites name tests failures errors time
///   testsuite (one per test project) name tests failures errors skipped time timestamp id package
///     properties/property  labcoat.version, labcoat.runId, labcoat.trigger, labcoat.scope, labcoat.randomSeed (random-order runs), targetFramework, …
///     testcase classname="Namespace.Class" name="display name (data row)" time="seconds"
///       failure | error  message type, text = stack trace      (assertion failures vs unexpected exceptions / timeouts)
///       skipped message                                        (skipped, not run, cancelled, quarantined failures)
///       system-out / system-err                                (capped; per test case, as Azure reads them)
/// </code>
/// Text is cleaned of ANSI sequences and characters XML 1.0 cannot carry; console output is cut to
/// <see cref="MaxOutputChars"/> per stream and <see cref="MaxTotalOutputChars"/> per report (failed tests first).
/// </summary>
public static class JUnitWriter
{
    /// <summary>Console output per stream and test case (head + tail).</summary>
    public const int MaxOutputChars = 16_384;
    /// <summary>Console output in the whole report: failed tests get theirs first, then the others in order.</summary>
    public const int MaxTotalOutputChars = 8 * 1024 * 1024;
    /// <summary>The message attribute of failure / error / skipped.</summary>
    public const int MaxMessageChars = 4096;
    /// <summary>The stack trace (the text of failure / error).</summary>
    public const int MaxStackChars = 32_768;

    private const string NameAttr = "name";
    private const string MessageAttr = "message";
    private const string TypeAttr = "type";

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
        w.WriteStartDocument();
        var withOutput = OutputBudget(export.Tests);
        var all = export.Tests.Select(t => t.Kind).ToList();
        w.WriteStartElement("testsuites");
        w.WriteAttributeString(NameAttr, Text($"Labcoat {export.WorkspaceName}"));
        w.WriteAttributeString("tests", Int(all.Count));
        w.WriteAttributeString("failures", Int(all.Count(k => k == ExportKind.Failure)));
        w.WriteAttributeString("errors", Int(all.Count(k => k == ExportKind.Error)));
        w.WriteAttributeString("time", Seconds(export.Run.DurationMs ?? export.Tests.Sum(t => t.DurationMs ?? 0)));
        var byProject = export.Tests.ToLookup(t => t.ProjectId, StringComparer.Ordinal);
        var index = 0;
        foreach (var project in export.Projects)
            WriteSuite(w, export, project, byProject[project.Id].ToList(), index++, withOutput);
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

    private static void WriteSuite(XmlWriter w, RunExport export, ExportProject project, List<ExportTest> tests, int index, HashSet<ExportTest> withOutput)
    {
        w.WriteStartElement("testsuite");
        w.WriteAttributeString(NameAttr, Text(project.Name));
        w.WriteAttributeString("tests", Int(tests.Count));
        w.WriteAttributeString("failures", Int(tests.Count(t => t.Kind == ExportKind.Failure)));
        w.WriteAttributeString("errors", Int(tests.Count(t => t.Kind == ExportKind.Error)));
        w.WriteAttributeString("skipped", Int(tests.Count(t => t.Kind is ExportKind.Skipped or ExportKind.NotRun or ExportKind.QuarantinedFailure)));
        w.WriteAttributeString("time", Seconds(tests.Sum(t => t.DurationMs ?? 0)));
        w.WriteAttributeString("timestamp", Timestamp(export.Run.StartedAt));
        w.WriteAttributeString("id", Int(index));
        w.WriteAttributeString("package", Text(project.Id));

        w.WriteStartElement("properties");
        foreach (var (name, value) in Properties(export, project))
        {
            w.WriteStartElement("property");
            w.WriteAttributeString(NameAttr, name);
            w.WriteAttributeString("value", Text(value));
            w.WriteEndElement();
        }
        w.WriteEndElement();

        foreach (var t in tests) WriteCase(w, project, t, withOutput.Contains(t));
        w.WriteEndElement();
    }

    private static IEnumerable<(string Name, string Value)> Properties(RunExport export, ExportProject project)
    {
        var run = export.Run;
        yield return ("labcoat.version", export.Version);
        yield return ("labcoat.runId", run.RunId);
        yield return ("labcoat.trigger", run.Trigger ?? "user");
        yield return ("labcoat.scope", run.Scope ?? "all");
        if (run.Seed is { } seed) yield return ("labcoat.randomSeed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return ("labcoat.startedAt", run.StartedAt);
        if (run.FinishedAt is { } finished) yield return ("labcoat.finishedAt", finished);
        yield return ("labcoat.workspace", export.WorkspaceName);
        yield return ("labcoat.projectId", project.Id);
        if (project.Path is { } path) yield return ("labcoat.project", path);
        if (project.TargetFramework is { } tfm) yield return ("targetFramework", tfm);
        if (run.Aborted == true) yield return ("labcoat.aborted", run.AbortReason ?? "true");
        if (run.Cancelled == true) yield return ("labcoat.cancelled", "true");
    }

    private static void WriteCase(XmlWriter w, ExportProject project, ExportTest t, bool output)
    {
        w.WriteStartElement("testcase");
        w.WriteAttributeString(NameAttr, Text(t.Name));
        var classname = t.ClassFullName;
        w.WriteAttributeString("classname", Text(classname.Length > 0 ? classname : project.Name));
        w.WriteAttributeString("time", Seconds(t.DurationMs ?? 0));
        switch (t.Kind)
        {
            case ExportKind.Failure:
                WriteProblem(w, "failure", t, t.ErrorType ?? "AssertionFailure");
                break;
            case ExportKind.Error:
                WriteProblem(w, "error", t, t.ErrorType ?? DefaultErrorType(t));
                break;
            case ExportKind.Skipped or ExportKind.NotRun or ExportKind.QuarantinedFailure:
                w.WriteStartElement("skipped");
                w.WriteAttributeString(MessageAttr, Text(ExportKinds.SkipReason(t)));
                w.WriteEndElement();
                break;
        }
        if (output)
        {
            WriteOutput(w, "system-out", t.StdOut);
            WriteOutput(w, "system-err", t.StdErr);
        }
        w.WriteEndElement();
    }

    /// <summary>&lt;failure&gt; / &lt;error&gt;: the message as attribute (what reporters show as the title), the stack trace as text.</summary>
    private static void WriteProblem(XmlWriter w, string element, ExportTest t, string type)
    {
        w.WriteStartElement(element);
        var message = Text(ExportText.Cap(ExportText.Clean(t.ErrorMessage), MaxMessageChars));
        w.WriteAttributeString(MessageAttr, message.Length > 0 ? message : DefaultMessage(t));
        w.WriteAttributeString(TypeAttr, Text(type));
        var body = string.IsNullOrWhiteSpace(t.StackTrace) ? t.ErrorMessage : t.StackTrace;
        w.WriteString(ExportText.Cap(ExportText.Clean(body), MaxStackChars));
        w.WriteEndElement();
    }

    private static string DefaultMessage(ExportTest t) => t.Outcome == Outcome.TimedOut ? "Timed out" : "Failed";

    private static string DefaultErrorType(ExportTest t) => t.Outcome == Outcome.TimedOut ? "Timeout" : "Error";

    private static void WriteOutput(XmlWriter w, string element, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        w.WriteElementString(element, ExportText.Cap(ExportText.Clean(text), MaxOutputChars));
    }

    /// <summary>The tests whose console output fits into <see cref="MaxTotalOutputChars"/>: failed ones first, then in order.</summary>
    internal static HashSet<ExportTest> OutputBudget(IReadOnlyList<ExportTest> tests, int total = MaxTotalOutputChars, int perStream = MaxOutputChars)
    {
        var chosen = new HashSet<ExportTest>(ReferenceEqualityComparer.Instance);
        var left = total;
        foreach (var t in tests.Where(t => ExportKinds.IsFailed(t.Kind)).Concat(tests.Where(t => !ExportKinds.IsFailed(t.Kind))))
        {
            var size = Math.Min(t.StdOut?.Length ?? 0, perStream) + Math.Min(t.StdErr?.Length ?? 0, perStream);
            if (size == 0 || size > left) continue;
            left -= size;
            chosen.Add(t);
        }
        return chosen;
    }

    private static string Text(string? s) => ExportText.Clean(s);

    private static string Int(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Seconds with up to 3 decimals ("0.042"): Surefire's time format.</summary>
    internal static string Seconds(double ms) => (Math.Max(0, ms) / 1000).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>ISO 8601 in UTC without a zone ("2026-10-11T08:15:00"), as Ant / Surefire write it.</summary>
    internal static string Timestamp(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : iso;
}
