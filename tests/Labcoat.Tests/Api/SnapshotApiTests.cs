using System.Net;
using System.Text.Json.Nodes;
using Labcoat;
using Labcoat.Tests.Unit;

namespace Labcoat.Tests.Api;

/// <summary>The Verify snapshot endpoints (contract Round 6) on the real app: list, read, accept, reject, path rules.</summary>
[TestClass]
public class SnapshotApiTests
{
    public TestContext TestContext { get; set; }

    private static string SnapDir(ApiHost h) => Directory.CreateDirectory(Path.Combine(h.Workspace, ApiHost.ProjectId, "Snapshots")).FullName;

    /// <summary>A failed test of A.Tests whose Verify message names the given snapshots (in Snapshots/).</summary>
    private static TestNode Failed(ApiHost h, string id, string section, params string[] names)
    {
        var t = h.Test(id, Outcome.Failed);
        t.ClassName = "CartTests";
        t.ErrorMessage = SnapshotTests.Message(SnapDir(h), section, names);
        return t;
    }

    private static string Write(ApiHost h, string name, string text)
    {
        var p = Path.Combine(SnapDir(h), name);
        File.WriteAllText(p, text);
        return p;
    }

    private static Task<HttpResponseMessage> Accept(ApiHost h, string json) => h.PostJson("/api/snapshots/accept", json);

    [TestMethod]
    public async Task Lists_a_tests_pending_snapshots()
    {
        await using var h = await ApiHost.StartAsync();
        Write(h, "CartTests.Total.received.txt", "﻿new");
        Write(h, "CartTests.Total.verified.txt", "old");
        File.WriteAllBytes(Path.Combine(SnapDir(h), "CartTests.Chart.received.png"), [137, 80, 78, 71]);
        var t1 = Failed(h, "t1", "NotEqual", "CartTests.Total");
        t1.ErrorMessage = t1.ErrorMessage!.Replace("\n\nFileContent:", "\n  - Received: CartTests.Chart.received.png\n    Verified: CartTests.Chart.verified.png\n\nFileContent:");
        h.Seed(t1, h.Test("t2", Outcome.Passed));

        Assert.AreEqual(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/tests/snapshots", TestContext.CancellationToken)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await h.Client.GetAsync("/api/tests/snapshots?id=nope", TestContext.CancellationToken)).StatusCode);

        var s = await h.GetJson($"/api/tests/snapshots?projectId={ApiHost.ProjectId}&id=t1");
        Assert.AreEqual(SnapDir(h), s["directory"]!.GetValue<string>());
        var snaps = s["snapshots"]!.AsArray();
        Assert.AreEqual(2, snaps.Count);
        Assert.AreEqual(("CartTests.Total.verified.txt", "text", false), (snaps[0]!["name"]!.GetValue<string>(), snaps[0]!["kind"]!.GetValue<string>(), snaps[0]!["isNew"]!.GetValue<bool>()));
        Assert.AreEqual(("image", true), (snaps[1]!["kind"]!.GetValue<string>(), snaps[1]!["isNew"]!.GetValue<bool>()));

        // the flag is in the state; a passing test has none
        var state = await h.GetJson("/api/state?lean=1");
        Assert.IsTrue(state["tests"]![0]!["hasSnapshotMismatch"]!.GetValue<bool>());
        Assert.IsNull(state["tests"]![1]!["hasSnapshotMismatch"]);
        Assert.AreEqual(0, (await h.GetJson("/api/tests/snapshots?id=t2"))["snapshots"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task Serves_snapshot_files_only()
    {
        await using var h = await ApiHost.StartAsync();
        var text = Write(h, "CartTests.Total.received.txt", "﻿line 1\nline 2");
        var png = Path.Combine(SnapDir(h), "CartTests.Chart.verified.png");
        File.WriteAllBytes(png, [137, 80, 78, 71, 0, 1]);
        var bin = Path.Combine(SnapDir(h), "CartTests.Blob.received.bin");
        File.WriteAllBytes(bin, [1, 0, 2]);
        var source = Write(h, "CartTests.cs", "class CartTests {}");
        var outside = Path.Combine(h.Dir.FullName, "Other.received.txt");
        File.WriteAllText(outside, "x");

        using var t = await h.Client.GetAsync("/api/snapshots/file?path=" + Uri.EscapeDataString(text), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, t.StatusCode);
        Assert.AreEqual("text/plain", t.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("line 1\nline 2", await t.Content.ReadAsStringAsync(TestContext.CancellationToken)); // BOM dropped
        Assert.AreEqual("no-store", t.Headers.CacheControl!.ToString());

        using var i = await h.Client.GetAsync("/api/snapshots/file?path=" + Uri.EscapeDataString(png), TestContext.CancellationToken);
        Assert.AreEqual("image/png", i.Content.Headers.ContentType!.MediaType);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 0, 1 }, await i.Content.ReadAsByteArrayAsync(TestContext.CancellationToken));

        using var b = await h.Client.GetAsync("/api/snapshots/file?path=" + Uri.EscapeDataString(bin), TestContext.CancellationToken);
        Assert.AreEqual("application/octet-stream", b.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("attachment", b.Content.Headers.ContentDisposition!.DispositionType);

        foreach (var (path, status) in new[]
                 {
                     (source, HttpStatusCode.Forbidden),                     // not a snapshot file name
                     (outside, HttpStatusCode.Forbidden),                    // outside the source roots
                     (Path.Combine(SnapDir(h), "..", "..", "..", "Other.received.txt"), HttpStatusCode.Forbidden), // traversal
                     (Path.Combine(SnapDir(h), "Missing.received.txt"), HttpStatusCode.NotFound),
                     (SnapDir(h) + "/Dir.received.txt", HttpStatusCode.NotFound),
                 })
            Assert.AreEqual(status, (await h.Client.GetAsync("/api/snapshots/file?path=" + Uri.EscapeDataString(path), TestContext.CancellationToken)).StatusCode, path);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/snapshots/file", TestContext.CancellationToken)).StatusCode);
    }

    [TestMethod]
    public async Task Accept_moves_received_to_verified_and_clears_the_flag()
    {
        await using var h = await ApiHost.StartAsync();
        var received = Write(h, "CartTests.Total.received.txt", "new");
        var verified = Write(h, "CartTests.Total.verified.txt", "old");
        var fresh = Write(h, "CartTests.Fresh.received.txt", "fresh");
        h.Seed(Failed(h, "t1", "NotEqual", "CartTests.Total", "CartTests.Fresh"));

        // only the named one
        using (var res = await Accept(h, """{"projectId":"A.Tests","id":"t1","names":["CartTests.Total.verified.txt"],"rerun":false}"""))
        {
            var body = await ApiHost.Json(res);
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode, body?.ToJsonString());
            Assert.AreEqual("CartTests.Total.verified.txt", body!["tests"]![0]!["files"]![0]!.GetValue<string>());
            Assert.IsNull(body["runId"]);
        }
        Assert.IsFalse(File.Exists(received));
        Assert.AreEqual("new", File.ReadAllText(verified));
        Assert.IsTrue(File.Exists(fresh));
        Assert.IsTrue(h.Explorer.GetTest(null, "t1")!.HasSnapshotMismatch, "one snapshot still pending");

        Assert.AreEqual(HttpStatusCode.NotFound, (await Accept(h, """{"id":"t1","names":["Nope.verified.txt"]}""")).StatusCode);

        // the rest (a new snapshot) -> creates the verified file, the flag goes
        Assert.AreEqual(HttpStatusCode.OK, (await Accept(h, """{"id":"t1","rerun":false}""")).StatusCode);
        Assert.AreEqual("fresh", File.ReadAllText(Path.Combine(SnapDir(h), "CartTests.Fresh.verified.txt")));
        Assert.IsNull(h.Explorer.GetTest(null, "t1")!.HasSnapshotMismatch);
        Assert.AreEqual(0, (await h.GetJson("/api/tests/snapshots?id=t1"))["snapshots"]!.AsArray().Count);
        Assert.IsTrue(h.Explorer.Snapshot(includeLogs: true).Logs!.Any(l => l.Message.Contains("Accepted 1 snapshot of", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Accept_reruns_by_default_and_reject_deletes()
    {
        await using var h = await ApiHost.StartAsync();
        Write(h, "CartTests.Total.received.txt", "new");
        var keep = Write(h, "CartTests.Total.verified.txt", "old");
        var other = Write(h, "CartTests.Other.received.txt", "x");
        h.Seed(Failed(h, "t1", "NotEqual", "CartTests.Total"), Failed(h, "t2", "New", "CartTests.Other"));

        using (var res = await h.PostJson("/api/snapshots/reject", """{"id":"t1"}"""))
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.IsFalse(File.Exists(Path.Combine(SnapDir(h), "CartTests.Total.received.txt")));
        Assert.AreEqual("old", File.ReadAllText(keep));
        Assert.IsNull(h.Explorer.GetTest(null, "t1")!.HasSnapshotMismatch);

        // accept all (no id): t2 only (t1 is resolved); a re-run is requested
        using var all = await Accept(h, "{}");
        var body = await ApiHost.Json(all);
        Assert.AreEqual(HttpStatusCode.OK, all.StatusCode, body?.ToJsonString());
        Assert.AreEqual(1, body!["tests"]!.AsArray().Count);
        Assert.AreEqual("t2", body["tests"]![0]!["id"]!.GetValue<string>());
        Assert.IsFalse(string.IsNullOrEmpty(body["runId"]?.GetValue<string>()));
        Assert.IsFalse(File.Exists(other));
    }

    [TestMethod]
    public async Task Bad_requests_and_running_tests_are_refused()
    {
        await using var h = await ApiHost.StartAsync();
        Write(h, "CartTests.Total.received.txt", "new");
        var running = Failed(h, "t1", "NotEqual", "CartTests.Total");
        h.Seed(running, Failed(h, "t2", "New", "CartTests.Total") with { Outcome = Outcome.Queued });

        await Expect(HttpStatusCode.BadRequest, Accept(h, "{not json"), "Malformed");
        await Expect(HttpStatusCode.NotFound, Accept(h, """{"id":"nope"}"""), "Unknown test");
        await Expect(HttpStatusCode.NotFound, Accept(h, """{"projectId":"Nope"}"""), "Unknown project");
        await Expect(HttpStatusCode.BadRequest, Accept(h, """{"names":["x"]}"""), "names needs an id");
        await Expect(HttpStatusCode.Conflict, h.PostJson("/api/snapshots/reject", """{"id":"t2"}"""), "running");
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, (await h.PostRaw("/api/snapshots/accept", """{"id":"t1"}""", "text/plain")).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/snapshots/accept") { Content = new StringContent("""{"id":"t1"}""", System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("Origin", "http://evil.example");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await h.Client.SendAsync(req, TestContext.CancellationToken)).StatusCode);
        Assert.IsTrue(File.Exists(Path.Combine(SnapDir(h), "CartTests.Total.received.txt")), "nothing was accepted");

        // an empty body = accept all: the queued test is skipped
        using var empty = await h.PostRaw("/api/snapshots/accept", "", "application/json");
        Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
    }

    [TestMethod]
    public async Task Snapshots_outside_the_project_are_never_touched()
    {
        await using var h = await ApiHost.StartAsync();
        var outsideDir = Directory.CreateDirectory(Path.Combine(h.Workspace, "Elsewhere")).FullName;
        var outside = Path.Combine(outsideDir, "CartTests.Total.received.txt");
        File.WriteAllText(outside, "x");
        var t = h.Test("t1", Outcome.Failed);
        t.ErrorMessage = SnapshotTests.Message(outsideDir, "New", "CartTests.Total");
        h.Seed(t);

        Assert.AreEqual(0, (await h.GetJson("/api/tests/snapshots?id=t1"))["snapshots"]!.AsArray().Count);
        using var res = await Accept(h, """{"id":"t1","rerun":false}""");
        Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.IsTrue(File.Exists(outside));
        Assert.IsFalse(File.Exists(Path.Combine(outsideDir, "CartTests.Total.verified.txt")));
    }

    private static async Task Expect(HttpStatusCode status, Task<HttpResponseMessage> request, string errorContains)
    {
        using var res = await request;
        var body = await res.Content.ReadAsStringAsync();
        Assert.AreEqual(status, res.StatusCode, body);
        Assert.Contains(errorContains, JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }
}
