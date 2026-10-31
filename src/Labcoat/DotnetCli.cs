using System.Diagnostics;
using System.Text;

namespace Labcoat;

/// <summary>
/// The <c>dotnet</c> child processes of the explorer (build, MSBuild evaluation, the <c>--help</c> coverage probe) and
/// where the dotnet executable is.
/// </summary>
internal static class DotnetCli
{
    /// <summary>The dotnet executable: the host's own (DOTNET_HOST_PATH, this process), DOTNET_ROOT, else the PATH.</summary>
    public static string Executable { get; } = Resolve();

    private static string Resolve()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath)) return hostPath;
        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    /// <summary>
    /// Runs dotnet with English output, no logo and no terminal logger; the output is stdout and stderr interleaved.
    /// Cancelled = the process tree is killed, exit code -1.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string[] args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDTERMINALLOGGER"] = "off";

        var sb = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort: the process may have exited already */ }
            return (-1, "cancelled");
        }
        await proc.WaitForExitAsync(CancellationToken.None); // flush async output (the redirected streams' last lines)
        lock (sb) return (proc.ExitCode, sb.ToString());
    }

    /// <summary>The last <paramref name="lines"/> lines of a process output (a build error's tail).</summary>
    public static string TailLines(string s, int lines)
    {
        var all = s.TrimEnd().Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines))).TrimEnd();
    }
}
