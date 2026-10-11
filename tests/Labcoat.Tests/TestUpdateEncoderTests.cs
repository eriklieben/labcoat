using System.Text.Json;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests;

/// <summary>Full entries vs result patches (contract "Round 7"), the patch fields, and the lean snapshot shape.</summary>
[TestClass]
public class TestUpdateEncoderTests
{
    private static TestNode Node(string id, Outcome outcome = Outcome.NotRun, string project = "P") => new()
    {
        Id = id, ProjectId = project, DisplayName = id, Namespace = "N", ClassName = "C", MethodName = id,
        FilePath = "/src/" + id + ".cs", Line = 3, LineEnd = 3, Outcome = outcome,
    };

    private static JsonNode Json_(object o) => JsonNode.Parse(JsonSerializer.Serialize(o, Json.Options))!;

    [TestMethod]
    public void Unknown_and_restructured_tests_go_out_in_full_result_changes_as_patches()
    {
        var enc = new TestUpdateEncoder();
        var a = Node("a");
        var b = Node("b");
        enc.Reset([a, b]);
        Assert.AreEqual(2, enc.Count);

        var a2 = a.Copy();
        a2.Outcome = Outcome.Running;
        var b2 = b.Copy();
        b2.DisplayName = "b (renamed)";
        var c = Node("c");
        var (full, changed) = enc.Split([a2, b2, c]);
        CollectionAssert.AreEqual(new[] { "b", "c" }, full.Select(t => t.Id).ToArray());
        Assert.AreEqual(("a", a2), (changed.Single().Prev.Id, changed.Single().Next));

        // the mirror now holds what was sent: an unchanged copy needs nothing
        var (full2, changed2) = enc.Split([a2.Copy()]);
        Assert.IsEmpty(full2);
        Assert.IsNull(TestUpdateEncoder.Patches(changed2, lean: false));

        // tests with updates pending while a client took its own snapshot go out in full once
        enc.ForceFull([("P", "a")]);
        var a3 = a2.Copy();
        a3.Outcome = Outcome.Passed;
        Assert.HasCount(1, enc.Split([a3]).Full);
        Assert.HasCount(1, enc.Split([a3.Copy() with { Outcome = Outcome.Failed }]).Changed);

        // traits are structure too (by value)
        var t1 = a3.Copy();
        t1.Traits = [new Trait("Category", "x")];
        Assert.HasCount(1, enc.Split([t1]).Full);
        Assert.HasCount(1, enc.Split([t1.Copy() with { Outcome = Outcome.Skipped }]).Changed);
    }

    [TestMethod]
    public void Patches_set_what_changed_and_clear_what_is_gone()
    {
        var prev = Node("a", Outcome.Failed);
        prev.ErrorMessage = "boom";
        prev.StackTrace = "at X";
        prev.StdOut = "out";
        prev.DurationMs = 5;
        prev.LastRunAt = "t1";
        prev.Stale = true;
        var next = prev.Copy();
        next.Outcome = Outcome.Passed;
        next.ErrorMessage = null;
        next.StackTrace = null;
        next.StdOut = "out 2";
        next.DurationMs = 7;
        next.LastRunAt = "t2";
        next.Stale = null;
        next.TraceId = "abc";
        string? runId = null;
        var legacy = TestUpdateEncoder.Diff(prev, next, lean: false, ref runId)!;
        Assert.AreEqual(
            """{"id":"a","outcome":"passed","durationMs":7,"stdOut":"out 2","lastRunAt":"t2","traceId":"abc","clear":["errorMessage","stackTrace","stale"]}""",
            Json_(legacy).ToJsonString());
        // lean: no output, only whether there is some (unchanged here)
        var lean = TestUpdateEncoder.Diff(prev, next, lean: true, ref runId)!;
        Assert.IsNull(lean.StdOut);
        Assert.IsNull(lean.HasOutput);
        var noOut = next.Copy();
        noOut.StdOut = null;
        CollectionAssert.Contains(TestUpdateEncoder.Diff(prev, noOut, lean: true, ref runId)!.Clear!, "hasOutput");
        Assert.IsTrue(TestUpdateEncoder.Diff(noOut, next, lean: true, ref runId)!.HasOutput);
        // nothing visible changed
        Assert.IsNull(TestUpdateEncoder.Diff(prev, prev.Copy(), lean: true, ref runId));
        Assert.IsNull(TestUpdateEncoder.Diff(prev, prev.Copy() with { StdOut = "other" }, lean: true, ref runId));
        // a snapshot mismatch (derived) and flags
        var verify = prev.Copy();
        verify.ErrorMessage = "Directory: /x\nNotEqual:\n  - Received: a.received.txt\n    Verified: a.verified.txt";
        Assert.IsTrue(TestUpdateEncoder.Diff(prev, verify, lean: true, ref runId)!.HasSnapshotMismatch);
    }

    [TestMethod]
    public void History_appends_become_recorded_or_historyAdd_and_other_changes_replace_it()
    {
        var e1 = new HistoryEntry(Outcome.Passed, 1, "t1", "r1");
        var prev = Node("a", Outcome.Passed);
        prev.History = [e1];
        var next = prev.Copy();
        next.DurationMs = 2;
        next.LastRunAt = "t2";
        next.History = [e1, new HistoryEntry(Outcome.Passed, 2, "t2", "r2")];
        next.Flaky = null;

        // the entry is exactly the test's result: the short form with the event's run id
        string? runId = null;
        var p = TestUpdateEncoder.Diff(prev, next, lean: true, ref runId)!;
        Assert.AreEqual((true, "r2"), (p.Recorded, runId));
        Assert.IsNull(p.HistoryAdd);

        // another run id in the same event, or a repeat run's flaky entry: the entry itself
        var other = next.Copy();
        other.History = [e1, new HistoryEntry(Outcome.Passed, 2, "t2", "r3")];
        Assert.AreEqual("r3", TestUpdateEncoder.Diff(prev, other, lean: true, ref runId)!.HistoryAdd!.RunId);
        var flaky = next.Copy();
        flaky.History = [e1, new HistoryEntry(Outcome.Failed, 2, "t2", "r2", Flaky: true)];
        flaky.Flaky = true;
        string? none = null;
        var fp = TestUpdateEncoder.Diff(prev, flaky, lean: false, ref none)!;
        Assert.AreEqual((true, true), (fp.HistoryAdd!.Flaky, fp.Flaky));

        // at the cap the oldest entry drops out: still an append
        var full = Enumerable.Range(0, TestUpdateEncoder.MaxHistory).Select(i => new HistoryEntry(Outcome.Passed, i, $"t{i}", $"r{i}")).ToList();
        var capped = full.Skip(1).Append(new HistoryEntry(Outcome.Failed, 9, "tx", "rx")).ToList();
        Assert.IsTrue(TestUpdateEncoder.IsAppend(full, capped));
        Assert.IsFalse(TestUpdateEncoder.IsAppend(full, full.Skip(2).Append(e1).Append(e1).ToList()));
        Assert.IsTrue(TestUpdateEncoder.IsAppend(null, [e1]));

        // anything else replaces it; no history any more clears it; an equal copy is no change
        var replaced = prev.Copy();
        replaced.History = [new HistoryEntry(Outcome.Failed, 3, "t3", "r9")];
        string? r = null;
        Assert.HasCount(1, TestUpdateEncoder.Diff(prev, replaced, lean: true, ref r)!.History!);
        var gone = prev.Copy();
        gone.History = null;
        CollectionAssert.Contains(TestUpdateEncoder.Diff(prev, gone, lean: true, ref r)!.Clear!, "history");
        Assert.IsNull(TestUpdateEncoder.Diff(prev, prev.Copy() with { History = [e1] }, lean: true, ref r));
    }

    [TestMethod]
    public void Patches_are_grouped_per_project_in_order()
    {
        var pairs = new List<(TestNode, TestNode)>
        {
            (Node("a"), Node("a", Outcome.Running)),
            (Node("b", project: "Q"), Node("b", Outcome.Running, "Q")),
            (Node("c"), Node("c", Outcome.Queued)),
            (Node("d"), Node("d")), // no change: left out
        };
        var e = TestUpdateEncoder.Patches(pairs, lean: true)!;
        var json = JsonNode.Parse(JsonSerializer.Serialize<ServerEvent>(e, Json.Options))!;
        Assert.AreEqual("testResults", (string?)json["type"]);
        Assert.AreEqual(
            """[{"projectId":"P","results":[{"id":"a","outcome":"running"},{"id":"c","outcome":"queued"}]},{"projectId":"Q","results":[{"id":"b","outcome":"running"}]}]""",
            json["projects"]!.ToJsonString());
        Assert.IsNull(json["runId"]);
    }

    [TestMethod]
    public void Lean_copies_drop_a_line_end_equal_to_the_line_but_keep_traits()
    {
        var t = Node("a");
        var lean = Json_(t.LeanCopy());
        Assert.AreEqual(0, lean["traits"]!.AsArray().Count); // always present: the UI reads it unguarded
        Assert.IsNull(lean["lineEnd"]);
        Assert.AreEqual(3, (int)lean["line"]!);
        Assert.IsNotNull(Json_(t)["traits"]); // default payloads keep them
        Assert.AreEqual(3, (int)Json_(t)["lineEnd"]!);
        t.Traits = [new Trait("k", "v")];
        t.LineEnd = 9;
        lean = Json_(t.LeanCopy());
        Assert.AreEqual(("k", 9), ((string?)lean["traits"]![0]!["key"], (int)lean["lineEnd"]!));
    }
}
