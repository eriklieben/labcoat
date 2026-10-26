using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>
/// Runners / WithRunners: the test runners of a Labcoat resource (LABCOAT_RUNNERS), and the runner packages the AppHost
/// references (labcoat/runners, LABCOAT_RUNNER_PATH). docs/runners.md.
/// </summary>
[TestClass]
public class RunnersTests
{
    [TestMethod]
    public async Task WithRunners_and_the_options_pass_the_environment_variable()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-hosting-runners-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var appHost = Directory.CreateDirectory(Path.Combine(root.FullName, "AppHost")).FullName;

            var none = await Hosting.EnvironmentAsync(Hosting.Builder(appHost).AddLabcoat("tests").Resource);
            Assert.IsFalse(none.ContainsKey(LabcoatResourceBuilderExtensions.RunnersEnvironmentVariable));

            var rb = Hosting.Builder(appHost).AddLabcoat("tests").WithRunners("Dotnet", " vitest ", "vitest");
            CollectionAssert.AreEqual(new[] { "dotnet", "vitest" }, rb.Resource.Runners.ToArray());
            Assert.AreEqual("dotnet,vitest", (await Hosting.EnvironmentAsync(rb.Resource))["LABCOAT_RUNNERS"]);
            rb.WithRunners("vitest");
            Assert.AreEqual("vitest", (await Hosting.EnvironmentAsync(rb.Resource))["LABCOAT_RUNNERS"], "a later call replaces the list");

            CollectionAssert.AreEqual(new[] { "vitest" }, Hosting.Builder(appHost).AddLabcoat("tests", o => o.Runners.Add("vitest")).Resource.Runners.ToArray());

            var builder = Hosting.Builder(appHost);
            builder.Configuration["Labcoat:Runners:0"] = "dotnet";
            builder.Configuration["Labcoat:Runners:1"] = "vitest";
            CollectionAssert.AreEqual(new[] { "dotnet", "vitest" }, builder.AddLabcoat("tests").Resource.Runners.ToArray());

            Assert.ThrowsExactly<ArgumentException>(() => rb.WithRunners());
            Assert.ThrowsExactly<ArgumentException>(() => rb.WithRunners("dotnet,vitest"));
            Assert.ThrowsExactly<ArgumentException>(() => rb.WithRunners("--runners"));
            Assert.ThrowsExactly<ArgumentException>(() => rb.WithRunners(" "));
            Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithRunners(null!, "vitest"));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Options_bind_Runners_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:Runners"] = "dotnet, vitest" }).Build();
        CollectionAssert.AreEqual(new[] { "dotnet", "vitest" }, new LabcoatOptions().Bind(config.GetSection("Labcoat")).Runners.ToArray());
        Assert.IsEmpty(new LabcoatOptions().Bind(new ConfigurationBuilder().Build().GetSection("Labcoat")).Runners);
    }

    [TestMethod]
    [DoNotParallelize] // changes the static bundle lookup directory
    public async Task Runner_packages_of_the_AppHost_are_passed_on()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-hosting-runner-path-");
        var original = LabcoatResourceBuilderExtensions.BaseDirectory;
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var appHost = Directory.CreateDirectory(Path.Combine(root.FullName, "AppHost")).FullName;
            LabcoatResourceBuilderExtensions.BaseDirectory = appHost;
            var env = await Hosting.EnvironmentAsync(Hosting.Builder(appHost).AddLabcoat("tests").Resource);
            Assert.IsFalse(env.ContainsKey(LabcoatResourceBuilderExtensions.RunnerPathEnvironmentVariable), "no runner package referenced");

            var runners = Directory.CreateDirectory(Path.Combine(appHost, "labcoat", "runners", "vitest")).Parent!.FullName;
            env = await Hosting.EnvironmentAsync(Hosting.Builder(appHost).AddLabcoat("tests").Resource);
            Assert.AreEqual(runners, env["LABCOAT_RUNNER_PATH"]);
        }
        finally
        {
            LabcoatResourceBuilderExtensions.BaseDirectory = original;
            root.Delete(recursive: true);
        }
    }
}
