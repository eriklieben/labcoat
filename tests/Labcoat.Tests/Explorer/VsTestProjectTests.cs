using Labcoat;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// Projects that aren't on Microsoft.Testing.Platform (Explorer.Platform.cs), through a fake `dotnet`: they are never
/// built or started, and their first log line and diagnostics say how to switch them.
/// </summary>
[TestClass]
public class VsTestProjectTests
{
    public TestContext TestContext { get; set; }

    private const string NUnitVsTest = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
            <PackageReference Include="NUnit" Version="4.4.0" />
            <PackageReference Include="NUnit3TestAdapter" Version="6.3.0" />
          </ItemGroup>
        </Project>
        """;

    private const string MSTestMtp = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup>
          <ItemGroup><PackageReference Include="MSTest" Version="4.0.2" /></ItemGroup>
        </Project>
        """;

    private sealed class Workspace : IAsyncDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-vstest-");
        public FakeDotnet Dotnet { get; } = new();
        public Labcoat.Explorer Explorer { get; }
        public string Project { get; }

        public Workspace(string projectXml)
        {
            var dir = Directory.CreateDirectory(Path.Combine(Root.FullName, "Legacy.Tests")).FullName;
            Project = Path.Combine(dir, "Legacy.Tests.csproj");
            File.WriteAllText(Project, projectXml);
            Explorer = new Labcoat.Explorer(new EventHub(), [Project], null, [Root.FullName]) { DotnetOverride = Dotnet.InvokeAsync };
        }

        public TestProject State => Explorer.Snapshot().Projects.Single();
        public List<LogEvent> Logs => Explorer.Snapshot(includeLogs: true).Logs!.Where(l => l.ProjectId == "Legacy.Tests").ToList();
        public bool Built => Dotnet.Calls.Any(c => c[0] == "build");

        /// <summary>A newer write time, so the stamp of the platform check changes.</summary>
        public void Rewrite(string projectXml)
        {
            File.WriteAllText(Project, projectXml);
            File.SetLastWriteTimeUtc(Project, DateTime.UtcNow.AddMinutes(1));
        }

        public async ValueTask DisposeAsync()
        {
            await Explorer.DisposeAsync();
            try { Root.Delete(recursive: true); } catch { /* temp folder: best effort */ }
        }
    }

    [TestMethod]
    public async Task A_vstest_project_is_never_built_or_started_and_its_first_log_line_says_how_to_switch()
    {
        await using var w = new Workspace(NUnitVsTest);
        // the MSBuild evaluation (no restore) confirms what the project file says
        w.Dotnet.Evaluation = FakeDotnet.Eval(null, packages: ["Microsoft.NET.Test.Sdk", "NUnit", "NUnit3TestAdapter"]);
        await w.Explorer.DiscoverAsync(null);

        Assert.IsFalse(w.Built, string.Join("\n", w.Dotnet.Calls.Select(c => string.Join(' ', c))));
        var p = w.State;
        Assert.AreEqual("vstest", p.TestPlatform);
        Assert.AreEqual(ProjectStatus.Idle, p.Status);
        Assert.IsNull(p.LastError);
        Assert.AreEqual("Legacy.Tests uses VSTest (Microsoft.NET.Test.Sdk + NUnit3TestAdapter). Labcoat runs Microsoft.Testing.Platform projects.",
            p.Diagnostics![0].Message);
        Assert.AreEqual(TestPlatform.DocsUrl, p.Diagnostics[0].Link);
        Assert.AreEqual("Legacy.Tests is not restored yet: Labcoat read its project files.", p.Diagnostics[1].Message);
        var first = w.Logs[0];
        Assert.AreEqual(LogLevels.Warning, first.Level);
        Assert.StartsWith("Legacy.Tests uses VSTest (Microsoft.NET.Test.Sdk + NUnit3TestAdapter). Labcoat runs Microsoft.Testing.Platform projects: " +
                          "set <EnableNUnitRunner>true</EnableNUnitRunner> and <OutputType>Exe</OutputType>", first.Message);
        Assert.HasCount(1, w.Logs);

        // a run of it is skipped; a second discovery doesn't evaluate or log again (same files)
        var evaluations = w.Dotnet.Calls.Count;
        var r = w.Explorer.RequestRun(null, null);
        await r.Completion!;
        Assert.AreEqual("skipped", w.Explorer.GetRunStatus(r.RunId!)!.State);
        await w.Explorer.DiscoverAsync(null);
        await w.Explorer.RestartHostAsync(null);
        Assert.HasCount(evaluations, w.Dotnet.Calls);
        Assert.HasCount(1, w.Logs.Where(l => l.Level == LogLevels.Warning));
        Assert.IsFalse(w.Logs.Any(l => l.Message.Contains("test host", StringComparison.OrdinalIgnoreCase)), string.Join("\n", w.Logs.Select(l => l.Message)));

        // switched to Microsoft.Testing.Platform: the marks go and the project builds
        w.Rewrite(NUnitVsTest.Replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFramework>net10.0</TargetFramework><EnableNUnitRunner>true</EnableNUnitRunner>"));
        w.Dotnet.BuildExitCode = 1;
        await w.Explorer.DiscoverAsync(null);
        Assert.IsTrue(w.Built);
        Assert.IsNull(w.State.TestPlatform);
        Assert.IsNull(w.State.Diagnostics);
    }

    [TestMethod]
    public async Task The_evaluation_overrules_the_project_file_when_a_setting_comes_from_elsewhere()
    {
        // the file says VSTest (no runner switch in it); MSBuild sees EnableNUnitRunner from an import
        await using var w = new Workspace(NUnitVsTest);
        w.Dotnet.Evaluation = FakeDotnet.Eval(null, properties: new Dictionary<string, string> { ["EnableNUnitRunner"] = "true" },
            packages: ["NUnit", "NUnit3TestAdapter"]);
        w.Dotnet.BuildExitCode = 1;
        await w.Explorer.DiscoverAsync(null);
        Assert.IsTrue(w.Built);
        Assert.IsNull(w.State.TestPlatform);
    }

    [TestMethod]
    public async Task After_the_build_the_restored_settings_decide_before_any_test_host_starts()
    {
        // the project file turns the runner on, but the evaluation (a condition) says it's off: VSTest after all
        await using var w = new Workspace(MSTestMtp);
        var assets = Path.Combine(Path.GetDirectoryName(w.Project)!, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        File.WriteAllText(assets, "{}");
        var dll = Path.Combine(w.Root.FullName, "Legacy.Tests.dll");
        File.WriteAllText(dll, "not a test app");
        w.Dotnet.Evaluation = FakeDotnet.Eval(dll, properties: new Dictionary<string, string>
        {
            ["EnableMSTestRunner"] = "false", ["IsTestingPlatformApplication"] = "false", ["IsTestProject"] = "true", ["ProjectAssetsFile"] = assets,
        }, packages: ["MSTest"]);
        await w.Explorer.DiscoverAsync(null);

        Assert.IsTrue(w.Built);
        Assert.AreEqual("vstest", w.State.TestPlatform);
        Assert.HasCount(1, w.State.Diagnostics!); // restored: no restore note
        Assert.AreEqual("Legacy.Tests uses VSTest (MSTest). Labcoat runs Microsoft.Testing.Platform projects.", w.State.Diagnostics![0].Message);
        var logs = w.Logs.Select(l => l.Message).ToList();
        Assert.IsFalse(logs.Any(l => l.Contains("test host", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Discovery", StringComparison.Ordinal)),
            string.Join("\n", logs));
        Assert.StartsWith("Legacy.Tests uses VSTest (MSTest)", logs[^1]);

        // the next discovery doesn't build it again (same files)
        var builds = w.Dotnet.Calls.Count(c => c[0] == "build");
        await w.Explorer.DiscoverAsync(null);
        Assert.AreEqual(builds, w.Dotnet.Calls.Count(c => c[0] == "build"));
    }

    [TestMethod]
    public async Task A_restored_project_without_a_test_framework_is_not_started()
    {
        await using var w = new Workspace("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""");
        var assets = Path.Combine(w.Root.FullName, "project.assets.json");
        File.WriteAllText(assets, "{}");
        w.Dotnet.Evaluation = FakeDotnet.Eval(Path.Combine(w.Root.FullName, "Tool.dll"), properties: new Dictionary<string, string> { ["ProjectAssetsFile"] = assets });
        await w.Explorer.DiscoverAsync(null);
        Assert.AreEqual("none", w.State.TestPlatform);
        Assert.StartsWith("Legacy.Tests is not a Microsoft.Testing.Platform test project", w.State.Diagnostics![0].Message);
        Assert.StartsWith("Legacy.Tests is not a Microsoft.Testing.Platform test project", w.Logs[^1].Message);
    }
}
