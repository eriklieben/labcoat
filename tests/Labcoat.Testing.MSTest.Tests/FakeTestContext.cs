namespace Labcoat.Testing.MSTest.Tests;

/// <summary>A TestContext for calling LabcoatMSTest.StartTest / EndTest outside MSTest's own lifecycle.</summary>
internal sealed class FakeTestContext : TestContext
{
    private readonly string _testName;
    private readonly string _className;
    private readonly UnitTestOutcome _outcome;
    private readonly Dictionary<string, object?> _properties;

    public FakeTestContext(string testName, string? displayName = null, string className = "Shop.Tests.OrderTests",
        UnitTestOutcome outcome = UnitTestOutcome.Passed)
    {
        _testName = testName;
        _className = className;
        _outcome = outcome;
        _properties = new() { ["TestName"] = testName, ["FullyQualifiedTestClassName"] = className };
        TestDisplayName = displayName;
    }

    public override IDictionary<string, object?> Properties => _properties;
    public override string TestName => _testName;
    public override string FullyQualifiedTestClassName => _className;
    public override UnitTestOutcome CurrentTestOutcome => _outcome;

    public override void AddResultFile(string fileName) { }
    public override void Write(string? message) { }
    public override void Write(string format, params object?[] args) { }
    public override void WriteLine(string? message) { }
    public override void WriteLine(string format, params object?[] args) { }
    public override void DisplayMessage(MessageLevel messageLevel, string message) { }
}
