using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// Microsoft.Testing.Platform vs VSTest (TestPlatform.cs): per framework, from the project files alone (not restored) and
/// from an MSBuild evaluation (restored: the properties the test packages set), and the message per case.
/// </summary>
[TestClass]
public class TestPlatformTests
{
    private static string Csproj(string packages, string properties = "", string sdk = "Microsoft.NET.Sdk") => $"""
        <Project Sdk="{sdk}">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework>{properties}</PropertyGroup>
          <ItemGroup>{packages}</ItemGroup>
        </Project>
        """;

    private static string Refs(params string[] ids) => string.Concat(ids.Select(id => $"""<PackageReference Include="{id}" Version="1.0.0" />"""));

    private const string Exe = "<OutputType>Exe</OutputType>";

    /// <summary>Project file fixtures: (name, csproj, expected runner, expected framework).</summary>
    public static IEnumerable<object?[]> Fixtures =>
    [
        ["MSTest on MTP", Csproj(Refs("MSTest"), Exe + "<EnableMSTestRunner>true</EnableMSTestRunner>"), TestRunner.Mtp, TestPlatform.MSTest],
        ["MSTest.Sdk", Csproj("", "", "MSTest.Sdk/4.0.2"), TestRunner.Mtp, TestPlatform.MSTest],
        ["MSTest.Sdk with UseVSTest", Csproj("", "<UseVSTest>true</UseVSTest>", "MSTest.Sdk/4.0.2"), TestRunner.VsTest, TestPlatform.MSTest],
        ["MSTest on VSTest (adapter)", Csproj(Refs("Microsoft.NET.Test.Sdk", "MSTest.TestAdapter", "MSTest.TestFramework")), TestRunner.VsTest, TestPlatform.MSTest],
        ["MSTest metapackage without the runner", Csproj(Refs("MSTest"), Exe), TestRunner.VsTest, TestPlatform.MSTest],
        ["NUnit on MTP", Csproj(Refs("NUnit", "NUnit3TestAdapter"), Exe + "<EnableNUnitRunner>true</EnableNUnitRunner>"), TestRunner.Mtp, TestPlatform.NUnit],
        ["NUnit on VSTest", Csproj(Refs("Microsoft.NET.Test.Sdk", "NUnit", "NUnit3TestAdapter")), TestRunner.VsTest, TestPlatform.NUnit],
        ["NUnit runner switched off", Csproj(Refs("NUnit", "NUnit3TestAdapter"), "<EnableNUnitRunner>false</EnableNUnitRunner>"), TestRunner.VsTest, TestPlatform.NUnit],
        ["xUnit v2", Csproj(Refs("Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio")), TestRunner.VsTest, TestPlatform.XUnitV2],
        ["xUnit v2 without the adapter", Csproj(Refs("Microsoft.NET.Test.Sdk", "xunit")), TestRunner.VsTest, TestPlatform.XUnitV2],
        ["xUnit v2 with YTest.MTP.XUnit2", Csproj(Refs("xunit", "xunit.runner.visualstudio", "YTest.MTP.XUnit2"), Exe), TestRunner.Mtp, TestPlatform.XUnitV2],
        ["xunit.v3.mtp-v2", Csproj(Refs("xunit.v3.mtp-v2"), Exe + "<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>"), TestRunner.Mtp, TestPlatform.XUnitV3],
        // xunit.v3 4.x is on MTP v2; its test app answers --server even without UseMicrosoftTestingPlatformRunner
        ["xunit.v3 with the VSTest adapter too", Csproj(Refs("Microsoft.NET.Test.Sdk", "xunit.v3", "xunit.runner.visualstudio"), Exe), TestRunner.Mtp, TestPlatform.XUnitV3],
        ["TUnit", Csproj(Refs("TUnit"), Exe), TestRunner.Mtp, TestPlatform.TUnit],
        ["TUnit.Sdk", Csproj("", "", "TUnit.Sdk/1.0.0"), TestRunner.Mtp, TestPlatform.TUnit],
        ["another framework on VSTest", Csproj(Refs("Microsoft.NET.Test.Sdk", "Expecto")), TestRunner.VsTest, null],
        ["IsTestProject only", Csproj("", "<IsTestProject>true</IsTestProject>"), TestRunner.VsTest, null],
        ["Microsoft.Testing.Platform directly", Csproj(Refs("Microsoft.Testing.Platform"), Exe), TestRunner.Mtp, null],
        ["a console app", Csproj(Refs("Newtonsoft.Json"), Exe), TestRunner.None, null],
    ];

    public static string FixtureName(System.Reflection.MethodInfo _, object?[] data) => (string)data[0]!;

    [TestMethod]
    [DynamicData(nameof(Fixtures), DynamicDataDisplayName = nameof(FixtureName))]
    public void Not_restored_the_project_file_decides(string name, string csproj, TestRunner runner, string? framework)
    {
        var info = TestPlatform.Classify(TestPlatform.ReadText(csproj));
        Assert.AreEqual(runner, info.Runner, name);
        Assert.AreEqual(framework, info.Framework, name);
        Assert.AreEqual(runner != TestRunner.None, ProjectDiscovery.IsTestProjectText(csproj), name);
    }

    [TestMethod]
    public void Restored_the_evaluated_properties_decide()
    {
        // what `dotnet msbuild -getProperty… -getItem:PackageReference` prints for a restored project: the MSTest / NUnit
        // adapters set IsTestingPlatformApplication from their runner switch, Microsoft.NET.Test.Sdk sets IsTestProject
        static string Eval(string props, params string[] packages) =>
            "{\"Properties\":{" + props + "},\"Items\":{\"PackageReference\":[" +
            string.Join(",", packages.Select(p => "{\"Identity\":\"" + p + "\"}")) + "]}}";

        var vstest = ProjectDiscovery.ParseEvaluation(Eval("""
            "IsTestingPlatformApplication":"false","IsTestProject":"true","EnableMSTestRunner":"false"
            """, "Microsoft.NET.Test.Sdk", "MSTest.TestAdapter", "MSTest.TestFramework"))!;
        Assert.AreEqual((TestRunner.VsTest, TestPlatform.MSTest), (vstest.Runner, vstest.Framework));
        CollectionAssert.AreEqual(new[] { "Microsoft.NET.Test.Sdk", "MSTest.TestAdapter" }, vstest.Evidence.ToArray());

        var mtp = ProjectDiscovery.ParseEvaluation(Eval("""
            "IsTestingPlatformApplication":"true","IsTestProject":"true","EnableMSTestRunner":"true"
            """, "MSTest"))!;
        Assert.AreEqual(TestRunner.Mtp, mtp.Runner);

        // NUnit: IsTestingPlatformApplication alone doesn't switch the adapter (it sets it from EnableNUnitRunner)
        Assert.AreEqual(TestRunner.VsTest, ProjectDiscovery.ParseEvaluation(Eval("""
            "IsTestingPlatformApplication":"true","EnableNUnitRunner":""
            """, "NUnit", "NUnit3TestAdapter"))!.Runner);
        // MSTest.Sdk: UsingMSTestSdk, unless UseVSTest
        Assert.AreEqual(TestRunner.Mtp, ProjectDiscovery.ParseEvaluation(Eval("\"UsingMSTestSdk\":\"true\""))!.Runner);
        Assert.AreEqual(TestRunner.VsTest, ProjectDiscovery.ParseEvaluation(Eval("\"UsingMSTestSdk\":\"true\",\"UseVSTest\":\"true\""))!.Runner);
        // a restored project with Microsoft.NET.Test.Sdk only (IsTestProject from the package): VSTest
        Assert.AreEqual(TestRunner.VsTest, ProjectDiscovery.ParseEvaluation(Eval("\"IsTestProject\":\"true\""))!.Runner);
    }

    [TestMethod]
    public void Static_reading_includes_the_directory_files_in_msbuild_order()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-platform-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup>
                  <ItemGroup><PackageReference Include="MSTest.TestAdapter;$(Extra)" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root.FullName, "Directory.Packages.props"),
                """<Project><ItemGroup><GlobalPackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>""");
            File.WriteAllText(Path.Combine(root.FullName, "Directory.Build.targets"), "<Project><broken");
            var dir = Directory.CreateDirectory(Path.Combine(root.FullName, "A.Tests")).FullName;
            var project = Path.Combine(dir, "A.Tests.csproj");
            File.WriteAllText(project, """
                <Project>
                  <Sdk Name="Microsoft.NET.Sdk" />
                  <Import Project="x.props" Sdk="Other.Sdk/1.0" />
                  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
                </Project>
                """);
            var facts = TestPlatform.ReadStatic(project);
            CollectionAssert.AreEquivalent(new[] { "MSTest.TestAdapter", "Microsoft.NET.Test.Sdk" }, facts.Packages.ToArray());
            CollectionAssert.AreEquivalent(new[] { "Microsoft.NET.Sdk", "Other.Sdk" }, facts.Sdks.ToArray());
            Assert.AreEqual(TestRunner.Mtp, TestPlatform.Classify(facts).Runner);

            // the project file comes after Directory.Build.props: its value wins
            File.WriteAllText(project, Csproj("", "<EnableMSTestRunner>false</EnableMSTestRunner>"));
            Assert.AreEqual(TestRunner.VsTest, TestPlatform.Classify(TestPlatform.ReadStatic(project)).Runner);
            Assert.AreEqual(ProjectFacts.Empty, TestPlatform.ReadText("<Project"));
        }
        finally { root.Delete(recursive: true); }
    }

    // ------------------------------------------------------------------ messages

    private static TestPlatformInfo Info(string csproj, string sdk = "Microsoft.NET.Sdk") => TestPlatform.Classify(TestPlatform.ReadText(Csproj(csproj, "", sdk)));

    [TestMethod]
    public void One_message_per_framework_with_its_switch_and_the_docs()
    {
        var mstest = Info(Refs("Microsoft.NET.Test.Sdk", "MSTest.TestAdapter", "MSTest.TestFramework"));
        Assert.AreEqual(
            "SampleTests uses VSTest (Microsoft.NET.Test.Sdk + MSTest.TestAdapter). Labcoat runs Microsoft.Testing.Platform projects: " +
            "set <EnableMSTestRunner>true</EnableMSTestRunner> and <OutputType>Exe</OutputType> (or use MSTest.Sdk). Labcoat doesn't start it. " +
            "See https://github.com/eriklieben/labcoat/blob/main/docs/troubleshooting.md#my-project-uses-vstest",
            TestPlatform.Message("SampleTests", mstest, restored: true));

        Assert.AreEqual("set <EnableNUnitRunner>true</EnableNUnitRunner> and <OutputType>Exe</OutputType>, with NUnit3TestAdapter 6 or later",
            TestPlatform.Fix(Info(Refs("Microsoft.NET.Test.Sdk", "NUnit", "NUnit3TestAdapter"))));
        Assert.AreEqual("move to xUnit v3: replace xunit and xunit.runner.visualstudio with xunit.v3.mtp-v2, and set <OutputType>Exe</OutputType>",
            TestPlatform.Fix(Info(Refs("Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio"))));
        var sdk = TestPlatform.Classify(TestPlatform.ReadText(Csproj("", "<UseVSTest>true</UseVSTest>", "MSTest.Sdk/4.0.2")));
        Assert.AreEqual("A uses VSTest (MSTest.Sdk + UseVSTest=true). Labcoat runs Microsoft.Testing.Platform projects.", TestPlatform.Summary("A", sdk));
        Assert.StartsWith("remove <UseVSTest>true</UseVSTest>", TestPlatform.Fix(sdk));
        var unknown = Info(Refs("Microsoft.NET.Test.Sdk", "Expecto"));
        Assert.AreEqual("A uses VSTest (Microsoft.NET.Test.Sdk). Labcoat runs Microsoft.Testing.Platform projects.", TestPlatform.Summary("A", unknown));
        Assert.StartsWith("turn on the Microsoft.Testing.Platform runner of its test framework", TestPlatform.Fix(unknown));
        Assert.AreEqual("A uses VSTest (IsTestProject=true). Labcoat runs Microsoft.Testing.Platform projects.",
            TestPlatform.Summary("A", TestPlatform.Classify(TestPlatform.ReadText(Csproj("", "<IsTestProject>true</IsTestProject>")))));
    }

    [TestMethod]
    public void Not_restored_and_not_a_test_project_say_so()
    {
        var nunit = Info(Refs("NUnit", "NUnit3TestAdapter"));
        var message = TestPlatform.Message("Legacy.Tests", nunit, restored: false);
        Assert.Contains("(Not restored yet: Labcoat read the project files. Run `dotnet restore` if a package should switch it.)", message);
        var diagnostics = TestPlatform.Diagnostics("Legacy.Tests", nunit, restored: false);
        Assert.HasCount(2, diagnostics);
        Assert.AreEqual(("warning", TestPlatform.DocsUrl), (diagnostics[0].Level, diagnostics[0].Link));
        Assert.AreEqual("Legacy.Tests uses VSTest (NUnit3TestAdapter). Labcoat runs Microsoft.Testing.Platform projects.", diagnostics[0].Message);
        Assert.AreEqual("Set <EnableNUnitRunner>true</EnableNUnitRunner> and <OutputType>Exe</OutputType>, with NUnit3TestAdapter 6 or later, then Rediscover.", diagnostics[0].Hint);
        Assert.AreEqual(("info", "Legacy.Tests is not restored yet: Labcoat read its project files."), (diagnostics[1].Level, diagnostics[1].Message));
        Assert.HasCount(1, TestPlatform.Diagnostics("Legacy.Tests", nunit, restored: true));

        var app = Info(Refs("Newtonsoft.Json"));
        Assert.AreEqual(
            "Tool is not a Microsoft.Testing.Platform test project: it references no test framework and IsTestingPlatformApplication is not true. " +
            "Labcoat doesn't start it. See " + TestPlatform.DocsUrl,
            TestPlatform.Message("Tool", app, restored: true));
        Assert.StartsWith("If it is a test project, turn on the Microsoft.Testing.Platform runner", TestPlatform.Diagnostics("Tool", app, true)[0].Hint);
    }
}
