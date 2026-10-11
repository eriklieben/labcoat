using System.Text;
using System.Text.Json;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>LiveOutput.cs: attribution of live test output, coalescing, caps, snapshots, framework detection.</summary>
[TestClass]
public class LiveOutputTests
{
    public TestContext TestContext { get; set; }

    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private LiveOutput Create() => new(clock: () => _now);

    private void Advance(int ms) => _now = _now.AddMilliseconds(ms);

    private static TestNode Test(string id, string? displayName = null, string? ns = "Ns", string? cls = "C", string? method = null) => new()
    {
        Id = id, ProjectId = "P", DisplayName = displayName ?? id, Namespace = ns, ClassName = cls, MethodName = method ?? id, Traits = [],
    };

    private static string Text(IEnumerable<TestOutputEvent> events, string id) => string.Concat(events.Where(e => e.Id == id).Select(e => e.Text));

    [TestMethod]
    public void Host_lines_go_to_the_single_running_test_after_the_attribution_delay()
    {
        var live = Create();
        live.Started(Test("A"));
        live.HostLine("P", "hello", err: false);
        live.HostLine("P", "oops", err: true);
        Assert.IsEmpty(live.Drain(), "not before the attribution delay (MTP batches test updates every 200 ms)");
        Assert.IsTrue(live.HasWork);
        Advance(250);
        var events = live.Drain();
        Assert.HasCount(2, events); // one per run of the same kind (stdout, then stderr)
        Assert.AreEqual(("host", "hello\n", 1L, (bool?)null), (events[0].Stream, events[0].Text, events[0].Seq, events[0].Err));
        Assert.AreEqual(("host", "oops\n", 2L, (bool?)true), (events[1].Stream, events[1].Text, events[1].Seq, events[1].Err));
        Assert.IsFalse(live.HasWork);
    }

    [TestMethod]
    public void Host_lines_are_shared_between_parallel_tests_and_dropped_while_nothing_runs()
    {
        var live = Create();
        live.HostLine("P", "before any test", false);
        Advance(250);
        Assert.IsEmpty(live.Drain());
        Advance(1000); // no test started within StartWait: dropped
        Assert.IsEmpty(live.Drain());
        live.Started(Test("A"));
        live.Started(Test("B"));
        live.HostLine("P", "who wrote this?", false);
        live.HostLine("Other", "another project", false);
        Advance(250);
        var events = live.Drain();
        Assert.HasCount(2, events);
        Assert.IsTrue(events.All(e => e.Shared == true && e.Text == "who wrote this?\n"));
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, events.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void Coalesces_a_burst_into_one_event_per_test_and_stream()
    {
        var live = Create();
        live.Started(Test("A"));
        for (var i = 0; i < 100; i++) live.HostLine("P", $"line {i}", false);
        Advance(250);
        var events = live.Drain();
        Assert.HasCount(1, events);
        Assert.HasCount(100, events[0].Text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [TestMethod]
    public void Caps_each_flush_keeping_the_tail_with_a_marker()
    {
        var live = Create();
        live.Started(Test("A"));
        var line = new string('x', 99);
        for (var i = 0; i < 1000; i++) live.HostLine("P", line, false); // 100 000 chars
        Advance(250);
        var text = string.Concat(live.Drain().Select(e => e.Text));
        Assert.StartsWith("… [", text);
        Assert.Contains("characters skipped", text);
        Assert.IsLessThan(LiveOutput.MaxCharsPerFlush + 200, text.Length);
        Assert.EndsWith(line + "\n", text);
    }

    [TestMethod]
    public void Snapshot_keeps_head_and_tail_within_the_per_test_cap()
    {
        var live = Create();
        live.Started(Test("A"));
        var chunk = new string('y', 4999);
        for (var i = 0; i < 40; i++) // 200 000 characters, 40 flushes
        {
            live.HostLine("P", chunk, false);
            Advance(250);
            live.Drain();
        }
        var snap = live.Snapshot("P", "A")!;
        Assert.AreEqual(40L, snap.Seq);
        Assert.IsGreaterThan(0L, snap.Omitted!.Value);
        var total = snap.Chunks.Sum(c => c.Text.Length);
        Assert.IsLessThanOrEqualTo(TestOutput.MaxChars + 100, total);
        Assert.IsTrue(snap.Chunks.Any(c => c.Text.Contains("characters omitted")));
        Assert.IsNull(live.Snapshot("P", "nope"));
        Assert.IsNotNull(live.Snapshot(null, "A"));
    }

    [TestMethod]
    public void Finished_tests_get_no_more_output_and_a_restart_counts_from_one()
    {
        var live = Create();
        live.Started(Test("A"));
        live.HostLine("P", "late", false);
        live.Finished("P", "A");
        Advance(250);
        Assert.IsEmpty(live.Drain());
        Assert.IsFalse(live.IsRunning("P", "A"));
        Assert.IsNull(live.Snapshot("P", "A"));

        live.Started(Test("A"));
        live.Partial("P", "A", "again\n", null);
        Assert.AreEqual(1L, live.Drain().Single().Seq);
        live.Reset();
        Assert.IsFalse(live.IsRunning("P", "A"));
    }

    [TestMethod]
    public void Partial_takes_the_new_part_of_cumulative_mtp_output()
    {
        var live = Create();
        live.Started(Test("A"));
        live.Partial("P", "A", "1\n2\n", "e1\n");
        live.Partial("P", "A", null, null); // heartbeat
        var first = live.Drain();
        Assert.AreEqual("1\n2\n", Text(first.Where(e => e.Stream == "stdout"), "A"));
        Assert.AreEqual("e1\n", Text(first.Where(e => e.Stream == "stderr"), "A"));
        live.Partial("P", "A", "1\n2\n3\n", "e1\n");
        live.Partial("P", "A", "1\n", null); // shorter: ignored
        var second = live.Drain();
        Assert.AreEqual("3\n", second.Single().Text);
        live.Partial("P", "unknown", "x", null);
        Assert.IsEmpty(live.Drain());
    }

    [TestMethod]
    public void TUnit_projects_ignore_unattributed_host_lines()
    {
        var live = Create();
        live.Configure("P", new LiveOutputSupport(XunitLiveOption: false, TUnit: true));
        live.Started(Test("A"));
        live.HostLine("P", "console tee (also in the in-progress output)", false);
        Advance(250);
        Assert.IsEmpty(live.Drain());
    }

    [TestMethod]
    public void Xunit_live_output_lines_are_attributed_by_display_name()
    {
        var live = Create();
        live.Configure("P", new LiveOutputSupport(XunitLiveOption: true, TUnit: false));
        live.HostLine("P", "OUTPUT: [X.Tests.Slow] before the in-progress update", false);
        live.Started(Test("1", "X.Tests.Slow"));
        live.Started(Test("2", "X.Tests.Other"));
        live.HostLine("P", "OUTPUT: [X.Tests.Other] for the other test", false);
        var events = live.Drain(); // exact lines need no attribution delay
        Assert.AreEqual("before the in-progress update\n", Text(events, "1"));
        Assert.AreEqual("for the other test\n", Text(events, "2"));
        Assert.IsTrue(events.All(e => e.Stream == "stdout" && e.Shared is null));

        // a line whose test never runs is dropped after IdentityWait
        live.HostLine("P", "OUTPUT: [X.Tests.Never] nobody", false);
        Assert.IsEmpty(live.Drain());
        Assert.IsTrue(live.HasWork);
        Advance((int)LiveOutput.IdentityWait.TotalMilliseconds + 1);
        Assert.IsEmpty(live.Drain());
        Assert.IsFalse(live.HasWork);
    }

    [TestMethod]
    public void Labcoat_testing_markers_are_matched_by_class_method_and_display_name()
    {
        var live = Create();
        live.Started(Test("r1", "Rows (1)", "Ns", "C", "Rows"));
        live.Started(Test("r2", "Rows (2)", "Ns", "C", "Rows"));
        live.HostLine("P", LiveOutput.FormatMarker("out", "row two", "Ns.C", "Rows", "Rows (2)"), false);
        live.HostLine("P", "  " + LiveOutput.FormatMarker("err", "an error", "Ns.C", "Rows", "Rows (1)"), false); // interleaved
        var events = live.Drain();
        Assert.AreEqual(("stdout", "row two\n"), (events.Single(e => e.Id == "r2").Stream, events.Single(e => e.Id == "r2").Text));
        Assert.AreEqual(("stderr", "an error\n"), (events.Single(e => e.Id == "r1").Stream, events.Single(e => e.Id == "r1").Text));

        // malformed markers are ignored, never shown as host output
        live.HostLine("P", LiveOutput.Marker + "%%%not-base64", false);
        Advance(3000);
        Assert.IsEmpty(live.Drain());
    }

    [TestMethod]
    public void Marker_format_matches_Labcoat_Testing()
    {
        // The JSON shape Labcoat.Testing's LabcoatOutput.FormatMarker writes (keys s/t/c/m/n, "out" | "err").
        var json = JsonSerializer.SerializeToUtf8Bytes(new { s = "err", t = "x\n", c = "Ns.C", m = "M", n = "M" });
        Assert.IsTrue(LiveOutput.TryParseMarker(Convert.ToBase64String(json) + "\r", out var stream, out var text, out var cls, out var method, out var name));
        Assert.AreEqual(("stderr", "x\n", "Ns.C", "M", "M"), (stream, text, cls, method, name));
        Assert.IsFalse(LiveOutput.TryParseMarker("e30", out _, out _, out _, out _, out _)); // not valid base64
    }

    [TestMethod]
    [DataRow("Connecting to client host '127.0.0.1' port '4000'", true)]
    [DataRow("Test Parallelization enabled for /x.dll (Workers: 1, Scope: MethodLevel)", true)]
    [DataRow("NUnit Adapter 6.3.0.0: Test execution started", true)]
    [DataRow("MSTest v4.0.2 (UTC 11/11/2025) [linux-x64 - .NET 10.0.12]", true)]
    [DataRow("Order 42 persisted", false)]
    public void Recognizes_platform_noise(string line, bool noise) => Assert.AreEqual(noise, LiveOutput.IsNoise(line));

    [TestMethod]
    public void Detects_xunit_live_option_and_tunit_in_the_output_folder()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-live-detect-").FullName;
        try
        {
            Assert.AreEqual(LiveOutputSupport.None, LiveOutput.Detect(dir));
            Assert.AreEqual(LiveOutputSupport.None, LiveOutput.Detect(null));
            Assert.AreEqual(LiveOutputSupport.None, LiveOutput.Detect(Path.Combine(dir, "missing")));

            File.WriteAllBytes(Path.Combine(dir, "xunit.v3.core.dll"), Encoding.UTF8.GetBytes("show-live-output")); // UTF-8: not the option table
            var stamp = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "xunit.v3.core.dll"), stamp);
            Assert.IsFalse(LiveOutput.Detect(dir).XunitLiveOption);

            File.WriteAllBytes(Path.Combine(dir, "xunit.v3.mtp-v2.dll"), [1, 2, .. Encoding.Unicode.GetBytes("--show-live-output"), 3]);
            File.WriteAllBytes(Path.Combine(dir, "TUnit.Engine.dll"), [0]);
            var s = LiveOutput.Detect(dir);
            Assert.AreEqual(new LiveOutputSupport(true, true), s);
            Assert.AreSame(s, LiveOutput.Detect(dir)); // cached

            var (args, env) = LiveOutput.HostOptions(s);
            CollectionAssert.AreEqual(new[] { "--show-live-output", "on" }, args);
            Assert.AreEqual("1", env[LiveOutput.TUnitStreamingVariable]);
            var none = LiveOutput.HostOptions(LiveOutputSupport.None);
            Assert.IsEmpty(none.Args);
            Assert.IsEmpty(none.Env);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public async Task The_flush_timer_publishes_and_reschedules_while_there_is_work()
    {
        var published = new List<TestOutputEvent>();
        LiveOutput? live = null;
        live = new LiveOutput(() => { lock (published) published.AddRange(live!.Drain()); });
        live.Started(Test("A"));
        live.Partial("P", "A", "now\n", null);
        live.HostLine("P", "after the delay", false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            lock (published) if (published.Count >= 2) break;
            await Task.Delay(20, TestContext.CancellationToken);
        }
        lock (published)
        {
            Assert.AreEqual("now\n", published[0].Text);
            Assert.AreEqual("after the delay\n", published[1].Text);
        }
    }

    [TestMethod]
    [DataRow("plain\n", "plain\n")]
    [DataRow("labcoat:traceId=0af7651916cd43dd8448eb211c80319c\n", "")]
    [DataRow("before\nlabcoat:traceId=0af7651916cd43dd8448eb211c80319c\nafter\n", "before\nafter\n")]
    [DataRow("[Calc.Add] labcoat:traceId=0af7\n", "")]
    [DataRow("no newline labcoat:traceId=0af7", "")]
    public void Trace_id_marker_lines_stay_out_of_the_live_view(string chunk, string expected) =>
        Assert.AreEqual(expected, LiveOutput.WithoutTraceMarkers(chunk));

    [TestMethod]
    public void A_line_written_before_the_start_is_reported_goes_to_that_test()
    {
        var live = Create();
        live.HostLine("P", "first line", false);  // the test's first output, before MTP reports it running
        live.HostLine("Q", "other project", false);
        Advance(250);
        Assert.IsEmpty(live.Drain());           // waits for a test of P (and of Q) to start
        live.Started(Test("A"));
        Advance(100);
        var events = live.Drain();
        Assert.HasCount(1, events);
        Assert.AreEqual("A", events[0].Id);
        Assert.AreEqual("first line\n", events[0].Text);
        Assert.IsNull(events[0].Shared);
        Advance(1000);                           // Q never started a test: its line is dropped
        Assert.IsEmpty(live.Drain());
        Assert.IsFalse(live.HasWork);
    }
}
