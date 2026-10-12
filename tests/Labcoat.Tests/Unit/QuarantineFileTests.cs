using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>The shared quarantine list (QuarantineFile.cs): names, the option and setting, and its read-only place next to the UI's quarantine.</summary>
[TestClass]
[DoNotParallelize] // LABCOAT_HISTORY_DIR is process-wide
public class QuarantineFileTests
{
    private static TestNode Node(string display, string? ns, string? cls, string? method, string project = "P", string? id = null) =>
        new() { Id = id ?? display, ProjectId = project, DisplayName = display, Namespace = ns, ClassName = cls, MethodName = method };

    [TestMethod]
    public void Entries_match_whole_names_of_methods_rows_and_classes()
    {
        var file = QuarantineFile.Parse("q.txt",
        [
            "# flaky since the new queue", "", "  Shop.Orders.OrderTests.Places_order  ",
            "Shop.Math.CalculatorTests.Multiply (2,3,7)", "shop.slow.*", "Shop.Legacy.LegacyTests",
        ]);
        Assert.AreEqual(4, file.Count);

        Assert.IsTrue(file.Matches(Node("Places_order", "Shop.Orders", "OrderTests", "Places_order")));
        Assert.IsTrue(file.Matches(Node("Places_order (1)", "Shop.Orders", "OrderTests", "Places_order")), "every data row of a method");
        Assert.IsFalse(file.Matches(Node("Places_order_twice", "Shop.Orders", "OrderTests", "Places_order_twice")), "whole names, not substrings");
        Assert.IsTrue(file.Matches(Node("Multiply (2,3,7)", "Shop.Math", "CalculatorTests", "Multiply")), "one data row");
        Assert.IsFalse(file.Matches(Node("Multiply (2,3,6)", "Shop.Math", "CalculatorTests", "Multiply")));
        Assert.IsTrue(file.Matches(Node("Anything", "Shop.Slow", "DbTests", "Anything")), "wildcard, case-insensitive");
        Assert.IsTrue(file.Matches(Node("A", "Shop.Legacy", "LegacyTests", "A")), "a whole class");
        Assert.IsFalse(QuarantineFile.Parse("e.txt", ["# nothing"]).Matches(Node("A", null, null, null)));
    }

    [TestMethod]
    public void Load_reads_a_file_and_reports_a_missing_one()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-quarantine-");
        try
        {
            var path = Path.Combine(dir.FullName, "quarantine.txt");
            File.WriteAllText(path, "A.B.C\n# c\nA.D.*\n");
            var file = QuarantineFile.Load(path, out var error);
            Assert.IsNull(error);
            Assert.AreEqual(2, file!.Count);
            Assert.AreEqual(path, file.Path);

            Assert.IsNull(QuarantineFile.Load(Path.Combine(dir.FullName, "nope.txt"), out error));
            Assert.Contains("does not exist", error!);
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public void Option_and_setting_are_validated()
    {
        Assert.AreEqual("q.txt", StartOptions.Parse(["--quarantine-file", "q.txt"], out var error)!.QuarantineFile);
        Assert.AreEqual("q.txt", StartOptions.Parse(["--quarantine-file=q.txt"], out error)!.QuarantineFile);
        Assert.IsNull(StartOptions.Parse(["--quarantine-file"], out error));
        Assert.Contains("--quarantine-file needs a file", error!);
        Assert.IsNull(StartOptions.Parse(["--quarantine-file", " "], out error));
        Assert.Contains("--quarantine-file <file>", Cli.Usage);

        var dir = Directory.CreateTempSubdirectory("labcoat-quarantine-cfg-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, ".git"));
            var inputs = new LabcoatConfig.Inputs { WorkspaceRoot = dir.FullName, Home = Path.Combine(dir.FullName, "home"), NoUserConfig = true };
            File.WriteAllText(Path.Combine(dir.FullName, ".labcoat.json"), """{ "quarantineFile": "ci/quarantine.txt" }""");
            var config = LabcoatConfig.Load(inputs);
            Assert.IsEmpty(config.Warnings);
            Assert.AreEqual(Path.Combine(dir.FullName, "ci", "quarantine.txt"), config.QuarantineFile, "relative to the settings file");

            File.WriteAllText(Path.Combine(dir.FullName, ".labcoat.json"), """{ "quarantineFile": 4 }""");
            config = LabcoatConfig.Load(inputs);
            Assert.IsNull(config.QuarantineFile);
            Assert.Contains("'quarantineFile' must be a file path", config.Warnings.Single());
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task The_explorer_adds_the_file_to_the_ui_quarantine_and_keeps_it_read_only()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-quarantine-ex-");
        var history = Environment.GetEnvironmentVariable("LABCOAT_HISTORY_DIR");
        Environment.SetEnvironmentVariable("LABCOAT_HISTORY_DIR", Path.Combine(dir.FullName, "history"));
        try
        {
            var project = Path.Combine(dir.FullName, "P", "P.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var file = QuarantineFile.Parse(Path.Combine(dir.FullName, "q.txt"), ["S.T.Listed"]);
            await using var explorer = new Explorer(new EventHub(), [project], null, [dir.FullName]) { QuarantineFile = file };
            explorer.SeedTests([Node("Listed", "S", "T", "Listed", "P", "1"), Node("Other", "S", "T", "Other", "P", "2")]);

            CollectionAssert.AreEqual(new[] { "P|1" }, explorer.Snapshot().Quarantined);
            Assert.IsNull(explorer.SetQuarantine("P", "2", true), "the UI's own quarantine still works");
            CollectionAssert.AreEquivalent(new[] { "P|1", "P|2" }, explorer.Snapshot().Quarantined);
            Assert.IsNull(explorer.SetQuarantine("P", "1", true), "already quarantined by the file");
            Assert.IsNull(explorer.SetQuarantine("P", "2", false));
            Assert.Contains("remove it from that file", explorer.SetQuarantine("P", "1", false)!);
            CollectionAssert.AreEqual(new[] { "P|1" }, explorer.Snapshot().Quarantined);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LABCOAT_HISTORY_DIR", history);
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
