using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The full coverage report across restarts (FullCoverageStore.cs, coverage.json): restored with its numbers, files
/// changed while the host was down marked stale, saved only when the report changes, and unreadable files ignored.
/// </summary>
[TestClass]
public class FullCoverageStoreTests
{
    public TestContext TestContext { get; set; }

    private static readonly HashSet<string> NoExclusions = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Workspace : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-covstore-");
        public string Data => System.IO.Path.Combine(Root.FullName, "data");
        public List<string> Log { get; } = [];

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

        public FullCoverageStore Store(long maxBytes = 128L * 1024 * 1024, IProjectKeys? keys = null) =>
            new(Data, (level, msg) => { lock (Log) Log.Add($"{level}: {msg}"); }) { MaxBytes = maxBytes, Keys = keys };

        public string FilePath => System.IO.Path.Combine(Data, "coverage.json");

        /// <summary>
        /// One Cobertura report: (module, relative path, lines as (number, hits, "covered/total" or null)).
        /// </summary>
        public CoverageAccumulator Coverage(params (string Module, string File, (int Line, int Hits, string? Branch)[] Lines)[] files)
        {
            var packages = string.Join("\n", files.GroupBy(f => f.Module).Select(m => $"""
                <package name="{m.Key}"><classes>
                {string.Join("\n", m.Select(f => $"""
                  <class name="{f.File}" filename="{f.File}"><lines>
                  {string.Join("\n", f.Lines.Select(l => l.Branch is null
                      ? $"""<line number="{l.Line}" hits="{l.Hits}" branch="False" />"""
                      : $"""<line number="{l.Line}" hits="{l.Hits}" branch="True" condition-coverage="50% ({l.Branch})" />"""))}
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

    private static readonly (int, int, string?)[] Calc = [(1, 3, null), (2, 1, "1/2"), (3, 0, "0/4"), (7, 0, null), (12, 5, null)];
    private static readonly (int, int, string?)[] Format = [(1, 1, null), (2, 0, null)];

    private static FileCoverage File(CoverageReport r, string suffix) => r.Files.Single(f => f.Path.EndsWith(suffix, StringComparison.Ordinal));

    private static string Shape(CoverageReport r) =>
        string.Join("\n", r.Files.Select(f => $"{f.RelativePath} {f.Module} {f.LinesCovered}/{f.LinesValid} {f.BranchesCovered}/{f.BranchesValid} " +
                                              string.Join(",", f.Lines.Select(l => $"{l.Line}:{l.Hits}:{l.BranchesCovered}/{l.BranchesTotal}"))));

    [TestMethod]
    public void The_report_survives_a_restart_with_the_same_numbers()
    {
        using var ws = new Workspace();
        var settings = new CoverageSettingsInfo { ProjectId = "P", Source = "file", Path = ws.Path("cov.xml"), Origin = "convention", Instrumentation = "dynamic", Exclude = ["**/Gen/*.cs"] };
        CoverageReport before;
        using (var store = ws.Store())
        {
            var coverage = new FullCoverage(store);
            Assert.IsNull(coverage.Report, "nothing persisted yet");
            coverage.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", Calc), ("Core", "src/Format.cs", Format)), "run-1", settings);
            coverage.SetFull("Q", ws.Coverage(("Api", "api/Startup.cs", [(1, 2, null), (2, 0, null)])), "run-2", null);
            // a subset run that covers more of an unchanged file: merged, and listed in `merged`
            var (_, merged) = coverage.MergeSubset("P", ws.Coverage(("Core", "src/Format.cs", [(2, 4, null)])), "run-3", 1);
            before = merged!;
        } // Dispose writes the pending save

        Assert.IsTrue(System.IO.File.Exists(ws.FilePath));
        Assert.IsFalse(System.IO.File.Exists(ws.FilePath + ".tmp"));
        var json = JsonNode.Parse(System.IO.File.ReadAllText(ws.FilePath))!;
        Assert.AreEqual(FullCoverageStore.FileVersion, (int)json["version"]!);
        var calcEntry = json["parts"]![0]!["files"]!.AsArray().Single(f => ((string)f!["path"]!).EndsWith("Calc.cs", StringComparison.Ordinal))!;
        Assert.AreEqual("[1,1,1,4,5]", calcEntry["lines"]!.ToJsonString(), "line numbers delta-encoded");
        Assert.AreEqual(64, ((string)calcEntry["sha256"]!).Length);

        using var reloaded = ws.Store();
        var restored = new FullCoverage(reloaded).Report!;
        Assert.AreEqual(Shape(before), Shape(restored));
        Assert.AreEqual((before.LinesCovered, before.LinesValid, before.BranchesCovered, before.BranchesValid, before.LineRate, before.BranchRate),
            (restored.LinesCovered, restored.LinesValid, restored.BranchesCovered, restored.BranchesValid, restored.LineRate, restored.BranchRate));
        Assert.AreEqual((before.RunId, before.GeneratedAt), (restored.RunId, restored.GeneratedAt));
        CollectionAssert.AreEqual(new[] { "P", "Q" }, restored.ProjectIds);
        Assert.AreEqual(restored.GeneratedAt, restored.RestoredAt, "everything is from before the restart");
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(settings, Json.Options), System.Text.Json.JsonSerializer.Serialize(restored.Settings!.Single(), Json.Options));
        Assert.AreEqual(("run-3", "P", 1, 1), (restored.Merged!.Single().RunId, restored.Merged![0].ProjectId, restored.Merged[0].TestCount, restored.Merged[0].FilesChanged));
        Assert.IsNull(restored.StaleFiles, "nothing changed while the host was down");
        Assert.IsTrue(ws.Log.Any(l => l.StartsWith("info: Restored the coverage report of P, Q (3 files", StringComparison.Ordinal)), string.Join("\n", ws.Log));
    }

    /// <summary>Project ids ↔ relative paths, as <see cref="HistoryStore"/> knows them.</summary>
    private sealed class Keys(params (string Id, string Path)[] projects) : IProjectKeys
    {
        public string? ProjectPathOf(string projectId) => projects.Where(p => p.Id == projectId).Select(p => p.Path).FirstOrDefault();
        public string? ProjectIdOf(string relativePath) => projects.Where(p => p.Path == relativePath).Select(p => p.Id).FirstOrDefault();
    }

    [TestMethod]
    public void Parts_are_kept_by_project_path_so_a_changed_id_keeps_its_coverage()
    {
        using var ws = new Workspace();
        using (var store = ws.Store(keys: new Keys(("X", "a/X.csproj"), ("X-2", "b/X.csproj"))))
        {
            var coverage = new FullCoverage(store);
            coverage.SetFull("X", ws.Coverage(("Core", "src/Calc.cs", Calc)), "run-1", null);
            coverage.SetFull("X-2", ws.Coverage(("Api", "api/Startup.cs", [(1, 1, null)])), "run-2", null);
            coverage.MergeSubset("X-2", ws.Coverage(("Api", "api/Startup.cs", [(2, 1, null)])), "run-3", 1);
        }
        var json = JsonNode.Parse(System.IO.File.ReadAllText(ws.FilePath))!;
        CollectionAssert.AreEqual(new[] { "a/X.csproj", "b/X.csproj" }, json["parts"]!.AsArray().Select(p => (string?)p!["projectPath"]).ToArray());

        // b is X now, a X-2: the parts follow the paths, the merged run too
        using (var store = ws.Store(keys: new Keys(("X", "b/X.csproj"), ("X-2", "a/X.csproj"))))
        {
            var report = new FullCoverage(store).Report!;
            CollectionAssert.AreEqual(new[] { "X-2", "X" }, report.ProjectIds);
            StringAssert.EndsWith(report.Files.Single(f => f.Module == "Core").Path, "Calc.cs");
            Assert.AreEqual("X", report.Merged!.Single().ProjectId);
        }

        // a is gone: its part is kept under its path (and written back with it) ...
        using (var store = ws.Store(keys: new Keys(("X", "b/X.csproj"))))
        {
            var coverage = new FullCoverage(store);
            CollectionAssert.AreEqual(new[] { "a/X.csproj", "X" }, coverage.Report!.ProjectIds);
            // ... until it comes back while running
            Assert.IsNull(coverage.RenameProject("unknown", "Y"));
            Assert.IsNull(coverage.RenameProject("X", "X"));
            CollectionAssert.AreEqual(new[] { "X-2", "X" }, coverage.RenameProject("a/X.csproj", "X-2")!.ProjectIds);
            CollectionAssert.AreEqual(new[] { "X-2", "b/X.csproj" }, coverage.RenameProject("X", "b/X.csproj")!.ProjectIds, "b removed: its path");
            Assert.AreEqual("b/X.csproj", coverage.Report!.Merged!.Single().ProjectId);
            CollectionAssert.AreEqual(new[] { "X-2" }, coverage.RenameProject("b/X.csproj", "X-2")!.ProjectIds, "the part already under the new key is kept");
        }
    }

    [TestMethod]
    public void A_removed_projects_part_ages_out_and_parts_written_before_paths_keep_their_id()
    {
        using var ws = new Workspace();
        using (var store = ws.Store())
        {
            var coverage = new FullCoverage(store);
            coverage.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", Calc)), "run-1", null);
            coverage.SetFull("Q", ws.Coverage(("Api", "api/Startup.cs", [(1, 1, null)])), "run-2", null);
        }
        var json = JsonNode.Parse(System.IO.File.ReadAllText(ws.FilePath))!;
        Assert.IsNull(json["parts"]![0]!["projectPath"], "without keys: no paths (as before)");

        // an earlier file: the parts keep their ids
        using (var store = ws.Store(keys: new Keys(("P", "p/P.csproj"), ("Q", "q/Q.csproj"))))
        {
            var coverage = new FullCoverage(store);
            CollectionAssert.AreEqual(new[] { "P", "Q" }, coverage.Report!.ProjectIds);
            coverage.RenameProject("Q", "q/Q.csproj"); // Q removed while running: saved under its path
        }
        json = JsonNode.Parse(System.IO.File.ReadAllText(ws.FilePath))!;
        Assert.AreEqual("q/Q.csproj", (string?)json["parts"]![1]!["projectPath"]);

        // Q's full run is older than the retention: dropped at the next start without it
        json["parts"]![1]!["generatedAt"] = DateTimeOffset.UtcNow.AddDays(-HistoryStore.OrphanRetention.TotalDays - 1).ToString("o");
        System.IO.File.WriteAllText(ws.FilePath, json.ToJsonString());
        using var later = ws.Store(keys: new Keys(("P", "p/P.csproj")));
        CollectionAssert.AreEqual(new[] { "P" }, new FullCoverage(later).Report!.ProjectIds);
    }

    [TestMethod]
    public void Files_changed_while_the_host_was_down_are_stale_and_a_new_full_run_replaces_the_restored_part()
    {
        using var ws = new Workspace();
        using (var store = ws.Store())
        {
            var coverage = new FullCoverage(store);
            coverage.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", Calc), ("Core", "src/Format.cs", Format)), "run-1", null);
            ws.Write("src/New.cs", "// new");
            // a file added to the module since the full run: stale, and remembered across the restart
            coverage.MergeSubset("P", ws.Coverage(("Core", "src/New.cs", [(1, 1, null)])), "run-2", 1);
            coverage.SetFull("Q", ws.Coverage(("Api", "api/Startup.cs", [(1, 1, null)])), "run-3", null);
        }
        ws.Write("src/Calc.cs", "// edited while Labcoat was not running");
        System.IO.File.Delete(ws.Path("api/Startup.cs"));

        using var reloaded = ws.Store();
        var coverage2 = new FullCoverage(reloaded);
        var report = coverage2.Report!;
        CollectionAssert.AreEquivalent(new[] { ws.Path("src/Calc.cs"), ws.Path("src/New.cs"), ws.Path("api/Startup.cs") }, report.StaleFiles);
        Assert.IsTrue(File(report, "Calc.cs").Stale);
        Assert.IsNull(File(report, "Format.cs").Stale);
        Assert.AreEqual((3, 5), (File(report, "Calc.cs").LinesCovered, File(report, "Calc.cs").LinesValid), "the numbers stay those of the full run");
        Assert.IsNull(coverage2.RefreshStale(), "the startup check already found them");

        // P runs all its tests again: its part is new, Q's is still from before the restart
        var restoredAt = report.RestoredAt;
        ws.Write("api/Startup.cs", "// back");
        report = coverage2.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", [(1, 1, null)])), "run-4", null);
        Assert.IsNotNull(restoredAt);
        Assert.AreEqual(restoredAt, report.RestoredAt);
        Assert.AreNotEqual(report.GeneratedAt, report.RestoredAt);
        CollectionAssert.AreEqual(new[] { ws.Path("api/Startup.cs") }, report.StaleFiles, "Q's file is still stale (the content differs)");
        report = coverage2.SetFull("Q", ws.Coverage(("Api", "api/Startup.cs", [(1, 1, null)])), "run-5", null);
        Assert.IsNull(report.RestoredAt);
        Assert.IsNull(report.StaleFiles);
    }

    [TestMethod]
    public void Saved_only_when_the_report_changes()
    {
        using var ws = new Workspace();
        using var store = ws.Store();
        var coverage = new FullCoverage(store);
        store.Save();
        Assert.IsFalse(System.IO.File.Exists(ws.FilePath), "no report: nothing to save");
        coverage.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", Calc)), "run-1", null);
        store.Save();
        Assert.IsTrue(System.IO.File.Exists(ws.FilePath));

        System.IO.File.Delete(ws.FilePath);
        // a subset run that adds nothing, a staleness change and a refresh: the report itself did not change
        coverage.MergeSubset("P", ws.Coverage(("Core", "src/Calc.cs", [(1, 1, null)])), "run-2", 1);
        ws.Write("src/Calc.cs", "// changed");
        Assert.IsNotNull(coverage.RefreshStale());
        store.Save();
        Assert.IsFalse(System.IO.File.Exists(ws.FilePath));

        // a subset run that adds lines does
        coverage.MergeSubset("P", ws.Coverage(("Api", "api/Startup.cs", [(1, 1, null)])), "run-3", 1);
        store.Save();
        Assert.IsTrue(System.IO.File.Exists(ws.FilePath));
    }

    [TestMethod]
    public async Task The_debounced_save_writes_after_the_delay()
    {
        using var ws = new Workspace();
        using var store = ws.Store();
        var coverage = new FullCoverage(store);
        coverage.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", Calc)), "run-1", null);
        var deadline = DateTime.UtcNow + FullCoverageStore.SaveDelay + TimeSpan.FromSeconds(10);
        while (!System.IO.File.Exists(ws.FilePath) && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.CancellationToken);
        Assert.IsTrue(System.IO.File.Exists(ws.FilePath));
    }

    [TestMethod]
    [DataRow("{ not json", "Could not read the coverage report")]
    [DataRow("""{"version":99,"parts":[]}""", "has version 99")]
    [DataRow("""{"version":1,"parts":[{"projectId":"P","modules":["M"],"files":[{"path":"/x.cs","module":0,"lines":[1,1],"hits":[1]}]}]}""", "the line columns differ in length")]
    [DataRow("""{"version":1,"parts":[{"projectId":"P","modules":[],"files":[{"path":"/x.cs","module":0,"lines":[1],"hits":[1]}]}]}""", "unknown module 0")]
    [DataRow("""{"version":1,"parts":[{"projectId":"","modules":[],"files":[]}]}""", "without a project id")]
    [DataRow("""{"version":1,"parts":[{"projectId":"P","modules":["M"],"files":[{"path":"/x.cs","module":0,"lines":[1],"hits":[1],"branchesCovered":[1]}]}]}""", "differ in length")]
    public void An_unreadable_file_is_ignored_with_a_warning(string content, string expected)
    {
        using var ws = new Workspace();
        Directory.CreateDirectory(ws.Data);
        System.IO.File.WriteAllText(ws.FilePath, content);
        using var store = ws.Store();
        var coverage = new FullCoverage(store);
        Assert.IsNull(coverage.Report);
        Assert.IsTrue(ws.Log.Any(l => l.StartsWith("warning: ", StringComparison.Ordinal) && l.Contains(expected, StringComparison.Ordinal)), string.Join("\n", ws.Log));

        // the next full run overwrites it
        coverage.SetFull("P", ws.Coverage(("Core", "src/Calc.cs", Calc)), "run-1", null);
        store.Save();
        using var reloaded = ws.Store();
        Assert.AreEqual(5, new FullCoverage(reloaded).Report!.LinesValid);
    }

    [TestMethod]
    public void An_empty_file_or_one_without_parts_restores_nothing()
    {
        using var ws = new Workspace();
        Directory.CreateDirectory(ws.Data);
        System.IO.File.WriteAllText(ws.FilePath, "null");
        using (var store = ws.Store()) Assert.IsNull(new FullCoverage(store).Report);
        System.IO.File.WriteAllText(ws.FilePath, """{"version":1}""");
        using (var store = ws.Store()) Assert.IsNull(new FullCoverage(store).Report);
        Assert.IsFalse(ws.Log.Any(l => l.StartsWith("warning", StringComparison.Ordinal)), string.Join("\n", ws.Log));
    }

    [TestMethod]
    public void The_size_is_capped()
    {
        using var ws = new Workspace();
        var big = Enumerable.Range(1, 5000).Select(i => (i, 1, (string?)null)).ToArray();
        ws.Write("src/Big.cs", string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"// {i}")));
        using (var store = ws.Store(maxBytes: 4 * 1024))
        {
            new FullCoverage(store).SetFull("P", ws.Coverage(("Core", "src/Big.cs", big)), "run-1", null);
            store.Save();
        }
        Assert.IsFalse(System.IO.File.Exists(ws.FilePath), "over the cap: not saved");
        Assert.IsFalse(System.IO.File.Exists(ws.FilePath + ".tmp"), "the temp file is removed");
        Assert.IsTrue(ws.Log.Any(l => l.StartsWith("warning: Could not save the coverage report", StringComparison.Ordinal) && l.Contains("larger than")), string.Join("\n", ws.Log));

        using (var store = ws.Store())
        {
            new FullCoverage(store).SetFull("P", ws.Coverage(("Core", "src/Big.cs", big)), "run-1", null);
            store.Save();
        }
        Assert.IsTrue(System.IO.File.Exists(ws.FilePath));
        ws.Log.Clear();
        using (var store = ws.Store(maxBytes: 4 * 1024)) Assert.IsNull(new FullCoverage(store).Report);
        Assert.IsTrue(ws.Log.Any(l => l.StartsWith("warning: Not restoring the coverage report", StringComparison.Ordinal)), string.Join("\n", ws.Log));
    }

    [TestMethod]
    public void A_file_that_could_not_be_read_at_the_full_run_is_stale_once_it_exists()
    {
        using var ws = new Workspace();
        using (var store = ws.Store())
        {
            // Gone.cs is in the report but does not exist: no stamp
            new FullCoverage(store).SetFull("P", ws.Coverage(("Core", "src/Calc.cs", Calc), ("Core", "src/Gone.cs", [(1, 1, null)])), "run-1", null);
        }
        Assert.IsFalse(JsonNode.Parse(System.IO.File.ReadAllText(ws.FilePath))!["parts"]![0]!["files"]!.AsArray()
            .Single(f => ((string)f!["path"]!).EndsWith("Gone.cs", StringComparison.Ordinal))!.AsObject().ContainsKey("sha256"));
        using (var store = ws.Store()) Assert.IsNull(new FullCoverage(store).Report!.StaleFiles, "still missing: unchanged");
        ws.Write("src/Gone.cs", "// here now");
        using (var store = ws.Store()) CollectionAssert.AreEqual(new[] { ws.Path("src/Gone.cs") }, new FullCoverage(store).Report!.StaleFiles);
    }
}
