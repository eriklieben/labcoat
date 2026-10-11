using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>AffectedBase / WithAffectedBase: the base ref of "Run affected: branch vs …", passed as LABCOAT_AFFECTED_BASE.</summary>
[TestClass]
public class AffectedBaseTests
{
    [TestMethod]
    public async Task WithAffectedBase_and_the_options_pass_the_environment_variable()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-hosting-affected-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var appHost = Directory.CreateDirectory(Path.Combine(root.FullName, "AppHost")).FullName;

            var none = await Hosting.EnvironmentAsync(Hosting.Builder(appHost).AddLabcoat("tests").Resource);
            Assert.IsFalse(none.ContainsKey(LabcoatResourceBuilderExtensions.AffectedBaseEnvironmentVariable));

            var rb = Hosting.Builder(appHost).AddLabcoat("tests").WithAffectedBase(" origin/develop ");
            Assert.AreEqual("origin/develop", rb.Resource.AffectedBase);
            Assert.AreEqual("origin/develop", (await Hosting.EnvironmentAsync(rb.Resource))["LABCOAT_AFFECTED_BASE"]);

            var viaOptions = Hosting.Builder(appHost).AddLabcoat("tests", o => o.AffectedBase = "main");
            Assert.AreEqual("main", viaOptions.Resource.AffectedBase);

            var builder = Hosting.Builder(appHost);
            builder.Configuration["Labcoat:AffectedBase"] = "release/1.0";
            Assert.AreEqual("release/1.0", builder.AddLabcoat("tests").Resource.AffectedBase);

            Assert.ThrowsExactly<ArgumentException>(() => rb.WithAffectedBase("--output=x"));
            Assert.ThrowsExactly<ArgumentException>(() => rb.WithAffectedBase("a b"));
            Assert.ThrowsExactly<ArgumentException>(() => rb.WithAffectedBase(" "));
            Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithAffectedBase(null!, "main"));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Options_bind_AffectedBase_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:AffectedBase"] = " origin/x " }).Build();
        Assert.AreEqual("origin/x", new LabcoatOptions().Bind(config.GetSection("Labcoat")).AffectedBase);
        Assert.IsNull(new LabcoatOptions().Bind(new ConfigurationBuilder().Build().GetSection("Labcoat")).AffectedBase);
    }
}
