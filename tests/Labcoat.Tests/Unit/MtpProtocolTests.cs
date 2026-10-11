using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>
/// The JSON-RPC client side of MTP server mode (MtpConnection) against a fake test host on a loopback socket:
/// framing, responses, errors, notifications, server requests, cancellation and connection loss.
/// </summary>
[TestClass]
public class MtpProtocolTests
{
    public TestContext TestContext { get; set; }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The test host end of the connection, scripted by the test.</summary>
    private sealed class FakeHost : IAsyncDisposable
    {
        private readonly TcpClient _host;
        private readonly NetworkStream _stream;
        private readonly MemoryStream _buffer = new();
        public MtpConnection Connection { get; }
        public List<(string Method, JsonNode? Params)> Notifications { get; } = [];
        public List<(string Level, string Message)> Logs { get; } = [];
        public int Kills;

        private FakeHost(TcpClient host, TcpClient labcoat)
        {
            _host = host;
            _stream = host.GetStream();
            Connection = new MtpConnection(labcoat,
                (m, p) => { lock (Notifications) Notifications.Add((m, p)); },
                (level, msg) => { lock (Logs) Logs.Add((level, msg)); },
                () => Interlocked.Increment(ref Kills));
        }

        public static async Task<FakeHost> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var labcoat = new TcpClient();
                var connect = labcoat.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var host = await listener.AcceptTcpClientAsync();
                await connect;
                return new FakeHost(host, labcoat);
            }
            finally { listener.Stop(); }
        }

        /// <summary>The next message Labcoat sent.</summary>
        public async Task<JsonObject> ReadAsync()
        {
            using var cts = new CancellationTokenSource(Timeout);
            var chunk = new byte[4096];
            while (true)
            {
                var data = _buffer.ToArray();
                var headerEnd = data.AsSpan().IndexOf("\r\n\r\n"u8);
                if (headerEnd >= 0)
                {
                    var header = Encoding.ASCII.GetString(data, 0, headerEnd);
                    var length = int.Parse(header.Split(':')[1].Trim());
                    if (data.Length - headerEnd - 4 >= length)
                    {
                        var json = Encoding.UTF8.GetString(data, headerEnd + 4, length);
                        _buffer.SetLength(0);
                        _buffer.Write(data, headerEnd + 4 + length, data.Length - headerEnd - 4 - length);
                        return JsonNode.Parse(json)!.AsObject();
                    }
                }
                var n = await _stream.ReadAsync(chunk, cts.Token);
                if (n == 0) throw new IOException("closed");
                _buffer.Write(chunk, 0, n);
            }
        }

        public static byte[] Frame(string json, bool contentType = true)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = $"Content-Length: {body.Length}\r\n" + (contentType ? "Content-Type: application/testingplatform\r\n" : "") + "\r\n";
            return [.. Encoding.ASCII.GetBytes(header), .. body];
        }

        public async Task WriteAsync(byte[] bytes)
        {
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public Task SendAsync(JsonObject message) => WriteAsync(Frame(message.ToJsonString()));

        public Task ReplyAsync(JsonObject request, JsonNode? result) =>
            SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.GetValue<int>(), ["result"] = result });

        public void Close() => _host.Dispose();

        public async ValueTask DisposeAsync()
        {
            _host.Dispose();
            await Connection.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Initialize_sends_the_client_capabilities_and_keeps_the_result()
    {
        await using var host = await FakeHost.StartAsync();
        var init = host.Connection.InitializeAsync(CancellationToken.None);
        var request = await host.ReadAsync();
        Assert.AreEqual("2.0", request["jsonrpc"]!.GetValue<string>());
        Assert.AreEqual("initialize", request["method"]!.GetValue<string>());
        Assert.AreEqual(Environment.ProcessId, request["params"]!["processId"]!.GetValue<int>());
        Assert.AreEqual("labcoat", request["params"]!["clientInfo"]!["name"]!.GetValue<string>());
        Assert.IsTrue(request["params"]!["capabilities"]!["testing"]!["attachmentsSupport"]!.GetValue<bool>());

        // The response arrives in two pieces, after a notification in the same write as its first piece.
        var response = FakeHost.Frame("""{"jsonrpc":"2.0","id":1,"result":{"capabilities":{"testing":{"supportsDiscovery":true}}}}""");
        var notification = FakeHost.Frame("""{"jsonrpc":"2.0","method":"client/log","params":{"level":"Information","message":"hi"}}""", contentType: false);
        await host.WriteAsync([.. notification, .. response.AsSpan(0, 20)]);
        await Task.Delay(50, TestContext.CancellationToken);
        await host.WriteAsync(response[20..]);

        var result = await init.WaitAsync(Timeout, TestContext.CancellationToken);
        Assert.IsTrue(result!["capabilities"]!["testing"]!["supportsDiscovery"]!.GetValue<bool>());
        Assert.AreSame(result, host.Connection.InitializeResult);
        lock (host.Notifications)
        {
            Assert.HasCount(1, host.Notifications);
            Assert.AreEqual("client/log", host.Notifications[0].Method);
            Assert.AreEqual("hi", host.Notifications[0].Params!["message"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task Large_and_back_to_back_messages_are_parsed_from_the_utf8_bytes()
    {
        await using var host = await FakeHost.StartAsync();
        // three messages in one write (the middle one ~1.5 MB, non-ASCII), then one more: the buffer is let go and reused
        var big = string.Concat(Enumerable.Repeat("ü✓-", 200_000));
        byte[] Log(string message) => FakeHost.Frame(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "client/log", ["params"] = new JsonObject { ["level"] = "Information", ["message"] = message },
        }.ToJsonString());
        await host.WriteAsync([.. Log("one"), .. Log(big), .. Log("three")]);
        await host.WriteAsync(Log("four"));
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline) { lock (host.Notifications) if (host.Notifications.Count == 4) break; await Task.Delay(20, TestContext.CancellationToken); }
        lock (host.Notifications)
            CollectionAssert.AreEqual(new[] { "one", big, "three", "four" }, host.Notifications.Select(n => n.Params!["message"]!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public void Framing_helpers_find_the_body_and_drop_consumed_bytes()
    {
        var frame = FakeHost.Frame("{}");
        Assert.IsTrue(MtpConnection.TryParseMessage(frame, out var start, out var length));
        Assert.AreEqual((frame.Length - 2, 2), (start, length));
        Assert.IsFalse(MtpConnection.TryParseMessage(frame.AsSpan(0, frame.Length - 1), out _, out _)); // body incomplete
        Assert.IsFalse(MtpConnection.TryParseMessage("Content-Length: 2\r\n"u8, out _, out _)); // header incomplete

        var data = new MemoryStream();
        data.Write([.. frame, .. "xy"u8]);
        var same = MtpConnection.Consume(data, frame.Length);
        Assert.AreSame(data, same);
        CollectionAssert.AreEqual("xy"u8.ToArray(), same.ToArray());
        Assert.AreSame(same, MtpConnection.Consume(same, 0));
        var large = new MemoryStream(2 * 1024 * 1024);
        large.Write(frame);
        var fresh = MtpConnection.Consume(large, frame.Length);
        Assert.AreNotSame(large, fresh); // an empty large buffer is not kept
        Assert.AreEqual(0, fresh.Length);
    }

    [TestMethod]
    public async Task Discover_and_run_requests_carry_the_run_id_and_the_selected_tests()
    {
        await using var host = await FakeHost.StartAsync();
        var runId = Guid.NewGuid();

        var discover = host.Connection.DiscoverAsync(runId, CancellationToken.None);
        var d = await host.ReadAsync();
        Assert.AreEqual("testing/discoverTests", d["method"]!.GetValue<string>());
        Assert.AreEqual(runId.ToString(), d["params"]!["runId"]!.GetValue<string>());
        await host.ReplyAsync(d, new JsonObject());
        Assert.IsNotNull(await discover.WaitAsync(Timeout, TestContext.CancellationToken));

        var runAll = host.Connection.RunAsync(runId, null, CancellationToken.None);
        var all = await host.ReadAsync();
        Assert.IsNull(all["params"]!["tests"]);
        await host.ReplyAsync(all, null);
        Assert.IsNull(await runAll.WaitAsync(Timeout, TestContext.CancellationToken));

        var subset = host.Connection.RunAsync(runId, [("u1", "Test (1)"), ("u2", "Other")], CancellationToken.None);
        var some = await host.ReadAsync();
        Assert.AreEqual("testing/runTests", some["method"]!.GetValue<string>());
        Assert.AreEqual("""[{"uid":"u1","display-name":"Test (1)"},{"uid":"u2","display-name":"Other"}]""", some["params"]!["tests"]!.ToJsonString());
        Assert.AreEqual(d["id"]!.GetValue<int>() + 2, some["id"]!.GetValue<int>()); // ids increase per request
        await host.ReplyAsync(some, new JsonObject { ["attachments"] = new JsonArray() });
        Assert.IsNotNull((await subset.WaitAsync(Timeout, TestContext.CancellationToken))!["attachments"]);
    }

    [TestMethod]
    public async Task Json_rpc_errors_become_exceptions_with_the_code()
    {
        await using var host = await FakeHost.StartAsync();
        var call = host.Connection.RequestAsync("testing/runTests", new JsonObject(), CancellationToken.None);
        var req = await host.ReadAsync();
        await host.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = req["id"]!.GetValue<int>(),
            ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "boom", ["data"] = new JsonObject { ["x"] = 1 } },
        });
        var e = await Assert.ThrowsExactlyAsync<MtpRpcException>(() => call.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.AreEqual(-32000, e.Code);
        Assert.AreEqual("""JSON-RPC error -32000: boom {"x":1}""", e.Message);

        // A response to an unknown id is ignored; an error without details still fails the request.
        var second = host.Connection.RequestAsync("x", null, CancellationToken.None);
        var req2 = await host.ReadAsync();
        await host.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 999, ["result"] = 1 });
        await host.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = req2["id"]!.GetValue<int>(), ["error"] = new JsonObject() });
        var e2 = await Assert.ThrowsExactlyAsync<MtpRpcException>(() => second.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.AreEqual(0, e2.Code);
        Assert.AreEqual("JSON-RPC error 0: error", e2.Message);
    }

    [TestMethod]
    public async Task Requests_from_the_test_host_are_answered_with_method_not_found()
    {
        await using var host = await FakeHost.StartAsync();
        await host.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 77, ["method"] = "client/attachDebugger", ["params"] = new JsonObject() });
        var reply = await host.ReadAsync();
        Assert.AreEqual(77, reply["id"]!.GetValue<int>());
        Assert.AreEqual(-32601, reply["error"]!["code"]!.GetValue<int>());
        Assert.Contains("client/attachDebugger", reply["error"]!["message"]!.GetValue<string>());
        lock (host.Logs) Assert.IsTrue(host.Logs.Any(l => l.Level == "info" && l.Message.Contains("not supported")));
    }

    [TestMethod]
    public async Task Exit_is_a_notification_and_bad_messages_are_logged_not_fatal()
    {
        await using var host = await FakeHost.StartAsync();
        await host.Connection.SendNotificationAsync("exit", new JsonObject());
        var exit = await host.ReadAsync();
        Assert.AreEqual("exit", exit["method"]!.GetValue<string>());
        Assert.IsNull(exit["id"]);

        await host.WriteAsync(FakeHost.Frame("{ not json"));
        await host.WriteAsync(FakeHost.Frame("[1,2]")); // not an object: ignored
        var call = host.Connection.RequestAsync("still/alive", null, CancellationToken.None);
        var req = await host.ReadAsync();
        await host.ReplyAsync(req, "yes");
        Assert.AreEqual("yes", (await call.WaitAsync(Timeout, TestContext.CancellationToken))!.GetValue<string>());
        lock (host.Logs) Assert.IsTrue(host.Logs.Any(l => l.Level == "error" && l.Message.StartsWith("Failed to handle message from test host")));
    }

    [TestMethod]
    public async Task Cancel_sends_cancel_request_and_kills_the_host_when_it_does_not_answer_in_time()
    {
        await using var host = await FakeHost.StartAsync();
        using var cts = new CancellationTokenSource();
        var call = host.Connection.RequestAsync("testing/runTests", new JsonObject(), cts.Token, cancelGrace: TimeSpan.FromMilliseconds(100));
        var req = await host.ReadAsync();
        cts.Cancel();
        var cancel = await host.ReadAsync();
        Assert.AreEqual("$/cancelRequest", cancel["method"]!.GetValue<string>());
        Assert.AreEqual(req["id"]!.GetValue<int>(), cancel["params"]!["id"]!.GetValue<int>());

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.AreEqual(1, host.Kills);
        lock (host.Logs) Assert.IsTrue(host.Logs.Any(l => l.Level == "warning" && l.Message.StartsWith("Test host did not stop within")));
    }

    [TestMethod]
    public async Task A_host_that_answers_the_cancellation_in_time_survives()
    {
        await using var host = await FakeHost.StartAsync();
        host.Connection.CancelGrace = TimeSpan.FromSeconds(5);
        Assert.AreEqual(TimeSpan.FromSeconds(5), host.Connection.CancelGrace);
        using var cts = new CancellationTokenSource();
        var call = host.Connection.RequestAsync("testing/runTests", new JsonObject(), cts.Token);
        var req = await host.ReadAsync();
        cts.Cancel();
        await host.ReadAsync(); // $/cancelRequest
        await host.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = req["id"]!.GetValue<int>(), ["error"] = new JsonObject { ["code"] = -32800, ["message"] = "" },
        });
        Assert.AreEqual(-32800, (await Assert.ThrowsExactlyAsync<MtpRpcException>(() => call.WaitAsync(Timeout, TestContext.CancellationToken))).Code);
        Assert.AreEqual(0, host.Kills);

        await Assert.ThrowsAsync<OperationCanceledException>(() => host.Connection.RequestAsync("x", null, cts.Token)); // already cancelled
    }

    [TestMethod]
    public async Task Pending_requests_fail_when_the_connection_closes()
    {
        await using var host = await FakeHost.StartAsync();
        var call = host.Connection.RequestAsync("testing/runTests", new JsonObject(), CancellationToken.None);
        await host.ReadAsync();
        host.Close();
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => call.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.AreEqual("Connection to the test host was closed.", e.Message);
        await host.Connection.SendNotificationAsync("exit", null); // writing to a closed connection is not an error
    }

    [TestMethod]
    public async Task A_frame_without_content_length_ends_the_connection()
    {
        await using var host = await FakeHost.StartAsync();
        var call = host.Connection.RequestAsync("x", null, CancellationToken.None);
        await host.ReadAsync();
        await host.WriteAsync(Encoding.ASCII.GetBytes("Content-Type: text/plain\r\n\r\n{}"));
        await Assert.ThrowsExactlyAsync<IOException>(() => call.WaitAsync(Timeout, TestContext.CancellationToken));
    }

    [TestMethod]
    public void Test_host_exit_exceptions_carry_the_exit_code_and_stderr()
    {
        var inner = new InvalidOperationException("inner");
        var e = new TestHostExitedException("exited", 3, ["a", "b"], inner);
        Assert.AreEqual(3, e.ExitCode);
        CollectionAssert.AreEqual(new[] { "a", "b" }, e.StderrTail);
        Assert.AreSame(inner, e.InnerException);
        Assert.AreEqual("JSON-RPC error 1: m", new MtpRpcException(1, "m", null).Message);
        Assert.AreEqual("…6789", MtpHost.Tail("0123456789", 4));
        Assert.AreEqual("0123", MtpHost.Tail("0123", 4));
    }
}
