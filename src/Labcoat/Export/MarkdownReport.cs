using System.Globalization;
using System.Text;

namespace Labcoat.Export;

/// <summary>
/// Writes a run as GitHub-flavored Markdown: the verdict and totals, the failed tests first (message, location, stack
/// trace in a collapsed block), the flaky tests of a <c>--retry-failed</c> run, the slowest tests and, when collected,
/// the coverage. The same text serves as a PR comment
/// (<see cref="MaxCommentBytes"/>, under GitHub's 65,536 character limit) and as the <c>$GITHUB_STEP_SUMMARY</c> of
/// <c>--run</c> (<see cref="MaxStepSummaryBytes"/>, under its 1 MiB limit). When the report would be longer, the
/// remaining failed tests are left out and a note says so. Test data is escaped: it can neither inject markup nor
/// mention a GitHub user.
/// </summary>
public static class MarkdownReport
{
    /// <summary>A PR comment may hold 65,536 characters; UTF-8 bytes are the safe upper bound of that.</summary>
    public const int MaxCommentBytes = 60_000;
    /// <summary>GitHub rejects a step summary over 1 MiB (1,048,576 bytes).</summary>
    public const int MaxStepSummaryBytes = 1_000_000;
    /// <summary>The message of a failed test.</summary>
    public const int MaxMessageChars = 1_500;
    /// <summary>The stack trace of a failed test.</summary>
    public const int MaxStackChars = 3_000;
    /// <summary>How many of the slowest tests are listed.</summary>
    public const int SlowestCount = 10;
    /// <summary>How many flaky tests are listed; the rest are counted.</summary>
    public const int FlakyCount = 20;

    /// <summary>Room kept for the note that counts the failed tests left out.</summary>
    private const int NoteBytes = 400;

    /// <summary>Writes the report as UTF-8 to <paramref name="stream"/> (left open), sized for a PR comment.</summary>
    public static void Write(RunExport export, Stream stream)
    {
        var bytes = new UTF8Encoding(false).GetBytes(ToMarkdown(export));
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>The report as a string (<see cref="MaxCommentBytes"/> unless <paramref name="maxBytes"/> says otherwise).</summary>
    public static string ToMarkdown(RunExport export, CoverageReport? coverage = null, int maxBytes = MaxCommentBytes)
    {
        var sb = new StringBuilder();
        var failed = export.Tests.Where(t => ExportKinds.IsFailed(t.Kind)).ToList();
        Header(sb, export, failed.Count);

        var tail = new StringBuilder();
        Flaky(tail, export);
        Slowest(tail, export);
        Coverage(tail, coverage);
        Footer(tail, export);

        // The failed tests get what the fixed parts leave, one by one; the rest is counted in a note.
        var budget = maxBytes - Bytes(sb.ToString()) - Bytes(tail.ToString()) - NoteBytes;
        var shown = 0;
        if (failed.Count > 0)
        {
            sb.Append("### Failed tests\n\n");
            foreach (var t in failed)
            {
                var block = Failed(t);
                var size = Bytes(block);
                if (size > budget) break;
                budget -= size;
                sb.Append(block);
                shown++;
            }
            var left = failed.Count - shown;
            if (left > 0)
                sb.Append($"_{Count(left)} more failed test{Words.Plural(left)} not shown here (report size limit); the HTML report lists them all._\n\n");
        }
        return sb.Append(tail).ToString();
    }

    private static void Header(StringBuilder sb, RunExport export, int failedCount)
    {
        var run = export.Run;
        sb.Append($"## Test results: {Verdict(export, failedCount)}\n\n");
        if (run.Aborted == true && !string.IsNullOrWhiteSpace(run.AbortReason))
            sb.Append($"> {Inline(ExportText.FirstLine(run.AbortReason, 500))}\n\n");

        var passed = Count(export, ExportKind.Passed);
        var skipped = export.Tests.Count(t => t.Kind is ExportKind.Skipped or ExportKind.NotRun);
        var quarantined = Count(export, ExportKind.QuarantinedFailure);
        var duration = HtmlReport.Duration(run.DurationMs ?? export.Tests.Sum(t => t.DurationMs ?? 0));
        var flaky = export.Flaky;
        sb.Append("| Total | Passed | Failed | Skipped |").Append(flaky > 0 ? " Flaky |" : "").Append(quarantined > 0 ? " Quarantined |" : "").Append(" Duration |\n");
        sb.Append("| ---: | ---: | ---: | ---: |").Append(flaky > 0 ? " ---: |" : "").Append(quarantined > 0 ? " ---: |" : "").Append(" ---: |\n");
        sb.Append($"| {export.Tests.Count} | {passed} | {failedCount} | {skipped} |")
            .Append(flaky > 0 ? $" {flaky} |" : "").Append(quarantined > 0 ? $" {quarantined} |" : "").Append($" {duration} |\n\n");
        // a failure that only shows in this order needs the seed to reproduce it, and a CI job summary is where it is read
        if (run.Seed is { } seed)
        {
            var s = seed.ToString(CultureInfo.InvariantCulture);
            sb.Append($"Random order, seed {s}: rerun with `--order random --seed {s}`.\n\n");
        }
    }

    private static string Verdict(RunExport export, int failedCount)
    {
        if (export.Run.Aborted == true) return "Run aborted";
        if (export.Run.Cancelled == true) return "Run cancelled";
        if (failedCount > 0) return $"{Count(failedCount)} failed";
        if (export.Tests.Count == 0) return "No tests ran";
        return export.Flaky > 0 ? $"No failures, {Count(export.Flaky)} flaky" : "All tests passed";
    }

    /// <summary>One failed test: its name and location, the message and the stack trace folded away.</summary>
    private static string Failed(ExportTest t)
    {
        var sb = new StringBuilder();
        var cls = t.ClassFullName;
        sb.Append($"**{Inline(t.Name)}**");
        if (cls.Length > 0) sb.Append($" in {Inline(cls)}");
        if (t.SourceFile is { Length: > 0 } file)
            sb.Append($" (`{Code(file + (t.SourceLine is { } line ? ":" + line.ToString(CultureInfo.InvariantCulture) : ""))}`)");
        sb.Append("\n\n");
        if (t.Attempts > 1) sb.Append($"Failed all {t.Attempts} attempts (this is the first).\n\n");
        var message = ExportText.Cap(ExportText.Clean(t.ErrorMessage).Trim(), MaxMessageChars);
        if (message.Length == 0) message = t.Outcome == Outcome.TimedOut ? "Timed out" : "Failed";
        sb.Append(Fence(message)).Append('\n');
        if (!string.IsNullOrWhiteSpace(t.StackTrace))
            sb.Append("<details><summary>Stack trace</summary>\n\n")
              .Append(Fence(ExportText.Cap(ExportText.Clean(t.StackTrace).Trim(), MaxStackChars)))
              .Append("\n</details>\n\n");
        return sb.ToString();
    }

    /// <summary>The tests that passed only on a --retry-failed retry: they count as passed, but deserve a look.</summary>
    private static void Flaky(StringBuilder sb, RunExport export)
    {
        var flaky = export.Tests.Where(t => t.IsFlaky).ToList();
        if (flaky.Count == 0) return;
        sb.Append("### Flaky tests\n\nPassed on a retry (`--retry-failed`), so they don't fail the run.\n\n| Test | Passed on attempt | First failure |\n| --- | ---: | --- |\n");
        foreach (var t in flaky.Take(FlakyCount))
        {
            var cls = t.ClassFullName;
            var first = t.FailedAttempts?.FirstOrDefault() is { } f ? Inline(ExportText.FirstLine(f.ErrorMessage, 200)) : "";
            sb.Append($"| {Inline(cls.Length > 0 ? cls + "." + t.Name : t.Name)} | {t.Attempts} | {first} |\n");
        }
        if (flaky.Count > FlakyCount) sb.Append($"\n_{flaky.Count - FlakyCount} more flaky test{Words.Plural(flaky.Count - FlakyCount)}._\n");
        sb.Append('\n');
    }

    private static void Slowest(StringBuilder sb, RunExport export)
    {
        var slow = export.Tests.Where(t => t.DurationMs is > 0 && t.Kind is ExportKind.Passed or ExportKind.Failure or ExportKind.Error)
            .OrderByDescending(t => t.DurationMs).Take(SlowestCount).ToList();
        if (slow.Count == 0) return;
        sb.Append("### Slowest tests\n\n| Test | Duration |\n| --- | ---: |\n");
        foreach (var t in slow)
        {
            var cls = t.ClassFullName;
            sb.Append($"| {Inline(cls.Length > 0 ? cls + "." + t.Name : t.Name)} | {HtmlReport.Duration(t.DurationMs ?? 0)} |\n");
        }
        sb.Append('\n');
    }

    private static void Coverage(StringBuilder sb, CoverageReport? c)
    {
        if (c is null) return;
        sb.Append("### Coverage\n\n");
        if (c.NoCoverableLines == true)
        {
            sb.Append("No coverable lines.\n\n");
            return;
        }
        sb.Append("| Lines | Branches | Files |\n| ---: | ---: | ---: |\n");
        var lines = $"{Percent(c.LineRate)} ({Count(c.LinesCovered)} of {Count(c.LinesValid)})";
        var branches = c.BranchesValid > 0 ? $"{Percent(c.BranchRate)} ({Count(c.BranchesCovered)} of {Count(c.BranchesValid)})" : "n/a";
        sb.Append($"| {lines} | {branches} | {Count(c.Files.Count)} |\n\n");
    }

    private static void Footer(StringBuilder sb, RunExport export) =>
        sb.Append($"<sub>Labcoat {Inline(export.Version)} · run {Inline(export.Run.RunId)} · {Inline(export.WorkspaceName)}</sub>\n");

    /// <summary>"1,234": the same in every culture.</summary>
    private static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static int Count(RunExport export, ExportKind kind) => export.Tests.Count(t => t.Kind == kind);

    private static string Percent(double rate) => (rate * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// Text inside a Markdown line: cleaned, on one line, markup characters backslash-escaped, and '@' broken up so a
    /// test name cannot mention a user or team.
    /// </summary>
    internal static string Inline(string? s)
    {
        var clean = ExportText.Clean(s).ReplaceLineEndings(" ").Trim();
        var sb = new StringBuilder(clean.Length + 8);
        foreach (var c in clean)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '~' or '&' or '#') sb.Append('\\').Append(c);
            else if (c == '@') sb.Append("@\u200B"); // a zero-width space: no mention
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Text inside a code span: the backticks and line breaks that would end it are replaced.</summary>
    private static string Code(string s) => ExportText.Clean(s).ReplaceLineEndings(" ").Replace('`', '\'');

    /// <summary>A fenced code block whose fence is longer than any backtick run in the text.</summary>
    internal static string Fence(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}text\n{text}\n{fence}\n";
    }
}
