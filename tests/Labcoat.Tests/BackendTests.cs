using Labcoat;

namespace Labcoat.Tests;

[TestClass]
public static class TestSetup
{
    public static string DataDir { get; private set; } = "";

    /// <summary>Explorer instances write history / settings / caches: keep them out of the user's profile.</summary>
    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        DataDir = Directory.CreateTempSubdirectory("labcoat-tests-data-").FullName;
        Environment.SetEnvironmentVariable("LABCOAT_HISTORY_DIR", DataDir);
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(DataDir, recursive: true); } catch { }
    }
}

[TestClass]
public class PathContainmentTests
{
    [TestMethod]
    public async Task Source_and_editor_paths_stay_inside_the_roots()
    {
        var tmp = Directory.CreateTempSubdirectory("labcoat-paths-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(tmp.FullName, "ws")).FullName;
            var sibling = Directory.CreateDirectory(Path.Combine(tmp.FullName, "ws-secret")).FullName; // prefix of the root name
            var outside = Directory.CreateDirectory(Path.Combine(tmp.FullName, "outside")).FullName;
            File.WriteAllText(Path.Combine(root, "Code.cs"), "class C {}");
            File.WriteAllText(Path.Combine(sibling, "Secret.cs"), "secret");
            File.WriteAllText(Path.Combine(outside, "passwd"), "secret");
            File.CreateSymbolicLink(Path.Combine(root, "link-to-outside.txt"), Path.Combine(outside, "passwd"));
            Directory.CreateSymbolicLink(Path.Combine(root, "linkdir"), outside);

            await using var explorer = new Explorer(new EventHub(), [], null, [root]);
            Assert.AreEqual(SourceAccessResult.Ok, explorer.CheckSourcePath(Path.Combine(root, "Code.cs")).Result);
            Assert.AreEqual(SourceAccessResult.NotFound, explorer.CheckSourcePath(Path.Combine(root, "Missing.cs")).Result);
            foreach (var bad in new[]
                     {
                         Path.Combine(root, "..", "outside", "passwd"),                 // traversal
                         Path.Combine(root, "..", "..", "..", "..", "..", "..", "etc", "passwd"),
                         Path.Combine(sibling, "Secret.cs"),                            // "/x/ws" must not allow "/x/ws-secret"
                         Path.Combine(root, "link-to-outside.txt"),                    // file symlink out of the root
                         Path.Combine(root, "linkdir", "passwd"),                      // directory symlink out of the root
                         root,                                                          // the root itself is not a file under it
                         "relative/../../etc/passwd", "", "   ",
                     })
            {
                Assert.AreEqual(SourceAccessResult.Forbidden, explorer.CheckSourcePath(bad).Result, bad);
            }

            // open-in-editor uses the same rule (403 before any process is started).
            Assert.AreEqual(403, explorer.OpenInEditor("vscode", Path.Combine(root, "link-to-outside.txt"), 1, null).Status);
            Assert.AreEqual(403, explorer.OpenInEditor("vscode", Path.Combine(root, "..", "outside", "passwd"), 1, null).Status);
            Assert.AreEqual(400, explorer.OpenInEditor("notepad", Path.Combine(root, "Code.cs"), 1, null).Status);
        }
        finally { tmp.Delete(recursive: true); }
    }

    [TestMethod]
    public void IsUnder_is_segment_aware_and_never_allows_the_filesystem_root()
    {
        var sep = Path.DirectorySeparatorChar;
        var r = $"{sep}a{sep}b";
        Assert.IsTrue(PathUtil.IsUnder($"{r}{sep}c.cs", r));
        Assert.IsFalse(PathUtil.IsUnder($"{sep}a{sep}bc{sep}c.cs", r));
        Assert.IsFalse(PathUtil.IsUnder(r, r));
        Assert.IsFalse(PathUtil.IsUnder($"{sep}etc{sep}passwd", sep.ToString()));
    }
}

[TestClass]
public class ProjectDiscoveryTests
{
    private static string Project(string items, string props = "", string sdk = "Microsoft.NET.Sdk") =>
        $"""<Project Sdk="{sdk}"><PropertyGroup><OutputType>Exe</OutputType>{props}</PropertyGroup><ItemGroup>{items}</ItemGroup></Project>""";

    [TestMethod]
    [DataRow("""<PackageReference Include="TUnit" Version="1.72.16" />""", DisplayName = "TUnit")]
    [DataRow("""<PackageReference Include="TUnit.Engine" Version="1.72.16" />""", DisplayName = "TUnit.Engine")]
    [DataRow("""<PackageReference Include="xunit.v3.mtp-v2" Version="4.0.1" />""", DisplayName = "xunit.v3.mtp-v2")]
    [DataRow("""<PackageReference Include="xunit.v3" Version="3.1.0" />""", DisplayName = "xunit.v3")]
    [DataRow("""<PackageReference Include="MSTest" Version="4.0.2" />""", DisplayName = "MSTest")]
    [DataRow("""<PackageReference Include="Microsoft.Testing.Platform.MSBuild" Version="2.0.2" />""", DisplayName = "MTP.MSBuild")]
    [DataRow("""<PackageReference Include="NUnit" Version="4.4.0" /><EnableNUnitRunner>true</EnableNUnitRunner>""", DisplayName = "NUnit runner")]
    public void Text_heuristic_detects_mtp_frameworks(string items) =>
        Assert.IsTrue(ProjectDiscovery.IsTestProjectText(Project(items)));

    [TestMethod]
    public void Text_heuristic_detects_sdks_and_properties()
    {
        Assert.IsTrue(ProjectDiscovery.IsTestProjectText("""<Project Sdk="MSTest.Sdk/4.0.2" />"""));
        Assert.IsTrue(ProjectDiscovery.IsTestProjectText(Project("", "<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>")));
        Assert.IsTrue(ProjectDiscovery.IsTestProjectText(Project("", "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>")));
        Assert.IsFalse(ProjectDiscovery.IsTestProjectText(Project("""<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />""")));
        Assert.IsFalse(ProjectDiscovery.IsTestProjectText(Project("""<PackageReference Include="TUnitHelpersX" Version="1" />""".Replace("TUnit", "Foo"))));
    }

    [TestMethod]
    public void Evaluation_decision_uses_mtp_properties_and_packages()
    {
        var empty = new Dictionary<string, string>();
        static TestRunner Runner(IReadOnlyDictionary<string, string> props, params string[] packages) => TestPlatform.Classify(props, packages).Runner;
        Assert.AreEqual(TestRunner.Mtp, Runner(new Dictionary<string, string> { ["IsTestingPlatformApplication"] = "true" }));
        Assert.AreEqual(TestRunner.Mtp, Runner(new Dictionary<string, string> { ["TestingPlatformDotnetTestSupport"] = "True" }));
        Assert.AreEqual(TestRunner.Mtp, Runner(empty, "TUnit"));
        Assert.AreEqual(TestRunner.Mtp, Runner(empty, "xunit.v3.mtp-v2"));
        Assert.AreEqual(TestRunner.None, Runner(new Dictionary<string, string> { ["IsTestingPlatformApplication"] = "false" }, "Newtonsoft.Json"));
        Assert.AreEqual(TestRunner.Mtp, ProjectDiscovery.ParseEvaluation("""{"Properties":{"IsTestingPlatformApplication":"","EnableMSTestRunner":"true"},"Items":{"PackageReference":[]}}""")!.Runner);
        Assert.AreEqual(TestRunner.Mtp, ProjectDiscovery.ParseEvaluation("""{"Properties":{},"Items":{"PackageReference":[{"Identity":"TUnit.Core"}]}}""")!.Runner);
        Assert.AreEqual(TestRunner.None, ProjectDiscovery.ParseEvaluation("""{"Properties":{},"Items":{"PackageReference":[{"Identity":"Serilog"}]}}""")!.Runner);
        Assert.IsNull(ProjectDiscovery.ParseEvaluation("not json"));
    }

    [TestMethod]
    public void Directory_build_props_settings_are_found_by_msbuild_evaluation()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-detect-");
        try
        {
            // The project file itself says nothing; Directory.Build.props turns it into an MTP test app.
            File.WriteAllText(Path.Combine(root.FullName, "Directory.Build.props"),
                "<Project><PropertyGroup><IsTestingPlatformApplication>true</IsTestingPlatformApplication></PropertyGroup></Project>");
            var inner = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "Checks")).FullName;
            File.WriteAllText(Path.Combine(inner, "Checks.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            var lib = Directory.CreateDirectory(Path.Combine(root.FullName, "lib")).FullName;
            File.WriteAllText(Path.Combine(lib, "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestingPlatformApplication>false</IsTestingPlatformApplication></PropertyGroup></Project>""");

            Assert.IsFalse(ProjectDiscovery.IsTestProject(Path.Combine(inner, "Checks.csproj")));
            var errors = new List<string>();
            var found = ProjectDiscovery.FindProjects([root.FullName], _ => { }, errors);
            Assert.IsEmpty(errors);
            CollectionAssert.AreEqual(new[] { Path.Combine(inner, "Checks.csproj") }, found);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Explicit_paths_that_do_not_work_are_errors()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-bad-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Broken.slnx"), "<Solution><Project Path=");
            File.WriteAllText(Path.Combine(root.FullName, "Broken.sln"), "garbage");
            File.WriteAllText(Path.Combine(root.FullName, "Broken.slnf"), "{ \"solution\": ");
            File.WriteAllText(Path.Combine(root.FullName, "notes.txt"), "");
            var errors = new List<string>();
            var found = ProjectDiscovery.FindProjects(
                [Path.Combine(root.FullName, "missing"), .. new[] { "Broken.slnx", "Broken.sln", "Broken.slnf", "notes.txt" }.Select(f => Path.Combine(root.FullName, f))],
                _ => { }, errors);
            Assert.IsEmpty(found);
            Assert.HasCount(5, errors);
            Assert.StartsWith("Path not found", errors[0]);
            Assert.IsTrue(errors.Skip(1).Take(3).All(e => e.StartsWith("Could not read the solution")), string.Join("\n", errors));
            Assert.StartsWith("Unsupported path", errors[4]);
        }
        finally { root.Delete(recursive: true); }
    }
}

[TestClass]
public class RunModelTests
{
    [TestMethod]
    [DataRow("net8.0;net10.0", "net10.0")]
    [DataRow("net10.0;net8.0", "net10.0")]
    [DataRow("netstandard2.0;net9.0-windows;net9.0", "net9.0")]
    [DataRow("net48;netcoreapp3.1", "netcoreapp3.1")]
    [DataRow("net8.0-windows;net8.0", "net8.0")]
    [DataRow("net481", "net481")]
    public void Highest_dotnet_target_framework_is_the_default(string tfms, string expected) =>
        Assert.AreEqual(expected, Explorer.PickTargetFramework(tfms.Split(';')));

    [TestMethod]
    public void Output_is_capped_with_head_tail_and_marker()
    {
        Assert.AreEqual("small", TestOutput.Cap("small", out var t1));
        Assert.IsFalse(t1);
        var big = new string('a', TestOutput.HeadChars) + new string('m', 200_000) + new string('z', TestOutput.MaxChars);
        var capped = TestOutput.Cap(big, out var t2)!;
        Assert.IsTrue(t2);
        Assert.IsLessThan(TestOutput.MaxChars + 200, capped.Length);
        Assert.StartsWith(new string('a', TestOutput.HeadChars) + "\n… [", capped);
        Assert.EndsWith(new string('z', TestOutput.MaxChars - TestOutput.HeadChars), capped);
        Assert.Contains("GET /api/tests/output", capped);
    }

    [TestMethod]
    public void Output_store_evicts_least_recent_beyond_its_budget()
    {
        var store = new OutputStore(maxChars: 100);
        store.Put("p", "a", new string('x', 40), null);
        store.Put("p", "b", new string('x', 40), null);
        store.Put("p", "c", new string('x', 40), null); // a is evicted
        Assert.IsFalse(store.TryGet("p", "a", out _, out _));
        Assert.IsTrue(store.TryGet("p", "c", out var o, out _));
        Assert.AreEqual(40, o!.Length);
        store.Put("p", "huge", new string('x', 500), null); // larger than the budget: not kept
        Assert.IsFalse(store.TryGet("p", "huge", out _, out _));
        Assert.AreEqual(80, store.Size);
    }

    [TestMethod]
    public void Coverage_without_coverable_lines_is_not_100_percent()
    {
        var report = new CoverageAccumulator().Build("run", ["P"]);
        Assert.AreEqual(0, report.LinesValid);
        Assert.AreEqual(0, report.LineRate);
        Assert.IsTrue(report.NoCoverableLines);
    }

    [TestMethod]
    public void Lean_copy_drops_history_and_output_but_flags_output()
    {
        var t = new TestNode { Id = "1", ProjectId = "p", DisplayName = "T", StdOut = "hi", History = [new(Outcome.Passed, 1, "now")] };
        var lean = t.LeanCopy();
        Assert.IsNull(lean.History);
        Assert.IsNull(lean.StdOut);
        Assert.IsTrue(lean.HasOutput);
        Assert.AreEqual("hi", t.StdOut); // the original is untouched
        Assert.IsNull(new TestNode { Id = "2", ProjectId = "p", DisplayName = "U" }.LeanCopy().HasOutput);
    }

    [TestMethod]
    public void Debug_hints_name_the_pid_for_every_editor()
    {
        var hints = Explorer.DebugHints(4242);
        CollectionAssert.AreEqual(new[] { "vscode", "rider", "visualstudio" }, hints.Select(h => h.Editor).ToArray());
        Assert.IsTrue(hints.All(h => h.Steps.Contains("4242")));
        Assert.Contains("\"processId\": \"4242\"", hints[0].LaunchJson!);
    }

    [TestMethod]
    public async Task Run_requests_are_validated_before_anything_starts()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-req-");
        try
        {
            var proj = Path.Combine(dir.FullName, "A.Tests.csproj");
            await using var explorer = new Explorer(new EventHub(), [proj], null);
            var unknownProject = explorer.RequestRun("nope", null);
            Assert.AreEqual(404, unknownProject.Status);
            CollectionAssert.AreEqual(new[] { "A.Tests" }, unknownProject.KnownProjects);
            var unknownTests = explorer.RequestRun(null, ["x", "y"]);
            Assert.AreEqual(404, unknownTests.Status);
            CollectionAssert.AreEqual(new[] { "x", "y" }, unknownTests.UnknownTestIds);
            Assert.AreEqual(400, explorer.RequestRun(new() { Debug = true }).Status); // debug needs test ids
            Assert.IsNull(explorer.Summary().PendingRun);
            var reply = await explorer.HandleCommandAsync(new ClientCommand { Type = "setWatch" });
            Assert.IsInstanceOfType<CommandRejectedEvent>(reply);
            Assert.AreEqual(400, ((CommandRejectedEvent)reply!).Status);
            Assert.AreEqual(404, ((CommandRejectedEvent)(await explorer.HandleCommandAsync(new ClientCommand { Type = "restartHost", ProjectId = "nope" }))!).Status);
        }
        finally { dir.Delete(recursive: true); }
    }
}
