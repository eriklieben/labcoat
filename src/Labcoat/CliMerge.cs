using System.Text;
using System.Xml;

namespace Labcoat;

/// <summary>What a report file holds (found by content, not by name).</summary>
public enum ReportKind { JUnit, Lcov, Cobertura }

/// <summary>
/// <c>labcoat merge --export format=file ... &lt;inputs...&gt;</c>: combines the per-shard report files of a split CI run
/// into one report. Builds and runs nothing. The inputs are JUnit XML, lcov and Cobertura files (found by content;
/// lcov and Cobertura merge into one coverage report that either format is written from), as paths or as a pattern
/// with <c>*</c> / <c>?</c> in the file name part (for shells that don't expand it). Exit codes:
/// <see cref="ExitPassed"/> when no merged test failed, <see cref="ExitFailed"/> when one failed or errored (or a
/// report could not be written), <see cref="ExitError"/> for a usage error or an input that is missing or unreadable.
/// The summary is where a coverage gate can follow (it already has the totals).
/// </summary>
public static class CliMerge
{
    public const int ExitPassed = CliRun.ExitPassed;
    public const int ExitFailed = CliRun.ExitFailed;
    public const int ExitError = CliRun.ExitBuildError;

    /// <summary>The formats <c>merge</c> writes: the ones that can be merged.</summary>
    public static readonly string[] Formats = [ExportFormats.JUnit, ExportFormats.Lcov, ExportFormats.Cobertura];

    private static string ErrorLabel => Cli.Red("error");

    /// <summary>
    /// Whether the command line is <c>merge ...</c>: its first argument is "merge" and no file or directory of that name
    /// is next to it (<c>./merge</c> still selects one, as a start path).
    /// </summary>
    public static bool IsMerge(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == "merge" && !File.Exists("merge") && !Directory.Exists("merge");

    /// <summary>The parsed <c>merge</c> arguments.</summary>
    public sealed record MergeOptions(IReadOnlyList<ExportTarget> Exports, IReadOnlyList<string> Inputs, bool Help = false);

    /// <summary>Parses the arguments after "merge"; null + <paramref name="error"/> for an invalid command line.</summary>
    public static MergeOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var exports = new List<ExportTarget>();
        var inputs = new List<string>();
        var onlyInputs = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (onlyInputs || !a.StartsWith('-') || a == "-") inputs.Add(a);
            else if (a == "--") onlyInputs = true;
            else if (a is "--help" or "-h" or "-?") return new MergeOptions([], [], Help: true);
            else if (a == "--export" || a.StartsWith("--export=", StringComparison.Ordinal))
            {
                var value = a == "--export" ? (i + 1 < args.Count ? args[++i] : null) : a["--export=".Length..];
                error = AddExport(exports, value);
                if (error is not null) return null;
            }
            else
            {
                error = $"merge: unknown option '{a}'.";
                return null;
            }
        }
        if (exports.Count == 0) error = "merge needs at least one --export, e.g. --export junit=out/results.xml.";
        else if (inputs.Count == 0) error = "merge needs the report files to merge, e.g. shard-1/results.xml shard-2/results.xml.";
        return error is null ? new MergeOptions(exports, inputs) : null;
    }

    private static string? AddExport(List<ExportTarget> exports, string? value)
    {
        const string Usage = "--export needs format=file, e.g. junit=out/results.xml (formats: junit, lcov, cobertura).";
        var eq = value?.IndexOf('=') ?? -1;
        if (value is null || eq <= 0 || eq == value.Length - 1) return Usage;
        var format = value[..eq].Trim().ToLowerInvariant();
        if (!Formats.Contains(format)) return $"--export: merge cannot write '{value[..eq]}'. {Usage}";
        exports.Add(new ExportTarget(format, value[(eq + 1)..].Trim()));
        return null;
    }

    /// <summary>Runs <c>merge</c> with the arguments after "merge"; returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        var options = Parse(args, out var error);
        if (options is null) return Fail(error! + " Use --help for usage.");
        if (options.Help)
        {
            Console.WriteLine(Cli.Usage);
            return ExitPassed;
        }
        var files = ExpandInputs(options.Inputs, options.Exports, out error);
        if (files is null) return Fail(error!);

        Console.WriteLine($"{Cli.Bold("Labcoat " + Cli.Version)} {Cli.Dim("merge")}: {files.Count} file{(files.Count == 1 ? "" : "s")}");
        var junit = new JUnitMerge();
        var coverage = new CoverageMerge();
        var counts = new Dictionary<ReportKind, int>();
        foreach (var file in files)
        {
            try
            {
                var kind = Detect(file);
                Read(kind, file, junit, coverage);
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or FormatException)
            {
                return Fail($"Could not read {file}: {ex.Message}");
            }
        }
        foreach (var w in junit.Warnings.Take(MaxWarnings)) Cli.Warn(w);
        if (junit.Warnings.Count > MaxWarnings) Cli.Warn($"... and {junit.Warnings.Count - MaxWarnings} more duplicate tests.");
        Console.WriteLine($"  Read {string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Value} {Name(c.Key)}"))}");

        foreach (var target in options.Exports)
        {
            var hasInput = target.Format == ExportFormats.JUnit ? junit.Inputs > 0 : coverage.Inputs > 0;
            if (!hasInput) return Fail($"--export {target.Format}: none of the inputs is {(target.Format == ExportFormats.JUnit ? "a JUnit" : "an lcov or Cobertura")} file.");
        }

        var junitTotals = junit.Inputs > 0 ? junit.Totals() : null;
        var coverageTotals = coverage.Inputs > 0 ? coverage.Totals() : null;
        var written = WriteExports(options.Exports, junit, coverage, junitTotals, coverageTotals);
        if (junitTotals is not null) PrintTests(junitTotals);
        if (coverageTotals is not null)
            Console.WriteLine($"  Coverage: {coverageTotals.LineRate:P1} of lines ({coverageTotals.LinesCovered}/{coverageTotals.LinesValid})" +
                              (coverageTotals.BranchesValid > 0 ? $", {coverageTotals.BranchRate:P1} of branches ({coverageTotals.BranchesCovered}/{coverageTotals.BranchesValid})" : "") +
                              $" in {coverageTotals.Files} file{(coverageTotals.Files == 1 ? "" : "s")}");
        return ExitCode(junitTotals, written);
    }

    private const int MaxWarnings = 20;

    internal static int ExitCode(JUnitTotals? tests, bool exportsOk) =>
        tests is { Failed: > 0 } || !exportsOk ? ExitFailed : ExitPassed;

    private static void PrintTests(JUnitTotals t)
    {
        var verdict = t.Failed > 0 ? Cli.Red($"{t.Failed} failed") : Cli.Bold("no failures");
        Console.WriteLine($"  Tests: {verdict}, {t.Passed} passed, {t.Skipped} skipped ({t.Tests} total) in {t.Suites} suite{(t.Suites == 1 ? "" : "s")}");
    }

    private static string Name(ReportKind kind) => kind switch
    {
        ReportKind.JUnit => "junit",
        ReportKind.Lcov => "lcov",
        _ => "cobertura",
    };

    private static void Read(ReportKind kind, string file, JUnitMerge junit, CoverageMerge coverage)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        switch (kind)
        {
            case ReportKind.JUnit: junit.Add(file, stream); break;
            case ReportKind.Cobertura: coverage.AddCobertura(stream); break;
            default:
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)) coverage.AddLcov(reader);
                break;
        }
    }

    /// <summary>The kind of report by its content: an XML root of testsuites / testsuite or coverage, else lcov (TN: / SF: first).</summary>
    public static ReportKind Detect(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        int c;
        while ((c = reader.Peek()) >= 0 && char.IsWhiteSpace((char)c)) reader.Read();
        if (c < 0) throw new FormatException("the file is empty.");
        if (c == '<')
        {
            using var xml = XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            xml.MoveToContent();
            return xml.LocalName switch
            {
                "testsuites" or "testsuite" => ReportKind.JUnit,
                "coverage" => ReportKind.Cobertura,
                _ => throw new FormatException($"not a JUnit, lcov or Cobertura report (XML root <{xml.LocalName}>)."),
            };
        }
        var head = new char[3];
        var read = reader.ReadBlock(head, 0, 3);
        if (read == 3 && (new string(head) is "TN:" or "SF:")) return ReportKind.Lcov;
        throw new FormatException("not a JUnit, lcov or Cobertura report.");
    }

    // ------------------------------------------------------------------ inputs

    /// <summary>
    /// The files of the inputs, in order: a path as is, a pattern (<c>*</c> / <c>?</c> in the file name part only)
    /// as its matches sorted by name; each file once. Null + <paramref name="error"/> when one is missing.
    /// A pattern never matches a file that is one of the outputs (so <c>out/*.xml</c> can also be the output folder).
    /// </summary>
    internal static List<string>? ExpandInputs(IReadOnlyList<string> inputs, IReadOnlyList<ExportTarget> exports, out string? error)
    {
        error = null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var outputs = exports.Select(e => Path.GetFullPath(e.Path)).ToHashSet(StringComparer.FromComparison(comparison));
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.FromComparison(comparison));
        foreach (var input in inputs)
        {
            var pattern = Path.GetFileName(input);
            var dir = Path.GetDirectoryName(input);
            if (dir is not null && dir.AsSpan().IndexOfAny('*', '?') >= 0)
            {
                error = $"{input}: wildcards are only supported in the file name, not in the folders.";
                return null;
            }
            if (pattern.AsSpan().IndexOfAny('*', '?') < 0)
            {
                if (!File.Exists(input))
                {
                    error = Directory.Exists(input) ? $"{input} is a directory: name the report files in it (e.g. {Path.Combine(input, "*.xml")})." : $"Input not found: {input}";
                    return null;
                }
                if (seen.Add(Path.GetFullPath(input))) files.Add(input);
                continue;
            }
            var folder = string.IsNullOrEmpty(dir) ? "." : dir;
            var matches = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Where(f => GlobMatch(Path.GetFileName(f), pattern, comparison) && !outputs.Contains(Path.GetFullPath(f)))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
                : [];
            if (matches.Count == 0)
            {
                error = $"No file matches {input}.";
                return null;
            }
            foreach (var m in matches) if (seen.Add(Path.GetFullPath(m))) files.Add(m);
        }
        return files;
    }

    /// <summary>'*' (any run of characters) and '?' (one character) over the whole <paramref name="name"/>.</summary>
    internal static bool GlobMatch(string name, string pattern, StringComparison comparison = StringComparison.Ordinal)
    {
        int n = 0, p = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = n; }
            else if (p < pattern.Length && (pattern[p] == '?' || string.Compare(pattern, p, name, n, 1, comparison) == 0)) { p++; n++; }
            else if (star >= 0) { p = star + 1; n = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    // ------------------------------------------------------------------ outputs

    /// <summary>Writes every --export target; false when one could not be written.</summary>
    private static bool WriteExports(IReadOnlyList<ExportTarget> exports, JUnitMerge junit, CoverageMerge coverage, JUnitTotals? tests, CoverageTotals? totals)
    {
        var ok = true;
        foreach (var target in exports)
        {
            try
            {
                var path = Path.GetFullPath(target.Path);
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    switch (target.Format)
                    {
                        case ExportFormats.JUnit: junit.Write(stream); break;
                        case ExportFormats.Lcov: coverage.WriteLcov(stream); break;
                        default: coverage.WriteCobertura(stream); break;
                    }
                }
                Console.WriteLine(target.Format == ExportFormats.JUnit
                    ? $"  Wrote {target.Format}: {target.Path} ({tests!.Tests} tests in {tests.Suites} suite{(tests.Suites == 1 ? "" : "s")})"
                    : $"  Wrote {target.Format}: {target.Path} ({totals!.Files} files, {totals.LineRate:P1} of lines)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Console.Error.WriteLine($"{ErrorLabel} Could not write {target.Path}: {ex.Message}");
                ok = false;
            }
        }
        return ok;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"{ErrorLabel} {message}");
        return ExitError;
    }
}
