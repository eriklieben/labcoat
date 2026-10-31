using System.Text.Json;

namespace Labcoat;

/// <summary>
/// The Labcoat host: <see cref="RunAsync"/> is the whole program (command line, discovery, settings, port, banner,
/// start); <see cref="Build"/> creates the web app (services, security middleware, UI, REST API and WebSocket).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling",
    Justification = "The whole program: command line, services, middleware and every API endpoint. "
        + "Over the limit since before CA1506 was on: don't add to it, and remove this once a split brings it "
        + "under (src/CodeMetricsConfig.txt).")]
public static partial class LabcoatApp
{
    /// <summary>Runs Labcoat until shutdown (Ctrl+C / SIGTERM, or <paramref name="ct"/>); returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        // ------------------------------------------------------------------ CLI
        // merge: combine the report files of a split run (CliMerge.cs); no workspace, no build, no web server.
        if (CliMerge.IsMerge(args)) return CliMerge.Run(args.AsSpan(1).ToArray());
        var options = StartOptions.Parse(args, out var argError);
        if (options is null) return Fail(argError!, exitCode: ErrorExitCode(args));
        if (options.PrintAndExit is { } text)
        {
            Console.WriteLine(text);
            return 0;
        }
        var paths = options.Paths;
        // No paths: the test project in the current directory, else the nearest solution walking up, else the current directory.
        if (paths.Count == 0) paths.Add(Cli.ResolveDefaultPath(Directory.GetCurrentDirectory(), Cli.Warn));

        var startupNotes = new List<string>(); // printed below the banner
        var projects = WorkspaceDiscovery.FindProjects(paths, startupNotes, out var pathError);
        if (pathError is not null) return Fail(pathError, usageHint: false, ErrorExitCode(options));

        // Settings files + layered UI defaults (LabcoatConfig.cs): user file < workspace file < AppHost < LABCOAT_UI_SETTINGS < CLI.
        var config = StartupSettings.LoadConfig(options, paths, out var configError);
        if (config is null) return Fail(configError!, usageHint: false, ErrorExitCode(options));
        var watch = options.Watch;
        var coverage = options.Coverage;
        var traces = options.Traces;
        watch |= config.Watch;
        coverage |= config.Coverage;
        traces |= config.Traces;
        var affectedBase = StartupSettings.ResolveAffectedBase(options, config);
        var impactMap = StartupSettings.ResolveImpactMap(options, config);
        var traceFilter = StartupSettings.ResolveTraceFilter(options, config, out var traceFilterError);
        if (traceFilterError is not null) return Fail(traceFilterError, usageHint: false, ErrorExitCode(options));
        // coverage settings: the command line's file wins over the settings files'; include / exclude lists add up.
        var coverageOptions = config.CoverageOptions.Merge(new CoverageOptions
        {
            Settings = options.CoverageSettings, BaseDirectory = Directory.GetCurrentDirectory(),
            Include = options.CoverageInclude, Exclude = options.CoverageExclude,
        });

        var aspireDashboardUrl = AspireDashboardUrl();

        // Host log level and file (StartupSettings.cs, HostLogging.cs); --diagnostics prints the report instead of starting.
        var logLevel = StartupSettings.ResolveLogLevel(options, config);
        var logFile = StartupSettings.ResolveLogFile(options, projects);
        if (options.Diagnostics)
            return await Diagnostics.PrintAsync(projects, new DiagnosticsSettings
            {
                Watch = watch, Coverage = coverage, Traces = traces, TraceFilter = traceFilter, AffectedBase = affectedBase, ImpactMap = impactMap,
                CoverageOptions = coverageOptions, UiDefaults = config.UiDefaults, SettingsFiles = config.FilesForDisplay,
                LogLevel = logLevel, LogFile = logFile,
            }, ct);
        if (logFile is not null) startupNotes.Add($"Log file: {logFile}");

        LabcoatAppOptions AppOptions(string? url, RequestGuard guard, string webRoot) => new()
        {
            Paths = paths,
            Projects = projects,
            ExtraRoots = options.ExtraRoots,
            Url = url,
            Guard = guard,
            WebRoot = webRoot,
            UiDefaults = config.UiDefaults,
            AspireDashboardUrl = aspireDashboardUrl,
            WorkspaceName = Environment.GetEnvironmentVariable("LABCOAT_NAME"), // set by Labcoat.Aspire.Hosting to the resource name
            Traces = traces,
            TraceFilter = traceFilter,
            AffectedBase = affectedBase,
            ImpactMap = impactMap,
            Coverage = coverageOptions,
            Limits = StartupSettings.ResolveRunLimits(options, config),
            SettingsFiles = config.FilesForDisplay,
            LogLevel = logLevel,
            LogFile = logFile,
        };
        // --run: build, run once, write the exports, exit (CliRun.cs); no web server.
        if (options.RunOnce) return await CliRun.RunAsync(options, AppOptions(null, new RequestGuard([]), ""), coverage, startupNotes, ct);

        // ------------------------------------------------------------------ host
        var listen = ListenAddress.Resolve(options.Port, config, out var portError);
        if (listen is null) return Fail(portError!, usageHint: false);
        var url = listen.Url;

        var app = Build(AppOptions(url, CreateGuard(url), WebUi.FindWebRoot()));
        var explorer = app.Services.GetRequiredService<Explorer>();
        if (await StartOrFailAsync(app, explorer, listen.Port, ct) is { } exitCode) return exitCode;

        var address = app.Urls.FirstOrDefault() ?? url ?? $"http://localhost:{Cli.DefaultPort}";
        address = ListenAddress.Display(address);
        Cli.PrintBanner(address, paths, projects, listen.Note, aspireDashboardUrl, config.FilesForDisplay);
        LogStartupNotes(explorer, config, startupNotes);

        if (options.Open && aspireDashboardUrl is null) Cli.OpenBrowser(address);
        Console.WriteLine();
        explorer.ReleaseConsole(); // startup log lines (e.g. "Loaded test history…") below the banner, not above it
        StartExplorer(explorer, watch, coverage);

        await app.WaitForShutdownAsync(ct);
        return 0;
    }

    /// <summary>The Aspire dashboard's URL when Labcoat runs as an Aspire resource (Labcoat.Aspire.Hosting sets it).</summary>
    private static string? AspireDashboardUrl() =>
        Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_URL") is { } url && !string.IsNullOrWhiteSpace(url) ? url : null;

    /// <summary>DNS rebinding / cross-site protection (Security.cs, SECURITY.md): the Host names this server answers to.</summary>
    private static RequestGuard CreateGuard(string? url)
    {
        var bindHosts = RequestGuard.HostsOfUrls(url ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")).ToList();
        var guard = RequestGuard.FromEnvironment(bindHosts);
        if (guard.AllowsAnyHost) Cli.Warn("LABCOAT_ALLOWED_HOSTS contains '*': the Host header is not checked (DNS rebinding protection is off).");
        return guard;
    }

    /// <summary>Turns on the startup modes, then discovers the tests and watches for new / removed test projects.</summary>
    private static void StartExplorer(Explorer explorer, bool watch, bool coverage)
    {
        if (watch) explorer.SetWatch(true);
        if (coverage) explorer.SetCoverage(true);
        _ = explorer.DiscoverAsync(null);
        explorer.StartProjectWatcher(); // new / removed test projects (Explorer.Projects.cs)
    }

    /// <summary>Starts the app; null when it runs, else the exit code (the port is in use).</summary>
    private static async Task<int?> StartOrFailAsync(WebApplication app, Explorer explorer, int? port, CancellationToken ct)
    {
        try
        {
            await app.StartAsync(ct);
            return null;
        }
        catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            explorer.ReleaseConsole();
            await app.DisposeAsync();
            return Fail(port is not null
                ? $"Port {port} is already in use. Pass another --port, or leave it out to pick a free one."
                : $"Could not bind: {ex.Message}", usageHint: false);
        }
    }

    /// <summary>Below the banner: discovery notes, settings warnings and the UI defaults in effect.</summary>
    private static void LogStartupNotes(Explorer explorer, LabcoatConfig config, List<string> startupNotes)
    {
        foreach (var note in startupNotes) Console.WriteLine(Cli.Dim("  " + note));
        foreach (var warning in config.Warnings) explorer.Log(null, "warning", "Settings: " + warning);
        if (config.UiDefaults is not null) explorer.Log(null, "info", "UI defaults: " + UiLayer.Describe(config.UiDefaults));
    }

    /// <summary>
    /// Creates the web app: the <see cref="Explorer"/> and <see cref="EventHub"/> services, the security middleware
    /// (Host / Origin / token checks, response headers), index.html + static files, the REST API and <c>/ws</c>.
    /// <paramref name="configure"/> runs before the app is built (tests: <c>b.WebHost.UseTestServer()</c>).
    /// </summary>
    public static WebApplication Build(LabcoatAppOptions o, Action<WebApplicationBuilder>? configure = null)
    {
        var webRoot = o.WebRoot;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = webRoot,
        });
        var hostLog = HostLogging.Configure(builder, o); // --verbose / LABCOAT_LOG_LEVEL, --log-file (HostLogging.cs)
        // A port that is already in use is reported as one line (RunAsync) instead of the host's stack trace.
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.Critical);
        if (o.Url is not null) builder.WebHost.UseUrls(o.Url);

        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton(sp => CreateExplorer(o, sp.GetRequiredService<EventHub>()));
        builder.Services.ConfigureHttpJsonOptions(j =>
        {
            j.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            j.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        configure?.Invoke(builder);

        var app = builder.Build();
        HostLogging.Attach(app, hostLog); // before the explorer exists: its first log lines go to the log file too
        var explorer = app.Services.GetRequiredService<Explorer>();
        var hub = app.Services.GetRequiredService<EventHub>();

        // index.html with the UI defaults as an <html data-labcoat-defaults> attribute (no flash of the wrong theme).
        var indexPage = new IndexPage(webRoot, o.UiDefaults);
        WebUi.Use(app, o.Guard, new SecurityHeaders(webRoot), indexPage, webRoot);

        // ------------------------------------------------------------------ API
        MapStateEndpoints(app, explorer);
        MapCommandEndpoints(app, explorer);
        MapAffectedEndpoints(app, explorer);
        MapTestEndpoints(app, explorer);
        MapCoverageEndpoints(app, explorer);
        MapExportEndpoints(app, explorer); // JUnit / HTML report / lcov / Cobertura downloads (LabcoatApp.Export.cs)
        // Verify snapshots of failed tests: list, read, accept, reject (Snapshots.cs).
        SnapshotEndpoints.Map(app, explorer);
        MapSourceEndpoints(app, explorer);
        Diagnostics.Map(app, o, explorer, hostLog); // GET /api/diagnostics (Diagnostics.cs)

        app.Map("/ws", (HttpContext ctx) => HandleWebSocketAsync(ctx, hub, explorer, app.Lifetime.ApplicationStopping));

        app.MapFallback(async ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await WebUi.ServeIndex(ctx, indexPage);
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            Console.WriteLine(Cli.Dim("  Stopping Labcoat (shutting down test hosts)..."));
            explorer.DisposeAsync().AsTask().Wait();
        });
        return app;
    }

    /// <summary>The explorer of the web app (and of <c>--run</c>, CliRun.cs, which has no web app).</summary>
    internal static Explorer CreateExplorer(LabcoatAppOptions o, EventHub hub)
    {
        // Source roots for /api/source: the common root of the input paths plus any --root directories.
        var sourceRoots = new List<string>(o.ExtraRoots);
        if (PathUtil.CommonRoot(o.Paths) is { } inputRoot) sourceRoots.Insert(0, inputRoot);
        var uiDefaultsJson = o.UiDefaults is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(o.UiDefaults);
        return new Explorer(hub, o.Projects, o.AspireDashboardUrl, sourceRoots,
            o.WorkspaceName, tracingOnStart: o.Traces, inputPaths: o.Paths) { UiDefaults = uiDefaultsJson, AffectedBase = o.AffectedBase, CoverageOptions = o.Coverage,
              TraceFilter = o.TraceFilter,
              ImpactMapMode = o.ImpactMap ?? ImpactMapModes.Default,
              MaxParallelProjects = o.Limits.EffectiveMaxParallel, HangThreshold = o.Limits.EffectiveHangThreshold, TestTimeout = o.Limits.EffectiveTestTimeout,
              ProjectFinder = warn => ProjectDiscovery.FindProjects(o.Paths, warn), WorkspaceRoots = WorkspaceDiscovery.WorkspaceRootsOf(o.Paths) };
    }



    private static int Fail(string message, bool usageHint = true, int exitCode = 1)
    {
        Console.Error.WriteLine($"{Cli.Red("error")} {message}{(usageHint ? " Use --help for usage." : "")}");
        return exitCode;
    }

    /// <summary>The exit code of a startup error: 2 under --run (CliRun.ExitBuildError: CI tells it from failed tests), else 1.</summary>
    private static int ErrorExitCode(StartOptions options) => options.RunOnce ? CliRun.ExitBuildError : 1;

    /// <summary>The same for a command line that didn't parse.</summary>
    private static int ErrorExitCode(string[] args) => args.Contains("--run") ? CliRun.ExitBuildError : 1;
}
