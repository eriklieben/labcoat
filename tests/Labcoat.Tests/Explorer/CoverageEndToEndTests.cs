using Labcoat;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// Code coverage of a solution shaped like the Support Desk golden sample: TUnit test projects (on
/// Microsoft.Testing.Platform, with Microsoft.Testing.Extensions.CodeCoverage from the TUnit package) that test a
/// referenced ASP.NET Core API in-process through WebApplicationFactory, one with its own coverage.settings.xml and one
/// without. Regression tests for "the API shows 0 lines covered after a normal run", for honouring the project's own
/// coverage settings, for Labcoat's defaults and for the warm test host surviving a coverage run.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("e2e")]
public class CoverageEndToEndTests
{
    private static string Id(Labcoat.Explorer e, string projectId, string displayName) =>
        e.Snapshot().Tests.Single(t => t.ProjectId == projectId && t.DisplayName == displayName).Id;

    private static async Task<Labcoat.Explorer> DiscoveredAsync(DeskWorkspace ws, string project, CoverageOptions? options = null)
    {
        var explorer = new Labcoat.Explorer(new EventHub(), [project], null, [ws.Root])
        {
            DotnetOverride = ExplorerEndToEndTests.SkipBuild,
            CoverageOptions = options ?? CoverageOptions.None,
        };
        await explorer.DiscoverAsync(null);
        Assert.HasCount(3, explorer.Snapshot().Tests, ScratchWorkspace.Logs(explorer));
        return explorer;
    }

    private static List<int> HostPids(Labcoat.Explorer e, string dll) =>
        System.Text.RegularExpressions.Regex.Matches(ScratchWorkspace.Logs(e), $@"Started test host \(pid (\d+)\): dotnet {System.Text.RegularExpressions.Regex.Escape(dll)} --server --client-port \d+$",
            System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value)).ToList();

    /// <summary>(module, path, line) → covered, from a report.</summary>
    private static Dictionary<(string, string, int), bool> Lines(CoverageReport r) =>
        r.Files.SelectMany(f => f.Lines.Select(l => ((f.Module, f.Path, l.Line), l.Hits > 0))).ToDictionary(x => x.Item1, x => x.Item2);

    /// <summary>
    /// The golden sample's bug: a normal run on the warm host, then a coverage run, showed the API at 0 lines covered.
    /// The warm host had read Desk.Api.pdb for a stack trace with line numbers (the API logs the exception of /boom)
    /// and kept it open with a shared lock; static instrumentation could not rewrite the API next to it and silently
    /// left it uninstrumented. Now the coverage run uses the project's own coverage.settings.xml, matches
    /// `dotnet test --coverage --coverage-settings coverage.settings.xml` line for line, and (with dynamic
    /// instrumentation) leaves the warm host alone.
    /// </summary>
    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task After_a_warm_run_coverage_counts_the_api_like_dotnet_test_with_the_projects_settings()
    {
        var ws = await DeskWorkspace.GetAsync();
        // The reference: the test app run the way `dotnet test --coverage --coverage-settings coverage.settings.xml` runs it
        // (first: a warm host holding Desk.Api.pdb open would break that run the same way).
        var reference = await DirectCoverageAsync(ws.SettingsTestsDll, ["--coverage-settings", Path.Combine(ws.Root, "Desk.Api.Tests", "coverage.settings.xml")]);
        await using var explorer = await DiscoveredAsync(ws, ws.SettingsTestsProject);

        await explorer.RunAsync(null, null); // the warm host runs the API, /boom included
        Assert.AreEqual(3, explorer.Summary().LastRun!.Passed, ScratchWorkspace.Logs(explorer));
        var warmPid = HostPids(explorer, "Desk.Api.Tests.dll").Single();

        await explorer.RunAsync(null, null, coverage: true);
        var report = explorer.Snapshot().Coverage;
        Assert.IsNotNull(report, ScratchWorkspace.Logs(explorer));

        // The project's settings: only Desk.Api.dll (not Labcoat.Probe, which the tests use too).
        CollectionAssert.AreEquivalent(new[] { "Desk.Api" }, report.Files.Select(f => f.Module).Distinct().ToArray(), ScratchWorkspace.Logs(explorer));
        var tickets = report.Files.Single(f => f.Path.EndsWith("Tickets.cs", StringComparison.Ordinal));
        Assert.IsGreaterThan(0, tickets.LinesCovered, "the API is covered (it showed 0 lines before)\n" + ScratchWorkspace.Logs(explorer));
        Assert.AreEqual(0, tickets.Lines.Single(l => l.Line == DeskWorkspace.UnusedLine).Hits);

        var info = report.Settings!.Single();
        Assert.AreEqual(("file", "convention"), (info.Source, info.Origin));
        Assert.IsTrue(SamePath(Path.Combine(ws.Root, "Desk.Api.Tests", "coverage.settings.xml"), info.Path), info.Path);
        Assert.Contains("Coverage settings: coverage.settings.xml (found by name)", ScratchWorkspace.Logs(explorer));

        // The same files, lines and covered lines as the reference run.
        var acc = new CoverageAccumulator();
        acc.AddCobertura(reference, CoverageFilter.ForSettingsFile(CoverageOptions.None));
        var expected = acc.Build("ref", ["Desk.Api.Tests"]);
        CollectionAssert.AreEquivalent(Lines(expected).ToList(), Lines(report).ToList());
        Assert.AreEqual((expected.LinesCovered, expected.LinesValid), (report.LinesCovered, report.LinesValid));

        // The warm host: kept when nothing was rewritten in place (dynamic instrumentation), else replaced by a fresh one.
        var dynamic = info.Instrumentation == "dynamic";
        Assert.AreEqual(CoverageSettingsResolver.DynamicInstrumentationAvailable(Path.GetDirectoryName(ws.SettingsTestsDll)!) || OperatingSystem.IsWindows(), dynamic);
        await explorer.RunAsync(null, null);
        Assert.AreEqual(3, explorer.Summary().LastRun!.Passed, ScratchWorkspace.Logs(explorer));
        Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState);
        var pids = HostPids(explorer, "Desk.Api.Tests.dll");
        if (dynamic) CollectionAssert.AreEqual(new[] { warmPid }, pids, "the warm host survived the coverage run\n" + ScratchWorkspace.Logs(explorer));
        else Assert.HasCount(2, pids, ScratchWorkspace.Logs(explorer));
    }

    /// <summary>
    /// Static instrumentation (the only kind where the extension has no CLR profiler, e.g. Linux / macOS on arm64; here
    /// forced by a settings file): the warm host is stopped before the coverage run, so it neither holds the API's files
    /// open (0 lines covered) nor keeps running on assemblies rewritten under it; a fresh one serves the next run.
    /// </summary>
    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)] // static instrumentation in place is the Linux / macOS case
    public async Task Static_instrumentation_stops_the_warm_host_first_and_still_counts_the_api()
    {
        var ws = await DeskWorkspace.GetAsync();
        await using var explorer = await DiscoveredAsync(ws, ws.SettingsTestsProject, new CoverageOptions { Settings = ws.StaticSettings });

        await explorer.RunAsync(null, null);
        await explorer.RunAsync(null, null, coverage: true);
        var report = explorer.Snapshot().Coverage;
        Assert.IsNotNull(report, ScratchWorkspace.Logs(explorer));
        var info = report.Settings!.Single();
        Assert.AreEqual(("file", "option", "static"), (info.Source, info.Origin, info.Instrumentation));
        Assert.IsGreaterThan(0, report.Files.Where(f => f.Module == "Desk.Api").Sum(f => f.LinesCovered), ScratchWorkspace.Logs(explorer));
        Assert.Contains("Stopping the warm test host for the coverage run", ScratchWorkspace.Logs(explorer));

        await explorer.RunAsync(null, null);
        Assert.AreEqual(3, explorer.Summary().LastRun!.Passed, ScratchWorkspace.Logs(explorer));
        Assert.AreEqual("warm", explorer.Summary().Projects[0].HostState);
        Assert.HasCount(2, HostPids(explorer, "Desk.Api.Tests.dll"), ScratchWorkspace.Logs(explorer));
    }

    /// <summary>
    /// No settings file: Labcoat's defaults count the projects the test project references (Desk.Api), not Labcoat's
    /// own assemblies (Labcoat.Probe stands in for Labcoat.Testing), the test project, TUnit / Microsoft.Testing.* or
    /// generated code under obj/ (the [GeneratedRegex] source). Coverage.Include brings Labcoat's back,
    /// Coverage.Exclude drops a file.
    /// </summary>
    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task Without_settings_the_defaults_count_the_referenced_projects_only()
    {
        var ws = await DeskWorkspace.GetAsync();
        await using (var explorer = await DiscoveredAsync(ws, ws.PlainTestsProject))
        {
            await explorer.RunAsync(null, null, coverage: true);
            var report = explorer.Snapshot().Coverage;
            Assert.IsNotNull(report, ScratchWorkspace.Logs(explorer));
            CollectionAssert.AreEquivalent(new[] { "Desk.Api" }, report.Files.Select(f => f.Module).Distinct().ToArray(), ScratchWorkspace.Logs(explorer));
            Assert.IsFalse(report.Files.Any(f => CoverageSettingsResolver.IsGenerated(f.Path)), string.Join("\n", report.Files.Select(f => f.Path)));
            Assert.IsGreaterThan(0, report.LinesCovered);
            var info = report.Settings!.Single();
            Assert.AreEqual("defaults", info.Source);
            CollectionAssert.AreEqual(new[] { "Desk.Api" }, info.Modules); // Labcoat.Probe is referenced, but Labcoat's
            Assert.Contains("Coverage settings: Labcoat's defaults (referenced projects: Desk.Api;", ScratchWorkspace.Logs(explorer));
        }

        await using (var explorer = await DiscoveredAsync(ws, ws.PlainTestsProject, new CoverageOptions { Include = ["Labcoat.*"], Exclude = ["Program.cs"] }))
        {
            await explorer.RunAsync(null, null, coverage: true);
            var report = explorer.Snapshot().Coverage!;
            CollectionAssert.AreEquivalent(new[] { "Desk.Api", "Labcoat.Probe" }, report.Files.Select(f => f.Module).Distinct().ToArray(), ScratchWorkspace.Logs(explorer));
            Assert.IsFalse(report.Files.Any(f => Path.GetFileName(f.Path) == "Program.cs"));
            Assert.IsGreaterThan(0, report.Files.Single(f => f.Module == "Labcoat.Probe").LinesCovered);
        }
    }

    private static bool SamePath(string a, string? b) =>
        b is not null && string.Equals(PathUtil.Canonicalize(a), PathUtil.Canonicalize(b), PathUtil.Comparison);

    /// <summary>The test app run with coverage directly (what `dotnet test --coverage` starts), into a Cobertura file.</summary>
    private static async Task<string> DirectCoverageAsync(string dll, string[] extra)
    {
        var output = Path.Combine(Path.GetTempPath(), "labcoat-e2e-cov-" + Guid.NewGuid().ToString("N") + ".xml");
        var (code, text) = await ScratchWorkspace.DotnetAsync(Path.GetDirectoryName(dll)!,
            [dll, "--coverage", "--coverage-output-format", "cobertura", "--coverage-output", output, .. extra]);
        Assert.AreEqual(0, code, text);
        Assert.IsTrue(File.Exists(output), text);
        return output;
    }
}

/// <summary>
/// The golden sample in small: Desk.Api (an ASP.NET Core minimal API with a [GeneratedRegex], whose /boom endpoint
/// throws: the exception is logged with its stack trace), Labcoat.Probe (a library named like Labcoat's own assemblies),
/// Desk.Api.Tests (TUnit + WebApplicationFactory, with a coverage.settings.xml that counts Desk.Api only) and
/// Desk.Plain.Tests (the same without settings). Built once per test run.
/// </summary>
internal sealed class DeskWorkspace
{
    private static readonly object s_gate = new();
    private static Task<DeskWorkspace>? s_instance;

    public required string Root { get; init; }
    public string SettingsTestsProject => Path.Combine(Root, "Desk.Api.Tests", "Desk.Api.Tests.csproj");
    public string SettingsTestsDll => Path.Combine(Root, "Desk.Api.Tests", "bin", "Debug", "net10.0", "Desk.Api.Tests.dll");
    public string PlainTestsProject => Path.Combine(Root, "Desk.Plain.Tests", "Desk.Plain.Tests.csproj");
    /// <summary>Settings asking for static instrumentation (Coverage.Settings in the static-instrumentation test).</summary>
    public string StaticSettings => Path.Combine(Root, "static.coverage-settings.xml");
    /// <summary>The line of Tickets.Unused's body (never executed).</summary>
    public const int UnusedLine = 21;

    public static Task<DeskWorkspace> GetAsync()
    {
        lock (s_gate) return s_instance ??= Task.Run(CreateAsync);
    }

    private static async Task<DeskWorkspace> CreateAsync()
    {
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        var root = ScratchWorkspace.CreateRealTempDirectory("labcoat-coverage-e2e-");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, recursive: true); } catch { } };
        var ws = new DeskWorkspace { Root = root };
        ws.WriteAll();
        var (code, output) = await ScratchWorkspace.DotnetAsync(root, "build", Path.Combine(root, "desk.slnx"), "-nologo", "-v:q", "-tl:off");
        if (code != 0) throw new InvalidOperationException("Building the desk workspace failed:\n" + output);
        return ws;
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string TestsProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="TUnit" Version="1.72.10" />
            <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="../Desk.Api/Desk.Api.csproj" />
            <ProjectReference Include="../Labcoat.Probe/Labcoat.Probe.csproj" />
          </ItemGroup>
          SETTINGS
        </Project>
        """;

    private static string Tests(string ns) => $$"""
        using Microsoft.AspNetCore.Mvc.Testing;

        namespace {{ns}};

        public class TicketTests
        {
            [Test]
            public async Task Describes_a_ticket()
            {
                await using var factory = new WebApplicationFactory<Program>();
                var text = await factory.CreateClient().GetStringAsync("/tickets/7");
                await Assert.That(text).IsEqualTo("ticket 7");
            }

            [Test]
            public async Task A_failing_endpoint_answers_500()
            {
                // The API logs the exception with its stack trace: file and line numbers come from Desk.Api.pdb.
                await using var factory = new WebApplicationFactory<Program>();
                var response = await factory.CreateClient().GetAsync("/boom");
                await Assert.That((int)response.StatusCode).IsEqualTo(500);
            }

            [Test]
            public async Task Probe() => await Assert.That(Labcoat.Probe.Probe.Ping()).IsEqualTo(1);
        }
        """;

    private void WriteAll()
    {
        Write("desk.slnx", """
            <Solution>
              <Project Path="Desk.Api/Desk.Api.csproj" />
              <Project Path="Labcoat.Probe/Labcoat.Probe.csproj" />
              <Project Path="Desk.Api.Tests/Desk.Api.Tests.csproj" />
              <Project Path="Desk.Plain.Tests/Desk.Plain.Tests.csproj" />
            </Solution>
            """);
        Write("Desk.Api/Desk.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        Write("Desk.Api/Program.cs", """
            using Desk.Api;
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/tickets/{id:int}", (int id) => Tickets.Describe(id));
            app.MapGet("/boom", () => Tickets.Boom());
            app.Run();
            public partial class Program { }
            """);
        // Line numbers matter: UnusedLine is Unused's body.
        Write("Desk.Api/Tickets.cs", """
            using System.Text.RegularExpressions;

            namespace Desk.Api;

            public static partial class Tickets
            {
                public static string Describe(int id)
                {
                    if (id <= 0)
                    {
                        return "none";
                    }
                    return Digits().IsMatch(id.ToString()) ? $"ticket {id}" : "?";
                }

                public static string Boom() => throw new InvalidOperationException("boom");

                public static int Unused(int a)
                {
                    var doubled = a * 2;
                    return doubled + 1;
                }

                [GeneratedRegex("^[0-9]+$")]
                private static partial Regex Digits();
            }
            """);
        Write("Labcoat.Probe/Labcoat.Probe.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write("Labcoat.Probe/Probe.cs", """
            namespace Labcoat.Probe;
            public static class Probe
            {
                public static int Ping()
                {
                    return 1;
                }
            }
            """);
        Write("Desk.Api.Tests/Desk.Api.Tests.csproj",
            TestsProject.Replace("SETTINGS", """<ItemGroup><None Include="coverage.settings.xml" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>"""));
        Write("Desk.Api.Tests/TicketTests.cs", Tests("Desk.Api.Tests"));
        Write("Desk.Api.Tests/coverage.settings.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Configuration>
              <CodeCoverage>
                <ModulePaths>
                  <Include>
                    <ModulePath>.*Desk\.Api\.dll$</ModulePath>
                  </Include>
                </ModulePaths>
              </CodeCoverage>
            </Configuration>
            """);
        Write("Desk.Plain.Tests/Desk.Plain.Tests.csproj", TestsProject.Replace("SETTINGS", ""));
        Write("Desk.Plain.Tests/TicketTests.cs", Tests("Desk.Plain.Tests"));
        Write("static.coverage-settings.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Configuration>
              <CodeCoverage>
                <EnableStaticManagedInstrumentation>True</EnableStaticManagedInstrumentation>
                <EnableDynamicManagedInstrumentation>False</EnableDynamicManagedInstrumentation>
                <ModulePaths>
                  <Include>
                    <ModulePath>.*Desk\.Api\.dll$</ModulePath>
                  </Include>
                </ModulePaths>
              </CodeCoverage>
            </Configuration>
            """);
    }
}
