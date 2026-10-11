using System.Runtime.InteropServices;
using System.Xml.Linq;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>Which coverage settings a project uses, the settings Labcoat writes, and what a report keeps (CoverageSettings.cs).</summary>
[TestClass]
public class CoverageSettingsTests
{
    private const string Settings = """
        <?xml version="1.0" encoding="utf-8"?>
        <Configuration><CodeCoverage><ModulePaths><Include><ModulePath>.*Api\.dll$</ModulePath></Include></ModulePaths></CodeCoverage></Configuration>
        """;

    /// <summary>repo/.git, repo/app.slnx, repo/tests/Api.Tests/Api.Tests.csproj with bin/Debug/net10.0/Api.Tests.dll.</summary>
    private sealed class Repo : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-covset-");
        public string ProjectDir => P("tests/Api.Tests");
        public string Output => P("tests/Api.Tests/bin/Debug/net10.0");
        public string WorkDir => P("work");

        public Repo()
        {
            Directory.CreateDirectory(P(".git"));
            Write("app.slnx", "<Solution />");
            Write("tests/Api.Tests/Api.Tests.csproj", "<Project />");
            Write("tests/Api.Tests/bin/Debug/net10.0/Api.Tests.dll", "");
        }

        public string P(string relative) => Path.GetFullPath(Path.Combine(Root.FullName, relative));

        public string Write(string relative, string text)
        {
            var p = P(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            return p;
        }

        public CoverageProject Project(string? tpArgs = null, string[]? refs = null) => new()
        {
            ProjectId = "Api.Tests",
            ProjectPath = P("tests/Api.Tests/Api.Tests.csproj"),
            TargetPath = P("tests/Api.Tests/bin/Debug/net10.0/Api.Tests.dll"),
            TestingPlatformArguments = tpArgs,
            ReferencedAssemblies = refs ?? ["Api", "Labcoat.Testing", "Lib"],
            TestAssemblies = ["Api.Tests", "Other.Tests"],
            WorkspaceRoot = Root.FullName,
        };

        /// <summary>The coverage extension (<paramref name="version"/>, in the test app's deps.json) with its CLR profiler for this platform.</summary>
        public void AddProfiler(string version = "18.11.2")
        {
            Write("tests/Api.Tests/bin/Debug/net10.0/Api.Tests.deps.json",
                $$"""{ "libraries": { "Microsoft.Testing.Extensions.CodeCoverage/{{version}}": { "type": "package" }, "MSTest/4.0.2": {} } }""");
            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            if (OperatingSystem.IsMacOS()) Write($"tests/Api.Tests/bin/Debug/net10.0/runtimes/osx-{arch}/native/libInstrumentationEngine.dylib", "");
            else Write($"tests/Api.Tests/bin/Debug/net10.0/runtimes/linux-{arch}/native/libInstrumentationEngine.so", "");
        }

        public void Dispose() => Root.Delete(recursive: true);
    }

    private static (string? Path, string? Origin) Find(Repo r, CoverageOptions? o = null, string? tp = null, List<string>? log = null) =>
        CoverageSettingsResolver.Find(r.Project(tp), o ?? CoverageOptions.None, (_, m) => log?.Add(m));

    // ------------------------------------------------------------------ finding the file

    [TestMethod]
    public void By_name_next_to_the_project_then_the_output_folder_then_up_to_the_solution_root()
    {
        using var r = new Repo();
        Assert.AreEqual((null, null), Find(r));

        var atRoot = r.Write("CodeCoverage.xml", Settings);
        Assert.AreEqual((atRoot, "convention"), Find(r));
        var inTests = r.Write("tests/unit.coverage.xml", Settings);
        Assert.AreEqual((inTests, "convention"), Find(r));
        var inOutput = r.Write("tests/Api.Tests/bin/Debug/net10.0/coverage.settings.xml", Settings);
        Assert.AreEqual((inOutput, "convention"), Find(r));
        var next = r.Write("tests/Api.Tests/coverage.settings.xml", Settings);
        Assert.AreEqual((next, "convention"), Find(r));
    }

    [TestMethod]
    public void Only_files_with_a_CodeCoverage_section_count_and_runsettings_qualify()
    {
        using var r = new Repo();
        r.Write("tests/Api.Tests/coverage.settings.xml", "<Configuration><Other /></Configuration>"); // no CodeCoverage
        r.Write("tests/Api.Tests/broken.coverage.xml", "<not xml");
        r.Write("tests/Api.Tests/plain.runsettings", "<RunSettings><RunConfiguration /></RunSettings>");
        Assert.AreEqual((null, null), Find(r));
        var rs = r.Write("tests/Api.Tests/coverage.runsettings", """
            <RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName="Code Coverage">
              <Configuration><CodeCoverage /></Configuration>
            </DataCollector></DataCollectors></DataCollectionRunSettings></RunSettings>
            """);
        Assert.AreEqual((rs, "convention"), Find(r));
    }

    [TestMethod]
    public void The_walk_up_stops_at_the_solution_root()
    {
        using var r = new Repo();
        // above the solution root (repo/.git + app.slnx): never looked at
        File.WriteAllText(Path.Combine(r.Root.Parent!.FullName, $"{r.Root.Name}.coverage.xml"), Settings);
        try
        {
            Assert.AreEqual((null, null), Find(r));
            var dirs = CoverageSettingsResolver.SearchDirectories(r.ProjectDir, r.Output, null).ToList();
            CollectionAssert.AreEqual(new[] { r.ProjectDir, r.Output, r.P("tests"), r.Root.FullName }, dirs);
        }
        finally { File.Delete(Path.Combine(r.Root.Parent!.FullName, $"{r.Root.Name}.coverage.xml")); }
    }

    [TestMethod]
    public void The_workspace_root_stops_the_walk_when_there_is_no_solution()
    {
        using var r = new Repo();
        File.Delete(r.P("app.slnx"));
        Directory.Delete(r.P(".git"));
        var dirs = CoverageSettingsResolver.SearchDirectories(r.ProjectDir, r.Output, r.P("tests")).ToList();
        CollectionAssert.AreEqual(new[] { r.ProjectDir, r.Output, r.P("tests") }, dirs);
    }

    [TestMethod]
    public void The_option_wins_relative_to_the_project_then_its_base_directory()
    {
        using var r = new Repo();
        r.Write("tests/Api.Tests/coverage.settings.xml", Settings);
        var shared = r.Write("shared/ci.xml", Settings);
        Assert.AreEqual((shared, "option"), Find(r, new CoverageOptions { Settings = shared }));
        Assert.AreEqual((shared, "option"), Find(r, new CoverageOptions { Settings = "ci.xml", BaseDirectory = r.P("shared") }));
        var perProject = r.Write("tests/Api.Tests/ci.xml", Settings);
        Assert.AreEqual((perProject, "option"), Find(r, new CoverageOptions { Settings = "ci.xml", BaseDirectory = r.P("shared") }));

        // missing: a warning, then the project's own
        var log = new List<string>();
        Assert.AreEqual((r.P("tests/Api.Tests/coverage.settings.xml"), "convention"), Find(r, new CoverageOptions { Settings = "nope.xml" }, log: log));
        Assert.Contains("nope.xml was not found", log.Single());
    }

    [TestMethod]
    public void Coverage_settings_in_TestingPlatformCommandLineArguments_and_testconfig_json()
    {
        using var r = new Repo();
        r.Write("tests/Api.Tests/coverage.settings.xml", Settings);
        var named = r.Write("tests/Api.Tests/my settings.xml", Settings);
        Assert.AreEqual((named, "msbuild"), Find(r, tp: "--report-trx --coverage-settings \"my settings.xml\""));
        var log = new List<string>();
        Assert.AreEqual("convention", Find(r, tp: "--coverage-settings missing.xml", log: log).Origin);
        Assert.Contains("missing.xml", log.Single());

        // testconfig.json with a codeCoverage section: read by the extension itself, before files found by name
        var tc = r.Write("tests/Api.Tests/bin/Debug/net10.0/Api.Tests.testconfig.json", """{ /* c */ "platformOptions": {}, "codeCoverage": { "Configuration": {} }, }""");
        Assert.AreEqual((tc, "testconfig"), Find(r));
        r.Write("tests/Api.Tests/bin/Debug/net10.0/Api.Tests.testconfig.json", """{ "platformOptions": {} }""");
        Assert.AreEqual("convention", Find(r).Origin);
    }

    [TestMethod]
    public void SettingsArgument_parses_the_forms_a_command_line_uses()
    {
        Assert.IsNull(CoverageSettingsResolver.SettingsArgument(null));
        Assert.IsNull(CoverageSettingsResolver.SettingsArgument("--coverage --report-trx"));
        Assert.AreEqual("a.xml", CoverageSettingsResolver.SettingsArgument("--coverage-settings a.xml --x"));
        Assert.AreEqual("a b.xml", CoverageSettingsResolver.SettingsArgument("--coverage-settings 'a b.xml'"));
        Assert.AreEqual("c.xml", CoverageSettingsResolver.SettingsArgument("--coverage-settings=c.xml"));
        Assert.AreEqual("d.xml", CoverageSettingsResolver.SettingsArgument("--x \"\" --coverage-settings:d.xml"));
        Assert.IsNull(CoverageSettingsResolver.SettingsArgument("--coverage-settings"));
    }

    // ------------------------------------------------------------------ the plan

    [TestMethod]
    public void A_settings_file_is_used_as_it_is_apart_from_dynamic_instrumentation_where_the_profiler_exists()
    {
        using var r = new Repo();
        var file = r.Write("tests/Api.Tests/coverage.settings.xml", Settings);
        var plan = CoverageSettingsResolver.Plan(r.Project(), new CoverageOptions { Exclude = ["**/Migrations/**"] }, r.WorkDir);
        CollectionAssert.AreEqual(new[] { "--coverage", "--coverage-output-format", "cobertura", "--coverage-output", "out.xml", "--coverage-settings" },
            plan.Arguments("out.xml")[..6]);
        Assert.AreEqual(("file", "convention", file), (plan.Info.Source, plan.Info.Origin, plan.Info.Path));
        CollectionAssert.AreEqual(new[] { "**/Migrations/**" }, plan.Info.Exclude);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(file, plan.SettingsArguments[1]);
            Assert.IsFalse(plan.RewritesOutput);
            return;
        }
        // no profiler for this platform in the output folder: the file as it is, static instrumentation
        Assert.AreEqual(file, plan.SettingsArguments[1]);
        Assert.IsTrue(plan.RewritesOutput);

        r.AddProfiler();
        plan = CoverageSettingsResolver.Plan(r.Project(), CoverageOptions.None, r.WorkDir);
        Assert.AreEqual("dynamic", plan.Info.Instrumentation);
        Assert.IsFalse(plan.RewritesOutput);
        var effective = plan.SettingsArguments[1];
        Assert.IsTrue(effective.StartsWith(r.WorkDir, StringComparison.Ordinal), effective);
        var cc = XDocument.Load(effective).Descendants("CodeCoverage").Single();
        Assert.AreEqual("False", cc.Element("EnableStaticManagedInstrumentation")!.Value);
        Assert.AreEqual("True", cc.Element("EnableDynamicManagedInstrumentation")!.Value);
        Assert.AreEqual(@".*Api\.dll$", cc.Descendants("ModulePath").Single().Value); // the rest unchanged
        // the same settings again: the same file
        Assert.AreEqual(effective, CoverageSettingsResolver.Plan(r.Project(), CoverageOptions.None, r.WorkDir).SettingsArguments[1]);
        Assert.Contains("coverage.settings.xml (found by name), dynamic instrumentation", plan.Info.Describe(r.ProjectDir));
    }

    [TestMethod]
    public void A_file_that_chooses_its_instrumentation_keeps_it()
    {
        foreach (var (inner, dynamic) in new[]
                 {
                     ("<EnableStaticManagedInstrumentation>True</EnableStaticManagedInstrumentation>", false),
                     ("<EnableDynamicManagedInstrumentation>false</EnableDynamicManagedInstrumentation>", false),
                     ("<EnableDynamicManagedInstrumentation>True</EnableDynamicManagedInstrumentation>", true),
                     ("", true),
                 })
        {
            var doc = XDocument.Parse($"<RunSettings><X><Configuration><CodeCoverage>{inner}</CodeCoverage></Configuration></X></RunSettings>");
            Assert.AreEqual(dynamic, CoverageSettingsResolver.PreferDynamic(doc), inner);
            if (dynamic) Assert.AreEqual("False", doc.Descendants("EnableStaticManagedInstrumentation").Single().Value);
        }
        Assert.IsFalse(CoverageSettingsResolver.PreferDynamic(XDocument.Parse("<Configuration />")));
    }

    [TestMethod]
    public void Testconfig_json_passes_no_settings_argument()
    {
        using var r = new Repo();
        r.Write("tests/Api.Tests/bin/Debug/net10.0/Api.Tests.testconfig.json", """{ "codeCoverage": {} }""");
        var plan = CoverageSettingsResolver.Plan(r.Project(), CoverageOptions.None, r.WorkDir);
        Assert.IsEmpty(plan.SettingsArguments);
        Assert.AreEqual("testconfig", plan.Info.Source);
        Assert.Contains("the codeCoverage section of Api.Tests.testconfig.json", plan.Info.Describe());
    }

    [TestMethod]
    public void An_unreadable_settings_file_is_passed_on_as_it_is()
    {
        using var r = new Repo();
        r.AddProfiler();
        var file = r.Write("cov.xml", Settings);
        var log = new List<string>();
        // readable for the search, then gone: Plan reports it and passes the path on
        var options = new CoverageOptions { Settings = file };
        var project = r.Project();
        var (found, _) = CoverageSettingsResolver.Find(project, options);
        Assert.AreEqual(file, found);
        File.WriteAllText(file, "<Configuration><CodeCoverage>"); // broken XML, but named explicitly
        var plan = CoverageSettingsResolver.Plan(project, options, r.WorkDir, (_, m) => log.Add(m));
        Assert.AreEqual(file, plan.SettingsArguments[1]);
        Assert.Contains("passed on as it is", log.Single());
    }

    [TestMethod]
    public void The_defaults_count_referenced_projects_without_Labcoat_tests_frameworks_and_generated_code()
    {
        using var r = new Repo();
        r.AddProfiler();
        var project = r.Project(refs: ["Api", "Labcoat.Testing", "Labcoat.Aspire.Hosting", "Lib", "Other.Tests", "TUnit.Core", "Microsoft.Testing.Platform", "xunit.core"]);
        var plan = CoverageSettingsResolver.Plan(project, CoverageOptions.None, r.WorkDir);
        Assert.AreEqual("defaults", plan.Info.Source);
        CollectionAssert.AreEqual(new[] { "Api", "Lib" }, plan.Info.Modules);
        Assert.AreEqual(OperatingSystem.IsWindows() || CoverageSettingsResolver.DynamicInstrumentationAvailable(r.Output) ? "dynamic" : "static", plan.Info.Instrumentation);
        Assert.Contains("Labcoat's defaults (referenced projects: Api, Lib; generated code and obj/ left out)", plan.Info.Describe());

        var cc = XDocument.Load(plan.SettingsArguments[1]).Descendants("CodeCoverage").Single();
        var include = cc.Element("ModulePaths")!.Element("Include")!.Elements().Select(e => e.Value).ToList();
        CollectionAssert.AreEqual(new[] { @".*[\\/]Api\.(dll|exe)$", @".*[\\/]Lib\.(dll|exe)$" }, include);
        var exclude = cc.Element("ModulePaths")!.Element("Exclude")!.Elements().Select(e => e.Value).ToList();
        CollectionAssert.Contains(exclude, @".*[\\/]Labcoat(\..*)?\.(dll|exe)$");
        CollectionAssert.Contains(exclude, @".*[\\/]Api\.Tests\.(dll|exe)$");
        CollectionAssert.Contains(exclude, @".*[\\/]Microsoft\.Testing\..*\.(dll|exe)$");
        var sources = cc.Element("Sources")!.Element("Exclude")!.Elements().Select(e => e.Value).ToList();
        CollectionAssert.Contains(sources, @".*[\\/]obj[\\/].*");
        CollectionAssert.Contains(sources, @".*\.g\.cs$");
        var attributes = cc.Element("Attributes")!.Element("Exclude")!.Elements().Select(e => e.Value).ToList();
        CollectionAssert.Contains(attributes, @"^System\.CodeDom\.Compiler\.GeneratedCodeAttribute$");
        Assert.IsFalse(attributes.Any(a => a.Contains("CompilerGenerated")), "async state machines and lambdas are real code");

        var f = plan.Filter;
        Assert.IsTrue(f.Keeps("Api", "/r/src/Api/Orders.cs"));
        Assert.IsFalse(f.Keeps("Labcoat.Testing", "/r/src/Labcoat.Testing/X.cs"));
        Assert.IsFalse(f.Keeps("Api.Tests", "/r/tests/Api.Tests/T.cs"));
        Assert.IsFalse(f.Keeps("TUnit.Engine", "/_/TUnit/X.cs"));
        Assert.IsFalse(f.Keeps("Api", "/r/src/Api/obj/Debug/net10.0/Microsoft.Gen.Logging/Logging.g.cs"));
        Assert.IsFalse(f.Keeps("Api", "/r/src/Api/Data/Model.Designer.cs"));
        Assert.IsFalse(f.Keeps("Tools", "/r/tools/Seeder/Program.cs"), "not a referenced project");
    }

    [TestMethod]
    public void Include_brings_defaults_back_and_exclude_always_wins()
    {
        using var r = new Repo();
        var options = new CoverageOptions { Include = ["Labcoat.*", "**/obj/**/Keep.g.cs"], Exclude = ["Lib", "**/Migrations/**"] };
        var plan = CoverageSettingsResolver.Plan(r.Project(), options, r.WorkDir);
        CollectionAssert.AreEqual(new[] { "Api", "Labcoat.Testing", "Lib" }, plan.Info.Modules);
        var cc = XDocument.Load(plan.SettingsArguments[1]).Descendants("CodeCoverage").Single();
        var include = cc.Element("ModulePaths")!.Element("Include")!.Elements().Select(e => e.Value).ToList();
        CollectionAssert.Contains(include, @".*[\\/]Labcoat\.[^\\/]*\.(dll|exe)$");
        var exclude = cc.Element("ModulePaths")!.Element("Exclude")!.Elements().Select(e => e.Value).ToList();
        Assert.IsFalse(exclude.Any(e => e.Contains("Labcoat")), string.Join("\n", exclude));

        var f = plan.Filter;
        Assert.IsTrue(f.Keeps("Labcoat.Testing", "/r/src/Labcoat.Testing/X.cs"));
        Assert.IsTrue(f.Keeps("Api", "/r/src/Api/obj/Debug/Keep.g.cs"));
        Assert.IsFalse(f.Keeps("Api", "/r/src/Api/obj/Debug/Other.g.cs"));
        Assert.IsFalse(f.Keeps("Lib", "/r/src/Lib/A.cs"));
        Assert.IsFalse(f.Keeps("Api", "/r/src/Api/Migrations/0001_Init.cs"));
        Assert.Contains("; include Labcoat.*, **/obj/**/Keep.g.cs; exclude Lib, **/Migrations/**", plan.Info.Describe());

        // an include of a test framework undoes the framework exclusions in the collector settings too
        var fw = CoverageSettingsResolver.Plan(r.Project(), new CoverageOptions { Include = ["TUnit*"] }, r.WorkDir);
        var fwExclude = XDocument.Load(fw.SettingsArguments[1]).Descendants("ModulePaths").Single().Element("Exclude")!.Elements().Select(e => e.Value).ToList();
        Assert.IsFalse(fwExclude.Any(e => e.Contains("TUnit")));
        Assert.IsTrue(fw.Filter.Keeps("TUnit.Core", "/x/A.cs"));
    }

    [TestMethod]
    public void With_a_settings_file_only_Coverage_Exclude_changes_the_report()
    {
        var f = CoverageFilter.ForSettingsFile(new CoverageOptions { Include = ["X"], Exclude = ["Seeder", "*.g.cs"] });
        Assert.IsTrue(f.Keeps("Labcoat.Testing", "/a/B.cs"));
        Assert.IsTrue(f.Keeps("Api.Tests", "/a/obj/C.cs"));
        Assert.IsFalse(f.Keeps("Seeder", "/a/C.cs"));
        Assert.IsFalse(f.Keeps("Api", "/a/obj/Logging.g.cs"));
    }

    [TestMethod]
    public void Globs_match_modules_file_names_and_paths()
    {
        var f = CoverageFilter.ForSettingsFile(new CoverageOptions { Exclude = ["Shop.*", "Gen?.cs", "src/Legacy/**", "/abs/only/*.cs", "**/deep/x.cs"] });
        Assert.IsFalse(f.Keeps("Shop.Core", "/r/a.cs"));
        Assert.IsTrue(f.Keeps("Shop", "/r/a.cs"));
        Assert.IsFalse(f.Keeps("M", "/r/Gen1.cs"));
        Assert.IsTrue(f.Keeps("M", "/r/Gen12.cs"));
        Assert.IsFalse(f.Keeps("M", "/r/src/Legacy/a/b.cs"));
        Assert.IsFalse(f.Keeps("M", @"C:\r\src\Legacy\b.cs"));
        Assert.IsTrue(f.Keeps("M", "/r/src/LegacyX/b.cs"));
        Assert.IsFalse(f.Keeps("M", "/abs/only/a.cs"));
        Assert.IsTrue(f.Keeps("M", "/abs/only/sub/a.cs"));
        Assert.IsFalse(f.Keeps("M", "/r/deep/x.cs"));
        Assert.IsTrue(CoverageFilter.ForSettingsFile(new CoverageOptions { Exclude = [" ", ""] }).Keeps("M", "/a.cs"));
    }

    [TestMethod]
    public void Module_and_generated_classification()
    {
        Assert.IsTrue(CoverageSettingsResolver.IsLabcoat("Labcoat"));
        Assert.IsTrue(CoverageSettingsResolver.IsLabcoat("labcoat.testing.tunit"));
        Assert.IsFalse(CoverageSettingsResolver.IsLabcoat("LabcoatFan"));
        foreach (var m in new[] { "TUnit", "TUnit.Core", "MSTest.TestFramework", "Microsoft.VisualStudio.TestPlatform.TestFramework", "Microsoft.Testing.Platform", "Microsoft.TestPlatform.CoreUtilities", "xunit.core", "NUnit", "nunit.framework" })
            Assert.IsTrue(CoverageSettingsResolver.IsFramework(m), m);
        foreach (var m in new[] { "TUnitHelpers", "Microsoft.TestingX", "MyMSTest" })
            Assert.IsFalse(CoverageSettingsResolver.IsFramework(m), m);
        foreach (var p in new[] { "/a/obj/b.cs", "obj/x.cs", @"C:\a\OBJ\b.cs", "/a/B.g.cs", "/a/B.g.i.cs", "/a/Form.Designer.cs", "/a/X.generated.cs" })
            Assert.IsTrue(CoverageSettingsResolver.IsGenerated(p), p);
        foreach (var p in new[] { "/a/object/b.cs", "/a/B.cs", "/a/Design.cs" })
            Assert.IsFalse(CoverageSettingsResolver.IsGenerated(p), p);
    }

    [TestMethod]
    public void Dynamic_instrumentation_needs_the_profiler_for_this_platform()
    {
        using var r = new Repo();
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(CoverageSettingsResolver.DynamicInstrumentationAvailable(r.Output));
            return;
        }
        Assert.IsFalse(CoverageSettingsResolver.DynamicInstrumentationAvailable(r.Output));
        r.Write("tests/Api.Tests/bin/Debug/net10.0/runtimes/linux-nothere/native/libInstrumentationEngine.so", "");
        Assert.IsFalse(CoverageSettingsResolver.DynamicInstrumentationAvailable(r.Output));
        r.AddProfiler();
        Assert.IsTrue(CoverageSettingsResolver.DynamicInstrumentationAvailable(r.Output));
        // an extension older than 18.11 collects nothing dynamically in server mode: static instrumentation
        r.AddProfiler("18.1.0");
        Assert.IsFalse(CoverageSettingsResolver.DynamicInstrumentationAvailable(r.Output));
        r.AddProfiler("18.12.0-preview.1");
        Assert.IsTrue(CoverageSettingsResolver.DynamicInstrumentationAvailable(r.Output));
        r.Write("tests/Api.Tests/bin/Debug/net10.0/Api.Tests.deps.json", "{ broken");
        Assert.IsFalse(CoverageSettingsResolver.DynamicInstrumentationAvailable(r.Output), "no version known");

        using var flat = new Repo(); // a RID-specific build: the natives next to the app
        flat.Write("tests/Api.Tests/bin/Debug/net10.0/" + (OperatingSystem.IsMacOS() ? "libInstrumentationEngine.dylib" : "libInstrumentationEngine.so"), "");
        flat.Write("tests/Api.Tests/bin/Debug/net10.0/Api.Tests.deps.json", """{ "libraries": { "Microsoft.Testing.Extensions.CodeCoverage/18.12.0": {} } }""");
        Assert.IsTrue(CoverageSettingsResolver.DynamicInstrumentationAvailable(flat.Output));
    }

    [TestMethod]
    public void The_extension_version_comes_from_deps_json_else_the_dll()
    {
        // this test app's own output folder: MSTest's extension, from its deps.json
        var own = CoverageSettingsResolver.ExtensionVersion(AppContext.BaseDirectory);
        Assert.IsNotNull(own);
        Assert.IsGreaterThanOrEqualTo(17, own.Major);
        using var r = new Repo();
        Assert.IsNull(CoverageSettingsResolver.ExtensionVersion(r.Output));
        // no deps.json entry: the DLL's product version
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Microsoft.Testing.Extensions.CodeCoverage.dll"), Path.Combine(r.Output, "Microsoft.Testing.Extensions.CodeCoverage.dll"));
        Assert.AreEqual(own, CoverageSettingsResolver.ExtensionVersion(r.Output));
        Assert.IsNull(CoverageSettingsResolver.ExtensionVersion(r.P("missing")));
    }

    [TestMethod]
    public void Options_merge_settings_by_layer_and_add_up_lists()
    {
        var file = new CoverageOptions { Settings = "a.xml", BaseDirectory = "/cfg", Include = ["A"], Exclude = ["X"] };
        Assert.AreSame(file, file.Merge(null));
        var cli = file.Merge(new CoverageOptions { Include = ["a", "B"], Exclude = ["Y"] });
        Assert.AreEqual(("a.xml", "/cfg"), (cli.Settings, cli.BaseDirectory));
        CollectionAssert.AreEqual(new[] { "A", "B" }, cli.Include.ToArray());
        CollectionAssert.AreEqual(new[] { "X", "Y" }, cli.Exclude.ToArray());
        var over = file.Merge(new CoverageOptions { Settings = "b.xml", BaseDirectory = "/cwd" });
        Assert.AreEqual(("b.xml", "/cwd"), (over.Settings, over.BaseDirectory));
    }

    [TestMethod]
    public void The_report_keeps_what_the_filter_keeps()
    {
        using var ws = new CoverageTests.Workspace();
        var acc = new CoverageAccumulator();
        acc.AddCobertura(ws.Report("product-a.xml"), CoverageFilter.ForDefaults(new CoverageOptions { Exclude = ["src/Text/**"] }, ["Shop.Tests"]));
        var report = acc.Build("r", ["Shop.Tests"]);
        CollectionAssert.AreEquivalent(new[] { "Constants.cs", "Dead.cs", "Calc.cs" }, report.Files.Select(f => Path.GetFileName(f.Path)).ToArray());
    }

    [TestMethod]
    public void Describe_names_the_choice()
    {
        var info = new CoverageSettingsInfo { ProjectId = "P", Source = "file", Path = "/r/P/c.xml", Origin = "option", Instrumentation = "static" };
        Assert.AreEqual("c.xml (Coverage.Settings), static instrumentation", info.Describe("/r/P"));
        Assert.AreEqual("/r/P/c.xml (--coverage-settings in TestingPlatformCommandLineArguments), static instrumentation",
            (info with { Origin = "msbuild" }).Describe());
        Assert.Contains("every module except tests, test frameworks and Labcoat",
new CoverageSettingsInfo { ProjectId = "P", Source = "defaults", Instrumentation = "dynamic" }.Describe());
    }
}
