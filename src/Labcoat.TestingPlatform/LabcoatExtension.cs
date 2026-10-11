using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Microsoft.Testing.Platform.Extensions.TestHost;
using Microsoft.Testing.Platform.Services;

namespace Labcoat.TestingPlatform;

/// <summary>
/// Entry point called by the Microsoft.Testing.Platform generated <c>SelfRegisteredExtensions</c> (registered through the
/// <c>TestingPlatformBuilderHook</c> MSBuild item in this package's build/ props).
/// </summary>
public static class TestingPlatformBuilderHook
{
    /// <summary>Adds the <c>--labcoat</c> option and its launcher.</summary>
    public static void AddExtensions(ITestApplicationBuilder builder, string[] _) => builder.AddLabcoat();
}

/// <summary>Registration helpers for a hand-written Microsoft.Testing.Platform entry point.</summary>
public static class LabcoatTestApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the <c>--labcoat</c> command-line option: instead of running the tests, the test application starts the
    /// Labcoat web runner for its own project, opens the browser and stays alive until Labcoat stops (Ctrl+C).
    /// </summary>
    public static void AddLabcoat(this ITestApplicationBuilder builder)
    {
        builder.CommandLine.AddProvider(() => new LabcoatCommandLineProvider());
        builder.TestHost.AddTestHostApplicationLifetime(sp => new LabcoatLauncher(sp.GetCommandLineOptions()));
    }
}

internal static class LabcoatInfo
{
    public const string Uid = "Labcoat.TestingPlatform";
    public const string DisplayName = "Labcoat";
    public const string OptionName = "labcoat";

    public static string Version { get; } =
        typeof(LabcoatInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary><c>dnx Labcoat</c>, with <c>--prerelease</c> while this is a prerelease (dnx skips prereleases without it).</summary>
    public static string DnxCommand => DnxCommandFor(Version);

    internal static string DnxCommandFor(string version) => version.Contains('-') ? "dnx Labcoat --prerelease" : "dnx Labcoat";
}

/// <summary>
/// <c>--labcoat [settings...]</c>, e.g. <c>--labcoat</c> or <c>--labcoat watch port=5000</c>. Settings are Labcoat options
/// without the leading dashes: MTP's parser reads any value starting with "--" as a new option.
/// </summary>
internal sealed class LabcoatCommandLineProvider : ICommandLineOptionsProvider
{
    public string Uid => LabcoatInfo.Uid;
    public string Version => LabcoatInfo.Version;
    public string DisplayName => LabcoatInfo.DisplayName;
    public string Description => "Opens this test project in the Labcoat web test runner.";
    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() =>
    [
        new(LabcoatInfo.OptionName,
            "Open this test project in the Labcoat web UI instead of running the tests. " +
            "Optional settings: watch, coverage, traces, no-open, port=<n>, root=<dir>; e.g. --labcoat watch port=5000.",
            ArgumentArity.ZeroOrMore, isHidden: false),
    ];

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments)
    {
        foreach (var a in arguments)
        {
            var (name, value) = Split(a);
            var ok = name switch
            {
                "watch" or "coverage" or "traces" or "no-open" => value is null,
                "port" => int.TryParse(value, out var p) && p is > 0 and <= 65535,
                "root" => !string.IsNullOrEmpty(value),
                _ => false,
            };
            if (!ok)
                return ValidationResult.InvalidTask($"Unknown --labcoat setting '{a}'. Use: watch, coverage, traces, no-open, port=<n>, root=<dir>.");
        }
        return ValidationResult.ValidTask;
    }

    /// <summary>"port=5000" -> ("port", "5000"); "watch" -> ("watch", null). Dashes are tolerated: "-w" is not.</summary>
    internal static (string Name, string? Value) Split(string setting)
    {
        var s = setting.TrimStart('-');
        var eq = s.IndexOf('=');
        return eq < 0 ? (s.ToLowerInvariant(), null) : (s[..eq].ToLowerInvariant(), s[(eq + 1)..]);
    }

    /// <summary>The Labcoat command-line arguments for the --labcoat settings.</summary>
    internal static IEnumerable<string> ToLabcoatArgs(IEnumerable<string> settings)
    {
        foreach (var a in settings)
        {
            var (name, value) = Split(a);
            yield return "--" + name;
            if (value is not null) yield return value;
        }
    }

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions)
    {
        // 'dotnet test' (MTP mode) starts the app with --server dotnettestcli and owns its output, possibly for several
        // projects at once: not a place to start an interactive UI. Say so instead of silently running the tests.
        if (commandLineOptions.IsOptionSet(LabcoatInfo.OptionName)
            && commandLineOptions.TryGetOptionArgumentList("server", out var server)
            && server.Any(v => v.Equals("dotnettestcli", StringComparison.OrdinalIgnoreCase)))
            return ValidationResult.InvalidTask(
                $"--labcoat isn't supported under 'dotnet test'. Use 'dotnet run --project <test project> -- --labcoat', or '{LabcoatInfo.DnxCommand}'.");
        return ValidationResult.ValidTask;
    }
}

/// <summary>
/// Runs before the test session: when <c>--labcoat</c> is set (and we are not ourselves a test host started by Labcoat or an
/// IDE), starts Labcoat for this project, waits for it and ends the process with its exit code, so no tests run here.
/// </summary>
internal sealed class LabcoatLauncher(ICommandLineOptions options) : ITestHostApplicationLifetime
{
    /// <summary>Set by Labcoat on every test host it starts.</summary>
    private const string TestHostEnvVar = "LABCOAT_TEST_HOST";

    public string Uid => LabcoatInfo.Uid + ".Launcher";
    public string Version => LabcoatInfo.Version;
    public string DisplayName => LabcoatInfo.DisplayName;
    public string Description => "Starts Labcoat for this test project when --labcoat is passed.";

    public Task<bool> IsEnabledAsync() => Task.FromResult(ShouldLaunch(options, Environment.GetEnvironmentVariable(TestHostEnvVar)));

    // Test seams (the defaults are the real behavior): how Labcoat is started, how long it may take to stop after
    // Ctrl+C, and how this process ends.
    internal static Func<ProcessStartInfo, ILabcoatProcess?> StartProcess { get; set; } = psi => Process.Start(psi) is { } p ? new OsProcess(p) : null;
    internal static TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(35);
    internal static Action<int> Exit { get; set; } = Environment.Exit;

    /// <summary>
    /// Inert unless --labcoat is set, and always inert in server mode (--server: Labcoat itself, IDE test explorers,
    /// 'dotnet test' in MTP mode) or when LABCOAT_TEST_HOST says Labcoat started us: that's what prevents recursion.
    /// </summary>
    internal static bool ShouldLaunch(ICommandLineOptions options, string? testHostEnv) =>
        options.IsOptionSet(LabcoatInfo.OptionName)
        && !options.IsOptionSet("server")
        && string.IsNullOrEmpty(testHostEnv);

    public async Task BeforeRunAsync(CancellationToken cancellationToken)
    {
        options.TryGetOptionArgumentList(LabcoatInfo.OptionName, out var values);
        var extraArgs = LabcoatCommandLineProvider.ToLabcoatArgs(values ?? []).ToList();

        int exitCode;
        try
        {
            exitCode = await RunLabcoatAsync(extraArgs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"Labcoat: {ex.Message}").ConfigureAwait(false);
            exitCode = 1;
        }
        // Labcoat was the whole point of this invocation: end here instead of running the tests in this process.
        await Console.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false); // flushed whether or not the run was cancelled
        Exit(exitCode);
    }

    public Task AfterRunAsync(int exitCode, CancellationToken cancellation) => Task.CompletedTask;

    private static async Task<int> RunLabcoatAsync(IReadOnlyList<string> extraArgs, CancellationToken ct)
    {
        var project = AppContext.GetData("Labcoat.TestingPlatform.ProjectPath") as string;
        if (string.IsNullOrEmpty(project) || !File.Exists(project))
            throw new InvalidOperationException("the test project path isn't known (Labcoat.TestingPlatform.ProjectPath is missing from runtimeconfig.json). Rebuild the project.");

        var psi = CreateStartInfo();
        psi.ArgumentList.Add(project);
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);
        psi.WorkingDirectory = Path.GetDirectoryName(project)!;
        psi.UseShellExecute = false; // inherit the console: Labcoat's banner and logs show up as is
        // Labcoat must not inherit settings meant for this process (e.g. the test-host markers of an outer runner).
        psi.Environment.Remove(TestHostEnvVar);

        using var process = StartProcess(psi) ?? throw new InvalidOperationException($"could not start {psi.FileName}");
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: the terminal sent SIGINT to Labcoat too (same process group / console). Give it time to stop its
            // warm test hosts (it waits up to 30 s for fixtures), then make sure nothing is left behind.
            using var grace = new CancellationTokenSource(StopGrace);
            try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { process.Kill(); } catch { /* best effort: the process may have exited already */ } }
        }
        return process.HasExited ? process.ExitCode : 1;
    }

    /// <summary>
    /// Which Labcoat to start, in order: $LABCOAT_PATH (a Labcoat.dll or a labcoat executable, e.g. a local build), the
    /// runner bundled in this package (path recorded at build time), else <c>dotnet tool exec Labcoat@version</c>.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo()
    {
        var path = Environment.GetEnvironmentVariable("LABCOAT_PATH") is { Length: > 0 } env ? env
            : AppContext.GetData("Labcoat.TestingPlatform.LabcoatPath") as string;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo(DotnetPath());
                psi.ArgumentList.Add(path);
                return psi;
            }
            return new ProcessStartInfo(path);
        }
        var exec = new ProcessStartInfo(DotnetPath());
        foreach (var a in new[] { "tool", "exec", $"Labcoat@{LabcoatInfo.Version}", "--yes", "--" }) exec.ArgumentList.Add(a);
        return exec;
    }

    /// <summary>Where System.Private.CoreLib was loaded from; empty in a single-file or Native AOT app.</summary>
    [UnconditionalSuppressMessage("SingleFile", "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "Only a hint for finding dotnet: an empty location (single-file, Native AOT) gives no runtime directory "
            + "(RuntimeDirectory), and DotnetPath falls back to DOTNET_HOST_PATH, DOTNET_ROOT or dotnet on the PATH.")]
    private static string CoreLibLocation() => typeof(object).Assembly.Location;

    /// <summary>The directory of the runtime at <paramref name="coreLibLocation"/>; null when the location is unknown (empty).</summary>
    internal static string? RuntimeDirectory(string? coreLibLocation) =>
        string.IsNullOrEmpty(coreLibLocation) ? null : Path.GetDirectoryName(coreLibLocation);

    internal static string DotnetPath() => DotnetPath(Environment.ProcessPath, RuntimeDirectory(CoreLibLocation()));

    /// <param name="processPath">This process's executable.</param>
    /// <param name="runtimeDir">Directory of the runtime running us (System.Private.CoreLib).</param>
    internal static string DotnetPath(string? processPath, string? runtimeDir)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe)))
            return Path.Combine(root, exe);
        // The runtime running us: <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet
        if (runtimeDir is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", exe));
            if (File.Exists(candidate)) return candidate;
        }
        return exe;
    }
}

/// <summary>The started Labcoat process (a seam for tests: see <see cref="LabcoatLauncher.StartProcess"/>).</summary>
internal interface ILabcoatProcess : IDisposable
{
    Task WaitForExitAsync(CancellationToken cancellationToken);
    bool HasExited { get; }
    int ExitCode { get; }
    /// <summary>Kills the process and its children.</summary>
    void Kill();
}

internal sealed class OsProcess(Process process) : ILabcoatProcess
{
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public void Kill() => process.Kill(entireProcessTree: true);
    public void Dispose() => process.Dispose();
}
