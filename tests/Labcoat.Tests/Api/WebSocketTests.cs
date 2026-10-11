using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Labcoat;
using Microsoft.AspNetCore.TestHost;

namespace Labcoat.Tests.Api;

/// <summary>The <c>/ws</c> protocol on the real app: snapshot on connect, lean mode, commands, rejections, size cap.</summary>
[TestClass]
public class WebSocketTests
{
    public TestContext TestContext { get; set; }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<WebSocket> Connect(ApiHost h, string query = "", string? origin = null)
    {
        var client = h.Server.CreateWebSocketClient();
        if (origin is not null) client.ConfigureRequest = r => r.Headers.Origin = origin;
        using var cts = new CancellationTokenSource(Timeout);
        return await client.ConnectAsync(new Uri("ws://localhost/ws" + query), cts.Token);
    }

    private static async Task<JsonNode?> Receive(WebSocket ws)
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

    /// <summary>The next message of <paramref name="type"/> (skipping logs and other events).</summary>
    private static async Task<JsonNode> ReceiveOf(WebSocket ws, string type)
    {
        while (true)
        {
            var msg = await Receive(ws) ?? throw new AssertFailedException($"socket closed while waiting for '{type}'");
            if (msg["type"]!.GetValue<string>() == type) return msg;
        }
    }

    private static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    [TestMethod]
    public async Task Connect_sends_the_full_snapshot_with_the_log_history()
    {
        await using var h = await ApiHost.StartAsync();
        h.Seed(h.Test("t1", Outcome.Failed, stdOut: "boom output"));
        h.Explorer.Log(null, "info", "before connect");
        using var ws = await Connect(h);
        var first = (await Receive(ws))!;
        Assert.AreEqual("state", first["type"]!.GetValue<string>());
        var state = first["state"]!;
        Assert.IsNull(state["lean"]);
        Assert.AreEqual("boom output", state["tests"]![0]!["stdOut"]!.GetValue<string>());
        Assert.IsTrue(state["logs"]!.AsArray().Any(l => l!["message"]!.GetValue<string>() == "before connect"));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [TestMethod]
    public async Task Lean_clients_get_a_lean_snapshot_and_settings_changed_instead_of_state()
    {
        await using var h = await ApiHost.StartAsync(withProject: false);
        using var legacy = await Connect(h);
        using var lean = await Connect(h, "?lean=1");
        using var leanToo = await Connect(h, "?lean=true");
        Assert.IsNull((await ReceiveOf(legacy, "state"))["state"]!["lean"]);
        Assert.IsTrue((await ReceiveOf(lean, "state"))["state"]!["lean"]!.GetValue<bool>());
        Assert.IsTrue((await ReceiveOf(leanToo, "state"))["state"]!["lean"]!.GetValue<bool>());

        await Send(lean, """{"type":"setCoverage","enabled":true}""");
        var full = await ReceiveOf(legacy, "state");
        Assert.IsTrue(full["state"]!["coverageEnabled"]!.GetValue<bool>());
        var settings = await ReceiveOf(lean, "settingsChanged");
        Assert.IsTrue(settings["settings"]!["coverageEnabled"]!.GetValue<bool>());
        Assert.IsFalse(settings["settings"]!["watch"]!.GetValue<bool>());
        Assert.IsTrue(h.Explorer.Snapshot().CoverageEnabled);
    }

    [TestMethod]
    public async Task Commands_are_executed_and_invalid_ones_rejected_to_the_sender_only()
    {
        await using var h = await ApiHost.StartAsync();
        using var ws = await Connect(h);
        await ReceiveOf(ws, "state");

        await Send(ws, """{"type":"selfDestruct"}""");
        var rejected = await ReceiveOf(ws, "commandRejected");
        Assert.AreEqual("selfDestruct", rejected["command"]!.GetValue<string>());
        Assert.AreEqual(400, rejected["status"]!.GetValue<int>());
        Assert.Contains("Unknown command 'selfDestruct'", rejected["error"]!.GetValue<string>());

        await Send(ws, "{ not json");
        rejected = await ReceiveOf(ws, "commandRejected");
        Assert.IsNull(rejected["command"]);
        Assert.StartsWith("Malformed command: ", rejected["error"]!.GetValue<string>());

        await Send(ws, "null");
        Assert.Contains("empty message", (await ReceiveOf(ws, "commandRejected"))["error"]!.GetValue<string>());

        await Send(ws, """{"type":"run","testIds":["nope"]}""");
        rejected = await ReceiveOf(ws, "commandRejected");
        Assert.AreEqual(404, rejected["status"]!.GetValue<int>());
        Assert.AreEqual("nope", rejected["unknownTestIds"]![0]!.GetValue<string>());

        await Send(ws, """{"type":"setQuarantine","projectId":"A.Tests","id":"t9"}""");
        var state = await ReceiveOf(ws, "state");
        CollectionAssert.AreEqual(new[] { "A.Tests|t9" }, state["state"]!["quarantined"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task Messages_over_one_megabyte_close_the_socket_with_1009()
    {
        await using var h = await ApiHost.StartAsync(withProject: false);
        using var ws = await Connect(h);
        await ReceiveOf(ws, "state");
        var chunk = new byte[256 * 1024];
        Array.Fill(chunk, (byte)'x');
        for (var i = 0; i < 5; i++) // 1.25 MB in one message (not finished)
            await ws.SendAsync(chunk, WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);

        while (await Receive(ws) is not null) { }
        Assert.AreEqual(WebSocketCloseStatus.MessageTooBig, ws.CloseStatus);
        Assert.Contains("1024 KB", ws.CloseStatusDescription!);
    }

    [TestMethod]
    public async Task Plain_http_requests_to_the_socket_endpoint_are_400_and_cross_origin_sockets_403()
    {
        await using var h = await ApiHost.StartAsync(withProject: false);
        using (var res = await h.Client.GetAsync("/ws", TestContext.CancellationToken)) Assert.AreEqual(HttpStatusCode.BadRequest, res.StatusCode);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Connect(h, origin: "http://evil.example"));
        Assert.Contains("403", ex.Message);
        using var sameOrigin = await Connect(h, origin: "http://localhost");
        Assert.AreEqual("state", (await Receive(sameOrigin))!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Open_sockets_do_not_hold_up_shutdown()
    {
        var h = await ApiHost.StartAsync(withProject: false);
        using var ws = await Connect(h);
        await ReceiveOf(ws, "state");
        var stop = h.DisposeAsync().AsTask();
        Assert.AreSame(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10), TestContext.CancellationToken)), "shutdown waited for the socket");
        await stop;
    }
}
