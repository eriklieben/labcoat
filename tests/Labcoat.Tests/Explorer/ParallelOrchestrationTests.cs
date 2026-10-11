using Labcoat;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// Several projects without real test hosts (a fake `dotnet`): one batch build for all projects with build errors per
/// project, the project list refreshed by Rediscover / project file changes, run limits in the state.
/// </summary>
[TestClass]
public class ParallelOrchestrationTests
{
    private sealed class Workspace : IAsyncDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("labcoat-par-");
        public FakeDotnet Dotnet { get; } = new();
        public List<string> Found { get; } = [];
        public Labcoat.Explorer Explorer { get; }

        public Workspace(string[] names, bool finder = false, int maxParallel = 4)
        {
            foreach (var n in names) Found.Add(Add(n));
            Explorer = new Labcoat.Explorer(new EventHub(), [.. Found], null, [Root.FullName])
            {
                DotnetOverride = Dotnet.InvokeAsync,
                MaxParallelProjects = maxParallel,
                ProjectFinder = finder ? _ => [.. Found] : null,
                WorkspaceRoots = [Root.FullName],
            };
        }

        public string Add(string name)
        {
            var dir = Directory.CreateDirectory(Path.Combine(Root.FullName, name)).FullName;
            var path = Path.Combine(dir, name + ".csproj");
            File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            return path;
        }

        public string Project(string name) => Path.Combine(Root.FullName, name, name + ".csproj");
        public string Logs => ScratchWorkspace.Logs(Explorer);

        public async ValueTask DisposeAsync()
        {
            await Explorer.DisposeAsync();
            try { Root.Delete(recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Projects_build_in_one_build_and_errors_stay_with_their_project()
    {
        await using var w = new Workspace(["P", "Q", "R"]);
        w.Dotnet.Evaluation = null; // the evaluation after a successful build fails: no test host is started
        w.Dotnet.BuildExitCode = 1;
        w.Dotnet.BuildOutput = $"""
              P -> {w.Root.FullName}/P/bin/Debug/net10.0/P.dll
            {w.Root.FullName}/Q/Bad.cs(3,5): error CS1002: ; expected [{w.Project("Q")}]
              R -> {w.Root.FullName}/R/bin/Debug/net10.0/R.dll
            Build FAILED.
            """;
        await w.Explorer.DiscoverAsync(null);

        var builds = w.Dotnet.Calls.Where(c => c[0] == "build").ToList();
        Assert.HasCount(1, builds, w.Logs);
        StringAssert.EndsWith(builds[0][1], ".sln");
        StringAssert.Contains(builds[0][1], Path.Combine("P", "obj", "labcoat"));
        Assert.IsFalse(File.Exists(builds[0][1]), "the generated solution is deleted after the build");
        Assert.Contains("Building 3 projects in one build: P, Q, R", w.Logs);

        var projects = w.Explorer.Snapshot().Projects.ToDictionary(p => p.Id);
        Assert.AreEqual(ProjectStatus.Error, projects["Q"].Status);
        StringAssert.Contains(projects["Q"].LastError, "error CS1002");
        Assert.AreEqual(1, projects["Q"].BuildErrorCount);
        // P and R built; their evaluation is what failed (the fake has none)
        StringAssert.StartsWith(projects["P"].LastError, "Could not evaluate project");
        Assert.IsNull(projects["P"].BuildErrors);
        StringAssert.StartsWith(projects["R"].LastError, "Could not evaluate project");
        Assert.AreEqual(4, w.Explorer.Snapshot().Limits!.MaxParallelProjects);
    }

    [TestMethod]
    public async Task A_single_project_and_one_project_at_a_time_build_as_before()
    {
        await using var w = new Workspace(["P", "Q"], maxParallel: 1);
        w.Dotnet.Evaluation = null;
        await w.Explorer.DiscoverAsync("P");
        CollectionAssert.AreEqual(new[] { "build", w.Project("P"), "-nologo", "-tl:off", "-v:minimal" }, w.Dotnet.Calls.First(c => c[0] == "build"));

        // a cancelled batch build leaves the projects idle
        w.Dotnet.BuildHangs = true;
        var discovery = w.Explorer.DiscoverAsync(null);
        await ScratchWorkspace.WaitUntilAsync(() => w.Explorer.Snapshot().Projects.All(p => p.Status == ProjectStatus.Building), "building");
        w.Explorer.Cancel();
        await discovery;
        Assert.IsTrue(w.Explorer.Snapshot().Projects.All(p => p.Status == ProjectStatus.Idle), w.Logs);
    }

    [TestMethod]
    public async Task Rediscover_adds_new_projects_and_drops_removed_ones()
    {
        await using var w = new Workspace(["P", "Q"], finder: true);
        w.Dotnet.Evaluation = null;
        w.Explorer.SeedTests([new TestNode { Id = "q1", ProjectId = "Q", DisplayName = "q1" }, new TestNode { Id = "p1", ProjectId = "P", DisplayName = "p1" }]);

        w.Found.Remove(w.Project("Q"));
        w.Found.Add(w.Add("N"));
        w.Found.Add(w.Add("P2"));
        await w.Explorer.DiscoverCommandAsync(null); // the Rediscover button

        CollectionAssert.AreEqual(new[] { "P", "N", "P2" }, w.Explorer.Snapshot().Projects.Select(p => p.Id).ToList(), w.Logs);
        CollectionAssert.AreEqual(new[] { "p1" }, w.Explorer.Snapshot().Tests.Select(t => t.Id).ToList());
        Assert.Contains("Test projects changed: new test projects: N, P2; removed: Q", w.Logs);
        Assert.IsTrue(w.Dotnet.Calls.Any(c => c[0] == "build"), "the projects are built and discovered");
        Assert.IsFalse(w.Explorer.HasProject("Q"));

        // a project with the name of an existing one gets its own id; nothing changed → nothing logged
        var other = Directory.CreateDirectory(Path.Combine(w.Root.FullName, "sub", "P")).FullName;
        File.WriteAllText(Path.Combine(other, "P.csproj"), "<Project />");
        w.Found.Add(Path.Combine(other, "P.csproj"));
        await w.Explorer.OnProjectFilesChangedAsync([Path.Combine(other, "P.csproj")]);
        Assert.IsTrue(w.Explorer.HasProject("P-2"), w.Logs);
        Assert.Contains("Project files changed (P.csproj); looking for test projects again", w.Logs);

        // a changed project file of a known project rebuilds it; a solution / props change rebuilds everything
        var before = w.Dotnet.Calls.Count(c => c[0] == "build");
        await w.Explorer.OnProjectFilesChangedAsync([w.Project("N")]);
        Assert.AreEqual(before + 1, w.Dotnet.Calls.Count(c => c[0] == "build"));
        Assert.AreEqual(w.Project("N"), w.Dotnet.Calls.Last(c => c[0] == "build")[1]);
        await w.Explorer.OnProjectFilesChangedAsync([Path.Combine(w.Root.FullName, "Directory.Build.props")]);
        StringAssert.EndsWith(w.Dotnet.Calls.Last(c => c[0] == "build")[1], ".sln");
    }

    [TestMethod]
    public async Task Without_a_project_finder_the_list_is_fixed_and_the_watcher_does_not_start()
    {
        await using var w = new Workspace(["P"]);
        w.Dotnet.Evaluation = null;
        w.Explorer.StartProjectWatcher(); // no finder: no-op
        await w.Explorer.RediscoverAsync();
        CollectionAssert.AreEqual(new[] { "P" }, w.Explorer.Snapshot().Projects.Select(p => p.Id).ToList());
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task A_new_project_file_in_the_workspace_is_picked_up_by_the_watcher()
    {
        await using var w = new Workspace([], finder: true);
        w.Dotnet.Evaluation = null;
        w.Explorer.StartProjectWatcher();
        w.Explorer.StartProjectWatcher(); // once
        w.Found.Add(w.Add("Late.Tests"));
        await ScratchWorkspace.WaitUntilAsync(() => w.Explorer.HasProject("Late.Tests"), "the new project");
        await ScratchWorkspace.WaitUntilAsync(() => w.Dotnet.Calls.Any(c => c[0] == "build"), "its build");
    }

    [TestMethod]
    public async Task A_failing_project_search_is_a_warning()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-par-");
        await using var e = new Labcoat.Explorer(new EventHub(), [], null, [root.FullName])
        {
            ProjectFinder = warn =>
            {
                warn("odd project file");
                throw new InvalidOperationException("boom");
            },
        };
        await e.RediscoverAsync();
        var logs = ScratchWorkspace.Logs(e);
        Assert.Contains("odd project file", logs);
        Assert.Contains("Looking for test projects failed: boom", logs);
        root.Delete(recursive: true);
    }

    [TestMethod]
    public async Task A_project_found_while_running_keeps_its_history_quarantine_and_coverage_after_a_restart()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-par-");
        Directory.CreateDirectory(Path.Combine(root.FullName, ".git")); // one workspace, whatever projects it has
        string Add(string dir)
        {
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root.FullName, dir)).FullName, "P.csproj");
            File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            return path;
        }
        var found = new List<string> { Add("a") };
        var dotnet = new FakeDotnet { Evaluation = null };
        Labcoat.Explorer Start() => new(new EventHub(), [.. found], null, [root.FullName])
        {
            DotnetOverride = dotnet.InvokeAsync, ProjectFinder = _ => [.. found], WorkspaceRoots = [root.FullName],
        };
        static TestNode Node(string project, Outcome outcome) => new()
        {
            Id = "t", ProjectId = project, DisplayName = "t", Outcome = outcome, DurationMs = 1, LastRunAt = Json.Now(),
        };
        try
        {
            var b = Add("b");
            await using (var first = Start())
            {
                found.Add(b);
                await first.OnProjectFilesChangedAsync([b]); // b is found while running: P-2
                Assert.IsTrue(first.HasProject("P-2"), ScratchWorkspace.Logs(first));
                Assert.AreEqual("b/P.csproj", first.HistoryState.ProjectPathOf("P-2"), ignoreCase: true); // relative paths: case-insensitive on Windows / macOS
                first.HistoryState.Record(new RunSummary { RunId = "r1", StartedAt = Json.Now(), Total = 1 }, [Node("P-2", Outcome.Failed)]);
                first.SetQuarantine("P-2", "t", true);
                first.FullCoverageState.SetFull("P-2", new CoverageAccumulator(), "r1", null);
            }

            found.Reverse(); // the next search lists b first: b is P, a is P-2
            await using var second = Start();
            var t = Node("P", Outcome.NotRun);
            second.HistoryState.Restore(t);
            Assert.AreEqual(Outcome.Failed, t.Outcome, "b's history survived the restart");
            var other = Node("P-2", Outcome.NotRun);
            second.HistoryState.Restore(other);
            Assert.AreEqual(Outcome.NotRun, other.Outcome, "a doesn't get b's history");
            CollectionAssert.AreEqual(new[] { "P|t" }, second.Snapshot().Quarantined);
            CollectionAssert.AreEqual(new[] { "P" }, second.FullCoverageReport!.ProjectIds);

            // b removed while running, then c (another P.csproj) gets its free id "P": none of b's data is c's
            found.Remove(b);
            await second.OnProjectFilesChangedAsync([b]);
            var c = Add("c");
            found.Add(c);
            await second.OnProjectFilesChangedAsync([c]);
            Assert.AreEqual("c/P.csproj", second.HistoryState.ProjectPathOf("P"), ignoreCase: true);
            var fresh = Node("P", Outcome.NotRun);
            second.HistoryState.Restore(fresh);
            Assert.AreEqual(Outcome.NotRun, fresh.Outcome);
            Assert.IsEmpty(second.Snapshot().Quarantined!);
            Assert.AreEqual("b/P.csproj", second.FullCoverageReport!.ProjectIds.Single(), ignoreCase: true);

            // b back (a new id): its history, quarantine and coverage are its own again
            found.Add(b);
            await second.OnProjectFilesChangedAsync([b]);
            var id = second.Snapshot().Projects.Single(p => p.Path == b).Id;
            var back = Node(id, Outcome.NotRun);
            second.HistoryState.Restore(back);
            Assert.AreEqual(Outcome.Failed, back.Outcome);
            CollectionAssert.AreEqual(new[] { id + "|t" }, second.Snapshot().Quarantined);
            CollectionAssert.AreEqual(new[] { id }, second.FullCoverageReport!.ProjectIds);
            Assert.Contains("Restored the full coverage of P", ScratchWorkspace.Logs(second));
        }
        finally
        {
            try { root.Delete(recursive: true); } catch { /* best effort: a test host may still hold a file */ }
        }
    }
}
