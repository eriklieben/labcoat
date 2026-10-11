using System.Diagnostics;
using Labcoat;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// The reviewer's case end to end, with real `dotnet`: an MSTest project switched back to VSTest (Microsoft.NET.Test.Sdk +
/// MSTest.TestAdapter, OutputType Exe; the test app's generated Main exits with 0 when started with --server). Fresh
/// clone (not restored) and restored: Labcoat finds it, says how to switch it first, and never builds or starts it.
/// Needs the packages in the NuGet cache / feed.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("e2e")]
public class VsTestEndToEndTests
{
    public TestContext TestContext { get; set; }

    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
            <PackageReference Include="MSTest.TestAdapter" Version="4.0.2" />
            <PackageReference Include="MSTest.TestFramework" Version="4.0.2" />
          </ItemGroup>
          <ItemGroup><Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" /></ItemGroup>
        </Project>
        """;

    private const string Expected =
        "SampleTests uses VSTest (Microsoft.NET.Test.Sdk + MSTest.TestAdapter). Labcoat runs Microsoft.Testing.Platform projects: " +
        "set <EnableMSTestRunner>true</EnableMSTestRunner> and <OutputType>Exe</OutputType> (or use MSTest.Sdk).";

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task A_vstest_project_gets_the_clear_message_first_and_no_test_host_restored_or_not()
    {
        var root = ScratchWorkspace.CreateRealTempDirectory("labcoat-vstest-e2e-");
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "SampleTests")).FullName;
            var project = Path.Combine(dir, "SampleTests.csproj");
            File.WriteAllText(project, ProjectXml);
            File.WriteAllText(Path.Combine(dir, "Tests.cs"), "namespace S; [TestClass] public class T { [TestMethod] public void A() { } }");

            // a fresh clone: the folder scan finds it (not "no test projects found")
            CollectionAssert.AreEqual(new[] { project }, ProjectDiscovery.FindProjects([root], _ => { }));
            await AssertNotStartedAsync(project, restored: false);

            // restored (IsTestProject / IsTestingPlatformApplication=false from the packages now): the same, nothing starts
            var restore = await RunAsync("dotnet", ["restore", project, "-nologo", "-v:q"], dir);
            Assert.AreEqual(0, restore.Code, restore.Output);
            CollectionAssert.AreEqual(new[] { project }, ProjectDiscovery.FindProjects([root], _ => { }));
            await AssertNotStartedAsync(project, restored: true);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* temp folder: best effort */ }
        }
    }

    private async Task AssertNotStartedAsync(string project, bool restored)
    {
        await using var explorer = new Labcoat.Explorer(new EventHub(), [project], null, [Path.GetDirectoryName(project)!]);
        await explorer.DiscoverAsync(null);
        var r = explorer.RequestRun(null, null);
        await r.Completion!;

        var logs = explorer.Snapshot(includeLogs: true).Logs!.Where(l => l.ProjectId == "SampleTests").ToList();
        var all = string.Join("\n", logs.Select(l => $"{l.Level} {l.Message}"));
        Assert.IsNotEmpty(logs);
        Assert.AreEqual(LogLevels.Warning, logs[0].Level, all);
        Assert.StartsWith(Expected, logs[0].Message, all);
        Assert.AreEqual(!restored, logs[0].Message.Contains("Not restored yet"), all);
        Assert.IsFalse(all.Contains("test host", StringComparison.OrdinalIgnoreCase), all);
        Assert.IsFalse(all.Contains("Building", StringComparison.Ordinal), all);
        Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetDirectoryName(project)!, "bin")), "never built");

        var p = explorer.Snapshot().Projects.Single();
        Assert.AreEqual("vstest", p.TestPlatform);
        Assert.AreEqual(ProjectStatus.Idle, p.Status);
        Assert.StartsWith("SampleTests uses VSTest (Microsoft.NET.Test.Sdk + MSTest.TestAdapter).", p.Diagnostics![0].Message);
        Assert.HasCount(restored ? 1 : 2, p.Diagnostics);
        Assert.IsEmpty(explorer.Snapshot().Tests);
        Assert.AreEqual("skipped", explorer.GetRunStatus(r.RunId!)!.State);
    }

    private async Task<(int Code, string Output)> RunAsync(string file, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var stderr = proc.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await proc.WaitForExitAsync(TestContext.CancellationToken);
        return (proc.ExitCode, await stdout + await stderr);
    }
}
