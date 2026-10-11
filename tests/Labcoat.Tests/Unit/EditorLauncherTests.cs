using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// "Open in editor": fake editor executables (shell scripts that record their arguments) stand in for VS Code / Rider,
/// so the real process start runs. The detected paths are process-wide state: every test is [DoNotParallelize] and
/// resets detection afterwards.
/// </summary>
[TestClass]
[DoNotParallelize]
public class EditorLauncherTests
{
    private DirectoryInfo _dir = null!;

    [TestInitialize]
    public void Init() => _dir = Directory.CreateTempSubdirectory("labcoat-editor-");

    [TestCleanup]
    public void Cleanup()
    {
        EditorLauncher.SetDetectedPaths(null);
        _dir.Delete(recursive: true);
    }

    private string Script(string name, string body, bool executable = true)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    /// <summary>A fake editor that writes its arguments (one per line) to &lt;script&gt;.args.</summary>
    private string Recorder(string name) => Script(name, "printf '%s\\n' \"$@\" > \"$0.args\"");

    private static string[] RecordedArgs(string script)
    {
        var file = script + ".args";
        for (var i = 0; i < 100 && !File.Exists(file); i++) Thread.Sleep(50);
        return File.ReadAllLines(file);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public void Vscode_gets_goto_with_line_and_column()
    {
        var code = Recorder("code");
        EditorLauncher.SetDetectedPaths(new Dictionary<string, string?> { ["vscode"] = code });

        var (ok, message) = EditorLauncher.Open("vscode", "/src/A.cs", 12, 3);
        Assert.IsTrue(ok, message);
        CollectionAssert.AreEqual(new[] { "--goto", "/src/A.cs:12:3" }, RecordedArgs(code));

        File.Delete(code + ".args");
        Assert.IsTrue(EditorLauncher.Open("vscode", "/src/A.cs", 7, null).Ok);
        CollectionAssert.AreEqual(new[] { "--goto", "/src/A.cs:7" }, RecordedArgs(code));

        File.Delete(code + ".args");
        Assert.IsTrue(EditorLauncher.Open("vscode", "/src/A.cs", null, 5).Ok);
        CollectionAssert.AreEqual(new[] { "--goto", "/src/A.cs" }, RecordedArgs(code));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public void Rider_gets_line_and_column_options_before_the_file()
    {
        var rider = Recorder("rider");
        EditorLauncher.SetDetectedPaths(new Dictionary<string, string?> { ["rider"] = rider });

        Assert.IsTrue(EditorLauncher.Open("rider", "/src/B.cs", 12, 3).Ok);
        CollectionAssert.AreEqual(new[] { "--line", "12", "--column", "3", "/src/B.cs" }, RecordedArgs(rider));

        File.Delete(rider + ".args");
        Assert.IsTrue(EditorLauncher.Open("rider", "/src/B.cs", null, null).Ok);
        CollectionAssert.AreEqual(new[] { "/src/B.cs" }, RecordedArgs(rider));

        var info = EditorLauncher.Detect();
        CollectionAssert.AreEqual(EditorLauncher.Ids, info.Select(e => e.Id).ToArray());
        Assert.IsTrue(info.Single(e => e.Id == "rider").Available);
        Assert.IsFalse(info.Single(e => e.Id == "vscode").Available);
        Assert.AreEqual("JetBrains Rider", info.Single(e => e.Id == "rider").Name);
        Assert.AreEqual(rider, EditorLauncher.PathOf("rider"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public void A_failing_editor_reports_its_exit_code_and_stderr()
    {
        var code = Script("code", "echo 'cannot open display' >&2\nexit 3");
        EditorLauncher.SetDetectedPaths(new Dictionary<string, string?> { ["vscode"] = code });

        var (ok, message) = EditorLauncher.Open("vscode", "/src/A.cs", 1, 1);
        Assert.IsFalse(ok);
        Assert.AreEqual("code exited with code 3: cannot open display", message);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public void An_editor_that_cannot_be_started_is_an_error_not_an_exception()
    {
        var code = Script("code", "exit 0", executable: false);
        EditorLauncher.SetDetectedPaths(new Dictionary<string, string?> { ["vscode"] = code });

        var (ok, message) = EditorLauncher.Open("vscode", "/src/A.cs", 1, null);
        Assert.IsFalse(ok);
        // Linux starts editors through setsid (which then fails with "permission denied"); elsewhere Process.Start throws.
        Assert.IsTrue(message!.StartsWith("Could not start code") || message.StartsWith("code exited with code"), message);
    }

    [TestMethod]
    public void Missing_editors_and_visual_studio_off_windows_are_refused()
    {
        EditorLauncher.SetDetectedPaths(new Dictionary<string, string?>());
        Assert.AreEqual((false, "Visual Studio Code was not found on this machine"), EditorLauncher.Open("vscode", "/a.cs", 1, 1));
        Assert.AreEqual((false, "JetBrains Rider was not found on this machine"), EditorLauncher.Open("rider", "/a.cs", 1, 1));
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual((false, "Visual Studio is only available on Windows"), EditorLauncher.Open("visualstudio", "/a.cs", 1, 1));
        else
            Assert.AreEqual((false, "Visual Studio was not found on this machine"), EditorLauncher.Open("visualstudio", "/a.cs", 1, 1));
        Assert.IsTrue(EditorLauncher.Detect().All(e => !e.Available));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public void A_failing_editor_without_stderr_reports_only_its_exit_code()
    {
        var rider = Script("rider", "exit 4");
        EditorLauncher.SetDetectedPaths(new Dictionary<string, string?> { ["rider"] = rider });

        Assert.AreEqual((false, "rider exited with code 4"), EditorLauncher.Open("rider", "/src/B.cs", 3, null));
    }

    [TestMethod]
    public void An_unknown_editor_is_refused_by_its_id()
    {
        EditorLauncher.SetDetectedPaths(new Dictionary<string, string?> { ["vscode"] = "/usr/bin/code" });

        Assert.AreEqual((false, "notepad was not found on this machine"), EditorLauncher.Open("notepad", "/a.cs", 1, 1));
        Assert.IsNull(EditorLauncher.PathOf("notepad"));
    }

    [TestMethod]
    public void Detection_runs_once_until_it_is_reset()
    {
        EditorLauncher.SetDetectedPaths(new Dictionary<string, string?> { ["rider"] = "/opt/rider/bin/rider.sh", ["other"] = "/x" });

        var first = EditorLauncher.Detect();
        CollectionAssert.AreEqual(new[] { false, true, false }, first.Select(e => e.Available).ToArray());
        CollectionAssert.AreEqual(first, EditorLauncher.Detect()); // cached: no new detection
        CollectionAssert.AreEqual(new[] { "Visual Studio Code", "JetBrains Rider", "Visual Studio" }, first.Select(e => e.Name).ToArray());
        Assert.IsNull(EditorLauncher.PathOf("other")); // only the three known editors are kept
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void Detection_prefers_code_and_rider_on_path()
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(_dir.FullName, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(_dir.FullName, "second")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(_dir.FullName, "home")).FullName;
            // The Toolbox launcher script exists too: an editor on PATH wins over it.
            var scripts = Directory.CreateDirectory(Path.Combine(home, ".local", "share", "JetBrains", "Toolbox", "scripts")).FullName;
            File.WriteAllText(Path.Combine(scripts, "rider"), "");
            var codeInsiders = Executable(Path.Combine(first, "code-insiders"));
            var code = Executable(Path.Combine(second, "code"));
            var riderSh = Executable(Path.Combine(first, "rider.sh"));
            Environment.SetEnvironmentVariable("PATH", $"{first}{Path.PathSeparator}{second}");
            Environment.SetEnvironmentVariable("HOME", home);

            EditorLauncher.SetDetectedPaths(null);
            Assert.AreEqual(code, EditorLauncher.PathOf("vscode"), "code wins over code-insiders, wherever it is on PATH");
            Assert.AreEqual(riderSh, EditorLauncher.PathOf("rider"));

            var rider = Executable(Path.Combine(second, "rider"));
            EditorLauncher.SetDetectedPaths(null);
            Assert.AreEqual(rider, EditorLauncher.PathOf("rider"), "rider wins over rider.sh");

            File.Delete(code);
            EditorLauncher.SetDetectedPaths(null);
            Assert.AreEqual(codeInsiders, EditorLauncher.PathOf("vscode"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("HOME", oldHome);
        }
    }

    /// <summary>An empty file with the user's execute bit (enough for detection on PATH).</summary>
    private static string Executable(string path)
    {
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void Detection_finds_executables_on_path_and_rider_in_the_toolbox()
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            var notExecutable = Directory.CreateDirectory(Path.Combine(_dir.FullName, "a")).FullName;
            var bin = Directory.CreateDirectory(Path.Combine(_dir.FullName, "b")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(_dir.FullName, "home")).FullName;
            File.WriteAllText(Path.Combine(notExecutable, "code"), "");                      // no exec bit: skipped
            var codeInsiders = Path.Combine(bin, "code-insiders");
            File.WriteAllText(codeInsiders, "#!/bin/sh\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(codeInsiders, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var toolbox = Directory.CreateDirectory(Path.Combine(home, ".local", "share", "JetBrains", "Toolbox", "apps", "Rider", "ch-0", "251.1", "bin")).FullName;
            var riderSh = Path.Combine(toolbox, "rider.sh");
            File.WriteAllText(riderSh, "");
            Environment.SetEnvironmentVariable("PATH", $"{notExecutable}{Path.PathSeparator}{bin}{Path.PathSeparator}{Path.Combine(_dir.FullName, "missing")}");
            Environment.SetEnvironmentVariable("HOME", home);

            EditorLauncher.SetDetectedPaths(null);
            Assert.AreEqual(codeInsiders, EditorLauncher.PathOf("vscode"));
            Assert.AreEqual(riderSh, EditorLauncher.PathOf("rider"));
            Assert.IsNull(EditorLauncher.PathOf("visualstudio"));

            // The Toolbox launcher script wins over the app folders.
            var scripts = Directory.CreateDirectory(Path.Combine(home, ".local", "share", "JetBrains", "Toolbox", "scripts")).FullName;
            File.WriteAllText(Path.Combine(scripts, "rider"), "");
            EditorLauncher.SetDetectedPaths(null);
            Assert.AreEqual(Path.Combine(scripts, "rider"), EditorLauncher.PathOf("rider"));

            // Nothing on PATH at all.
            Environment.SetEnvironmentVariable("PATH", "");
            Directory.Delete(Path.Combine(home, ".local"), recursive: true);
            EditorLauncher.SetDetectedPaths(null);
            Assert.IsNull(EditorLauncher.PathOf("vscode"));
            Assert.IsNull(EditorLauncher.PathOf("rider"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("HOME", oldHome);
        }
    }
}
