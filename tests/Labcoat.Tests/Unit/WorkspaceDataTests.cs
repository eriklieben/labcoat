using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The workspace data folder: its key (git root / solution / common root), history that survives project changes,
/// the migration of the per-project-set folders of earlier versions, and the lock between two Labcoat instances.
/// </summary>
[TestClass]
public class WorkspaceDataTests
{
    private static TestNode Node(string project, string id, Outcome outcome, string at = "2026-10-09T08:00:00Z") => new()
    {
        Id = id, ProjectId = project, DisplayName = id, Outcome = outcome, DurationMs = 1, LastRunAt = at,
    };

    private static RunSummary Run(string id, string at = "2026-10-09T08:00:00Z") => new() { RunId = id, StartedAt = at, Total = 1 };

    /// <summary>A temp repository (a .git folder) with project files under tests/.</summary>
    private sealed class Repo : IDisposable
    {
        public string Root { get; } = ExplorerTests.ScratchWorkspace.CreateRealTempDirectory("labcoat-ws-");

        public Repo(bool git = true)
        {
            if (git) Directory.CreateDirectory(Path.Combine(Root, ".git"));
        }

        public string Project(string name, string? dir = null)
        {
            var folder = Directory.CreateDirectory(Path.Combine(Root, "tests", dir ?? name)).FullName;
            var path = Path.Combine(folder, name + ".csproj");
            if (!File.Exists(path)) File.WriteAllText(path, "<Project />");
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ the key

    [TestMethod]
    public void The_git_root_is_the_workspace_whatever_projects_it_has()
    {
        using var repo = new Repo();
        var a = repo.Project("A.Tests");
        var b = repo.Project("B.Tests");
        var one = WorkspaceIdentity.Resolve([a]);
        var both = WorkspaceIdentity.Resolve([b, a]);
        Assert.AreEqual("git", one.Kind);
        Assert.AreEqual(repo.Root, one.Path);
        Assert.AreEqual(repo.Root, one.Root);
        Assert.AreEqual(one.FolderName, both.FolderName);
        Assert.IsTrue(one.FolderName.StartsWith(Path.GetFileName(repo.Root) + "-", StringComparison.OrdinalIgnoreCase), one.FolderName);
        Assert.AreEqual("tests/A.Tests/A.Tests.csproj", one.RelativeProjectPath(a));
        Assert.AreEqual(Path.Combine(WorkspaceIdentity.BaseDirectory, one.FolderName), one.DataDirectory);

        // a worktree / submodule has a .git file
        using var worktree = new Repo(git: false);
        File.WriteAllText(Path.Combine(worktree.Root, ".git"), "gitdir: /elsewhere");
        Assert.AreEqual("git", WorkspaceIdentity.Resolve([worktree.Project("W")]).Kind);
    }

    [TestMethod]
    public void Without_git_the_solution_and_else_the_common_root_is_the_workspace()
    {
        using var plain = new Repo(git: false);
        var a = plain.Project("A");
        var b = plain.Project("B");
        var dir = WorkspaceIdentity.Resolve([a, b]);
        Assert.AreEqual("directory", dir.Kind);
        Assert.AreEqual(Path.Combine(plain.Root, "tests"), dir.Path);
        Assert.AreEqual("A/A.csproj", dir.RelativeProjectPath(a));

        File.WriteAllText(Path.Combine(plain.Root, "Shop.sln"), "");
        File.WriteAllText(Path.Combine(plain.Root, "Shop.slnx"), "<Solution />");
        var solution = WorkspaceIdentity.Resolve([a]);
        Assert.AreEqual("solution", solution.Kind);
        Assert.AreEqual(Path.Combine(plain.Root, "Shop.slnx"), solution.Path, ".slnx before .sln");
        Assert.AreEqual(plain.Root, solution.Root);
        Assert.AreEqual(solution.FolderName, WorkspaceIdentity.Resolve([a, b]).FolderName);
        Assert.IsTrue(solution.FolderName.StartsWith("Shop-", StringComparison.OrdinalIgnoreCase), solution.FolderName);

        Assert.AreEqual("none", WorkspaceIdentity.Resolve([]).Kind);
        StringAssert.StartsWith(WorkspaceIdentity.Resolve([]).FolderName, "workspace-");
    }

    [TestMethod]
    public void A_repository_in_the_home_directory_is_not_every_projects_workspace()
    {
        using var home = new Repo(); // ~/.git (a dotfiles repository)
        var p = home.Project("A");
        var id = WorkspaceIdentity.Resolve([p], home: home.Root);
        Assert.AreNotEqual("git", id.Kind);
        Assert.AreEqual("directory", id.Kind);
    }

    [TestMethod]
    public void The_key_is_normalized_and_case_folded_where_the_file_system_ignores_case()
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var upper = Path.Combine(root, "Work", "Repo") + sep;
        var lower = Path.Combine(root, "work", "repo");
        Assert.AreEqual(WorkspaceIdentity.Normalize(lower, caseInsensitive: true), WorkspaceIdentity.Normalize(upper, caseInsensitive: true));
        Assert.AreNotEqual(WorkspaceIdentity.Normalize(lower, caseInsensitive: false), WorkspaceIdentity.Normalize(upper, caseInsensitive: false));
        Assert.AreEqual(Path.Combine(root, "Work", "Repo"), WorkspaceIdentity.Normalize(upper, caseInsensitive: false), "trailing separator trimmed");
        Assert.AreEqual("", WorkspaceIdentity.Normalize("", caseInsensitive: true));

        // c:\Repo vs C:\repo (or /Users/Me vs /users/me on macOS): one folder; on a case-sensitive file system two.
        var a = WorkspaceIdentity.Resolve([Path.Combine(upper, "A.csproj")], home: null, caseInsensitive: true);
        var b = WorkspaceIdentity.Resolve([Path.Combine(lower, "A.csproj")], home: null, caseInsensitive: true);
        Assert.AreEqual(a.FolderName, b.FolderName);
        Assert.AreNotEqual(
            WorkspaceIdentity.Resolve([Path.Combine(upper, "A.csproj")], home: null, caseInsensitive: false).FolderName,
            WorkspaceIdentity.Resolve([Path.Combine(lower, "A.csproj")], home: null, caseInsensitive: false).FolderName);
    }

    [TestMethod]
    public async Task Project_ids_are_the_explorers()
    {
        string[] paths = ["/w/a/X.csproj", "/w/b/X.csproj", "/w/c/x.fsproj", "/w/d/Y.csproj"];
        CollectionAssert.AreEqual(new[] { "X", "X-2", "x-3", "Y" }, WorkspaceIdentity.ProjectIds(paths));
        await using var explorer = new Explorer(new EventHub(), paths, null);
        CollectionAssert.AreEqual(WorkspaceIdentity.ProjectIds(paths), explorer.Snapshot().Projects.Select(p => p.Id).ToArray());
    }

    // ------------------------------------------------------------------ history across project changes

    [TestMethod]
    public void History_survives_adding_removing_and_reordering_projects()
    {
        using var repo = new Repo();
        var a = repo.Project("X", "a");
        var b = repo.Project("X", "b"); // same name: ids X and X-2 depend on the order
        var c = repo.Project("C");
        string file;
        using (var first = new HistoryStore([a, b]))
        {
            file = first.FilePath;
            first.Record(Run("r1"), [Node("X", "ta", Outcome.Passed), Node("X-2", "tb", Outcome.Failed)]);
        }

        // a project added: the same folder, nothing lost
        using (var added = new HistoryStore([a, b, c]))
        {
            Assert.AreEqual(file, added.FilePath);
            var ta = Node("X", "ta", Outcome.NotRun);
            added.Restore(ta);
            Assert.AreEqual(Outcome.Passed, ta.Outcome);
            Assert.IsTrue(ta.Stale);
            var fresh = Node("C", "tc", Outcome.NotRun);
            added.Restore(fresh);
            Assert.IsNull(fresh.History, "an added project starts empty");
        }

        // a removed: b is now "X" (not "X-2"); its history follows the path, not the id
        using (var removed = new HistoryStore([b, c]))
        {
            Assert.AreEqual(file, removed.FilePath);
            var tb = Node("X", "tb", Outcome.NotRun);
            removed.Restore(tb);
            Assert.AreEqual(Outcome.Failed, tb.Outcome);
            var ta = Node("X", "ta", Outcome.NotRun);
            removed.Restore(ta);
            Assert.AreEqual(Outcome.NotRun, ta.Outcome, "a's history is not b's");
            // the past run keeps a's results under its relative path
            var run = removed.GetRun("r1")!;
            CollectionAssert.AreEquivalent(new[] { "tests/a/X.csproj", "X" }, run.Results.Select(r => r.ProjectId).ToArray());
        }

        // a back: its history is still there
        using var back = new HistoryStore([a, b, c]);
        var again = Node("X", "ta", Outcome.NotRun);
        back.Restore(again);
        Assert.AreEqual(Outcome.Passed, again.Outcome);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        CollectionAssert.Contains(json["projectPaths"]!.AsArray().Select(n => (string?)n).ToArray(), "tests/b/X.csproj");
    }

    [TestMethod]
    public void A_removed_projects_history_ages_out()
    {
        using var repo = new Repo();
        var a = repo.Project("A");
        var b = repo.Project("B");
        var old = DateTimeOffset.UtcNow.AddDays(-HistoryStore.OrphanRetention.TotalDays - 5).ToString("o");
        var recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
        using (var s = new HistoryStore([a, b]))
            s.Record(Run("r1", old), [Node("A", "old", Outcome.Passed, old), Node("B", "kept", Outcome.Passed, old), Node("A", "recent", Outcome.Passed, recent)]);

        using (var withoutA = new HistoryStore([b])) // A is gone: its old entry is dropped, the recent one kept
        {
            var kept = Node("B", "kept", Outcome.NotRun);
            withoutA.Restore(kept);
            Assert.AreEqual(Outcome.Passed, kept.Outcome, "a current project's history doesn't age out");
            withoutA.Record(Run("r2", recent), [Node("B", "kept", Outcome.Passed, recent)]);
        }
        using var withA = new HistoryStore([a, b]);
        var gone = Node("A", "old", Outcome.NotRun);
        withA.Restore(gone);
        Assert.IsNull(gone.History);
        var stillThere = Node("A", "recent", Outcome.NotRun);
        withA.Restore(stillThere);
        Assert.AreEqual(Outcome.Passed, stillThere.Outcome);
    }

    // ------------------------------------------------------------------ projects added and removed while running

    [TestMethod]
    public void A_project_added_while_running_keeps_its_history_after_a_restart()
    {
        using var repo = new Repo();
        var a = repo.Project("X", "a");
        var b = repo.Project("X", "b");
        string file;
        using (var s = new HistoryStore([a]))
        {
            file = s.FilePath;
            s.RegisterProject("X-2", b); // live discovery found b: X is taken, so it is X-2
            Assert.AreEqual("tests/b/X.csproj", s.ProjectPathOf("X-2"), ignoreCase: true); // relative paths: case-insensitive on Windows / macOS
            Assert.AreEqual("X-2", s.ProjectIdOf("tests/b/X.csproj"));
            s.Record(Run("r1"), [Node("X", "ta", Outcome.Passed), Node("X-2", "tb", Outcome.Failed)]);
        }
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        CollectionAssert.AreEquivalent(new[] { "tests/a/x.csproj", "tests/b/x.csproj" }, json["projectPaths"]!.AsArray().Select(n => ((string?)n)?.ToLowerInvariant()).ToArray());
        Assert.HasCount(2, json["projects"]!.AsArray());

        // restart: the search lists b first, so b is X now and a is X-2; each keeps its own history
        using var after = new HistoryStore([b, a]);
        var tb = Node("X", "tb", Outcome.NotRun);
        after.Restore(tb);
        Assert.AreEqual(Outcome.Failed, tb.Outcome);
        var ta = Node("X-2", "ta", Outcome.NotRun);
        after.Restore(ta);
        Assert.AreEqual(Outcome.Passed, ta.Outcome);
        var wrong = Node("X-2", "tb", Outcome.NotRun);
        after.Restore(wrong);
        Assert.AreEqual(Outcome.NotRun, wrong.Outcome, "b's history is not a's");
        CollectionAssert.AreEquivalent(new[] { "X", "X-2" }, after.GetRun("r1")!.Results.Select(r => r.ProjectId).ToArray());
    }

    [TestMethod]
    public void A_project_removed_while_running_gives_up_its_id()
    {
        using var repo = new Repo();
        var a = repo.Project("X", "a");
        var b = repo.Project("X", "b");
        var c = repo.Project("C");
        using (var s = new HistoryStore([a, c]))
        {
            s.Record(Run("r1"), [Node("X", "ta", Outcome.Passed)]);
            s.ForgetProject("X"); // a is gone ...
            s.ForgetProject("X"); // (once)
            Assert.IsNull(s.ProjectPathOf("X"));
            s.RegisterProject("X", b); // ... and b, found next, gets its id
            var stale = Node("X", "ta", Outcome.NotRun);
            s.Restore(stale);
            Assert.AreEqual(Outcome.NotRun, stale.Outcome, "a's results don't show on b's tests");
            Assert.AreEqual("tests/a/X.csproj", s.GetRun("r1")!.Results.Single().ProjectId, ignoreCase: true, "a past run keeps a's results under its path");
            s.RegisterProject("X", b); // again: nothing changes
            s.Record(Run("r2"), [Node("X", "tb", Outcome.Failed)]);
        }

        using (var withB = new HistoryStore([b, c]))
        {
            var tb = Node("X", "tb", Outcome.NotRun);
            withB.Restore(tb);
            Assert.AreEqual(Outcome.Failed, tb.Outcome, "b's history was saved under b's path, not a's");
        }
        using var withA = new HistoryStore([a, b, c]); // a is back: X again, b is X-2
        var ta = Node("X", "ta", Outcome.NotRun);
        withA.Restore(ta);
        Assert.AreEqual(Outcome.Passed, ta.Outcome);
        var tb2 = Node("X-2", "tb", Outcome.NotRun);
        withA.Restore(tb2);
        Assert.AreEqual(Outcome.Failed, tb2.Outcome);
    }

    [TestMethod]
    public void A_project_that_comes_back_while_running_gets_its_history_back()
    {
        using var repo = new Repo();
        var a = repo.Project("A");
        var b = repo.Project("B");
        using (var s = new HistoryStore([a, b])) s.Record(Run("r1"), [Node("B", "u", Outcome.Failed)]);

        using var withoutB = new HistoryStore([a]); // B wasn't there at startup (another branch) ...
        var missing = Node("B", "u", Outcome.NotRun);
        withoutB.Restore(missing);
        Assert.AreEqual(Outcome.NotRun, missing.Outcome);
        withoutB.RegisterProject("B", b); // ... and comes back while running
        var u = Node("B", "u", Outcome.NotRun);
        withoutB.Restore(u);
        Assert.AreEqual(Outcome.Failed, u.Outcome);
        Assert.AreEqual("B", withoutB.GetRun("r1")!.Results.Single().ProjectId);
        withoutB.RegisterProject("B2", b); // the same project under another id: the data follows
        Assert.IsNull(withoutB.ProjectPathOf("B"));
        var moved = Node("B2", "u", Outcome.NotRun);
        withoutB.Restore(moved);
        Assert.AreEqual(Outcome.Failed, moved.Outcome);
    }

    [TestMethod]
    public void A_project_removed_while_running_ages_out_like_one_removed_between_sessions()
    {
        using var repo = new Repo();
        var a = repo.Project("A");
        var n = repo.Project("N");
        var old = DateTimeOffset.UtcNow.AddDays(-HistoryStore.OrphanRetention.TotalDays - 5).ToString("o");
        var recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
        using (var s = new HistoryStore([a]))
        {
            s.RegisterProject("N", n); // added ...
            s.Record(Run("r1", old), [Node("N", "old", Outcome.Passed, old), Node("N", "recent", Outcome.Passed, recent), Node("A", "kept", Outcome.Passed, old)]);
            s.ForgetProject("N"); // ... and removed again
            s.ForgetProject("unknown");
        }
        var json = JsonNode.Parse(File.ReadAllText(WorkspaceIdentity.Resolve([a]).DataDirectory + "/history.json"))!;
        CollectionAssert.Contains(json["projectIds"]!.AsArray().Select(x => ((string?)x)?.ToLowerInvariant()).ToArray(), "tests/n/n.csproj");

        using (var withoutN = new HistoryStore([a])) // a restart without N drops its old entry (saved with the next result)
            withoutN.Record(Run("r2", recent), [Node("A", "kept", Outcome.Passed, recent)]);
        using var back = new HistoryStore([a, n]);
        var gone = Node("N", "old", Outcome.NotRun);
        back.Restore(gone);
        Assert.IsNull(gone.History);
        var kept = Node("N", "recent", Outcome.NotRun);
        back.Restore(kept);
        Assert.AreEqual(Outcome.Passed, kept.Outcome);
        var current = Node("A", "kept", Outcome.NotRun);
        back.Restore(current);
        Assert.AreEqual(Outcome.Passed, current.Outcome, "a current project's history doesn't age out");
    }

    [TestMethod]
    public void Quarantine_follows_the_project_path_not_its_id()
    {
        using var repo = new Repo();
        var a = repo.Project("X", "a");
        var b = repo.Project("X", "b");
        using (var s = new HistoryStore([a, b])) // a is X, b is X-2
        {
            // a version 1 file: id keys
            File.WriteAllText(Path.Combine(s.DataDirectory, "settings.json"), """{"version":1,"quarantined":["X-2|tb","Gone|t","odd"]}""");
            var settings = new WorkspaceSettings(s.DataDirectory, null, s);
            CollectionAssert.AreEquivalent(new[] { "X-2|tb", "Gone|t", "odd" }, settings.Quarantined());
            var json = JsonNode.Parse(File.ReadAllText(settings.FilePath))!;
            Assert.AreEqual(WorkspaceSettings.FileVersion, (int)json["version"]!);
            Assert.AreEqual("tests/b/X.csproj", (string?)json["quarantinedTests"]![0]!["project"], ignoreCase: true);
            CollectionAssert.AreEqual(new[] { "Gone|t", "odd" }, json["quarantined"]!.AsArray().Select(n => (string?)n).ToArray(), "unknown projects keep id keys");
            Assert.IsTrue(settings.SetQuarantined("X", "ta", true));
            Assert.IsFalse(settings.SetQuarantined("X", "ta", true));
            Assert.IsTrue(settings.IsQuarantined("X", "ta"));
        }

        // restart: the search lists b first, so b is X and a is X-2
        using var after = new HistoryStore([b, a]);
        var again = new WorkspaceSettings(after.DataDirectory, null, after);
        CollectionAssert.AreEquivalent(new[] { "X|tb", "X-2|ta", "Gone|t", "odd" }, again.Quarantined());
        Assert.IsTrue(again.IsQuarantined("X", "tb"));
        Assert.IsFalse(again.IsQuarantined("X-2", "tb"));

        // a removed project's entries stay (hidden) until it comes back
        after.ForgetProject("X-2");
        CollectionAssert.DoesNotContain(again.Quarantined(), "X-2|ta");
        after.RegisterProject("X-2", a);
        CollectionAssert.Contains(again.Quarantined(), "X-2|ta");

        Assert.IsTrue(again.SetQuarantined("X", "tb", false));
        Assert.IsTrue(again.SetQuarantined("Gone", "t", false), "id keys can be removed too");
        Assert.IsTrue(again.SetQuarantined("Unknown", "u", true), "a project without a path: an id key");
        CollectionAssert.AreEquivalent(new[] { "X-2|ta", "odd", "Unknown|u" }, again.Quarantined());

        // without project keys (tools): id keys only; the path entries are kept in the file
        var plain = new WorkspaceSettings(after.DataDirectory);
        CollectionAssert.AreEquivalent(new[] { "odd", "Unknown|u" }, plain.Quarantined());
        Assert.IsTrue(plain.SetQuarantined("P", "p", true));
        CollectionAssert.Contains(new WorkspaceSettings(after.DataDirectory, null, after).Quarantined(), "X-2|ta");
    }

    // ------------------------------------------------------------------ migration

    /// <summary>Writes a folder like earlier versions did: 16 hex characters, history.json without projectPaths.</summary>
    private static string LegacyFolder(Action<HistoryStore> record, string[] projects, DateTime written, string? settings = null)
    {
        // Let the current store write the file in a scratch data folder, then strip what earlier versions didn't write.
        using var scratch = new Repo();
        string source;
        using (var s = new HistoryStore([scratch.Project("Scratch")]))
        {
            record(s);
            s.Save();
            source = s.FilePath;
        }
        var json = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        json.Remove("projectPaths");
        json["projects"] = new JsonArray([.. projects.Order(StringComparer.Ordinal).Select(p => (JsonNode)p)]);
        var dir = Path.Combine(WorkspaceIdentity.BaseDirectory, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..16]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "history.json");
        File.WriteAllText(file, json.ToJsonString());
        File.SetLastWriteTimeUtc(file, written);
        if (settings is not null)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), settings);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "settings.json"), written);
        }
        Directory.Delete(Path.GetDirectoryName(source)!, recursive: true);
        return dir;
    }

    [TestMethod]
    public void The_first_start_merges_the_folders_of_earlier_versions_newest_wins()
    {
        using var repo = new Repo();
        var a = repo.Project("A");
        var b = repo.Project("B");
        using var elsewhere = new Repo();
        var other = elsewhere.Project("Other");

        // an old session with only A, a newer one with A and B (ids were file names then too)
        var older = LegacyFolder(s => s.Record(Run("r1", "2026-10-01T08:00:00Z"),
            [Node("A", "t", Outcome.Failed, "2026-10-01T08:00:00Z"), Node("A", "only-old", Outcome.Passed, "2026-10-01T08:00:00Z")]),
            [a], new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), """{"version":1,"tracingEnabled":false,"quarantined":["A|old"]}""");
        var newer = LegacyFolder(s => s.Record(Run("r2", "2026-10-05T08:00:00Z"),
            [Node("A", "t", Outcome.Passed, "2026-10-05T08:00:00Z"), Node("B", "u", Outcome.Failed, "2026-10-05T08:00:00Z")]),
            [a, b], new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc), """{"version":1,"tracingEnabled":true,"quarantined":["A|t"]}""");
        var foreign = LegacyFolder(s => s.Record(Run("rx"), [Node("Other", "o", Outcome.Passed)]), [other], DateTime.UtcNow);

        var log = new List<string>();
        using (var store = new HistoryStore([a, b], (level, msg) => log.Add($"{level}: {msg}")))
        {
            Assert.IsTrue(log.Any(l => l.StartsWith("info: Merged the test history of 2 earlier folder(s)")), string.Join("\n", log));
            var t = Node("A", "t", Outcome.NotRun);
            store.Restore(t);
            Assert.AreEqual(Outcome.Passed, t.Outcome, "the newer result wins");
            CollectionAssert.AreEqual(new[] { Outcome.Failed, Outcome.Passed }, t.History!.Select(h => h.Outcome).ToArray(), "both sessions' history, in order");
            var onlyOld = Node("A", "only-old", Outcome.NotRun);
            store.Restore(onlyOld);
            Assert.AreEqual(Outcome.Passed, onlyOld.Outcome);
            var u = Node("B", "u", Outcome.NotRun);
            store.Restore(u);
            Assert.AreEqual(Outcome.Failed, u.Outcome);
            CollectionAssert.AreEqual(new[] { "r2", "r1" }, store.Runs().Select(r => r.RunId).ToArray());

            // the newest settings.json came along (quarantine, trace toggle)
            var settings = new WorkspaceSettings(store.DataDirectory);
            Assert.IsTrue(settings.TracingEnabled);
            CollectionAssert.AreEqual(new[] { "A|t" }, settings.Quarantined());
            var workspace = JsonNode.Parse(File.ReadAllText(Path.Combine(store.DataDirectory, HistoryStore.WorkspaceFileName)))!;
            Assert.AreEqual("git", (string?)workspace["kind"]);
            Assert.HasCount(2, workspace["migratedFrom"]!.AsArray());
        }

        Assert.IsTrue(File.Exists(Path.Combine(older, HistoryStore.MigratedFileName)));
        Assert.IsTrue(File.Exists(Path.Combine(newer, HistoryStore.MigratedFileName)));
        Assert.IsFalse(File.Exists(Path.Combine(foreign, HistoryStore.MigratedFileName)), "another workspace's folder is left alone");

        // once only: a later start doesn't merge again
        var again = new List<string>();
        using (new HistoryStore([a, b], (level, msg) => again.Add(msg)))
            Assert.IsFalse(again.Any(l => l.StartsWith("Merged")), string.Join("\n", again));
        foreach (var d in new[] { older, newer, foreign }) Directory.Delete(d, recursive: true);
    }

    [TestMethod]
    public void The_projects_of_an_old_file_are_read_from_its_start()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("labcoat-proj-").FullName, "history.json");
        try
        {
            File.WriteAllText(file, """{"version":2,"other":{"x":[1,2]},"projects":["/a/A.csproj","/b/B.csproj"],"history":[]}""");
            CollectionAssert.AreEqual(new[] { "/a/A.csproj", "/b/B.csproj" }, HistoryStore.ReadProjects(file));
            File.WriteAllText(file, "[]");
            Assert.IsNull(HistoryStore.ReadProjects(file));
            File.WriteAllText(file, """{"version":2}""");
            Assert.IsNull(HistoryStore.ReadProjects(file));
            File.WriteAllText(file, """{"projects":"nope"}""");
            Assert.IsNull(HistoryStore.ReadProjects(file));
            Assert.IsNull(HistoryStore.ReadProjects(file + ".missing"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }
    }

    // ------------------------------------------------------------------ two instances

    [TestMethod]
    public void A_second_instance_reads_but_does_not_write_until_the_first_one_ends()
    {
        using var repo = new Repo();
        var a = repo.Project("A");
        var dataDir = WorkspaceIdentity.Resolve([a]).DataDirectory;
        Directory.CreateDirectory(dataDir);

        // "another Labcoat": holds the lock file (a separate handle, as another process would) and wrote a result
        using (var seed = new HistoryStore([a])) seed.Record(Run("r0"), [Node("A", "theirs", Outcome.Passed)]);
        var other = new FileStream(Path.Combine(dataDir, WorkspaceLock.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(Path.Combine(dataDir, WorkspaceLock.OwnerFileName), """{"pid":4242}""");
        try
        {
            var log = new List<string>();
            using var mine = new HistoryStore([a], (level, msg) => log.Add($"{level}: {msg}"));
            Assert.IsTrue(mine.ReadOnly);
            Assert.IsTrue(log.Any(l => l.StartsWith("warning: Another Labcoat is using this workspace's history: pid 4242")), string.Join("\n", log));
            Assert.IsFalse(WorkspaceLock.CanWrite(dataDir));
            var theirs = Node("A", "theirs", Outcome.NotRun);
            mine.Restore(theirs);
            Assert.AreEqual(Outcome.Passed, theirs.Outcome, "it shows the other instance's history");

            var before = File.ReadAllText(mine.FilePath);
            mine.Record(Run("r1"), [Node("A", "mine", Outcome.Failed)]);
            mine.Save();
            Assert.AreEqual(before, File.ReadAllText(mine.FilePath), "nothing written while the other one runs");
            var settings = new WorkspaceSettings(dataDir);
            settings.SetTracingEnabled(true);
            Assert.IsFalse(File.Exists(settings.FilePath));

            // the other one saved a newer result meanwhile, then ended
            other.Dispose();
            using (var otherSession = new HistoryStore([a])) otherSession.Record(Run("r2", "2026-10-09T09:00:00Z"), [Node("A", "theirs", Outcome.Failed, "2026-10-09T09:00:00Z")]);

            mine.Save(); // takes the lock over: merges and writes
            Assert.IsFalse(mine.ReadOnly);
            Assert.IsTrue(WorkspaceLock.CanWrite(dataDir));
            Assert.IsTrue(log.Any(l => l.Contains("this one saves the workspace history again")), string.Join("\n", log));
            using var reread = new HistoryStore([a]);
            var m = Node("A", "mine", Outcome.NotRun);
            reread.Restore(m);
            Assert.AreEqual(Outcome.Failed, m.Outcome);
            var t = Node("A", "theirs", Outcome.NotRun);
            reread.Restore(t);
            Assert.AreEqual(Outcome.Failed, t.Outcome, "the other instance's later result is kept");
            CollectionAssert.AreEquivalent(new[] { "r0", "r1", "r2" }, reread.Runs().Select(r => r.RunId).ToArray());
        }
        finally { other.Dispose(); }
    }

    [TestMethod]
    public void Explorers_in_one_process_share_the_lock()
    {
        using var repo = new Repo();
        var a = repo.Project("A");
        using var first = new HistoryStore([a]);
        using var second = new HistoryStore([a]);
        Assert.IsFalse(first.ReadOnly);
        Assert.IsFalse(second.ReadOnly);
        Assert.AreEqual(Environment.ProcessId, WorkspaceLock.ReadOwnerPid(first.DataDirectory));
    }

    // ------------------------------------------------------------------ saving (the seams of benchmarks/Labcoat.Benchmarks)

    [TestMethod]
    public void A_cancelled_save_keeps_the_change_and_a_marked_store_saves_again()
    {
        using var repo = new Repo();
        var a = repo.Project("A");
        var store = new HistoryStore([a]);
        store.Record(Run("r1"), [Node("A", "t", Outcome.Passed)]);
        store.CancelScheduledSave();
        store.Save();
        Assert.IsTrue(File.Exists(store.FilePath), "the change was kept for the next save");

        File.Delete(store.FilePath);
        store.Save();
        Assert.IsFalse(File.Exists(store.FilePath), "nothing changed: nothing written");
        store.MarkChanged();
        store.Save();
        Assert.IsTrue(File.Exists(store.FilePath));

        store.Dispose();
        store.CancelScheduledSave(); // after Dispose: nothing scheduled, no exception
    }
}
