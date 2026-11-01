using System.Text;

namespace Labcoat.Export;

/// <summary>
/// What <c>--run</c> reports to GitHub Actions on its own: a Markdown summary appended to the file
/// <c>$GITHUB_STEP_SUMMARY</c> names (<see cref="MarkdownReport"/>, cut to GitHub's 1 MiB limit) and workflow-command
/// annotations (<c>::error file=…,line=…,title=…::message</c>) for failed tests at their source location, at most
/// <see cref="MaxAnnotationsPerKind"/> of each kind. Both read the environment through a lookup so tests need no process
/// variables.
/// </summary>
public static class GitHubActions
{
    /// <summary>
    /// GitHub shows at most 10 error, 10 warning and 10 notice annotations per step (and 50 per job); more are dropped
    /// silently.
    /// </summary>
    public const int MaxAnnotationsPerKind = 10;
    /// <summary>The message of one annotation.</summary>
    public const int MaxMessageChars = 2_000;

    /// <summary>Running under GitHub Actions (<c>GITHUB_ACTIONS=true</c>).</summary>
    public static bool IsActive(Func<string, string?> env) =>
        string.Equals(env("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The step summary file, when GitHub Actions provides one.</summary>
    public static string? StepSummaryPath(Func<string, string?> env) =>
        env("GITHUB_STEP_SUMMARY") is { Length: > 0 } path ? path : null;

    /// <summary>
    /// Appends the Markdown summary of <paramref name="export"/> (with <paramref name="coverage"/> when collected) to
    /// <paramref name="path"/>: the file may hold the summaries of earlier steps, so what is left of the 1 MiB budget is
    /// what this one may use. Returns false when nothing was written because no room is left.
    /// </summary>
    public static bool AppendStepSummary(string path, RunExport export, CoverageReport? coverage)
    {
        var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        var room = (int)Math.Min(MarkdownReport.MaxStepSummaryBytes - existing - 2, MarkdownReport.MaxStepSummaryBytes);
        if (room < 2_000) return false;
        var text = MarkdownReport.ToMarkdown(export, coverage, room);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = new UTF8Encoding(false).GetBytes(text + "\n");
        stream.Write(bytes, 0, bytes.Length);
        return true;
    }

    /// <summary>
    /// The workflow commands annotating the failed tests (<see cref="ExportKinds.IsFailed"/>; quarantined failures are
    /// not failures of the run) as <c>::error</c> lines, then the flaky tests of a <c>--retry-failed</c> run as
    /// <c>::warning</c>s at their first failure: up to <paramref name="max"/> of each, then a <c>::notice</c> counting the
    /// rest.
    /// </summary>
    public static List<string> Annotations(RunExport export, int max = MaxAnnotationsPerKind)
    {
        var failed = export.Tests.Where(t => ExportKinds.IsFailed(t.Kind)).ToList();
        var flaky = export.Tests.Where(t => t.IsFlaky).ToList();
        var lines = failed.Take(max).Select(Annotation).Concat(flaky.Take(max).Select(FlakyAnnotation)).ToList();
        var failedLeft = Math.Max(failed.Count - max, 0);
        var flakyLeft = Math.Max(flaky.Count - max, 0);
        if (failedLeft + flakyLeft == 0) return lines;
        var left = string.Join(" and ", new[]
        {
            failedLeft > 0 ? $"{failedLeft} more failed test{Words.Plural(failedLeft)}" : null,
            flakyLeft > 0 ? $"{flakyLeft} more flaky test{Words.Plural(flakyLeft)}" : null,
        }.OfType<string>());
        lines.Add($"::notice title={Property(failedLeft > 0 ? "More failed tests" : "More flaky tests")}::{Data($"{left} not annotated (GitHub shows {max} of each kind per step; the job summary lists them all).")}");
        return lines;
    }

    internal static string Annotation(ExportTest t) => Command("error", t, Message(t.ErrorMessage, t.Outcome));

    /// <summary>A test that passed on a retry, at its first failure (the location the export kept from it).</summary>
    private static string FlakyAnnotation(ExportTest t)
    {
        var first = t.FailedAttempts?.FirstOrDefault();
        return Command("warning", t, $"Flaky: passed on attempt {t.Attempts}; first failure: {Message(first?.ErrorMessage, first?.Outcome ?? Outcome.Failed)}");
    }

    private static string Message(string? errorMessage, Outcome outcome)
    {
        var message = ExportText.Cap(ExportText.Clean(errorMessage).Trim(), MaxMessageChars);
        if (message.Length > 0) return message;
        return outcome == Outcome.TimedOut ? "Timed out" : "Failed";
    }

    private static string Command(string command, ExportTest t, string message)
    {
        var sb = new StringBuilder("::").Append(command).Append(' ');
        if (t.SourceFile is { Length: > 0 } file)
        {
            sb.Append("file=").Append(Property(file)).Append(',');
            if (t.SourceLine is > 0 and var line) sb.Append("line=").Append(line).Append(',');
        }
        var cls = t.ClassFullName;
        sb.Append("title=").Append(Property(cls.Length > 0 ? $"{cls}.{t.Name}" : t.Name));
        return sb.Append("::").Append(Data(message)).ToString();
    }

    /// <summary>Message text of a workflow command: '%', CR and LF are percent-encoded.</summary>
    internal static string Data(string s) =>
        ExportText.Clean(s).Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    /// <summary>A property value (file, title): as <see cref="Data"/>, plus ':' and ','.</summary>
    internal static string Property(string s) => Data(s).Replace(":", "%3A").Replace(",", "%2C");
}
