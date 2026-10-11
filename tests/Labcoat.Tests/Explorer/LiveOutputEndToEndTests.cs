using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// Live console output against a real MSTest 4 test app (its own scratch project, built once): a slow test writes
/// captured console output, <c>TestContext.DisplayMessage</c> lines (live on the host's stdout, not in the result) and
/// Labcoat.Testing-style markers (exact) with delays. The <c>testOutput</c> events must arrive while it runs, before
/// its final result, and the final output stays authoritative.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("e2e")]
public class LiveOutputEndToEndTests
{
    private static readonly object s_gate = new();
    private static Task<string>? s_project;

    private static Task<string> ProjectAsync()
    {
        lock (s_gate) return s_project ??= Task.Run(CreateAsync);
    }

    private static async Task<string> CreateAsync()
    {
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        var root = Directory.CreateTempSubdirectory("labcoat-live-e2e-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, recursive: true); } catch { } };
        var csproj = Path.Combine(root, "Live.Tests.csproj");
        File.WriteAllText(csproj, """
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
            """);
        File.WriteAllText(Path.Combine(root, "SlowTests.cs"), """
            using System.Text;
            [assembly: Parallelize(Workers = 1, Scope = ExecutionScope.MethodLevel)]
            namespace Live;
            [TestClass]
            public class SlowTests
            {
                public TestContext TestContext { get; set; } = null!;

                // What Labcoat.Testing's LabcoatOutput writes to the raw stdout.
                private void Marker(string text)
                {
                    var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?>
                    {
                        ["s"] = "out", ["t"] = text, ["c"] = TestContext.FullyQualifiedTestClassName, ["m"] = TestContext.TestName, ["n"] = TestContext.TestDisplayName,
                    });
                    var raw = Console.OpenStandardOutput();
                    raw.Write(Encoding.UTF8.GetBytes("labcoat:live:" + Convert.ToBase64String(json) + "\n"));
                    raw.Flush();
                }

                [TestMethod]
                public async Task Slow()
                {
                    for (var i = 1; i <= 4; i++)
                    {
                        Console.WriteLine($"captured {i}");
                        Marker($"exact {i}");
                        TestContext.DisplayMessage(MessageLevel.Informational, $"display {i}");
                        await Task.Delay(400);
                    }
                    await Task.Delay(800); // room for the last lines to arrive while the test still runs (slow CI runners)
                }

                [TestMethod] public void Fast() => Console.WriteLine("fast");
            }
            """);
        var (code, output) = await ScratchWorkspace.DotnetAsync(root, "build", csproj, "-nologo", "-v:q", "-tl:off");
        if (code != 0) throw new InvalidOperationException("Building the live output project failed:\n" + output);
        return csproj;
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task A_slow_test_streams_output_while_it_runs()
    {
        var csproj = await ProjectAsync();
        var hub = new EventHub();
        await using var explorer = new Labcoat.Explorer(hub, [csproj], null, [Path.GetDirectoryName(csproj)!]);
        explorer.DotnetOverride = ExplorerEndToEndTests.SkipBuild;
        await explorer.DiscoverAsync(null);
        var slow = explorer.Snapshot().Tests.Single(t => t.DisplayName == "Slow");
        await using var events = new EventRecorder(hub, explorer, lean: true);
        await events.WaitForAsync("state");

        var run = explorer.RunAsync(null, null);
        await ScratchWorkspace.WaitUntilAsync(() => explorer.GetTest(null, slow.Id)!.Outcome == Outcome.Running, "Slow running");
        await ScratchWorkspace.WaitUntilAsync(() => explorer.GetLiveOutput(null, slow.Id)?.Chunks.Count > 0, "a live output snapshot", 10_000);
        await run;
        Assert.IsNull(explorer.GetLiveOutput(null, slow.Id), "no snapshot once the test finished");

        // the recorder receives events asynchronously: wait for the end of the run (slower runners can still be delivering)
        await events.WaitForAsync("runFinished");
        var all = events.All();
        var outputs = all.Where(e => (string?)e["type"] == "testOutput" && (string?)e["id"] == slow.Id).ToList();
        var text = string.Concat(outputs.Select(e => (string?)e["text"]));
        for (var i = 1; i <= 4; i++)
        {
            Assert.Contains($"exact {i}\n", text, ScratchWorkspace.Logs(explorer));
            Assert.Contains($"display {i}\n", text);
        }
        Assert.DoesNotContain("captured", text, "MSTest captures console output: it only arrives with the result");
        Assert.DoesNotContain("labcoat:live:", text);
        Assert.IsTrue(outputs.Where(e => (string?)e["text"] is { } t && t.Contains("exact")).All(e => (string?)e["stream"] == "stdout"));
        Assert.IsTrue(outputs.Where(e => (string?)e["text"] is { } t && t.Contains("display")).All(e => (string?)e["stream"] == "host"));
        CollectionAssert.AreEqual(Enumerable.Range(1, outputs.Count).Select(i => (long)i).ToList(), outputs.Select(e => (long)e["seq"]!).ToList());

        // streamed while running: before the test's final result, after it was reported running
        var updates = events.TestUpdates();
        // the final result arrives as a testsUpdated entry or a testResults patch; if neither carried it, the run's end
        var finalIndex = updates.FirstOrDefault(u => u.Id == slow.Id && u.Outcome == "passed") is { Id: not null } done
            ? done.Index
            : all.FindIndex(e => (string?)e["type"] == "runFinished");
        Assert.IsGreaterThanOrEqualTo(0, finalIndex, "the final result or the end of the run was recorded");
        var runningIndex = updates.First(u => u.Id == slow.Id && u.Outcome == "running").Index;
        Assert.IsGreaterThan(runningIndex, all.IndexOf(outputs[0]));
        Assert.IsLessThan(finalIndex, all.IndexOf(outputs[^1]));

        // the final output is authoritative: the captured lines, no markers
        var final = explorer.GetTest(null, slow.Id)!;
        Assert.Contains("captured 4", final.StdOut!);
        Assert.DoesNotContain("labcoat:live", final.StdOut ?? "");
        Assert.IsFalse(all.Any(e => (string?)e["type"] == "testOutput" && (string?)e["id"] != slow.Id && ((string?)e["text"])!.Contains("exact")));
    }
}
