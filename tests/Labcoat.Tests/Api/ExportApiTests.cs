using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Labcoat;
using Labcoat.Tests.Unit;

namespace Labcoat.Tests.Api;

/// <summary>GET /api/runs/{runId}/export and GET /api/coverage/export (LabcoatApp.Export.cs) on the in-memory app.</summary>
[TestClass]
public class ExportApiTests
{
    public TestContext TestContext { get; set; }

    private static async Task AssertError(HttpStatusCode expected, Task<HttpResponseMessage> request, string errorContains)
    {
        using var res = await request;
        var body = await res.Content.ReadAsStringAsync();
        Assert.AreEqual(expected, res.StatusCode, body);
        Assert.Contains(errorContains, JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    /// <summary>Two recorded runs: run-1 (older) and run-2 (latest, with a failure whose live result is still this run's).</summary>
    private static void RecordRuns(ApiHost h)
    {
        var pass = h.Test("t1", Outcome.Passed, stdOut: "out <b>1</b>");
        var fail = h.Test("t2", Outcome.Failed) with { DisplayName = "Data (1,\"<x>\")", ErrorMessage = "Expected:<1>. Actual:<2>.", ErrorType = "System.Exception", StackTrace = "at X" };
        h.Seed(pass, fail);
        lock (h.Explorer.Sync)
        {
            h.Explorer.HistoryState.Record(new RunSummary { RunId = "run-1", StartedAt = "2026-10-01T10:00:00Z", Total = 1, Passed = 1 }, [pass]);
            h.Explorer.HistoryState.Record(new RunSummary { RunId = "run-2", StartedAt = "2026-10-02T10:00:00Z", Total = 2, Passed = 1, Failed = 1, Trigger = "api" }, [pass, fail]);
        }
    }

    [TestMethod]
    public async Task Run_exports_are_downloads_of_junit_and_html()
    {
        await using var h = await ApiHost.StartAsync();
        await AssertError(HttpStatusCode.NotFound, h.Client.GetAsync("/api/runs/latest/export?format=junit", TestContext.CancellationToken), "No runs yet");
        RecordRuns(h);
        await AssertError(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/runs/latest/export", TestContext.CancellationToken), "format must be junit, html, markdown, trx or sarif");
        await AssertError(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/runs/latest/export?format=pdf", TestContext.CancellationToken), "format must be junit, html, markdown, trx or sarif");
        await AssertError(HttpStatusCode.NotFound, h.Client.GetAsync("/api/runs/nope/export?format=html", TestContext.CancellationToken), "Unknown run id");

        using (var res = await h.Client.GetAsync("/api/runs/latest/export?format=junit", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.AreEqual("application/xml", res.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
            Assert.AreEqual("labcoat-api-tests-run-2.junit.xml", res.Content.Headers.ContentDisposition.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
            Assert.AreEqual("no-store", res.Headers.CacheControl!.ToString());
            Assert.AreEqual("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            var doc = ExportTests.ValidJUnit(await res.Content.ReadAsStringAsync(TestContext.CancellationToken));
            var cases = doc.Descendants("testcase").ToList();
            Assert.HasCount(2, cases);
            var failed = cases.Single(c => c.Element("error") is not null);
            Assert.AreEqual("Data (1,\"<x>\")", (string)failed.Attribute("name")!);
            Assert.AreEqual("Shop.CartTests", (string)failed.Attribute("classname")!);
            Assert.AreEqual("System.Exception", (string)failed.Element("error")!.Attribute("type")!, "the live result is this run's: its error type");
            Assert.AreEqual("out <b>1</b>", cases.Single(c => c.Element("error") is null).Element("system-out")!.Value);
            Assert.AreEqual("api", doc.Descendants("property").Single(p => (string)p.Attribute("name")! == "labcoat.trigger").Attribute("value")!.Value);
        }

        // an older run: names from the tests, no output (the live result is a later run's)
        var old = ExportTests.ValidJUnit(await h.Client.GetStringAsync("/api/runs/run-1/export?format=junit", TestContext.CancellationToken));
        var c1 = old.Descendants("testcase").Single();
        Assert.AreEqual("Test_t1", (string)c1.Attribute("name")!);
        Assert.IsNull(c1.Element("system-out"));

        using (var res = await h.Client.GetAsync("/api/runs/run-2/export?format=html", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.AreEqual("text/html", res.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("labcoat-api-tests-run-2.html", res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
            var html = await res.Content.ReadAsStringAsync(TestContext.CancellationToken);
            Assert.StartsWith("<!doctype html>", html);
            Assert.Contains("Data (1,&quot;&lt;x&gt;&quot;)", html);
            Assert.Contains("1 test failed", html);
        }
    }

    [TestMethod]
    public async Task Markdown_trx_and_sarif_exports_are_downloads_too()
    {
        await using var h = await ApiHost.StartAsync();
        RecordRuns(h);
        foreach (var (format, mediaType, file) in new[]
        {
            ("markdown", "text/markdown", "labcoat-api-tests-run-2.md"),
            ("trx", "application/xml", "labcoat-api-tests-run-2.trx"),
            ("sarif", "application/sarif+json", "labcoat-api-tests-run-2.sarif"),
        })
        {
            using var res = await h.Client.GetAsync($"/api/runs/latest/export?format={format}", TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode, format);
            Assert.AreEqual(mediaType, res.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual(file, res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
            var body = await res.Content.ReadAsStringAsync(TestContext.CancellationToken);
            Assert.Contains("Data (1,", body, format);
        }
    }

    [TestMethod]
    public async Task A_run_without_stored_results_is_not_found_and_hosts_are_checked()
    {
        await using var h = await ApiHost.StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/runs/latest/export?format=junit");
        req.Headers.Host = "evil.example";
        using var res = await h.Client.SendAsync(req, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, res.StatusCode, "the Host check applies to exports too");
    }

    [TestMethod]
    public async Task Coverage_exports_lcov_and_cobertura_of_the_full_or_last_report()
    {
        await using var h = await ApiHost.StartAsync();
        await AssertError(HttpStatusCode.NotFound, h.Client.GetAsync("/api/coverage/export?format=lcov", TestContext.CancellationToken), "No coverage report yet");
        await AssertError(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage/export", TestContext.CancellationToken), "format must be lcov or cobertura");
        await AssertError(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage/export?format=lcov&scope=x", TestContext.CancellationToken), "scope must be full or last");
        await AssertError(HttpStatusCode.BadRequest, h.Client.GetAsync("/api/coverage/export?format=lcov&paths=x", TestContext.CancellationToken), "paths must be relative or absolute");

        var src = Directory.CreateDirectory(Path.Combine(h.Workspace, "src")).FullName;
        var covered = Path.Combine(src, "Cart.cs");
        File.WriteAllText(covered, "a\nb");
        var xml = Path.Combine(h.Dir.FullName, "full.xml");
        File.WriteAllText(xml, $"<coverage><sources /><packages><package name=\"Shop\"><classes><class name=\"C\" filename=\"{covered}\"><lines>" +
            "<line number=\"1\" hits=\"3\" branch=\"True\" condition-coverage=\"50% (1/2)\" /><line number=\"2\" hits=\"0\" branch=\"False\" /></lines></class></classes></package></packages></coverage>");
        var acc = new CoverageAccumulator();
        acc.AddCobertura(xml, new HashSet<string>());
        h.Explorer.FullCoverageState.SetFull(ApiHost.ProjectId, acc, "full-run", null);
        h.Explorer.SetCoverageReport(new CoverageReport { RunId = "last-run", GeneratedAt = "2026-10-11T00:00:00Z", ProjectIds = [ApiHost.ProjectId], Files = [] });

        using (var res = await h.Client.GetAsync("/api/coverage/export?format=lcov", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.AreEqual("text/plain", res.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("labcoat-api-tests-full-run.info", res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
            var lcov = await res.Content.ReadAsStringAsync(TestContext.CancellationToken);
            Assert.Contains("SF:src/Cart.cs\n", lcov, "relative to the workspace root");
            Assert.Contains("BRDA:1,0,0,1\nBRDA:1,0,1,0\n", lcov);
            Assert.Contains("DA:1,3\nDA:2,0\nLF:2\nLH:1\nend_of_record", lcov);
        }
        var absolute = await h.Client.GetStringAsync("/api/coverage/export?format=lcov&paths=absolute", TestContext.CancellationToken);
        Assert.Contains($"SF:{covered}\n", absolute);
        using (var res = await h.Client.GetAsync("/api/coverage/export?format=cobertura&scope=last", TestContext.CancellationToken))
        {
            Assert.AreEqual("application/xml", res.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("labcoat-api-tests-last-run.cobertura.xml", res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
            var doc = XDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.CancellationToken));
            Assert.AreEqual("coverage", doc.Root!.Name.LocalName);
            Assert.Contains("coverage of run last-run", doc.Nodes().OfType<XComment>().Single().Value);
        }
    }
}
