using System.Text.Json;
using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>Where a UI default or lock came from, lowest precedence first (see docs/configuration.md).</summary>
public enum ConfigLayer
{
    /// <summary>The user's own file: <c>~/.labcoat.json</c> or the platform config dir.</summary>
    User,
    /// <summary>The workspace file: <c>.labcoat.json</c> / <c>.config/labcoat.json</c> walking up, or <c>--config</c>.</summary>
    Workspace,
    /// <summary>The Aspire AppHost (<c>AddLabcoat(..., o => o.Ui...)</c>), passed as <c>LABCOAT_APPHOST_UI_SETTINGS</c>.</summary>
    AppHost,
    /// <summary><c>LABCOAT_UI_SETTINGS</c> (scripts, containers, CI).</summary>
    Env,
    /// <summary>Command-line flags (<c>--theme</c>, <c>--mode</c>, <c>--editor</c>, <c>--motion</c>, <c>--lock</c>).</summary>
    Cli,
}

/// <summary>
/// The user-facing UI settings that can get a default (and a lock) from outside the browser. One list for the
/// validation of every source; the UI's built-in defaults live in ui/src/settings.ts and ui/src/theme.ts.
/// </summary>
public static class UiSettings
{
    /// <param name="Key">camelCase key in JSON (labcoat.json "ui", LABCOAT_*_UI_SETTINGS, ExplorerState.uiDefaults).</param>
    /// <param name="Values">Allowed values of a choice setting; null for a boolean.</param>
    /// <param name="Builtin">The UI's own default (for messages and docs).</param>
    public sealed record Definition(string Key, string[]? Values, string Builtin)
    {
        public bool IsBoolean => Values is null;
        public string Expected => IsBoolean ? "true or false" : string.Join(", ", Values!);
    }

    /// <summary>The value of mode / motion that follows the operating system's preference.</summary>
    private const string FollowSystem = "system";

    public static readonly IReadOnlyList<Definition> All =
    [
        new("theme", ["catppuccin", "aspire", "github", "one", "dracula", "solarized", "gruvbox"], "catppuccin"),
        new("mode", [FollowSystem, "light", "dark"], FollowSystem),
        new("motion", [FollowSystem, "reduced", "off"], FollowSystem),
        new("editor", ["vscode", "rider", "visualstudio"], "vscode"),
        new("coverageTint", ["line", "gutter"], "line"),
        new("confetti", null, "false"),
        new("partialRunsInHistory", null, "false"),
        new("openOutputOnError", null, "false"),
        new("singleKeyShortcuts", null, "true"),
    ];

    /// <summary>Case-insensitive lookup ("Theme", "theme", "THEME").</summary>
    public static Definition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Canonical value for <paramref name="def"/>: a choice in lower case ("Dracula" → "dracula"), a boolean from
    /// <c>true</c>/<c>false</c> (also the strings "true"/"false", for env vars). Null + <paramref name="error"/> otherwise.
    /// </summary>
    public static JsonValue? Normalize(Definition def, JsonNode? value, out string? error)
    {
        error = null;
        if (value is JsonValue v)
        {
            if (def.IsBoolean)
            {
                if (v.TryGetValue<bool>(out var b)) return JsonValue.Create(b);
                if (v.TryGetValue<string>(out var bs) && bool.TryParse(bs.Trim(), out var b2)) return JsonValue.Create(b2);
            }
            else if (v.TryGetValue<string>(out var s))
            {
                var match = def.Values!.FirstOrDefault(x => string.Equals(x, s.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null) return JsonValue.Create(match);
            }
        }
        error = $"'{def.Key}' must be {(def.IsBoolean ? "" : "one of ")}{def.Expected} (got {value?.ToJsonString() ?? "null"})";
        return null;
    }

    public static string LayerName(ConfigLayer layer) => layer switch
    {
        ConfigLayer.User => "user",
        ConfigLayer.Workspace => "workspace",
        ConfigLayer.AppHost => "apphost",
        ConfigLayer.Env => "env",
        _ => "cli",
    };
}

/// <summary>The UI defaults and locks of one source, validated.</summary>
public sealed class UiLayer(ConfigLayer layer, string origin)
{
    public ConfigLayer Layer { get; } = layer;
    /// <summary>For messages: "~/.labcoat.json", "LABCOAT_UI_SETTINGS", "the command line".</summary>
    public string Origin { get; } = origin;
    /// <summary>Canonical key → canonical value, in definition order of insertion.</summary>
    public Dictionary<string, JsonValue> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Locked { get; } = new(StringComparer.Ordinal);
    public bool IsEmpty => Values.Count == 0 && Locked.Count == 0;

    /// <summary>The "locked" list of a "ui" object and of ExplorerState.uiDefaults.</summary>
    private const string LockedKey = "locked";

    /// <summary>Sets a value (validated); false + error when it is invalid.</summary>
    public bool TrySet(string key, JsonNode? value, out string? error)
    {
        var def = UiSettings.Find(key);
        if (def is null)
        {
            error = $"Unknown UI setting '{key}' (known: {string.Join(", ", UiSettings.All.Select(d => d.Key))})";
            return false;
        }
        var v = UiSettings.Normalize(def, value, out error);
        if (v is null) return false;
        Values[def.Key] = v;
        return true;
    }

    /// <summary>Locks the named settings; "all" / "*" locks every setting. False + error for an unknown name.</summary>
    public bool TryLock(string name, out string? error)
    {
        error = null;
        var n = name.Trim();
        if (n is "*" || n.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var d in UiSettings.All) Locked.Add(d.Key);
            return true;
        }
        var def = UiSettings.Find(n);
        if (def is null)
        {
            error = $"Unknown UI setting '{n}' in \"locked\" (known: {string.Join(", ", UiSettings.All.Select(d => d.Key))}, or \"all\")";
            return false;
        }
        Locked.Add(def.Key);
        return true;
    }

    /// <summary>
    /// Reads a "ui" object: setting keys plus <c>"locked": ["theme", …]</c> (or <c>"all"</c> / <c>true</c>). Unknown keys and
    /// invalid values are skipped with a warning naming <see cref="Origin"/>; the valid ones are kept.
    /// </summary>
    public static UiLayer Parse(ConfigLayer layer, string origin, JsonNode? node, ICollection<string> warnings)
    {
        var result = new UiLayer(layer, origin);
        if (node is null) return result;
        if (node is not JsonObject obj)
        {
            warnings.Add($"{origin}: the UI settings must be a JSON object; ignored.");
            return result;
        }
        foreach (var (key, value) in obj)
        {
            if (key == "$schema") continue;
            if (key.Equals(LockedKey, StringComparison.OrdinalIgnoreCase)) result.ReadLocked(value, warnings);
            else if (!result.TrySet(key, value, out var error)) warnings.Add($"{origin}: {error}; ignored.");
        }
        return result;
    }

    /// <summary><c>"locked"</c>: a list of setting names (array or comma-separated string), <c>"all"</c> or <c>true</c>.</summary>
    private void ReadLocked(JsonNode? value, ICollection<string> warnings)
    {
        switch (value)
        {
            case JsonValue lv when lv.TryGetValue<bool>(out var all):
                if (all) TryLock("all", out _);
                break;
            case JsonValue lv when lv.TryGetValue<string>(out var one):
                LockAll(RequestGuard.SplitList(one), warnings);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is JsonValue iv && iv.TryGetValue<string>(out var name)) LockAll([name], warnings);
                    else warnings.Add($"{Origin}: \"locked\" entries must be setting names (got {item?.ToJsonString() ?? "null"}); ignored.");
                }
                break;
            default:
                warnings.Add($"{Origin}: \"locked\" must be a list of setting names, \"all\" or true; ignored.");
                break;
        }
    }

    private void LockAll(IEnumerable<string> names, ICollection<string> warnings)
    {
        foreach (var name in names)
            if (!TryLock(name, out var e)) warnings.Add($"{Origin}: {e}; ignored.");
    }

    /// <summary>Parses a JSON text (env var). Malformed JSON → a warning and an empty layer.</summary>
    public static UiLayer ParseJson(ConfigLayer layer, string origin, string? json, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(json)) return new UiLayer(layer, origin);
        JsonNode? node;
        try { node = JsonNode.Parse(json, documentOptions: LabcoatConfig.FileJsonOptions); }
        catch (JsonException ex)
        {
            warnings.Add($"{origin} is not valid JSON ({ex.Message}); ignored.");
            return new UiLayer(layer, origin);
        }
        return Parse(layer, origin, node, warnings);
    }

    /// <summary>
    /// Merges layers (lowest precedence first) into <c>ExplorerState.uiDefaults</c>: per setting the value of the highest
    /// layer that sets it; locks from every layer. Null when no layer sets or locks anything.
    /// </summary>
    public static JsonObject? Merge(IEnumerable<UiLayer> layers, string? userFile = null, string? workspaceFile = null)
    {
        var list = layers.ToList();
        if (list.All(l => l.IsEmpty)) return null;
        var result = new JsonObject();
        var sources = new JsonObject();
        var locks = new List<(string Key, string Layer)>(); // each locked key with the layer that locked it
        foreach (var key in UiSettings.All.Select(def => def.Key))
        {
            var setter = list.LastOrDefault(l => l.Values.ContainsKey(key));
            if (setter is not null)
            {
                result[key] = setter.Values[key].DeepClone();
                sources[key] = UiSettings.LayerName(setter.Layer);
            }
            var locker = list.LastOrDefault(l => l.Locked.Contains(key));
            if (locker is not null) locks.Add((key, UiSettings.LayerName(locker.Layer)));
        }
        // same key order as always: locked, sources, lockedBy
        if (locks.Count > 0) result[LockedKey] = new JsonArray([.. locks.Select(l => (JsonNode)l.Key)]);
        if (sources.Count > 0) result["sources"] = sources;
        if (locks.Count > 0) result["lockedBy"] = new JsonObject(locks.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Layer)));
        var files = new JsonObject();
        if (userFile is not null) files["user"] = userFile;
        if (workspaceFile is not null) files["workspace"] = workspaceFile;
        if (files.Count > 0) result["files"] = files;
        return result;
    }

    /// <summary>One line for the startup log: "theme=dracula (apphost), editor=rider (cli, locked)".</summary>
    public static string Describe(JsonObject merged)
    {
        var locked = (merged[LockedKey] as JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet() ?? [];
        var parts = new List<string>();
        foreach (var def in UiSettings.All)
        {
            var hasValue = merged[def.Key] is not null;
            if (!hasValue && !locked.Contains(def.Key)) continue;
            var value = hasValue ? merged[def.Key]!.ToJsonString().Trim('"') : $"{def.Builtin} (built-in)";
            var source = merged["sources"]?[def.Key]?.GetValue<string>();
            var tags = new[] { source, locked.Contains(def.Key) ? LockedKey : null }.Where(t => t is not null).ToArray();
            parts.Add($"{def.Key}={value}{(tags.Length > 0 ? $" ({string.Join(", ", tags)})" : "")}");
        }
        return string.Join(", ", parts);
    }
}

/// <summary>Raised for configuration errors that stop the start (an explicit <c>--config</c> file that is missing or malformed).</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// Labcoat's settings files and setting layers (docs/configuration.md "Settings files"):
/// <list type="number">
/// <item>user file: <c>~/.labcoat.json</c>, else <c>$XDG_CONFIG_HOME/labcoat/config.json</c> (<c>~/.config/labcoat/config.json</c>)
///   or <c>%APPDATA%\Labcoat\config.json</c> on Windows; skipped with <c>--no-user-config</c> / <c>LABCOAT_NO_USER_CONFIG=1</c>;</item>
/// <item>workspace file: <c>--config &lt;file&gt;</c>, else the first <c>.labcoat.json</c> / <c>.config/labcoat.json</c> walking up
///   from the workspace root to the repository root (a directory with <c>.git</c>), never into the home directory;</item>
/// <item><c>LABCOAT_APPHOST_UI_SETTINGS</c> (set by Labcoat.Aspire.Hosting), <c>LABCOAT_UI_SETTINGS</c>, CLI flags.</item>
/// </list>
/// Later layers win per setting; locks from any layer apply.
/// </summary>
public sealed class LabcoatConfig
{
    public const string WorkspaceFileName = ".labcoat.json";
    public const string AppHostUiEnv = "LABCOAT_APPHOST_UI_SETTINGS";
    public const string UiEnv = "LABCOAT_UI_SETTINGS";
    public const string NoUserConfigEnv = "LABCOAT_NO_USER_CONFIG";

    /// <summary>Settings files may contain comments and trailing commas (like other dot-files / .jsonc).</summary>
    public static readonly JsonDocumentOptions FileJsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private const string CoverageKey = "coverage";
    private const string TraceFilterKey = "traceFilter";

    private static readonly string[] FileKeys = ["$schema", "ui", "watch", CoverageKey, "traces", TraceFilterKey, "port", "affectedBase", "impactMap",
        "maxParallelProjects", "hangThreshold", "testTimeout"];

    /// <summary>Everything <see cref="Load"/> reads; tests pass their own home / env so they never see the real ones.</summary>
    public sealed record Inputs
    {
        /// <summary>Common root of the workspace paths: where the walk-up for the workspace file starts.</summary>
        public string? WorkspaceRoot { get; init; }
        /// <summary><c>--config &lt;file&gt;</c> (relative to the current directory): replaces the walk-up.</summary>
        public string? ConfigFile { get; init; }
        public bool NoUserConfig { get; init; }
        public string? Home { get; init; }
        public string? XdgConfigHome { get; init; }
        public string? AppData { get; init; }
        public bool IsWindows { get; init; }
        public string? AppHostUiJson { get; init; }
        public string? EnvUiJson { get; init; }
        public UiLayer? Cli { get; init; }

        private static bool IsTruthy(string? value) =>
            !string.IsNullOrEmpty(value) && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";

        public static Inputs FromEnvironment(string? workspaceRoot, string? configFile, bool noUserConfig, UiLayer? cli) => new()
        {
            WorkspaceRoot = workspaceRoot,
            ConfigFile = configFile,
            NoUserConfig = noUserConfig || IsTruthy(Environment.GetEnvironmentVariable(NoUserConfigEnv)),
            Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            XdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            IsWindows = OperatingSystem.IsWindows(),
            AppHostUiJson = Environment.GetEnvironmentVariable(AppHostUiEnv),
            EnvUiJson = Environment.GetEnvironmentVariable(UiEnv),
            Cli = cli,
        };
    }

    /// <summary>The user file in use (absolute), null when none.</summary>
    public string? UserFile { get; private set; }
    /// <summary>The workspace file in use (absolute), null when none.</summary>
    public string? WorkspaceFile { get; private set; }
    /// <summary>Display names of the loaded files, user file first ("~/.labcoat.json", ".labcoat.json").</summary>
    public List<string> FilesForDisplay { get; } = [];
    public bool Watch { get; private set; }
    public bool Coverage { get; private set; }
    public bool Traces { get; private set; }
    /// <summary>"traceFilter" of the files (the workspace file wins): only matching tests get a trace (Labcoat.Testing).</summary>
    public string? TraceFilter { get; private set; }
    /// <summary>"coverage" as an object: settings file and include / exclude globs (the workspace file's settings path wins; lists add up).</summary>
    public CoverageOptions CoverageOptions { get; private set; } = CoverageOptions.None;
    /// <summary>"port" of the files (the workspace file wins); used only when neither --port nor PORT / ASPNETCORE_URLS is set.</summary>
    public int? Port { get; private set; }
    /// <summary>"affectedBase" of the files (the workspace file wins): base ref of "Run affected: branch vs …".</summary>
    public string? AffectedBase { get; private set; }
    /// <summary>"impactMap" of the files (the workspace file wins): "class" | "test" | "auto" (ImpactMapModes).</summary>
    public string? ImpactMap { get; private set; }
    /// <summary>"maxParallelProjects" of the files (the workspace file wins): projects discovered / run at once.</summary>
    public int? MaxParallelProjects { get; private set; }
    /// <summary>"hangThreshold" of the files: a running test is shown as possibly hanging after this long (zero = never).</summary>
    public TimeSpan? HangThreshold { get; private set; }
    /// <summary>"testTimeout" of the files: per-test timeout (zero = off).</summary>
    public TimeSpan? TestTimeout { get; private set; }
    public List<UiLayer> UiLayers { get; } = [];
    /// <summary>Problems found while reading (unknown keys, invalid values, malformed env JSON): logged at startup.</summary>
    public List<string> Warnings { get; } = [];
    /// <summary>ExplorerState.uiDefaults; null when nothing is configured.</summary>
    public JsonObject? UiDefaults { get; private set; }

    public static LabcoatConfig Load(Inputs inputs)
    {
        var config = new LabcoatConfig();
        string? userDisplay = null, workspaceDisplay = null;

        if (!inputs.NoUserConfig && FindUserFile(inputs, config.Warnings) is { } userFile)
        {
            userDisplay = DisplayUserPath(userFile, inputs.Home);
            config.UserFile = userFile;
            config.ReadFile(userFile, userDisplay, ConfigLayer.User, explicitFile: false);
        }

        string? workspaceFile;
        if (!string.IsNullOrEmpty(inputs.ConfigFile))
        {
            workspaceFile = Path.GetFullPath(inputs.ConfigFile);
            if (!File.Exists(workspaceFile)) throw new ConfigException($"--config: {inputs.ConfigFile} does not exist.");
        }
        else workspaceFile = FindWorkspaceFile(inputs.WorkspaceRoot, inputs.Home, config.Warnings);
        if (workspaceFile is not null)
        {
            workspaceDisplay = inputs.WorkspaceRoot is not null ? Path.GetRelativePath(inputs.WorkspaceRoot, workspaceFile) : workspaceFile;
            if (workspaceDisplay.Length > workspaceFile.Length) workspaceDisplay = workspaceFile; // a long ../../.. chain
            config.WorkspaceFile = workspaceFile;
            config.ReadFile(workspaceFile, workspaceDisplay, ConfigLayer.Workspace, explicitFile: !string.IsNullOrEmpty(inputs.ConfigFile));
        }

        config.UiLayers.Add(UiLayer.ParseJson(ConfigLayer.AppHost, $"the AppHost settings ({AppHostUiEnv})", inputs.AppHostUiJson, config.Warnings));
        config.UiLayers.Add(UiLayer.ParseJson(ConfigLayer.Env, UiEnv, inputs.EnvUiJson, config.Warnings));
        if (inputs.Cli is not null) config.UiLayers.Add(inputs.Cli);
        config.UiLayers.RemoveAll(l => l.IsEmpty);
        config.UiDefaults = UiLayer.Merge(config.UiLayers, userDisplay, workspaceDisplay);
        return config;
    }

    private void ReadFile(string path, string display, ConfigLayer layer, bool explicitFile)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: FileJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var message = $"{display} could not be read ({ex.Message})";
            if (explicitFile) throw new ConfigException(message + ".");
            Warnings.Add(message + "; ignored.");
            return;
        }
        FilesForDisplay.Add(display);
        if (root is not JsonObject obj)
        {
            if (root is not null) Warnings.Add($"{display}: expected a JSON object; ignored.");
            return;
        }
        foreach (var (key, value) in obj) ReadSetting(key, value, path, display, layer);
    }

    private void ReadSetting(string key, JsonNode? value, string path, string display, ConfigLayer layer)
    {
        switch (key)
        {
            case "$schema": break;
            case "ui":
                UiLayers.Add(UiLayer.Parse(layer, display, value, Warnings));
                break;
            case CoverageKey when value is JsonObject coverageObject:
                ReadCoverage(coverageObject, path, display);
                break;
            case "watch" or CoverageKey or "traces":
                ReadFlag(key, value, display);
                break;
            case "affectedBase":
                AffectedBase = ReadAffectedBase(value, display) ?? AffectedBase;
                break;
            case TraceFilterKey:
                TraceFilter = ReadTraceFilter(value, display) ?? TraceFilter;
                break;
            case "maxParallelProjects":
                MaxParallelProjects = ReadMaxParallel(value, display) ?? MaxParallelProjects;
                break;
            case "hangThreshold" or "testTimeout":
                ReadDuration(key, value, display);
                break;
            case "impactMap":
                ImpactMap = ReadImpactMap(value, display) ?? ImpactMap;
                break;
            case "port":
                Port = ReadPort(value, display) ?? Port;
                break;
            default:
                Warnings.Add(UiSettings.Find(key) is { } ui
                    ? $"{display}: '{key}' is a UI setting; put it under \"ui\" ({{ \"ui\": {{ \"{ui.Key}\": … }} }}); ignored."
                    : $"{display}: unknown setting '{key}' (known: {string.Join(", ", FileKeys.Skip(1))}); ignored.");
                break;
        }
    }

    private string? ReadAffectedBase(JsonNode? value, string display)
    {
        if (value is JsonValue av && av.TryGetValue<string>(out var baseRef) && GitRepo.IsValidRef(baseRef.Trim())) return baseRef.Trim();
        Warnings.Add($"{display}: 'affectedBase' must be a git ref such as \"origin/main\"; ignored.");
        return null;
    }

    /// <summary>
    /// "traceFilter": a string in the UI's search syntax. Unlike other values an invalid filter is an error, not a
    /// warning: ignoring it would trace every test.
    /// </summary>
    private static string? ReadTraceFilter(JsonNode? value, string display)
    {
        if (value is not JsonValue fv || !fv.TryGetValue<string>(out var filter))
            throw new ConfigException($"{display}: '{TraceFilterKey}' must be a string such as \"cat:Integration\".");
        if (Labcoat.Testing.TraceFilterSyntax.Validate(filter) is { } error) throw new ConfigException($"{display}: '{TraceFilterKey}': {error}");
        return string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
    }

    private int? ReadMaxParallel(JsonNode? value, string display)
    {
        if (RunLimits.ParseMaxParallel(ScalarText(value)) is { } max) return max;
        Warnings.Add($"{display}: 'maxParallelProjects' must be a number from 1 to {RunLimits.MaxParallelLimit}; ignored.");
        return null;
    }

    /// <summary>"hangThreshold" / "testTimeout": seconds, or a duration such as "90s", "5m"; 0 or "off" turns it off.</summary>
    private void ReadDuration(string key, JsonNode? value, string display)
    {
        if (RunLimits.ParseDuration(ScalarText(value)) is not { } d)
        {
            Warnings.Add($"{display}: '{key}' must be seconds or a duration such as \"90s\", \"5m\" (0 or \"off\" turns it off); ignored.");
            return;
        }
        if (key == "hangThreshold") HangThreshold = d;
        else TestTimeout = d;
    }

    /// <summary>A JSON number or string as text (invariant culture); null for anything else.</summary>
    private static string? ScalarText(JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var text)) return text;
        return v.TryGetValue<double>(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private string? ReadImpactMap(JsonNode? value, string display)
    {
        if (value is JsonValue iv && iv.TryGetValue<string>(out var mode) && ImpactMapModes.Parse(mode) is { } im) return im;
        Warnings.Add($"{display}: 'impactMap' must be \"class\", \"test\" or \"auto\"; ignored.");
        return null;
    }

    private int? ReadPort(JsonNode? value, string display)
    {
        if (value is JsonValue pv && pv.TryGetValue<int>(out var port) && port is > 0 and <= 65535) return port;
        Warnings.Add($"{display}: 'port' must be a number from 1 to 65535; ignored.");
        return null;
    }

    /// <summary>"watch", "coverage" (as a boolean) or "traces": true turns it on, whichever file says so.</summary>
    private void ReadFlag(string key, JsonNode? value, string display)
    {
        if (value is not JsonValue bv || !bv.TryGetValue<bool>(out var on))
        {
            Warnings.Add($"{display}: '{key}' must be true or false{(key == CoverageKey ? " (or an object: enabled, settings, include, exclude)" : "")}; ignored.");
            return;
        }
        if (key == "watch") Watch |= on;
        else if (key == CoverageKey) Coverage |= on;
        else Traces |= on;
    }

    /// <summary>"coverage": { "enabled", "settings", "include", "exclude" } (docs/configuration.md "Code coverage").</summary>
    private void ReadCoverage(JsonObject obj, string path, string display)
    {
        string? settings = null;
        List<string> include = [], exclude = [];
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "enabled":
                    if (value is JsonValue ev && ev.TryGetValue<bool>(out var on)) Coverage |= on;
                    else Warnings.Add($"{display}: 'coverage.enabled' must be true or false; ignored.");
                    break;
                case "settings":
                    settings = ReadCoverageSettingsFile(value, display) ?? settings;
                    break;
                case "include" or "exclude":
                    ReadCoverageGlobs(key, value, display, key == "include" ? include : exclude);
                    break;
                default:
                    Warnings.Add($"{display}: unknown setting 'coverage.{key}' (known: enabled, settings, include, exclude); ignored.");
                    break;
            }
        }
        CoverageOptions = CoverageOptions.Merge(new CoverageOptions
        {
            Settings = settings, BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)), Include = include, Exclude = exclude,
        });
    }

    private string? ReadCoverageSettingsFile(JsonNode? value, string display)
    {
        if (value is JsonValue sv && sv.TryGetValue<string>(out var file) && file.Trim().Length > 0) return file.Trim();
        Warnings.Add($"{display}: 'coverage.settings' must be a file path; ignored.");
        return null;
    }

    /// <summary>"coverage.include" / "coverage.exclude": a list of module names or path globs, or one.</summary>
    private void ReadCoverageGlobs(string key, JsonNode? value, string display, List<string> list)
    {
        if (value is JsonArray arr && arr.All(x => x is JsonValue xv && xv.TryGetValue<string>(out _)))
            list.AddRange(arr.Select(x => x!.GetValue<string>().Trim()).Where(x => x.Length > 0));
        else if (value is JsonValue one && one.TryGetValue<string>(out var single) && single.Trim().Length > 0) list.Add(single.Trim());
        else Warnings.Add($"{display}: 'coverage.{key}' must be a list of module names or path globs; ignored.");
    }

    /// <summary>~/.labcoat.json, else the platform config dir file; both present → the home file, with a warning.</summary>
    internal static string? FindUserFile(Inputs inputs, ICollection<string> warnings)
    {
        string? home = string.IsNullOrEmpty(inputs.Home) ? null : Path.Combine(inputs.Home, WorkspaceFileName);
        string? configDir = null;
        if (inputs.IsWindows)
        {
            if (!string.IsNullOrEmpty(inputs.AppData)) configDir = Path.Combine(inputs.AppData, "Labcoat", "config.json");
        }
        else if (!string.IsNullOrEmpty(inputs.XdgConfigHome)) configDir = Path.Combine(inputs.XdgConfigHome, "labcoat", "config.json");
        else if (!string.IsNullOrEmpty(inputs.Home)) configDir = Path.Combine(inputs.Home, ".config", "labcoat", "config.json");

        var homeExists = home is not null && File.Exists(home);
        var dirExists = configDir is not null && File.Exists(configDir);
        if (homeExists && dirExists)
            warnings.Add($"Both {DisplayUserPath(home!, inputs.Home)} and {DisplayUserPath(configDir!, inputs.Home)} exist; using {DisplayUserPath(home!, inputs.Home)}.");
        if (homeExists) return home;
        return dirExists ? configDir : null;
    }

    /// <summary>
    /// The first <c>.labcoat.json</c> (else <c>.config/labcoat.json</c>) walking up from <paramref name="start"/>; stops after
    /// the repository root (a directory containing <c>.git</c>) and before the home directory (whose <c>.labcoat.json</c> is
    /// the user file).
    /// </summary>
    internal static string? FindWorkspaceFile(string? start, string? home, ICollection<string> warnings)
    {
        if (string.IsNullOrEmpty(start)) return null;
        var homeDir = string.IsNullOrEmpty(home) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            if (homeDir is not null && string.Equals(Path.TrimEndingDirectorySeparator(dir.FullName), homeDir, PathUtil.Comparison)) break;
            if (WorkspaceFileIn(dir, warnings) is { } file) return file;
            if (IsRepositoryRoot(dir)) break;
        }
        return null;
    }

    /// <summary><c>.labcoat.json</c>, else <c>.config/labcoat.json</c> in <paramref name="dir"/>; null when neither exists.</summary>
    private static string? WorkspaceFileIn(DirectoryInfo dir, ICollection<string> warnings)
    {
        var dot = Path.Combine(dir.FullName, WorkspaceFileName);
        var nested = Path.Combine(dir.FullName, ".config", "labcoat.json");
        var dotExists = File.Exists(dot);
        var nestedExists = File.Exists(nested);
        if (dotExists && nestedExists)
            warnings.Add($"Both {WorkspaceFileName} and .config/labcoat.json exist in {dir.FullName}; using {WorkspaceFileName}.");
        if (dotExists) return dot;
        return nestedExists ? nested : null;
    }

    /// <summary>A directory with <c>.git</c> (directory or file); an unreadable one also ends the walk-up.</summary>
    private static bool IsRepositoryRoot(DirectoryInfo dir)
    {
        try
        {
            return Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>"~/.labcoat.json" for files under the home directory, else the full path.</summary>
    internal static string DisplayUserPath(string path, string? home)
    {
        if (string.IsNullOrEmpty(home)) return path;
        var h = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        return PathUtil.IsUnder(path, h) ? "~/" + Path.GetRelativePath(h, path).Replace('\\', '/') : path;
    }
}

/// <summary>
/// Serves index.html with the merged UI defaults as <c>&lt;html data-labcoat-defaults="{…}"&gt;</c>, so the pre-paint
/// script applies the right theme before the WebSocket connects. An attribute (not an inline script) keeps the CSP
/// hashes of index.html's inline scripts valid.
/// </summary>
public sealed partial class IndexPage(string webRoot, JsonObject? uiDefaults)
{
    private readonly string? _attribute = uiDefaults is null ? null
        : $" data-labcoat-defaults=\"{System.Net.WebUtility.HtmlEncode(uiDefaults.ToJsonString())}\"";
    private readonly object _gate = new();
    private DateTime _stamp;
    private string? _html;

    public string Path => System.IO.Path.Combine(webRoot, "index.html");

    /// <summary>The page to serve, or null when the UI isn't built.</summary>
    public string? Html()
    {
        DateTime stamp;
        try
        {
            if (!File.Exists(Path)) return null;
            stamp = File.GetLastWriteTimeUtc(Path);
        }
        catch { return null; }
        lock (_gate)
        {
            if (_html is not null && stamp == _stamp) return _html;
            string raw;
            try { raw = File.ReadAllText(Path); } catch { return null; }
            _html = Inject(raw, _attribute);
            _stamp = stamp;
            return _html;
        }
    }

    /// <summary>Adds <paramref name="attribute"/> to the first <c>&lt;html</c> tag (unchanged without one).</summary>
    public static string Inject(string html, string? attribute)
    {
        if (attribute is null) return html;
        var m = HtmlTagRe().MatchOrEmpty(html);
        return m.Success ? html.Insert(m.Index + m.Length, attribute) : html;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<html\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase, RegexTimeout.Milliseconds)]
    private static partial System.Text.RegularExpressions.Regex HtmlTagRe();
}
