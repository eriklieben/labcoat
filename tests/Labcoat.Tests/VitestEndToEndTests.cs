using Labcoat;

namespace Labcoat.Tests;

/// <summary>
/// A Vitest project driven through Labcoat with the Vitest runner (src/Labcoat.Vitest/runner, docs/runners.md):
/// discovery, runs, a coverage run (a new host process) and a watch-style rerun after an edit. Needs Node.js on PATH and
/// a Vitest install to link the scratch project to: $LABCOAT_VITEST_NODE_MODULES, else ui/node_modules (npm ci in
/// ui/); inconclusive without them. The runner's own tests: tests/Labcoat.Vitest.Tests.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("e2e")]
public class VitestEndToEndTests
{
    private static readonly string Repo = FindRepo();

    private static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Labcoat.slnx"))) return d.FullName;
        throw new InvalidOperationException("Labcoat.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string? VitestModules() =>
        new[] { Environment.GetEnvironmentVariable("LABCOAT_VITEST_NODE_MODULES"), Path.Combine(Repo, "ui", "node_modules") }
            .FirstOrDefault(d => d is not null && File.Exists(Path.Combine(d, "vitest", "package.json")) && File.Exists(Path.Combine(d, "@vitest", "coverage-v8", "package.json")));

    private const string MathTests = """
        import { describe, expect, it } from "vitest";
        import { add } from "../src/math.js";
        describe("math", () => {
          it("adds", () => { expect(add(2, 3)).toBe(5); });
          it.each([[1, 1, 2], [2, 2, 4]])("add(%i, %i) = %i", (a, b, c) => { expect(add(a, b)).toBe(c); });
          it("fails", () => { expect(add(1, 1)).toBe(3); });
        });
        """;

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Discover_run_cover_and_rerun_a_vitest_project()
    {
        if (RunnerPlugin.ExecutableOnPath("node") is null) Assert.Inconclusive("Node.js isn't on PATH.");
        var modules = VitestModules();
        if (modules is null) Assert.Inconclusive("No Vitest install to link to (npm ci in ui/, or set LABCOAT_VITEST_NODE_MODULES).");

        var root = Directory.CreateTempSubdirectory("labcoat-vitest-e2e-");
        var dir = root.CreateSubdirectory("web");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "package.json"), """{ "name": "web", "private": true, "type": "module", "devDependencies": { "vitest": "*" } }""");
            File.WriteAllText(Path.Combine(dir.FullName, "vitest.config.mjs"),
                """export default { test: { include: ["test/**/*.test.js"], coverage: { provider: "v8", include: ["src/**/*.js"] } } };""");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "src"));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "test"));
            var source = Path.Combine(dir.FullName, "src", "math.js");
            File.WriteAllText(source, "export const add = (a, b) => a + b;\n");
            File.WriteAllText(Path.Combine(dir.FullName, "test", "math.test.js"), MathTests);
            Directory.CreateSymbolicLink(Path.Combine(dir.FullName, "node_modules"), modules);

            var warnings = new List<string>();
            var plugin = TestRunners.FindPlugins([Path.Combine(Repo, "src", "Labcoat.Vitest", "runner")], warnings.Add).Single();
            var runners = new TestRunners(false, [plugin]);
            var project = ProjectDiscovery.FindProjects([dir.FullName], warnings.Add, runners: runners).Single();
            Assert.IsEmpty(warnings);
            await using var explorer = new Explorer(new EventHub(), [project], null, [dir.FullName], runners: runners);
            string Logs() => string.Join("\n", explorer.Snapshot(includeLogs: true).Logs!.Select(l => l.Message));

            // ---- discovery: the tree levels and the test.each rows grouped under their template
            await explorer.DiscoverAsync(null);
            var state = explorer.Snapshot();
            Assert.AreEqual(("web", "vitest"), (state.Projects.Single().Name, state.Projects.Single().Runner));
            var tests = state.Tests;
            Assert.HasCount(4, tests, Logs());
            var row = tests.Single(t => t.DisplayName == "add(1, 1) = 2");
            Assert.AreEqual(("test/math.test.js", "math", "add(%i, %i) = %i", 5), (row.Namespace, row.ClassName, row.MethodName, row.Line));
            Assert.AreEqual("test/math.test.js > math > add(1, 1) = 2", row.Id);

            // ---- a run on the warm host
            await explorer.RunAsync(null, null);
            var run = explorer.Summary().LastRun!;
            Assert.AreEqual((4, 3, 1), (run.Total, run.Passed, run.Failed), Logs());
            var failed = explorer.GetTest(null, "test/math.test.js > math > fails")!;
            Assert.StartsWith("expected 2 to be 3", failed.ErrorMessage);
            Assert.Contains("Expected:<3>. Actual:<2>.", failed.ErrorMessage);
            Assert.Contains("math.test.js:line 6", failed.StackTrace!);

            // ---- a coverage run of some tests: a new host process, which gets ids it hasn't discovered itself
            await explorer.RunAsync(null, ["test/math.test.js > math > adds"], coverage: true);
            Assert.AreEqual(Outcome.Passed, explorer.GetTest(null, "test/math.test.js > math > adds")!.Outcome, Logs());
            var coverage = explorer.LastCoverageReport;
            Assert.IsNotNull(coverage, Logs());
            Assert.AreEqual("math.js", Path.GetFileName(coverage.Files.Single().Path));
            Assert.AreEqual(1.0, coverage.LineRate);

            // ---- an edit is picked up by the warm host (no build: the runner reloads changed files)
            File.WriteAllText(source, "export const add = (a, b) => a + b + (a === 1 ? 1 : 0);\n");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(5));
            await explorer.RunAsync(null, ["test/math.test.js > math > fails"]);
            Assert.AreEqual(Outcome.Passed, explorer.GetTest(null, "test/math.test.js > math > fails")!.Outcome, Logs());
        }
        finally
        {
            try { root.Delete(recursive: true); } catch (IOException) { /* a host may still hold a file: temp folder */ }
        }
    }
}
