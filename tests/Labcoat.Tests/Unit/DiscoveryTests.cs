using System.Net;
using Labcoat;
using Microsoft.AspNetCore.Http;

namespace Labcoat.Tests.Unit;

/// <summary>Test project discovery (solutions, directory scans, MSBuild detection cache), path helpers and request checks.</summary>
[TestClass]
public class DiscoveryTests
{
    private const string TestProject = """<Project Sdk="MSTest.Sdk/4.0.2" />""";
    private const string AppProject = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    /// <summary>A copy of Fixtures/Solutions with the projects they list (Missing.Tests is listed but absent).</summary>
    private static DirectoryInfo SolutionWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-sln-");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Solutions")))
            File.Copy(f, Path.Combine(root.FullName, Path.GetFileName(f)));
        Write(root, "src/Shop/Shop.csproj", AppProject);
        Write(root, "tests/Shop.Tests/Shop.Tests.csproj", TestProject);
        return root;
    }

    private static string Write(DirectoryInfo root, string relative, string text)
    {
        var path = Path.Combine(root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [TestMethod]
    [DataRow("Shop.slnx")]
    [DataRow("Shop.sln")]
    [DataRow("Shop.slnf")]
    public void Solutions_list_their_projects_and_only_test_projects_are_used(string solution)
    {
        var root = SolutionWorkspace();
        try
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            var found = ProjectDiscovery.FindProjects([Path.Combine(root.FullName, solution)], warnings.Add, errors);
            Assert.IsEmpty(errors);
            CollectionAssert.AreEqual(new[] { Path.Combine(root.FullName, "tests", "Shop.Tests", "Shop.Tests.csproj") }, found);
            if (solution == "Shop.slnx")
                Assert.StartsWith("Project in solution not found: " + Path.Combine(root.FullName, "tests", "Missing", "Missing.Tests.csproj"), warnings.Single());
            else
                Assert.IsEmpty(warnings);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Solution_files_are_parsed_with_windows_separators()
    {
        var root = SolutionWorkspace();
        try
        {
            var shop = Path.Combine(root.FullName, "src", "Shop", "Shop.csproj");
            var tests = Path.Combine(root.FullName, "tests", "Shop.Tests", "Shop.Tests.csproj");
            CollectionAssert.AreEqual(new[] { shop, tests }, ProjectDiscovery.ReadSolution(Path.Combine(root.FullName, "Shop.sln")).ToArray());
            CollectionAssert.AreEqual(new[] { shop, tests, Path.Combine(root.FullName, "tests", "Missing", "Missing.Tests.csproj") },
                ProjectDiscovery.ReadSolution(Path.Combine(root.FullName, "Shop.slnx")).ToArray());
            CollectionAssert.AreEqual(new[] { tests }, ProjectDiscovery.ReadSolution(Path.Combine(root.FullName, "Shop.slnf")).ToArray());
            var e = Assert.ThrowsExactly<InvalidOperationException>(() => ProjectDiscovery.ReadSolution(Path.Combine(root.FullName, "NotASolution.slnx")));
            Assert.Contains("<Solution>", e.Message);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Directory_scans_skip_build_output_dependencies_and_dot_folders()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-scan-");
        try
        {
            var a = Write(root, "A.Tests/A.Tests.csproj", TestProject);
            var b = Write(root, "nested/deeper/B.Tests.fsproj", TestProject);
            Write(root, "A.Tests/bin/Debug/Copy.Tests.csproj", TestProject);
            Write(root, "A.Tests/obj/Gen.Tests.csproj", TestProject);
            Write(root, "node_modules/pkg/Pkg.Tests.csproj", TestProject);
            Write(root, ".hidden/Hidden.Tests.csproj", TestProject);
            Write(root, "Lib/Lib.csproj", AppProject);              // not a test project, not an evaluation candidate
            Write(root, "README.md", "");

            var found = ProjectDiscovery.FindProjects([root.FullName, a], _ => { });
            CollectionAssert.AreEquivalent(new[] { a, b }, found); // the explicit path is not listed twice

            // An explicitly passed project file is always used, test project or not.
            var lib = Path.Combine(root.FullName, "Lib", "Lib.csproj");
            CollectionAssert.AreEqual(new[] { lib }, ProjectDiscovery.FindProjects([lib], _ => { }));

            // Without an error list, problems with explicit paths are warnings.
            var warnings = new List<string>();
            Assert.IsEmpty(ProjectDiscovery.FindProjects([Path.Combine(root.FullName, "nope")], warnings.Add));
            Assert.StartsWith("Path not found", warnings.Single());
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Evaluation_candidates_have_a_test_like_name_or_a_test_framework_in_a_directory_file()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-cand-");
        try
        {
            Assert.IsTrue(ProjectDiscovery.IsEvaluationCandidate(Write(root, "x/Shop.Specs.csproj", AppProject)));
            var plain = Write(root, "y/Shop.csproj", AppProject);
            Assert.IsFalse(ProjectDiscovery.IsEvaluationCandidate(plain));
            Write(root, "y/Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"xunit.v3\" Version=\"3.1.0\" /></ItemGroup></Project>");
            Assert.IsTrue(ProjectDiscovery.IsEvaluationCandidate(plain));
            CollectionAssert.Contains(ProjectDiscovery.DirectoryFiles(plain).ToList(), Path.Combine(root.FullName, "y", "Directory.Packages.props"));
            Assert.IsFalse(ProjectDiscovery.IsTestProject(Path.Combine(root.FullName, "missing.csproj")));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Evaluation_json_without_properties_or_items_is_not_a_test_project()
    {
        Assert.IsFalse(ProjectDiscovery.ParseEvaluation("{}")!.IsTestProject);
        Assert.IsFalse(ProjectDiscovery.ParseEvaluation("""{"Properties":{"IsTestProject":1},"Items":{"PackageReference":[{"Identity":null},{"Identity":2},{}]}}""")!.IsTestProject);
        Assert.AreEqual(TestRunner.Mtp, TestPlatform.Classify(new ProjectFacts(new Dictionary<string, string>(), [], ["MSTest.Sdk"])).Runner);
        Assert.AreEqual(TestRunner.None, TestPlatform.Classify(ProjectFacts.Empty).Runner);
    }

    [TestMethod]
    public void Vstest_test_projects_are_found_restored_or_not()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-vstest-");
        try
        {
            // not restored: the project file names a VSTest adapter (no "test" in its name, no evaluation needed)
            var nunit = Write(root, "legacy/Legacy.Checks.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" /><PackageReference Include="NUnit3TestAdapter" Version="6.3.0" /></ItemGroup>
                </Project>
                """);
            // Directory.Build.props adds the test packages (with a condition): only an MSBuild evaluation sees it; a
            // project the condition leaves out stays out
            Write(root, "suite/Directory.Build.props", """
                <Project>
                  <ItemGroup Condition="$(MSBuildProjectName.EndsWith('.Tests'))">
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
                    <PackageReference Include="MSTest.TestAdapter" Version="4.0.2" />
                  </ItemGroup>
                </Project>
                """);
            var viaProps = Write(root, "suite/Shop.Tests/Shop.Tests.csproj", AppProject);
            Write(root, "suite/Shop/Shop.csproj", AppProject);

            var found = ProjectDiscovery.FindProjects([root.FullName], _ => { });
            CollectionAssert.AreEquivalent(new[] { nunit, viaProps }, found);
            Assert.AreEqual(TestRunner.VsTest, TestPlatform.Classify(TestPlatform.ReadStatic(nunit)).Runner);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task An_msbuild_evaluation_of_an_unrestored_project_sees_its_test_packages()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-vstest-eval-");
        try
        {
            var project = Write(root, "X.Tests/X.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
                  <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" /><PackageReference Include="xunit" Version="2.9.3" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" /></ItemGroup>
                </Project>
                """);
            var info = await ProjectDiscovery.EvaluateAsync(project, TestContext.CancellationToken);
            Assert.IsNotNull(info);
            Assert.AreEqual((TestRunner.VsTest, TestPlatform.XUnitV2), (info.Runner, info.Framework));
        }
        finally { root.Delete(recursive: true); }
    }

    public TestContext TestContext { get; set; }

    private static string DetectionCacheFile =>
        Path.Combine(Environment.GetEnvironmentVariable("LABCOAT_HISTORY_DIR")!, ProjectDiscovery.DetectionCacheFileName);

    /// <summary>The cache stamp: project file, obj/project.assets.json and the Directory.* files above it (write times).</summary>
    private static string Stamp(string project)
    {
        var files = new List<string> { project, Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json") };
        files.AddRange(ProjectDiscovery.DirectoryFiles(project));
        return string.Join("|", files.Select(f => File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks.ToString() : "-"));
    }

    [TestMethod]
    [DoNotParallelize] // reads / writes the shared detection cache file
    public void Msbuild_detection_results_are_cached_by_file_timestamps()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-cache-");
        var saved = File.Exists(DetectionCacheFile) ? File.ReadAllText(DetectionCacheFile) : null;
        try
        {
            // The text heuristic says no; the (pretend) evaluation result in the cache says yes.
            var cached = Write(root, "Integration.Tests/Integration.Tests.csproj", AppProject);
            Directory.CreateDirectory(Path.GetDirectoryName(DetectionCacheFile)!);
            File.WriteAllText(DetectionCacheFile, $$"""
                { "{{cached.Replace("\\", "\\\\")}}": { "stamp": "{{Stamp(cached)}}", "isTest": true },
                  "/elsewhere/Other.csproj": { "stamp": "1|-", "isTest": false },
                  "/broken.csproj": { "isTest": true } }
                """);
            CollectionAssert.AreEqual(new[] { cached }, ProjectDiscovery.FindProjects([root.FullName], _ => { }));

            // A stale stamp (and a corrupt cache file, which is ignored) means: evaluate again. A project MSBuild can't
            // evaluate is not a test project, and is not cached.
            File.WriteAllText(cached, "<Project");
            File.WriteAllText(DetectionCacheFile, "{ nope");
            var notes = new List<string>();
            Assert.IsEmpty(ProjectDiscovery.FindProjects([root.FullName], _ => { }, info: notes.Add));
            Assert.StartsWith("Evaluated 1 project(s) with MSBuild", notes.Single());
            Assert.AreEqual("{ nope", File.ReadAllText(DetectionCacheFile)); // nothing new to save
        }
        finally
        {
            if (saved is null) File.Delete(DetectionCacheFile);
            else File.WriteAllText(DetectionCacheFile, saved);
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Evaluation_can_be_cancelled()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-eval-");
        try
        {
            var project = Write(root, "A.Tests.csproj", AppProject);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.IsNull(await ProjectDiscovery.EvaluateAsync(project, cts.Token));
        }
        finally { root.Delete(recursive: true); }
    }

    // ------------------------------------------------------------------ PathUtil

    [TestMethod]
    public void Common_root_of_paths()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-root-");
        try
        {
            var a = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "x")).FullName;
            var file = Path.Combine(root.FullName, "a", "y", "Y.csproj"); // a file (need not exist) contributes its directory
            Assert.AreEqual(Path.Combine(root.FullName, "a"), PathUtil.CommonRoot([a, file]));
            Assert.AreEqual(a, PathUtil.CommonRoot([a]));
            Assert.IsNull(PathUtil.CommonRoot([]));
            if (!OperatingSystem.IsWindows()) Assert.IsNull(PathUtil.CommonRoot(["/usr/x", "/opt/y"])); // only "/" in common
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Canonicalize_keeps_unknown_paths_and_falls_back_on_errors()
    {
        var missing = Path.Combine(Path.GetTempPath(), "labcoat-nope", "x.cs");
        Assert.EndsWith(Path.Combine("labcoat-nope", "x.cs"), PathUtil.Canonicalize(missing));
        Assert.AreEqual("bad\0path", PathUtil.CanonicalizeOrSelf("bad\0path"));
        Assert.IsFalse(PathUtil.IsUnder("/a/b", ""));
        Assert.AreEqual(OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase, PathUtil.Comparison);
    }

    // ------------------------------------------------------------------ RequestGuard / SecurityHeaders

    private static DefaultHttpContext Request(string method, string path, string? host = "localhost:4300", string? origin = null, string scheme = "http")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = scheme;
        ctx.Request.Path = path;
        if (host is not null) ctx.Request.Host = new HostString(host);
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        return ctx;
    }

    [TestMethod]
    [DoNotParallelize]
    public void Guard_from_environment_reads_hosts_origins_and_token()
    {
        var saved = new[] { "LABCOAT_ALLOWED_HOSTS", "LABCOAT_ALLOWED_ORIGINS", "LABCOAT_TOKEN" }.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("LABCOAT_ALLOWED_HOSTS", "devbox; *");
            Environment.SetEnvironmentVariable("LABCOAT_ALLOWED_ORIGINS", "http://localhost:5174, not-a-url");
            Environment.SetEnvironmentVariable("LABCOAT_TOKEN", "t0k3n");
            var guard = RequestGuard.FromEnvironment(["bindhost"]);
            Assert.IsTrue(guard.AllowsAnyHost);
            Assert.IsTrue(guard.RequiresToken);
            Assert.IsTrue(guard.IsAllowedOrigin("http://LOCALHOST:5174", "http", new HostString("localhost:4300")));

            foreach (var (n, _) in saved) Environment.SetEnvironmentVariable(n, null);
            var plain = RequestGuard.FromEnvironment([]);
            Assert.IsFalse(plain.AllowsAnyHost);
            Assert.IsFalse(plain.RequiresToken);
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
        }
    }

    [TestMethod]
    public void Guard_edge_cases()
    {
        var guard = new RequestGuard(["", " ", "[fe80::1]", "Host.Example."]);
        Assert.IsTrue(guard.IsAllowedHost("fe80::1"));
        Assert.IsTrue(guard.IsAllowedHost("host.example"));
        Assert.IsNull(RequestGuard.NormalizeHost("[]"));
        Assert.IsEmpty(RequestGuard.SplitList(null));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, RequestGuard.SplitList(" a,b; c ").ToArray());
        Assert.IsEmpty(RequestGuard.HostsOfUrls("not a url;http://[::]:80"));

        Assert.AreEqual(400, guard.Check(Request("GET", "/", host: null))?.Status); // no Host header at all
        Assert.IsNull(guard.Check(Request("GET", "/assets/a.js", origin: "http://evil.example")));
        Assert.IsNull(guard.Check(Request("OPTIONS", "/api/run", origin: "http://evil.example")));
        Assert.IsNull(guard.Check(Request("HEAD", "/api/state")));
        Assert.IsNull(guard.Check(Request("GET", "/ws", origin: "https://localhost", host: "localhost", scheme: "https"))); // default ports
        Assert.AreEqual(403, guard.Check(Request("GET", "/ws", origin: "https://localhost", host: "localhost", scheme: "http"))?.Status);
        Assert.AreEqual(403, guard.Check(Request("GET", "/ws", origin: "ftp://localhost:4300"))?.Status);
        Assert.AreEqual(403, guard.Check(Request("GET", "/ws", origin: "http://127.0.0.1:4300"))?.Status); // different host name
        var sameSite = Request("POST", "/api/cancel");
        sameSite.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.IsNull(guard.Check(sameSite));
        var noRemote = Request("POST", "/api/cancel");
        noRemote.Connection.RemoteIpAddress = null;
        Assert.IsNull(guard.Check(noRemote));
        var chunked = Request("POST", "/api/watch");
        chunked.Request.Headers.TransferEncoding = "chunked";
        Assert.AreEqual(415, guard.Check(chunked)?.Status);
        Assert.IsFalse(RequestGuard.IsJson(null));
        Assert.IsFalse(RequestGuard.IsJson("text/json"));
    }

    [TestMethod]
    public void Csp_without_a_host_or_index_html_has_no_websocket_or_hashes()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-csp-");
        try
        {
            var headers = new SecurityHeaders(dir.FullName);
            var csp = headers.ContentSecurityPolicy(default);
            Assert.Contains("script-src 'self';", csp);
            Assert.Contains("connect-src 'self';", csp);

            File.WriteAllText(Path.Combine(dir.FullName, "index.html"), "<script>a()</script><script></script><SCRIPT SRC='x.js'></SCRIPT>");
            var first = headers.ContentSecurityPolicy(new HostString("localhost"));
            Assert.AreEqual(first, headers.ContentSecurityPolicy(new HostString("localhost"))); // cached until index.html changes
            Assert.AreEqual(1, first.Split("'sha256-").Length - 1);
            var ctx = new DefaultHttpContext();
            headers.Apply(ctx);
            Assert.AreEqual("DENY", ctx.Response.Headers.XFrameOptions.ToString());
            Assert.AreEqual("same-origin", ctx.Response.Headers["Cross-Origin-Opener-Policy"].ToString());
        }
        finally { dir.Delete(recursive: true); }
    }
}
