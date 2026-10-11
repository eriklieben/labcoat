using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The full coverage report of the Coverage page (FullCoverage.cs): per project the last full run, never replaced by
/// a run of selected tests; subset coverage merged in only into unchanged files; changed files marked stale.
/// </summary>
[TestClass]
public class FullCoverageTests
{
    private static readonly HashSet<string> NoExclusions = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Workspace : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-fullcov-");

        public Workspace()
        {
            Write("src/Calc.cs", string.Join("\n", Enumerable.Range(1, 20).Select(i => $"// calc {i}")));
            Write("src/Format.cs", string.Join("\n", Enumerable.Range(1, 10).Select(i => $"// format {i}")));
            Write("api/Startup.cs", "// startup\n// 2\n// 3");
        }

        public string Path(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root.FullName, relative));

        public string Write(string relative, string text)
        {
            var p = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            System.IO.File.WriteAllText(p, text);
            return p;
        }

        /// <summary>
        /// One Cobertura report: <paramref name="files"/> = (module, relative path, covered lines, valid lines).
        /// </summary>
        public CoverageAccumulator Coverage(params (string Module, string File, int[] Covered, int[] Valid)[] files)
        {
            var packages = string.Join("\n", files.GroupBy(f => f.Module).Select(m => $"""
                <package name="{m.Key}"><classes>
                {string.Join("\n", m.Select(f => $"""
                  <class name="{f.File}" filename="{f.File}"><lines>
                  {string.Join("\n", f.Valid.Select(l => $"""<line number="{l}" hits="{(f.Covered.Contains(l) ? 1 : 0)}" branch="False" />"""))}
                  </lines></class>
                """))}
                </classes></package>
                """));
            var xml = $"""<coverage><sources><source>{Root.FullName}</source></sources><packages>{packages}</packages></coverage>""";
            var acc = new CoverageAccumulator();
            acc.AddCobertura(Write($"reports/{Guid.NewGuid():N}.xml", xml), NoExclusions);
            return acc;
        }

        public void Dispose() => Root.Delete(recursive: true);
    }

    private static readonly int[] CalcLines = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    private static readonly int[] FormatLines = [1, 2, 3, 4];

    private static FileCoverage File(CoverageReport r, string suffix) => r.Files.Single(f => f.Path.EndsWith(suffix, StringComparison.Ordinal));

    [TestMethod]
    public void A_subset_run_after_a_full_run_leaves_the_full_report_as_it_was()
    {
        using var ws = new Workspace();
        var store = new FullCoverage();
        Assert.IsNull(store.Report);

        var full = store.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", [1, 2, 3, 4, 5, 6, 7, 8, 9], CalcLines), ("Core", "src/Format.cs", [1, 2, 3, 4], FormatLines)), "run-full", null);
        Assert.AreEqual(("all", 13, 14), (full.Scope, full.LinesCovered, full.LinesValid));
        Assert.AreEqual("run-full", full.RunId);

        // 4 tests that cover one line (the 7% of the bug report): nothing new, so nothing changes.
        var (result, report) = store.MergeSubset("P", ws.Coverage(("Core", "src/Calc.cs", [1], CalcLines), ("Core", "src/Format.cs", [], FormatLines)), "run-subset", 4);
        Assert.IsNull(report, "no new lines, nothing stale: no update");
        Assert.AreEqual((true, 2, 0), (result.HasFull, result.FilesMerged, result.FilesChanged));
        Assert.IsEmpty(result.ChangedFiles);
        Assert.AreSame(full, store.Report);
        Assert.AreEqual((13, 14, 0.9286), (store.Report!.LinesCovered, store.Report.LinesValid, store.Report.LineRate));
        Assert.IsNull(store.Report.Merged);
        Assert.IsNull(store.Report.StaleFiles);
    }

    [TestMethod]
    public void A_subset_that_covers_more_of_an_unchanged_file_is_merged_without_double_counting()
    {
        using var ws = new Workspace();
        var store = new FullCoverage();
        store.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", [1, 2], CalcLines)), "run-full", null);

        // e.g. a test edited to cover more: lines 2 (already covered) and 3 (new)
        var (result, report) = store.MergeSubset("P", ws.Coverage(("Core", "src/Calc.cs", [2, 3], CalcLines)), "run-subset", 1);
        Assert.IsNotNull(report);
        Assert.AreEqual((1, 1), (result.FilesMerged, result.FilesChanged));
        Assert.AreEqual(("all", "run-subset", 3, 10), (report.Scope, report.RunId, report.LinesCovered, report.LinesValid));
        Assert.AreEqual(1, File(report, "Calc.cs").Lines.Single(l => l.Line == 2).Hits, "max, not the sum");
        var merged = report.Merged!.Single();
        Assert.AreEqual(("run-subset", "P", 1, 1), (merged.RunId, merged.ProjectId, merged.TestCount, merged.FilesChanged));
        Assert.IsNull(report.TestIds);

        // the next full run of the project replaces it all, merged runs included
        report = store.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", [1], CalcLines)), "run-full-2", null);
        Assert.AreEqual(1, report.LinesCovered);
        Assert.IsNull(report.Merged);
    }

    [TestMethod]
    public void A_changed_file_is_not_merged_and_is_marked_stale_until_the_next_full_run()
    {
        using var ws = new Workspace();
        var store = new FullCoverage();
        store.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", [1, 2], CalcLines), ("Core", "src/Format.cs", [1], FormatLines)), "run-full", null);

        ws.Write("src/Calc.cs", "// changed\n" + System.IO.File.ReadAllText(ws.Path("src/Calc.cs")));
        var (result, report) = store.MergeSubset("P",
            ws.Coverage(("Core", "src/Calc.cs", [5, 6, 7, 8], CalcLines), ("Core", "src/Format.cs", [2], FormatLines)), "run-subset", 2);
        Assert.IsNotNull(report);
        CollectionAssert.AreEqual(new[] { ws.Path("src/Calc.cs") }, result.ChangedFiles);
        Assert.AreEqual(1, result.FilesChanged, "Format.cs (unchanged) is merged");
        var calc = File(report, "Calc.cs");
        Assert.AreEqual((2, true), (calc.LinesCovered, calc.Stale), "the changed file keeps the full run's numbers, marked stale");
        Assert.AreEqual((2, (bool?)null), (File(report, "Format.cs").LinesCovered, File(report, "Format.cs").Stale));
        CollectionAssert.AreEqual(new[] { ws.Path("src/Calc.cs") }, report.StaleFiles);

        // nothing else changed: no further update
        Assert.IsNull(store.RefreshStale());

        report = store.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", [1], CalcLines), ("Core", "src/Format.cs", [1], FormatLines)), "run-full-2", null);
        Assert.IsNull(report.StaleFiles);
        Assert.IsNull(File(report, "Calc.cs").Stale);
    }

    [TestMethod]
    public void New_files_and_new_modules()
    {
        using var ws = new Workspace();
        var store = new FullCoverage();
        store.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", [1], CalcLines)), "run-full", null);
        ws.Write("src/New.cs", "// new\n// 2");

        // A file added to a module since the full run: only the subset measured it, so it stays out (listed as changed).
        // A module the full run never loaded: added.
        var (result, report) = store.MergeSubset("P",
            ws.Coverage(("Core", "src/Calc.cs", [1], CalcLines), ("Core", "src/New.cs", [1], [1, 2]), ("Api", "api/Startup.cs", [1], [1, 2, 3])), "run-subset", 1);
        Assert.IsNotNull(report);
        CollectionAssert.AreEqual(new[] { ws.Path("src/New.cs") }, result.ChangedFiles);
        Assert.IsFalse(report.Files.Any(f => f.Path.EndsWith("New.cs", StringComparison.Ordinal)));
        Assert.AreEqual((1, 3), (File(report, "Startup.cs").LinesCovered, File(report, "Startup.cs").LinesValid));
        CollectionAssert.AreEqual(new[] { ws.Path("src/New.cs") }, report.StaleFiles);

        // the added file stays listed when staleness is refreshed (it has no stamp of the full run)
        Assert.IsNull(store.RefreshStale());
        ws.Write("api/Startup.cs", "// changed");
        report = store.RefreshStale();
        Assert.IsNotNull(report);
        CollectionAssert.AreEquivalent(new[] { ws.Path("src/New.cs"), ws.Path("api/Startup.cs") }, report.StaleFiles);
    }

    [TestMethod]
    public void Staleness_follows_the_content_not_the_write_time()
    {
        using var ws = new Workspace();
        var store = new FullCoverage();
        Assert.IsNull(store.RefreshStale(), "no report yet");
        store.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", [1], CalcLines), ("Core", "src/Format.cs", [1], FormatLines)), "run-full", null);
        var original = System.IO.File.ReadAllText(ws.Path("src/Calc.cs"));

        // the same content saved again
        System.IO.File.WriteAllText(ws.Path("src/Calc.cs"), original);
        System.IO.File.SetLastWriteTimeUtc(ws.Path("src/Calc.cs"), DateTime.UtcNow.AddMinutes(5));
        Assert.IsNull(store.RefreshStale());

        ws.Write("src/Calc.cs", original.Replace("calc 3", "calc 9"));   // same length, other content
        var report = store.RefreshStale();
        Assert.IsTrue(File(report!, "Calc.cs").Stale);
        Assert.AreEqual("run-full", report!.RunId, "a staleness update keeps the run id");

        System.IO.File.Delete(ws.Path("src/Format.cs"));
        report = store.RefreshStale();
        Assert.HasCount(2, report!.StaleFiles!);

        ws.Write("src/Calc.cs", original);
        ws.Write("src/Format.cs", string.Join("\n", Enumerable.Range(1, 10).Select(i => $"// format {i}")));
        report = store.RefreshStale();
        Assert.IsNull(report!.StaleFiles, "back to what the full run measured");
    }

    [TestMethod]
    public void Projects_keep_their_own_full_run()
    {
        using var ws = new Workspace();
        var store = new FullCoverage();
        var settingsA = new CoverageSettingsInfo { ProjectId = "A", Source = "defaults", Instrumentation = "dynamic" };
        store.SetFull("A", ws.Coverage(("Core", "src/Calc.cs", [1, 2], CalcLines)), "run-1", settingsA);
        var report = store.SetFull("B", ws.Coverage(("Core", "src/Calc.cs", [3], CalcLines), ("Api", "api/Startup.cs", [1], [1, 2, 3])), "run-2", null);
        CollectionAssert.AreEqual(new[] { "A", "B" }, report.ProjectIds);
        Assert.AreEqual((3, 2), (File(report, "Calc.cs").LinesCovered, report.Files.Count));
        Assert.AreSame(settingsA, report.Settings!.Single());

        // a full run of A alone keeps B's part
        report = store.SetFull("A", ws.Coverage(("Core", "src/Calc.cs", [1], CalcLines)), "run-3", settingsA);
        Assert.AreEqual(2, File(report, "Calc.cs").LinesCovered);
        Assert.AreEqual(1, File(report, "Startup.cs").LinesCovered);
        CollectionAssert.AreEqual(new[] { "A", "B" }, report.ProjectIds);

        // a subset of a project without a full run of its own changes nothing
        var (result, merged) = store.MergeSubset("C", ws.Coverage(("Core", "src/Calc.cs", [9], CalcLines)), "run-4", 1);
        Assert.IsFalse(result.HasFull);
        Assert.IsNull(merged);
        Assert.AreSame(report, store.Report);
    }

    [TestMethod]
    public void Stamps_of_missing_files()
    {
        using var ws = new Workspace();
        var missing = ws.Path("nope.cs");
        Assert.IsNull(FullCoverage.Stamp(missing));
        Assert.IsFalse(FullCoverage.Changed(missing, null), "missing then and now");
        var stamp = FullCoverage.Stamp(ws.Path("src/Calc.cs"));
        Assert.IsTrue(FullCoverage.Changed(missing, stamp), "deleted");
        Assert.IsTrue(FullCoverage.Changed(ws.Path("src/Calc.cs"), null), "appeared");
        Assert.IsFalse(FullCoverage.Changed(ws.Path("src/Calc.cs"), stamp));
    }

    [TestMethod]
    public void Accumulator_add_and_clone_copy_the_lines()
    {
        using var ws = new Workspace();
        var a = ws.Coverage(("Core", "src/Calc.cs", [1], CalcLines));
        var copy = a.Clone();
        copy.Add(ws.Coverage(("Core", "src/Calc.cs", [1, 2], CalcLines)));
        Assert.AreEqual(1, a.Build("r", ["P"]).Files.Single().Lines.Single(l => l.Line == 1).Hits, "the original is untouched");
        Assert.AreEqual(2, copy.Build("r", ["P"]).Files.Single().Lines.Single(l => l.Line == 1).Hits, "hits add up across reports");
        Assert.AreEqual(-1, a.CoveredLines(ws.Path("src/Other.cs")));
    }
}
