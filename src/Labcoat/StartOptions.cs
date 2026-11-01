using System.Text.Json.Nodes;

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
            "--export", "--filter", "--export-paths", "--shard", "--shard-by", "--shard-timings", "--min-coverage", "--min-diff-coverage", // --run (CliRun.cs)
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
            case "--run" or "--export" or "--filter" or "--export-paths" or "--shard" or "--shard-by" or "--shard-timings" or "--min-coverage" or "--min-diff-coverage":
                return ApplyRunOption(a, args); // CliRun.cs
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
