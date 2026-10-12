using System.Diagnostics;
using System.Reflection;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// Run affected (GitAffected.cs): changed files from a scratch git repository (working tree, branch vs base, commit
/// ranges, untracked files, renames, deletions, detached HEAD, shallow clones, no commits) mapped to tests through test
/// files, the impact map, cached per-test coverage and the project fallback.
/// </summary>
[TestClass]
public class GitAffectedTests
{
    public TestContext TestContext { get; set; }

    /// <summary>
    /// repo/ (git, branch main): Directory.Build.props, README.md, src/Shop/{Shop.csproj, Orders.cs, Prices.cs},
    /// tests/Shop.Tests/{Shop.Tests.csproj → Shop, OrdersTests.cs}, tests/Other.Tests/{Other.Tests.csproj, OtherTests.cs}.
    /// Tests: Shop.Tests t1 (Validate), t2 (Total), t3 (in OrdersTests.cs); Other.Tests o1.
    /// </summary>
    internal sealed class Repo : IAsyncDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("labcoat-git-");
        public string Root => Path.Combine(Dir.FullName, "repo");
        public string P(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public string ShopTests => P("tests/Shop.Tests/Shop.Tests.csproj");
        public string OtherTests => P("tests/Other.Tests/Other.Tests.csproj");
        public Explorer Explorer { get; private set; } = null!;

        public const string Orders = """
            namespace Shop;
            public class Orders
            {
                public bool Validate(int qty)
                {
                    return qty > 0;
                }
                public int Total(int qty, int price)
                {
                    return qty * price;
                }
            }
            """;

        public static async Task<Repo> CreateAsync(bool commit = true)
        {
            var r = new Repo();
            Directory.CreateDirectory(r.Root);
            await r.GitAsync("init", "-q", "-b", "main");
            await r.GitAsync("config", "user.email", "t@example.com");
            await r.GitAsync("config", "user.name", "T");
            await r.GitAsync("config", "commit.gpgsign", "false");
            r.Write("Directory.Build.props", "<Project />");
            r.Write("README.md", "# shop");
            r.Write("src/Shop/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            r.Write("src/Shop/Orders.cs", Orders);
            r.Write("src/Shop/Prices.cs", "namespace Shop;\npublic static class Prices { public const int Base = 1; }\n");
            r.Write("tests/Shop.Tests/Shop.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\..\\src\\Shop\\Shop.csproj\" /></ItemGroup></Project>");
            r.Write("tests/Shop.Tests/OrdersTests.cs", "class OrdersTests {}\n");
            r.Write("tests/Other.Tests/Other.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            r.Write("tests/Other.Tests/OtherTests.cs", "class OtherTests {}\n");
            if (commit) await r.CommitAllAsync("initial");
            r.Explorer = new Explorer(new EventHub(), [r.ShopTests, r.OtherTests], null, [r.Root]);
            r.Explorer.SeedTests([
                r.Node("Shop.Tests", "t1", "tests/Shop.Tests/OrdersTests.cs"),
                r.Node("Shop.Tests", "t2", "tests/Shop.Tests/OrdersTests.cs"),
                r.Node("Shop.Tests", "t3", "tests/Shop.Tests/OrdersTests.cs"),
                r.Node("Other.Tests", "o1", "tests/Other.Tests/OtherTests.cs"),
            ]);
            return r;
        }

        /// <summary>Impact map: t1 executes Validate (lines 4–7), t2 executes Total (lines 8–11).</summary>
        public void RecordImpact()
        {
            var file = PathUtil.Canonicalize(P("src/Shop/Orders.cs"));
            lock (Explorer.Sync)
            {
                Explorer.ImpactMapState.Record("Shop.Tests", ["t1"], new() { [file] = [new LineSpan(4, 7) { Name = "Validate" }] });
                Explorer.ImpactMapState.Record("Shop.Tests", ["t2"], new() { [file] = [new LineSpan(8, 11) { Name = "Total" }] });
            }
        }

        public TestNode Node(string projectId, string id, string file) => new()
        {
            Id = id, ProjectId = projectId, DisplayName = id, ClassName = "C", MethodName = id, FilePath = P(file), Outcome = Outcome.NotRun,
        };

        public void Write(string relative, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(P(relative))!);
            System.IO.File.WriteAllText(P(relative), text);
        }

        public async Task CommitAllAsync(string message)
        {
            await GitAsync("add", "-A");
            await GitAsync("commit", "-q", "-m", message);
        }

        public async Task<string> GitAsync(params string[] args)
        {
            var (code, output, err) = await GitRepo.RunAsync(Root, args);
            Assert.AreEqual(0, code, $"git {string.Join(' ', args)}: {err}");
            return output.Trim();
        }

        public async ValueTask DisposeAsync()
        {
            if (Explorer is not null) await Explorer.DisposeAsync();
            try
            {
                foreach (var f in Dir.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal; // .git objects are read-only
                Dir.Delete(recursive: true);
            }
            catch { }
        }
    }

    private static AffectedFile File(AffectedPlan plan, string relative) => plan.Files.Single(f => f.RelativePath == relative);
    private static string[] Ids(AffectedPlan plan) => plan.Tests.Select(t => $"{t.ProjectId}/{t.Id}").ToArray();

    [TestMethod]
    public async Task A_change_inside_a_method_selects_the_tests_that_execute_it()
    {
        await using var r = await Repo.CreateAsync();
        r.RecordImpact();
        r.Write("src/Shop/Orders.cs", Repo.Orders.Replace("qty > 0", "qty > 1"));

        var plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        Assert.AreEqual("working", plan.Base);
        Assert.AreEqual("impactMap", plan.Strategy);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t1" }, Ids(plan));
        CollectionAssert.AreEqual(new[] { "Orders.cs changed (method Validate, per-test data)" }, plan.Tests[0].Reasons);
        Assert.AreEqual("test", plan.Tests[0].Granularity);
        var f = File(plan, "src/Shop/Orders.cs");
        Assert.AreEqual(("modified", "product", "impactMap", 1), (f.Status, f.Kind, f.Mapping, f.Tests));
        CollectionAssert.AreEqual(new[] { 6, 6 }, f.Lines!.Single());
        CollectionAssert.AreEqual(new[] { "Validate" }, f.Methods);
        Assert.AreEqual(new AffectedCounts(1, 1, 1), plan.Counts);
        Assert.IsNull(plan.NeedsImpactMap);
        Assert.AreEqual("main", plan.Git.Branch);
        Assert.AreEqual(1, plan.Git.Dirty);

        // A change outside both methods (the namespace line): every test that touches the file.
        r.Write("src/Shop/Orders.cs", Repo.Orders.Replace("namespace Shop;", "namespace Shop; // x"));
        plan = await r.Explorer.PlanAffectedAsync(null, "ignored for working", TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t1", "Shop.Tests/t2" }, Ids(plan));
        Assert.AreEqual("Orders.cs changed (outside executed methods, per-test data)", plan.Tests[1].Reasons.Single());
        Assert.AreEqual("HEAD", plan.Ref); // a ref is ignored for "working"

        // Deleted: every test that touched it, no line ranges.
        System.IO.File.Delete(r.P("src/Shop/Orders.cs"));
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        f = File(plan, "src/Shop/Orders.cs");
        Assert.AreEqual(("deleted", 2), (f.Status, f.Tests));
        Assert.IsNull(f.Lines);
        Assert.AreEqual("Orders.cs deleted (per-test data)", plan.Tests[0].Reasons.Single());
    }

    [TestMethod]
    public async Task Test_files_project_files_and_docs_map_as_smart_watch_does()
    {
        await using var r = await Repo.CreateAsync();
        // staged change of a test file + an unstaged doc change
        r.Write("tests/Shop.Tests/OrdersTests.cs", "class OrdersTests { int x; }\n");
        await r.GitAsync("add", "tests/Shop.Tests/OrdersTests.cs");
        r.Write("README.md", "# shop!");
        var plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        Assert.AreEqual("testFile", plan.Strategy);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t1", "Shop.Tests/t2", "Shop.Tests/t3" }, Ids(plan));
        Assert.AreEqual("OrdersTests.cs changed (test file)", plan.Tests[0].Reasons.Single());
        Assert.AreEqual(("other", "none", 0), (File(plan, "README.md").Kind, File(plan, "README.md").Mapping, File(plan, "README.md").Tests));
        Assert.AreEqual(("test", "testFile", 3), (File(plan, "tests/Shop.Tests/OrdersTests.cs").Kind, File(plan, "tests/Shop.Tests/OrdersTests.cs").Mapping, File(plan, "tests/Shop.Tests/OrdersTests.cs").Tests));
        await r.GitAsync("reset", "-q", "--hard");

        // a product csproj → the test projects that reference it
        r.Write("src/Shop/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>");
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t1", "Shop.Tests/t2", "Shop.Tests/t3" }, Ids(plan));
        Assert.AreEqual("Shop.csproj changed: all tests of Shop.Tests", plan.Tests[0].Reasons.Single());
        Assert.AreEqual(("project", "project", 3), (File(plan, "src/Shop/Shop.csproj").Kind, File(plan, "src/Shop/Shop.csproj").Mapping, File(plan, "src/Shop/Shop.csproj").Tests));
        Assert.IsTrue(plan.Projects.Single().All);
        await r.GitAsync("checkout", "-q", "--", ".");

        // Directory.Build.props at the root → every test project below it
        r.Write("Directory.Build.props", "<Project><PropertyGroup /></Project>");
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        Assert.AreEqual("project", plan.Strategy);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t1", "Shop.Tests/t2", "Shop.Tests/t3", "Other.Tests/o1" }, Ids(plan));
        Assert.AreEqual(2, plan.Counts.Projects);
        await r.GitAsync("checkout", "-q", "--", ".");

        // A test project's own csproj; another .props imported from a folder outside every project → all projects
        r.Write("tests/Other.Tests/Other.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>");
        r.Write("build/Common.props", "<Project />");
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        CollectionAssert.Contains(plan.Tests.Single(t => t.Id == "o1").Reasons, "Other.Tests.csproj changed: all tests of Other.Tests");
        CollectionAssert.Contains(plan.Tests.Single(t => t.Id == "t1").Reasons, "Common.props added: all tests of Shop.Tests");
        Assert.AreEqual(("untracked", "project"), (File(plan, "build/Common.props").Status, File(plan, "build/Common.props").Mapping));
        Assert.AreEqual(4, plan.Counts.Tests);
        await r.GitAsync("checkout", "-q", "--", ".");
        await r.GitAsync("clean", "-qfd");

        // output of test runs (Verify, TestResults) selects nothing
        r.Write("tests/Shop.Tests/OrdersTests.Total.received.txt", "x");
        r.Write("tests/Shop.Tests/TestResults/log.json", "{}");
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        Assert.AreEqual((2, 0, "none"), (plan.Counts.Files, plan.Counts.Tests, plan.Strategy));
        await r.GitAsync("clean", "-qfd");

        // nothing changed at all
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        Assert.AreEqual(("none", 0, 0), (plan.Strategy, plan.Counts.Files, plan.Counts.Tests));
    }

    [TestMethod]
    public async Task Without_impact_data_product_code_falls_back_to_coverage_then_to_the_project()
    {
        await using var r = await Repo.CreateAsync();
        // untracked product file: no impact data, no coverage → all tests of the project whose closure contains it
        r.Write("src/Shop/Helpers.cs", "namespace Shop; static class Helpers {}\n");
        var plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        Assert.AreEqual("project", plan.Strategy);
        Assert.IsTrue(plan.NeedsImpactMap);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t1", "Shop.Tests/t2", "Shop.Tests/t3" }, Ids(plan));
        Assert.AreEqual("no impact data for Helpers.cs: all tests of Shop.Tests", plan.Tests[0].Reasons.Single());
        var f = File(plan, "src/Shop/Helpers.cs");
        Assert.AreEqual(("untracked", "product", 3), (f.Status, f.Kind, f.Tests));
        CollectionAssert.AreEqual(new[] { "Shop.Tests" }, f.ProjectIds);

        // cached single-test coverage of Prices.cs → that test only
        System.IO.File.Delete(r.P("src/Shop/Helpers.cs"));
        r.Write("src/Shop/Prices.cs", "namespace Shop;\npublic static class Prices { public const int Base = 2; }\n");
        var report = new CoverageReport
        {
            RunId = "r", GeneratedAt = Json.Now(), ProjectIds = ["Shop.Tests"], Scope = "subset", TestIds = ["t2"],
            Files = [new FileCoverage { Path = r.P("src/Shop/Prices.cs"), RelativePath = "Prices.cs", Module = "Shop", LinesCovered = 1, LinesValid = 1, LineRate = 1, Lines = [] }],
        };
        lock (r.Explorer.Sync) r.Explorer.CoverageCacheState.Store("Shop.Tests", report, "stamp");
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        Assert.AreEqual("coverage", plan.Strategy);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t2" }, Ids(plan));
        Assert.AreEqual("Prices.cs changed (covered by cached coverage)", plan.Tests[0].Reasons.Single());

        // a non-code file inside a test project (test data) → all tests of that project, no impact map needed
        await r.GitAsync("checkout", "-q", "--", ".");
        r.Write("tests/Other.Tests/data.json", "{}");
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "Other.Tests/o1" }, Ids(plan));
        Assert.AreEqual("data.json added: all tests of Other.Tests", plan.Tests[0].Reasons.Single());
        Assert.IsNull(plan.NeedsImpactMap);
    }

    [TestMethod]
    public async Task Branch_compares_with_the_merge_base_and_range_with_commits()
    {
        await using var r = await Repo.CreateAsync();
        r.RecordImpact();
        var mainSha = await r.GitAsync("rev-parse", "--short", "HEAD");
        await r.GitAsync("checkout", "-q", "-b", "feature");
        r.Write("src/Shop/Orders.cs", Repo.Orders.Replace("qty * price", "qty * price + 0"));
        await r.CommitAllAsync("total");
        // main moves on too: its change must not count for the branch
        await r.GitAsync("checkout", "-q", "main");
        r.Write("tests/Other.Tests/OtherTests.cs", "class OtherTests { }\n");
        await r.CommitAllAsync("other");
        await r.GitAsync("checkout", "-q", "feature");
        r.Write("tests/Shop.Tests/New.cs", "class New {}\n"); // untracked, counts too

        var plan = await r.Explorer.PlanAffectedAsync("branch", null, TestContext.CancellationToken);
        Assert.AreEqual(("main", mainSha), (plan.Ref, plan.MergeBase));
        CollectionAssert.AreEquivalent(new[] { "src/Shop/Orders.cs", "tests/Shop.Tests/New.cs" }, plan.Files.Select(f => f.RelativePath).ToArray());
        Assert.AreEqual("mixed", plan.Strategy);
        Assert.AreEqual("Orders.cs changed (method Total, per-test data)", plan.Tests.Single(t => t.Id == "t2").Reasons[0]);
        Assert.AreEqual("main", plan.Git.DefaultBase);

        // explicit base, unknown base, invalid ref
        Assert.AreEqual("main", (await r.Explorer.PlanAffectedAsync("branch", "main", TestContext.CancellationToken)).Ref);
        var unknown = await Assert.ThrowsExactlyAsync<AffectedException>(() => r.Explorer.PlanAffectedAsync("branch", "nope", TestContext.CancellationToken));
        Assert.AreEqual((404, "unknownRef"), (unknown.Status, unknown.Code));
        var bad = await Assert.ThrowsExactlyAsync<AffectedException>(() => r.Explorer.PlanAffectedAsync("branch", "--output=/tmp/x", TestContext.CancellationToken));
        Assert.AreEqual((400, "badRef"), (bad.Status, bad.Code));

        // range: one commit, A..B, A...B
        var one = await r.Explorer.PlanAffectedAsync("range", "HEAD", TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "src/Shop/Orders.cs" }, one.Files.Select(f => f.RelativePath).ToArray());
        Assert.AreEqual("HEAD", one.Ref);
        var twoDots = await r.Explorer.PlanAffectedAsync("range", "main..feature", TestContext.CancellationToken);
        CollectionAssert.AreEquivalent(new[] { "src/Shop/Orders.cs", "tests/Other.Tests/OtherTests.cs" }, twoDots.Files.Select(f => f.RelativePath).ToArray());
        var threeDots = await r.Explorer.PlanAffectedAsync("range", "main...feature", TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "src/Shop/Orders.cs" }, threeDots.Files.Select(f => f.RelativePath).ToArray());
        var root = await r.GitAsync("rev-list", "--max-parents=0", "HEAD");
        Assert.AreEqual(9, (await r.Explorer.PlanAffectedAsync("range", root, TestContext.CancellationToken)).Counts.Files); // the root commit: everything it added
        Assert.AreEqual("added", (await r.Explorer.PlanAffectedAsync("range", root, TestContext.CancellationToken)).Files[0].Status);

        foreach (var (b, rf, status, code) in new[] { ("range", (string?)null, 400, "refRequired"), ("range", "a..b..c", 400, "badRef"),
                     ("range", "nope..HEAD", 404, "unknownRef"), ("range", "nope", 404, "unknownRef"), ("sideways", null, 400, "badBase") })
        {
            var ex = await Assert.ThrowsExactlyAsync<AffectedException>(() => r.Explorer.PlanAffectedAsync(b, rf, TestContext.CancellationToken));
            Assert.AreEqual((status, code), (ex.Status, ex.Code), $"{b} {rf}");
        }
    }

    [TestMethod]
    public async Task Renames_detached_head_and_the_configured_base()
    {
        await using var r = await Repo.CreateAsync();
        await r.GitAsync("mv", "src/Shop/Prices.cs", "src/Shop/Pricing.cs");
        var plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        var f = File(plan, "src/Shop/Pricing.cs");
        Assert.AreEqual(("renamed", "src/Shop/Prices.cs"), (f.Status, f.OldPath));
        Assert.AreEqual("no impact data for Pricing.cs: all tests of Shop.Tests", plan.Tests[0].Reasons.Single());
        await r.CommitAllAsync("rename");

        await r.GitAsync("checkout", "-q", "--detach", "HEAD~1");
        var info = await r.Explorer.GitInfoAsync(TestContext.CancellationToken);
        Assert.AreEqual((true, (string?)null, 0), (info.Detached, info.Branch, info.Dirty));
        Assert.IsNotNull(info.Head);
        plan = await r.Explorer.PlanAffectedAsync("branch", null, TestContext.CancellationToken); // detached HEAD vs main: main is ahead, nothing of ours
        Assert.AreEqual(0, plan.Counts.Files);

        // affectedBase (server option) wins over the detected default
        await r.GitAsync("checkout", "-q", "main");
        await r.GitAsync("branch", "develop", "HEAD~1");
        await using var e = new Explorer(new EventHub(), [r.ShopTests], null, [r.Root]) { AffectedBase = "develop" };
        plan = await e.PlanAffectedAsync("branch", null, TestContext.CancellationToken);
        Assert.AreEqual("develop", plan.Ref);
        CollectionAssert.AreEquivalent(new[] { "src/Shop/Pricing.cs" }, plan.Files.Select(x => x.RelativePath).ToArray());
        Assert.AreEqual(("develop", "develop"), (plan.Git.DefaultBase, plan.Git.AffectedBase));
    }

    [TestMethod]
    public async Task Clones_use_origin_HEAD_and_a_shallow_clone_without_merge_base_warns()
    {
        await using var r = await Repo.CreateAsync();
        await r.GitAsync("checkout", "-q", "-b", "feature");
        r.Write("src/Shop/Orders.cs", Repo.Orders + "\n");
        await r.CommitAllAsync("feature work");
        await r.GitAsync("checkout", "-q", "main");
        r.Write("README.md", "# main moved");
        await r.CommitAllAsync("main work");

        var clone = Path.Combine(r.Dir.FullName, "clone");
        var (code, _, err) = await GitRepo.RunAsync(r.Dir.FullName, ["clone", "-q", "--depth", "1", "--no-single-branch", "file://" + r.Root, clone], TestContext.CancellationToken);
        Assert.AreEqual(0, code, err);
        Assert.AreEqual(0, (await GitRepo.RunAsync(clone, ["checkout", "-q", "feature"], TestContext.CancellationToken)).Code);
        await using var e = new Explorer(new EventHub(), [Path.Combine(clone, "tests", "Shop.Tests", "Shop.Tests.csproj")], null, [clone]);
        var info = await e.GitInfoAsync(TestContext.CancellationToken);
        Assert.AreEqual(("origin/main", true, "feature", "origin/feature"), (info.DefaultBase, info.Shallow, info.Branch, info.Upstream));
        Assert.AreEqual((0, 0), (info.Ahead, info.Behind));

        var plan = await e.PlanAffectedAsync("branch", null, TestContext.CancellationToken);
        Assert.AreEqual("origin/main", plan.Ref);
        Assert.IsNull(plan.MergeBase);
        Assert.Contains("Shallow clone", plan.Warnings![0]);
        Assert.Contains("Shop.Tests has no discovered tests yet", plan.Warnings[1]);
        Assert.IsTrue(plan.Projects.Single().All);
        CollectionAssert.AreEquivalent(new[] { "README.md", "src/Shop/Orders.cs" }, plan.Files.Select(f => f.RelativePath).ToArray());
    }

    [TestMethod]
    public async Task A_repository_without_commits_and_a_folder_without_git()
    {
        await using var r = await Repo.CreateAsync(commit: false);
        var info = await r.Explorer.GitInfoAsync(TestContext.CancellationToken);
        Assert.AreEqual((true, "main", (string?)null, (string?)null), (info.Available, info.Branch, info.Head, info.DefaultBase));
        var plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken); // everything is untracked
        Assert.AreEqual(9, plan.Counts.Files);
        Assert.IsTrue(plan.Files.All(f => f.Status == "untracked"));
        Assert.AreEqual(4, plan.Counts.Tests);
        await r.GitAsync("add", "src/Shop/Orders.cs"); // staged against the empty tree
        Assert.AreEqual("added", File(await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken), "src/Shop/Orders.cs").Status);
        var noBase = await Assert.ThrowsExactlyAsync<AffectedException>(() => r.Explorer.PlanAffectedAsync("branch", null, TestContext.CancellationToken));
        Assert.AreEqual((404, "noBase"), (noBase.Status, noBase.Code));

        var plain = Directory.CreateTempSubdirectory("labcoat-nogit-");
        try
        {
            var proj = Path.Combine(plain.FullName, "A.Tests.csproj");
            System.IO.File.WriteAllText(proj, "<Project />");
            await using var e = new Explorer(new EventHub(), [proj], null, [plain.FullName]);
            var none = await e.GitInfoAsync(TestContext.CancellationToken);
            Assert.IsFalse(none.Available);
            Assert.Contains("not in a git repository", none.Error!);
            Assert.AreSame(none, await e.GitInfoAsync(TestContext.CancellationToken)); // cached
            var ex = await Assert.ThrowsExactlyAsync<AffectedException>(() => e.PlanAffectedAsync("working", null, TestContext.CancellationToken));
            Assert.AreEqual((409, "notGitRepo"), (ex.Status, ex.Code));
            var reply = await e.HandleCommandAsync(new ClientCommand { Type = "runAffected" });
            Assert.AreEqual(409, ((CommandRejectedEvent)reply!).Status);
        }
        finally { plain.Delete(recursive: true); }
    }

    [TestMethod]
    [DoNotParallelize] // changes the static git executable
    public async Task Missing_git_is_reported_as_unavailable()
    {
        await using var r = await Repo.CreateAsync(commit: false);
        var original = GitRepo.Executable;
        GitRepo.Executable = Path.Combine(r.Dir.FullName, "no-such-git");
        try
        {
            var info = await r.Explorer.GitInfoAsync(TestContext.CancellationToken);
            Assert.IsFalse(info.Available);
            Assert.Contains("git is not available", info.Error!);
            var ex = await Assert.ThrowsExactlyAsync<AffectedException>(() => r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken));
            Assert.AreEqual((409, "gitUnavailable"), (ex.Status, ex.Code));
        }
        finally { GitRepo.Executable = original; }
    }

    [TestMethod]
    public async Task Run_affected_queues_one_run_with_the_exact_selection()
    {
        await using var r = await Repo.CreateAsync();
        r.RecordImpact();
        r.Write("src/Shop/Orders.cs", Repo.Orders.Replace("qty > 0", "qty > 1"));
        r.Write("tests/Other.Tests/data.json", "{}");
        using (await r.Explorer.HoldOperationAsync("discovery"))
        {
            var result = await r.Explorer.RunAffectedAsync("working", null, coverage: null, buildImpactMap: false, trigger: "api", TestContext.CancellationToken);
            Assert.AreEqual((202, true), (result.Run!.Status, result.Run.Queued));
            var pending = typeof(Explorer).GetField("_pendingRun", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.Explorer)!;
            var selection = (Dictionary<string, HashSet<string>?>)pending.GetType().GetProperty("Selection")!.GetValue(pending)!;
            CollectionAssert.AreEquivalent(new[] { "t1" }, selection["Shop.Tests"]!.ToArray());
            Assert.IsNull(selection["Other.Tests"]); // whole project
            var affected = (AffectedRun)pending.GetType().GetProperty("Affected")!.GetValue(pending)!;
            Assert.AreEqual(("git", "working", 2), (affected.Strategy, affected.Base, affected.Tests));
            Assert.AreEqual("api", pending.GetType().GetProperty("Trigger")!.GetValue(pending));
            r.Explorer.Cancel();
        }

        // nothing affected: no run
        await r.GitAsync("checkout", "-q", "--", ".");
        await r.GitAsync("clean", "-qfd");
        var nothing = await r.Explorer.RunAffectedAsync("working", null, null, false, "user", TestContext.CancellationToken);
        Assert.IsNull(nothing.Run);
        Assert.AreEqual(0, nothing.Plan!.Counts.Tests);
        Assert.IsNull(await r.Explorer.HandleCommandAsync(new ClientCommand { Type = "runAffected", Base = "working" }));
        var badBase = await r.Explorer.HandleCommandAsync(new ClientCommand { Type = "runAffected", Base = "x" });
        Assert.AreEqual(400, ((CommandRejectedEvent)badBase!).Status);

        // the selection is checked again when the run is requested
        var gone = r.Explorer.RequestRun(new() { Selection = new() { ["Nope"] = null, ["Shop.Tests"] = ["zz"] } });
        Assert.AreEqual(404, gone.Status);
    }

    [TestMethod]
    public async Task Build_impact_map_first_starts_in_the_background_for_projects_without_data()
    {
        await using var r = await Repo.CreateAsync();
        r.Write("src/Shop/Helpers.cs", "namespace Shop; static class Helpers {}\n");
        r.Explorer.DotnetOverride = (_, _, _) => Task.FromResult((1, "error : no build here"));
        var result = await r.Explorer.RunAffectedAsync("working", null, null, buildImpactMap: true, trigger: "user", TestContext.CancellationToken);
        Assert.IsTrue(result.ImpactMapFirst);
        Assert.IsNull(result.Run);
        CollectionAssert.AreEqual(new[] { "Shop.Tests" }, result.Plan!.ProjectsNeedingImpactMap);
        // the pass (build fails here) and the run after it happen in the background; wait until the log says so
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !r.Explorer.Snapshot(includeLogs: true).Logs!.Any(l => l.Message.StartsWith("Run affected (uncommitted changes)")))
            await Task.Delay(50, TestContext.CancellationToken);
        Assert.Contains("building the impact map for Shop.Tests first", string.Join("\n", r.Explorer.Snapshot(includeLogs: true).Logs!.Select(l => l.Message)));
        r.Explorer.Cancel();
    }

    [TestMethod]
    public void Parsers_handle_name_status_hunks_quoting_and_refs()
    {
        var files = GitRepo.ParseNameStatus("M\0a.cs\0R087\0old.cs\0new.cs\0A\0b.cs\0D\0c.cs\0T\0d\0U\0e\0C100\0f\0g\0");
        CollectionAssert.AreEqual(new[] { "a.cs:modified", "new.cs:renamed", "b.cs:added", "c.cs:deleted", "d:typeChanged", "e:unmerged", "g:copied" },
            files.Select(f => $"{f.Path}:{f.Status}").ToArray());
        Assert.AreEqual("old.cs", files[1].OldPath);

        var hunks = GitRepo.ParseHunks("""
            diff --git a/x.cs b/x.cs
            --- a/x.cs
            +++ b/x.cs
            @@ -1 +1 @@
            -a
            +b
            @@ -3,0 +4,2 @@
            @@ -10,2 +6,0 @@
            @@ -20,1 +30,3 @@
            diff --git a/gone.cs b/gone.cs
            +++ /dev/null
            @@ -1,3 +0,0 @@
            diff --git "a/sp ace\tq.cs" "b/sp ace\tq.cs"
            +++ "b/sp ace\tq.cs"
            @@ -0,0 +1 @@
            diff --git a/o.cs b/n.cs
            rename from o.cs
            rename to n.cs
            @@ -0,0 +0,0 @@
            """.Replace("\r", ""));
        CollectionAssert.AreEqual(new[] { new LineSpan(1, 1), new LineSpan(4, 7), new LineSpan(30, 32) }, hunks["x.cs"]);
        Assert.IsFalse(hunks.ContainsKey("gone.cs"));
        CollectionAssert.AreEqual(new[] { new LineSpan(1, 1) }, hunks["sp ace\tq.cs"]);
        CollectionAssert.AreEqual(new[] { new LineSpan(1, 1) }, hunks["n.cs"]);

        Assert.AreEqual("café \"x\"\\", GitRepo.Unquote("\"caf\\303\\251 \\\"x\\\"\\\\\""));
        Assert.AreEqual("plain", GitRepo.Unquote("plain"));
        Assert.AreEqual("a\nb", GitRepo.Unquote("\"a\\nb\""));

        foreach (var ok in new[] { "main", "origin/main", "HEAD~3..HEAD", "a...b", "v1.2.3", "@{u}", "HEAD^1", "refs/heads/x" })
            Assert.IsTrue(GitRepo.IsValidRef(ok), ok);
        foreach (var bad in new[] { null, "", "-x", "--output=f", "a b", "a;b", "$(x)", "@{-1}", new string('a', 300) })
            Assert.IsFalse(GitRepo.IsValidRef(bad), bad);
    }

    [TestMethod]
    public async Task Class_level_impact_data_is_marked_and_queued_for_per_test_refinement()
    {
        await using var r = await Repo.CreateAsync();
        var file = PathUtil.Canonicalize(r.P("src/Shop/Orders.cs"));
        lock (r.Explorer.Sync)
            // the class pass: one coverage process ran t1..t3, so all three are credited with Validate and Total
            r.Explorer.ImpactMapState.Record("Shop.Tests", ["t1", "t2", "t3"], new() { [file] = [new LineSpan(4, 7) { Name = "Validate" }, new LineSpan(8, 11) { Name = "Total" }] });
        r.Write("src/Shop/Orders.cs", Repo.Orders.Replace("qty > 0", "qty > 1"));

        var plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t1", "Shop.Tests/t2", "Shop.Tests/t3" }, Ids(plan));
        Assert.AreEqual("Orders.cs changed (method Validate, class-level data)", plan.Tests[0].Reasons.Single());
        Assert.IsTrue(plan.Tests.All(t => t.Granularity == "class"));
        Assert.HasCount(3, plan.ClassLevel);

        // refinement (auto) has no test app to run here: it skips the project and the plan stays class level
        await r.Explorer.RefineImpactMapAsync(plan.ClassLevel);
        await r.Explorer.RefineImpactMapAsync([("Shop.Tests", "gone")]);
        Assert.AreEqual("class", (await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken)).Tests[0].Granularity);

        // per-test data for t1 (e.g. after the refinement): only t1 executes Validate
        lock (r.Explorer.Sync)
        {
            r.Explorer.ImpactMapState.Record("Shop.Tests", ["t1"], new() { [file] = [new LineSpan(4, 7) { Name = "Validate" }] });
            r.Explorer.ImpactMapState.Record("Shop.Tests", ["t2"], new() { [file] = [new LineSpan(8, 11) { Name = "Total" }] });
            r.Explorer.ImpactMapState.Record("Shop.Tests", ["t3"], new() { [file] = [new LineSpan(8, 11) { Name = "Total" }] });
        }
        plan = await r.Explorer.PlanAffectedAsync("working", null, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t1" }, Ids(plan));
        Assert.AreEqual(("test", "Orders.cs changed (method Validate, per-test data)"), (plan.Tests[0].Granularity, plan.Tests[0].Reasons.Single()));
        Assert.IsEmpty(plan.ClassLevel);
    }

    private const string OrdersTests = """
        class OrdersTests
        {
            void t1()
            {
            }
            void t2()
            {
            }
            void t3()
            {
            }
        }
        """;

    /// <summary>OrdersTests.cs committed with t1 (lines 3–5), t2 (6–8) and t3 (9–11); the other tests keep their line-less nodes.</summary>
    private static async Task<Repo> CreateWithTestLinesAsync()
    {
        var r = await Repo.CreateAsync();
        r.Write("tests/Shop.Tests/OrdersTests.cs", OrdersTests);
        await r.CommitAllAsync("tests");
        foreach (var (id, line) in new[] { ("t1", 3), ("t2", 6), ("t3", 9) })
        {
            var node = r.Node("Shop.Tests", id, "tests/Shop.Tests/OrdersTests.cs");
            node.Line = line;
            node.LineEnd = line + 2;
            r.Explorer.SeedTests([node]);
        }
        return r;
    }

    private static string[] Changed(ChangedTests c) => c.Tests.Select(t => $"{t.ProjectId}/{t.Id}").ToArray();

    [TestMethod]
    public async Task Only_the_tests_whose_lines_were_edited_are_changed()
    {
        await using var r = await CreateWithTestLinesAsync();
        var none = await r.Explorer.ChangedTestsAsync(TestContext.CancellationToken);
        Assert.IsTrue(none.Available);
        Assert.AreEqual("branch", none.Base);
        Assert.IsEmpty(none.Tests);

        // a line inside t2, and a product file (no tests of its own)
        r.Write("tests/Shop.Tests/OrdersTests.cs", OrdersTests.Replace("void t2()\n    {\n    }", "void t2()\n    {\n        var x = 1;\n    }"));
        r.Write("src/Shop/Prices.cs", "namespace Shop;\npublic static class Prices { public const int Base = 2; }\n");
        r.Explorer.InvalidateChangedTests();
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t2" }, Changed(await r.Explorer.ChangedTestsAsync(TestContext.CancellationToken)));
    }

    [TestMethod]
    public async Task Committed_branch_changes_and_new_test_files_count()
    {
        await using var r = await CreateWithTestLinesAsync();
        await r.GitAsync("switch", "-q", "-c", "feature");
        r.Write("tests/Shop.Tests/OrdersTests.cs", OrdersTests.Replace("void t1()\n    {\n    }", "void t1()\n    {\n        var y = 1;\n    }"));
        await r.CommitAllAsync("edit t1");
        r.Write("tests/Other.Tests/OtherTests.cs", "class OtherTests { void o1() {} }\n"); // o1 has no line range: its file counts
        var changed = await r.Explorer.ChangedTestsAsync(TestContext.CancellationToken);
        Assert.AreEqual("main", changed.Ref);
        CollectionAssert.AreEquivalent(new[] { "Shop.Tests/t1", "Other.Tests/o1" }, Changed(changed));

        // a test in a newly added (untracked) file is changed as a whole
        r.Write("tests/Other.Tests/NewTests.cs", "class NewTests {}\n");
        r.Explorer.SeedTests([r.Node("Other.Tests", "n1", "tests/Other.Tests/NewTests.cs")]);
        CollectionAssert.Contains(Changed(await r.Explorer.ChangedTestsAsync(TestContext.CancellationToken)), "Other.Tests/n1");
    }

    [TestMethod]
    public async Task Without_a_base_branch_the_uncommitted_changes_count_and_outside_a_repository_it_is_unavailable()
    {
        await using var r = await CreateWithTestLinesAsync();
        await r.GitAsync("branch", "-m", "trunk"); // no main / master: no base branch
        r.Write("tests/Shop.Tests/OrdersTests.cs", OrdersTests.Replace("void t3()\n    {\n    }", "void t3()\n    {\n        var z = 1;\n    }"));
        var changed = await r.Explorer.ChangedTestsAsync(TestContext.CancellationToken);
        Assert.AreEqual("working", changed.Base);
        CollectionAssert.AreEqual(new[] { "Shop.Tests/t3" }, Changed(changed));

        var dir = Directory.CreateTempSubdirectory("labcoat-nogit-");
        try
        {
            var project = Path.Combine(dir.FullName, "A.Tests.csproj");
            System.IO.File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await using var explorer = new Explorer(new EventHub(), [project], null, [dir.FullName]);
            var outside = await explorer.ChangedTestsAsync(TestContext.CancellationToken);
            Assert.IsFalse(outside.Available);
            Assert.IsNotNull(outside.Error);
            Assert.IsEmpty(outside.Tests);
        }
        finally { dir.Delete(recursive: true); }
    }

    private const string CartTests = """"
        namespace Shop;
        public class CartTests
        {
            [TestMethod]
            public void Adds_an_item()
            {
                var json = "{ not a brace";   // } neither
                Assert.AreEqual(1, 1);
            }

            [Test]
            [TestCase(1)]
            public async Task Removes_an_item(int n)
            {
                /* { */
                var raw = """{""";
                await Task.Delay(n);
            }

            [Fact]
            public int Total() => 1 + 2;

            [Test]
            public void Untouched()
            {
                Assert.IsTrue(true);
            }
        }
        """";

    [TestMethod]
    public void The_extent_of_a_method_comes_from_its_source_when_the_framework_reports_one_line_or_none()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-extent-");
        try
        {
            var file = Path.Combine(dir.FullName, "CartTests.cs");
            System.IO.File.WriteAllText(file, CartTests);
            Assert.AreEqual((4, 9), TestSourceRange.Find(file, 4, "Adds_an_item")); // MSTest: the [TestMethod] line
            Assert.AreEqual((4, 9), TestSourceRange.Find(file, null, "Adds_an_item")); // NUnit: no line, found by name
            Assert.AreEqual((11, 18), TestSourceRange.Find(file, null, "Removes_an_item(1)")); // attributes included, data row name
            Assert.AreEqual((20, 21), TestSourceRange.Find(file, 21, "Total")); // expression body
            Assert.IsNull(TestSourceRange.Find(file, null, "Missing"));
            Assert.IsNull(TestSourceRange.Find(Path.Combine(dir.FullName, "nope.cs"), 3, "X"));
            Assert.IsNull(TestSourceRange.Find(Path.ChangeExtension(file, ".fs"), 4, "Adds_an_item"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task A_body_edit_marks_the_test_whatever_the_framework_reports()
    {
        await using var r = await Repo.CreateAsync();
        r.Write("tests/Shop.Tests/CartTests.cs", CartTests);
        await r.CommitAllAsync("cart tests");
        TestNode Node(string id, string method, int? line, int? lineEnd)
        {
            var n = r.Node("Shop.Tests", id, "tests/Shop.Tests/CartTests.cs");
            (n.MethodName, n.Line, n.LineEnd) = (method, line, lineEnd);
            return n;
        }
        r.Explorer.SeedTests([
            Node("mstest", "Adds_an_item", 4, 4), // MSTest / xUnit: one line
            Node("nunit", "Removes_an_item", null, null), // NUnit: none
            Node("tunit", "Untouched", 24, 27), // TUnit: a real range
            Node("xunit", "Total", 21, 21),
        ]);
        // body-only edits of Adds_an_item (line 8) and Untouched (line 26)
        r.Write("tests/Shop.Tests/CartTests.cs", CartTests.Replace("Assert.AreEqual(1, 1)", "Assert.AreEqual(2, 2)").Replace("Assert.IsTrue(true)", "Assert.IsFalse(false)"));
        CollectionAssert.AreEquivalent(new[] { "Shop.Tests/mstest", "Shop.Tests/tunit" }, Changed(await r.Explorer.ChangedTestsAsync(TestContext.CancellationToken)));

        // an edit inside the NUnit test (the raw string line) and the expression-bodied one
        r.Write("tests/Shop.Tests/CartTests.cs", CartTests.Replace("Task.Delay(n)", "Task.Delay(n + 1)").Replace("1 + 2", "1 + 3"));
        r.Explorer.InvalidateChangedTests();
        CollectionAssert.AreEquivalent(new[] { "Shop.Tests/nunit", "Shop.Tests/xunit" }, Changed(await r.Explorer.ChangedTestsAsync(TestContext.CancellationToken)));
    }

    [TestMethod]
    public void A_pure_rename_changes_no_test_and_line_ranges_must_overlap()
    {
        var test = new TestNode { Id = "t", ProjectId = "P", DisplayName = "t", Line = 10, LineEnd = 12 };
        Assert.IsFalse(Explorer.IsChanged(test, new GitChange("a.cs", "renamed", "b.cs")));
        Assert.IsTrue(Explorer.IsChanged(test, new GitChange("a.cs", "modified")));
        Assert.IsTrue(Explorer.IsChanged(test, new GitChange("a.cs", "modified") { Lines = [new LineSpan(12, 14)] }));
        Assert.IsFalse(Explorer.IsChanged(test, new GitChange("a.cs", "modified") { Lines = [new LineSpan(13, 14), new LineSpan(1, 9)] }));
    }

    [TestMethod]
    public void Cobertura_method_names_are_readable_and_spans_compare_without_names()
    {
        Assert.AreEqual("ValidateAsync", ImpactMap.MethodName("Shop.Orders/<ValidateAsync>d__3", "MoveNext"));
        Assert.AreEqual("Validate", ImpactMap.MethodName("Shop.Orders", "<Validate>b__0"));
        Assert.AreEqual("Orders", ImpactMap.MethodName("Shop.Orders", ".ctor"));
        Assert.AreEqual("Total", ImpactMap.MethodName("Shop.Orders", "Total"));
        Assert.AreEqual(".cctor", ImpactMap.MethodName("", ".cctor"));
        Assert.IsNull(ImpactMap.MethodName("Shop.Orders", null));
        Assert.AreEqual(new LineSpan(1, 2), new LineSpan(1, 2) { Name = "X" });
        Assert.AreEqual(new LineSpan(1, 2).GetHashCode(), new LineSpan(1, 2) { Name = "X" }.GetHashCode());

        var map = new ImpactMap();
        map.Record("P", ["a"], new() { ["/f.cs"] = [new LineSpan(1, 5), new LineSpan(10, 12) { Name = "M" }] });
        var hits = map.AffectedByLines("P", "/f.cs", [new LineSpan(2, 2), new LineSpan(11, 11)], out var outside);
        Assert.IsFalse(outside);
        CollectionAssert.AreEqual(new[] { "lines 1–5", "M" }, hits!["a"]);
        Assert.IsNull(map.AffectedByLines("Q", "/f.cs", null, out _));
        Assert.HasCount(0, map.AffectedByLines("P", "/f.cs", null, out _)!["a"]);
    }
}
