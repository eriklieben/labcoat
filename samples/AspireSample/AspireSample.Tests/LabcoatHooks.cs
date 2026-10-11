using Labcoat.Testing;

namespace AspireSample.Tests;

/// <summary>
/// Gives every test its own trace when run from Labcoat hosted by the AppHost: Labcoat then shows "View trace" /
/// "Structured logs" links into the Aspire dashboard for each test. No-op otherwise (e.g. plain `dotnet test`).
/// (Labcoat.Testing.MSTest; TUnit needs no hooks: see AspireSample.TUnitTests.)
/// </summary>
[TestClass]
public static class LabcoatHooks
{
    [GlobalTestInitialize]
    public static void StartTrace(TestContext context) => LabcoatMSTest.StartTest(context);

    [GlobalTestCleanup]
    public static void EndTrace(TestContext context) => LabcoatMSTest.EndTest(context);
}
