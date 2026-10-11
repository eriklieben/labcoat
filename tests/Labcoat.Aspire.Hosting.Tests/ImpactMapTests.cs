using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>ImpactMap / WithImpactMap: the granularity of the test impact map, passed as LABCOAT_IMPACT_MAP.</summary>
[TestClass]
public class ImpactMapTests
{
    [TestMethod]
    public async Task WithImpactMap_and_the_options_pass_the_environment_variable()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-hosting-impact-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var appHost = Directory.CreateDirectory(Path.Combine(root.FullName, "AppHost")).FullName;

            var none = await Hosting.EnvironmentAsync(Hosting.Builder(appHost).AddLabcoat("tests").Resource);
            Assert.IsFalse(none.ContainsKey(LabcoatResourceBuilderExtensions.ImpactMapEnvironmentVariable));

            var rb = Hosting.Builder(appHost).AddLabcoat("tests").WithImpactMap(" Test ");
            Assert.AreEqual("test", rb.Resource.ImpactMap);
            Assert.AreEqual("test", (await Hosting.EnvironmentAsync(rb.Resource))["LABCOAT_IMPACT_MAP"]);

            Assert.AreEqual("class", Hosting.Builder(appHost).AddLabcoat("tests", o => o.ImpactMap = "class").Resource.ImpactMap);

            var builder = Hosting.Builder(appHost);
            builder.Configuration["Labcoat:ImpactMap"] = "auto";
            Assert.AreEqual("auto", builder.AddLabcoat("tests").Resource.ImpactMap);

            Assert.ThrowsExactly<ArgumentException>(() => rb.WithImpactMap("method"));
            Assert.ThrowsExactly<ArgumentException>(() => rb.WithImpactMap(" "));
            Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithImpactMap(null!, "test"));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Options_bind_ImpactMap_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:ImpactMap"] = " TEST " }).Build();
        Assert.AreEqual("test", new LabcoatOptions().Bind(config.GetSection("Labcoat")).ImpactMap);
        Assert.IsNull(new LabcoatOptions().Bind(new ConfigurationBuilder().Build().GetSection("Labcoat")).ImpactMap);
        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:ImpactMap"] = "method" }).Build();
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatOptions().Bind(bad.GetSection("Labcoat")));
        Assert.Contains("ImpactMap", ex.Message);
    }
}
