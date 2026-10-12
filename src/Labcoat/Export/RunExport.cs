namespace Labcoat;

/// <summary>One test result of an exported run (<see cref="RunExport"/>).</summary>
public sealed record ExportTest
{
    public required string ProjectId { get; init; }
    public required string Id { get; init; }
    /// <summary>The display name: a data row has its own (e.g. "Add (1,2,3)").</summary>
    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public string? ClassName { get; init; }
    public Outcome Outcome { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorType { get; init; }
    public string? StackTrace { get; init; }
    public string? StdOut { get; init; }
    public string? StdErr { get; init; }
    /// <summary>The test is quarantined: a failure does not fail the run (RunSummary.quarantined).</summary>
    public bool Quarantined { get; init; }
    /// <summary>How often the test ran in the run: more than 1 when <c>--retry-failed</c> reran it.</summary>
    public int Attempts { get; init; } = 1;
    /// <summary>
    /// The failed attempts of a retried test: all of them for a test that passed on a retry (<see cref="IsFlaky"/>), the
    /// reruns after the first failure (which stays in the error fields) for one that never passed.
    /// </summary>
    public List<ExportAttempt>? FailedAttempts { get; init; }

    /// <summary>The test failed and passed on a retry (<c>--retry-failed</c>): it passed, but not at the first attempt.</summary>
    public bool IsFlaky => Outcome == Outcome.Passed && Attempts > 1;

    /// <summary>"Namespace.Class", the class alone, or "" when the framework reported neither.</summary>
    public string ClassFullName
    {
        get
        {
            if (string.IsNullOrEmpty(ClassName)) return Namespace ?? "";
            return string.IsNullOrEmpty(Namespace) ? ClassName : $"{Namespace}.{ClassName}";
        }
    }

    /// <summary>How the test counts in a report (JUnit's failure vs error, quarantined failures as skipped).</summary>
    public ExportKind Kind => ExportKinds.Of(this);
}

/// <summary>One failed attempt of a retried test (<see cref="ExportTest.FailedAttempts"/>).</summary>
public sealed record ExportAttempt(Outcome Outcome, string? ErrorMessage, string? ErrorType, string? StackTrace)
{
    public static ExportAttempt Of(ExportTest t) => new(t.Outcome, t.ErrorMessage, t.ErrorType, t.StackTrace);

    /// <summary>An assertion failure rather than an unexpected exception or a timeout (JUnit failure vs error).</summary>
    public bool IsAssertion => Outcome != Outcome.TimedOut && ExportKinds.IsAssertion(ErrorType);
}

/// <summary>A test project of an exported run.</summary>
/// <param name="Path">The project file relative to the workspace root ('/' separators), or its file name.</param>
public sealed record ExportProject(string Id, string Name, string? TargetFramework, string? Path);

/// <summary>
/// A run as the exports see it (Explorer.ExportRun): the summary, the projects that reported results and every result
/// with the test's names, error and (when the live result is still this run's) console output, in tree order.
/// </summary>
public sealed record RunExport
{
    public required string WorkspaceName { get; init; }
    public required string Version { get; init; }
    public required RunSummary Run { get; init; }
    public required List<ExportProject> Projects { get; init; }
    public required List<ExportTest> Tests { get; init; }
    public required string GeneratedAt { get; init; }

    /// <summary>Tests that failed and passed on a retry (<c>--retry-failed</c>); they count as passed.</summary>
    public int Flaky => Tests.Count(t => t.IsFlaky);
}

/// <summary>How a result counts in a report.</summary>
public enum ExportKind
{
    Passed,
    /// <summary>An assertion failed (JUnit &lt;failure&gt;).</summary>
    Failure,
    /// <summary>An unexpected exception, a timeout or a crashed test host (JUnit &lt;error&gt;).</summary>
    Error,
    Skipped,
    /// <summary>Requested but no result: cancelled or not run (JUnit: skipped).</summary>
    NotRun,
    /// <summary>A quarantined test that failed: reported, but it does not fail the run (JUnit: skipped).</summary>
    QuarantinedFailure,
}

internal static class ExportKinds
{
    public static ExportKind Of(ExportTest t) => t.Outcome switch
    {
        Outcome.Passed => ExportKind.Passed,
        Outcome.Skipped => ExportKind.Skipped,
        Outcome.Failed or Outcome.TimedOut when t.Quarantined => ExportKind.QuarantinedFailure,
        Outcome.TimedOut => ExportKind.Error,
        Outcome.Failed => IsAssertion(t.ErrorType) ? ExportKind.Failure : ExportKind.Error,
        _ => ExportKind.NotRun,
    };

    /// <summary>
    /// An assertion failure rather than an unexpected exception: no exception type known (Labcoat reports one only when
    /// it can tell), or the type of an assertion library (MSTest AssertFailedException, NUnit AssertionException,
    /// xUnit's Xunit.Sdk.*, Shouldly, FluentAssertions, …).
    /// </summary>
    public static bool IsAssertion(string? errorType) =>
        string.IsNullOrEmpty(errorType)
        || errorType.Contains("Assert", StringComparison.OrdinalIgnoreCase)
        || errorType.EndsWith("VerifyException", StringComparison.Ordinal) // Verify: a snapshot mismatch
        || errorType.StartsWith("Xunit.Sdk.", StringComparison.Ordinal)
        || errorType.StartsWith("NUnit.Framework.", StringComparison.Ordinal)
        || errorType.StartsWith("Shouldly.", StringComparison.Ordinal)
        || errorType.StartsWith("FluentAssertions.", StringComparison.Ordinal)
        || errorType.StartsWith("AwesomeAssertions.", StringComparison.Ordinal)
        || errorType.StartsWith("TUnit.Assertions.", StringComparison.Ordinal);

    public static bool IsFailed(ExportKind k) => k is ExportKind.Failure or ExportKind.Error;

    /// <summary>"failed" | "passed" | "skipped": the filter bucket of the HTML report.</summary>
    public static string Bucket(ExportKind k) => k switch
    {
        ExportKind.Passed => "passed",
        ExportKind.Failure or ExportKind.Error => "failed",
        _ => "skipped",
    };

    /// <summary>The reason shown for a result that did not pass or fail.</summary>
    public static string SkipReason(ExportTest t) => t.Kind switch
    {
        ExportKind.QuarantinedFailure => "Quarantined; failed: " + ExportText.FirstLine(t.ErrorMessage, 500),
        ExportKind.NotRun => t.Outcome == Outcome.Cancelled ? "Cancelled" : "Not run",
        _ => string.IsNullOrWhiteSpace(t.ErrorMessage) ? "Skipped" : ExportText.FirstLine(t.ErrorMessage, 500),
    };
}

/// <summary>The export formats, their content types and download file names.</summary>
public static class ExportFormats
{
    public const string JUnit = "junit";
    public const string Html = "html";
    public const string Lcov = "lcov";
    public const string Cobertura = "cobertura";

    public static readonly string[] RunFormats = [JUnit, Html];
    public static readonly string[] CoverageFormats = [Lcov, Cobertura];

    public static string ContentType(string format) => format switch
    {
        JUnit or Cobertura => "application/xml; charset=utf-8",
        Html => "text/html; charset=utf-8",
        _ => "text/plain; charset=utf-8",
    };

    /// <summary>"junit.xml", "html", "info" (lcov) or "cobertura.xml".</summary>
    public static string Extension(string format) => format switch
    {
        JUnit => "junit.xml",
        Html => "html",
        Lcov => "info",
        _ => "cobertura.xml",
    };

    /// <summary>labcoat-&lt;workspace&gt;-&lt;run id&gt;.&lt;extension&gt;, e.g. labcoat-shop-3f2a….junit.xml.</summary>
    public static string FileName(string? workspaceName, string runId, string format) =>
        $"labcoat-{ExportText.Slug(workspaceName)}-{ExportText.Slug(runId, "run")}.{Extension(format)}";
}
