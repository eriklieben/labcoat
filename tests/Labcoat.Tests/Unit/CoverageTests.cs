using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>Cobertura parsing and merging (Coverage.cs), the per-test coverage cache and the impact map.</summary>
[TestClass]
public class CoverageTests
{
    private static readonly HashSet<string> NoExclusions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TestAssemblies = new(StringComparer.OrdinalIgnoreCase) { "Shop.Tests" };

    /// <summary>A temp workspace with the fixture reports ($ROOT$ replaced) and the source files they name.</summary>
    internal sealed class Workspace : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-cov-");

        public Workspace()
        {
            Write("src/Math/Calc.cs", string.Join("\n", Enumerable.Range(1, 40).Select(i => $"// line {i}")));
            Write("src/Text/Format.cs", "a\nb\nc\nd\ne\nf\ng");
            Write("src/Constants.cs", "1\n2\n3\n4");
        }

        public string Path(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root.FullName, relative));

        public string Write(string relative, string text)
        {
            var p = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            return p;
        }

        public string Report(string fixture)
        {
            var text = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cobertura", fixture))
                .Replace("$ROOT$", Root.FullName);
            return Write("reports/" + fixture, text);
        }

        public void Dispose() => Root.Delete(recursive: true);
    }

    [TestMethod]
    public void One_report_uses_class_lines_takes_the_max_per_line_and_skips_excluded_modules()
    {
        using var ws = new Workspace();
        var acc = new CoverageAccumulator();
        acc.AddCobertura(ws.Report("product-a.xml"), TestAssemblies);
        var report = acc.Build("run-1", ["Shop.Tests"]);

        Assert.AreEqual(4, acc.FileCount); // Calc, Format, Constants, Dead (Shop.Tests and the class without a file are left out)
        CollectionAssert.AreEqual(new[] { "Constants.cs", "Dead.cs", Path.Combine("Math", "Calc.cs"), Path.Combine("Text", "Format.cs") },
            report.Files.Select(f => f.RelativePath).ToArray());
        var calc = report.Files.Single(f => f.RelativePath.EndsWith("Calc.cs"));
        Assert.AreEqual(ws.Path("src/Math/Calc.cs"), calc.Path);
        Assert.AreEqual("Shop.Core", calc.Module);
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 20, 21, 22, 30, 31 }, calc.Lines.Select(l => l.Line).ToArray());
        Assert.AreEqual(5, calc.Lines.Single(l => l.Line == 21).Hits); // max of 0 (class) and 5 (state machine)
        var line20 = calc.Lines.Single(l => l.Line == 20);
        Assert.AreEqual((1, 2, 2), (line20.Hits, line20.BranchesCovered, line20.BranchesTotal)); // branches: max covered / total
        Assert.AreEqual((6, 8), (calc.LinesCovered, calc.LinesValid));
        Assert.AreEqual(0.75, calc.LineRate);
        Assert.AreEqual(1.0, calc.BranchRate);

        var format = report.Files.Single(f => f.RelativePath.EndsWith("Format.cs"));
        Assert.AreEqual(int.MaxValue, format.Lines[0].Hits); // method-line fallback; hits capped at int.MaxValue
        Assert.AreEqual(1.0, format.BranchRate);           // nothing to cover: 100% (Cobertura convention)

        Assert.AreEqual("all", report.Scope);
        Assert.IsNull(report.TestIds);
        Assert.IsNull(report.NoCoverableLines);
        Assert.AreEqual(report.Files.Sum(f => f.LinesValid), report.LinesValid);
        Assert.AreEqual(Math.Round((double)report.LinesCovered / report.LinesValid, 4), report.LineRate);
        CollectionAssert.AreEqual(new[] { "Shop.Tests" }, report.ProjectIds);
    }

    [TestMethod]
    public void Several_reports_add_up_hits_and_keep_the_best_branch_counts()
    {
        using var ws = new Workspace();
        var acc = new CoverageAccumulator();
        acc.AddCobertura(ws.Report("product-a.xml"), TestAssemblies);
        acc.AddCobertura(ws.Report("product-b.xml"), TestAssemblies);
        var report = acc.Build("run-2", ["A", "B"], ["t2", "t1"]);

        var calc = report.Files.Single(f => f.Path == ws.Path("src/Math/Calc.cs"));
        Assert.AreEqual(5, calc.Lines.Single(l => l.Line == 10).Hits);  // 3 + 2
        Assert.AreEqual(5, calc.Lines.Single(l => l.Line == 20).Hits);  // 1 + 4
        Assert.AreEqual(1, calc.Lines.Single(l => l.Line == 31).Hits);  // 0 + 1
        Assert.AreEqual(2, calc.Lines.Single(l => l.Line == 20).BranchesCovered);
        Assert.IsTrue(report.Files.Any(f => f.Module == "Shop.Api" && f.Path == ws.Path("api/Startup.cs"))); // rooted file name
        Assert.AreEqual("subset", report.Scope);
        CollectionAssert.AreEqual(new[] { "t2", "t1" }, report.TestIds);
    }

    [TestMethod]
    public void Excluding_nothing_keeps_the_test_module()
    {
        using var ws = new Workspace();
        var acc = new CoverageAccumulator();
        acc.AddCobertura(ws.Report("product-a.xml"), NoExclusions);
        Assert.AreEqual(5, acc.FileCount);
    }

    [TestMethod]
    public void Relative_file_names_resolve_against_the_first_source_that_has_them()
    {
        using var ws = new Workspace();
        var other = Directory.CreateDirectory(ws.Path("other")).FullName;
        Assert.AreEqual(ws.Path("src/Constants.cs"), CoverageAccumulator.ResolvePath(Path.Combine("src", "Constants.cs"), [other, ws.Root.FullName]));
        // not found anywhere: relative to the first source
        Assert.AreEqual(Path.GetFullPath(Path.Combine(other, "x.cs")), CoverageAccumulator.ResolvePath("x.cs", [other, ws.Root.FullName]));
        // no sources: left as is
        Assert.AreEqual("x.cs", CoverageAccumulator.ResolvePath("x.cs", []));
        Assert.AreEqual(ws.Path("a.cs"), CoverageAccumulator.ResolvePath(ws.Path("a.cs"), []));
    }

    [TestMethod]
    public void A_single_file_is_relative_to_its_own_directory()
    {
        using var ws = new Workspace();
        var xml = $"""
            <coverage><sources /><packages><package name="M"><classes>
              <class name="A" filename="{ws.Path("src/Constants.cs")}"><lines><line number="1" hits="1" branch="False" /></lines></class>
            </classes></package></packages></coverage>
            """;
        var acc = new CoverageAccumulator();
        acc.AddCobertura(ws.Write("one.xml", xml), NoExclusions);
        var single = acc.Build("r", ["p"]).Files.Single();
        Assert.AreEqual("Constants.cs", single.RelativePath); // one file: relative to its own directory
    }

    // ------------------------------------------------------------------ per-test coverage cache

    private static CoverageReport Report(string runId, params string[] testIds) => new()
    {
        RunId = runId,
        GeneratedAt = "now",
        ProjectIds = ["P"],
        Files = [],
        Scope = testIds.Length > 0 ? "subset" : "all",
        TestIds = testIds.Length > 0 ? [.. testIds] : null,
    };

    [TestMethod]
    public void Cache_finds_reports_by_exact_test_set_and_single_tests_by_id()
    {
        var cache = new CoverageCache();
        cache.Store("P", Report("all"), "s0"); // run-all: not cached
        cache.Store("P", Report("r1", "a"), "s1");
        cache.Store("P", Report("r2", "b", "a"), "s2");

        Assert.IsNull(cache.Find("P", ["c"]));
        Assert.AreEqual("r1", cache.Find("P", ["a"])!.Value.Report.RunId);
        Assert.AreEqual("s1", cache.Find("P", ["a"])!.Value.Stamp);
        Assert.AreEqual("r2", cache.Find("P", ["a", "b"])!.Value.Report.RunId); // order does not matter
        Assert.IsNull(cache.Find("Q", ["a"]));
        Assert.AreEqual(CoverageCache.Key("P", ["b", "a", "a"]), CoverageCache.Key("P", ["a", "b"]));

        var singles = cache.SingleTestReports().ToList();
        Assert.HasCount(1, singles);
        Assert.AreEqual(("P", "a", "r1", "s1"), (singles[0].ProjectId, singles[0].TestId, singles[0].Report.RunId, singles[0].Stamp));
    }

    [TestMethod]
    public void Cache_evicts_the_least_recently_used_report_beyond_256()
    {
        var cache = new CoverageCache();
        cache.Store("P", Report("first", "t0"), "s");
        for (var i = 1; i <= 256; i++)
        {
            cache.Store("P", Report($"r{i}", $"t{i}"), "s");
            if (i == 100) Assert.IsNotNull(cache.Find("P", ["t0"])); // touch: t0 is no longer the oldest
        }
        Assert.IsNotNull(cache.Find("P", ["t0"]));
        Assert.IsNull(cache.Find("P", ["t1"])); // the oldest untouched one went
        Assert.HasCount(256, cache.SingleTestReports().ToList());
    }

    [TestMethod]
    public async Task Cached_coverage_is_served_and_marked_stale_when_the_output_changed()
    {
        using var ws = new Workspace();
        var bin = Directory.CreateDirectory(ws.Path("bin")).FullName;
        var dll = Path.Combine(bin, "P.Tests.dll");
        File.WriteAllText(dll, "v1");
        await using var explorer = new Explorer(new EventHub(), [ws.Path("P.Tests.csproj")], null);
        explorer.SetTargetPath("P.Tests", dll);

        // a stamp that is not the current output fingerprint: stale
        lock (explorer.Sync) explorer.CoverageCacheState.Store("P.Tests", Report("r1", "t1") with { ProjectIds = ["P.Tests"] }, "old");
        var hit = explorer.GetCachedTestCoverage(null, ["t1"])!;
        Assert.AreEqual("r1", hit.RunId);
        Assert.IsTrue(hit.Stale);
        Assert.IsNull(explorer.GetCachedTestCoverage("P.Tests", ["nope"]));
        Assert.IsNull(explorer.GetCachedTestCoverage(null, []));

        explorer.SetTargetPath("P.Tests", Path.Combine(bin, "missing.dll"));
        Assert.IsTrue(explorer.GetCachedTestCoverage("P.Tests", ["t1"])!.Stale);
    }

    // ------------------------------------------------------------------ impact map

    [TestMethod]
    public void Impact_map_parses_executed_method_spans_per_file()
    {
        using var ws = new Workspace();
        var spans = ImpactMap.ParseCobertura(ws.Report("product-a.xml"), TestAssemblies);

        var calc = spans[PathUtil.CanonicalizeOrSelf(ws.Path("src/Math/Calc.cs"))];
        // Unused (no hits) is not a span; the state machine class has no methods: its executed lines are one-line spans
        CollectionAssert.AreEquivalent(new[] { new LineSpan(10, 12), new LineSpan(20, 22), new LineSpan(21, 21) }, calc);
        CollectionAssert.AreEqual(new[] { new LineSpan(3, 3) }, spans[PathUtil.CanonicalizeOrSelf(ws.Path("src/Constants.cs"))]);
        Assert.IsTrue(spans.ContainsKey(PathUtil.CanonicalizeOrSelf(ws.Path("src/Text/Format.cs"))));
        Assert.IsFalse(spans.Keys.Any(k => k.EndsWith("Dead.cs") || k.EndsWith("CalcTests.cs")));
    }

    [TestMethod]
    public void Impact_map_selects_tests_by_the_changed_lines()
    {
        using var ws = new Workspace();
        var file = PathUtil.CanonicalizeOrSelf(ws.Path("src/Math/Calc.cs"));
        var map = new ImpactMap();
        Assert.IsNull(map.Info());
        map.Record("P", ["add"], new() { [file] = [new LineSpan(10, 12)] });
        map.Record("P", ["divide1", "divide2"], new() { [file] = [new LineSpan(20, 22)] });
        map.Record("Q", ["other"], new() { [file] = [new LineSpan(1, 40)] });

        Assert.IsNull(map.Affected("P", ws.Path("src/Constants.cs"), out _)); // no test touches it

        Assert.IsEmpty(map.Affected("P", file, out var detail)!);
        Assert.AreEqual("content unchanged", detail);

        var lines = File.ReadAllLines(file);
        lines[20] = "// changed"; // line 21
        File.WriteAllLines(file, lines);
        CollectionAssert.AreEquivalent(new[] { "divide1", "divide2" }, map.Affected("P", file, out detail)!.ToArray());
        Assert.AreEqual("line 21 (class-level data)", detail);
        map.AcceptChange(file);
        Assert.IsEmpty(map.Affected("P", file, out _)!);

        lines[34] = "// outside every executed method"; // line 35
        File.WriteAllLines(file, lines);
        CollectionAssert.AreEquivalent(new[] { "add", "divide1", "divide2" }, map.Affected("P", file, out detail)!.ToArray());
        Assert.Contains("outside executed methods", detail);
        map.AcceptChange(file);

        // two lines inserted above Divide: its span moves down by two
        var inserted = lines.Take(15).Concat(["// new 1", "// new 2"]).Concat(lines.Skip(15)).ToArray();
        File.WriteAllLines(file, inserted);
        Assert.IsTrue(map.Affected("P", file, out detail)!.SetEquals(["add", "divide1", "divide2"]));
        Assert.StartsWith("lines 15–16", detail); // a pure insertion touches both neighbours
        map.AcceptChange(file);
        CollectionAssert.AreEquivalent(new[] { ("P", "divide1"), ("P", "divide2"), ("Q", "other") }, map.TestsCoveringLine(file, 23).ToArray());
        CollectionAssert.AreEquivalent(new[] { ("Q", "other") }, map.TestsCoveringLine(file, 21).ToArray());

        // edit inside Add (lines 10..12): only add
        inserted[10] = "// edited";
        File.WriteAllLines(file, inserted);
        CollectionAssert.AreEqual(new[] { "add" }, map.Affected("P", file, out _)!.ToArray());
    }

    [TestMethod]
    public void Impact_map_without_a_snapshot_or_readable_file_uses_every_test_of_the_file()
    {
        using var ws = new Workspace();
        var missing = ws.Path("src/Gone.cs");
        var map = new ImpactMap();
        map.Record("P", ["a", "b"], new() { [missing] = [new LineSpan(1, 2)] });
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, map.Affected("P", missing, out var detail)!.ToArray());
        Assert.AreEqual("file", detail);
        map.AcceptChange(missing); // nothing to accept: no snapshot
        Assert.AreEqual(1, map.Info()!.Files);

        map.ForgetTests("P", new HashSet<string> { "b" });
        Assert.AreEqual(1, map.Info()!.Tests);
        CollectionAssert.AreEqual(new[] { "b" }, map.Affected("P", missing, out _)!.ToArray());
    }

    [TestMethod]
    public void Impact_map_info_reports_building_and_build_time()
    {
        var map = new ImpactMap { Building = true };
        var info = map.Info()!;
        Assert.IsTrue(info.Building);
        Assert.AreEqual(0, info.Tests);
        Assert.IsNull(info.BuiltAt);
        map.Building = false;
        map.BuiltAt = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        info = map.Info()!;
        Assert.IsNull(info.Building);
        Assert.StartsWith("2026-10-09T08:00:00", info.BuiltAt);
    }

    [TestMethod]
    public void Impact_map_keeps_the_granularity_of_each_test()
    {
        using var ws = new Workspace();
        var file = PathUtil.CanonicalizeOrSelf(ws.Path("src/Math/Calc.cs"));
        var map = new ImpactMap { Mode = ImpactMapModes.Test };
        // a class-level recording (one coverage process ran the whole class): everything is credited to every test
        map.Record("P", ["add", "sub", "add"], new() { [file] = [new LineSpan(10, 12) { Name = "Add" }, new LineSpan(20, 22) { Name = "Sub" }] });
        Assert.IsFalse(map.IsPerTest("P", "sub"));
        Assert.IsTrue(map.Has("P", "sub"));
        Assert.IsFalse(map.Has("P", "nope"));
        Assert.AreEqual("class-level data", map.DataNote("P", "sub"));
        Assert.HasCount(2, map.AffectedByLines("P", file, [new LineSpan(21, 21)], out _)!);

        // refined: one coverage process per test
        map.Record("P", ["sub"], new() { [file] = [new LineSpan(20, 22) { Name = "Sub" }] });
        map.Record("P", ["add"], new() { [file] = [new LineSpan(10, 12) { Name = "Add" }] });
        Assert.IsTrue(map.IsPerTest("P", "sub"));
        Assert.AreEqual("per-test data", map.DataNote("P", "sub"));
        CollectionAssert.AreEqual(new[] { "sub" }, map.AffectedByLines("P", file, [new LineSpan(21, 21)], out _)!.Keys.ToArray());
        var info = map.Info()!;
        Assert.AreEqual((2, 2, "test"), (info.Tests, info.PerTest, info.Mode));

        // a later shared recording (a coverage run of a subset) does not downgrade per-test data …
        map.Record("P", ["add", "sub", "mul"], new() { [file] = [new LineSpan(1, 40)] });
        Assert.IsTrue(map.IsPerTest("P", "sub"));
        Assert.IsFalse(map.IsPerTest("P", "mul"));
        var lines = File.ReadAllLines(file);
        lines[20] = "// changed"; // line 21: Sub (per test) and mul (class level)
        File.WriteAllLines(file, lines);
        CollectionAssert.AreEquivalent(new[] { "sub", "mul" }, map.Affected("P", file, out var detail)!.ToArray());
        Assert.AreEqual("line 21 (per-test and class-level data)", detail);
        map.AcceptChange(file);
        lines[10] = "// changed"; // line 11: Add (per test) and mul
        File.WriteAllLines(file, lines);
        map.ForgetTests("P", new HashSet<string> { "add" });
        CollectionAssert.AreEqual(new[] { "add" }, map.Affected("P", file, out detail)!.ToArray());
        Assert.AreEqual("line 11 (per-test data)", detail);
        Assert.AreEqual(1, map.Info()!.PerTest);

        // … an explicit rebuild does
        map.Record("P", ["add", "x"], new() { [file] = [new LineSpan(1, 40)] }, replacePerTest: true);
        Assert.IsFalse(map.IsPerTest("P", "add"));
        Assert.AreEqual(0, map.Info()!.PerTest);
    }

    [TestMethod]
    public void Impact_map_modes_parse()
    {
        Assert.AreEqual("auto", new ImpactMap().Mode);
        Assert.AreEqual("test", ImpactMapModes.Parse(" Test "));
        Assert.AreEqual("class", ImpactMapModes.Parse("class"));
        Assert.IsNull(ImpactMapModes.Parse("method"));
        Assert.IsNull(ImpactMapModes.Parse(null));
        Assert.AreEqual("test", new Explorer(new EventHub(), [], null) { ImpactMapMode = "TEST" }.ImpactMapMode);
        Assert.AreEqual("auto", new Explorer(new EventHub(), [], null) { ImpactMapMode = "nonsense" }.ImpactMapMode);
    }

    [TestMethod]
    public void The_impact_map_pass_runs_one_process_per_class_or_per_test()
    {
        TestNode T(string id, string? ns, string? cls) => new() { Id = id, ProjectId = "P", DisplayName = id + "()", Namespace = ns, ClassName = cls };
        TestNode[] tests = [T("a", "N", "A"), T("b", "N", "A"), T("c", "M", "A"), T("d", null, null)];
        var byClass = Explorer.ImpactGroups(tests, perTest: false);
        CollectionAssert.AreEqual(new[] { "A", "A", "(no class)" }, byClass.Select(g => g.Label).ToArray()); // N.A and M.A differ
        CollectionAssert.AreEqual(new[] { 2, 1, 1 }, byClass.Select(g => g.Tests.Count).ToArray());
        var byTest = Explorer.ImpactGroups(tests, perTest: true);
        CollectionAssert.AreEqual(new[] { "A.a()", "A.b()", "A.c()", "(no class).d()" }, byTest.Select(g => g.Label).ToArray());
        Assert.IsTrue(byTest.All(g => g.Tests.Count == 1));
    }

    [TestMethod]
    public void Large_files_are_not_snapshotted()
    {
        using var ws = new Workspace();
        var big = ws.Write("src/Big.cs", new string('x', 2 * 1024 * 1024 + 10));
        var map = new ImpactMap();
        map.Record("P", ["a"], new() { [big] = [new LineSpan(1, 1)] });
        var affected = map.Affected("P", big, out var detail);
        Assert.HasCount(1, affected!);
        Assert.AreEqual("file", detail);
    }
}
