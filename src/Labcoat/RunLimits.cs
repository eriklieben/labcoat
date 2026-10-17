using System.Globalization;
using System.Text;

namespace Labcoat;

/// <summary>
/// How many projects build / discover / run at once, when a running test counts as slow, and the optional per-test
/// timeout (docs/configuration.md "Parallel projects, slow tests and timeouts"), plus the <c>--run</c> duration budget.
/// Options: <c>maxParallelProjects</c>, <c>hangThreshold</c>, <c>testTimeout</c>, <c>maxTestDuration</c> in a settings
/// file; <c>--max-parallel</c>, <c>--hang-threshold</c>, <c>--test-timeout</c>, <c>--max-test-duration</c>;
/// <c>LABCOAT_MAX_PARALLEL</c>, <c>LABCOAT_HANG_THRESHOLD</c>, <c>LABCOAT_TEST_TIMEOUT</c>, <c>LABCOAT_MAX_TEST_DURATION</c>
/// (set by Labcoat.Aspire.Hosting).
/// </summary>
public static class RunLimits
{
    public const string MaxParallelEnv = "LABCOAT_MAX_PARALLEL";
    public const string HangThresholdEnv = "LABCOAT_HANG_THRESHOLD";
    public const string TestTimeoutEnv = "LABCOAT_TEST_TIMEOUT";
    public const string MaxTestDurationEnv = "LABCOAT_MAX_TEST_DURATION";

    /// <summary>Largest accepted <c>maxParallelProjects</c>.</summary>
    public const int MaxParallelLimit = 64;

    /// <summary>Default <c>maxParallelProjects</c>: min(processor count, 4).</summary>
    public static int DefaultMaxParallel { get; } = Math.Clamp(Environment.ProcessorCount, 1, 4);

    /// <summary>Default <c>hangThreshold</c>: a test running this long is shown as possibly hanging.</summary>
    public static readonly TimeSpan DefaultHangThreshold = TimeSpan.FromSeconds(60);

    /// <summary>A <c>maxParallelProjects</c> value: 1-64; null when invalid.</summary>
    public static int? ParseMaxParallel(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxParallelLimit ? n : null;

    /// <summary>
    /// A duration: seconds as a number (<c>90</c>, <c>1.5</c>) or a number with a unit like MTP's <c>--timeout</c>
    /// (<c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>1.5h</c>; <c>sec</c>, <c>min</c>, <c>hour</c> also). <c>0</c>, <c>off</c>,
    /// <c>none</c> and <c>false</c> mean off (<see cref="TimeSpan.Zero"/>). Null when it is no duration.
    /// </summary>
    public static TimeSpan? ParseDuration(string? value)
    {
        var s = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return null;
        if (s is "off" or "none" or "false" or "never") return TimeSpan.Zero;
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0 || !double.TryParse(s[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0) return null;
        double? seconds = s[i..].Trim() switch
        {
            "" or "s" or "sec" or "secs" or "second" or "seconds" => number,
            "ms" => number / 1000,
            "m" or "min" or "mins" or "minute" or "minutes" => number * 60,
            "h" or "hour" or "hours" => number * 3600,
            _ => null,
        };
        if (seconds is not { } sec || sec > TimeSpan.FromDays(1).TotalSeconds) return null;
        return TimeSpan.FromSeconds(sec);
    }

    /// <summary>"90 s", "5 min", "1.5 h" for messages.</summary>
    public static string Describe(TimeSpan d)
    {
        if (d.TotalSeconds < 1) return $"{d.TotalMilliseconds:0} ms";
        if (d.TotalSeconds < 120) return $"{d.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s";
        if (d.TotalMinutes < 120) return $"{d.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} min";
        return $"{d.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";
    }
}

/// <summary>
/// The test app options of a project's <c>TestingPlatformCommandLineArguments</c> MSBuild property (what
/// <c>dotnet test</c> passes to the test app), sorted for Labcoat's server-mode test hosts: options that work in server
/// mode (<c>--settings</c>, <c>--filter*</c>, <c>--treenode-filter</c>, <c>--config-file</c>, framework options, …) are
/// passed on; options that conflict with server mode or with what Labcoat controls (its own coverage, live output,
/// the client connection, report and terminal options) are skipped with a reason; <c>--timeout</c> is MTP's
/// whole-session timeout, which would end a warm test host that outlives many runs, so Labcoat applies it as a timeout of
/// each run of the project instead (<see cref="SessionTimeout"/>).
/// </summary>
public sealed class TestingPlatformArguments
{
    /// <summary>Options passed to every test host of the project, in order.</summary>
    public List<string> Passed { get; } = [];
    /// <summary>Skipped options with the reason, for the log.</summary>
    public List<(string Option, string Reason)> Skipped { get; } = [];
    /// <summary><c>--timeout</c>: how long one run of the project may take; null when not set.</summary>
    public TimeSpan? SessionTimeout { get; private set; }
    /// <summary>The <c>--timeout</c> text as written (for messages).</summary>
    public string? SessionTimeoutText { get; private set; }

    public static readonly TestingPlatformArguments Empty = new();

    /// <summary>Options with no value that Labcoat skips, and why.</summary>
    private static readonly Dictionary<string, string> SkippedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--server"] = "Labcoat starts the test host in server mode itself",
        ["--list-tests"] = "Labcoat discovers the tests over the server protocol",
        ["--help"] = "not a test run option",
        ["-?"] = "not a test run option",
        ["--info"] = "not a test run option",
        ["--coverage"] = "Labcoat turns coverage on per run (Collect coverage)",
        ["--no-progress"] = "terminal output only",
        ["--no-ansi"] = "terminal output only",
        ["--ignore-exit-code"] = "exit codes do not apply in server mode",
        ["--show-live-output"] = "Labcoat turns live output on itself where the framework supports it",
        ["--debug"] = "use Labcoat's Debug test command",
    };

    private const string ConnectsItself = "Labcoat connects to the test host itself";

    /// <summary>Options with a value that Labcoat skips, and why.</summary>
    private static readonly Dictionary<string, string> SkippedWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--client-host"] = ConnectsItself,
        ["--client-port"] = ConnectsItself,
        ["--dotnet-test-pipe"] = ConnectsItself,
        ["--port"] = ConnectsItself,
        ["--coverage-output"] = "Labcoat chooses the coverage output",
        ["--coverage-output-format"] = "Labcoat chooses the coverage output",
        ["--coverage-settings"] = "used for coverage runs only (see Code coverage)",
        ["--minimum-expected-tests"] = "Labcoat often runs a selection of the tests",
        ["--exit-on-process-exit"] = "Labcoat manages the test host's lifetime",
        ["--output"] = "terminal output only",
        ["--results-directory"] = "Labcoat keeps the results itself",
    };

    /// <summary>A token that is a value of the option before it (not an option itself; negative numbers are values).</summary>
    private static bool LooksLikeValue(string token) => !token.StartsWith('-') || IsNumber(token);

    private static bool IsNumber(string token) => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>Options whose value is a file: a relative path that exists under the project directory is made absolute
    /// (the test host runs in the output folder).</summary>
    private static readonly HashSet<string> FileOptions = new(StringComparer.OrdinalIgnoreCase) { "--settings", "--config-file", "--filter-uid-file" };

    /// <summary>Sorts the options of <paramref name="raw"/> (null / empty: nothing).</summary>
    /// <param name="projectDir">The project directory, for relative file paths of <see cref="FileOptions"/>.</param>
    public static TestingPlatformArguments Parse(string? raw, string? projectDir = null)
    {
        var result = new TestingPlatformArguments();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var tokens = new Queue<string>(Tokenize(raw));
        while (tokens.TryDequeue(out var token))
        {
            var (name, inline) = SplitInline(token);
            // The values of this option: the inline one, else the following tokens that are no option.
            var values = new List<string>();
            if (inline is not null) values.Add(inline);
            else
                while (tokens.TryPeek(out var next) && LooksLikeValue(next)) values.Add(tokens.Dequeue());
            if (projectDir is not null && FileOptions.Contains(name) && values.Count == 1) values[0] = ResolveFile(values[0], projectDir);
            result.Classify(name, inline is not null, values);
        }
        return result;
    }

    private void Classify(string name, bool inline, List<string> values)
    {
        var shown = values.Count == 0 ? name : $"{name} {string.Join(' ', values)}";
        if (!name.StartsWith('-'))
        {
            Skipped.Add((shown, "not an option"));
            return;
        }
        if (name.Equals("--timeout", StringComparison.OrdinalIgnoreCase))
        {
            var text = values.FirstOrDefault();
            if (RunLimits.ParseDuration(text) is { } t && t > TimeSpan.Zero)
            {
                SessionTimeout = t;
                SessionTimeoutText = text;
            }
            else Skipped.Add((shown, "not a duration"));
            return;
        }
        if (SkippedFlags.TryGetValue(name, out var why) || SkippedWithValue.TryGetValue(name, out why))
        {
            Skipped.Add((shown, why));
            return;
        }
        if (inline) Passed.Add($"{name}={values[0]}");
        else
        {
            Passed.Add(name);
            Passed.AddRange(values);
        }
    }

    private static string ResolveFile(string value, string projectDir)
    {
        if (Path.IsPathRooted(value)) return value;
        var full = Path.GetFullPath(Path.Combine(projectDir, value));
        return File.Exists(full) ? full : value;
    }

    /// <summary>"--name=value" → (name, value); anything else → (token, null).</summary>
    private static (string Name, string? Value) SplitInline(string token)
    {
        if (!token.StartsWith("--", StringComparison.Ordinal)) return (token, null);
        var eq = token.IndexOf('=');
        return eq > 2 ? (token[..eq], token[(eq + 1)..]) : (token, null);
    }

    /// <summary>Splits like a shell: whitespace separates, double or single quotes group (and are removed).</summary>
    public static List<string> Tokenize(string raw)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;
        foreach (var c in raw)
        {
            if (quote is { } q)
            {
                if (c == q) quote = null;
                else current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken) tokens.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }
        if (inToken) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>One log line: what is passed on, applied by Labcoat and skipped; null when there is nothing.</summary>
    public string? Describe()
    {
        var parts = new List<string>();
        if (Passed.Count > 0) parts.Add("passed to the test host: " + string.Join(' ', Passed));
        if (SessionTimeout is { } t) parts.Add($"--timeout {SessionTimeoutText}: applied by Labcoat to each run of the project ({RunLimits.Describe(t)})");
        if (Skipped.Count > 0) parts.Add("skipped: " + string.Join("; ", Skipped.Select(s => $"{s.Option} ({s.Reason})")));
        return parts.Count == 0 ? null : "TestingPlatformCommandLineArguments: " + string.Join("; ", parts);
    }
}
