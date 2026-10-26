using System.Diagnostics;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Api;

/// <summary>GET /api/git, GET /api/affected and POST /api/run-affected (contract "Run affected tests (git)"), plus the affectedBase option.</summary>
[TestClass]
public class GitApiTests
{
    public TestContext TestContext { get; set; }

    private static async Task Git(string dir, params string[] args)
    {
        var (code, _, err) = await GitRepo.RunAsync(dir, args);
        Assert.AreEqual(0, code, err);
    }

    [TestMethod]
    public async Task Outside_a_repository_the_endpoints_explain_why()
    {
        await using var h = await ApiHost.StartAsync();
        var git = await h.GetJson("/api/git");
        Assert.IsFalse(git["available"]!.GetValue<bool>());

        var changed = await h.GetJson("/api/changed-tests");
        Assert.IsFalse(changed["available"]!.GetValue<bool>());
        Assert.IsEmpty(changed["tests"]!.AsArray());

        using var res = await h.Client.GetAsync("/api/affected?base=working", TestContext.CancellationToken);
        Assert.AreEqual(409, (int)res.StatusCode);
        Assert.AreEqual("notGitRepo", (await ApiHost.Json(res))!["code"]!.GetValue<string>());
        using var bad = await h.Client.GetAsync("/api/affected?base=sideways", TestContext.CancellationToken);
        Assert.AreEqual(400, (int)bad.StatusCode);
        Assert.AreEqual("badBase", (await ApiHost.Json(bad))!["code"]!.GetValue<string>());

        using var run = await h.PostJson("/api/run-affected", "{}");
        Assert.AreEqual(409, (int)run.StatusCode);
        using var malformed = await h.PostJson("/api/run-affected", "{");
        Assert.AreEqual(400, (int)malformed.StatusCode);
    }

    [TestMethod]
    public async Task In_a_repository_the_plan_is_returned_and_run()
    {
        await using var h = await ApiHost.StartAsync();
        await Git(h.Workspace, "init", "-q", "-b", "main");
        await Git(h.Workspace, "config", "user.email", "t@example.com");
        await Git(h.Workspace, "config", "user.name", "T");
        var testFile = Path.Combine(h.Workspace, ApiHost.ProjectId, "CartTests.cs");
        File.WriteAllText(testFile, "class CartTests {}\n");
        await Git(h.Workspace, "add", "-A");
        await Git(h.Workspace, "-c", "commit.gpgsign=false", "commit", "-q", "-m", "init");
        h.Seed(h.Test("t1"), h.Test("t2"));

        using (var none = await h.PostJson("/api/run-affected", """{"base":"working"}"""))
        {
            Assert.AreEqual(200, (int)none.StatusCode);
            var body = (await ApiHost.Json(none))!;
            Assert.IsFalse(body["accepted"]!.GetValue<bool>());
            Assert.Contains("No tests are affected", body["reason"]!.GetValue<string>());
        }

        File.WriteAllText(testFile, "class CartTests { }\n");
        var plan = await h.GetJson("/api/affected");
        Assert.AreEqual("working", plan["base"]!.GetValue<string>());
        Assert.AreEqual(2, plan["counts"]!["tests"]!.GetValue<int>());
        Assert.AreEqual("A.Tests/CartTests.cs", plan["files"]![0]!["relativePath"]!.GetValue<string>());
        Assert.AreEqual("CartTests.cs changed (test file)", plan["tests"]![0]!["reasons"]![0]!.GetValue<string>());
        Assert.IsNull(plan["selection"]); // internal
        var git = await h.GetJson("/api/git");
        Assert.AreEqual(("main", 1), (git["branch"]!.GetValue<string>(), git["dirty"]!.GetValue<int>()));

        var changedTests = await h.GetJson("/api/changed-tests");
        Assert.IsTrue(changedTests["available"]!.GetValue<bool>());
        Assert.IsEmpty(changedTests["tests"]!.AsArray()); // the edit is on line 1, the seeded tests start at line 10

        using (var unknown = await h.Client.GetAsync("/api/affected?base=branch&ref=nope", TestContext.CancellationToken))
            Assert.AreEqual(404, (int)unknown.StatusCode);

        using var run = await h.PostJson("/api/run-affected", """{"base":"working","coverage":false}""");
        Assert.AreEqual(202, (int)run.StatusCode);
        var accepted = (await ApiHost.Json(run))!;
        Assert.AreEqual((2, 1, 1), (accepted["tests"]!.GetValue<int>(), accepted["files"]!.GetValue<int>(), accepted["projects"]!.GetValue<int>()));
        Assert.IsNotNull(accepted["runId"]);

        // product code outside the test project without impact data: buildImpactMap runs the pass first
        File.WriteAllText(h.ProjectPath!, """<Project><ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup></Project>""");
        Directory.CreateDirectory(Path.Combine(h.Workspace, "Lib"));
        File.WriteAllText(Path.Combine(h.Workspace, "Lib", "Lib.csproj"), "<Project />");
        await Git(h.Workspace, "add", "-A");
        await Git(h.Workspace, "-c", "commit.gpgsign=false", "commit", "-q", "-m", "lib");
        File.WriteAllText(Path.Combine(h.Workspace, "Lib", "Lib.cs"), "class Lib {}\n");
        var needs = await h.GetJson("/api/affected?base=working");
        Assert.IsTrue(needs["needsImpactMap"]!.GetValue<bool>());
        using var first = await h.PostJson("/api/run-affected", """{"buildImpactMap":true}""");
        Assert.AreEqual(202, (int)first.StatusCode);
        Assert.IsTrue((await ApiHost.Json(first))!["impactMapFirst"]!.GetValue<bool>());
        h.Explorer.Cancel();
    }

    [TestMethod]
    public void The_affectedBase_option_comes_from_the_command_line_and_settings_files()
    {
        Assert.AreEqual("origin/dev", StartOptions.Parse(["--affected-base", "origin/dev"], out _)!.AffectedBase);
        Assert.AreEqual("main", StartOptions.Parse(["--affected-base=main"], out _)!.AffectedBase);
        Assert.IsNull(StartOptions.Parse(["--affected-base", "-x"], out var error));
        Assert.Contains("--affected-base needs a git ref", error!);
        Assert.IsNull(StartOptions.Parse(["--affected-base"], out _));

        var dir = Directory.CreateTempSubdirectory("labcoat-affected-config-");
        try
        {
            var file = Path.Combine(dir.FullName, ".labcoat.json");
            File.WriteAllText(file, """{ "affectedBase": "origin/develop" }""");
            var config = LabcoatConfig.Load(new LabcoatConfig.Inputs { ConfigFile = file, NoUserConfig = true });
            Assert.AreEqual("origin/develop", config.AffectedBase);
            Assert.IsEmpty(config.Warnings);

            File.WriteAllText(file, """{ "affectedBase": "not a ref" }""");
            config = LabcoatConfig.Load(new LabcoatConfig.Inputs { ConfigFile = file, NoUserConfig = true });
            Assert.IsNull(config.AffectedBase);
            Assert.Contains("'affectedBase' must be a git ref", config.Warnings.Single());
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public void The_impactMap_option_comes_from_the_command_line_and_settings_files()
    {
        Assert.IsNull(StartOptions.Parse([], out _)!.ImpactMap);
        Assert.AreEqual("test", StartOptions.Parse(["--impact-map", "Test"], out _)!.ImpactMap);
        Assert.AreEqual("class", StartOptions.Parse(["--impact-map=class"], out _)!.ImpactMap);
        Assert.IsNull(StartOptions.Parse(["--impact-map", "method"], out var error));
        Assert.Contains("--impact-map needs class, test or auto", error!);
        Assert.IsNull(StartOptions.Parse(["--impact-map"], out _));

        var dir = Directory.CreateTempSubdirectory("labcoat-impact-config-");
        try
        {
            var file = Path.Combine(dir.FullName, ".labcoat.json");
            File.WriteAllText(file, """{ "impactMap": "test" }""");
            var config = LabcoatConfig.Load(new LabcoatConfig.Inputs { ConfigFile = file, NoUserConfig = true });
            Assert.AreEqual("test", config.ImpactMap);
            Assert.IsEmpty(config.Warnings);

            File.WriteAllText(file, """{ "impactMap": true }""");
            config = LabcoatConfig.Load(new LabcoatConfig.Inputs { ConfigFile = file, NoUserConfig = true });
            Assert.IsNull(config.ImpactMap);
            Assert.Contains("'impactMap' must be \"class\", \"test\" or \"auto\"", config.Warnings.Single());
        }
        finally { dir.Delete(recursive: true); }
    }
}
