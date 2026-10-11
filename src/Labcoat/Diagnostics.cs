using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Labcoat;

/// <summary>The settings in effect, as the diagnostics report lists them.</summary>
public sealed class DiagnosticsSettings
{
    public bool Watch { get; init; }
    public bool Coverage { get; init; }
    public bool Traces { get; init; }
    public string? AffectedBase { get; init; }
    public string? ImpactMap { get; init; }
    public CoverageOptions CoverageOptions { get; init; } = CoverageOptions.None;
    public JsonObject? UiDefaults { get; init; }
    public IReadOnlyList<string> SettingsFiles { get; init; } = [];
    public LogLevel LogLevel { get; init; } = LogLevel.Warning;
    public string? LogFile { get; init; }
    public string? Url { get; init; }

    public SortedDictionary<string, string> ToDictionary()
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["watch"] = Watch ? "on" : "off",
            ["coverage"] = Coverage ? "on" : "off",
            ["traces"] = Traces ? "on" : "off",
            ["impactMap"] = ImpactMap ?? ImpactMapModes.Default,
            ["logLevel"] = LogLevel.ToString(),
            ["settingsFiles"] = SettingsFiles.Count == 0 ? "(none)" : string.Join(", ", SettingsFiles),
        };
        if (AffectedBase is not null) d["affectedBase"] = AffectedBase;
        if (CoverageOptions.Settings is not null) d["coverage.settings"] = CoverageOptions.Settings;
        if (CoverageOptions.Include.Count > 0) d["coverage.include"] = string.Join(", ", CoverageOptions.Include);
        if (CoverageOptions.Exclude.Count > 0) d["coverage.exclude"] = string.Join(", ", CoverageOptions.Exclude);
        if (UiDefaults is not null) d["uiDefaults"] = UiDefaults.ToJsonString();
        if (LogFile is not null) d["logFile"] = LogFile;
        if (Url is not null) d["url"] = Url;
        return d;
    }
}

/// <summary>
/// The diagnostics report for bug reports (<c>labcoat --diagnostics</c>, <c>GET /api/diagnostics</c>, "Copy diagnostics"
/// in the UI): Labcoat version, OS, .NET runtime and SDK, the workspace, the test projects with their target frameworks
/// and test packages (from obj/project.assets.json), the settings, the relevant environment variables with secrets
/// redacted (never LABCOAT_TOKEN or OTLP headers) and the last log lines. Paths under the home directory are shown as
/// <c>~</c>. It is what .github/ISSUE_TEMPLATE/bug_report.yml asks for.
/// </summary>
public static class Diagnostics
{
    public const int LogLines = 100;
    private const string Redacted = "(set, redacted)";
    private const string TextFence = "```text";

    public sealed record PackageInfo(string Name, string Version);

    public sealed record ProjectInfo
    {
        public required string Name { get; init; }
        /// <summary>Relative to the workspace root ('/' separators).</summary>
        public required string Path { get; init; }
        public string? Id { get; init; }
        public string? Status { get; init; }
        public List<string> TargetFrameworks { get; init; } = [];
        /// <summary>e.g. "MSTest 4.0.2"; null when unknown (not restored).</summary>
        public string? TestFramework { get; init; }
        public string? TestingPlatform { get; init; }
        /// <summary>Test framework, Microsoft.Testing.Platform, extension (coverage, TRX, …) and Labcoat packages, resolved versions.</summary>
        public List<PackageInfo> Packages { get; init; } = [];
        public string? Note { get; init; }
    }

    public sealed record Report
    {
        public required string LabcoatVersion { get; init; }
        public required string StartedAs { get; init; }
        public required string Os { get; init; }
        public required string Runtime { get; init; }
        public string? Sdk { get; init; }
        public required string WorkspaceKind { get; init; }
        public required string Workspace { get; init; }
        public required string DataDirectory { get; init; }
        /// <summary>Another Labcoat owns the data folder (this one doesn't save history).</summary>
        public bool DataReadOnly { get; init; }
        public required List<ProjectInfo> Projects { get; init; }
        public required SortedDictionary<string, string> Settings { get; init; }
        public required SortedDictionary<string, string> Environment { get; init; }
        public required List<string> Log { get; init; }
        public string? LogSource { get; init; }
    }

    /// <summary>What the report is made of.</summary>
    public sealed class Input
    {
        /// <summary>The test project paths (the explorer's order).</summary>
        public required IReadOnlyList<string> Projects { get; init; }
        /// <summary>The explorer's projects (id, status), when a host runs.</summary>
        public IReadOnlyList<TestProject>? Live { get; init; }
        public SortedDictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> Log { get; init; } = [];
        public string? LogSource { get; init; }
        /// <summary>The environment (default: this process's).</summary>
        public IDictionary<string, string?>? Environment { get; init; }
        /// <summary>The home directory shown as "~" (default: the user's).</summary>
        public string? Home { get; init; }
    }

    // ------------------------------------------------------------------ collect

    public static async Task<Report> CollectAsync(Input input, CancellationToken ct = default)
    {
        var env = input.Environment ?? CurrentEnvironment();
        var home = input.Home ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var workspace = WorkspaceIdentity.Resolve(input.Projects);
        var projects = new List<ProjectInfo>();
        var ids = WorkspaceIdentity.ProjectIds(input.Projects);
        for (var i = 0; i < input.Projects.Count; i++)
        {
            var live = input.Live?.FirstOrDefault(p => p.Id == ids[i]);
            projects.Add(Project(input.Projects[i], workspace, live));
        }
        var secrets = SecretValues(env);
        var report = new Report
        {
            LabcoatVersion = Cli.Version,
            StartedAs = HowStarted(env, AppContext.BaseDirectory),
            Os = $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})",
            Runtime = $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})",
            Sdk = await SdkVersionAsync(workspace.Root.Length > 0 ? workspace.Root : System.Environment.CurrentDirectory, ct),
            WorkspaceKind = workspace.Kind,
            Workspace = workspace.Path,
            DataDirectory = workspace.DataDirectory,
            DataReadOnly = !WorkspaceLock.CanWrite(workspace.DataDirectory),
            Projects = projects,
            Settings = input.Settings,
            Environment = SelectedEnvironment(env),
            Log = [.. input.Log.TakeLast(LogLines).Select(l => RedactValues(l, secrets))],
            LogSource = input.LogSource,
        };
        return Anonymize(report, home);
    }

    private static Dictionary<string, string?> CurrentEnvironment()
    {
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables()) d[(string)e.Key] = e.Value as string;
        return d;
    }

    /// <summary>How this Labcoat was started, from its environment and install location.</summary>
    internal static string HowStarted(IDictionary<string, string?> env, string baseDirectory)
    {
        if (env.TryGetValue("LABCOAT_APPHOST_UI_SETTINGS", out var a) && a is not null || env.TryGetValue("ASPIRE_DASHBOARD_URL", out var d) && !string.IsNullOrEmpty(d))
            return "Aspire (Labcoat.Aspire.Hosting)";
        var dir = baseDirectory.Replace('\\', '/');
        if (dir.Contains("/.dotnet/tools/", StringComparison.OrdinalIgnoreCase)) return "dotnet tool (global)";
        if (dir.Contains("/.nuget/packages/labcoat/", StringComparison.OrdinalIgnoreCase)) return "dotnet tool (local) or dnx";
        if (dir.Contains("/src/Labcoat/", StringComparison.OrdinalIgnoreCase)) return "from source";
        return "other (" + baseDirectory + ")";
    }

    private static async Task<string?> SdkVersionAsync(string directory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(Explorer.DotnetPath)
            {
                WorkingDirectory = Directory.Exists(directory) ? directory : System.Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en"; // parsed: Labcoat's own call
            psi.Environment["DOTNET_NOLOGO"] = "1";
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            _ = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* exited already */ }
                return null;
            }
            var version = (await stdout).Trim();
            return proc.ExitCode == 0 && version.Length is > 0 and < 64 ? version : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    // ------------------------------------------------------------------ projects

    /// <summary>Package names (lower case prefixes) the report lists: test frameworks, the platform and its extensions, Labcoat, Verify, coverage.</summary>
    private static readonly string[] PackagePrefixes =
    [
        "microsoft.testing.", "mstest", "xunit", "nunit", "tunit", "microsoft.codecoverage", "microsoft.net.test.sdk", "labcoat",
        "verify", "aspire.hosting.testing", "coverlet",
    ];

    internal static ProjectInfo Project(string projectPath, WorkspaceIdentity workspace, TestProject? live)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var rel = workspace.RelativeProjectPath(projectPath);
        var assets = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, "obj", "project.assets.json");
        var (frameworks, packages, note) = ReadAssets(assets);
        if (frameworks.Count == 0) frameworks = ReadProjectFrameworks(projectPath);
        var sdkPackage = ReadSdkPackage(projectPath); // MSTest.Sdk/4.0.2 is no package reference
        if (sdkPackage is not null && !packages.Any(p => p.Name.Equals(sdkPackage.Name, StringComparison.OrdinalIgnoreCase))) packages.Add(sdkPackage);
        packages.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return new ProjectInfo
        {
            Name = name,
            Path = rel,
            Id = live?.Id,
            Status = live is null ? null : live.Status.ToString(),
            TargetFrameworks = frameworks,
            TestFramework = TestFrameworkOf(packages),
            TestingPlatform = PackageVersion(packages, "Microsoft.Testing.Platform"),
            Packages = packages,
            Note = note,
        };
    }

    private static (List<string> Frameworks, List<PackageInfo> Packages, string? Note) ReadAssets(string assetsFile)
    {
        if (!File.Exists(assetsFile)) return ([], [], "not restored (no obj/project.assets.json)");
        try
        {
            using var stream = new FileStream(assetsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            return (AssetFrameworks(root), AssetPackages(root), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ([], [], "obj/project.assets.json could not be read: " + ex.Message);
        }
    }

    private static List<string> AssetFrameworks(JsonElement root)
    {
        var frameworks = new List<string>();
        if (root.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var fws) && fws.ValueKind == JsonValueKind.Object)
            frameworks.AddRange(fws.EnumerateObject().Select(fw => fw.Name));
        return frameworks;
    }

    /// <summary>The test-related packages of project.assets.json's libraries (see <see cref="PackagePrefixes"/>).</summary>
    private static List<PackageInfo> AssetPackages(JsonElement root)
    {
        var packages = new List<PackageInfo>();
        if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object) return packages;
        foreach (var lib in libraries.EnumerateObject())
        {
            if (lib.Value.TryGetProperty("type", out var type) && type.GetString() != "package") continue;
            var slash = lib.Name.IndexOf('/');
            if (slash <= 0) continue;
            var id = lib.Name[..slash];
            if (PackagePrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                packages.Add(new PackageInfo(id, lib.Name[(slash + 1)..]));
        }
        return packages;
    }

    private static XDocument? LoadProject(string projectPath)
    {
        try { return XDocument.Load(projectPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
    }

    private static List<string> ReadProjectFrameworks(string projectPath)
    {
        var doc = LoadProject(projectPath);
        var value = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName is "TargetFrameworks" or "TargetFramework")?.Value;
        return value is null ? [] : [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary><c>&lt;Project Sdk="MSTest.Sdk/4.0.2"&gt;</c> as a package.</summary>
    private static PackageInfo? ReadSdkPackage(string projectPath)
    {
        var sdk = (string?)LoadProject(projectPath)?.Root?.Attribute("Sdk");
        if (sdk is null) return null;
        foreach (var part in sdk.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = part.IndexOf('/');
            var id = slash > 0 ? part[..slash] : part;
            if (PackagePrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return new PackageInfo(id, slash > 0 ? part[(slash + 1)..] : "(global.json)");
        }
        return null;
    }

    private static string? PackageVersion(List<PackageInfo> packages, string name) =>
        packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Version;

    internal static string? TestFrameworkOf(List<PackageInfo> packages)
    {
        (string Package, string Name)[] frameworks =
        [
            ("MSTest.TestFramework", "MSTest"), ("MSTest.Sdk", "MSTest"), ("xunit.v3.core", "xUnit v3"), ("xunit.v3", "xUnit v3"),
            ("NUnit", "NUnit"), ("TUnit.Core", "TUnit"), ("TUnit", "TUnit"),
        ];
        foreach (var (package, name) in frameworks)
            if (PackageVersion(packages, package) is { } v) return $"{name} {v}";
        return null;
    }

    // ------------------------------------------------------------------ environment and redaction

    /// <summary>Variables listed besides every LABCOAT_*.</summary>
    private static readonly string[] ListedVariables =
    [
        "PORT", "ASPNETCORE_URLS", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "DOTNET_ROOT", "DOTNET_HOST_PATH",
        "DOTNET_CLI_UI_LANGUAGE", "LANG", "LC_ALL", "CI", "NO_COLOR", "BROWSER", "ASPIRE_DASHBOARD_URL",
        "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_HEADERS",
    ];

    /// <summary>Name parts of variables whose value is never shown.</summary>
    private static readonly string[] SecretNameParts = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "KEY", "HEADERS", "AUTH", "CONNECTIONSTRING", "CREDENTIAL"];

    /// <summary>Labcoat's, Aspire's and OpenTelemetry's variables (the ones whose secrets could reach Labcoat's log).</summary>
    private static bool IsOwnVariable(string name) =>
        name.StartsWith("LABCOAT_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ASPIRE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("DOTNET_DASHBOARD", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSecretName(string name) => SecretNameParts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

    internal static SortedDictionary<string, string> SelectedEnvironment(IDictionary<string, string?> env)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in env)
        {
            if (value is null || !(name.StartsWith("LABCOAT_", StringComparison.OrdinalIgnoreCase) || ListedVariables.Contains(name, StringComparer.OrdinalIgnoreCase)))
                continue;
            d[name] = IsSecretName(name) ? Redacted : RedactUrlCredentials(value);
        }
        return d;
    }

    /// <summary><c>scheme://user:password@host</c> → <c>scheme://***@host</c>.</summary>
    internal static string RedactUrlCredentials(string value)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < value.Length)
        {
            var scheme = value.IndexOf("://", i, StringComparison.Ordinal);
            if (scheme < 0) break;
            var start = scheme + 3;
            var end = start;
            while (end < value.Length && value[end] is not ('/' or ' ' or ';' or ',' or '@')) end++;
            sb.Append(value, i, start - i);
            if (end < value.Length && value[end] == '@')
            {
                sb.Append("***");
                i = end;
            }
            else i = start;
        }
        sb.Append(value, i, value.Length - i);
        return sb.ToString();
    }

    /// <summary>The values of secret variables (LABCOAT_TOKEN, OTLP headers, …) that are long enough to replace in log lines.</summary>
    private static List<string> SecretValues(IDictionary<string, string?> env)
    {
        var values = new List<string>();
        foreach (var (name, value) in env)
        {
            if (value is not { Length: >= 6 } || !IsSecretName(name) || !IsOwnVariable(name)) continue;
            values.Add(value);
            // OTLP headers: "name=value,name2=value2": each value on its own too
            foreach (var pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (pair.IndexOf('=') is var eq and > 0 && pair[(eq + 1)..].Trim() is { Length: >= 6 } part) values.Add(part);
        }
        return [.. values.Distinct().OrderByDescending(v => v.Length)];
    }

    internal static string RedactValues(string line, IReadOnlyList<string> secrets)
    {
        foreach (var s in secrets) line = line.Replace(s, "***", StringComparison.Ordinal);
        return RedactUrlCredentials(line);
    }

    /// <summary>
    /// Matches the home directory as a whole path (its real path first: on macOS /var/… is /private/var/…), only where
    /// a path starts (start of text, after a space, quote, '=' or ':') and ends (a separator, the end, a space or quote),
    /// so /private/var/x with home /var/x isn't turned into "/private~".
    /// </summary>
    private static Regex HomePattern(string home)
    {
        var real = Path.TrimEndingDirectorySeparator(PathUtil.CanonicalizeOrSelf(home));
        var alternatives = real == home ? Regex.Escape(home) : Regex.Escape(real) + "|" + Regex.Escape(home);
        var options = RegexOptions.CultureInvariant | (WorkspaceIdentity.CaseInsensitiveFileSystem ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex("(?<=^|[\\s\"'=:(])(?:" + alternatives + ")(?=[/\\\\]|$|[\\s\"')])", options, RegexTimeout.Value);
    }

    /// <summary>For tests: <paramref name="text"/> with the home directory <paramref name="home"/> masked as "~".</summary>
    internal static string MaskHomeForTest(string text, string home) => MaskHome(text, HomePattern(Path.TrimEndingDirectorySeparator(home)));

    private static string MaskHome(string text, Regex homePattern)
    {
        try { return homePattern.Replace(text, "~"); }
        catch (RegexMatchTimeoutException) { return text; } // pathological input: leave it as it is
    }

    /// <summary>Paths under the home directory as "~/…" (user names are private).</summary>
    private static Report Anonymize(Report r, string home)
    {
        if (string.IsNullOrEmpty(home) || Path.TrimEndingDirectorySeparator(home) is not { Length: > 1 } h) return r;
        var homePattern = HomePattern(h);
        string A(string s) => MaskHome(s, homePattern);
        return r with
        {
            StartedAs = A(r.StartedAs),
            Workspace = A(r.Workspace),
            DataDirectory = A(r.DataDirectory),
            Projects = [.. r.Projects.Select(p => p with { Path = A(p.Path) })],
            Settings = new SortedDictionary<string, string>(r.Settings.ToDictionary(kv => kv.Key, kv => A(kv.Value)), StringComparer.Ordinal),
            Environment = new SortedDictionary<string, string>(r.Environment.ToDictionary(kv => kv.Key, kv => A(kv.Value)), StringComparer.Ordinal),
            Log = [.. r.Log.Select(A)],
            LogSource = r.LogSource is null ? null : A(r.LogSource),
        };
    }

    // ------------------------------------------------------------------ markdown

    private static string Cell(string? s) => string.IsNullOrEmpty(s) ? "-" : s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    /// <summary>The report as GitHub markdown (for the bug report's "Diagnostics and logs" field).</summary>
    public static string ToMarkdown(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Labcoat diagnostics").AppendLine();
        sb.AppendLine("| | |").AppendLine("| --- | --- |");
        sb.AppendLine($"| Labcoat | {Cell(r.LabcoatVersion)} ({Cell(r.StartedAs)}) |");
        sb.AppendLine($"| OS | {Cell(r.Os)} |");
        sb.AppendLine($"| .NET runtime | {Cell(r.Runtime)} |");
        sb.AppendLine($"| .NET SDK | {Cell(r.Sdk ?? "unknown")} |");
        sb.AppendLine($"| Workspace | {Cell(r.WorkspaceKind)}: {Cell(r.Workspace)} |");
        sb.AppendLine($"| Data folder | {Cell(r.DataDirectory)}{(r.DataReadOnly ? " (read-only: another Labcoat uses it)" : "")} |");
        sb.AppendLine();

        sb.AppendLine($"#### Test projects ({r.Projects.Count})").AppendLine();
        if (r.Projects.Count > 0)
        {
            sb.AppendLine("| Project | Target frameworks | Test framework | Microsoft.Testing.Platform | Status |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var p in r.Projects)
                sb.AppendLine($"| {Cell(p.Name)} (`{Cell(p.Path)}`) | {Cell(string.Join(", ", p.TargetFrameworks))} | {Cell(p.TestFramework ?? p.Note)} | {Cell(p.TestingPlatform)} | {Cell(p.Status)} |");
            sb.AppendLine();
            sb.AppendLine("<details><summary>Test packages</summary>").AppendLine().AppendLine(TextFence);
            foreach (var p in r.Projects)
                sb.AppendLine($"{p.Name}: {(p.Packages.Count == 0 ? p.Note ?? "(none found)" : string.Join(", ", p.Packages.Select(x => $"{x.Name} {x.Version}")))}");
            sb.AppendLine("```").AppendLine().AppendLine("</details>").AppendLine();
        }

        sb.AppendLine("#### Settings").AppendLine().AppendLine(TextFence);
        foreach (var (k, v) in r.Settings) sb.AppendLine($"{k}: {v}");
        sb.AppendLine("```").AppendLine();

        sb.AppendLine("#### Environment").AppendLine().AppendLine(TextFence);
        if (r.Environment.Count == 0) sb.AppendLine("(no LABCOAT_* or related variables)");
        foreach (var (k, v) in r.Environment) sb.AppendLine($"{k}={v}");
        sb.AppendLine("```").AppendLine();

        sb.AppendLine($"#### Log (last {r.Log.Count} lines{(r.LogSource is null ? "" : ", " + r.LogSource)})").AppendLine();
        sb.AppendLine("<details><summary>Log</summary>").AppendLine().AppendLine(TextFence);
        if (r.Log.Count == 0) sb.AppendLine("(empty)");
        foreach (var l in r.Log) sb.AppendLine(l.Replace("```", "'''"));
        sb.AppendLine("```").AppendLine().AppendLine("</details>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the API and the command line

    /// <summary><c>GET /api/diagnostics</c>: <c>{ markdown, report }</c>.</summary>
    public static void Map(WebApplication app, LabcoatAppOptions o, Explorer explorer, HostLog log)
    {
        app.MapGet("/api/diagnostics", async (CancellationToken ct) =>
        {
            var state = explorer.Snapshot(lean: true);
            var settings = new DiagnosticsSettings
            {
                Watch = state.Watch, Coverage = state.CoverageEnabled, Traces = state.TracingEnabled,
                AffectedBase = o.AffectedBase, ImpactMap = o.ImpactMap, CoverageOptions = o.Coverage, UiDefaults = o.UiDefaults,
                SettingsFiles = o.SettingsFiles, LogLevel = log.Level, LogFile = log.FilePath, Url = o.Url,
            };
            var report = await CollectAsync(new Input
            {
                Projects = o.Projects, Live = state.Projects, Settings = settings.ToDictionary(), Log = log.Recent(LogLines),
                LogSource = "this session",
            }, ct);
            return Results.Json(new { markdown = ToMarkdown(report), report }, Json.Options);
        });
    }

    /// <summary>
    /// <c>labcoat --diagnostics</c>: prints the report (no host is started; the log is the tail of the log file, when one
    /// is configured or in the data folder) and copies it to the clipboard when the output is a terminal.
    /// </summary>
    public static async Task<int> PrintAsync(IReadOnlyList<string> projects, DiagnosticsSettings settings, CancellationToken ct)
    {
        var logFile = settings.LogFile ?? Path.Combine(WorkspaceIdentity.Resolve(projects).DataDirectory, HostLog.DefaultFileName);
        var report = await CollectAsync(new Input
        {
            Projects = projects, Settings = settings.ToDictionary(), Log = HostLog.Tail(logFile, LogLines),
            LogSource = File.Exists(logFile) ? logFile : "no log file; start Labcoat with --log-file auto to keep one",
        }, ct);
        var markdown = ToMarkdown(report);
        await Console.Out.WriteLineAsync(markdown);
        if (CanCopy() && CopyToClipboard(markdown))
            await Console.Error.WriteLineAsync(Cli.Dim("Copied to the clipboard: paste it into the bug report (remove anything private first)."));
        return 0;
    }

    /// <summary>Test seam: whether to copy (the output is a terminal and this is not CI).</summary>
    internal static Func<bool> CanCopy { get; set; } =
        () => !Console.IsOutputRedirected && string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("CI"));

    /// <summary>Test seam: copies text to the clipboard; false when no clipboard tool worked.</summary>
    internal static Func<string, bool> CopyToClipboard { get; set; } = text => TryCopyToClipboard(text, ClipboardTools());

    /// <summary>The clipboard commands to try on this OS (Linux: Wayland's, then X11's).</summary>
    internal static List<(string File, string[] Args)> ClipboardTools()
    {
        if (OperatingSystem.IsWindows()) return [("clip", [])];
        if (OperatingSystem.IsMacOS()) return [("pbcopy", [])];
        var tools = new List<(string File, string[] Args)>();
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) tools.Add(("wl-copy", []));
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DISPLAY")))
        {
            tools.Add(("xclip", ["-selection", "clipboard"]));
            tools.Add(("xsel", ["--clipboard", "--input"]));
        }
        return tools;
    }

    /// <summary>Pipes <paramref name="text"/> into the first of <paramref name="tools"/> that runs and exits with 0.</summary>
    internal static bool TryCopyToClipboard(string text, IEnumerable<(string File, string[] Args)> tools)
    {
        foreach (var (file, args) in tools)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { RedirectStandardInput = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                if (p.WaitForExit(5000) && p.ExitCode == 0) return true;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { /* not installed: try the next one */ }
        }
        return false;
    }
}
