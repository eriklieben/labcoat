using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>
/// WithTestTracing(filter:) and LabcoatOptions.TestTracingFilter: only the matching tests get a trace. Passed to Labcoat as
/// LABCOAT_TRACE_FILTER; an invalid filter fails in the AppHost, not in every test.
/// </summary>
[TestClass]
public class TraceFilterTests
{
    [TestMethod]
    public async Task WithTestTracing_with_a_filter_turns_tracing_on_and_passes_the_environment_variable()
    {
        var root = Directory.CreateTempSubdirectory("labcoat-hosting-trace-filter-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var appHost = Directory.CreateDirectory(Path.Combine(root.FullName, "AppHost")).FullName;

            var none = Hosting.Builder(appHost).AddLabcoat("tests").WithTestTracing();
            Assert.IsNull(none.Resource.TracingFilter);
            Assert.IsFalse((await Hosting.EnvironmentAsync(none.Resource)).ContainsKey(LabcoatResourceBuilderExtensions.TraceFilterEnvironmentVariable));

            var rb = Hosting.Builder(appHost).AddLabcoat("tests").WithTestTracing(filter: " cat:Integration ");
            Assert.IsTrue(rb.Resource.TracingOnStart);
            Assert.AreEqual("cat:Integration", rb.Resource.TracingFilter);
            Assert.AreEqual("cat:Integration", (await Hosting.EnvironmentAsync(rb.Resource))["LABCOAT_TRACE_FILTER"]);

            rb.WithTestTracing(filter: "trait:Kind=Slow", enabled: false); // the filter for when tracing is toggled on
            Assert.IsFalse(rb.Resource.TracingOnStart);
            Assert.AreEqual("trait:Kind=Slow", rb.Resource.TracingFilter);
            rb.WithTestTracing(filter: null);
            Assert.IsNull(rb.Resource.TracingFilter);

            var viaOptions = Hosting.Builder(appHost).AddLabcoat("tests", o => o.TestTracingFilter = "proj:Shop");
            Assert.AreEqual("proj:Shop", viaOptions.Resource.TracingFilter);
            Assert.IsFalse(viaOptions.Resource.TracingOnStart, "the filter alone does not turn tracing on");

            var ex = Assert.ThrowsExactly<ArgumentException>(() => rb.WithTestTracing(filter: "is:failed"));
            StringAssert.Contains(ex.Message, "'is:failed' is not a valid trace filter: 'is:' depends on the test's result");
            Assert.AreEqual("filter", ex.ParamName);
            Assert.ThrowsExactly<ArgumentException>(() => Hosting.Builder(appHost).AddLabcoat("tests", o => o.TestTracingFilter = "cat:\"open"));
            Assert.ThrowsExactly<ArgumentNullException>(() => LabcoatResourceBuilderExtensions.WithTestTracing(null!, "cat:x"));
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    [DataRow("cat:\"slow tests\" trait:Kind=Slow trait:Owner proj:Shop OrdersApi \"\"", null)]
    [DataRow("msg:x", "'msg:' depends")]
    [DataRow("trait:", "'trait:' needs a value")]
    [DataRow("proj:", "'proj:' needs a value")]
    [DataRow("trait:=x", "'trait:' needs a key")]
    [DataRow("x \"open", "is not closed")]
    public void Filters_are_checked_in_the_AppHost(string filter, string? error)
    {
        var options = new LabcoatOptions { TestTracingFilter = filter };
        var root = Directory.CreateTempSubdirectory("labcoat-hosting-trace-filter-check-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Shop.slnx"), "<Solution />");
            var appHost = Directory.CreateDirectory(Path.Combine(root.FullName, "AppHost")).FullName;
            if (error is null)
            {
                Assert.AreEqual(filter, Hosting.Builder(appHost).AddLabcoat("tests").WithTestTracing(options.TestTracingFilter).Resource.TracingFilter);
                return;
            }
            var ex = Assert.ThrowsExactly<ArgumentException>(() => Hosting.Builder(appHost).AddLabcoat("tests").WithTestTracing(options.TestTracingFilter));
            StringAssert.Contains(ex.Message, error);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Options_bind_TestTracingFilter_from_configuration()
    {
        static LabcoatOptions Bind(string? value) => new LabcoatOptions().Bind(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Labcoat:TestTracingFilter"] = value }).Build().GetSection("Labcoat"));

        Assert.AreEqual("cat:Integration", Bind(" cat:Integration ").TestTracingFilter);
        Assert.IsNull(Bind(null).TestTracingFilter);
        Assert.IsNull(Bind(" ").TestTracingFilter);
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => Bind("msg:boom"));
        StringAssert.Contains(ex.Message, "'Labcoat:TestTracingFilter' is 'msg:boom'; expected a trace filter ('msg:' depends on the test's result");
    }
}
