extern alias MSTestAdapter;

using System.Text.Json;
using Labcoat.Testing;
using LabcoatMSTest = MSTestAdapter::Labcoat.Testing.LabcoatMSTest;
using LabcoatOutput = MSTestAdapter::Labcoat.Testing.LabcoatOutput;
// MSTest.TestFramework (through the MSTest adapter) adds a global using of its namespace: TUnit's types win.
using Assert = TUnit.Assertions.Assert;
using TestContext = TUnit.Core.TestContext;
using MSTest = Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AotSmoke;

/// <summary>
/// The Labcoat libraries in a Native AOT test app (TUnit, which supports Native AOT). Run with LABCOAT_TEST_HOST=1, as
/// Labcoat starts test hosts: live output is on.
/// </summary>
public class SmokeTests
{
    [Test]
    public async Task Runs_under_the_Labcoat_test_host()
    {
        await Assert.That(LabcoatLiveOutput.IsEnabled).IsTrue();
    }

    [Test]
    [Category("Smoke")]
    public async Task Live_output_writes_a_marker()
    {
        var test = LabcoatTracingAttribute.ToLabcoatTest(TestContext.Current!);
        await Assert.That(test.Categories).Contains("Smoke");

        var marker = LabcoatLiveOutput.FormatMarker(test, "hello from Native AOT \"quoted\" <tag> é\n", error: true);
        using var json = JsonDocument.Parse(Convert.FromBase64String(marker[LabcoatLiveOutput.Marker.Length..]));
        var root = json.RootElement;
        await Assert.That(root.GetProperty("s").GetString()).IsEqualTo("err");
        await Assert.That(root.GetProperty("t").GetString()).IsEqualTo("hello from Native AOT \"quoted\" <tag> é\n");
        await Assert.That(root.GetProperty("c").GetString()).IsEqualTo(typeof(SmokeTests).FullName);
        await Assert.That(root.GetProperty("m").GetString()).IsEqualTo(nameof(Live_output_writes_a_marker));
        await Assert.That(root.GetProperty("i").GetString()).IsEqualTo(test.Id);

        // On the process's raw stdout, where Labcoat reads it (the CI job checks it is there).
        LabcoatLiveOutput.Send(test, "labcoat-aot-smoke: live output works\n");
    }

    [Test]
    public async Task The_MSTest_adapter_without_its_test_class_has_no_categories()
    {
        // MSTest's TestContext has no categories: LabcoatMSTest reads them from the test class by reflection. The class
        // below is only named here, so the AOT compiler removed it: no categories or traits, and no exception.
        var context = new FakeMSTestContext("AotSmoke.TrimmedMSTestClass", "Categorized");
        var test = LabcoatMSTest.ToLabcoatTest(context);
        await Assert.That(test.ClassName).IsEqualTo("AotSmoke.TrimmedMSTestClass");
        await Assert.That(test.Categories.Count).IsEqualTo(0);
        await Assert.That(test.Traits.Count).IsEqualTo(0);
        LabcoatOutput.WriteLine(context, "labcoat-aot-smoke: MSTest live output works");
    }
}

/// <summary>An MSTest test class nothing refers to (see The_MSTest_adapter_without_its_test_class_has_no_categories).</summary>
[MSTest.TestClass]
[MSTest.TestCategory("Integration")]
public class TrimmedMSTestClass
{
    [MSTest.TestMethod]
    [MSTest.TestCategory("Integration")]
    public void Categorized()
    {
        // Never runs: only its metadata matters.
    }
}

/// <summary>A TestContext for calling the MSTest adapter outside MSTest.</summary>
internal sealed class FakeMSTestContext(string className, string testName) : MSTest.TestContext
{
    private readonly Dictionary<string, object?> _properties = new()
    {
        ["TestName"] = testName,
        ["FullyQualifiedTestClassName"] = className,
    };

    public override IDictionary<string, object?> Properties => _properties;
    public override string TestName => testName;
    public override string FullyQualifiedTestClassName => className;
    public override MSTest.UnitTestOutcome CurrentTestOutcome => MSTest.UnitTestOutcome.Passed;
    public override void AddResultFile(string fileName) { }
    public override void Write(string? message) { }
    public override void Write(string format, params object?[] args) { }
    public override void WriteLine(string? message) { }
    public override void WriteLine(string format, params object?[] args) { }
    public override void DisplayMessage(MSTest.MessageLevel messageLevel, string message) { }
}
