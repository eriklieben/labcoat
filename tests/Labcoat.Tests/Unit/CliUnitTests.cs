using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The startup banner, opening the browser and the default workspace. These read / write process-wide state
/// (Console.Out, environment variables), so the class runs on its own and restores everything it changes.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CliUnitTests
{
    public TestContext TestContext { get; set; }

    private static readonly string[] EnvNames =
        ["CI", "BROWSER", "DISPLAY", "WAYLAND_DISPLAY", "WSL_DISTRO_NAME", "SSH_CONNECTION", "PATH", "HOME"];

    private readonly Dictionary<string, string?> _savedEnv = [];
    private TextWriter _savedOut = null!;
    private StringWriter _out = null!;
    private DirectoryInfo _dir = null!;

    [TestInitialize]
    public void Init()
    {
        foreach (var n in EnvNames) _savedEnv[n] = Environment.GetEnvironmentVariable(n);
        _savedOut = Console.Out;
        _out = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(_out));
        _dir = Directory.CreateTempSubdirectory("labcoat-cli-unit-");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Console.SetOut(_savedOut);
        foreach (var (n, v) in _savedEnv) Environment.SetEnvironmentVariable(n, v);
        _dir.Delete(recursive: true);
    }

    private string Output
    {
        get { lock (_out) return _out.ToString(); }
    }

    private string WaitForOutput(string text)
    {
        for (var i = 0; i < 100 && !Output.Contains(text); i++) Thread.Sleep(50);
        return Output;
    }

    // ------------------------------------------------------------------ banner

    [TestMethod]
    public void Banner_lists_url_workspace_settings_and_projects_relative_to_the_workspace()
    {
        var ws = _dir.FullName;
        var projects = Enumerable.Range(1, 15).Select(i => Path.Combine(ws, $"P{i:00}", $"P{i:00}.csproj")).ToList();
        Cli.PrintBanner("http://localhost:4300", [Path.Combine(ws, "a"), Path.Combine(ws, "b")], projects,
            "Port 4300 is in use, using 4301 instead.", "https://localhost:17000", ["~/.labcoat.json", ".labcoat.json"]);
        var o = Output;
        Assert.Contains("v" + Cli.Version, o);
        Assert.Contains("http://localhost:4300", o);
        Assert.Contains("Port 4300 is in use, using 4301 instead.", o);
        Assert.Contains("https://localhost:17000", o);
        Assert.Contains(ws, o);                                  // two inputs: their common root
        Assert.Contains("~/.labcoat.json, .labcoat.json", o);
        Assert.Contains("15 test projects", o);
        Assert.Contains(Path.Combine("P01", "P01.csproj"), o);   // relative to the workspace
        Assert.Contains(Path.Combine("P12", "P12.csproj"), o);
        Assert.DoesNotContain(Path.Combine("P13", "P13.csproj"), o); // only the first 12
        Assert.Contains("... and 3 more", o);
        Assert.Contains("Press Ctrl+C to stop.", o);
    }

    [TestMethod]
    public void Banner_without_projects_explains_what_was_looked_for()
    {
        Cli.PrintBanner("http://localhost:4301", [_dir.FullName], [], null, null);
        var o = Output;
        Assert.Contains("no test projects found", o);
        Assert.Contains("Looked for MSTest", o);
        Assert.DoesNotContain("Aspire:", o);
        Assert.DoesNotContain("Settings:", o);

        _out.GetStringBuilder().Clear();
        Cli.PrintBanner("http://localhost:4301", [Path.Combine(_dir.FullName, "One.csproj")], [Path.Combine(_dir.FullName, "One.csproj")], null, null);
        Assert.Contains("1 test project", Output);
    }

    [TestMethod]
    public void Usage_and_colors()
    {
        Assert.Contains("Labcoat " + Cli.Version, Cli.Usage);
        Assert.Contains($"Default {Cli.DefaultPort};", Cli.Usage);
        Assert.Contains(Cli.DefaultDataDir, Cli.Usage);
        Assert.Contains("LABCOAT_UI_SETTINGS", Cli.Usage);
        foreach (var f in new Func<string, string>[] { Cli.Bold, Cli.Dim, Cli.Gray, Cli.Cyan, Cli.Yellow, Cli.Red, Cli.Badge })
        {
            var s = f("text");
            Assert.IsTrue(Cli.UseColor ? s.StartsWith("\x1b[") && s.EndsWith("text\x1b[0m") : s == "text", s);
        }
        var savedErr = Console.Error;
        var err = new StringWriter();
        Console.SetError(err);
        try { Cli.Warn("careful"); }
        finally { Console.SetError(savedErr); }
        Assert.Contains("careful", err.ToString());
    }

    // ------------------------------------------------------------------ browser

    private string Recorder(string name, string body = "printf '%s\\n' \"$@\" > \"$0.args\"")
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string[] RecordedArgs(string script)
    {
        for (var i = 0; i < 100 && !File.Exists(script + ".args"); i++) Thread.Sleep(50);
        Thread.Sleep(50);
        return File.ReadAllLines(script + ".args");
    }

    private static void Display(bool on)
    {
        Environment.SetEnvironmentVariable("DISPLAY", on ? ":0" : null);
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        Environment.SetEnvironmentVariable("WSL_DISTRO_NAME", null);
        Environment.SetEnvironmentVariable("SSH_CONNECTION", null);
        Environment.SetEnvironmentVariable("CI", null);
        Environment.SetEnvironmentVariable("BROWSER", null);
    }

    [TestMethod]
    public async Task Nothing_is_opened_on_ci()
    {
        Environment.SetEnvironmentVariable("CI", "true");
        var browser = Recorder("browser");
        Environment.SetEnvironmentVariable("BROWSER", browser);
        Cli.OpenBrowser("http://localhost:4300");
        // Give a (wrongly) started browser time to record its arguments.
        await Task.Delay(200, TestContext.CancellationToken);
        Assert.IsFalse(File.Exists(browser + ".args"));
        Assert.DoesNotContain("Could not open a browser", Output);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void Browser_variable_supports_a_url_placeholder_and_a_command_list()
    {
        Display(on: false); // $BROWSER works without a display too
        var browser = Recorder("browser");
        Environment.SetEnvironmentVariable("BROWSER", $"{browser} --new-tab %s --private : other-browser");
        Cli.OpenBrowser("http://localhost:4300");
        CollectionAssert.AreEqual(new[] { "--new-tab", "http://localhost:4300", "--private" }, RecordedArgs(browser));

        File.Delete(browser + ".args");
        Environment.SetEnvironmentVariable("BROWSER", browser);
        Cli.OpenBrowser("http://localhost:4301");
        CollectionAssert.AreEqual(new[] { "http://localhost:4301" }, RecordedArgs(browser));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void A_browser_that_fails_prints_the_url_instead()
    {
        Display(on: true);
        Environment.SetEnvironmentVariable("BROWSER", Recorder("broken", "exit 4"));
        Cli.OpenBrowser("http://localhost:4300");
        var o = WaitForOutput("exited with code 4");
        Assert.Contains("Could not open a browser (broken exited with code 4)", o);
        Assert.Contains("http://localhost:4300", o);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void Headless_sessions_print_the_url_with_an_ssh_hint()
    {
        Display(on: false);
        Cli.OpenBrowser("http://localhost:4300");
        Assert.Contains("Could not open a browser (no display found)", Output);

        _out.GetStringBuilder().Clear();
        Environment.SetEnvironmentVariable("SSH_CONNECTION", "10.0.0.1 5000 10.0.0.2 22");
        Cli.OpenBrowser("http://localhost:4302");
        Assert.Contains("SSH session without a display", Output);
        Assert.Contains("ssh -L 4302:localhost:4302 <host>", Output);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void Xdg_open_is_used_when_there_is_a_display()
    {
        Display(on: true);
        Environment.SetEnvironmentVariable("PATH", _dir.FullName); // no setsid here: xdg-open is started directly
        Cli.OpenBrowser("http://localhost:4300");
        Assert.Contains("Could not open a browser (xdg-open not found)", Output);

        var xdg = Recorder("xdg-open");
        Cli.OpenBrowser("http://localhost:4303");
        CollectionAssert.AreEqual(new[] { "http://localhost:4303" }, RecordedArgs(xdg));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void A_browser_command_that_cannot_start_is_reported()
    {
        Display(on: true);
        Environment.SetEnvironmentVariable("PATH", _dir.FullName); // no setsid: Process.Start itself fails
        Environment.SetEnvironmentVariable("BROWSER", Path.Combine(_dir.FullName, "does-not-exist"));
        Cli.OpenBrowser("http://localhost:4300");
        Assert.Contains("Could not open a browser (", Output);
        Assert.Contains("http://localhost:4300", Output);
    }

    // ------------------------------------------------------------------ default workspace

    [TestMethod]
    public void Default_path_walk_stops_at_the_home_directory()
    {
        var home = Directory.CreateDirectory(Path.Combine(_dir.FullName, "home")).FullName;
        File.WriteAllText(Path.Combine(_dir.FullName, "AboveHome.slnx"), "<Solution />");
        var cwd = Directory.CreateDirectory(Path.Combine(home, "scratch")).FullName;
        File.WriteAllText(Path.Combine(cwd, "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
        Environment.SetEnvironmentVariable("HOME", home);
        if (OperatingSystem.IsWindows()) return; // UserProfile does not come from HOME there
        Assert.AreEqual(cwd, Cli.ResolveDefaultPath(cwd));
        Assert.AreEqual(Path.Combine(_dir.FullName, "missing"), Cli.ResolveDefaultPath(Path.Combine(_dir.FullName, "missing")));
    }
}
