using Labcoat;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// Two real MSTest projects built in one build and discovered / run in parallel: the tree keeps the project order, the
/// projects' TestingPlatformCommandLineArguments reach the test host (or are skipped), a hanging test shows up in
/// runningNow with its elapsed time and "slow", and both the per-test timeout and the session --timeout cancel it.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("e2e")]
public class ParallelEndToEndTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly object s_gate = new();
    private static Task<string>? s_root;

    private static string Project(string root, string name) => Path.Combine(root, name, name + ".csproj");

    /// <summary>Fast.Tests (two tests; a third is filtered out by its TestingPlatformCommandLineArguments) and Hang.Tests.</summary>
    private static Task<string> WorkspaceAsync()
    {
        lock (s_gate) return s_root ??= Task.Run(CreateAsync);
    }

    private static async Task<string> CreateAsync()
    {
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        var root = ScratchWorkspace.CreateRealTempDirectory("labcoat-parallel-e2e-");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, recursive: true); } catch { } };
        static string Csproj(string args) => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <OutputType>Exe</OutputType>
                <EnableMSTestRunner>true</EnableMSTestRunner>
                <TestingPlatformCommandLineArguments>{args}</TestingPlatformCommandLineArguments>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="MSTest" Version="4.0.2" /></ItemGroup>
              <ItemGroup><Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" /></ItemGroup>
            </Project>
            """;
        void Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("Fast.Tests/Fast.Tests.csproj", Csproj("--filter \"TestCategory!=Excluded\" --minimum-expected-tests 50"));
        Write("Fast.Tests/FastTests.cs", """
            namespace Fast;
            [TestClass]
            public class FastTests
            {
                [TestMethod] public void One() { }
                [TestMethod] public void Two() { }
                [TestMethod, TestCategory("Excluded")] public void Excluded() => Assert.Fail("filtered out");
            }
            """);
        Write("Hang.Tests/Hang.Tests.csproj", Csproj("--timeout 6s"));
        Write("Hang.Tests/HangTests.cs", """
            namespace Hang;
            [TestClass]
            public class HangTests
            {
                [TestMethod] public void Quick() { }
                [TestMethod] public void Hangs() => Thread.Sleep(Timeout.Infinite);
            }
            """);
        Write("ws.slnx", """<Solution><Project Path="Fast.Tests/Fast.Tests.csproj" /><Project Path="Hang.Tests/Hang.Tests.csproj" /></Solution>""");
        var (code, output) = await ScratchWorkspace.DotnetAsync(root, "build", Path.Combine(root, "ws.slnx"), "-nologo", "-v:q", "-tl:off");
        if (code != 0) throw new InvalidOperationException("Building the parallel workspace failed:\n" + output);
        return root;
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Projects_run_in_parallel_and_a_hanging_test_is_shown_then_timed_out()
    {
        var root = await WorkspaceAsync();
        var hub = new EventHub();
        await using var explorer = new Labcoat.Explorer(hub, [Project(root, "Fast.Tests"), Project(root, "Hang.Tests")], null, [root])
        {
            MaxParallelProjects = 2,
            HangThreshold = TimeSpan.FromSeconds(1),
            TestTimeout = TimeSpan.FromSeconds(3),
            HangCheckInterval = TimeSpan.FromMilliseconds(200),
        };
        await using var events = new EventRecorder(hub, explorer);
        await explorer.DiscoverAsync(null);
        var logs = ScratchWorkspace.Logs(explorer);
        Assert.Contains("Building 2 projects in one build: Fast.Tests, Hang.Tests", logs);
        // the filter of Fast.Tests' TestingPlatformCommandLineArguments reached its test host; the tree keeps the project order
        CollectionAssert.AreEqual(new[] { "One", "Two", "Quick", "Hangs" }, explorer.Snapshot().Tests.Select(t => t.DisplayName).ToList(), logs);
        Assert.Contains("TestingPlatformCommandLineArguments: passed to the test host: --filter TestCategory!=Excluded", logs);
        Assert.Contains("--minimum-expected-tests 50 (Labcoat often runs a selection of the tests)", logs);
        Assert.Contains("--timeout 6s: applied by Labcoat to each run of the project (6 s)", logs);
        Assert.AreEqual((2, 1_000d, 3_000d), (explorer.Snapshot().Limits!.MaxParallelProjects, explorer.Snapshot().Limits!.HangThresholdMs, explorer.Snapshot().Limits!.TestTimeoutMs));

        var run = explorer.RunAsync(null, null);
        await ScratchWorkspace.WaitUntilAsync(() => explorer.Snapshot().RunningNow?.Tests.Any(t => t.DisplayName == "Hangs" && t.Slow == true) == true, "Hangs shown as slow");
        await run;
        logs = ScratchWorkspace.Logs(explorer);
        var hangs = explorer.Snapshot().Tests.Single(t => t.DisplayName == "Hangs");
        Assert.AreEqual(Outcome.TimedOut, hangs.Outcome, logs);
        Assert.AreEqual("Timeout", hangs.ErrorType);
        StringAssert.Contains(hangs.ErrorMessage, "the test ran longer than the test timeout (3 s, testTimeout)");
        Assert.IsGreaterThanOrEqualTo(3000, hangs.DurationMs!.Value);
        Assert.AreEqual(Outcome.Passed, explorer.Snapshot().Tests.Single(t => t.DisplayName == "Quick").Outcome);
        Assert.IsTrue(explorer.Snapshot().Tests.Where(t => t.ProjectId == "Fast.Tests").All(t => t.Outcome == Outcome.Passed), logs);
        var last = explorer.Summary().LastRun!;
        Assert.IsTrue(last.Aborted);
        StringAssert.Contains(last.AbortReason, "Hangs timed out after");
        Assert.AreEqual((3, 1), (last.Passed, last.Failed));
        Assert.Contains("has been running for", logs); // "possibly hanging" warning
        // the running-now list is cleared after the run; the killed host can take a moment to be reaped (Windows)
        for (var i = 0; i < 150 && (explorer.Snapshot().RunningNow is not null || events.Of("runningNow")[^1]["runningNow"] is not null); i++)
            await Task.Delay(100, TestContext.CancellationToken);
        Assert.IsNull(explorer.Snapshot().RunningNow);

        // the runningNow events: Hangs listed with its elapsed time, marked slow, and cleared at the end of the run
        var running = events.Of("runningNow");
        Assert.IsTrue(running.Any(e => e["runningNow"]?["tests"]?.AsArray().Any(t => (string?)t!["displayName"] == "Hangs" && (bool?)t["slow"] == true) == true));
        Assert.IsNull(running[^1]["runningNow"]);
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task The_session_timeout_of_TestingPlatformCommandLineArguments_cancels_the_projects_run()
    {
        var root = await WorkspaceAsync();
        await using var explorer = new Labcoat.Explorer(new EventHub(), [Project(root, "Hang.Tests")], null, [root])
        {
            HangThreshold = TimeSpan.Zero,
            HangCheckInterval = TimeSpan.FromMilliseconds(200),
        };
        explorer.DotnetOverride = ExplorerEndToEndTests.SkipBuild;
        await explorer.DiscoverAsync(null);
        await explorer.RunAsync(null, null);
        var logs = ScratchWorkspace.Logs(explorer);
        var hangs = explorer.Snapshot().Tests.Single(t => t.DisplayName == "Hangs");
        Assert.AreEqual(Outcome.TimedOut, hangs.Outcome, logs);
        StringAssert.Contains(hangs.ErrorMessage, "took longer than its --timeout 6s");
        var last = explorer.Summary().LastRun!;
        StringAssert.Contains(last.AbortReason, "exceeded its --timeout 6s (6 s) and was cancelled while 1 test ran.");
        Assert.AreEqual(Outcome.Passed, explorer.Snapshot().Tests.Single(t => t.DisplayName == "Quick").Outcome);
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Cancel_reaches_every_project_that_runs()
    {
        var root = await WorkspaceAsync();
        await using var explorer = new Labcoat.Explorer(new EventHub(), [Project(root, "Fast.Tests"), Project(root, "Hang.Tests")], null, [root])
        {
            MaxParallelProjects = 2,
            DotnetOverride = ExplorerEndToEndTests.SkipBuild,
        };
        await explorer.DiscoverAsync(null);
        var r = explorer.RequestRun(null, null);
        await ScratchWorkspace.WaitUntilAsync(() => explorer.Snapshot().Tests.Any(t => t.DisplayName == "Hangs" && t.Outcome == Outcome.Running), "Hangs running");
        Assert.AreEqual(1, explorer.Snapshot().RunningNow!.Count);
        explorer.Cancel();
        await r.Completion!;
        var logs = ScratchWorkspace.Logs(explorer);
        Assert.AreEqual(Outcome.Cancelled, explorer.Snapshot().Tests.Single(t => t.DisplayName == "Hangs").Outcome, logs);
        Assert.IsTrue(explorer.Summary().LastRun!.Cancelled);
        Assert.AreEqual("cancelled", explorer.GetRunStatus(r.RunId!)!.State);
        Assert.IsFalse(explorer.Snapshot().Tests.Any(t => t.Outcome is Outcome.Queued or Outcome.Running), logs);
    }
}
