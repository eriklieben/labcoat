using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Labcoat;

/// <summary>
/// Fan-out of ServerEvents to all connected WebSocket clients. Clients connect either in the default ("legacy") mode or
/// lean mode (<c>/ws?lean=1</c>), which gets smaller variants of some events (see <see cref="Publish(ServerEvent?, Func{ServerEvent?}?)"/>).
/// </summary>
public sealed class EventHub
{
    /// <summary>Largest client message accepted; bigger ones close the socket with 1009 (message too big).</summary>
    public const int MaxMessageBytes = 1024 * 1024;
    /// <summary>Outgoing messages buffered per client; a client that falls this far behind is disconnected (it reconnects and resyncs).</summary>
    public const int MaxQueuedMessages = 10_000;

    private sealed class Client(Channel<PooledMessage> channel, bool lean)
    {
        public Channel<PooledMessage> Channel { get; } = channel;
        public bool Lean { get; } = lean;
        public readonly CancellationTokenSource Overflow = new();
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    /// <summary>Every published log line (the host log file and the diagnostics report, HostLog.cs), also without clients.</summary>
    public event Action<LogEvent>? Logged;

    public void Publish(ServerEvent evt) => Publish(evt, null);

    /// <summary>
    /// Sends <paramref name="legacy"/> to default clients and the result of <paramref name="lean"/> to lean clients
    /// (<paramref name="lean"/> null = the same event for everybody; a factory returning null = nothing for lean clients;
    /// <paramref name="legacy"/> null = nothing for default clients). Each variant is serialized once, to pooled UTF-8
    /// segments that all clients share (<see cref="PooledMessage"/>).
    /// </summary>
    public void Publish(ServerEvent? legacy, Func<ServerEvent?>? lean)
    {
        if (legacy is LogEvent log) Logged?.Invoke(log);
        var variants = new EventVariants(legacy, lean);
        try
        {
            foreach (var c in _clients.Values)
                if (variants.For(c) is { } msg) Enqueue(c, msg);
        }
        finally
        {
            // the publisher's own reference: the segments go back to the pool once every client sent (or dropped) them
            variants.Release();
        }
    }

    /// <summary>The serialized default and lean variants of one event, each serialized once (on first use).</summary>
    private sealed class EventVariants(ServerEvent? legacy, Func<ServerEvent?>? lean)
    {
        private PooledMessage? _legacy;
        private PooledMessage? _lean;
        private bool _leanDone;

        /// <summary>The variant for <paramref name="c"/>; null = nothing for it.</summary>
        public PooledMessage? For(Client c)
        {
            if (c.Lean && lean is not null)
            {
                if (_leanDone) return _lean;
                _leanDone = true;
                var e = lean();
                _lean = e is null ? null : PooledMessage.Serialize(e);
                return _lean;
            }
            if (legacy is null) return null;
            return _legacy ??= PooledMessage.Serialize(legacy);
        }

        public void Release()
        {
            _legacy?.Release();
            _lean?.Release();
        }
    }

    private static void Enqueue(Client c, PooledMessage msg)
    {
        msg.AddRef(); // the client's reference, released by its sender (or below)
        if (c.Channel.Writer.TryWrite(msg)) return;
        msg.Release();
        // too far behind: disconnect, the client resyncs (it may be disconnecting right now: Overflow disposed)
        try { c.Overflow.Cancel(); }
        catch (ObjectDisposedException) { /* the client is disconnecting already */ }
    }

    public int ClientCount => _clients.Count;

    /// <summary>Runs a client connection until it closes. <paramref name="initial"/> is sent first.</summary>
    /// <remarks>
    /// The snapshot is taken and the client registered while holding <paramref name="syncRoot"/>; publishers must hold the
    /// same lock while publishing so no event can slip between the snapshot and the subscription.
    /// <paramref name="onCommand"/> may return an event that is sent to this client only (e.g. <see cref="CommandRejectedEvent"/>).
    /// </remarks>
    public async Task HandleAsync(WebSocket socket, object syncRoot, Func<ServerEvent> initial, Func<ClientCommand, Task<ServerEvent?>> onCommand,
        CancellationToken ct, bool lean = false)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<PooledMessage>(new BoundedChannelOptions(MaxQueuedMessages) { FullMode = BoundedChannelFullMode.Wait });
        var client = new Client(channel, lean);
        lock (syncRoot)
        {
            channel.Writer.TryWrite(PooledMessage.Serialize(initial()));
            _clients[id] = client;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, client.Overflow.Token);
        // The sender stops through cts.Token (passed to it); the task itself is always started and awaited below.
        var sender = Task.Run(() => SendAllAsync(channel.Reader, socket, cts.Token), CancellationToken.None);

        var close = (Status: WebSocketCloseStatus.NormalClosure, Reason: (string?)null);
        try
        {
            close = await ReceiveCommandsAsync(socket, channel.Writer, onCommand, cts.Token);
            if (client.Overflow.IsCancellationRequested) close = FellBehind;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            if (client.Overflow.IsCancellationRequested) close = FellBehind;
        }
        finally
        {
            _clients.TryRemove(id, out _);
            channel.Writer.TryComplete();
            await cts.CancelAsync();
            await sender;
            // messages queued but never sent (the connection ended first): hand their segments back
            while (channel.Reader.TryRead(out var unsent)) unsent.Release();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await CloseQuietlyAsync(socket, close.Status, close.Reason);
            client.Overflow.Dispose();
        }
    }

    private static readonly (WebSocketCloseStatus Status, string? Reason) FellBehind =
        (WebSocketCloseStatus.PolicyViolation, "Client fell behind; reconnect to resync.");

    private static async Task SendAllAsync(ChannelReader<PooledMessage> reader, WebSocket socket, CancellationToken token)
    {
        try
        {
            await foreach (var msg in reader.ReadAllAsync(token))
            {
                try { await msg.SendAsync(socket, token); }
                finally { msg.Release(); }
            }
        }
        catch { /* socket closed */ }
    }

    /// <summary>Reads commands until the socket closes; the close status (MessageTooBig for an oversized message).</summary>
    private static async Task<(WebSocketCloseStatus Status, string? Reason)> ReceiveCommandsAsync(WebSocket socket, ChannelWriter<PooledMessage> writer,
        Func<ClientCommand, Task<ServerEvent?>> onCommand, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var ms = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var r = await socket.ReceiveAsync(buffer, token);
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (ms.Length + r.Count > MaxMessageBytes)
                return (WebSocketCloseStatus.MessageTooBig, $"Messages are limited to {MaxMessageBytes / 1024} KB.");
            await ms.WriteAsync(buffer.AsMemory(0, r.Count), CancellationToken.None); // an in-memory copy: completes at once
            if (!r.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            ms.SetLength(0);
            await HandleCommandTextAsync(text, writer, onCommand);
        }
        return (WebSocketCloseStatus.NormalClosure, null);
    }

    /// <summary>One command message; a reply (e.g. a rejection) goes to this client only.</summary>
    private static async Task HandleCommandTextAsync(string text, ChannelWriter<PooledMessage> writer, Func<ClientCommand, Task<ServerEvent?>> onCommand)
    {
        ClientCommand? cmd = null;
        string? parseError = null;
        try { cmd = JsonSerializer.Deserialize<ClientCommand>(text, Json.Options); }
        catch (JsonException ex) { parseError = ex.Message; }
        ServerEvent? reply = cmd is not null
            ? await onCommand(cmd)
            : new CommandRejectedEvent(null, 400, "Malformed command: " + (parseError ?? "empty message"));
        if (reply is null) return;
        var msg = PooledMessage.Serialize(reply);
        if (!writer.TryWrite(msg)) msg.Release();
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string? reason)
    {
        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(status, reason, closeCts.Token);
        }
        catch { /* best effort: the socket may be gone already */ }
    }
}
