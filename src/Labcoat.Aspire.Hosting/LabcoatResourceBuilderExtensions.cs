using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Labcoat to an Aspire AppHost.
/// </summary>
public static class LabcoatResourceBuilderExtensions
{
    /// <summary>Folder (next to the AppHost binaries) that the Labcoat.Aspire.Hosting package copies the explorer into.</summary>
    public const string BundleFolderName = "labcoat";

    private const string ExplorerAssembly = "Labcoat.dll";

    /// <summary>Environment variable that carries <see cref="LabcoatResource.UiSettings"/> to the explorer (compact JSON).</summary>
    public const string UiSettingsEnvironmentVariable = "LABCOAT_APPHOST_UI_SETTINGS";

    /// <summary>Environment variable that carries <see cref="WithAffectedBase"/> to the explorer.</summary>
    public const string AffectedBaseEnvironmentVariable = "LABCOAT_AFFECTED_BASE";

    /// <summary>Environment variable that carries <see cref="WithImpactMap"/> to the explorer.</summary>
    public const string ImpactMapEnvironmentVariable = "LABCOAT_IMPACT_MAP";

    /// <summary>Values of <see cref="WithImpactMap"/>.</summary>
    internal static readonly string[] ImpactMapModes = ["class", "test", "auto"];
    /// <summary>
    /// Environment variable that carries <see cref="LabcoatResource.PassThroughEnvironment"/> to the explorer: the names
    /// (or <c>PREFIX*</c> patterns) its test hosts keep although they look like variables of the Aspire run.
    /// </summary>
    public const string PassThroughEnvironmentVariable = "LABCOAT_TEST_ENV_PASSTHROUGH";
    private const string ToolPackageId = "Labcoat";

    /// <summary>
    /// This package's version (without the "+commit" suffix): the fallback runs the Labcoat tool of the same version,
    /// pinned as <c>Labcoat@version</c>, so a prerelease resolves without <c>--prerelease</c>.
    /// </summary>
    internal static string ToolPackageVersion { get; } =
        typeof(LabcoatResourceBuilderExtensions).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Test seam: the directory the bundle folder is looked up in (the AppHost's binaries).</summary>
    internal static string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>
    /// Adds Labcoat, a web UI for the Microsoft.Testing.Platform (MSTest, ...) tests of your solution,
    /// to the application model. It shows up in the dashboard with a "Labcoat UI" link and commands to run tests,
    /// run failed tests and toggle watch / coverage.
    /// </summary>
    /// <remarks>
    /// Without <see cref="WithTestProject"/> the explorer scans the AppHost's solution directory (the directory of the
    /// nearest <c>.sln</c>/<c>.slnx</c> walking up from the AppHost directory, or the AppHost's parent directory) for
    /// test projects. The explorer runs from the copy the package places in <c>$(OutDir)labcoat/</c>; when that
    /// copy is missing it falls back to <c>dotnet tool exec Labcoat@&lt;this package's version&gt;</c>.
    /// The resource is not added when the AppHost runs inside <c>Aspire.Hosting.Testing</c> (integration tests), so test
    /// runs don't start a nested explorer.
    /// </remarks>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name, e.g. <c>"tests"</c>.</param>
    public static IResourceBuilder<LabcoatResource> AddLabcoat(this IDistributedApplicationBuilder builder, string name) =>
        AddLabcoat(builder, name, configure: null!);

    /// <summary>
    /// Adds Labcoat with options: start options (test projects, watch, coverage, tracing, port) and defaults for the
    /// UI's settings in one place.
    /// </summary>
    /// <remarks>
    /// Options are read from the AppHost configuration first (section <c>Labcoat</c> for every Labcoat resource, then
    /// <c>Labcoat:Resources:&lt;name&gt;</c>), then <paramref name="configure"/> runs, so code wins over appsettings.json.
    /// UI settings are defaults: a choice made in the browser wins unless the setting is locked
    /// (<c>o.Ui.Lock(s =&gt; s.Theme)</c>, <c>o.Ui.LockAll()</c>).
    /// <code>
    /// builder.AddLabcoat("tests", o =>
    /// {
    ///     o.TestProjects.Add("../MyApp.Tests");
    ///     o.Watch = true;
    ///     o.Ui.Theme = LabcoatTheme.Dracula;
    ///     o.Ui.Editor = LabcoatEditor.Rider;
    ///     o.Ui.Lock(s => s.Editor);
    /// });
    /// </code>
    /// </remarks>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name, e.g. <c>"tests"</c>.</param>
    /// <param name="configure">Sets the options; runs after the configuration binding.</param>
    public static IResourceBuilder<LabcoatResource> AddLabcoat(this IDistributedApplicationBuilder builder, string name, Action<LabcoatOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var options = new LabcoatOptions()
            .Bind(builder.Configuration.GetSection("Labcoat"))
            .Bind(builder.Configuration.GetSection($"Labcoat:Resources:{name}"));
        configure?.Invoke(options);
        var rb = AddLabcoatCore(builder, name);
        foreach (var p in options.TestProjects) rb.WithTestProject(p);
        rb.Resource.WatchOnStart = options.Watch;
        rb.Resource.CoverageOnStart = options.Coverage;
        rb.Resource.TracingOnStart = options.TestTracing;
        if (!string.IsNullOrWhiteSpace(options.AffectedBase)) rb.WithAffectedBase(options.AffectedBase);
        if (!string.IsNullOrWhiteSpace(options.ImpactMap)) rb.WithImpactMap(options.ImpactMap);
        rb.WithRunLimits(options); // LabcoatRunLimitsExtensions.cs
        rb.Resource.SuiteResultState = options.SuiteResultState;
        rb.Resource.UiSettings.Apply(options.Ui);
        if (options.PassThroughEnvironment.Count > 0) rb.WithPassThroughEnvironment([.. options.PassThroughEnvironment]);
        if (!string.IsNullOrWhiteSpace(options.CoverageSettings)) rb.WithCoverageSettings(options.CoverageSettings);
        if (options.CoverageInclude.Count > 0 || options.CoverageExclude.Count > 0) rb.WithCoverageFilter(options.CoverageInclude, options.CoverageExclude);
        if (options.HostPort is { } port && rb.Resource.Annotations.OfType<EndpointAnnotation>().Any()) rb.WithHostPort(port);
        return rb;
    }

    /// <summary>
    /// Sets defaults (and optional locks) for the Labcoat UI's settings: theme, mode, motion, editor, coverage tint,
    /// confetti, partial runs in history, opening the output panel on errors and single-key shortcuts. A choice the user
    /// makes in the browser wins over a default; a locked setting is shown disabled ("Set by the AppHost (Program.cs)").
    /// Can be called several times; later calls add to / overwrite earlier ones.
    /// <code>
    /// builder.AddLabcoat("tests").WithUiSettings(ui =>
    /// {
    ///     ui.Theme = LabcoatTheme.Aspire;
    ///     ui.Mode = LabcoatThemeMode.Dark;
    ///     ui.Confetti = true;
    ///     ui.Lock(s => s.Theme);
    /// });
    /// </code>
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithUiSettings(this IResourceBuilder<LabcoatResource> builder, Action<LabcoatUiSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(builder.Resource.UiSettings);
        return builder;
    }

    private static IResourceBuilder<LabcoatResource> AddLabcoatCore(IDistributedApplicationBuilder builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var bundleDir = Path.Combine(BaseDirectory, BundleFolderName);
        var bundled = File.Exists(Path.Combine(bundleDir, ExplorerAssembly));
        var solutionRoot = FindSolutionRoot(builder.AppHostDirectory);

        var resource = new LabcoatResource(name, "dotnet", bundled ? bundleDir : solutionRoot)
        {
            SolutionRoot = solutionRoot,
        };

        // Integration tests (Aspire.Hosting.Testing) start the AppHost too; don't spin up a nested explorer there.
        if (builder.ExecutionContext.IsPublishMode || IsRunningUnderAspireTesting())
            return builder.CreateResourceBuilder(resource);

        var dashboardUrls = builder.Configuration["ASPNETCORE_URLS"]?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var dashboardUrl = dashboardUrls.FirstOrDefault();
        // The dashboard's telemetry HTTP API (/api/telemetry/...) is served on the same URL; prefer plain http so
        // Labcoat doesn't depend on trusting the dev certificate. Secured with the AppHost's dashboard API key.
        var dashboardApiUrl = dashboardUrls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? dashboardUrl;
        var dashboardApiKey = builder.Configuration["AppHost:DashboardApiKey"];

        var rb = builder.AddResource(resource)
            // Executable-derived resources default to type "Executable"; DCP keeps an initial ResourceType.
            .WithInitialState(new CustomResourceSnapshot { ResourceType = "Labcoat", Properties = [] })
            .WithIconName("Beaker")
            .WithArgs(ctx => AddExplorerArgs(ctx.Args, resource, bundled, bundleDir))
            // The explorer listens on $PORT (http://localhost:{PORT}).
            .WithHttpEndpoint(env: "PORT", name: LabcoatResource.HttpEndpointName)
            .WithUrlForEndpoint(LabcoatResource.HttpEndpointName, url => url.DisplayText = "Labcoat UI")
            // Cheap liveness endpoint (no state snapshot).
            .WithHttpHealthCheck("/api/health", endpointName: LabcoatResource.HttpEndpointName)
            .WithEnvironment("DOTNET_NOLOGO", "1")
            // Security (Labcoat's SECURITY.md): the dashboard commands below send this token; requests without a browser
            // Origin must carry it. The host name of the (proxied) endpoint is allowed besides localhost.
            .WithEnvironment("LABCOAT_TOKEN", resource.ApiToken)
            // Display name in the Labcoat UI (header, browser tab): the resource name.
            .WithEnvironment("LABCOAT_NAME", name)
            // UI setting defaults / locks (AddLabcoat options, WithUiSettings); read when the resource starts, so the
            // fluent calls after AddLabcoat count too.
            .WithEnvironment(ctx => SetExplorerEnvironment(ctx.EnvironmentVariables, resource))
            // OTEL_EXPORTER_OTLP_* of this AppHost's dashboard. Only when trace collection is on (WithTestTracing() or the
            // UI toggle) does Labcoat hand them to test hosts (as LABCOAT_OTLP_*), so tests using Labcoat.Testing can
            // send per-test traces + their services' telemetry to this dashboard.
            .WithOtlpExporter()
            // Inherited by the `dotnet build` processes the explorer starts (MSBuild reads env vars as properties):
            // building a test project that references this AppHost (in-repo ProjectReference setup) must not
            // republish the running explorer. See Labcoat.Aspire.Hosting.csproj.
            .WithEnvironment("LabcoatSkipBundle", "true")
            .ExcludeFromManifest();

        AddDashboardEnvironment(rb, dashboardUrl, dashboardApiUrl, dashboardApiKey);

        AddCommands(rb);
        builder.Eventing.Subscribe<ResourceReadyEvent>(resource, (_, _) =>
        {
            resource.IsReady = true;
            return Task.CompletedTask;
        });
        // Show the suite result as the resource state ("✕ 1 failed · 20 tests") once the AppHost runs.
        builder.Eventing.Subscribe<BeforeStartEvent>((evt, unused) =>
        {
            var notifications = evt.Services.GetRequiredService<ResourceNotificationService>();
            var stopping = evt.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            _ = Task.Run(() => PublishSuiteStateAsync(resource, notifications, stopping), CancellationToken.None);
            return Task.CompletedTask;
        });
        return rb;
    }

    /// <summary>The explorer's command line: Labcoat.dll (or <c>dotnet tool exec</c>), the test projects and the options.</summary>
    private static void AddExplorerArgs(IList<object> args, LabcoatResource resource, bool bundled, string bundleDir)
    {
        if (bundled)
        {
            args.Add(Path.Combine(bundleDir, ExplorerAssembly)); // dotnet labcoat/Labcoat.dll <args>
        }
        else
        {
            // Fallback when the bundle wasn't copied: dotnet tool exec Labcoat@<this version> --yes -- <args>
            foreach (var a in new[] { "tool", "exec", $"{ToolPackageId}@{ToolPackageVersion}", "--yes", "--" }) args.Add(a);
        }

        if (resource.TestProjects.Count == 0) args.Add(resource.SolutionRoot);
        foreach (var p in resource.TestProjects) args.Add(p);
        args.Add("--root");
        args.Add(resource.SolutionRoot);
        args.Add("--no-open");
        if (resource.WatchOnStart) args.Add("--watch");
        if (resource.CoverageOnStart) args.Add("--coverage");
        if (resource.TracingOnStart) args.Add("--traces");
        AddCoverageArgs(args, resource);
    }

    private static void AddCoverageArgs(IList<object> args, LabcoatResource resource)
    {
        if (!string.IsNullOrWhiteSpace(resource.CoverageSettings))
        {
            args.Add("--coverage-settings");
            args.Add(resource.CoverageSettings);
        }
        if (resource.CoverageInclude.Count > 0)
        {
            args.Add("--coverage-include");
            args.Add(string.Join(',', resource.CoverageInclude));
        }
        if (resource.CoverageExclude.Count > 0)
        {
            args.Add("--coverage-exclude");
            args.Add(string.Join(',', resource.CoverageExclude));
        }
    }

    /// <summary>
    /// The environment read when the resource starts (so the fluent calls after AddLabcoat count too). Set in a callback,
    /// not with WithEnvironment(name, endpointExpression): that overload adds a "Reference" relationship to every resource
    /// in the expression, here the Labcoat resource itself (the dashboard showed it as referencing itself).
    /// </summary>
    private static void SetExplorerEnvironment(Dictionary<string, object> env, LabcoatResource resource)
    {
        env["LABCOAT_ALLOWED_HOSTS"] = resource.HttpEndpoint.Property(EndpointProperty.Host);
        // Variables the test hosts keep although they look like the Aspire run's (see WithPassThroughEnvironment).
        if (resource.PassThroughEnvironment.Count > 0)
            env[PassThroughEnvironmentVariable] = string.Join(';', resource.PassThroughEnvironment);
        var ui = resource.UiSettings.ToJson();
        if (ui.Count > 0) env[UiSettingsEnvironmentVariable] = ui.ToJsonString();
        if (!string.IsNullOrWhiteSpace(resource.AffectedBase)) env[AffectedBaseEnvironmentVariable] = resource.AffectedBase;
        if (!string.IsNullOrWhiteSpace(resource.ImpactMap)) env[ImpactMapEnvironmentVariable] = resource.ImpactMap;
    }

    /// <summary>The AppHost's dashboard: its URL, and its telemetry API (URL and API key) for the Aspire failure context.</summary>
    private static void AddDashboardEnvironment(IResourceBuilder<LabcoatResource> rb, string? dashboardUrl, string? dashboardApiUrl, string? dashboardApiKey)
    {
        if (!string.IsNullOrEmpty(dashboardUrl))
            rb.WithEnvironment("ASPIRE_DASHBOARD_URL", dashboardUrl);
        if (!string.IsNullOrEmpty(dashboardApiUrl))
            rb.WithEnvironment("LABCOAT_DASHBOARD_API_URL", dashboardApiUrl);
        // No key = the dashboard API runs unsecured (ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS); Labcoat sends no header then.
        if (!string.IsNullOrEmpty(dashboardApiKey))
            rb.WithEnvironment("LABCOAT_DASHBOARD_API_KEY", dashboardApiKey);
    }

    /// <summary>
    /// Adds a test project (a <c>.csproj</c>, a solution file, or a directory that is scanned for test projects).
    /// Relative paths are resolved against the AppHost directory. Can be called multiple times.
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithTestProject(this IResourceBuilder<LabcoatResource> builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(Path.Combine(builder.ApplicationBuilder.AppHostDirectory, path));
        if (!builder.Resource.TestProjects.Contains(full)) builder.Resource.TestProjects.Add(full);
        return builder;
    }

    /// <summary>Starts the explorer with watch mode on (rebuild and re-run affected tests on source changes).</summary>
    public static IResourceBuilder<LabcoatResource> WithWatch(this IResourceBuilder<LabcoatResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.WatchOnStart = enabled;
        return builder;
    }

    /// <summary>
    /// Sets the base git ref of the UI's "Run affected: branch vs …" (e.g. <c>origin/develop</c>). Without it Labcoat
    /// uses the remote default branch (origin/HEAD), else origin/main, origin/master, main or master.
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithAffectedBase(this IResourceBuilder<LabcoatResource> builder, string baseRef)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        var trimmed = baseRef.Trim();
        if (trimmed.StartsWith('-') || trimmed.Any(char.IsWhiteSpace))
            throw new ArgumentException($"'{baseRef}' is not a git ref.", nameof(baseRef));
        builder.Resource.AffectedBase = trimmed;
        return builder;
    }

    /// <summary>
    /// Sets the granularity of the test impact map that smart watch and "Run affected" use: <c>"class"</c> (one coverage
    /// run per test class), <c>"test"</c> (one per test: exact, slower) or <c>"auto"</c> (per class, then refined per test
    /// where a change needs it; Labcoat's default).
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithImpactMap(this IResourceBuilder<LabcoatResource> builder, string mode)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        var normalized = mode.Trim().ToLowerInvariant();
        if (!ImpactMapModes.Contains(normalized))
            throw new ArgumentException($"'{mode}' is not an impact map mode (class, test or auto).", nameof(mode));
        builder.Resource.ImpactMap = normalized;
        return builder;
    }

    /// <summary>Starts the explorer with code coverage collection on (requires Microsoft.Testing.Extensions.CodeCoverage in the test project).</summary>
    public static IResourceBuilder<LabcoatResource> WithCoverage(this IResourceBuilder<LabcoatResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.CoverageOnStart = enabled;
        return builder;
    }

    /// <summary>
    /// Uses this coverage settings file (a Microsoft.CodeCoverage configuration or a .runsettings with a CodeCoverage
    /// section) for every test project, instead of each project's own coverage.settings.xml / CodeCoverage.xml /
    /// *.runsettings or Labcoat's defaults. A relative path that exists under the AppHost directory is used from there;
    /// otherwise Labcoat looks for it next to each test project.
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithCoverageSettings(this IResourceBuilder<LabcoatResource> builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        var underAppHost = Path.GetFullPath(Path.Combine(builder.ApplicationBuilder.AppHostDirectory, trimmed));
        builder.Resource.CoverageSettings = Path.IsPathRooted(trimmed) || !File.Exists(underAppHost) ? trimmed : underAppHost;
        return builder;
    }

    /// <summary>
    /// Adds module names or path globs to the coverage report's filter: <paramref name="include"/> counts what Labcoat's
    /// defaults leave out (its own assemblies, test projects, test frameworks, generated code), e.g. <c>"Labcoat.*"</c>;
    /// <paramref name="exclude"/> is never counted, e.g. <c>"SupportDesk.DemoDataSeeder"</c> or <c>"**/Migrations/**"</c>.
    /// A glob without <c>/</c> matches a module (assembly) or file name; with <c>/</c>, a source path. Calls add up.
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithCoverageFilter(this IResourceBuilder<LabcoatResource> builder,
        IEnumerable<string>? include = null, IEnumerable<string>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Add(include, builder.Resource.CoverageInclude, nameof(include));
        Add(exclude, builder.Resource.CoverageExclude, nameof(exclude));
        return builder;

        static void Add(IEnumerable<string>? globs, IList<string> target, string parameter)
        {
            foreach (var glob in globs ?? [])
            {
                var g = glob?.Trim();
                if (string.IsNullOrEmpty(g) || g.Contains(',') || g.StartsWith('-'))
                    throw new ArgumentException($"'{glob}' is not a module name or path glob.", parameter);
                if (!target.Contains(g)) target.Add(g);
            }
        }
    }

    /// <summary>
    /// Starts the explorer with per-test trace collection on: test hosts get this dashboard's OTLP endpoint
    /// (<c>LABCOAT_OTLP_*</c>), so tests using Labcoat.Testing can jump from a failing integration test to the service
    /// traces / logs it caused, and Labcoat can show the error spans and logs inline. Off by default; it can also be
    /// toggled in the UI or with the dashboard command (that runtime choice is remembered per workspace).
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithTestTracing(this IResourceBuilder<LabcoatResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.TracingOnStart = enabled;
        return builder;
    }

    /// <summary>
    /// Shows the test suite result as the resource state in the dashboard ("✓ 12 passed", "✕ 1 failed · 20 tests",
    /// "▶ Running 3/20"; on by default). The custom state replaces "Running" once the resource is ready, so the dashboard
    /// shows no health status for it then (Aspire computes health for the "Running" state only); turn it off when
    /// something waits for this resource's health later on.
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithSuiteResultState(this IResourceBuilder<LabcoatResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.SuiteResultState = enabled;
        return builder;
    }

    /// <summary>Uses a fixed host port for the explorer UI instead of a random one.</summary>
    public static IResourceBuilder<LabcoatResource> WithHostPort(this IResourceBuilder<LabcoatResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(LabcoatResource.HttpEndpointName, e => e.Port = port);
    }

    /// <summary>
    /// Keeps these variables in the environment of the test processes. Labcoat starts tests with the environment
    /// <c>dotnet test</c> would get in your terminal: the variables of the Aspire run (<c>ASPIRE_*</c> such as
    /// <c>ASPIRE_BACKCHANNEL_PATH</c> and <c>ASPIRE_CLI_PID</c>, <c>DOTNET_DASHBOARD_*</c>, <c>DCP_*</c>,
    /// <c>DEBUG_SESSION_*</c>, the <c>OTEL_*</c> of this resource) and Labcoat's own listen address
    /// (<c>PORT</c>, <c>ASPNETCORE_URLS</c>) are removed. Name a variable here when your tests do need it, also one you
    /// set on this resource with <c>WithEnvironment</c> (Labcoat can't tell those apart from inherited ones).
    /// A trailing <c>*</c> matches a prefix: <c>WithPassThroughEnvironment("OTEL_*")</c>. Other variables (e.g.
    /// <c>DOCKER_HOST</c>, connection strings from <c>WithReference</c>) are passed on anyway.
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithPassThroughEnvironment(this IResourceBuilder<LabcoatResource> builder, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            var n = name?.Trim();
            if (string.IsNullOrEmpty(n) || n == "*" || n.Contains(';') || n.Contains(',') || n.Contains('='))
                throw new ArgumentException($"'{name}' is not an environment variable name or a PREFIX* pattern.", nameof(names));
            builder.Resource.PassThroughEnvironment.Add(n);
        }
        return builder;
    }

    /// <summary>
    /// Sets an environment variable for the tests Labcoat runs: on this resource, and kept for the test processes even
    /// when its name looks like a variable of the Aspire run (see <see cref="WithPassThroughEnvironment"/>).
    /// <code>builder.AddLabcoat("tests").WithTestEnvironment("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "true");</code>
    /// </summary>
    public static IResourceBuilder<LabcoatResource> WithTestEnvironment(this IResourceBuilder<LabcoatResource> builder, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return builder.WithPassThroughEnvironment(name).WithEnvironment(name.Trim(), value);
    }

    // ------------------------------------------------------------------ dashboard commands

    private static void AddCommands(IResourceBuilder<LabcoatResource> rb)
    {
        var resource = rb.Resource;

        rb.WithCommand("run-all-tests", "Run all tests",
            ctx => RunTestsAsync(resource, failedOnly: false, ctx.CancellationToken),
            Options(resource, "Build and run every test in the explorer and report the result.", "Play", highlighted: true));

        rb.WithCommand("run-failed-tests", "Run failed tests",
            ctx => RunTestsAsync(resource, failedOnly: true, ctx.CancellationToken),
            Options(resource, "Re-run the tests that failed or timed out in the last run.", "ArrowRepeatAll"));

        rb.WithCommand("cancel-run", "Cancel run",
            ctx => CancelAsync(resource, ctx.CancellationToken),
            Options(resource, "Cancel the running test run (and a queued one). The warm test host is kept when the tests stop in time.", "Stop"));

        rb.WithCommand("restart-test-host", "Restart test host",
            ctx => RestartHostAsync(resource, ctx.CancellationToken),
            Options(resource, "Stop the warm test host(s) and start fresh ones (cancels a hung run first).", "ArrowClockwise"));

        rb.WithCommand("toggle-watch", "Toggle watch mode",
            ctx => ToggleAsync(resource, "watch", "/api/watch", "Watch mode", ctx.CancellationToken),
            Options(resource, "Turn watch mode (rebuild + re-run on source changes) on or off.", "Eye"));

        rb.WithCommand("toggle-coverage", "Toggle code coverage",
            ctx => ToggleAsync(resource, "coverageEnabled", "/api/coverage", "Code coverage", ctx.CancellationToken),
            Options(resource, "Turn code coverage collection for the next runs on or off.", "Shield"));

        rb.WithCommand("toggle-tracing", "Toggle test tracing",
            ctx => ToggleAsync(resource, "tracingEnabled", "/api/tracing", "Test tracing", ctx.CancellationToken),
            Options(resource, "Send per-test traces of the tests to this dashboard (Labcoat.Testing), or stop sending them.", "BranchFork"));
    }

    private static CommandOptions Options(LabcoatResource resource, string description, string icon, bool highlighted = false) => new()
    {
        Description = description,
        IconName = icon,
        IconVariant = IconVariant.Regular,
        IsHighlighted = highlighted,
        UpdateState = ctx => IsUp(resource, ctx.ResourceSnapshot.State?.Text)
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled,
    };

    /// <summary>Running, or showing the suite result this integration published instead of "Running".</summary>
    private static bool IsUp(LabcoatResource resource, string? state) =>
        state == KnownResourceStates.Running || (state is not null && state == resource.SuiteStateText);

    private static async Task<ExecuteCommandResult> ToggleAsync(LabcoatResource resource, string stateProperty, string path, string label, CancellationToken ct)
    {
        try
        {
            var state = await GetJsonAsync(resource, "/api/state?lean=1", ct);
            var enabled = !(state?[stateProperty]?.GetValue<bool>() ?? false);
            await PostAsync(resource, path, new JsonObject { ["enabled"] = enabled }, ct);
            return CommandResults.Success($"{label} {(enabled ? "enabled" : "disabled")}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    private static async Task<ExecuteCommandResult> CancelAsync(LabcoatResource resource, CancellationToken ct)
    {
        try
        {
            var r = await PostAsync(resource, "/api/cancel", new JsonObject(), ct);
            var running = r?["cancelledRunning"]?.GetValue<bool>() ?? false;
            var queued = r?["clearedQueuedRequests"]?.GetValue<int>() ?? 0;
            return CommandResults.Success(CancelledMessage(r, running, queued));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    /// <summary>"Cancelled the current run and the queued run (2 requests)." / "Nothing was running."</summary>
    private static string CancelledMessage(JsonNode? response, bool running, int queued)
    {
        if (!running && queued == 0) return "Nothing was running.";
        var current = running ? $" the current {response?["activeOperation"]?.GetValue<string>() ?? "operation"}" : "";
        var and = running && queued > 0 ? " and" : "";
        var requests = queued == 1 ? "request" : "requests";
        var cleared = queued > 0 ? $" the queued run ({queued} {requests})" : "";
        return $"Cancelled{current}{and}{cleared}.";
    }

    private static async Task<ExecuteCommandResult> RestartHostAsync(LabcoatResource resource, CancellationToken ct)
    {
        try
        {
            await PostAsync(resource, "/api/restart-host", new JsonObject(), ct);
            return CommandResults.Success("Restarting the test host(s); see the Labcoat log for progress.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    /// <summary>Requests a run and waits for exactly that run (by the runId /api/run returns) to finish.</summary>
    private static async Task<ExecuteCommandResult> RunTestsAsync(LabcoatResource resource, bool failedOnly, CancellationToken ct)
    {
        try
        {
            var body = new JsonObject();
            if (failedOnly)
            {
                var state = await GetJsonAsync(resource, "/api/state?lean=1", ct);
                var failed = (state?["tests"]?.AsArray() ?? [])
                    .Where(t => t?["outcome"]?.GetValue<string>() is "failed" or "timedOut")
                    .Select(t => t!["id"]!.GetValue<string>())
                    .Distinct()
                    .ToArray();
                if (failed.Length == 0) return CommandResults.Success("No failed tests to re-run.");
                body["testIds"] = new JsonArray([.. failed.Select(id => (JsonNode?)JsonValue.Create(id))]);
            }

            var accepted = await PostAsync(resource, "/api/run", body, ct);
            if (accepted?["runId"]?.GetValue<string>() is not { } runId)
                return CommandResults.Success("Test run requested; open Labcoat for details.");
            var merged = accepted["merged"]?.GetValue<bool>() == true;
            return await WaitForRunAsync(resource, runId, merged, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResults.Failure(ex.Message);
        }
    }

    /// <summary>Polls the status of the run (queued -> building -> running -> finished / cancelled / aborted / skipped).</summary>
    private static async Task<ExecuteCommandResult> WaitForRunAsync(LabcoatResource resource, string runId, bool merged, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(resource.StatusPollInterval, ct);
            JsonNode? status;
            try { status = await GetJsonAsync(resource, $"/api/runs/{Uri.EscapeDataString(runId)}/status", ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return CommandResults.Success("Test run requested; open Labcoat for details."); }
            if (RunResult(status, merged) is { } result) return result;
            if (status?["state"]?.GetValue<string>() == "superseded")
                runId = status["supersededBy"]?.GetValue<string>() ?? runId; // a newer request replaced ours: report that one
        }
    }

    /// <summary>The command result for a finished run's status; null while it isn't finished (or was superseded).</summary>
    private static ExecuteCommandResult? RunResult(JsonNode? status, bool merged)
    {
        if (status is null) return null;
        var run = status["run"];
        int N(string key) => run?[key]?.GetValue<int>() ?? 0;
        var total = N(TotalKey);
        var counts = $"{total} test{(total == 1 ? "" : "s")}: {N(PassedKey)} passed, {N(FailedKey)} failed, {N(SkippedKey)} skipped" +
                     (N("notRun") > 0 ? $", {N("notRun")} not run" : "") + ".";
        switch (status["state"]?.GetValue<string>())
        {
            case "finished":
                if (N(FailedKey) > 0) return CommandResults.Failure(counts);
                return CommandResults.Success(counts + (merged ? " (merged with a queued run)" : ""));
            case "aborted":
                return CommandResults.Failure($"Run aborted: {status["reason"]?.GetValue<string>()} {counts}");
            case "cancelled":
                return CommandResults.Failure("The run was cancelled." + (run is null ? "" : " " + counts));
            case "skipped":
                return CommandResults.Failure("Nothing ran: " + (status["reason"]?.GetValue<string>() ?? "see Labcoat for details."));
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ suite result as resource state

    /// <summary>
    /// Polls <c>/api/summary</c> every few seconds while the explorer runs and publishes the suite result as the
    /// resource's state text ("✓ 12 passed", "✕ 1 failed · 20 tests", "▶ Running 3/20", ...) with a matching state style.
    /// DCP may set the state back to "Running" on its own updates; the next poll restores the result.
    /// </summary>
    internal static async Task PublishSuiteStateAsync(LabcoatResource resource, ResourceNotificationService notifications, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(resource.SuitePollInterval, ct); }
            catch (OperationCanceledException) { return; }
            if (resource.SuiteResultState) await UpdateSuiteStateAsync(resource, notifications, ct);
        }
    }

    private static async Task UpdateSuiteStateAsync(LabcoatResource resource, ResourceNotificationService notifications, CancellationToken ct)
    {
        // Only once the resource is ready: a custom state text has no HealthStatus (Aspire computes health for
        // "Running" only), so WaitFor(...) on this resource must have been released before.
        if (!notifications.TryGetCurrentState(resource.Name, out var current) || !IsUp(resource, current.Snapshot.State?.Text)
            || !resource.IsReady)
        {
            if (current?.Snapshot.State?.Text is { } st && KnownResourceStates.TerminalStates.Contains(st)) resource.IsReady = false; // restarts become ready again
            resource.SuiteStateText = null;
            return;
        }
        var (fetched, summary) = await TryGetSummaryAsync(resource, ct);
        if (!fetched) return;
        var (text, style) = DescribeSuite(summary);
        if (text is null || current.Snapshot.State?.Text == text && current.Snapshot.State?.Style == style) return;
        await PublishSuiteTextAsync(resource, notifications, text, style);
    }

    private static async Task<(bool Fetched, JsonNode? Summary)> TryGetSummaryAsync(LabcoatResource resource, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return (true, await GetJsonAsync(resource, "/api/summary", timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return (false, null);
        }
    }

    private static async Task PublishSuiteTextAsync(LabcoatResource resource, ResourceNotificationService notifications, string text, string? style)
    {
        var previous = resource.SuiteStateText;
        resource.SuiteStateText = text;
        try
        {
            // Only replace "Running" or our own previous text: never a state DCP set meanwhile (Stopping, Exited, ...).
            await notifications.PublishUpdateAsync(resource, s =>
                s.State?.Text is { } t && (t == KnownResourceStates.Running || t == previous || t == text)
                    ? s with { State = new ResourceStateSnapshot(text, style) }
                    : s);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort: the dashboard state is cosmetic, a failed update must not stop the resource */ }
    }

    /// <summary>State text + style for a <c>/api/summary</c> response; (null, null) while nothing is known yet.</summary>
    internal static (string? Text, string? Style) DescribeSuite(JsonNode? summary)
    {
        if (summary is null) return (null, null);
        // Cancel requested; the test host may take the cancel grace period to stop. Not "▶ Running" meanwhile.
        if (summary["cancelling"]?.GetValue<bool>() == true) return ("◼ Cancelling…", KnownResourceStateStyles.Warn);
        if (summary["currentRun"] is { } current) return DescribeRunning(current);
        var projects = summary["projects"]?.AsArray() ?? [];
        if (projects.Any(p => p?["status"]?.GetValue<string>() is "building" or "discovering"))
            return ("◌ Building", KnownResourceStateStyles.Info);
        if (projects.Any(p => p?["status"]?.GetValue<string>() == "error"))
            return ("✕ Build / host error", KnownResourceStateStyles.Error);
        return DescribeResults(summary, projects.Count);
    }

    private static (string Text, string Style) DescribeRunning(JsonNode current)
    {
        int N(string key) => current[key]?.GetValue<int>() ?? 0;
        var done = N(PassedKey) + N(FailedKey) + N(SkippedKey);
        var failing = N(FailedKey);
        return ($"▶ Running {done}/{N(TotalKey)}" + (failing > 0 ? $" · {failing} failed" : ""),
            failing > 0 ? KnownResourceStateStyles.Warn : KnownResourceStateStyles.Info);
    }

    private static (string Text, string Style) DescribeResults(JsonNode summary, int projectCount)
    {
        int Count(string key) => summary["tests"]?[key]?.GetValue<int>() ?? 0;
        var total = Count(TotalKey);
        if (total == 0) return (projectCount == 0 ? "No test projects" : "No tests", KnownResourceStateStyles.Warn);
        var failed = Count(FailedKey);
        var passed = Count(PassedKey);
        var skipped = Count(SkippedKey);
        var lastRun = summary["lastRun"];
        if (failed > 0) return ($"✕ {failed} failed · {total} tests", KnownResourceStateStyles.Error);
        if (lastRun?["aborted"]?.GetValue<bool>() == true) return ($"✕ Run aborted · {total} tests", KnownResourceStateStyles.Error);
        if (passed == 0) return ($"○ {total} tests · not run", KnownResourceStateStyles.Info);
        if (passed + skipped < total) return ($"✓ {passed} passed · {total - passed - skipped} not run", KnownResourceStateStyles.Success);
        return ($"✓ {passed} passed" + (skipped > 0 ? $" · {skipped} skipped" : ""), KnownResourceStateStyles.Success);
    }

    private static async Task<JsonNode?> GetJsonAsync(LabcoatResource resource, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(resource, path));
        request.Headers.Add(TokenHeader, resource.ApiToken);
        using var response = await (resource.Http ?? s_http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }

    // JsonNode-based (no reflection serialization), so it also works in AppHosts with reflection-based JSON disabled.
    private static async Task<JsonNode?> PostAsync(LabcoatResource resource, string path, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(resource, path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TokenHeader, resource.ApiToken);
        using var response = await (resource.Http ?? s_http).SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            try { error = JsonNode.Parse(text)?["error"]?.GetValue<string>(); } catch (System.Text.Json.JsonException) { /* not JSON: the raw text is reported instead */ }
            throw new HttpRequestException($"Labcoat answered {(int)response.StatusCode}{(error is null ? "" : ": " + error)}", null, response.StatusCode);
        }
        try { return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private const string TokenHeader = "X-Labcoat-Token";

    // Counts of /api/summary (tests, currentRun) and of a run's status (run).
    private const string TotalKey = "total";
    private const string PassedKey = "passed";
    private const string FailedKey = "failed";
    private const string SkippedKey = "skipped";

    private static Uri Url(LabcoatResource resource, string path) => new(new Uri(resource.HttpEndpoint.Url), path);

    // ------------------------------------------------------------------ helpers

    /// <summary>Directory of the nearest .sln/.slnx walking up from <paramref name="appHostDirectory"/>, else its parent.</summary>
    internal static string FindSolutionRoot(string appHostDirectory)
    {
        for (var dir = new DirectoryInfo(appHostDirectory); dir is not null; dir = dir.Parent)
        {
            try
            {
                if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
                    return dir.FullName;
            }
            catch (IOException) { /* unreadable folder: keep walking up */ }
            catch (UnauthorizedAccessException) { /* same */ }
        }
        return Directory.GetParent(appHostDirectory)?.FullName ?? appHostDirectory;
    }

    private static bool IsRunningUnderAspireTesting() =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Aspire.Hosting.Testing");
}
