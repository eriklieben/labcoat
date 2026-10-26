using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;

namespace Labcoat;

/// <summary>The parsed command line (see <see cref="Cli.Usage"/>).</summary>
public sealed partial class StartOptions
{
    public List<string> Paths { get; } = [];
    public int? Port { get; set; }
    public bool Open { get; set; } = true;
    public bool Watch { get; set; }
    public bool Coverage { get; set; }
    public bool Traces { get; set; }
    /// <summary>--trace-filter: only the tests matching it get a trace (Labcoat.Testing; the UI's search syntax).</summary>
    public string? TraceFilter { get; set; }
    public List<string> ExtraRoots { get; } = [];
    /// <summary>--affected-base: base ref of "Run affected: branch vs …".</summary>
    public string? AffectedBase { get; set; }
    /// <summary>--impact-map: granularity of the impact-map pass ("class" | "test" | "auto").</summary>
    public string? ImpactMap { get; set; }
    /// <summary>--max-parallel: projects discovered / run at once (RunLimits.cs).</summary>
    public int? MaxParallel { get; set; }
    /// <summary>--hang-threshold: a running test is shown as possibly hanging after this long.</summary>
    public TimeSpan? HangThreshold { get; set; }
    /// <summary>--test-timeout: per-test timeout (zero = off).</summary>
    public TimeSpan? TestTimeout { get; set; }
    public string? ConfigFile { get; set; }
    public bool NoUserConfig { get; set; }
    /// <summary>--coverage-settings: the coverage settings file for every test project (relative: to each project, then the current directory).</summary>
    public string? CoverageSettings { get; set; }
    /// <summary>--coverage-include / --coverage-exclude: module names or path globs (repeatable, comma-separated).</summary>
    public List<string> CoverageInclude { get; } = [];
    public List<string> CoverageExclude { get; } = [];
    /// <summary>UI defaults from the command line (--theme, --mode, --editor, --motion, --lock): the highest settings layer.</summary>
    public UiLayer CliUi { get; } = new(ConfigLayer.Cli, "the command line");
    /// <summary>--help / --version: print this and exit with code 0.</summary>
    public string? PrintAndExit { get; private set; }
    /// <summary>-v / --verbose: the host log at Debug level.</summary>
    public bool Verbose { get; set; }
    /// <summary>--log-file: the log file ("auto" = labcoat.log in the workspace data folder).</summary>
    public string? LogFile { get; set; }
    /// <summary>--diagnostics: print the diagnostics report and exit.</summary>
    public bool Diagnostics { get; set; }

    private static readonly Dictionary<string, string> CliUiFlags = new(StringComparer.Ordinal)
    {
        ["--theme"] = "theme", ["--mode"] = "mode", ["--editor"] = "editor", ["--motion"] = "motion",
    };

    /// <summary>Parses the arguments; null + <paramref name="error"/> for an invalid command line.</summary>
    public static StartOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var o = new StartOptions();
        var reader = new ArgReader(args);
        while (reader.Next() is { } a)
        {
            error = o.Apply(a, reader);
            if (error is not null) return null;
            if (o.PrintAndExit is not null) return o;
        }
        error = o.ValidateRunOptions();
        return error is null ? o : null;
    }

    /// <summary>Walks the arguments; the value options also come as <c>--name=value</c>.</summary>
    private sealed class ArgReader(IReadOnlyList<string> args)
    {
        private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
        {
            "--config", "--theme", "--mode", "--editor", "--motion", "--lock", "--affected-base", "--impact-map", "--max-parallel", "--hang-threshold", "--test-timeout",
            "--coverage-settings", "--coverage-include", "--coverage-exclude", "--log-file", "--trace-filter",
            "--export", "--filter", "--export-paths", "--shard", "--shard-by", "--shard-timings", // --run (CliRun.cs)
        };

        private int _index = -1;
        private string? _inlineValue;

        /// <summary>The next argument (the name of a <c>--name=value</c> value option); null after the last one.</summary>
        public string? Next()
        {
            if (++_index >= args.Count) return null;
            var a = args[_index];
            _inlineValue = null;
            if (a.StartsWith("--", StringComparison.Ordinal) && a.IndexOf('=') is > 2 and var eq && ValueOptions.Contains(a[..eq]))
            {
                _inlineValue = a[(eq + 1)..];
                return a[..eq];
            }
            return a;
        }

        /// <summary>The option's value: its <c>=value</c>, else the next argument (consumed); null when there is none.</summary>
        public string? Value()
        {
            if (_inlineValue is not null) return _inlineValue;
            if (_index + 1 >= args.Count) return null;
            return args[++_index];
        }
    }

    /// <summary>Applies one option (or path); returns the error for an invalid one.</summary>
    private string? Apply(string a, ArgReader args)
    {
        switch (a)
        {
            case "--config":
                ConfigFile = args.Value();
                return ErrorUnless(!string.IsNullOrEmpty(ConfigFile), "--config needs a file.");
            case "--no-user-config": NoUserConfig = true; return null;
            case "--coverage-settings":
                CoverageSettings = args.Value();
                return ErrorUnless(!string.IsNullOrWhiteSpace(CoverageSettings), "--coverage-settings needs a file.");
            case "--coverage-include" or "--coverage-exclude": return AddCoverageGlobs(a, args.Value());
            case "--affected-base":
                AffectedBase = args.Value();
                return ErrorUnless(GitRepo.IsValidRef(AffectedBase), "--affected-base needs a git ref, e.g. origin/main.");
            case "--impact-map":
                ImpactMap = ImpactMapModes.Parse(args.Value());
                return ErrorUnless(ImpactMap is not null, "--impact-map needs class, test or auto.");
            case "--max-parallel":
                MaxParallel = RunLimits.ParseMaxParallel(args.Value());
                return ErrorUnless(MaxParallel is not null, $"--max-parallel needs a number from 1 to {RunLimits.MaxParallelLimit}.");
            case "--hang-threshold" or "--test-timeout": return SetDuration(a, args.Value());
            case "--theme" or "--mode" or "--editor" or "--motion": return SetUi(a, args.Value());
            case "--lock": return Lock(args.Value());
            case "--port" or "-p": return SetPort(args.Value(), $"{a} needs a port number (1-65535).");
            case "--no-open": Open = false; return null;
            case "--watch" or "-w": Watch = true; return null;
            case "--coverage": Coverage = true; return null;
            case "--traces": Traces = true; return null;
            case "--trace-filter": return SetTraceFilter(args.Value());
            case "--root": return AddRoot(args.Value());
            case "--verbose" or "-v": Verbose = true; return null;
            case "--diagnostics": Diagnostics = true; return null;
            case "--run" or "--export" or "--filter" or "--export-paths" or "--shard" or "--shard-by" or "--shard-timings": return ApplyRunOption(a, args); // CliRun.cs
            case "--log-file":
                LogFile = args.Value();
                return ErrorUnless(!string.IsNullOrWhiteSpace(LogFile), "--log-file needs a file (or 'auto').");
            case "--help" or "-h" or "-?": PrintAndExit = Cli.Usage; return null;
            case "--version": PrintAndExit = Cli.Version; return null;
            default: return ApplyOther(a);
        }
    }

    private static string? ErrorUnless(bool valid, string error) => valid ? null : error;

    /// <summary>--trace-filter: checked now, so an invalid filter stops Labcoat instead of reaching every test.</summary>
    private string? SetTraceFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"--trace-filter needs a filter: {Labcoat.Testing.TraceFilterSyntax.Expected}.";
        if (Labcoat.Testing.TraceFilterSyntax.Validate(value) is { } error) return $"--trace-filter: {error}";
        TraceFilter = value.Trim();
        return null;
    }

    /// <summary>--hang-threshold / --test-timeout: seconds or a duration ("90s", "5m"); 0 or "off" turns it off.</summary>
    private string? SetDuration(string option, string? value)
    {
        var d = RunLimits.ParseDuration(value);
        if (d is null) return $"{option} needs seconds or a duration such as 90s or 5m (0 or off turns it off).";
        if (option == "--hang-threshold") HangThreshold = d;
        else TestTimeout = d;
        return null;
    }

    private string? AddCoverageGlobs(string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"{option} needs module names or path globs (comma-separated).";
        (option == "--coverage-include" ? CoverageInclude : CoverageExclude).AddRange(RequestGuard.SplitList(value));
        return null;
    }

    private string? SetUi(string option, string? value)
    {
        var key = CliUiFlags[option];
        if (string.IsNullOrEmpty(value)) return $"{option} needs a value: {UiSettings.Find(key)!.Expected}.";
        return CliUi.TrySet(key, JsonValue.Create(value), out var uiError) ? null : $"{option}: {uiError}.";
    }

    private string? Lock(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "--lock needs setting names (comma-separated) or 'all'.";
        foreach (var name in RequestGuard.SplitList(value))
            if (!CliUi.TryLock(name, out var lockError)) return $"--lock: {lockError}.";
        return null;
    }

    private string? SetPort(string? value, string error)
    {
        if (value is null || !int.TryParse(value, out var port) || port is < 1 or > 65535) return error;
        Port = port;
        return null;
    }

    private string? AddRoot(string? value)
    {
        if (value is null) return "--root needs a directory.";
        ExtraRoots.Add(Path.GetFullPath(value));
        return null;
    }

    /// <summary><c>--port=N</c>, <c>--root=DIR</c>, an unknown option, or a path.</summary>
    private string? ApplyOther(string a)
    {
        if (a.StartsWith("--port=", StringComparison.Ordinal)) return SetPort(a["--port=".Length..], "--port needs a port number (1-65535).");
        if (a.StartsWith("--root=", StringComparison.Ordinal) && a.Length > "--root=".Length) ExtraRoots.Add(Path.GetFullPath(a["--root=".Length..]));
        else if (a.StartsWith('-') && a != "-") return $"Unknown option '{a}'.";
        else Paths.Add(a);
        return null;
    }
}

/// <summary>What <see cref="LabcoatApp.Build"/> needs: the resolved workspace and host settings.</summary>
public sealed class LabcoatAppOptions
{
    /// <summary>The input paths (command line, or the resolved default).</summary>
    public required IReadOnlyList<string> Paths { get; init; }
    /// <summary>The test projects found for <see cref="Paths"/>.</summary>
    public required IReadOnlyList<string> Projects { get; init; }
    /// <summary>--root directories (absolute).</summary>
    public IReadOnlyList<string> ExtraRoots { get; init; } = [];
    /// <summary>Listen URL; null = leave ASPNETCORE_URLS as configured.</summary>
    public string? Url { get; init; }
    public required RequestGuard Guard { get; init; }
    public required string WebRoot { get; init; }
    /// <summary>Merged UI defaults (LabcoatConfig.UiDefaults).</summary>
    public JsonObject? UiDefaults { get; init; }
    public string? AspireDashboardUrl { get; init; }
    /// <summary>LABCOAT_NAME (set by Labcoat.Aspire.Hosting to the resource name).</summary>
    public string? WorkspaceName { get; init; }
    public bool Traces { get; init; }
    /// <summary>The trace filter passed to test hosts while tracing is on (--trace-filter > LABCOAT_TRACE_FILTER > settings files).</summary>
    public string? TraceFilter { get; init; }
    /// <summary>Server option affectedBase (CLI > LABCOAT_AFFECTED_BASE > settings files).</summary>
    public string? AffectedBase { get; init; }
    /// <summary>Server option impactMap (CLI > LABCOAT_IMPACT_MAP > settings files; default "auto").</summary>
    public string? ImpactMap { get; init; }
    /// <summary>Coverage settings file and include / exclude globs (settings files &lt; command line).</summary>
    public CoverageOptions Coverage { get; init; } = CoverageOptions.None;
    /// <summary>maxParallelProjects, hangThreshold, testTimeout (LabcoatApp.Limits.cs).</summary>
    public RunLimitOptions Limits { get; init; } = RunLimitOptions.Default;
    /// <summary>The settings files in use (for the diagnostics report).</summary>
    public IReadOnlyList<string> SettingsFiles { get; init; } = [];
    /// <summary>The ASP.NET Core log level (--verbose / LABCOAT_LOG_LEVEL; default Warning).</summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Warning;
    /// <summary>The log file (full path), or null.</summary>
    public string? LogFile { get; init; }
}

/// <summary>
/// The Labcoat host: <see cref="RunAsync"/> is the whole program (command line, discovery, settings, port, banner,
/// start); <see cref="Build"/> creates the web app (services, security middleware, UI, REST API and WebSocket).
/// </summary>
public static partial class LabcoatApp
{
    /// <summary>Server option affectedBase from the environment (Labcoat.Aspire.Hosting's WithAffectedBase sets it).</summary>
    public const string AffectedBaseEnv = "LABCOAT_AFFECTED_BASE";
    /// <summary>Server option impactMap from the environment (Labcoat.Aspire.Hosting's WithImpactMap sets it).</summary>
    public const string ImpactMapEnv = "LABCOAT_IMPACT_MAP";
    /// <summary>The trace filter from the environment (Labcoat.Aspire.Hosting's WithTestTracing(filter:) sets it).</summary>
    public const string TraceFilterEnv = "LABCOAT_TRACE_FILTER";

    /// <summary>Runs Labcoat until shutdown (Ctrl+C / SIGTERM, or <paramref name="ct"/>); returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        // ------------------------------------------------------------------ CLI
        // merge: combine the report files of a split run (CliMerge.cs); no workspace, no build, no web server.
        if (CliMerge.IsMerge(args)) return CliMerge.Run(args.AsSpan(1).ToArray());
        var options = StartOptions.Parse(args, out var argError);
        if (options is null) return Fail(argError!, exitCode: args.Contains("--run") ? CliRun.ExitBuildError : 1);
        if (options.PrintAndExit is { } text)
        {
            Console.WriteLine(text);
            return 0;
        }
        var paths = options.Paths;
        // No paths: the test project in the current directory, else the nearest solution walking up, else the current directory.
        if (paths.Count == 0) paths.Add(Cli.ResolveDefaultPath(Directory.GetCurrentDirectory(), Cli.Warn));

        var startupNotes = new List<string>(); // printed below the banner
        var projects = FindProjects(paths, startupNotes, out var pathError);
        if (pathError is not null) return Fail(pathError, usageHint: false, ErrorExitCode(options));

        // Settings files + layered UI defaults (LabcoatConfig.cs): user file < workspace file < AppHost < LABCOAT_UI_SETTINGS < CLI.
        var config = LoadConfig(options, paths, out var configError);
        if (config is null) return Fail(configError!, usageHint: false, ErrorExitCode(options));
        var watch = options.Watch;
        var coverage = options.Coverage;
        var traces = options.Traces;
        watch |= config.Watch;
        coverage |= config.Coverage;
        traces |= config.Traces;
        var affectedBase = ResolveAffectedBase(options, config);
        var impactMap = ResolveImpactMap(options, config);
        var traceFilter = ResolveTraceFilter(options, config, out var traceFilterError);
        if (traceFilterError is not null) return Fail(traceFilterError, usageHint: false, ErrorExitCode(options));
        // coverage settings: the command line's file wins over the settings files'; include / exclude lists add up.
        var coverageOptions = config.CoverageOptions.Merge(new CoverageOptions
        {
            Settings = options.CoverageSettings, BaseDirectory = Directory.GetCurrentDirectory(),
            Include = options.CoverageInclude, Exclude = options.CoverageExclude,
        });

        var aspireDashboardUrl = AspireDashboardUrl();

        // Host log level and file (LabcoatApp.Diagnostics.cs); --diagnostics prints the report instead of starting.
        var logLevel = ResolveLogLevel(options, config);
        var logFile = ResolveLogFile(options, projects);
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
            Limits = ResolveRunLimits(options, config),
            SettingsFiles = config.FilesForDisplay,
            LogLevel = logLevel,
            LogFile = logFile,
        };
        // --run: build, run once, write the exports, exit (CliRun.cs); no web server.
        if (options.RunOnce) return await CliRun.RunAsync(options, AppOptions(null, new RequestGuard([]), ""), coverage, startupNotes, ct);

        // ------------------------------------------------------------------ host
        var listen = ResolveListenAddress(options.Port, config, out var portError);
        if (listen is null) return Fail(portError!, usageHint: false);
        var url = listen.Url;

        var app = Build(AppOptions(url, CreateGuard(url), FindWebRoot()));
        var explorer = app.Services.GetRequiredService<Explorer>();
        if (await StartOrFailAsync(app, explorer, listen.Port, ct) is { } exitCode) return exitCode;

        var address = app.Urls.FirstOrDefault() ?? url ?? $"http://localhost:{Cli.DefaultPort}";
        address = DisplayAddress(address);
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

    /// <summary>A listen address as a URL to open: wildcard hosts become localhost.</summary>
    private static string DisplayAddress(string address)
    {
        const string Localhost = "://localhost";
        return address.Replace("://[::]", Localhost).Replace("://0.0.0.0", Localhost).Replace("://+", Localhost).Replace("://*", Localhost);
    }

    /// <summary>The test projects of <paramref name="paths"/>; <paramref name="error"/> is the first path error (the others are printed).</summary>
    private static List<string> FindProjects(List<string> paths, List<string> startupNotes, out string? error)
    {
        var pathErrors = new List<string>();
        var projects = ProjectDiscovery.FindProjects(paths, Cli.Warn, pathErrors, info: startupNotes.Add);
        error = null;
        if (pathErrors.Count == 0) return projects;
        foreach (var e in pathErrors.Skip(1)) Console.Error.WriteLine($"{Cli.Red("error")} {e}");
        error = pathErrors[0];
        return projects;
    }

    private static LabcoatConfig? LoadConfig(StartOptions options, List<string> paths, out string? error)
    {
        error = null;
        try
        {
            return LabcoatConfig.Load(LabcoatConfig.Inputs.FromEnvironment(PathUtil.CommonRoot(paths), options.ConfigFile, options.NoUserConfig, options.CliUi));
        }
        catch (ConfigException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>affectedBase: --affected-base > LABCOAT_AFFECTED_BASE (Aspire WithAffectedBase) > workspace file > user file.</summary>
    private static string? ResolveAffectedBase(StartOptions options, LabcoatConfig config)
    {
        var affectedBase = options.AffectedBase;
        if (affectedBase is null && Environment.GetEnvironmentVariable(AffectedBaseEnv) is { Length: > 0 } envBase)
        {
            if (GitRepo.IsValidRef(envBase.Trim())) affectedBase = envBase.Trim();
            else config.Warnings.Add($"{AffectedBaseEnv}: '{envBase}' is not a git ref; ignored.");
        }
        return affectedBase ?? config.AffectedBase;
    }

    /// <summary>
    /// The trace filter: --trace-filter > LABCOAT_TRACE_FILTER (Aspire WithTestTracing(filter:)) > workspace file > user
    /// file. An invalid one from the environment is an error (<paramref name="error"/>): Labcoat does not start.
    /// </summary>
    internal static string? ResolveTraceFilter(StartOptions options, LabcoatConfig config, out string? error)
    {
        error = null;
        if (options.TraceFilter is not null) return options.TraceFilter;
        if (Environment.GetEnvironmentVariable(TraceFilterEnv) is { } env && !string.IsNullOrWhiteSpace(env))
        {
            error = Labcoat.Testing.TraceFilterSyntax.Validate(env) is { } invalid ? $"{TraceFilterEnv}: {invalid}" : null;
            return error is null ? env.Trim() : null;
        }
        return config.TraceFilter;
    }

    /// <summary>impactMap: --impact-map > LABCOAT_IMPACT_MAP (Aspire WithImpactMap) > workspace file > user file > "auto".</summary>
    private static string? ResolveImpactMap(StartOptions options, LabcoatConfig config)
    {
        var impactMap = options.ImpactMap;
        if (impactMap is null && Environment.GetEnvironmentVariable(ImpactMapEnv) is { Length: > 0 } envImpact)
        {
            impactMap = ImpactMapModes.Parse(envImpact);
            if (impactMap is null) config.Warnings.Add($"{ImpactMapEnv}: '{envImpact}' is not class, test or auto; ignored.");
        }
        return impactMap ?? config.ImpactMap;
    }

    /// <summary>Where the server listens: <see cref="Url"/> null = leave ASPNETCORE_URLS as configured.</summary>
    private sealed record ListenAddress(string? Url, int? Port, string? Note);

    /// <summary>
    /// Port precedence: --port > $PORT > $ASPNETCORE_URLS > 4300. Only the default port falls back to the next free one:
    /// an explicit port is what the caller (or Aspire's proxy) will connect to. Null + <paramref name="error"/> when no
    /// port is free.
    /// </summary>
    private static ListenAddress? ResolveListenAddress(int? port, LabcoatConfig config, out string? error)
    {
        error = null;
        var aspNetCoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (port is null && int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var envPort)) port = envPort;
        // "port" from a settings file: below the environment (Aspire sets PORT), used as an explicit port.
        if (port is null && config.Port is { } filePort && string.IsNullOrEmpty(aspNetCoreUrls)) port = filePort;
        if (port is null && !string.IsNullOrEmpty(aspNetCoreUrls)) return new ListenAddress(null, null, null); // keep ASPNETCORE_URLS as configured
        string? note = null;
        if (port is null)
        {
            var free = Cli.FindFreePort(Cli.DefaultPort);
            if (free is null)
            {
                error = $"Ports {Cli.DefaultPort}-{Cli.DefaultPort + Cli.PortAttempts - 1} are all in use. Pass a free one with --port.";
                return null;
            }
            if (free != Cli.DefaultPort) note = $"Port {Cli.DefaultPort} is in use, using {free} instead.";
            port = free;
        }
        return new ListenAddress($"http://localhost:{port}", port, note);
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
        var hostLog = ConfigureLogging(builder, o); // --verbose / LABCOAT_LOG_LEVEL, --log-file (LabcoatApp.Diagnostics.cs)
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
        AttachHostLog(app, hostLog); // before the explorer exists: its first log lines go to the log file too
        var explorer = app.Services.GetRequiredService<Explorer>();
        var hub = app.Services.GetRequiredService<EventHub>();

        // index.html with the UI defaults as an <html data-labcoat-defaults> attribute (no flash of the wrong theme).
        var indexPage = new IndexPage(webRoot, o.UiDefaults);
        UseSecurityAndUi(app, o.Guard, new SecurityHeaders(webRoot), indexPage, webRoot);

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
            await ServeIndex(ctx, indexPage);
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
              ProjectFinder = warn => ProjectDiscovery.FindProjects(o.Paths, warn), WorkspaceRoots = WorkspaceRootsOf(o.Paths) };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The security middleware (Host / Origin / token checks, response headers), index.html and the static files.</summary>
    private static void UseSecurityAndUi(WebApplication app, RequestGuard guard, SecurityHeaders securityHeaders, IndexPage indexPage, string webRoot)
    {
        app.Use(async (ctx, next) =>
        {
            securityHeaders.Apply(ctx);
            if (guard.Check(ctx) is { } rejection)
            {
                ctx.Response.StatusCode = rejection.Status;
                await ctx.Response.WriteAsJsonAsync(new { error = rejection.Message });
                return;
            }
            await next(ctx);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Use(async (ctx, next) =>
        {
            if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                && ctx.Request.Path.Value is "/" or "/index.html")
            {
                await ServeIndex(ctx, indexPage);
                return;
            }
            await next(ctx);
        });
        var fileProvider = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            // index.html must be revalidated (it names the hashed bundles of the installed version); hashed assets never change.
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
                ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
        });
    }

    /// <summary>JSON body of a command endpoint: (null, null) for an empty body, (null, 400 result) for malformed JSON / wrong types.</summary>
    private static async Task<(ClientCommand? Body, IResult? Error)> ReadBody(HttpRequest req)
    {
        if (req.ContentLength is 0) return (null, null);
        try
        {
            using var reader = new StreamReader(req.Body);
            var text = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(text)) return (null, null);
            return (JsonSerializer.Deserialize<ClientCommand>(text, Json.Options), null);
        }
        catch (JsonException ex)
        {
            return (null, Results.BadRequest(new { error = "Malformed JSON: " + ex.Message }));
        }
    }

    private static async Task ServeIndex(HttpContext ctx, IndexPage page)
    {
        var html = page.Html();
        if (html is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("UI not built yet - run npm run build in ui/", ctx.RequestAborted);
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await ctx.Response.WriteAsync(html, ctx.RequestAborted);
    }

    private static IResult AffectedError(AffectedException ex) =>
        Results.Json(new { error = ex.Message, code = ex.Code }, Json.Options, statusCode: ex.Status);

    private static IResult UnknownProject(string projectId) =>
        Results.NotFound(new { error = $"Unknown project '{projectId}'", projectId });

    private static IResult RunResult(Explorer.RunRequestResult r) => r.Status == 202
        ? Results.Json(new { accepted = true, runId = r.RunId, queued = r.Queued, merged = r.Merged, unknownTestIds = r.UnknownTestIds },
            Json.Options, statusCode: StatusCodes.Status202Accepted)
        : Results.Json(new { error = r.Error, unknownTestIds = r.UnknownTestIds, projects = r.KnownProjects }, Json.Options, statusCode: r.Status);

    /// <summary>
    /// wwwroot next to the binaries (installed tool / published), or, for <c>dotnet run</c> from source (wwwroot is not
    /// copied to bin), the project directory's wwwroot found walking up from <paramref name="baseDirectory"/>.
    /// </summary>
    public static string FindWebRoot(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Labcoat.csproj")) && Directory.Exists(Path.Combine(d.FullName, "wwwroot")))
                return Path.Combine(d.FullName, "wwwroot");
        }
        var local = Path.Combine(baseDirectory, "wwwroot");
        Directory.CreateDirectory(local);
        return local;
    }

    private static int Fail(string message, bool usageHint = true, int exitCode = 1)
    {
        Console.Error.WriteLine($"{Cli.Red("error")} {message}{(usageHint ? " Use --help for usage." : "")}");
        return exitCode;
    }

    /// <summary>The exit code of a startup error: 2 under --run (CliRun.ExitBuildError: CI tells it from failed tests), else 1.</summary>
    private static int ErrorExitCode(StartOptions options) => options.RunOnce ? CliRun.ExitBuildError : 1;
}

/// <summary>POST /api/open-in-editor body.</summary>
public sealed record OpenInEditorRequest(string? Editor, string? Path, int? Line, int? Column);
