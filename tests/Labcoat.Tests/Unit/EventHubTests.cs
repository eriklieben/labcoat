using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>EventHub over real WebSockets (two ends of a loopback TCP connection, no HTTP handshake).</summary>
[TestClass]
public class EventHubTests
{
    public TestContext TestContext { get; set; }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class Pair : IAsyncDisposable
    {
        private readonly TcpClient _serverTcp, _clientTcp;
        public WebSocket Server { get; }
        public WebSocket Client { get; }

        private Pair(TcpClient serverTcp, TcpClient clientTcp)
        {
            _serverTcp = serverTcp;
            _clientTcp = clientTcp;
            Server = WebSocket.CreateFromStream(serverTcp.GetStream(), new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
            Client = WebSocket.CreateFromStream(clientTcp.GetStream(), new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
        }

        public static async Task<Pair> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = await listener.AcceptTcpClientAsync();
                await connect;
                return new Pair(server, client);
            }
            finally { listener.Stop(); }
        }

        public async ValueTask DisposeAsync()
        {
            Server.Abort();
            Client.Abort();
            Server.Dispose();
            Client.Dispose();
            _serverTcp.Dispose();
            _clientTcp.Dispose();
            await Task.CompletedTask;
        }
    }

    /// <summary>The next text message (null when the server closed the socket).</summary>
    private static async Task<JsonNode?> ReceiveAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    /// <summary>True when the connection ends (close frame or aborted) instead of delivering another message.</summary>
    private static async Task<bool> EndsAsync(WebSocket ws)
    {
        try { return await ReceiveAsync(ws) is null; }
        catch (WebSocketException) { return true; }
    }

    private static Task SendAsync(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static LogEvent Log(string message) => new(null, "info", message, "t");

    private static Task Handle(EventHub hub, WebSocket socket, CancellationToken ct, bool lean = false,
        Func<ClientCommand, Task<ServerEvent?>>? onCommand = null, string initial = "hello") =>
        hub.HandleAsync(socket, new object(), () => Log(initial), onCommand ?? (_ => Task.FromResult<ServerEvent?>(null)), ct, lean);

    [TestMethod]
    public async Task Sends_the_initial_event_first_then_published_events()
    {
        var hub = new EventHub();
        await using var p = await Pair.CreateAsync();
        using var cts = new CancellationTokenSource();
        var handler = Handle(hub, p.Server, cts.Token);
        Assert.AreEqual(1, hub.ClientCount); // registered synchronously, with the snapshot

        hub.Publish(Log("second"));
        var first = await ReceiveAsync(p.Client);
        Assert.AreEqual("log", first!["type"]!.GetValue<string>());
        Assert.AreEqual("hello", first["message"]!.GetValue<string>());
        Assert.AreEqual("second", (await ReceiveAsync(p.Client))!["message"]!.GetValue<string>());

        cts.Cancel(); // app shutdown: the connection ends (a cancelled receive aborts the server socket)
        await handler.WaitAsync(Timeout, TestContext.CancellationToken);
        Assert.AreEqual(0, hub.ClientCount);
        Assert.IsTrue(await EndsAsync(p.Client));
    }

    [TestMethod]
    public async Task Lean_clients_get_the_lean_variant_and_nothing_when_it_is_null()
    {
        var hub = new EventHub();
        await using var legacy = await Pair.CreateAsync();
        await using var lean = await Pair.CreateAsync();
        using var cts = new CancellationTokenSource();
        var h1 = Handle(hub, legacy.Server, cts.Token);
        var h2 = Handle(hub, lean.Server, cts.Token, lean: true);
        await ReceiveAsync(legacy.Client);
        await ReceiveAsync(lean.Client);

        var leanCalls = 0;
        hub.Publish(Log("full"), () => { leanCalls++; return Log("small"); });
        hub.Publish(Log("legacy only"), () => null);
        hub.Publish(null, () => Log("lean only"));
        hub.Publish(Log("everybody"));

        Assert.AreEqual(1, leanCalls); // the lean variant is created once per publish
        Assert.AreEqual("full", (await ReceiveAsync(legacy.Client))!["message"]!.GetValue<string>());
        Assert.AreEqual("legacy only", (await ReceiveAsync(legacy.Client))!["message"]!.GetValue<string>());
        Assert.AreEqual("everybody", (await ReceiveAsync(legacy.Client))!["message"]!.GetValue<string>());
        Assert.AreEqual("small", (await ReceiveAsync(lean.Client))!["message"]!.GetValue<string>());
        Assert.AreEqual("lean only", (await ReceiveAsync(lean.Client))!["message"]!.GetValue<string>());
        Assert.AreEqual("everybody", (await ReceiveAsync(lean.Client))!["message"]!.GetValue<string>());

        cts.Cancel();
        await Task.WhenAll(h1, h2).WaitAsync(Timeout, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Command_replies_go_to_the_sender_only_and_malformed_commands_are_rejected()
    {
        var hub = new EventHub();
        await using var a = await Pair.CreateAsync();
        await using var b = await Pair.CreateAsync();
        using var cts = new CancellationTokenSource();
        var received = new List<ClientCommand>();
        Task<ServerEvent?> OnCommand(ClientCommand c)
        {
            lock (received) received.Add(c);
            return Task.FromResult<ServerEvent?>(c.Type == "bad" ? new CommandRejectedEvent(c.Type, 404, "nope") : null);
        }
        var h1 = Handle(hub, a.Server, cts.Token, onCommand: OnCommand);
        var h2 = Handle(hub, b.Server, cts.Token, onCommand: OnCommand);
        await ReceiveAsync(a.Client);
        await ReceiveAsync(b.Client);

        await SendAsync(a.Client, """{"type":"run","projectId":"P","testIds":["x"]}""");
        await SendAsync(a.Client, """{"type":"bad"}""");
        var rejected = await ReceiveAsync(a.Client);
        Assert.AreEqual("commandRejected", rejected!["type"]!.GetValue<string>());
        Assert.AreEqual(404, rejected["status"]!.GetValue<int>());

        await SendAsync(a.Client, "{ not json");
        var malformed = await ReceiveAsync(a.Client);
        Assert.AreEqual(400, malformed!["status"]!.GetValue<int>());
        Assert.StartsWith("Malformed command: ", malformed["error"]!.GetValue<string>());
        await SendAsync(a.Client, "null");
        Assert.AreEqual("Malformed command: empty message", (await ReceiveAsync(a.Client))!["error"]!.GetValue<string>());

        // b saw none of a's replies: its next message is the next broadcast
        hub.Publish(Log("marker"));
        Assert.AreEqual("marker", (await ReceiveAsync(b.Client))!["message"]!.GetValue<string>());
        lock (received)
        {
            Assert.HasCount(2, received);
            Assert.AreEqual("run", received[0].Type);
            CollectionAssert.AreEqual(new[] { "x" }, received[0].TestIds);
        }

        cts.Cancel();
        await Task.WhenAll(h1, h2).WaitAsync(Timeout, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Messages_split_into_frames_are_reassembled()
    {
        var hub = new EventHub();
        await using var p = await Pair.CreateAsync();
        using var cts = new CancellationTokenSource();
        var got = new TaskCompletionSource<ClientCommand>();
        var handler = Handle(hub, p.Server, cts.Token, onCommand: c => { got.TrySetResult(c); return Task.FromResult<ServerEvent?>(null); });
        await ReceiveAsync(p.Client);

        var json = Encoding.UTF8.GetBytes("""{"type":"setWatch","enabled":true}""");
        await p.Client.SendAsync(json.AsMemory(0, 10), WebSocketMessageType.Text, false, CancellationToken.None);
        await p.Client.SendAsync(json.AsMemory(10), WebSocketMessageType.Text, true, CancellationToken.None);
        var cmd = await got.Task.WaitAsync(Timeout, TestContext.CancellationToken);
        Assert.AreEqual(("setWatch", true), (cmd.Type, cmd.Enabled));

        cts.Cancel();
        await handler.WaitAsync(Timeout, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Oversized_messages_close_the_socket_with_1009()
    {
        var hub = new EventHub();
        await using var p = await Pair.CreateAsync();
        var handler = Handle(hub, p.Server, CancellationToken.None);
        await ReceiveAsync(p.Client);

        var chunk = new byte[256 * 1024];
        chunk.AsSpan().Fill((byte)' ');
        try
        {
            for (var sent = 0; sent <= EventHub.MaxMessageBytes; sent += chunk.Length)
                await p.Client.SendAsync(chunk, WebSocketMessageType.Text, false, CancellationToken.None);
        }
        catch (WebSocketException) { } // the server may already be gone

        Assert.IsNull(await ReceiveAsync(p.Client));
        Assert.AreEqual(WebSocketCloseStatus.MessageTooBig, p.Client.CloseStatus);
        Assert.Contains("1024 KB", p.Client.CloseStatusDescription!);
        await p.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await handler.WaitAsync(Timeout, TestContext.CancellationToken);
        Assert.AreEqual(0, hub.ClientCount);
    }

    [TestMethod]
    public async Task Client_close_ends_the_connection()
    {
        var hub = new EventHub();
        await using var p = await Pair.CreateAsync();
        var handler = Handle(hub, p.Server, CancellationToken.None);
        await ReceiveAsync(p.Client);
        await p.Client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(Timeout).Token);
        await handler.WaitAsync(Timeout, TestContext.CancellationToken);
        Assert.AreEqual(0, hub.ClientCount);
        Assert.AreEqual(WebSocketState.Closed, p.Client.State);
    }

    [TestMethod]
    public async Task A_client_that_falls_behind_is_disconnected()
    {
        var hub = new EventHub();
        await using var p = await Pair.CreateAsync();
        var handler = Handle(hub, p.Server, CancellationToken.None);
        // The client never reads: the socket buffers fill up, then the per-client queue (MaxQueuedMessages).
        var payload = Log(new string('x', 2048));
        for (var i = 0; i < EventHub.MaxQueuedMessages * 2 && hub.ClientCount > 0 && !handler.IsCompleted; i++)
            hub.Publish(payload);

        await handler.WaitAsync(Timeout, TestContext.CancellationToken);
        Assert.AreEqual(0, hub.ClientCount);
        hub.Publish(Log("nobody listens")); // no clients: a no-op
    }

    [TestMethod]
    public async Task Large_events_are_serialized_once_and_arrive_whole_at_every_client()
    {
        var hub = new EventHub();
        await using var a = await Pair.CreateAsync();
        await using var b = await Pair.CreateAsync();
        await using var lean = await Pair.CreateAsync();
        using var cts = new CancellationTokenSource();
        var handlers = new[] { Handle(hub, a.Server, cts.Token), Handle(hub, b.Server, cts.Token), Handle(hub, lean.Server, cts.Token, lean: true) };
        foreach (var p in new[] { a, b, lean }) await ReceiveAsync(p.Client);

        // ~1 MB: many pooled segments, sent as one message of several frames
        var big = new string('x', 1024 * 1024);
        hub.Publish(Log(big), () => Log("lean " + big.Length));
        hub.Publish(Log("after"));
        foreach (var p in new[] { a, b })
        {
            Assert.AreEqual(big, (await ReceiveAsync(p.Client))!["message"]!.GetValue<string>());
            Assert.AreEqual("after", (await ReceiveAsync(p.Client))!["message"]!.GetValue<string>());
        }
        Assert.AreEqual("lean 1048576", (await ReceiveAsync(lean.Client))!["message"]!.GetValue<string>());
        Assert.AreEqual("after", (await ReceiveAsync(lean.Client))!["message"]!.GetValue<string>());

        cts.Cancel();
        await Task.WhenAll(handlers).WaitAsync(Timeout, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Publishing_while_clients_come_and_go_is_safe()
    {
        // Clients disconnect (their Overflow token is disposed, queued messages are released) while another thread
        // publishes shared messages to them: no exception may escape Publish.
        var hub = new EventHub();
        using var stop = new CancellationTokenSource();
        var payload = Log(new string('y', 2_000));
        var publisher = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                hub.Publish(payload, () => n % 2 == 0 ? Log("lean") : null);
                n++;
                Thread.Yield();
            }
            return n;
        });
        for (var i = 0; i < 20; i++)
        {
            await using var p = await Pair.CreateAsync();
            using var cts = new CancellationTokenSource();
            var handler = Handle(hub, p.Server, cts.Token, lean: i % 2 == 0);
            await ReceiveAsync(p.Client);
            await ReceiveAsync(p.Client);
            if (i % 3 == 0) cts.Cancel(); // server side shutdown
            else await p.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            await handler.WaitAsync(Timeout, TestContext.CancellationToken);
        }
        stop.Cancel();
        Assert.IsGreaterThan(0, await publisher.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.AreEqual(0, hub.ClientCount);
    }
}
