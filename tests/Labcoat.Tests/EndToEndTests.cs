using Labcoat;

namespace Labcoat.Tests;

/// <summary>
/// Builds a small MSTest project and drives it through Labcoat over Microsoft.Testing.Platform's server mode: the
/// regression net for MTP upgrades (the protocol is not documented). ~20 s; needs the MSTest 4.0.2 package (restored
/// from the NuGet cache / feed).
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("e2e")]
public class EndToEndTests
{
    public TestContext TestContext { get; set; }

    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <OutputType>Exe</OutputType>
            <EnableMSTestRunner>true</EnableMSTestRunner>
          </PropertyGroup>
          <ItemGroup><PackageReference Include="MSTest" Version="4.0.2" /></ItemGroup>
          <ItemGroup><Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" /></ItemGroup>
        </Project>
        """;

    private static string TestsCs(string flagFile) => $$"""
        [assembly: DoNotParallelize]
        namespace E2E;
        [TestClass]
        public class A_Tests
        {
            [TestMethod] public void A1_Passes() { }
            [TestMethod] public void A2_Fails() => Assert.AreEqual(1, 2);
        }
        [TestClass]
        public class B_Tests
        {
            [TestMethod]
            public void B1_Crashes()
            {
                if (!File.Exists(@"{{flagFile}}")) return;
                using (var e = new StreamWriter(Console.OpenStandardError())) e.WriteLine("FATAL: simulated crash");
                Thread.Sleep(1500); // let MTP report the test as in progress
                Environment.Exit(3);
            }
        }
        [TestClass]
        public class C_Tests
        {
            [TestMethod] public void C1_Slow() => Thread.Sleep(2500);
            [TestMethod] public void C2_Passes() { }
        }
        """;

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Discover_run_crash_and_coalesce()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-e2e-");
        var flag = Path.Combine(dir.FullName, "crash.flag");
        try
        {
            var proj = Path.Combine(dir.FullName, "E2E.Tests.csproj");
            File.WriteAllText(proj, ProjectXml);
            File.WriteAllText(Path.Combine(dir.FullName, "Tests.cs"), TestsCs(flag));
            await using var explorer = new Explorer(new EventHub(), [proj], null, [dir.FullName]);

            await explorer.DiscoverAsync(null);
            var tests = explorer.Snapshot().Tests;
            Assert.HasCount(5, tests, string.Join("\n", explorer.Snapshot(includeLogs: true).Logs!.Select(l => l.Message)));
            string Id(string name) => tests.Single(t => t.DisplayName == name).Id;

            // ---- a normal run
            await explorer.RunAsync(null, null);
            var run = explorer.Summary().LastRun!;
            Assert.AreEqual((5, 4, 1), (run.Total, run.Passed, run.Failed));
            Assert.IsNull(run.Aborted);

            // ---- the test host exits mid-run
            File.WriteAllText(flag, "");
            var crash = explorer.RequestRun(null, null);
            Assert.AreEqual(202, crash.Status);
            await crash.Completion!;
            File.Delete(flag);
            run = explorer.Summary().LastRun!;
            Assert.AreEqual(crash.RunId, run.RunId);
            Assert.IsTrue(run.Aborted);
            Assert.Contains("exited with code 3", run.AbortReason!);
            var crashed = explorer.GetTest(null, Id("B1_Crashes"))!;
            Assert.AreEqual(Outcome.Failed, crashed.Outcome);
            Assert.AreEqual(Explorer.TestHostCrashedErrorType, crashed.ErrorType);
            Assert.StartsWith("Test host exited with code 3", crashed.ErrorMessage);
            Assert.Contains("FATAL: simulated crash", crashed.ErrorMessage);
            // Nothing that did not run in this run may still claim to have passed.
            foreach (var name in new[] { "C1_Slow", "C2_Passes" })
                Assert.AreEqual(Outcome.NotRun, explorer.GetTest(null, Id(name))!.Outcome, name);
            Assert.AreEqual("aborted", explorer.GetRunStatus(crash.RunId!)!.State);

            // ---- the warm host is restarted lazily; requests made while a run is busy are merged into one queued run
            var slow = explorer.RequestRun(null, [Id("C1_Slow")]);
            await Task.Delay(300, TestContext.CancellationToken);
            var q1 = explorer.RequestRun(null, [Id("A1_Passes")]);
            var q2 = explorer.RequestRun(null, [Id("C2_Passes")]);
            var q3 = explorer.RequestRun(null, [Id("A1_Passes")]);
            Assert.IsTrue(q1.Queued);
            Assert.IsTrue(q2.Merged && q3.Merged);
            Assert.AreEqual(q1.RunId, q2.RunId);
            Assert.AreEqual(3, explorer.Summary().PendingRun!.Requests);
            Assert.AreEqual(2, explorer.Summary().PendingRun!.TestCount);
            await slow.Completion!;
            await q1.Completion!;
            Assert.AreEqual("finished", explorer.GetRunStatus(slow.RunId!)!.State);
            var merged = explorer.GetRunStatus(q1.RunId!)!;
            Assert.AreEqual("finished", merged.State);
            Assert.AreEqual((2, 2), (merged.Run!.Total, merged.Run.Passed));

            // ---- cancel clears the queue
            var busy = explorer.RequestRun(null, [Id("C1_Slow")]);
            await Task.Delay(300, TestContext.CancellationToken);
            var queued = explorer.RequestRun(null, [Id("A1_Passes")]);
            var cancel = explorer.Cancel();
            Assert.AreEqual(queued.RunId, cancel.ClearedRunId);
            await busy.Completion!;
            Assert.AreEqual("cancelled", explorer.GetRunStatus(queued.RunId!)!.State);
            Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState); // C1_Slow finished within the grace period
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }
}
