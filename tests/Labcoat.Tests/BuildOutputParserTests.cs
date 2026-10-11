using Labcoat;

namespace Labcoat.Tests;

[TestClass]
public class BuildOutputParserTests
{
    private const string Output = """
          Determining projects to restore...
          All projects are up-to-date for restore.
        /src/App/Math/Calc.cs(12,18): error CS1002: ; expected [/src/App/App.csproj]
        /src/App/Math/Calc.cs(13,9): warning CS0219: The variable 'x' is assigned but its value is never used [/src/App/App.csproj::TargetFramework=net10.0]
        /src/App/App.csproj : warning NU1603: App depends on Foo (>= 1.0.0) but Foo 1.0.0 was not found. [/src/App/App.csproj]
        CSC : error CS2001: Source file '/src/App/Gone.cs' could not be found. [/src/App/App.csproj]
        MSBUILD : error MSB1009: Project file does not exist.

        Build FAILED.

        /src/App/Math/Calc.cs(12,18): error CS1002: ; expected [/src/App/App.csproj]
        /src/App/Math/Calc.cs(13,9): warning CS0219: The variable 'x' is assigned but its value is never used [/src/App/App.csproj::TargetFramework=net10.0]
            2 Warning(s)
            3 Error(s)
        """;

    [TestMethod]
    public void Parses_and_dedupes_errors_first()
    {
        var list = BuildOutputParser.Parse(Output, out var errors, out var warnings);
        Assert.AreEqual(3, errors);
        Assert.AreEqual(2, warnings);
        Assert.HasCount(5, list);
        CollectionAssert.AreEqual(new[] { "error", "error", "error", "warning", "warning" }, list.Select(d => d.Severity).ToArray());

        var first = list[0];
        Assert.AreEqual("/src/App/Math/Calc.cs", first.File);
        Assert.AreEqual(12, first.Line);
        Assert.AreEqual(18, first.Column);
        Assert.AreEqual("CS1002", first.Code);
        Assert.AreEqual("; expected", first.Message);
        Assert.AreEqual("/src/App/App.csproj", first.Project);
    }

    [TestMethod]
    public void Tool_origins_have_no_file()
    {
        var list = BuildOutputParser.Parse(Output);
        var csc = list.Single(d => d.Code == "CS2001");
        Assert.IsNull(csc.File);
        Assert.IsNull(csc.Line);
        var msbuild = list.Single(d => d.Code == "MSB1009");
        Assert.IsNull(msbuild.File);
        Assert.IsNull(msbuild.Project);
        Assert.AreEqual("Project file does not exist.", msbuild.Message);
    }

    [TestMethod]
    public void Project_level_diagnostic_and_tfm_suffix()
    {
        var list = BuildOutputParser.Parse(Output);
        var nu = list.Single(d => d.Code == "NU1603");
        Assert.AreEqual("/src/App/App.csproj", nu.File);
        Assert.IsNull(nu.Line);
        var cs0219 = list.Single(d => d.Code == "CS0219");
        Assert.AreEqual("/src/App/App.csproj", cs0219.Project); // "::TargetFramework=net10.0" stripped
    }

    [TestMethod]
    public void Windows_paths_and_four_part_locations()
    {
        var list = BuildOutputParser.Parse(@"C:\My Projects\App\Foo.cs(3,5,3,9): error CS0103: The name 'y' does not exist in the current context [C:\My Projects\App\App.csproj]");
        Assert.HasCount(1, list);
        Assert.AreEqual(@"C:\My Projects\App\Foo.cs", list[0].File);
        Assert.AreEqual(3, list[0].Line);
        Assert.AreEqual(5, list[0].Column);
        Assert.AreEqual("CS0103", list[0].Code);
    }

    [TestMethod]
    public void Ignores_prose_and_summary_lines()
    {
        var list = BuildOutputParser.Parse("""
            Build FAILED.
                0 Warning(s)
                1 Error(s)
            Time Elapsed 00:00:01.23
            Restore complete (0.4s)
            """);
        Assert.IsEmpty(list);
    }
}
