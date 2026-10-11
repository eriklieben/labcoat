using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>The resource AddLabcoat models: command line, environment, endpoint, health check, commands, options.</summary>
[TestClass]
public class AddLabcoatTests
{
    private static DirectoryInfo s_root = null!;
    private static string s_appHost = "";

    /// <summary>repo/Shop.slnx, repo/src/Shop.AppHost (the AppHost directory), repo/tests/Shop.Tests.</summary>
    [ClassInitialize]
    public static void Init(TestContext _)
    {
        s_root = Directory.CreateTempSubdirectory("labcoat-hosting-");
        File.WriteAllText(Path.Combine(s_root.FullName, "Shop.slnx"), "<Solution />");
        s_appHost = Directory.CreateDirectory(Path.Combine(s_root.FullName, "src", "Shop.AppHost")).FullName;
        Directory.CreateDirectory(Path.Combine(s_root.FullName, "tests", "Shop.Tests"));
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        try { s_root.Delete(recursive: true); } catch { }
    }

    [TestMethod]
    public async Task Without_test_projects_the_bundled_explorer_scans_the_solution_root()
    {
        var builder = Hosting.Builder(s_appHost);
        var rb = builder.AddLabcoat("tests");

        Assert.AreEqual("dotnet", rb.Resource.Command);
        Assert.AreEqual(s_root.FullName, rb.Resource.SolutionRoot);
        var bundle = Path.Combine(AppContext.BaseDirectory, LabcoatResourceBuilderExtensions.BundleFolderName);
        Assert.AreEqual(bundle, rb.Resource.WorkingDirectory); // the test output carries the bundle (ProjectReference)
        CollectionAssert.AreEqual(new[] { Path.Combine(bundle, "Labcoat.dll"), s_root.FullName, "--root", s_root.FullName, "--no-open" },
            await Hosting.ArgsAsync(rb.Resource));
    }

    [TestMethod]
    public async Task Coverage_settings_and_filters_become_arguments()
    {
        File.WriteAllText(Path.Combine(s_appHost, "apphost.coverage.xml"), "<Configuration />");
        var builder = Hosting.Builder(s_appHost);
        var rb = builder.AddLabcoat("tests", o =>
        {
            o.CoverageSettings = "apphost.coverage.xml"; // exists under the AppHost directory: passed absolute
            o.CoverageInclude.Add("Labcoat.*");
            o.CoverageExclude.Add("Shop.Tools");
        }).WithCoverageFilter(exclude: ["**/Migrations/**", "Shop.Tools"]);
        var args = await Hosting.ArgsAsync(rb.Resource);
        var tail = args.SkipWhile(a => a != "--coverage-settings").ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "--coverage-settings", Path.Combine(s_appHost, "apphost.coverage.xml"),
            "--coverage-include", "Labcoat.*",
            "--coverage-exclude", "Shop.Tools,**/Migrations/**",
        }, tail);

        // not under the AppHost directory: left relative, for Labcoat to find next to each test project
        rb.WithCoverageSettings("coverage.settings.xml");
        Assert.AreEqual("coverage.settings.xml", rb.Resource.CoverageSettings);
        Assert.ThrowsExactly<ArgumentException>(() => rb.WithCoverageSettings(" "));
        Assert.ThrowsExactly<ArgumentException>(() => rb.WithCoverageFilter(include: ["a,b"]));
        Assert.ThrowsExactly<ArgumentException>(() => rb.WithCoverageFilter(exclude: ["--x"]));
        Assert.ThrowsExactly<ArgumentException>(() => rb.WithCoverageFilter(include: [" "]));
        Assert.ThrowsExactly<ArgumentException>(() => rb.WithCoverageFilter(include: [null!]));
        rb.WithCoverageFilter(); // nothing to add
        // an absolute path is passed as it is
        var absolute = Path.Combine(s_root.FullName, "elsewhere.xml");
        Assert.AreEqual(absolute, rb.WithCoverageSettings(absolute).Resource.CoverageSettings);
        Assert.IsFalse((await Hosting.ArgsAsync(Hosting.Builder(s_appHost).AddLabcoat("plain").Resource)).Any(a => a.StartsWith("--coverage-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Test_projects_and_start_options_become_arguments()
    {
        var builder = Hosting.Builder(s_appHost);
        var rb = builder.AddLabcoat("tests")
            .WithTestProject("../../tests/Shop.Tests")
            .WithTestProject("../../tests/Shop.Tests") // same path twice: once
            .WithTestProject(Path.Combine(s_root.FullName, "Other.Tests.csproj"))
            .WithWatch().WithCoverage().WithTestTracing();

        var expectedProject = Path.Combine(s_root.FullName, "tests", "Shop.Tests");
        CollectionAssert.AreEqual(new[] { expectedProject, Path.Combine(s_root.FullName, "Other.Tests.csproj") }, rb.Resource.TestProjects.ToArray());
        var args = await Hosting.ArgsAsync(rb.Resource);
        CollectionAssert.AreEqual(new[] { expectedProject, Path.Combine(s_root.FullName, "Other.Tests.csproj"), "--root", s_root.FullName, "--no-open", "--watch", "--coverage", "--traces" },
            args.Skip(1).ToArray());

        rb.WithWatch(false).WithCoverage(false).WithTestTracing(false);
        Assert.IsFalse((await Hosting.ArgsAsync(rb.Resource)).Any(a => a is "--watch" or "--coverage" or "--traces"));
    }

    [TestMethod]
    [DoNotParallelize] // changes the static bundle lookup directory
    public async Task Without_the_bundle_it_falls_back_to_dotnet_tool_exec()
    {
        var original = LabcoatResourceBuilderExtensions.BaseDirectory;
        LabcoatResourceBuilderExtensions.BaseDirectory = s_root.FullName; // no labcoat/Labcoat.dll here
        try
        {
            var rb = Hosting.Builder(s_appHost).AddLabcoat("tests");
            Assert.AreEqual(s_root.FullName, rb.Resource.WorkingDirectory); // the solution root
            // pinned to this package's version (a prerelease such as 1.1.0-beta.1 resolves without --prerelease)
            var version = LabcoatResourceBuilderExtensions.ToolPackageVersion;
            Assert.IsFalse(string.IsNullOrEmpty(version));
            Assert.DoesNotContain("+", version);
            CollectionAssert.AreEqual(new[] { "tool", "exec", $"Labcoat@{version}", "--yes", "--", s_root.FullName, "--root", s_root.FullName, "--no-open" },
                await Hosting.ArgsAsync(rb.Resource));
        }
        finally { LabcoatResourceBuilderExtensions.BaseDirectory = original; }
    }

    [TestMethod]
    public async Task Environment_carries_token_name_hosts_and_the_dashboard_api()
    {
        var builder = Hosting.Builder(s_appHost);
        builder.Configuration["ASPNETCORE_URLS"] = "https://localhost:17001; http://localhost:15001";
        builder.Configuration["AppHost:DashboardApiKey"] = "dash-key";
        var rb = builder.AddLabcoat("suite");

        var env = await Hosting.EnvironmentAsync(rb.Resource);
        Assert.AreEqual(rb.Resource.ApiToken, env["LABCOAT_TOKEN"]);
        Assert.AreEqual(32, rb.Resource.ApiToken.Length); // 16 random bytes, hex
        Assert.AreEqual("suite", env["LABCOAT_NAME"]);
        Assert.AreEqual("1", env["DOTNET_NOLOGO"]);
        Assert.AreEqual("true", env["LabcoatSkipBundle"]);
        Assert.IsInstanceOfType<EndpointReferenceExpression>(env["LABCOAT_ALLOWED_HOSTS"]);
        Assert.AreEqual("https://localhost:17001", env["ASPIRE_DASHBOARD_URL"]);
        Assert.AreEqual("http://localhost:15001", env["LABCOAT_DASHBOARD_API_URL"]); // plain http preferred for the API
        Assert.AreEqual("dash-key", env["LABCOAT_DASHBOARD_API_KEY"]);
        Assert.IsFalse(env.ContainsKey(LabcoatResourceBuilderExtensions.UiSettingsEnvironmentVariable)); // nothing set: not passed
        Assert.IsTrue(env.ContainsKey("PORT"));
    }

    [TestMethod]
    public async Task Https_only_dashboard_is_used_for_the_api_and_no_key_means_no_key_variable()
    {
        var builder = Hosting.Builder(s_appHost);
        builder.Configuration["ASPNETCORE_URLS"] = "https://localhost:17002";
        var env = await Hosting.EnvironmentAsync(builder.AddLabcoat("tests").Resource);
        Assert.AreEqual("https://localhost:17002", env["LABCOAT_DASHBOARD_API_URL"]);
        Assert.IsFalse(env.ContainsKey("LABCOAT_DASHBOARD_API_KEY"));

        var noDashboard = await Hosting.EnvironmentAsync(Hosting.Builder(s_appHost).AddLabcoat("tests").Resource);
        Assert.IsFalse(noDashboard.ContainsKey("ASPIRE_DASHBOARD_URL"));
        Assert.IsFalse(noDashboard.ContainsKey("LABCOAT_DASHBOARD_API_URL"));
    }

    [TestMethod]
    public void Http_endpoint_health_check_icon_and_state_are_configured()
    {
        var builder = Hosting.Builder(s_appHost);
        var rb = builder.AddLabcoat("tests");

        var endpoint = rb.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        Assert.AreEqual("http", endpoint.Name);
        Assert.AreEqual("http", endpoint.UriScheme);
        Assert.AreSame(rb.Resource, rb.Resource.HttpEndpoint.Resource);
        Assert.AreEqual("http", rb.Resource.HttpEndpoint.EndpointName);
        Assert.HasCount(1, rb.Resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.HasCount(1, rb.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()); // excluded from the manifest

        rb.WithHostPort(4542);
        Assert.AreEqual(4542, endpoint.Port);
    }

    /// <summary>
    /// The resource used to show a "Reference" to itself in the dashboard: LABCOAT_ALLOWED_HOSTS was set with
    /// WithEnvironment(name, endpointExpression), which adds a relationship to each resource in the expression.
    /// </summary>
    [TestMethod]
    public async Task The_resource_does_not_reference_itself_and_keeps_the_users_relationships()
    {
        var builder = Hosting.Builder(s_appHost);
        var group = builder.AddParameter("group", "x");
        var api = builder.AddExecutable("api", "dotnet", s_appHost).WithHttpEndpoint(name: "http");
        var rb = builder.AddLabcoat("tests").WithParentRelationship(group).WithEnvironment("API", api.GetEndpoint("http"));

        var relationships = rb.Resource.Annotations.OfType<ResourceRelationshipAnnotation>().ToList();
        Assert.IsFalse(relationships.Any(r => ReferenceEquals(r.Resource, rb.Resource)),
            string.Join(", ", relationships.Select(r => $"{r.Type} -> {r.Resource.Name}")));
        CollectionAssert.AreEquivalent(new[] { "Parent -> group", "Reference -> api" }, relationships.Select(r => $"{r.Type} -> {r.Resource.Name}").ToArray());
        // The host name still reaches the explorer (resolved when the resource starts).
        Assert.IsInstanceOfType<EndpointReferenceExpression>((await Hosting.EnvironmentAsync(rb.Resource))["LABCOAT_ALLOWED_HOSTS"]);
    }

    [TestMethod]
    public async Task Pass_through_variables_from_configuration_and_code_reach_the_explorer()
    {
        var builder = Hosting.Builder(s_appHost);
        builder.Configuration["Labcoat:PassThroughEnvironment:0"] = "OTEL_*";
        builder.Configuration["Labcoat:Resources:tests:PassThroughEnvironment"] = "ASPNETCORE_URLS; aspire_container_runtime";
        var rb = builder.AddLabcoat("tests", o => o.PassThroughEnvironment.Add("DEBUG_SESSION_PORT"))
            .WithPassThroughEnvironment("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "OTEL_*")
            .WithTestEnvironment("ASPIRE_DASHBOARD_URL", "http://nested");

        CollectionAssert.AreEqual(
            new[] { "ASPIRE_ALLOW_UNSECURED_TRANSPORT", "aspire_container_runtime", "ASPIRE_DASHBOARD_URL", "ASPNETCORE_URLS", "DEBUG_SESSION_PORT", "OTEL_*" },
            rb.Resource.PassThroughEnvironment.ToArray(), "sorted, case-insensitive, once each");
        var env = await Hosting.EnvironmentAsync(rb.Resource);
        Assert.AreEqual("ASPIRE_ALLOW_UNSECURED_TRANSPORT;aspire_container_runtime;ASPIRE_DASHBOARD_URL;ASPNETCORE_URLS;DEBUG_SESSION_PORT;OTEL_*",
            env[LabcoatResourceBuilderExtensions.PassThroughEnvironmentVariable]);
        Assert.AreEqual("http://nested", env["ASPIRE_DASHBOARD_URL"]?.ToString()); // WithTestEnvironment sets it, too

        Assert.IsFalse((await Hosting.EnvironmentAsync(Hosting.Builder(s_appHost).AddLabcoat("plain").Resource))
            .ContainsKey(LabcoatResourceBuilderExtensions.PassThroughEnvironmentVariable)); // nothing to keep: not passed

        foreach (var bad in new[] { "", " ", "*", "A;B", "A,B", "A=1" })
            Assert.ThrowsExactly<ArgumentException>(() => rb.WithPassThroughEnvironment(bad), bad);
        Assert.ThrowsExactly<ArgumentNullException>(() => rb.WithPassThroughEnvironment(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithPassThroughEnvironment(null!, "A"));
        Assert.ThrowsExactly<ArgumentException>(() => rb.WithTestEnvironment(" ", "x"));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithTestEnvironment(null!, "A", "x"));
    }

    [TestMethod]
    public void Seven_dashboard_commands_are_registered()
    {
        var rb = Hosting.Builder(s_appHost).AddLabcoat("tests");
        var commands = rb.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        CollectionAssert.AreEqual(
            new[] { "run-all-tests", "run-failed-tests", "cancel-run", "restart-test-host", "toggle-watch", "toggle-coverage", "toggle-tracing" },
            commands.Select(c => c.Name).ToArray());
        Assert.IsTrue(commands.Single(c => c.Name == "run-all-tests").IsHighlighted);
        Assert.IsTrue(commands.All(c => !string.IsNullOrEmpty(c.DisplayDescription) && !string.IsNullOrEmpty(c.IconName)));
    }

    [TestMethod]
    public void Options_from_configuration_and_code_reach_the_resource()
    {
        var builder = Hosting.Builder(s_appHost);
        builder.Configuration["Labcoat:Coverage"] = "true";
        builder.Configuration["Labcoat:TestProjects:0"] = "../../tests/Shop.Tests";
        builder.Configuration["Labcoat:Resources:tests:SuiteResultState"] = "false";
        builder.Configuration["Labcoat:Resources:other:Watch"] = "true"; // another resource's section: ignored
        var rb = builder.AddLabcoat("tests", o => o.HostPort = 4543);

        Assert.IsTrue(rb.Resource.CoverageOnStart);
        Assert.IsFalse(rb.Resource.WatchOnStart);
        Assert.IsFalse(rb.Resource.SuiteResultState);
        CollectionAssert.AreEqual(new[] { Path.Combine(s_root.FullName, "tests", "Shop.Tests") }, rb.Resource.TestProjects.ToArray());
        Assert.AreEqual(4543, rb.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);

        rb.WithSuiteResultState();
        Assert.IsTrue(rb.Resource.SuiteResultState);
        rb.WithSuiteResultState(false);
        Assert.IsFalse(rb.Resource.SuiteResultState);
    }

    [TestMethod]
    public void Publish_mode_adds_only_the_resource()
    {
        var output = Path.Combine(s_root.FullName, "manifest-out");
        var builder = Hosting.Builder(s_appHost, "--operation", "publish", "--publisher", "manifest", "--output-path", output);
        Assert.IsTrue(builder.ExecutionContext.IsPublishMode);

        var rb = builder.AddLabcoat("tests", o => o.HostPort = 4544);
        Assert.AreEqual("tests", rb.Resource.Name);
        Assert.IsEmpty(rb.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.IsEmpty(rb.Resource.Annotations.OfType<ResourceCommandAnnotation>());
        Assert.DoesNotContain(rb.Resource, builder.Resources); // not part of the model
    }

    [TestMethod]
    public void Arguments_are_validated()
    {
        var builder = Hosting.Builder(s_appHost);
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.AddLabcoat(null!, "x"));
        Assert.ThrowsExactly<ArgumentException>(() => builder.AddLabcoat(""));
        var rb = builder.AddLabcoat("tests");
        Assert.ThrowsExactly<ArgumentException>(() => rb.WithTestProject(""));
        Assert.ThrowsExactly<ArgumentNullException>(() => rb.WithUiSettings(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithWatch(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithCoverage(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithTestTracing(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithSuiteResultState(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithHostPort(null!, 1));
        Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithTestProject(null!, "x"));
    }

    [TestMethod]
    public void Solution_root_is_the_nearest_directory_with_a_solution_else_the_parent()
    {
        Assert.AreEqual(s_root.FullName, LabcoatResourceBuilderExtensions.FindSolutionRoot(s_appHost));
        var lone = Directory.CreateTempSubdirectory("labcoat-nosln-");
        try
        {
            var inner = Directory.CreateDirectory(Path.Combine(lone.FullName, "App")).FullName;
            Assert.AreEqual(lone.FullName, LabcoatResourceBuilderExtensions.FindSolutionRoot(inner));
            File.WriteAllText(Path.Combine(inner, "App.sln"), "");
            Assert.AreEqual(inner, LabcoatResourceBuilderExtensions.FindSolutionRoot(inner));
        }
        finally { lone.Delete(recursive: true); }
    }
}
