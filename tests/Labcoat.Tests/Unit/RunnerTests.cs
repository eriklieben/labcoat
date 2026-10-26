using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// Runner packages (Runners.cs, RunnerPackages.cs, docs/runners.md): the runner.json manifest, which runners are used,
/// finding their projects, and the settings that choose them. The Vitest runner itself: tests/Labcoat.Vitest.Tests.
/// </summary>
[TestClass]
public class RunnerTests
{
    private const string Manifest = """
        {
          "id": "Vitest",
          "displayName": "Vitest",
          "command": ["node", "{runner}/bridge.mjs", "--flag"],
          "configFiles": ["vitest.config.ts", "vitest.config.mjs"],
          "packageDependencies": ["vitest"],
          "sourceExtensions": [".ts", "js"],
          "coverage": true
        }
        """;

    private static readonly string[] NoWarnings = [];

    private sealed class Workspace : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-runners-");

        public string Path(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root.FullName, relative));

        public string Write(string relative, string text)
        {
            var path = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        /// <summary>The Vitest runner of <see cref="Manifest"/> under runners/vitest.</summary>
        public RunnerPlugin Plugin()
        {
            var warnings = new List<string>();
            var plugin = RunnerPlugin.Load(Write("runners/vitest/runner.json", Manifest), warnings.Add);
            CollectionAssert.AreEqual(NoWarnings, warnings);
            return plugin!;
        }

        public void Dispose() => Root.Delete(recursive: true);
    }

    private const string VitestPackage = """{ "name": "web", "devDependencies": { "vitest": "5.0.2" } }""";

    [TestMethod]
    public void The_manifest_is_read_with_defaults_and_normalized_values()
    {
        using var w = new Workspace();
        var plugin = w.Plugin();
        Assert.AreEqual("vitest", plugin.Id);
        Assert.AreEqual(w.Path("runners/vitest"), plugin.Directory);
        CollectionAssert.AreEqual(new[] { ".ts", ".js" }, plugin.SourceExtensions.ToArray());
        Assert.IsTrue(plugin.Coverage);

        var project = w.Write("web/package.json", VitestPackage);
        var command = plugin.HostCommand(project);
        Assert.AreEqual(w.Path("web"), command.WorkingDirectory);
        CollectionAssert.AreEqual(new[] { System.IO.Path.Combine(w.Path("runners/vitest"), "bridge.mjs").Replace('\\', '/'), "--flag", "--project", project },
            command.Arguments.Select((a, i) => i == 0 ? a.Replace('\\', '/') : a).ToArray());
    }

    [TestMethod]
    [DataRow("{", "Could not read the test runner")]
    [DataRow("""{ "id": "x" }""", "needs an \"id\" (not 'dotnet') and a \"command\"")]
    [DataRow("""{ "id": "mstest", "command": ["x"] }""", "needs an \"id\" (not 'dotnet')")]
    public void A_bad_manifest_is_a_warning(string json, string warning)
    {
        using var w = new Workspace();
        var warnings = new List<string>();
        Assert.IsNull(RunnerPlugin.Load(w.Write("r/runner.json", json), warnings.Add));
        Assert.Contains(warning, warnings.Single());
    }

    [TestMethod]
    public void A_project_has_the_config_file_or_a_package_json_that_depends_on_the_runner()
    {
        using var w = new Workspace();
        var plugin = w.Plugin();
        w.Write("web/package.json", VitestPackage);
        w.Write("configured/vitest.config.mjs", "export default {}");
        w.Write("both/package.json", """{ "name": "both" }""");
        w.Write("both/vitest.config.ts", "export default {}");
        w.Write("other/package.json", """{ "dependencies": { "jest": "30" } }""");
        w.Write("broken/package.json", "{ not json");

        Assert.IsTrue(plugin.IsProjectDirectory(w.Path("web"), out var file));
        Assert.AreEqual(w.Path("web/package.json"), file);
        Assert.IsTrue(plugin.IsProjectDirectory(w.Path("configured"), out file));
        Assert.AreEqual(w.Path("configured/vitest.config.mjs"), file);
        Assert.IsTrue(plugin.IsProjectDirectory(w.Path("both"), out file));
        Assert.AreEqual(w.Path("both/package.json"), file, "the package.json is the project file when there is one");
        Assert.IsFalse(plugin.IsProjectDirectory(w.Path("other"), out _));
        Assert.IsFalse(plugin.IsProjectDirectory(w.Path("broken"), out _));

        Assert.IsTrue(plugin.IsProjectFile(w.Path("configured/vitest.config.mjs")));
        Assert.IsTrue(plugin.IsProjectFile(w.Path("web/package.json")));
        Assert.IsFalse(plugin.IsProjectFile(w.Path("other/package.json")));
    }

    [TestMethod]
    public void Runner_names_have_dotnet_aliases()
    {
        CollectionAssert.AreEqual(new[] { "dotnet", "vitest" }, TestRunners.ParseList(" MSTest, vitest;NUnit dotnet ")!.ToArray());
        Assert.IsNull(TestRunners.ParseList(" , "));
        Assert.IsNull(TestRunners.ParseList(null));
        foreach (var alias in new[] { "dotnet", ".NET", "mtp", "xunit", "tunit" }) Assert.IsTrue(TestRunners.IsDotnetId(alias), alias);
        Assert.IsFalse(TestRunners.IsDotnetId("vitest"));
    }

    [TestMethod]
    public void The_default_is_dotnet_plus_every_installed_runner_and_a_list_is_exact()
    {
        using var w = new Workspace();
        var plugin = w.Plugin();
        var fetched = new List<string>();
        RunnerPlugin? Fetch(string id)
        {
            fetched.Add(id);
            return null;
        }

        var all = TestRunners.Resolve(null, [plugin], Fetch, out var error)!;
        Assert.IsNull(error);
        Assert.AreEqual("dotnet, vitest", all.Describe());

        var vitestOnly = TestRunners.Resolve(["vitest"], [plugin], Fetch, out _)!;
        Assert.IsFalse(vitestOnly.IncludesDotnet);
        Assert.AreEqual("vitest", vitestOnly.Describe());
        Assert.AreEqual("dotnet", TestRunners.Resolve(["dotnet"], [plugin], Fetch, out _)!.Describe());

        Assert.IsNull(TestRunners.Resolve(["dotnet", "jest"], [plugin], Fetch, out error));
        Assert.StartsWith("The test runner 'jest' isn't installed. Add the Labcoat.Jest package", error);
        Assert.Contains("(installed: vitest)", error);
        CollectionAssert.AreEqual(new[] { "jest" }, fetched);

        Assert.AreSame(plugin, TestRunners.Resolve(["vitest"], [], _ => plugin, out _)!.Plugins.Single(), "a runner that isn't installed is fetched");
        Assert.AreEqual("dotnet", TestRunners.DotnetOnly.Describe());
    }

    [TestMethod]
    public void Runner_packages_are_found_in_a_folder_or_its_subfolders_and_the_first_id_wins()
    {
        using var w = new Workspace();
        w.Plugin();
        w.Write("more/vitest/runner.json", Manifest.Replace("\"Vitest\",\n  \"command\"", "\"Other\",\n  \"command\""));
        w.Write("more/jest/runner.json", """{ "id": "jest", "command": ["node", "jest.mjs"] }""");
        w.Write("single/runner.json", """{ "id": "single", "command": ["x"] }""");
        w.Write("more/empty/readme.txt", "");

        var warnings = new List<string>();
        var plugins = TestRunners.FindPlugins([w.Path("runners"), w.Path("more"), w.Path("single"), w.Path("missing")], warnings.Add);
        CollectionAssert.AreEqual(new[] { "vitest", "jest", "single" }, plugins.Select(p => p.Id).ToArray());
        Assert.AreEqual("Vitest", plugins[0].DisplayName);
        CollectionAssert.AreEqual(NoWarnings, warnings);

        var search = TestRunners.SearchDirectories([w.Path("more")]);
        Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "runners"), search[0]);
        Assert.AreEqual(w.Path("more"), search[1]);
    }

    [TestMethod]
    public void Directory_scans_find_runner_projects_but_not_below_one_or_in_node_modules()
    {
        using var w = new Workspace();
        var runners = new TestRunners(true, [w.Plugin()]);
        var tests = w.Write("tests/Shop.Tests/Shop.Tests.csproj", """<Project Sdk="MSTest.Sdk/4.0.2" />""");
        var web = w.Write("web/package.json", VitestPackage);
        w.Write("web/packages/inner/vitest.config.ts", "");
        w.Write("tools/node_modules/lib/vitest.config.ts", "");
        w.Write(".cache/vitest.config.ts", "");
        var config = w.Write("admin/vitest.config.mjs", "");

        var found = ProjectDiscovery.FindProjects([w.Root.FullName], _ => { }, runners: runners);
        CollectionAssert.AreEqual(new[] { tests, config, web }, found);
        Assert.IsNull(runners.PluginFor(tests));
        Assert.AreEqual("vitest", runners.PluginFor(web)!.Id);

        var vitestOnly = ProjectDiscovery.FindProjects([w.Root.FullName], _ => { }, runners: new TestRunners(false, runners.Plugins));
        CollectionAssert.AreEqual(new[] { config, web }, vitestOnly);
        CollectionAssert.AreEqual(new[] { tests }, ProjectDiscovery.FindProjects([w.Root.FullName], _ => { }), "without runners: .NET only, as before");
    }

    [TestMethod]
    public void Explicit_files_and_solutions_with_runners()
    {
        using var w = new Workspace();
        var plugin = w.Plugin();
        var tests = w.Write("tests/Shop.Tests/Shop.Tests.csproj", """<Project Sdk="MSTest.Sdk/4.0.2" />""");
        var sln = w.Write("Shop.slnx", """<Solution><Project Path="tests/Shop.Tests/Shop.Tests.csproj" /></Solution>""");
        var web = w.Write("web/package.json", VitestPackage);
        var both = new TestRunners(true, [plugin]);
        var vitestOnly = new TestRunners(false, [plugin]);

        CollectionAssert.AreEqual(new[] { tests, web }, ProjectDiscovery.FindProjects([sln], _ => { }, runners: both),
            "a solution's folder is scanned for runner projects");
        CollectionAssert.AreEqual(new[] { web }, ProjectDiscovery.FindProjects([sln], _ => { }, runners: vitestOnly));
        CollectionAssert.AreEqual(new[] { web }, ProjectDiscovery.FindProjects([web], _ => { }, runners: vitestOnly));

        var warnings = new List<string>();
        Assert.IsEmpty(ProjectDiscovery.FindProjects([tests], warnings.Add, runners: vitestOnly));
        Assert.AreEqual("Skipping Shop.Tests.csproj: the dotnet test runner isn't in use (runners: vitest).", warnings.Single());

        var errors = new List<string>();
        Assert.IsEmpty(ProjectDiscovery.FindProjects([web], _ => { }, errors), "without the runner a package.json is no project");
        Assert.StartsWith("Unsupported path", errors.Single());
    }

    [TestMethod]
    public void Runner_projects_are_named_after_their_folder()
    {
        using var w = new Workspace();
        var web = w.Write("web/package.json", VitestPackage);
        var config = w.Write("admin/vitest.config.ts", "");
        var tests = w.Write("tests/Shop.Tests/Shop.Tests.csproj", "<Project />");
        Assert.AreEqual("web", ProjectDiscovery.ProjectNameOf(web));
        Assert.AreEqual("admin", ProjectDiscovery.ProjectNameOf(config));
        Assert.AreEqual("Shop.Tests", ProjectDiscovery.ProjectNameOf(tests));
        CollectionAssert.AreEqual(new[] { "web", "Shop.Tests", "web-2" },
            WorkspaceIdentity.ProjectIds([web, tests, w.Write("other/web/package.json", VitestPackage)]));
    }

    [TestMethod]
    public void Runner_packages_come_from_the_global_packages_folder_by_labcoat_version()
    {
        Assert.AreEqual("Labcoat.Vitest", RunnerPackages.PackageIdOf("vitest"));
        Assert.AreEqual("Labcoat.MyRunner", RunnerPackages.PackageIdOf("my-runner"));
        Assert.AreEqual(Path.Combine("pkgs", "labcoat.vitest", "1.2.0-beta.1", "runner"), RunnerPackages.RunnerDirectory("pkgs", "vitest", "1.2.0-Beta.1"));

        var project = RunnerPackages.ScratchProject("Labcoat.Vitest", "1.2.0");
        Assert.Contains("""<PackageDownload Include="Labcoat.Vitest" Version="[1.2.0]" />""", project);
        Assert.Contains("<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>", project);
    }

    [TestMethod]
    [DoNotParallelize] // NUGET_PACKAGES
    public void A_runner_already_in_the_global_packages_folder_is_used_without_a_restore()
    {
        using var w = new Workspace();
        var before = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", w.Path("packages"));
            w.Write("packages/labcoat.vitest/9.9.9/runner/runner.json", Manifest);
            var info = new List<string>();
            var plugin = RunnerPackages.Fetch("vitest", "9.9.9", w.Root.FullName, info.Add, info.Add);
            Assert.AreEqual(w.Path("packages/labcoat.vitest/9.9.9/runner"), plugin!.Directory);
            CollectionAssert.AreEqual(NoWarnings, info, "nothing to fetch");
        }
        finally { Environment.SetEnvironmentVariable("NUGET_PACKAGES", before); }
    }

    [TestMethod]
    public void Runner_hosts_name_the_tree_levels_themselves()
    {
        var node = JsonNode.Parse("""
            {
              "uid": "test/a.test.ts > math > add(1, 2) = 3",
              "display-name": "add(1, 2) = 3",
              "location.type": "ignored.Type",
              "location.method": "ignored(int)",
              "labcoat.namespace": "test/a.test.ts",
              "labcoat.class": "math",
              "labcoat.method": "add(%i, %i) = %i"
            }
            """)!.AsObject();
        var t = MtpMapping.ToTestNode("web", node);
        Assert.AreEqual(("test/a.test.ts", "math", "add(%i, %i) = %i"), (t.Namespace, t.ClassName, t.MethodName));

        node.Remove("labcoat.class");
        Assert.IsNull(MtpMapping.ToTestNode("web", node).ClassName, "a test at the top of a file has no class");
    }

    [TestMethod]
    public void The_command_line_takes_runners_and_runner_paths()
    {
        var o = StartOptions.Parse(["--runners", "mstest,vitest", "--runner-path=runners", "--runner-path", "/opt/runners"], out var error)!;
        Assert.IsNull(error);
        CollectionAssert.AreEqual(new[] { "dotnet", "vitest" }, o.Runners!.ToArray());
        CollectionAssert.AreEqual(new[] { Path.GetFullPath("runners"), Path.GetFullPath("/opt/runners") }, o.RunnerPaths.ToArray());

        Assert.IsNull(StartOptions.Parse(["--runners", ","], out error));
        Assert.Contains("--runners needs runner names", error!);
        Assert.IsNull(StartOptions.Parse(["--runner-path"], out error));
        Assert.AreEqual("--runner-path needs a folder.", error);
        Assert.IsNull(StartOptions.Parse([], out _)!.Runners);
    }

    [TestMethod]
    [DataRow("""["dotnet", "Vitest"]""", "dotnet,vitest", null)]
    [DataRow("\"vitest, mstest\"", "vitest,dotnet", null)]
    [DataRow("[]", null, "'runners' must name test runners")]
    [DataRow("true", null, "'runners' must name test runners")]
    public void Settings_files_take_runners(string value, string? expected, string? warning)
    {
        using var w = new Workspace();
        Directory.CreateDirectory(w.Path(".git"));
        w.Write(".labcoat.json", $$"""{ "runners": {{value}} }""");
        var config = LabcoatConfig.Load(new LabcoatConfig.Inputs { WorkspaceRoot = w.Root.FullName, NoUserConfig = true });
        Assert.AreEqual(expected, config.Runners is null ? null : string.Join(',', config.Runners));
        if (warning is null) Assert.IsEmpty(config.Warnings);
        else Assert.Contains(warning, config.Warnings.Single());
    }

    [TestMethod]
    public void The_project_watcher_also_reacts_to_runner_project_files()
    {
        using var w = new Workspace();
        var changed = new List<string>();
        using var watcher = new ProjectWatcher([w.Root.FullName], c => { lock (changed) changed.AddRange(c); })
        {
            Quiet = TimeSpan.FromMilliseconds(10),
            ProjectFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "package.json", "vitest.config.ts" },
        };
        watcher.OnChange(w.Path("web/package.json"));
        watcher.OnChange(w.Path("web/node_modules/x/package.json"));
        watcher.OnChange(w.Path("web/src/app.ts"));
        watcher.OnChange(w.Path("admin/VITEST.CONFIG.TS"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (changed) if (changed.Count >= 2) break;
            Thread.Sleep(20);
        }
        lock (changed) CollectionAssert.AreEquivalent(new[] { w.Path("web/package.json"), w.Path("admin/VITEST.CONFIG.TS") }, changed);
    }
}
