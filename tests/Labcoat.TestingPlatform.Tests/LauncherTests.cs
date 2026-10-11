using System.Diagnostics;

namespace Labcoat.TestingPlatform.Tests;

/// <summary>
/// The --labcoat launcher: when it runs, which Labcoat it starts and with what, and how the test app ends.
/// Changes process-wide state (environment variables, AppContext data, the launcher's seams): not parallelized.
/// </summary>
[TestClass]
[DoNotParallelize]
public class LauncherTests
{
    public TestContext TestContext { get; set; }

    private const string ProjectPathKey = "Labcoat.TestingPlatform.ProjectPath";
    private const string LabcoatPathKey = "Labcoat.TestingPlatform.LabcoatPath";
    private static readonly string[] EnvVars = ["LABCOAT_PATH", "LABCOAT_TEST_HOST", "DOTNET_HOST_PATH", "DOTNET_ROOT"];

    private readonly Dictionary<string, string?> _savedEnv = [];
    private object? _savedProject, _savedLabcoat;
    private DirectoryInfo _dir = null!;
    private readonly List<int> _exits = [];
    private readonly List<FakeProcess> _started = [];

    [TestInitialize]
    public void Init()
    {
        foreach (var v in EnvVars) _savedEnv[v] = Environment.GetEnvironmentVariable(v);
        _savedProject = AppContext.GetData(ProjectPathKey);
        _savedLabcoat = AppContext.GetData(LabcoatPathKey);
        foreach (var v in EnvVars) Environment.SetEnvironmentVariable(v, null);
        AppContext.SetData(ProjectPathKey, null);
        AppContext.SetData(LabcoatPathKey, null);
        _dir = Directory.CreateTempSubdirectory("labcoat-launcher-");
        LabcoatLauncher.Exit = _exits.Add;
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var (k, v) in _savedEnv) Environment.SetEnvironmentVariable(k, v);
        AppContext.SetData(ProjectPathKey, _savedProject);
        AppContext.SetData(LabcoatPathKey, _savedLabcoat);
        LabcoatLauncher.Exit = Environment.Exit;
        LabcoatLauncher.StartProcess = psi => Process.Start(psi) is { } p ? new OsProcess(p) : null;
        LabcoatLauncher.StopGrace = TimeSpan.FromSeconds(35);
        _dir.Delete(recursive: true);
    }

    private string File(string name)
    {
        var path = Path.Combine(_dir.FullName, name);
        System.IO.File.WriteAllText(path, "");
        return path;
    }

    private void FakeStart(Func<FakeProcess?> create) =>
        LabcoatLauncher.StartProcess = psi =>
        {
            var p = create();
            if (p is null) return null;
            p.StartInfo = psi;
            _started.Add(p);
            return p;
        };

    // ------------------------------------------------------------------ when it runs

    [TestMethod]
    public void Launches_only_with_labcoat_outside_server_mode_and_outside_a_labcoat_test_host()
    {
        Assert.IsTrue(LabcoatLauncher.ShouldLaunch(new FakeOptions(("labcoat", [])), null));
        Assert.IsTrue(LabcoatLauncher.ShouldLaunch(new FakeOptions(("labcoat", ["watch"])), ""));
        Assert.IsFalse(LabcoatLauncher.ShouldLaunch(new FakeOptions(), null), "no --labcoat");
        Assert.IsFalse(LabcoatLauncher.ShouldLaunch(new FakeOptions(("labcoat", []), ("server", [])), null), "server mode (IDE, Labcoat)");
        Assert.IsFalse(LabcoatLauncher.ShouldLaunch(new FakeOptions(("labcoat", [])), "1"), "started by Labcoat");
    }

    [TestMethod]
    public async Task IsEnabled_reads_the_labcoat_test_host_marker()
    {
        var launcher = new LabcoatLauncher(new FakeOptions(("labcoat", [])));
        Assert.IsTrue(await launcher.IsEnabledAsync());
        Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", "1");
        Assert.IsFalse(await launcher.IsEnabledAsync());
        Assert.IsTrue(launcher.AfterRunAsync(0, CancellationToken.None).IsCompletedSuccessfully);
    }

    // ------------------------------------------------------------------ which Labcoat

    [TestMethod]
    public void Labcoat_path_dll_runs_with_dotnet()
    {
        var dll = File("Labcoat.dll");
        Environment.SetEnvironmentVariable("LABCOAT_PATH", dll);
        var psi = LabcoatLauncher.CreateStartInfo();
        Assert.AreEqual(LabcoatLauncher.DotnetPath(), psi.FileName);
        CollectionAssert.AreEqual(new[] { dll }, psi.ArgumentList.ToArray());
    }

    [TestMethod]
    public void Labcoat_path_executable_runs_directly_and_wins_over_the_bundled_runner()
    {
        var exe = File("labcoat");
        AppContext.SetData(LabcoatPathKey, File("Bundled.dll"));
        Environment.SetEnvironmentVariable("LABCOAT_PATH", exe);
        var psi = LabcoatLauncher.CreateStartInfo();
        Assert.AreEqual(exe, psi.FileName);
        Assert.IsEmpty(psi.ArgumentList);
    }

    [TestMethod]
    public void Bundled_runner_is_used_without_labcoat_path()
    {
        var bundled = File("Labcoat.dll");
        AppContext.SetData(LabcoatPathKey, bundled);
        var psi = LabcoatLauncher.CreateStartInfo();
        CollectionAssert.AreEqual(new[] { bundled }, psi.ArgumentList.ToArray());
    }

    [TestMethod]
    public void Missing_files_fall_back_to_dotnet_tool_exec_of_the_same_version()
    {
        Environment.SetEnvironmentVariable("LABCOAT_PATH", Path.Combine(_dir.FullName, "missing.dll"));
        var psi = LabcoatLauncher.CreateStartInfo();
        Assert.AreEqual(LabcoatLauncher.DotnetPath(), psi.FileName);
        var version = new LabcoatCommandLineProvider().Version;
        CollectionAssert.AreEqual(new[] { "tool", "exec", $"Labcoat@{version}", "--yes", "--" }, psi.ArgumentList.ToArray());

        Environment.SetEnvironmentVariable("LABCOAT_PATH", null);
        AppContext.SetData(LabcoatPathKey, Path.Combine(_dir.FullName, "gone", "Labcoat.dll"));
        Assert.AreEqual("tool", LabcoatLauncher.CreateStartInfo().ArgumentList[0]);
    }

    [TestMethod]
    public void Dotnet_is_found_from_host_path_process_root_or_runtime()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var host = File("host-dotnet");
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
        Assert.AreEqual(host, LabcoatLauncher.DotnetPath("/any/app", null));

        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Path.Combine(_dir.FullName, "missing"));
        var processDotnet = Path.Combine(_dir.FullName, "bin", "dotnet.exe");
        Assert.AreEqual(processDotnet, LabcoatLauncher.DotnetPath(processDotnet, null), "this process is dotnet itself");

        var root = Directory.CreateDirectory(Path.Combine(_dir.FullName, "root")).FullName;
        System.IO.File.WriteAllText(Path.Combine(root, exe), "");
        Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
        Assert.AreEqual(Path.Combine(root, exe), LabcoatLauncher.DotnetPath("/x/MyTests", null));

        Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.Combine(_dir.FullName, "empty"));
        var runtime = Directory.CreateDirectory(Path.Combine(_dir.FullName, "install", "shared", "Microsoft.NETCore.App", "10.0.0")).FullName;
        System.IO.File.WriteAllText(Path.Combine(_dir.FullName, "install", exe), "");
        Assert.AreEqual(Path.Combine(_dir.FullName, "install", exe), LabcoatLauncher.DotnetPath("/x/MyTests", runtime));

        Assert.AreEqual(exe, LabcoatLauncher.DotnetPath("/x/MyTests", Path.Combine(_dir.FullName, "a", "b", "c")), "on PATH");
        Assert.AreEqual(exe, LabcoatLauncher.DotnetPath(null, null));
        Assert.IsFalse(string.IsNullOrEmpty(LabcoatLauncher.DotnetPath()));
    }

    [TestMethod]
    public void An_unknown_runtime_location_falls_back_to_dotnet_on_the_path()
    {
        // Single-file and Native AOT apps: Assembly.Location is empty, so there is no runtime directory to look in.
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        // (Init cleared DOTNET_HOST_PATH and DOTNET_ROOT.)
        Assert.IsNull(LabcoatLauncher.RuntimeDirectory(""));
        Assert.IsNull(LabcoatLauncher.RuntimeDirectory(null));
        Assert.AreEqual(exe, LabcoatLauncher.DotnetPath("/x/MyTests", LabcoatLauncher.RuntimeDirectory("")));

        var coreLib = Path.Combine(_dir.FullName, "install", "shared", "Microsoft.NETCore.App", "10.0.0", "System.Private.CoreLib.dll");
        Assert.AreEqual(Path.GetDirectoryName(coreLib), LabcoatLauncher.RuntimeDirectory(coreLib));
    }

    // ------------------------------------------------------------------ running it

    [TestMethod]
    public async Task Starts_labcoat_for_the_project_with_the_settings_and_exits_with_its_code()
    {
        var project = File("My.Tests.csproj");
        AppContext.SetData(ProjectPathKey, project);
        var labcoat = File("labcoat");
        Environment.SetEnvironmentVariable("LABCOAT_PATH", labcoat);
        Environment.SetEnvironmentVariable("LABCOAT_TEST_HOST", "1"); // e.g. from an outer runner: not passed on
        FakeStart(() => new FakeProcess(7));

        await new LabcoatLauncher(new FakeOptions(("labcoat", ["watch", "port=5000"]))).BeforeRunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 7 }, _exits);
        var psi = _started.Single().StartInfo!;
        Assert.AreEqual(labcoat, psi.FileName);
        CollectionAssert.AreEqual(new[] { project, "--watch", "--port", "5000" }, psi.ArgumentList.ToArray());
        Assert.AreEqual(_dir.FullName, psi.WorkingDirectory);
        Assert.IsFalse(psi.UseShellExecute);
        Assert.IsFalse(psi.Environment.ContainsKey("LABCOAT_TEST_HOST"));
        Assert.IsTrue(_started[0].Disposed);
    }

    [TestMethod]
    public async Task Without_settings_only_the_project_is_passed()
    {
        var project = File("My.Tests.csproj");
        AppContext.SetData(ProjectPathKey, project);
        FakeStart(() => new FakeProcess(0));

        await new LabcoatLauncher(new FakeOptions(("labcoat", []))).BeforeRunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 0 }, _exits);
        CollectionAssert.AreEqual(new[] { "tool", "exec" }, _started[0].StartInfo!.ArgumentList.Take(2).ToArray());
        Assert.AreEqual(project, _started[0].StartInfo!.ArgumentList.Last());
    }

    [TestMethod]
    public async Task Unknown_project_path_is_an_error_and_exit_code_1()
    {
        FakeStart(() => new FakeProcess(0));
        var error = await CaptureErrorAsync(() => new LabcoatLauncher(new FakeOptions(("labcoat", []))).BeforeRunAsync(CancellationToken.None));
        Assert.Contains("Labcoat: the test project path isn't known", error);
        CollectionAssert.AreEqual(new[] { 1 }, _exits);
        Assert.IsEmpty(_started);

        AppContext.SetData(ProjectPathKey, Path.Combine(_dir.FullName, "deleted.csproj"));
        await CaptureErrorAsync(() => new LabcoatLauncher(new FakeOptions(("labcoat", []))).BeforeRunAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { 1, 1 }, _exits);
        Assert.IsEmpty(_started);
    }

    [TestMethod]
    public async Task A_process_that_cannot_be_started_is_an_error()
    {
        AppContext.SetData(ProjectPathKey, File("My.Tests.csproj"));
        FakeStart(() => null);
        var error = await CaptureErrorAsync(() => new LabcoatLauncher(new FakeOptions(("labcoat", []))).BeforeRunAsync(CancellationToken.None));
        Assert.Contains("Labcoat: could not start", error);
        CollectionAssert.AreEqual(new[] { 1 }, _exits);
    }

    [TestMethod]
    public async Task Ctrl_c_waits_for_labcoat_to_stop_and_keeps_its_exit_code()
    {
        AppContext.SetData(ProjectPathKey, File("My.Tests.csproj"));
        var process = new FakeProcess(130, exitsOnItsOwn: false);
        FakeStart(() => process);
        using var cts = new CancellationTokenSource();

        var run = new LabcoatLauncher(new FakeOptions(("labcoat", []))).BeforeRunAsync(cts.Token);
        cts.Cancel();
        await Task.Delay(50, TestContext.CancellationToken);
        Assert.IsFalse(run.IsCompleted, "waits for Labcoat to shut down its test hosts");
        process.ExitNow();
        await run;

        CollectionAssert.AreEqual(new[] { 130 }, _exits);
        Assert.IsFalse(process.Killed);
    }

    [TestMethod]
    public async Task Labcoat_that_does_not_stop_in_time_is_killed()
    {
        AppContext.SetData(ProjectPathKey, File("My.Tests.csproj"));
        LabcoatLauncher.StopGrace = TimeSpan.FromMilliseconds(50);
        var process = new FakeProcess(0, exitsOnItsOwn: false);
        FakeStart(() => process);

        await new LabcoatLauncher(new FakeOptions(("labcoat", []))).BeforeRunAsync(new CancellationToken(canceled: true));

        Assert.IsTrue(process.Killed);
        CollectionAssert.AreEqual(new[] { 1 }, _exits, "exit code unknown");
    }

    [TestMethod]
    public async Task The_default_process_starter_runs_a_real_process()
    {
        var psi = new ProcessStartInfo(LabcoatLauncher.DotnetPath()) { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("--version");
        using var process = LabcoatLauncher.StartProcess(psi)!;
        await process.WaitForExitAsync(CancellationToken.None);
        Assert.IsTrue(process.HasExited);
        Assert.AreEqual(0, process.ExitCode);
        process.Kill(); // already exited: nothing to do, no exception
    }

    private static async Task<string> CaptureErrorAsync(Func<Task> action)
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        try { await action(); }
        finally { Console.SetError(original); }
        return writer.ToString();
    }
}
