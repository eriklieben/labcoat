using System.Diagnostics;

namespace Labcoat;

/// <summary>
/// "Open in editor" (POST /api/open-in-editor) for exactly three editors: Visual Studio Code (default), JetBrains Rider
/// and Visual Studio. Executables are located once at startup (ExplorerState.editors) and launched directly with an
/// argument list — never through a shell — so the path can't inject anything.
/// <list type="bullet">
/// <item>VS Code: <c>code --goto &lt;path&gt;:&lt;line&gt;[:&lt;col&gt;]</c> (<c>code</c> / <c>code-insiders</c> on PATH, the macOS
/// app bundle CLI, or Code.exe on Windows).</item>
/// <item>Rider: <c>rider --line &lt;n&gt; [--column &lt;c&gt;] &lt;path&gt;</c> (<c>rider</c> / <c>rider.sh</c> on PATH, JetBrains
/// Toolbox scripts / app folders, the macOS app bundle, rider64.exe on Windows).</item>
/// <item>Visual Studio (Windows only): <c>devenv.exe /Edit &lt;path&gt;</c> found with vswhere; going to the line
/// (<c>/Command "Edit.GoTo n"</c>) is best effort.</item>
/// </list>
/// On Linux the editor is started through <c>setsid</c> (when available) so it is not part of Labcoat's process group
/// (Ctrl+C on Labcoat must not close the editor).
/// </summary>
public static class EditorLauncher
{
    // Editor ids (ExplorerState.editors, POST /api/open-in-editor).
    private const string VsCode = "vscode";
    private const string Rider = "rider";
    private const string VisualStudio = "visualstudio";

    // Install locations.
    private const string Programs = "Programs";
    private const string CodeExe = "Code.exe";
    private const string RiderExe = "rider64.exe";
    private const string RiderLauncher = "rider";
    private const string JetBrains = "JetBrains";
    private const string Toolbox = "Toolbox";

    public static readonly string[] Ids = [VsCode, Rider, VisualStudio];

    private static readonly Dictionary<string, string> Names = new()
    {
        [VsCode] = "Visual Studio Code",
        [Rider] = "JetBrains Rider",
        [VisualStudio] = "Visual Studio",
    };

    private static Dictionary<string, string?>? s_paths;

    /// <summary>Locates the editors (cached for the process lifetime).</summary>
    public static List<EditorInfo> Detect()
    {
        s_paths ??= new Dictionary<string, string?>
        {
            [VsCode] = FindVsCode(),
            [Rider] = FindRider(),
            [VisualStudio] = FindVisualStudio(),
        };
        return Ids.Select(id => new EditorInfo(id, Names[id], s_paths[id] is not null)).ToList();
    }

    public static string? PathOf(string id) { Detect(); return s_paths!.GetValueOrDefault(id); }

    /// <summary>Tests only: use these editor executables instead of detecting them (null: detect again on next use).</summary>
    internal static void SetDetectedPaths(IReadOnlyDictionary<string, string?>? paths) =>
        s_paths = paths is null ? null : Ids.ToDictionary(id => id, id => paths.GetValueOrDefault(id));

    public static (bool Ok, string? Message) Open(string editor, string file, int? line, int? column)
    {
        if (editor == VisualStudio && !OperatingSystem.IsWindows())
            return (false, "Visual Studio is only available on Windows");
        var exe = PathOf(editor);
        if (exe is null) return (false, $"{Names.GetValueOrDefault(editor, editor)} was not found on this machine");

        try
        {
            return Launch(exe, Arguments(editor, file, line, column));
        }
        catch (Exception ex)
        {
            return (false, $"Could not start {Path.GetFileName(exe)}: {ex.Message}");
        }
    }

    /// <summary>The editor's command line for opening <paramref name="file"/> at the line (and column).</summary>
    private static List<string> Arguments(string editor, string file, int? line, int? column)
    {
        var args = new List<string>();
        switch (editor)
        {
            case VsCode:
                args.Add("--goto");
                args.Add(GotoTarget(file, line, column));
                break;
            case Rider:
                if (line is { } rl) { args.Add("--line"); args.Add(rl.ToString()); }
                if (column is { } rc) { args.Add("--column"); args.Add(rc.ToString()); }
                args.Add(file);
                break;
            case VisualStudio:
                args.Add("/Edit");
                args.Add(file);
                if (line is { } vl) { args.Add("/Command"); args.Add($"Edit.GoTo {vl}"); }
                break;
        }
        return args;
    }

    /// <summary>VS Code's <c>--goto</c> argument: <c>path[:line[:column]]</c>.</summary>
    private static string GotoTarget(string file, int? line, int? column)
    {
        if (line is not { } l) return file;
        return column is { } c ? $"{file}:{l}:{c}" : $"{file}:{l}";
    }

    /// <summary>Starts the editor; a CLI that exits with an error within 3 s is reported with its stderr.</summary>
    private static (bool Ok, string? Message) Launch(string exe, List<string> args)
    {
        var psi = Launcher(exe, args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        var proc = new Process { StartInfo = psi };
        var err = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (err) err.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close();
        // CLIs (code, setsid) hand off and exit quickly; a launcher that keeps running is fine too.
        if (proc.WaitForExit(TimeSpan.FromSeconds(3)) && proc.ExitCode != 0)
        {
            proc.WaitForExit(); // flush the async stderr reader
            string text;
            lock (err) text = err.ToString().Trim();
            var exitCode = proc.ExitCode;
            proc.Dispose();
            return (false, $"{Path.GetFileName(exe)} exited with code {exitCode}" + (text.Length > 0 ? ": " + text : ""));
        }
        _ = Task.Run(async () => { try { await proc.WaitForExitAsync(); } catch { /* only waiting to dispose it */ } finally { proc.Dispose(); } });
        return (true, null);
    }

    private static ProcessStartInfo Launcher(string exe, List<string> args)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/setsid"))
        {
            psi = new ProcessStartInfo("/usr/bin/setsid");
            psi.ArgumentList.Add(exe);
        }
        else psi = new ProcessStartInfo(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // ------------------------------------------------------------------ detection

    private static string? FindVsCode()
    {
        if (OperatingSystem.IsWindows()) return FindVsCodeOnWindows();
        if ((OnPath("code") ?? OnPath("code-insiders")) is { } p) return p;
        return OperatingSystem.IsMacOS() ? FindVsCodeOnMac() : null;
    }

    private static string? FindVsCodeOnWindows()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(local, Programs, "Microsoft VS Code", CodeExe),
            Path.Combine(pf, "Microsoft VS Code", CodeExe),
            Path.Combine(local, Programs, "Microsoft VS Code Insiders", "Code - Insiders.exe"),
        ];
        if (candidates.FirstOrDefault(File.Exists) is { } installed) return installed;
        // code.cmd on PATH (…\bin\code.cmd) -> …\Code.exe; batch files are never started directly.
        if (OnPath("code.cmd") is { } cmd && Path.GetDirectoryName(Path.GetDirectoryName(cmd)) is { } root
            && File.Exists(Path.Combine(root, CodeExe)))
            return Path.Combine(root, CodeExe);
        return null;
    }

    private static string? FindVsCodeOnMac()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clis = from app in new[] { "/Applications", Path.Combine(home, "Applications") }
                   from name in new[] { "Visual Studio Code.app", "Visual Studio Code - Insiders.app" }
                   select Path.Combine(app, name, "Contents", "Resources", "app", "bin", name.Contains("Insiders") ? "code-insiders" : "code");
        return clis.FirstOrDefault(File.Exists);
    }

    private static string? FindRider()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string> { Path.Combine(local, Programs, "Rider", "bin", RiderExe) };
            foreach (var root in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), JetBrains), Path.Combine(local, Programs) })
            {
                try
                {
                    if (Directory.Exists(root))
                        candidates.AddRange(Directory.EnumerateDirectories(root, "*Rider*").OrderDescending().Select(d => Path.Combine(d, "bin", RiderExe)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: no candidates from it */ }
            }
            // Toolbox 1.x app channels: …\JetBrains\Toolbox\apps\Rider\ch-0\<build>\bin\rider64.exe
            candidates.AddRange(FindUnder(Path.Combine(local, JetBrains, Toolbox, "apps", "Rider"), RiderExe));
            return candidates.FirstOrDefault(File.Exists) ?? OnPath(RiderExe);
        }
        if ((OnPath(RiderLauncher) ?? OnPath("rider.sh")) is { } p) return p;
        var unix = new List<string>();
        if (OperatingSystem.IsMacOS())
        {
            unix.Add(Path.Combine(home, "Library", "Application Support", JetBrains, Toolbox, "scripts", RiderLauncher));
            unix.Add("/Applications/Rider.app/Contents/MacOS/rider");
            unix.Add(Path.Combine(home, "Applications", "Rider.app", "Contents", "MacOS", RiderLauncher));
        }
        else
        {
            unix.Add(Path.Combine(home, ".local", "share", JetBrains, Toolbox, "scripts", RiderLauncher));
            unix.Add(Path.Combine(home, ".local", "share", JetBrains, Toolbox, "apps", RiderLauncher, "bin", "rider.sh"));
            unix.Add("/snap/bin/rider");
            unix.Add("/opt/rider/bin/rider.sh");
            unix.Add("/opt/JetBrains Rider/bin/rider.sh");
            unix.AddRange(FindUnder(Path.Combine(home, ".local", "share", JetBrains, Toolbox, "apps", "Rider"), "rider.sh"));
        }
        return unix.FirstOrDefault(File.Exists);
    }

    private static string? FindVisualStudio()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            var psi = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-latest", "-property", "productPath" }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            var path = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith("devenv.exe", StringComparison.OrdinalIgnoreCase));
            return path is not null && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static List<string> FindUnder(string root, string fileName)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateFiles(root, fileName, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .OrderDescending().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? OnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (!File.Exists(candidate)) continue;
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                    continue;
                return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* a bad PATH entry: try the next */ }
        }
        return null;
    }
}
