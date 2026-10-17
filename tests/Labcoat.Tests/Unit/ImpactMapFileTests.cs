using System.Text.Json;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The impact map file (ImpactMapFile.cs, --impact-map-file): save and load between two explorers whose test ids differ,
/// stale and unknown entries, the stored format, and a plan that selects through a loaded map.
/// </summary>
[TestClass]
public class ImpactMapFileTests
{
    public TestContext TestContext { get; set; }

    private static string TempFile(GitAffectedTests.Repo r, string name = "impact.json") => Path.Combine(r.Dir.FullName, name);

    /// <summary>A second explorer on the repository, as a fresh CI runner has it: the same tests, other ids.</summary>
    private static Explorer OtherMachine(GitAffectedTests.Repo r, params (string Id, string Method)[] tests)
    {
        var explorer = new Explorer(new EventHub(), [r.ShopTests, r.OtherTests], null, [r.Root]);
        explorer.SeedTests([.. tests.Select(t => new TestNode
        {
            Id = t.Id, ProjectId = "Shop.Tests", DisplayName = t.Method, ClassName = "C", MethodName = t.Method,
            FilePath = r.P("tests/Shop.Tests/OrdersTests.cs"), Outcome = Outcome.NotRun,
        })]);
        return explorer;
    }

    private static string[] Ids(AffectedPlan plan) => plan.Tests.Select(t => $"{t.ProjectId}/{t.Id}").ToArray();

    [TestMethod]
    public async Task A_saved_map_selects_the_same_tests_on_another_machine_whose_ids_differ()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        r.RecordImpact();
        var file = TempFile(r);
        var saved = await r.Explorer.SaveImpactMapAsync(file, TestContext.CancellationToken);
        Assert.AreEqual(2, saved!.Tests);
        Assert.AreEqual(1, saved.Files);

        await using var fresh = OtherMachine(r, ("guid-a", "t1"), ("guid-b", "t2"), ("guid-c", "t3"));
        var loaded = fresh.LoadImpactMap(file);
        Assert.IsTrue(loaded.Loaded, loaded.Error);
        Assert.AreEqual((2, 0, 1), (loaded.Tests, loaded.Unmatched, loaded.Files));
        Assert.IsTrue(fresh.ImpactMapState.IsPerTest("Shop.Tests", "guid-a"));
        Assert.IsFalse(fresh.ImpactMapState.Has("Shop.Tests", "guid-c"));

        r.Write("src/Shop/Orders.cs", GitAffectedTests.Repo.Orders.Replace("qty > 0", "qty > 1"));
        var plan = await fresh.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        // t1 executes Validate; t3 has no data in the map (it is newer than it), so it runs too; t2 (Total) does not
        CollectionAssert.AreEquivalent(new[] { "Shop.Tests/guid-a", "Shop.Tests/guid-c" }, Ids(plan));
        var orders = plan.Files.Single(f => f.RelativePath == "src/Shop/Orders.cs");
        Assert.AreEqual(("impactMap", "per-test and class-level"), (orders.Mapping, orders.DataQuality));
        Assert.Contains("Validate", orders.Methods!);
        Assert.Contains("newer than the impact map", plan.Tests.Single(t => t.Id == "guid-c").Reasons.Single());
    }

    [TestMethod]
    public async Task Entries_of_tests_and_projects_that_no_longer_exist_are_ignored()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        r.RecordImpact();
        var file = TempFile(r);
        await r.Explorer.SaveImpactMapAsync(file, TestContext.CancellationToken);

        // t2 was deleted since, and the whole Shop.Tests project is gone from another workspace
        await using var fewer = OtherMachine(r, ("a", "t1"));
        var loaded = fewer.LoadImpactMap(file);
        Assert.AreEqual((1, 1), (loaded.Tests, loaded.Unmatched));
        Assert.IsFalse(fewer.ImpactMapState.Has("Shop.Tests", "b"));

        await using var otherProject = new Explorer(new EventHub(), [r.OtherTests], null, [r.Root]);
        var none = otherProject.LoadImpactMap(file);
        Assert.IsTrue(none.Loaded);
        Assert.AreEqual((0, 2), (none.Tests, none.Unmatched));
        Assert.IsNull(otherProject.ImpactMapState.Info());
    }

    [TestMethod]
    public async Task A_renamed_data_row_shares_the_data_of_its_method()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        r.RecordImpact();
        var file = TempFile(r);
        await r.Explorer.SaveImpactMapAsync(file, TestContext.CancellationToken);

        var explorer = new Explorer(new EventHub(), [r.ShopTests], null, [r.Root]);
        await using var _ = explorer;
        explorer.SeedTests([new TestNode { Id = "x", ProjectId = "Shop.Tests", DisplayName = "t1 (7)", ClassName = "C", MethodName = "t1", FilePath = r.P("tests/Shop.Tests/OrdersTests.cs") }]);
        var loaded = explorer.LoadImpactMap(file);
        Assert.AreEqual((1, 1), (loaded.Tests, loaded.Unmatched)); // t2 has no test any more
        Assert.IsFalse(explorer.ImpactMapState.IsPerTest("Shop.Tests", "x"), "shared by the rows: not exact");
    }

    [TestMethod]
    public async Task A_source_file_that_changed_after_the_map_was_built_falls_back_to_the_project()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        r.RecordImpact();
        var file = TempFile(r);
        await r.Explorer.SaveImpactMapAsync(file, TestContext.CancellationToken);

        // main moved on after the map was built: the lines of Orders.cs are not the ones the map knows
        r.Write("src/Shop/Orders.cs", "// header\n" + GitAffectedTests.Repo.Orders);
        await r.CommitAllAsync("moved");
        r.Write("src/Shop/Orders.cs", "// header\n" + GitAffectedTests.Repo.Orders.Replace("qty > 0", "qty > 1"));

        await using var fresh = OtherMachine(r, ("a", "t1"), ("b", "t2"), ("c", "t3"));
        fresh.LoadImpactMap(file);
        var plan = await fresh.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        Assert.AreEqual("project", plan.Files.Single(f => f.RelativePath == "src/Shop/Orders.cs").Mapping);
        CollectionAssert.AreEquivalent(new[] { "Shop.Tests/a", "Shop.Tests/b", "Shop.Tests/c" }, Ids(plan));
        Assert.IsTrue(plan.Warnings!.Any(w => w.Contains("Orders.cs changed after the impact map was built")));
    }

    [TestMethod]
    public async Task A_file_that_is_the_same_text_with_other_line_endings_is_not_stale()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        r.RecordImpact();
        var file = TempFile(r);
        await r.Explorer.SaveImpactMapAsync(file, TestContext.CancellationToken);
        Assert.AreEqual(ImpactMapFile.HashText("a\nb\n"), ImpactMapFile.HashText("a\r\nb\r\n"));
        Assert.AreEqual(ImpactMapFile.HashText("a"), ImpactMapFile.HashText("﻿a"));
        Assert.AreNotEqual(ImpactMapFile.HashText("a"), ImpactMapFile.HashText("b"));
    }

    [TestMethod]
    public async Task The_file_is_versioned_relative_with_forward_slashes_and_replaced_atomically()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        r.RecordImpact();
        var file = TempFile(r, Path.Combine("out", "nested", "impact.json"));
        await r.Explorer.SaveImpactMapAsync(file, TestContext.CancellationToken);
        await r.Explorer.SaveImpactMapAsync(file, TestContext.CancellationToken); // overwrites
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"), "no temp file is left behind");

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        Assert.AreEqual("labcoat-impact-map", root.GetProperty("format").GetString());
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(Cli.Version, root.GetProperty("labcoat").GetString());
        CollectionAssert.AreEqual(new[] { "src/Shop/Orders.cs" }, root.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "tests/Shop.Tests/Shop.Tests.csproj" }, root.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("path").GetString()).ToArray());
        Assert.DoesNotContain(r.Root, File.ReadAllText(file), "no absolute path");
        Assert.DoesNotContain("\\\\", File.ReadAllText(file), "no backslash separators");
        Assert.AreEqual(40, root.GetProperty("commit").GetString()!.Length);
        Assert.AreEqual(1, root.GetProperty("hashes").GetArrayLength());
    }

    [TestMethod]
    public async Task A_gz_file_round_trips()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        r.RecordImpact();
        var plain = TempFile(r, "impact.json");
        var gz = TempFile(r, "impact.json.gz");
        await r.Explorer.SaveImpactMapAsync(plain, TestContext.CancellationToken);
        await r.Explorer.SaveImpactMapAsync(gz, TestContext.CancellationToken);
        var magic = File.ReadAllBytes(gz).Take(2).ToArray();
        CollectionAssert.AreEqual(new byte[] { 0x1f, 0x8b }, magic, "gzip");

        await using var fresh = OtherMachine(r, ("a", "t1"), ("b", "t2"));
        Assert.AreEqual(2, fresh.LoadImpactMap(gz).Tests);
    }

    [TestMethod]
    public async Task Unusable_files_are_reported_not_thrown()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        await using var fresh = OtherMachine(r, ("a", "t1"));
        void Bad(string name, string text, string expected)
        {
            var path = TempFile(r, name);
            File.WriteAllText(path, text);
            var result = fresh.LoadImpactMap(path);
            Assert.IsFalse(result.Loaded, name);
            Assert.Contains(expected, result.Error!, name);
        }
        Bad("garbage.json", "not json at all", "not a valid impact map");
        Bad("other.json", """{"hello":1}""", "not a Labcoat impact map");
        Bad("future.json", """{"format":"labcoat-impact-map","version":99}""", "newer Labcoat");
        Bad("old.json", """{"format":"labcoat-impact-map","version":0}""", "unsupported format version");
        Bad("broken.json", """{"format":"labcoat-impact-map","version":1,"files":["a.cs"],"hashes":["h"],"names":[],"spans":[[5,1,2,-1]],"profiles":[],"projects":[]}""", "outside");
        Bad("empty.json", "", "not a valid impact map");
        Assert.IsFalse(fresh.LoadImpactMap(TempFile(r, "missing.json")).Loaded);
        Assert.IsNull(fresh.ImpactMapState.Info());
    }

    [TestMethod]
    public async Task A_symlinked_checkout_still_stores_relative_paths()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        var link = Path.Combine(r.Dir.FullName, "link");
        try { Directory.CreateSymbolicLink(link, r.Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("symbolic links are not permitted here: " + ex.Message);
            return;
        }

        // the workspace is opened through the link (like /var -> /private/var on macOS); git and the coverage paths are real paths
        string ViaLink(string p) => Path.Combine(link, p.Replace('/', Path.DirectorySeparatorChar));
        await using var explorer = new Explorer(new EventHub(), [ViaLink("tests/Shop.Tests/Shop.Tests.csproj")], null, [link]);
        explorer.SeedTests([new TestNode { Id = "a", ProjectId = "Shop.Tests", DisplayName = "t1", ClassName = "C", MethodName = "t1", FilePath = ViaLink("tests/Shop.Tests/OrdersTests.cs") }]);
        var orders = PathUtil.Canonicalize(r.P("src/Shop/Orders.cs"));
        lock (explorer.Sync) explorer.ImpactMapState.Record("Shop.Tests", ["a"], new() { [orders] = [new LineSpan(4, 7) { Name = "Validate" }] });

        var file = TempFile(r, "link-impact.json");
        Assert.IsNotNull(await explorer.SaveImpactMapAsync(file, TestContext.CancellationToken));
        var text = File.ReadAllText(file);
        Assert.Contains("\"path\":\"tests/Shop.Tests/Shop.Tests.csproj\"", text);
        Assert.Contains("\"src/Shop/Orders.cs\"", text);
        Assert.DoesNotContain("..", text, "no path climbs out of the root");

        await using var fresh = new Explorer(new EventHub(), [ViaLink("tests/Shop.Tests/Shop.Tests.csproj")], null, [link]);
        fresh.SeedTests([new TestNode { Id = "z", ProjectId = "Shop.Tests", DisplayName = "t1", ClassName = "C", MethodName = "t1", FilePath = ViaLink("tests/Shop.Tests/OrdersTests.cs") }]);
        Assert.AreEqual(1, fresh.LoadImpactMap(file).Tests);
    }

    [TestMethod]
    public async Task A_map_without_data_is_not_written()
    {
        await using var r = await GitAffectedTests.Repo.CreateAsync();
        var file = TempFile(r);
        Assert.IsNull(await r.Explorer.SaveImpactMapAsync(file, TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public void A_big_suite_stays_small()
    {
        // 20,000 tests in 1,000 classes, each class one coverage process over 40 methods of 40 files
        var doc = new ImpactMapDocument { Labcoat = "x", BuiltAt = "2026-01-01T00:00:00.0000000Z", Commit = new string('a', 40) };
        for (var f = 0; f < 400; f++)
        {
            doc.Files.Add($"src/Product/Module{f / 20}/Service{f}.cs");
            doc.Hashes.Add("0123456789abcdef");
        }
        for (var n = 0; n < 40; n++) doc.Names.Add($"Method{n}");
        int Span(int file, int line, int name)
        {
            doc.Spans.Add([file, line, line + 4, name]);
            return doc.Spans.Count - 1;
        }
        var spanOf = new Dictionary<(int, int, int), int>();
        int[] Profile(IEnumerable<(int File, int Line, int Name)> methods) =>
            methods.Select(m => spanOf.TryGetValue(m, out var i) ? i : spanOf[m] = Span(m.File, m.Line, m.Name)).Distinct().Order().ToArray();
        var project = new DocProject("tests/Big.Tests/Big.Tests.csproj", []);
        for (var c = 0; c < 1000; c++)
        {
            doc.Profiles.Add(Profile(Enumerable.Range(0, 40).Select(m => ((c * 7 + m) % 400, 10 + m * 5, m))));
            project.Classes.Add(new DocClass($"Big.Namespace.Tests{c}", Enumerable.Range(0, 20).Select(t => new DocTest($"Test_{t}_does_something", $"Test_{t}_does_something", c, false)).ToList()));
        }
        doc.Projects.Add(project);
        using var ms = new MemoryStream();
        ImpactMapFile.Write(ms, doc);
        TestContext.WriteLine($"20,000 tests, class-level: {ms.Length / 1024} KB");
        Assert.IsLessThan(3L * 1024 * 1024, ms.Length);
        ms.Position = 0;
        var back = ImpactMapFile.Read(ms);
        Assert.AreEqual(20_000, back.Projects.Single().Classes.Sum(c => c.Tests.Count));

        // per test (impactMap "test"): every test has a profile of its own, here 40 methods each
        var perTest = new DocProject(project.Path, []);
        var shared = back.Profiles.Count;
        foreach (var c in project.Classes)
            perTest.Classes.Add(new DocClass(c.Name, c.Tests.Select((t, i) =>
            {
                doc.Profiles.Add(Profile(Enumerable.Range(0, 40).Select(m => ((c.Name.Length + i + m * 3) % 400, 10 + m * 5 + i % 7, (m + i) % 40))));
                return t with { Profile = doc.Profiles.Count - 1, PerTest = true };
            }).ToList()));
        doc.Projects.Clear();
        doc.Projects.Add(perTest);
        using var big = new MemoryStream();
        ImpactMapFile.Write(big, doc);
        TestContext.WriteLine($"20,000 tests, per test: {big.Length / 1024} KB ({shared} shared profiles before)");
        Assert.IsLessThan(6L * 1024 * 1024, big.Length);
    }
}
