using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Labcoat;

/// <summary>
/// Command-line experience of the standalone tool (<c>labcoat</c>, <c>dotnet labcoat</c>, <c>dnx Labcoat</c>):
/// usage text, version, default workspace resolution, free-port selection, the startup banner and opening the browser.
/// </summary>
public static class Cli
{
    /// <summary>First port tried when none is given: $LABCOAT_DEFAULT_PORT, else 4300.</summary>
    public static int DefaultPort { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("LABCOAT_DEFAULT_PORT"), out var p) && p is > 0 and <= 65535 ? p : 4300;

    /// <summary>How many ports after <see cref="DefaultPort"/> are tried when the port wasn't given explicitly.</summary>
    public const int PortAttempts = 20;

    /// <summary>Package version without the "+commit" suffix, e.g. "1.0.0" (or "1.1.0-beta.1" for a prerelease).</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(Cli).Assembly;
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    private const string UiEnvExample = """{"theme":"dracula","locked":["theme"]}""";

    /// <summary>" --prerelease" while <paramref name="version"/> is a prerelease (dotnet tool / dnx skip prereleases without it), else "".</summary>
    internal static string PrereleaseFlag(string version) => version.Contains('-') ? " --prerelease" : "";

    private static string Pre => PrereleaseFlag(Version);

    public static string Usage => $"""
        Labcoat {Version} - a web UI for running and exploring Microsoft.Testing.Platform tests.

        Usage:
          labcoat [paths...] [options]           global tool  (dotnet tool install -g Labcoat{Pre})
          dotnet labcoat [paths...] [options]    local tool   (dotnet tool install Labcoat{Pre}, with a tool manifest)
          dnx Labcoat{Pre} [paths...] [options]
                                                 no install   (.NET 10 SDK; dnx reads --help/--version itself:
                                                               use 'dnx Labcoat{Pre} -- --help')

        Arguments:
          paths            .csproj / .sln / .slnx / .slnf files or directories; directories are scanned
                           recursively for test projects. Default: the test project in the current directory,
                           else the nearest .slnx/.sln walking up from it (up to the repository root),
                           else the current directory.

        Options:
          -p, --port <n>   HTTP port. Default {DefaultPort}; when it is busy the next free port is used.
                           An explicit --port (or $PORT) is used as is and fails when busy.
          --no-open        Don't open the browser (also skipped when $CI is set or no display is available).
          -w, --watch      Start in watch mode (rebuild + re-run affected tests on save).
          --coverage       Start with code coverage collection enabled.
          --coverage-settings <file>  Coverage settings for every test project (relative: to each test project,
                           then the current directory). Default: the project's own coverage.settings.xml /
                           CodeCoverage.xml / *.runsettings, else Labcoat's defaults (docs/features.md).
          --coverage-include <globs>  Modules or path globs to count although the defaults leave them out
                           (e.g. Labcoat.*). Comma-separated; repeatable.
          --coverage-exclude <globs>  Modules or path globs never to count. Comma-separated; repeatable.
          --traces         Start with per-test trace collection enabled (only has an effect when hosted by an
                           Aspire AppHost). Off by default; a choice made in the UI is remembered per workspace.
          --trace-filter <expr>  While trace collection is on, trace only the tests that match <expr>, in the
                           UI's search syntax: cat:<category>, trait:<key>[=<value>], proj:<project> or name
                           text; every term must match (e.g. "cat:Integration trait:Kind=Slow").
          --root <dir>     Extra directory whose source files the UI may show (e.g. the solution root, for
                           stack-trace frames outside the test projects). Repeatable.
          --affected-base <ref>  Base of "Run affected: branch vs …" (default: origin/HEAD, else
                           origin/main, origin/master, main or master).
          --impact-map <m> Granularity of the impact map that smart watch and "Run affected" use: class (one
                           coverage run per test class), test (one per test: exact, slower) or auto (per class,
                           then refined per test where a change needs it; the default).
          --max-parallel <n>  Test projects discovered and run at the same time (default: processors, max 4).
          --hang-threshold <d>  Mark a test running this long as possibly hanging (default 60s; 0 = off).
          --test-timeout <d>  Per-test timeout: a longer test is marked timed out and its project's run is
                           cancelled (off by default). Durations: 90, 30s, 5m, 1.5h; 0 or off.
          --config <file>  Settings file to use instead of the nearest .labcoat.json / .config/labcoat.json
                           (searched from the workspace up to the repository root).
          --no-user-config Ignore the user settings file (~/.labcoat.json or the platform config dir).
          -v, --verbose    Log more of the host (ASP.NET Core) at Debug level (like LABCOAT_LOG_LEVEL=debug).
          --log-file <path>  Also write the log to a file (capped at 10 MB, one rotated .1 copy). 'auto' = labcoat.log
                           in this workspace's data folder (see LABCOAT_HISTORY_DIR).
          --diagnostics    Print a diagnostics report for bug reports (version, OS, .NET, workspace, projects,
                           test packages, settings with secrets redacted, recent log lines), copy it to the
                           clipboard when possible, and exit.
          --version        Print the version and exit.
          -h, --help       Show this help.

        Run once (CI; no UI, no browser):
          --run            Build, run the tests once, write the --export files and exit: 0 when no test
                           failed, 1 when one failed (or the run was aborted / cancelled), 2 when a project
                           did not build or discover (or nothing could run).
          --export <format>=<file>  Write a report: junit, html, markdown, trx (Visual Studio / Azure DevOps),
                           sarif (code scanning), lcov or cobertura. Repeatable; lcov and cobertura
                           collect coverage (like --coverage) and hold this run's coverage.
          --filter <pattern>  Only the tests whose name, Namespace.Class(.Method) or project contains it
                           ('*' = wildcard over the whole name). Comma-separated; repeatable.
          --export-paths <m>  Source paths in lcov / cobertura: relative (to the workspace root; default)
                           or absolute.
                           On GitHub Actions, --run also appends a Markdown summary to $GITHUB_STEP_SUMMARY and
                           annotates failed tests at their source line (no option needed).

        UI defaults (a choice made in the browser's Settings page wins unless the setting is locked):
          --theme <name>   catppuccin, aspire, github, one, dracula, solarized or gruvbox
          --mode <mode>    system, light or dark
          --editor <id>    vscode, rider or visualstudio ("open in editor")
          --motion <m>     system, reduced or off
          --lock <names>   Lock settings so the Settings page can't change them: comma-separated names
                           (theme, mode, motion, editor, coverageTint, confetti, partialRunsInHistory,
                           openOutputOnError, singleKeyShortcuts) or 'all'.

        Environment:
          PORT, ASPNETCORE_URLS   listen address (lower precedence than --port)
          LABCOAT_DEFAULT_PORT    first port tried when no port is given (default 4300)
          LABCOAT_HISTORY_DIR     where test history, past runs, the coverage report and
                                  per-workspace settings are kept
                                  (default: {DefaultDataDir}; one sub-folder per workspace,
                                  delete it to reset)
          LABCOAT_NAME            workspace name shown in the UI (default: the solution / project name)
          LABCOAT_ALLOWED_HOSTS   extra host names the server answers to, comma-separated (default: localhost,
                                  *.localhost, 127.0.0.1, [::1] and the listen host); '*' turns the check off
          LABCOAT_ALLOWED_ORIGINS extra browser origins allowed to send commands, e.g. http://localhost:5174
                                  (a Vite dev server proxying to Labcoat)
          LABCOAT_TOKEN           when set, scripts (requests without an Origin header) must send it as
                                  X-Labcoat-Token on POST /api/* and /ws (Labcoat.Aspire.Hosting sets it)
          LABCOAT_CANCEL_GRACE_SECONDS  how long a cancelled run may take before the test host is killed (default 10)
          LABCOAT_VERIFY_WARM     1 = keep warm test hosts for projects that use Verify (default: a new host per run)
          LABCOAT_PATH            (Labcoat.TestingPlatform's --labcoat) the Labcoat.dll / labcoat executable to start
          LABCOAT_UI_SETTINGS     UI defaults as JSON, e.g. {UiEnvExample}
                                  (above the settings files, below the command line)
          LABCOAT_NO_USER_CONFIG  1 = ignore the user settings file (like --no-user-config)
          LABCOAT_AFFECTED_BASE   base ref of "Run affected: branch vs …" (like --affected-base)
          LABCOAT_IMPACT_MAP      class, test or auto (like --impact-map)
          LABCOAT_TRACE_FILTER    the tests to trace (like --trace-filter)
          LABCOAT_MAX_PARALLEL, LABCOAT_HANG_THRESHOLD, LABCOAT_TEST_TIMEOUT
                                  like --max-parallel, --hang-threshold, --test-timeout
          LABCOAT_LIVE_OUTPUT     0 = no live output of running tests (no extra test host options or
                                  variables, no liveOutput events)
          LABCOAT_LOG_LEVEL       host log level: trace, debug, information, warning (default), error,
                                  critical or none (--verbose = debug)
          LABCOAT_LOG_FILE        like --log-file
          BROWSER                 command used to open the browser (default: xdg-open / open / start)
          NO_COLOR                disable colored output

        Examples:
          labcoat                                  tests of the current project / solution
          labcoat tests/MyApp.Tests --watch        one project, watch mode
          labcoat MyApp.slnx --port 5000 --no-open
          labcoat --theme dracula --mode dark      UI defaults for this start
          labcoat --run --export junit=out/results.xml --export html=out/report.html
                                                   CI: run everything once, write JUnit + HTML reports
          labcoat --run --export markdown=out/summary.md --export sarif=out/tests.sarif
                                                   CI: a PR-comment Markdown report and SARIF failures
        """;

    /// <summary>Default base directory for history / settings / caches ($LABCOAT_HISTORY_DIR overrides it).</summary>
    public static string DefaultDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "labcoat");

    // ------------------------------------------------------------------ workspace

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>
    /// The paths to use when none were given: the current directory when it contains a test project, else the nearest
    /// solution (.slnx preferred over .sln) walking up from <paramref name="cwd"/>, stopping at the repository root
    /// (a directory containing .git) or the home directory; else the current directory.
    /// </summary>
    public static string ResolveDefaultPath(string cwd, Action<string>? warn = null)
    {
        try
        {
            if (Directory.EnumerateFiles(cwd).Any(f => ProjectExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
                                                       && ProjectDiscovery.IsTestProject(f)))
                return cwd;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return cwd; }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            var solutions = SolutionsIn(dir);
            if (solutions is null) break;
            if (solutions.Length > 0)
            {
                // foo.sln next to foo.slnx is the usual migration leftover: not worth a warning.
                var distinct = solutions.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (distinct > 1)
                    warn?.Invoke($"Several solutions in {dir.FullName}; using {Path.GetFileName(solutions[0])}. Pass a path to pick another.");
                return solutions[0];
            }
            if (EndsWalkUp(dir, home)) break;
        }
        return cwd;
    }

    /// <summary>The solutions in <paramref name="dir"/>, .slnx first; null when it can't be read.</summary>
    private static string[]? SolutionsIn(DirectoryInfo dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir.FullName)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".slnx" or ".sln")
                .OrderBy(f => Path.GetExtension(f).Equals(".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The repository root (a directory containing .git) or the home directory: the walk-up stops there.</summary>
    private static bool EndsWalkUp(DirectoryInfo dir, string home) =>
        Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))
        || (home.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(dir.FullName), Path.TrimEndingDirectorySeparator(home), PathUtil.Comparison));

    // ------------------------------------------------------------------ port

    /// <summary>True when nothing listens on <paramref name="port"/> on the IPv4 or IPv6 loopback (what "localhost" binds).</summary>
    public static bool IsPortFree(int port)
    {
        if (!CanBind(IPAddress.Loopback, port)) return false;
        return !Socket.OSSupportsIPv6 || CanBind(IPAddress.IPv6Loopback, port);
    }

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported)
        {
            return true; // no such loopback (e.g. IPv6 disabled): Kestrel skips it too
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The first free port in [<paramref name="start"/>, start + <see cref="PortAttempts"/>), or null.</summary>
    public static int? FindFreePort(int start)
    {
        for (var p = start; p < start + PortAttempts && p <= IPEndPoint.MaxPort; p++)
            if (IsPortFree(p)) return p;
        return null;
    }

    // ------------------------------------------------------------------ output

    public static bool UseColor { get; } =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) && !Console.IsOutputRedirected
        || Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_URL") is { Length: > 0 }; // the dashboard log viewer renders ANSI

    public static string Bold(string s) => Ansi("1", s);
    public static string Dim(string s) => Ansi("2", s);
    public static string Gray(string s) => Ansi("90", s);
    public static string Cyan(string s) => Ansi("36", s);
    public static string Yellow(string s) => Ansi("33", s);
    public static string Red(string s) => Ansi("31", s);
    public static string Badge(string s) => Ansi("1;30;46", s);
    private static string Ansi(string code, string s) => UseColor ? $"\x1b[{code}m{s}\x1b[0m" : s;

    private static bool Utf8Out => Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage;
    private static string Arrow => Utf8Out ? "➜" : ">";
    private static string Bullet => Utf8Out ? "·" : "-";

    public static void Warn(string message) => Console.Error.WriteLine($"{Yellow("warning")} {message}");

    /// <summary>The startup banner: version, URL, workspace, test projects and how to stop.</summary>
    public static void PrintBanner(string url, IReadOnlyList<string> inputPaths, IReadOnlyList<string> projects,
        string? portNote, string? aspireDashboardUrl, IReadOnlyList<string>? settingsFiles = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"  {Badge(" LABCOAT ")} {Bold("v" + Version)}  {Dim("test explorer for Microsoft.Testing.Platform")}");
        sb.AppendLine();
        sb.AppendLine($"  {Cyan(Arrow)}  {Bold("Local:")}     {Cyan(url)}");
        if (portNote is not null) sb.AppendLine($"     {Dim(portNote)}");
        if (aspireDashboardUrl is not null) sb.AppendLine($"  {Cyan(Arrow)}  {Bold("Aspire:")}    {aspireDashboardUrl}");

        var baseDir = PathUtil.CommonRoot(inputPaths);
        var workspace = inputPaths.Count == 1 ? inputPaths[0] : baseDir ?? string.Join(", ", inputPaths);
        sb.AppendLine($"  {Cyan(Arrow)}  {Bold("Workspace:")} {workspace}");
        if (settingsFiles is { Count: > 0 }) sb.AppendLine($"  {Cyan(Arrow)}  {Bold("Settings:")}  {string.Join(", ", settingsFiles)}");

        var label = projects.Count == 1 ? "1 test project" : $"{projects.Count} test projects";
        sb.AppendLine($"  {Cyan(Arrow)}  {Bold("Tests:")}     {(projects.Count == 0 ? Yellow("no test projects found") : label)}");
        const int maxListed = 12;
        foreach (var p in projects.Take(maxListed))
        {
            var shown = baseDir is not null ? Path.GetRelativePath(baseDir, p) : p;
            sb.AppendLine($"                {Dim(Bullet)} {shown}");
        }
        if (projects.Count > maxListed) sb.AppendLine($"                {Dim($"{Bullet} ... and {projects.Count - maxListed} more")}");
        if (projects.Count == 0)
            sb.AppendLine($"     {Dim("Looked for MSTest / MSTest.Sdk / Microsoft.Testing.Platform / xunit.v3 / TUnit references, or")}{Environment.NewLine}" +
                          $"     {Dim("IsTestingPlatformApplication / EnableMSTestRunner / EnableNUnitRunner / IsTestProject (MSBuild")}{Environment.NewLine}" +
                          $"     {Dim("evaluation for test-like projects). Pass a .csproj or .sln path (see --help).")}");
        sb.AppendLine();
        sb.AppendLine($"  {Dim("Press Ctrl+C to stop.")}");
        Console.Write(sb.ToString());
    }

    // ------------------------------------------------------------------ browser

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser. Never throws; when no browser can be started (SSH session,
    /// container, CI) it prints the URL with a hint instead.
    /// </summary>
    public static void OpenBrowser(string url)
    {
        if (IsTruthy(Environment.GetEnvironmentVariable("CI"))) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                return;
            }
            var browserEnv = Environment.GetEnvironmentVariable("BROWSER");
            if (OperatingSystem.IsMacOS() && string.IsNullOrWhiteSpace(browserEnv))
            {
                Launch("open", [url], url, "open");
                return;
            }

            // Linux & co (and macOS with $BROWSER): $BROWSER, else xdg-open (wslview on WSL).
            var args = new List<string>();
            var command = BrowserCommand(browserEnv, url, args) ?? DefaultOpener(url, args);
            if (command is null) return;
            var displayName = Path.GetFileName(command);
            // setsid: give the browser its own session, so a browser started by us doesn't receive the Ctrl+C meant for
            // Labcoat (the terminal signals the whole foreground process group).
            if (FindOnPath("setsid") is { } setsid)
            {
                args.Insert(0, command);
                command = setsid;
            }
            Launch(command, args, url, displayName);
        }
        catch (Exception ex)
        {
            NoBrowser(url, ex.Message);
        }
    }

    /// <summary>Python-style $BROWSER: a ':'-separated list of commands, optionally with %s for the URL. Null when unset.</summary>
    private static string? BrowserCommand(string? browserEnv, string url, List<string> args)
    {
        if (string.IsNullOrWhiteSpace(browserEnv)) return null;
        var first = browserEnv.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null) return null;
        var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        args.AddRange(parts.Skip(1).Select(p => p.Replace("%s", url)));
        if (!first.Contains("%s")) args.Add(url);
        return parts[0];
    }

    /// <summary>xdg-open (wslview on WSL); null (the URL printed with a hint) without a display or an opener.</summary>
    private static string? DefaultOpener(string url, List<string> args)
    {
        var headless = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                       && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                       && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"));
        if (headless)
        {
            NoBrowser(url, Environment.GetEnvironmentVariable("SSH_CONNECTION") is { Length: > 0 }
                ? "SSH session without a display"
                : "no display found");
            return null;
        }
        var command = FindOnPath("xdg-open")
                      ?? (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 } ? FindOnPath("wslview") : null);
        if (command is null)
        {
            NoBrowser(url, "xdg-open not found");
            return null;
        }
        args.Add(url);
        return command;
    }

    private static void Launch(string command, IEnumerable<string> args, string url, string displayName)
    {
        var psi = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true, // browser chatter ("Opening in existing browser session.") stays out of our output
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) =>
        {
            // xdg-open exits non-zero when it found nothing to open the URL with.
            int code;
            try { code = process.ExitCode; } catch { return; }
            if (code != 0) NoBrowser(url, $"{displayName} exited with code {code}");
        };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static void NoBrowser(string url, string reason) =>
        Console.WriteLine($"  {Yellow("!")}  Could not open a browser ({reason}). Open {Cyan(url)} yourself" +
                          (Environment.GetEnvironmentVariable("SSH_CONNECTION") is { Length: > 0 }
                              ? $" (from your machine: ssh -L {new Uri(url).Port}:localhost:{new Uri(url).Port} <host>)."
                              : "."));

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";
}
