using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Everything <see cref="LabcoatResourceBuilderExtensions.AddLabcoat(IDistributedApplicationBuilder, string, Action{LabcoatOptions})"/>
/// can set in one place: the explorer's start options and the defaults of the Labcoat UI's settings.
/// Bound from the AppHost configuration first (section <c>Labcoat</c>, then <c>Labcoat:Resources:&lt;name&gt;</c>), then
/// the configure callback runs, so code wins over appsettings.json.
/// </summary>
public sealed class LabcoatOptions
{
    /// <summary>
    /// Test projects: a <c>.csproj</c>, a solution file or a directory that is scanned, relative to the AppHost
    /// directory. Empty = the solution root. Same as <see cref="LabcoatResourceBuilderExtensions.WithTestProject"/>.
    /// </summary>
    public IList<string> TestProjects { get; } = new List<string>();

    /// <summary>Start with watch mode on (<see cref="LabcoatResourceBuilderExtensions.WithWatch"/>).</summary>
    public bool Watch { get; set; }

    /// <summary>Start with code coverage collection on (<see cref="LabcoatResourceBuilderExtensions.WithCoverage"/>).</summary>
    public bool Coverage { get; set; }

    /// <summary>
    /// Coverage settings file for every test project (<see cref="LabcoatResourceBuilderExtensions.WithCoverageSettings"/>).
    /// Null = each test project's own coverage.settings.xml / CodeCoverage.xml / *.runsettings, else Labcoat's defaults.
    /// </summary>
    public string? CoverageSettings { get; set; }

    /// <summary>
    /// Module names or path globs to count although Labcoat's coverage defaults leave them out, e.g. <c>Labcoat.*</c>
    /// (<see cref="LabcoatResourceBuilderExtensions.WithCoverageFilter"/>).
    /// </summary>
    public IList<string> CoverageInclude { get; } = new List<string>();

    /// <summary>
    /// Module names or path globs never to count in coverage, e.g. <c>SupportDesk.DemoDataSeeder</c> or <c>**/Migrations/**</c>
    /// (<see cref="LabcoatResourceBuilderExtensions.WithCoverageFilter"/>).
    /// </summary>
    public IList<string> CoverageExclude { get; } = new List<string>();

    /// <summary>Start with per-test trace collection on (<see cref="LabcoatResourceBuilderExtensions.WithTestTracing"/>).</summary>
    public bool TestTracing { get; set; }

    /// <summary>
    /// Base git ref of the UI's "Run affected: branch vs …", e.g. <c>origin/develop</c>
    /// (<see cref="LabcoatResourceBuilderExtensions.WithAffectedBase"/>). Null = origin/HEAD, else origin/main / master.
    /// </summary>
    public string? AffectedBase { get; set; }

    /// <summary>
    /// Granularity of the test impact map (<see cref="LabcoatResourceBuilderExtensions.WithImpactMap"/>): "class", "test"
    /// or "auto". Null = Labcoat's default ("auto").
    /// </summary>
    public string? ImpactMap { get; set; }

    /// <summary>
    /// Test projects discovered and run at the same time (<see cref="LabcoatRunLimitsExtensions.WithMaxParallelProjects"/>).
    /// Null = Labcoat's default, min(processor count, 4).
    /// </summary>
    public int? MaxParallelProjects { get; set; }

    /// <summary>
    /// A running test is shown as possibly hanging after this long (<see cref="LabcoatRunLimitsExtensions.WithHangThreshold"/>).
    /// Null = 60 seconds; <see cref="TimeSpan.Zero"/> = never.
    /// </summary>
    public TimeSpan? HangThreshold { get; set; }

    /// <summary>
    /// Per-test timeout (<see cref="LabcoatRunLimitsExtensions.WithTestTimeout"/>): a test running longer is marked timed out
    /// and its project's run is cancelled. Null or <see cref="TimeSpan.Zero"/> = off (the default).
    /// </summary>
    public TimeSpan? TestTimeout { get; set; }

    /// <summary>A fixed host port for the UI instead of a random one (<see cref="LabcoatResourceBuilderExtensions.WithHostPort"/>).</summary>
    public int? HostPort { get; set; }

    /// <summary>Show the suite result as the resource state (<see cref="LabcoatResourceBuilderExtensions.WithSuiteResultState"/>). On by default.</summary>
    public bool SuiteResultState { get; set; } = true;

    /// <summary>
    /// Environment variables (or <c>PREFIX*</c> patterns) the test processes keep although they look like variables of
    /// the Aspire run (<see cref="LabcoatResourceBuilderExtensions.WithPassThroughEnvironment"/>).
    /// </summary>
    public IList<string> PassThroughEnvironment { get; } = new List<string>();

    /// <summary>Defaults (and optional locks) for the settings of the Labcoat UI.</summary>
    public LabcoatUiSettings Ui { get; } = new();

    /// <summary>
    /// Reads options from configuration (keys as the property names, case-insensitive). Unknown keys are ignored;
    /// invalid values throw <see cref="InvalidOperationException"/> naming the key.
    /// </summary>
    public LabcoatOptions Bind(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        ReadTestProjects(section);
        ReadList(section, nameof(PassThroughEnvironment), PassThroughEnvironment);
        ReadList(section, nameof(CoverageInclude), CoverageInclude);
        ReadList(section, nameof(CoverageExclude), CoverageExclude);
        if (section[nameof(CoverageSettings)] is { Length: > 0 } coverageSettings) CoverageSettings = coverageSettings.Trim();
        Watch = ReadBool(section, nameof(Watch)) ?? Watch;
        Coverage = ReadBool(section, nameof(Coverage)) ?? Coverage;
        TestTracing = ReadBool(section, nameof(TestTracing)) ?? TestTracing;
        SuiteResultState = ReadBool(section, nameof(SuiteResultState)) ?? SuiteResultState;
        if (section[nameof(AffectedBase)] is { Length: > 0 } affectedBase) AffectedBase = affectedBase.Trim();
        ImpactMap = ReadImpactMap(section) ?? ImpactMap;
        HostPort = ReadHostPort(section) ?? HostPort;
        MaxParallelProjects = ReadMaxParallel(section) ?? MaxParallelProjects;
        HangThreshold = ReadDuration(section, nameof(HangThreshold)) ?? HangThreshold;
        TestTimeout = ReadDuration(section, nameof(TestTimeout)) ?? TestTimeout;
        Ui.Bind(section.GetSection(nameof(Ui)));
        return this;
    }

    /// <summary>TestProjects: array entries, or one value.</summary>
    private void ReadTestProjects(IConfiguration section)
    {
        var values = section.GetSection(nameof(TestProjects)).GetChildren().Select(c => c.Value).OfType<string>();
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v) && !TestProjects.Contains(v))) TestProjects.Add(value);
        if (section[nameof(TestProjects)] is { Length: > 0 } single && !TestProjects.Contains(single)) TestProjects.Add(single);
    }

    private static string? ReadImpactMap(IConfiguration section)
    {
        if (section[nameof(ImpactMap)] is not { Length: > 0 } impactMap) return null;
        return LabcoatResourceBuilderExtensions.ImpactMapModes.Contains(impactMap.Trim().ToLowerInvariant())
            ? impactMap.Trim().ToLowerInvariant()
            : throw Invalid(section, nameof(ImpactMap), impactMap, "class, test or auto");
    }

    private static int? ReadMaxParallel(IConfiguration section)
    {
        if (section[nameof(MaxParallelProjects)] is not { Length: > 0 } raw) return null;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= LabcoatRunLimitsExtensions.MaxParallelLimit
            ? n
            : throw Invalid(section, nameof(MaxParallelProjects), raw, $"a number from 1 to {LabcoatRunLimitsExtensions.MaxParallelLimit}");
    }

    /// <summary>A duration: seconds (<c>90</c>), a TimeSpan (<c>00:01:30</c>) or a number with s / m / h (<c>90s</c>, <c>5m</c>); 0 / off = zero.</summary>
    private static TimeSpan? ReadDuration(IConfiguration section, string key)
    {
        if (section[key] is not { Length: > 0 } raw) return null;
        return LabcoatRunLimitsExtensions.ParseDuration(raw) ?? throw Invalid(section, key, raw, "seconds or a duration such as 90s, 5m or 00:01:30 (0 or off = off)");
    }

    private static int? ReadHostPort(IConfiguration section)
    {
        if (section[nameof(HostPort)] is not { Length: > 0 } port) return null;
        return int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535
            ? p
            : throw Invalid(section, nameof(HostPort), port, "a port number (1-65535)");
    }

    /// <summary>A list: array entries, or one value separated by <c>;</c> / <c>,</c>.</summary>
    private static void ReadList(IConfiguration section, string key, IList<string> list)
    {
        var values = section.GetSection(key).GetChildren().Select(c => c.Value)
            .Concat((section[key] ?? "").Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim());
        foreach (var value in values.Where(v => !list.Contains(v))) list.Add(value);
    }

    internal static bool? ReadBool(IConfiguration section, string key)
    {
        var raw = section[key];
        if (string.IsNullOrEmpty(raw)) return null;
        return bool.TryParse(raw, out var b) ? b : throw Invalid(section, key, raw, "true or false");
    }

    internal static InvalidOperationException Invalid(IConfiguration section, string key, string value, string expected)
    {
        var prefix = (section as IConfigurationSection)?.Path is { Length: > 0 } path ? path + ":" : "";
        return new($"Labcoat configuration: '{prefix}{key}' is '{value}'; expected {expected}.");
    }
}

/// <summary>The color palette of the Labcoat UI (Settings → Theme).</summary>
public enum LabcoatTheme
{
    /// <summary>Catppuccin: Latte when light, Macchiato when dark (the built-in default).</summary>
    Catppuccin,
    /// <summary>The Aspire dashboard look.</summary>
    Aspire,
    /// <summary>GitHub's Primer colors.</summary>
    GitHub,
    /// <summary>Atom's One Light / One Dark.</summary>
    One,
    /// <summary>Dracula, with Alucard as its light variant.</summary>
    Dracula,
    /// <summary>Solarized light and dark.</summary>
    Solarized,
    /// <summary>Gruvbox (medium contrast).</summary>
    Gruvbox,
}

/// <summary>Light or dark (Settings → Mode).</summary>
public enum LabcoatThemeMode
{
    /// <summary>Follow the operating system (the built-in default).</summary>
    System,
    /// <summary>Always light.</summary>
    Light,
    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>Animations (Settings → Motion).</summary>
public enum LabcoatMotion
{
    /// <summary>Follow the operating system's reduce-motion setting (the built-in default).</summary>
    System,
    /// <summary>Short fades only.</summary>
    Reduced,
    /// <summary>No animations.</summary>
    Off,
}

/// <summary>The editor "open in editor" launches (Settings → Editor).</summary>
public enum LabcoatEditor
{
    /// <summary>Visual Studio Code (the built-in default).</summary>
    VSCode,
    /// <summary>JetBrains Rider.</summary>
    Rider,
    /// <summary>Visual Studio (Windows only).</summary>
    VisualStudio,
}

/// <summary>How the coverage source view marks lines (Settings → Coverage tint).</summary>
public enum LabcoatCoverageTint
{
    /// <summary>Tint the full line (the built-in default).</summary>
    Line,
    /// <summary>Only a bar in the gutter.</summary>
    Gutter,
}

/// <summary>A setting of the Labcoat UI, for <see cref="LabcoatUiSettings.Lock(LabcoatUiSetting[])"/>.</summary>
public enum LabcoatUiSetting
{
    /// <summary><see cref="LabcoatUiSettings.Theme"/></summary>
    Theme,
    /// <summary><see cref="LabcoatUiSettings.Mode"/></summary>
    Mode,
    /// <summary><see cref="LabcoatUiSettings.Motion"/></summary>
    Motion,
    /// <summary><see cref="LabcoatUiSettings.Editor"/></summary>
    Editor,
    /// <summary><see cref="LabcoatUiSettings.CoverageTint"/></summary>
    CoverageTint,
    /// <summary><see cref="LabcoatUiSettings.Confetti"/></summary>
    Confetti,
    /// <summary><see cref="LabcoatUiSettings.PartialRunsInHistory"/></summary>
    PartialRunsInHistory,
    /// <summary><see cref="LabcoatUiSettings.OpenOutputOnError"/></summary>
    OpenOutputOnError,
    /// <summary><see cref="LabcoatUiSettings.SingleKeyShortcuts"/></summary>
    SingleKeyShortcuts,
}

/// <summary>
/// Defaults for the Labcoat UI's settings (the Settings page). Each value is a default: a choice the user makes in
/// the browser wins, unless the setting is locked with <see cref="Lock(LabcoatUiSetting[])"/> /
/// <see cref="LockAll"/>; a locked setting is shown disabled with "Set by the AppHost (Program.cs)". Unset (null)
/// properties keep the UI's built-in default or a value from a <c>.labcoat.json</c>.
/// </summary>
public sealed class LabcoatUiSettings
{
    /// <summary>Color palette. Built-in default: <see cref="LabcoatTheme.Catppuccin"/>.</summary>
    public LabcoatTheme? Theme { get; set; }

    /// <summary>Light / dark. Built-in default: <see cref="LabcoatThemeMode.System"/>.</summary>
    public LabcoatThemeMode? Mode { get; set; }

    /// <summary>Animations. Built-in default: <see cref="LabcoatMotion.System"/>.</summary>
    public LabcoatMotion? Motion { get; set; }

    /// <summary>Editor for "open in editor". Built-in default: <see cref="LabcoatEditor.VSCode"/>.</summary>
    public LabcoatEditor? Editor { get; set; }

    /// <summary>Coverage source view: full-line tint or gutter only. Built-in default: <see cref="LabcoatCoverageTint.Line"/>.</summary>
    public LabcoatCoverageTint? CoverageTint { get; set; }

    /// <summary>A confetti burst when a run turns the suite green. Built-in default: off.</summary>
    public bool? Confetti { get; set; }

    /// <summary>List partial runs (a single test, a class) in the run switcher and history. Built-in default: off.</summary>
    public bool? PartialRunsInHistory { get; set; }

    /// <summary>Open the output panel when a build or run logs an error. Built-in default: off.</summary>
    public bool? OpenOutputOnError { get; set; }

    /// <summary>Single-key shortcuts (W, ?, R / C / O). Built-in default: on.</summary>
    public bool? SingleKeyShortcuts { get; set; }

    /// <summary>Settings the user can't change in the browser.</summary>
    public ISet<LabcoatUiSetting> Locked { get; } = new SortedSet<LabcoatUiSetting>();

    /// <summary>Locks settings: the browser shows them disabled and always uses the value set here (or the built-in default).</summary>
    public LabcoatUiSettings Lock(params LabcoatUiSetting[] settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var s in settings) Locked.Add(s);
        return this;
    }

    /// <summary>Locks one setting by property: <c>ui.Lock(s =&gt; s.Theme)</c>.</summary>
    public LabcoatUiSettings Lock<T>(Expression<Func<LabcoatUiSettings, T>> setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var body = setting.Body is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : setting.Body;
        if (body is not MemberExpression { Member.Name: var name } || !Enum.TryParse<LabcoatUiSetting>(name, out var which))
            throw new ArgumentException("Pass a settings property, e.g. ui.Lock(s => s.Theme).", nameof(setting));
        Locked.Add(which);
        return this;
    }

    /// <summary>Locks every setting (unset ones are locked to the UI's built-in default).</summary>
    public LabcoatUiSettings LockAll() => Lock(Enum.GetValues<LabcoatUiSetting>());

    /// <summary>Nothing set and nothing locked.</summary>
    public bool IsEmpty => ToJson().Count == 0;

    /// <summary>
    /// The compact JSON passed to the explorer as <c>LABCOAT_APPHOST_UI_SETTINGS</c>, e.g.
    /// <c>{"theme":"dracula","editor":"rider","locked":["editor"]}</c> (camelCase keys, lower-case values).
    /// </summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject();
        void Choice<T>(string key, T? v) where T : struct, Enum
        {
            if (v is { } value) o[key] = value.ToString().ToLowerInvariant();
        }
        void Flag(string key, bool? v)
        {
            if (v is { } value) o[key] = value;
        }
        Choice("theme", Theme);
        Choice("mode", Mode);
        Choice("motion", Motion);
        Choice("editor", Editor);
        Choice("coverageTint", CoverageTint);
        Flag("confetti", Confetti);
        Flag("partialRunsInHistory", PartialRunsInHistory);
        Flag("openOutputOnError", OpenOutputOnError);
        Flag("singleKeyShortcuts", SingleKeyShortcuts);
        if (Locked.Count > 0)
            o["locked"] = new JsonArray([.. Locked.Order().Select(l => (JsonNode?)JsonValue.Create(char.ToLowerInvariant(l.ToString()[0]) + l.ToString()[1..]))]);
        return o;
    }

    /// <summary>Copies the values and locks set in <paramref name="other"/> onto this instance.</summary>
    public LabcoatUiSettings Apply(LabcoatUiSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Theme = other.Theme ?? Theme;
        Mode = other.Mode ?? Mode;
        Motion = other.Motion ?? Motion;
        Editor = other.Editor ?? Editor;
        CoverageTint = other.CoverageTint ?? CoverageTint;
        Confetti = other.Confetti ?? Confetti;
        PartialRunsInHistory = other.PartialRunsInHistory ?? PartialRunsInHistory;
        OpenOutputOnError = other.OpenOutputOnError ?? OpenOutputOnError;
        SingleKeyShortcuts = other.SingleKeyShortcuts ?? SingleKeyShortcuts;
        foreach (var l in other.Locked) Locked.Add(l);
        return this;
    }

    /// <summary>
    /// Reads <c>Theme</c>, <c>Mode</c>, <c>Motion</c>, <c>Editor</c>, <c>CoverageTint</c> (names, case-insensitive),
    /// the booleans, and <c>Locked</c> (a list of setting names, or "All").
    /// </summary>
    public LabcoatUiSettings Bind(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Theme = ReadEnum<LabcoatTheme>(section, nameof(Theme)) ?? Theme;
        Mode = ReadEnum<LabcoatThemeMode>(section, nameof(Mode)) ?? Mode;
        Motion = ReadEnum<LabcoatMotion>(section, nameof(Motion)) ?? Motion;
        Editor = ReadEnum<LabcoatEditor>(section, nameof(Editor)) ?? Editor;
        CoverageTint = ReadEnum<LabcoatCoverageTint>(section, nameof(CoverageTint)) ?? CoverageTint;
        Confetti = LabcoatOptions.ReadBool(section, nameof(Confetti)) ?? Confetti;
        PartialRunsInHistory = LabcoatOptions.ReadBool(section, nameof(PartialRunsInHistory)) ?? PartialRunsInHistory;
        OpenOutputOnError = LabcoatOptions.ReadBool(section, nameof(OpenOutputOnError)) ?? OpenOutputOnError;
        SingleKeyShortcuts = LabcoatOptions.ReadBool(section, nameof(SingleKeyShortcuts)) ?? SingleKeyShortcuts;
        var names = section.GetSection(nameof(Locked)).GetChildren().Select(c => c.Value).ToList();
        if (section[nameof(Locked)] is { Length: > 0 } single) names.AddRange(single.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.Equals("all", StringComparison.OrdinalIgnoreCase) || name == "*") LockAll();
            else if (Enum.TryParse<LabcoatUiSetting>(name, ignoreCase: true, out var which) && Enum.IsDefined(which) && !name.Any(char.IsDigit)) Locked.Add(which);
            else throw LabcoatOptions.Invalid(section, nameof(Locked), name, $"setting names ({string.Join(", ", Enum.GetNames<LabcoatUiSetting>())}) or All");
        }
        return this;
    }

    private static T? ReadEnum<T>(IConfiguration section, string key) where T : struct, Enum
    {
        var raw = section[key];
        if (string.IsNullOrEmpty(raw)) return null;
        return Enum.TryParse<T>(raw, ignoreCase: true, out var v) && Enum.IsDefined(v) && !raw.Any(char.IsDigit)
            ? v
            : throw LabcoatOptions.Invalid(section, key, raw, string.Join(", ", Enum.GetNames<T>()));
    }
}
