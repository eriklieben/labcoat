using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>PooledMessage: events serialized once to pooled UTF-8 segments and shared by the WebSocket clients.</summary>
[TestClass]
public class PooledMessageTests
{
    public TestContext TestContext { get; set; }

    private static TestNode Node(int i, string? output = null) => new()
    {
        Id = $"id-{i}", ProjectId = "P", DisplayName = $"Test \"{i}\" <ü> + 🧪", Outcome = Outcome.Passed, DurationMs = i / 7.0,
        LastRunAt = "2026-10-09T08:00:00.1234567+00:00", StdOut = output,
    };

    [TestMethod]
    public void Small_events_are_byte_for_byte_what_the_serializer_writes()
    {
        ServerEvent evt = new LogEvent("P", "info", "hello \"world\" + ü", "2026-10-09T08:00:00+00:00");
        var m = PooledMessage.Serialize(evt);
        try
        {
            CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(evt, Json.Options), m.ToArray());
            Assert.AreEqual(1, m.SegmentCount);
            Assert.StartsWith("""{"type":"log",""", m.ToString());
            Assert.Contains("""hello \"world\" + ü""", m.ToString()); // minimal escaping (Json.Options)
        }
        finally { m.Release(); }
    }

    [TestMethod]
    public void Large_events_span_many_segments_below_the_large_object_heap_threshold()
    {
        // 3,000 tests, one with a console output bigger than a segment (a single JSON token > SegmentSize)
        var tests = Enumerable.Range(0, 3000).Select(i => Node(i, i == 10 ? new string('x', 3 * PooledMessage.SegmentSize) : null)).ToList();
        ServerEvent evt = new TestsUpdatedEvent(tests);
        var m = PooledMessage.Serialize(evt);
        try
        {
            var expected = JsonSerializer.SerializeToUtf8Bytes(evt, Json.Options);
            Assert.AreEqual(expected.Length, m.Length);
            CollectionAssert.AreEqual(expected, m.ToArray());
        }
        finally { m.Release(); }

        // without a huge token every segment is SegmentSize bytes (well below the large object heap threshold)
        ServerEvent plain = new TestsUpdatedEvent(tests.Select(t => t with { StdOut = null }).ToList());
        var p = PooledMessage.Serialize(plain);
        try
        {
            CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(plain, Json.Options), p.ToArray());
            Assert.IsGreaterThanOrEqualTo(p.Length / PooledMessage.SegmentSize, (long)p.SegmentCount);
            Assert.IsLessThanOrEqualTo(p.Length / (PooledMessage.SegmentSize / 2) + 1, (long)p.SegmentCount);
        }
        finally { p.Release(); }
    }

    [TestMethod]
    public void Reference_counting_guards_against_use_after_release()
    {
        var m = PooledMessage.Serialize<ServerEvent>(new LogEvent(null, "info", "x", "t"));
        m.AddRef();
        m.Release(); // the second holder
        Assert.AreNotEqual(0, m.Length);
        m.Release(); // the creator: segments go back to the pool
        Assert.AreEqual(0, m.SegmentCount);
        Assert.ThrowsExactly<InvalidOperationException>(m.Release);
        Assert.ThrowsExactly<InvalidOperationException>(m.AddRef);
        Assert.ThrowsExactly<InvalidOperationException>(() => m.GetSpan()); // sealed
    }

    [TestMethod]
    public void The_buffer_writer_contract_is_enforced()
    {
        var m = new PooledMessage();
        Assert.ThrowsExactly<InvalidOperationException>(() => m.Advance(1)); // nothing requested yet
        var length = m.GetSpan(10).Length;
        Assert.IsGreaterThanOrEqualTo(PooledMessage.SegmentSize, length);
        Assert.ThrowsExactly<InvalidOperationException>(() => m.Advance(length + 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => m.Advance(-1));
        "abc"u8.CopyTo(m.GetMemory().Span);
        m.Advance(3);
        // a request bigger than what is left of the segment starts a new (bigger) one
        var big = m.GetMemory(PooledMessage.SegmentSize * 2);
        Assert.IsGreaterThanOrEqualTo(PooledMessage.SegmentSize * 2, big.Length);
        "de"u8.CopyTo(big.Span);
        m.Advance(2);
        Assert.AreEqual(5, m.Length);
        m.GetSpan(); // rented but unused: dropped when the next segment starts / the message is released
        m.Release();
    }

    [TestMethod]
    public void Serialization_errors_release_the_rented_segments()
    {
        // a cycle: the serializer throws after writing part of the payload
        Assert.ThrowsExactly<JsonException>(() => PooledMessage.Serialize(new Loop()));
    }

    private sealed class Loop
    {
        public string Padding { get; } = new('p', 1000);
        public Loop Self => this;
    }

    [TestMethod]
    public async Task Sends_one_text_message_made_of_one_frame_per_segment()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientTcp = new TcpClient();
        var connect = clientTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.CancellationToken);
        using var serverTcp = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
        await connect;
        listener.Stop();
        using var server = WebSocket.CreateFromStream(serverTcp.GetStream(), new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        using var client = WebSocket.CreateFromStream(clientTcp.GetStream(), new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });

        ServerEvent evt = new TestsUpdatedEvent(Enumerable.Range(0, 2000).Select(i => Node(i)).ToList());
        var m = PooledMessage.Serialize(evt);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var send = m.SendAsync(server, cts.Token);
        var buffer = new byte[16 * 1024];
        var received = new List<byte>();
        var frames = 0;
        while (true)
        {
            var r = await client.ReceiveAsync(buffer, cts.Token);
            Assert.AreEqual(WebSocketMessageType.Text, r.MessageType);
            received.AddRange(buffer.AsSpan(0, r.Count));
            frames++;
            if (r.EndOfMessage) break;
        }
        await send;
        m.Release();
        Assert.AreEqual(JsonSerializer.Serialize(evt, Json.Options), Encoding.UTF8.GetString(received.ToArray()));
        Assert.IsGreaterThan(1, frames);
    }

    [TestMethod]
    public void The_json_options_escape_minimally()
    {
        var json = JsonSerializer.Serialize(Node(1), Json.Options);
        Assert.Contains("\"lastRunAt\":\"2026-10-09T08:00:00.1234567+00:00\"", json);
        Assert.Contains("Test \\\"1\\\" <ü> + ", json); // only quotes, control and non-BMP characters are escaped
        Assert.AreEqual("Test \"1\" <ü> + 🧪", JsonNode.Parse(json)!["displayName"]!.GetValue<string>()); // same value
        Assert.AreEqual("\"a\\u0001\\n\\\\\"", JsonSerializer.Serialize("a\u0001\n\\", Json.Options)); // control characters stay escaped
    }

    [TestMethod]
    public void The_host_uses_workstation_gc()
    {
        // Labcoat.runtimeconfig.json (copied next to the tests) is what every launcher uses: the tool, the Aspire
        // integration and Labcoat.TestingPlatform all start the published Labcoat.dll.
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Labcoat.runtimeconfig.json")))!;
        var props = config["runtimeOptions"]!["configProperties"]!;
        Assert.IsFalse(props["System.GC.Server"]!.GetValue<bool>());
        Assert.IsTrue(props["System.GC.Concurrent"]!.GetValue<bool>());
    }
}
