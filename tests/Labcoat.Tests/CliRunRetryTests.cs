using Labcoat;
using Labcoat.Tests.ExplorerTests;
using Labcoat.Tests.Unit;

namespace Labcoat.Tests;

/// <summary>
/// <c>labcoat --run --retry-failed</c> and <c>--quarantine-file</c> end to end on the shared scratch workspace, whose
/// <c>Flaky</c> test fails on every second run and whose <c>Fails</c> test always fails. Environment variables are
/// process-wide: not parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("e2e")]
public class CliRunRetryTests
{
    public TestContext TestContext { get; set; }

    private static readonly string[] IsolatedEnv = ["LABCOAT_HISTORY_DIR", LabcoatConfig.NoUserConfigEnv, "NO_COLOR", "CI"];

    [TestMethod]
    [Timeout(480_000, CooperativeCancellation = true)]
    public async Task Retried_failures_that_pass_are_flaky_the_others_stay_failed_and_quarantined_tests_are_not_retried()
    {
        var ws = await ScratchWorkspace.GetAsync();
        var saved = IsolatedEnv.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var dir = Directory.CreateTempSubdirectory("labcoat-retry-");
        var originalOut = Console.Out;
        var console = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("LABCOAT_HISTORY_DIR", Path.Combine(dir.FullName, "history"));
            Environment.SetEnvironmentVariable(LabcoatConfig.NoUserConfigEnv, "1");
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            Console.SetOut(console);
            var junit = Path.Combine(dir.FullName, "results.xml");
            var html = Path.Combine(dir.FullName, "report.html");

            // ---- Flaky fails on its 2nd run and passes on the 3rd: flaky, so the run passes (exit 0), and it is flagged
            File.WriteAllText(ws.FlakyCounter, "1");
            var code = await LabcoatApp.RunAsync(["--run", "--filter", "MiscTests.Flaky", "--retry-failed", "2", "--export", $"junit={junit}", "--export", $"html={html}", ws.TestsProject],
                TestContext.CancellationToken);
            var log = console.ToString();
            Assert.AreEqual(CliRun.ExitPassed, code, log);
            Assert.Contains("Retrying 1 failed test (attempt 2 of 3)", log);
            Assert.Contains("Flaky: Flaky failed 1 time and passed on attempt 2", log);
            Assert.Contains("no failures, 1 passed", log);
            Assert.Contains("1 flaky", log);
            var testcase = ExportTests.ValidJUnit(File.ReadAllText(junit)).Descendants("testcase").Single();
            Assert.IsNull(testcase.Element("failure"));
            Assert.Contains("run 2", (string)testcase.Element("flakyFailure")!.Attribute("message")!);
            Assert.Contains("Flaky: passed on attempt 2", File.ReadAllText(html));

            // ---- a test that never passes stays failed (exit 1); the quarantine file keeps the other one out of the retries
            console.GetStringBuilder().Clear();
            var quarantine = Path.Combine(dir.FullName, "quarantine.txt");
            File.WriteAllText(quarantine, "# always red\nX.MiscTests.Fails\n");
            File.WriteAllText(ws.FlakyCounter, "4");
            code = await LabcoatApp.RunAsync(["--run", "--filter", "MiscTests.Fails,MiscTests.Flaky", "--retry-failed", "1", "--quarantine-file", quarantine, ws.TestsProject],
                TestContext.CancellationToken);
            log = console.ToString();
            Assert.AreEqual(CliRun.ExitPassed, code, log); // Flaky: run 5 passes; Fails is quarantined
            Assert.Contains("1 quarantined", log);
            Assert.DoesNotContain("Retrying", log);

            console.GetStringBuilder().Clear();
            code = await LabcoatApp.RunAsync(["--run", "--filter", "MiscTests.Fails", "--retry-failed", "2", "--export", $"junit={junit}", ws.TestsProject], TestContext.CancellationToken);
            log = console.ToString();
            Assert.AreEqual(CliRun.ExitFailed, code, log);
            Assert.Contains("Still failing after 3 attempts: Fails", log);
            var failed = ExportTests.ValidJUnit(File.ReadAllText(junit)).Descendants("testcase").Single();
            Assert.IsNotNull(failed.Element("failure"));
            Assert.HasCount(2, failed.Elements("rerunFailure"));

            // ---- a quarantine file that does not exist stops the run with the build error exit code
            Assert.AreEqual(CliRun.ExitBuildError, await LabcoatApp.RunAsync(["--run", "--quarantine-file", Path.Combine(dir.FullName, "nope.txt"), ws.TestsProject], TestContext.CancellationToken));
        }
        finally
        {
            Console.SetOut(originalOut);
            try { File.Delete(ws.FlakyCounter); } catch (IOException) { } // the scratch workspace is shared: other tests count Flaky runs from 1
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
