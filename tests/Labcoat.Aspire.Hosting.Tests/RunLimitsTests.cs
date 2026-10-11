using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>WithMaxParallelProjects / WithHangThreshold / WithTestTimeout and the options: passed as LABCOAT_MAX_PARALLEL etc.</summary>
[TestClass]
public class RunLimitsTests
{
    [TestMethod]
    public async Task The_run_limits_pass_environment_variables()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-hosting-limits-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var appHost = Directory.CreateDirectory(Path.Combine(root.FullName, "AppHost")).FullName;

            var none = await Hosting.EnvironmentAsync(Hosting.Builder(appHost).AddLabcoat("tests").Resource);
            Assert.IsFalse(none.ContainsKey(LabcoatRunLimitsExtensions.MaxParallelEnvironmentVariable));
            Assert.IsFalse(none.ContainsKey(LabcoatRunLimitsExtensions.TestTimeoutEnvironmentVariable));

            var rb = Hosting.Builder(appHost).AddLabcoat("tests")
                .WithMaxParallelProjects(2).WithHangThreshold(TimeSpan.FromSeconds(90)).WithTestTimeout(TimeSpan.FromMinutes(2)).WithTestTimeout(TimeSpan.FromSeconds(30.5));
            var env = await Hosting.EnvironmentAsync(rb.Resource);
            Assert.AreEqual("2", env["LABCOAT_MAX_PARALLEL"]);
            Assert.AreEqual("90", env["LABCOAT_HANG_THRESHOLD"]);
            Assert.AreEqual("30.5", env["LABCOAT_TEST_TIMEOUT"]);

            var fromOptions = Hosting.Builder(appHost).AddLabcoat("tests", o =>
            {
                o.MaxParallelProjects = 8;
                o.HangThreshold = TimeSpan.Zero;
                o.TestTimeout = TimeSpan.FromMinutes(1);
            }).Resource;
            Assert.AreEqual((8, TimeSpan.Zero, TimeSpan.FromMinutes(1)), (fromOptions.MaxParallelProjects, fromOptions.HangThreshold, fromOptions.TestTimeout));
            Assert.AreEqual("0", (await Hosting.EnvironmentAsync(fromOptions))["LABCOAT_HANG_THRESHOLD"]);

            var builder = Hosting.Builder(appHost);
            builder.Configuration["Labcoat:MaxParallelProjects"] = "3";
            builder.Configuration["Labcoat:TestTimeout"] = "00:01:30";
            builder.Configuration["Labcoat:HangThreshold"] = "45s";
            var configured = builder.AddLabcoat("tests").Resource;
            Assert.AreEqual((3, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)), (configured.MaxParallelProjects, configured.HangThreshold, configured.TestTimeout));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => rb.WithMaxParallelProjects(0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => rb.WithMaxParallelProjects(65));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => rb.WithTestTimeout(TimeSpan.FromSeconds(-1)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => rb.WithHangThreshold(TimeSpan.FromSeconds(-1)));
            Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatRunLimitsExtensions.WithTestTimeout(null!, TimeSpan.Zero));
            Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatRunLimitsExtensions.WithHangThreshold(null!, TimeSpan.Zero));
            Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatRunLimitsExtensions.WithMaxParallelProjects(null!, 1));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    [DataRow("90", 90.0)]
    [DataRow("1.5m", 90.0)]
    [DataRow("2 min", 120.0)]
    [DataRow("500ms", 0.5)]
    [DataRow("1h", 3600.0)]
    [DataRow("off", 0.0)]
    [DataRow("00:00:10", 10.0)]
    public void Options_bind_durations(string value, double seconds)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:TestTimeout"] = value }).Build();
        Assert.AreEqual(TimeSpan.FromSeconds(seconds), new LabcoatOptions().Bind(config.GetSection("Labcoat")).TestTimeout);
    }

    [TestMethod]
    [DataRow("TestTimeout", "soon")]
    [DataRow("TestTimeout", "5 days")]
    [DataRow("HangThreshold", "-00:00:01")]
    [DataRow("MaxParallelProjects", "0")]
    [DataRow("MaxParallelProjects", "lots")]
    public void Invalid_options_name_the_key(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:" + key] = value }).Build();
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => new LabcoatOptions().Bind(config.GetSection("Labcoat")));
        Assert.Contains(key, ex.Message);
    }
}
