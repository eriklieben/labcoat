using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>HistoryStore (history.json) and WorkspaceSettings (settings.json).</summary>
[TestClass]
public class PersistenceTests
{
    private static TestNode Node(string id, Outcome outcome, string project = "P") => new()
    {
        Id = id, ProjectId = project, DisplayName = id, Outcome = outcome, DurationMs = 1.5, LastRunAt = "2026-10-09T08:00:00Z",
    };

    private static RunSummary Run(string id) => new() { RunId = id, StartedAt = "2026-10-09T08:00:00Z", Total = 1 };

    /// <summary>A store for a workspace of its own (the file name is a hash of the project paths).</summary>
    private static HistoryStore Store(string workspace, List<string>? log = null) =>
        new([Path.Combine(workspace, "P.Tests.csproj")], (level, msg) => log?.Add($"{level}: {msg}"));

    [TestMethod]
    public void History_survives_a_restart_and_marks_restored_results_stale()
    {
        var ws = Directory.CreateTempSubdirectory("labcoat-hist-").FullName;
        try
        {
            var failed = Node("b", Outcome.Failed) with { ErrorMessage = new string('e', 3000), StackTrace = new string('s', 5000), StdOut = new string('o', 70_000) };
            using (var store = Store(ws))
            {
                var changed = store.Record(Run("r1"), [Node("a", Outcome.Passed), failed, Node("c", Outcome.NotRun), Node("d", Outcome.Cancelled)]);
                CollectionAssert.AreEqual(new[] { "a", "b" }, changed.Select(n => n.Id).ToArray()); // cancelled / not run: no history entry
                Assert.AreEqual(Outcome.Passed, changed[0].History!.Single().Outcome);
                Assert.AreEqual("r1", changed[0].History![0].RunId);

                var run = store.GetRun("r1")!;
                CollectionAssert.AreEqual(new[] { "a", "b", "d" }, run.Results.Select(r => r.Id).ToArray());
                var stored = run.Results.Single(r => r.Id == "b");
                Assert.EndsWith("… (truncated)", stored.ErrorMessage);
                Assert.AreEqual(2048 + "\n… (truncated)".Length, stored.ErrorMessage!.Length);
                Assert.AreEqual(4096 + "\n… (truncated)".Length, stored.StackTrace!.Length);
                Assert.IsNull(run.Results.Single(r => r.Id == "a").ErrorMessage);
            }

            var log = new List<string>();
            using var reloaded = Store(ws, log);
            Assert.IsTrue(log.Any(l => l.StartsWith("info: Loaded test history for 2 tests and 1 runs")), string.Join("\n", log));
            CollectionAssert.AreEqual(new[] { "r1" }, reloaded.Runs().Select(r => r.RunId).ToArray());

            var restored = Node("b", Outcome.NotRun) with { DurationMs = null, LastRunAt = null };
            reloaded.Restore(restored);
            Assert.AreEqual(Outcome.Failed, restored.Outcome);
            Assert.IsTrue(restored.Stale);
            Assert.AreEqual(1.5, restored.DurationMs);
            Assert.AreEqual(64 * 1024 + "\n… (truncated)".Length, restored.StdOut!.Length);
            Assert.HasCount(1, restored.History!);

            var fresh = Node("a", Outcome.Passed);
            reloaded.Restore(fresh); // already has a result: only the history is attached
            Assert.IsNull(fresh.Stale);
            Assert.HasCount(1, fresh.History!);

            var unknown = Node("zzz", Outcome.NotRun);
            reloaded.Restore(unknown);
            Assert.IsNull(unknown.History);
            Assert.IsNull(reloaded.GetRun("nope"));
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [TestMethod]
    public void History_is_capped_per_test_and_runs_are_capped_overall()
    {
        var ws = Directory.CreateTempSubdirectory("labcoat-hist-").FullName;
        try
        {
            using var store = Store(ws);
            TestNode? last = null;
            for (var i = 0; i < HistoryStore.MaxStoredRuns + 5; i++)
                last = store.Record(Run($"r{i}"), [Node("a", i % 2 == 0 ? Outcome.Passed : Outcome.Failed)]).Single();
            Assert.HasCount(HistoryStore.MaxRuns, last!.History!);
            Assert.AreEqual($"r{HistoryStore.MaxStoredRuns + 4}", last.History![^1].RunId);
            Assert.IsTrue(last.Flaky);
            var runs = store.Runs();
            Assert.HasCount(HistoryStore.MaxStoredRuns, runs);
            Assert.AreEqual($"r{HistoryStore.MaxStoredRuns + 4}", runs[0].RunId); // newest first
            Assert.IsNull(store.GetRun("r0"));

            // The same run recorded twice: no second entry.
            Assert.IsEmpty(store.Record(Run($"r{HistoryStore.MaxStoredRuns + 4}"), [Node("a", Outcome.Passed)]));
            Assert.HasCount(HistoryStore.MaxStoredRuns, store.Runs());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [TestMethod]
    public void Repeat_runs_with_mixed_results_mark_the_entry_flaky()
    {
        var ws = Directory.CreateTempSubdirectory("labcoat-hist-").FullName;
        try
        {
            using var store = Store(ws);
            var changed = store.Record(Run("rep"), [Node("a", Outcome.Failed), Node("b", Outcome.Passed)], new HashSet<(string, string)> { ("P", "a") });
            Assert.IsTrue(changed.Single(n => n.Id == "a").History![0].Flaky);
            Assert.IsTrue(changed.Single(n => n.Id == "a").Flaky);
            Assert.IsNull(changed.Single(n => n.Id == "b").History![0].Flaky);
            Assert.IsNull(changed.Single(n => n.Id == "b").Flaky);
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [TestMethod]
    public void A_corrupt_history_file_is_a_warning_and_a_failed_save_too()
    {
        var ws = Directory.CreateTempSubdirectory("labcoat-hist-").FullName;
        try
        {
            string file;
            using (var probe = Store(ws)) file = probe.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{ not json");
            var log = new List<string>();
            using var store = Store(ws, log);
            Assert.IsTrue(log.Any(l => l.StartsWith("warning: Could not read test history")), string.Join("\n", log));
            Assert.IsEmpty(store.Runs());

            store.Save(); // nothing changed: nothing written
            Assert.AreEqual("{ not json", File.ReadAllText(file));

            Directory.CreateDirectory(file + ".tmp"); // the temp file can't be written
            store.Record(Run("r1"), [Node("a", Outcome.Passed)]);
            store.Save();
            Assert.IsTrue(log.Any(l => l.StartsWith("warning: Could not save test history")), string.Join("\n", log));
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [TestMethod]
    public void The_compact_file_round_trips_everything_the_api_returns()
    {
        var ws = Directory.CreateTempSubdirectory("labcoat-hist-").FullName;
        try
        {
            List<(string Id, List<HistoryEntry>? History, bool? Flaky)> before;
            List<RunDetails> runsBefore;
            string file;
            using (var store = Store(ws))
            {
                file = store.FilePath;
                for (var r = 0; r < 4; r++)
                {
                    var failed = Node("b", r % 2 == 0 ? Outcome.Failed : Outcome.Passed) with
                    {
                        ErrorMessage = r % 2 == 0 ? "boom \"quoted\" + ü" : null, StackTrace = r == 0 ? "at X()" : null, StdErr = "err",
                    };
                    store.Record(Run($"r{r}"),
                    [
                        Node("a", Outcome.Passed) with { DurationMs = r == 1 ? null : 0.1 + r },
                        failed,
                        Node("c", Outcome.Cancelled), // in the run, not in the per-test history
                        Node("d", Outcome.TimedOut, project: "Q|x") with { ErrorMessage = "slow", LastRunAt = null },
                        Node("e", Outcome.Skipped),
                    ], r == 2 ? new HashSet<(string, string)> { ("P", "a") } : null);
                }
                store.Record(Run("empty"), [Node("x", Outcome.NotRun)]); // nothing finished: no run stored
                runsBefore = store.Runs().Select(r => store.GetRun(r.RunId)!).ToList();
                before = new[] { "a", "b", "e" }.Select(id => { var n = Node(id, Outcome.NotRun); store.Restore(n); return (id, n.History, n.Flaky); }).ToList();
                var q = Node("d", Outcome.NotRun, project: "Q|x");
                store.Restore(q);
                Assert.IsNotNull(q.History![0].At); // LastRunAt was null: stamped with the record time
            }
            Assert.IsFalse(File.Exists(file + ".tmp"));
            var text = File.ReadAllText(file);
            Assert.StartsWith("{\"version\":2,", text);
            Assert.Contains("\"outcomes\":\"pppp\"", text); // a's history: one character per entry

            using var reloaded = Store(ws);
            CollectionAssert.AreEqual(runsBefore.Select(r => r.Run.RunId).ToList(), reloaded.Runs().Select(r => r.RunId).ToList());
            foreach (var run in runsBefore)
            {
                var again = reloaded.GetRun(run.Run.RunId)!;
                Assert.AreEqual(Json(run), Json(again));
            }
            foreach (var (id, history, flaky) in before)
            {
                var n = Node(id, Outcome.NotRun);
                reloaded.Restore(n);
                Assert.AreEqual(Json(history), Json(n.History), id);
                Assert.AreEqual(flaky, n.Flaky, id);
            }
            var b = Node("b", Outcome.NotRun);
            reloaded.Restore(b);
            Assert.AreEqual("err", b.StdErr);
            Assert.IsNull(reloaded.GetRun("r1")!.Results.Single(r => r.Id == "a").DurationMs);
            Assert.AreEqual(Outcome.Cancelled, reloaded.GetRun("r3")!.Results.Single(r => r.Id == "c").Outcome);
            Assert.AreEqual("Q|x", reloaded.GetRun("r3")!.Results.Single(r => r.Id == "d").ProjectId);
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, Labcoat.Json.Options);

    [TestMethod]
    public void A_version_1_file_is_read_and_rewritten_as_version_2()
    {
        var ws = Directory.CreateTempSubdirectory("labcoat-hist-").FullName;
        try
        {
            string file;
            using (var probe = Store(ws)) file = probe.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, """
                {"version":1,"projects":[],
                 "tests":{"P|a|b":{"runs":[{"outcome":"passed","durationMs":1.5,"at":"t1","runId":"r1"},{"outcome":"failed","at":"t2","runId":"r2","flaky":true}],
                                   "last":{"outcome":"failed","errorMessage":"boom","lastRunAt":"t2"}},
                          "no-separator":{"runs":[]}},
                 "runs":[{"run":{"runId":"r2","startedAt":"s","total":1},"results":[{"projectId":"P","id":"a|b","outcome":"failed","errorMessage":"boom"}]},
                         {"results":[]}]}
                """);
            var log = new List<string>();
            using (var store = Store(ws, log))
            {
                Assert.IsTrue(log.Any(l => l.StartsWith("info: Loaded test history for 1 tests and 1 runs")), string.Join("\n", log));
                var n = Node("a|b", Outcome.NotRun);
                store.Restore(n);
                Assert.AreEqual(Outcome.Failed, n.Outcome);
                Assert.HasCount(2, n.History!);
                Assert.IsTrue(n.History![1].Flaky);
                Assert.IsNull(n.History[1].DurationMs);
                Assert.AreEqual("boom", store.GetRun("r2")!.Results.Single().ErrorMessage);
                store.Record(Run("r3"), [Node("a|b", Outcome.Passed)]);
            }
            Assert.StartsWith("{\"version\":2,", File.ReadAllText(file));
            using var reloaded = Store(ws);
            var again = Node("a|b", Outcome.NotRun);
            reloaded.Restore(again);
            CollectionAssert.AreEqual(new[] { "r1", "r2", "r3" }, again.History!.Select(h => h.RunId).ToArray());
            Assert.IsTrue(again.History![1].Flaky);
            CollectionAssert.AreEqual(new[] { "r3", "r2" }, reloaded.Runs().Select(r => r.RunId).ToArray());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [TestMethod]
    [DataRow("""{"version":2,"projectIds":["P"],"history":[{"project":0,"id":"a","outcomes":"z"}]}""", DisplayName = "unknown outcome code")]
    [DataRow("""{"version":2,"projectIds":["P"],"history":[{"project":3,"id":"a","outcomes":""}]}""", DisplayName = "project index out of range")]
    [DataRow("""{"version":2,"projectIds":["P"],"pastRuns":[{"run":{"runId":"r","startedAt":"s"},"tests":[0],"outcomes":"pp"}]}""", DisplayName = "columns of different lengths")]
    public void A_damaged_version_2_file_is_a_warning(string json)
    {
        var ws = Directory.CreateTempSubdirectory("labcoat-hist-").FullName;
        try
        {
            string file;
            using (var probe = Store(ws)) file = probe.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, json);
            var log = new List<string>();
            using var store = Store(ws, log);
            Assert.IsTrue(log.Any(l => l.StartsWith("warning: Could not read test history")), string.Join("\n", log));
            Assert.IsEmpty(store.Runs());
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [TestMethod]
    public void A_large_history_is_streamed_and_compact()
    {
        var ws = Directory.CreateTempSubdirectory("labcoat-hist-").FullName;
        try
        {
            const int Tests = 5000;
            string file;
            using (var store = Store(ws))
            {
                file = store.FilePath;
                for (var r = 0; r < 25; r++)
                    store.Record(Run($"run-{r:D4}-{Guid.NewGuid()}"),
                        Enumerable.Range(0, Tests).Select(i => Node($"Namespace.Class{i / 50}.Method{i}", Outcome.Passed) with { DurationMs = i % 97 / 10.0, LastRunAt = $"2026-10-09T08:{r:D2}:00.1234567+00:00" }).ToList());
            }
            // version 1 wrote ~115 bytes per past-run result alone; version 2 stays well below that for everything
            var bytesPerResult = new FileInfo(file).Length / (25.0 * Tests);
            Assert.IsLessThan(80, bytesPerResult, $"{bytesPerResult:F1} bytes per result");
            using var reloaded = Store(ws);
            Assert.HasCount(25, reloaded.Runs());
            Assert.HasCount(Tests, reloaded.GetRun(reloaded.Runs()[0].RunId)!.Results);
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    [TestMethod]
    public void Carry_over_keeps_history_metadata_of_the_old_node()
    {
        var old = Node("a", Outcome.Passed) with { History = [new(Outcome.Passed, 1, "t")], Flaky = true, Stale = true };
        var now = Node("a", Outcome.NotRun);
        HistoryStore.CarryOver(old, now);
        Assert.AreSame(old.History, now.History);
        Assert.IsTrue(now.Flaky);
        Assert.IsTrue(now.Stale);
    }

    [TestMethod]
    [DataRow("PPPPP", false, DisplayName = "always passing")]
    [DataRow("PPFFF", false, DisplayName = "one regression")]
    [DataRow("PFP", true, DisplayName = "two flips")]
    [DataRow("PSFSP", true, DisplayName = "skips are ignored")]
    [DataRow("PTP", true, DisplayName = "timeouts count as failures")]
    [DataRow("PFPFFFFFFFFFFF", false, DisplayName = "old flips outside the window")]
    [DataRow("", false, DisplayName = "no runs")]
    public void Flaky_needs_two_pass_fail_transitions_in_the_last_ten_runs(string pattern, bool flaky)
    {
        var runs = pattern.Select(c => new HistoryEntry(c switch
        {
            'P' => Outcome.Passed,
            'F' => Outcome.Failed,
            'T' => Outcome.TimedOut,
            _ => Outcome.Skipped,
        }, 1, "t")).ToList();
        Assert.AreEqual(flaky, HistoryStore.IsFlaky(runs));
    }

    // ------------------------------------------------------------------ settings.json

    [TestMethod]
    public void Workspace_settings_round_trip()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("labcoat-settings-").FullName, "nested");
        try
        {
            var s = new WorkspaceSettings(dir);
            Assert.IsNull(s.TracingEnabled);
            Assert.IsFalse(File.Exists(s.FilePath)); // nothing written before the first change
            s.SetTracingEnabled(true);
            Assert.IsTrue(s.SetQuarantined("P", "t1", true));
            Assert.IsFalse(s.SetQuarantined("P", "t1", true)); // no change
            Assert.IsTrue(s.SetQuarantined("P", "t2", true));
            Assert.IsTrue(s.SetQuarantined("P", "t2", false));
            Assert.IsFalse(s.SetQuarantined("P", "t3", false));
            s.SetTargetFramework("/a.csproj", "net8.0");
            s.SetTargetFramework("/b.csproj", "net9.0");
            s.SetTargetFramework("/b.csproj", null);

            var again = new WorkspaceSettings(dir);
            Assert.IsTrue(again.TracingEnabled);
            CollectionAssert.AreEqual(new[] { WorkspaceSettings.QuarantineKey("P", "t1") }, again.Quarantined());
            Assert.IsTrue(again.IsQuarantined("P", "t1"));
            Assert.IsFalse(again.IsQuarantined("P", "t2"));
            Assert.AreEqual("net8.0", again.TargetFramework("/a.csproj"));
            Assert.IsNull(again.TargetFramework("/b.csproj"));
            Assert.AreEqual("P|t1", WorkspaceSettings.QuarantineKey("P", "t1"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); }
    }

    [TestMethod]
    public void Workspace_settings_problems_are_warnings()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-settings-").FullName;
        try
        {
            var log = new List<string>();
            File.WriteAllText(Path.Combine(root, "settings.json"), "[1,2");
            var s = new WorkspaceSettings(root, (level, msg) => log.Add($"{level}: {msg}"));
            Assert.IsNull(s.TracingEnabled);
            Assert.StartsWith("warning: Could not read workspace settings", log.Single());

            File.WriteAllText(Path.Combine(root, "settings.json"), "null");
            Assert.IsEmpty(new WorkspaceSettings(root).Quarantined()); // "null": defaults

            // A file where the directory should be: saving fails, the in-memory value stays.
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "");
            var failing = new WorkspaceSettings(Path.Combine(blocked, "sub"), (level, msg) => log.Add($"{level}: {msg}"));
            failing.SetTracingEnabled(false);
            Assert.IsFalse(failing.TracingEnabled);
            Assert.IsTrue(log.Any(l => l.StartsWith("warning: Could not save workspace settings")), string.Join("\n", log));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Output_store_entries_can_be_removed()
    {
        var store = new OutputStore();
        store.Put("P", "a", "out", "err");
        Assert.AreEqual(6, store.Size);
        store.Remove("P", "a");
        store.Remove("P", "missing");
        Assert.AreEqual(0, store.Size);
        Assert.IsFalse(store.TryGet("P", "a", out var o, out var e));
        Assert.IsNull(o);
        Assert.IsNull(e);
        Assert.IsNull(TestOutput.Cap(null, out var cut));
        Assert.IsFalse(cut);
    }
}
