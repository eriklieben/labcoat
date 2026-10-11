namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An Labcoat instance: a web UI that discovers, runs and watches the tests of one or more
/// Microsoft.Testing.Platform (MSTest, ...) test projects. Created with
/// <see cref="LabcoatResourceBuilderExtensions.AddLabcoat(IDistributedApplicationBuilder, string)"/>.
/// </summary>
/// <param name="name">The resource name.</param>
/// <param name="command">The executable to launch (normally <c>dotnet</c>).</param>
/// <param name="workingDirectory">The working directory of the explorer process.</param>
public class LabcoatResource(string name, string command, string workingDirectory)
    : ExecutableResource(name, command, workingDirectory), IResourceWithEndpoints
{
    internal const string HttpEndpointName = "http";

    private EndpointReference? _httpEndpoint;

    /// <summary>The HTTP endpoint the explorer UI and REST API listen on.</summary>
    public EndpointReference HttpEndpoint => _httpEndpoint ??= new(this, HttpEndpointName);

    /// <summary>Test project files or directories (absolute) passed to the explorer. Empty = the solution root.</summary>
    public IList<string> TestProjects { get; } = new List<string>();

    /// <summary>The solution root: scanned when no test project is configured, and always allowed for source viewing.</summary>
    public string SolutionRoot { get; internal set; } = "";

    /// <summary>Start with watch mode on.</summary>
    public bool WatchOnStart { get; internal set; }

    /// <summary>Start with code coverage collection on.</summary>
    public bool CoverageOnStart { get; internal set; }

    /// <summary>
    /// Coverage settings file for every test project (passed as <c>--coverage-settings</c>); null = each project's own
    /// coverage.settings.xml / CodeCoverage.xml / *.runsettings, else Labcoat's defaults.
    /// </summary>
    public string? CoverageSettings { get; internal set; }

    /// <summary>Module names or path globs to count although Labcoat's coverage defaults leave them out (<c>--coverage-include</c>).</summary>
    public IList<string> CoverageInclude { get; } = new List<string>();

    /// <summary>Module names or path globs never to count in coverage (<c>--coverage-exclude</c>).</summary>
    public IList<string> CoverageExclude { get; } = new List<string>();

    /// <summary>Start with per-test trace collection on (test hosts get the dashboard's OTLP endpoint). Off by default.</summary>
    public bool TracingOnStart { get; internal set; }

    /// <summary>
    /// While trace collection is on, only the tests matching this filter get a trace (the Labcoat UI's search syntax,
    /// e.g. <c>cat:Integration</c>; passed as <c>LABCOAT_TRACE_FILTER</c>). Null = every test.
    /// </summary>
    public string? TracingFilter { get; internal set; }

    /// <summary>Base git ref of "Run affected: branch vs …" (passed as <c>LABCOAT_AFFECTED_BASE</c>); null = Labcoat's default.</summary>
    public string? AffectedBase { get; internal set; }

    /// <summary>Granularity of the test impact map, "class" | "test" | "auto" (passed as <c>LABCOAT_IMPACT_MAP</c>); null = Labcoat's default.</summary>
    public string? ImpactMap { get; internal set; }

    /// <summary>Test projects discovered / run at once (passed as <c>LABCOAT_MAX_PARALLEL</c>); null = Labcoat's default, min(cores, 4).</summary>
    public int? MaxParallelProjects { get; internal set; }

    /// <summary>A running test is shown as possibly hanging after this long (<c>LABCOAT_HANG_THRESHOLD</c>); null = 60 s, zero = never.</summary>
    public TimeSpan? HangThreshold { get; internal set; }

    /// <summary>Per-test timeout (<c>LABCOAT_TEST_TIMEOUT</c>): a test running longer times out and its project's run is cancelled. Null / zero = off.</summary>
    public TimeSpan? TestTimeout { get; internal set; }

    /// <summary>
    /// Per-AppHost-process secret passed to the explorer as <c>LABCOAT_TOKEN</c>; the dashboard commands send it as
    /// <c>X-Labcoat-Token</c>. Requests without a browser Origin (scripts, other local users' processes) need it.
    /// </summary>
    internal string ApiToken { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Defaults (and locks) for the Labcoat UI's settings, passed to the explorer as <c>LABCOAT_APPHOST_UI_SETTINGS</c>.
    /// Set with <c>AddLabcoat(name, o =&gt; o.Ui...)</c> or <see cref="LabcoatResourceBuilderExtensions.WithUiSettings"/>.
    /// </summary>
    public LabcoatUiSettings UiSettings { get; } = new();

    /// <summary>
    /// Environment variables (or <c>PREFIX*</c> patterns) the test processes keep although they look like variables of
    /// the Aspire run; passed to the explorer as <c>LABCOAT_TEST_ENV_PASSTHROUGH</c>.
    /// Set with <see cref="LabcoatResourceBuilderExtensions.WithPassThroughEnvironment"/> or
    /// <see cref="LabcoatOptions.PassThroughEnvironment"/>.
    /// </summary>
    public ISet<string> PassThroughEnvironment { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Publish the suite result as the resource state (<see cref="LabcoatResourceBuilderExtensions.WithSuiteResultState"/>).</summary>
    public bool SuiteResultState { get; internal set; } = true;

    /// <summary>The resource's ResourceReadyEvent fired (health checks passed) since it was last started.</summary>
    internal volatile bool IsReady;

    /// <summary>The suite result last published as the resource state (e.g. "✕ 1 failed · 20 tests"), null when none.</summary>
    internal string? SuiteStateText { get; set; }

    /// <summary>Test seam: the HTTP client the dashboard commands and the suite state use (null = the shared one).</summary>
    internal HttpClient? Http { get; set; }

    /// <summary>How often a dashboard run command polls the run status.</summary>
    internal TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the suite result is read for the resource state.</summary>
    internal TimeSpan SuitePollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
