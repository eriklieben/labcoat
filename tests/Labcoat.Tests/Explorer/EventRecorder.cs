using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Labcoat.Tests.ExplorerTests;

/// <summary>
/// A WebSocket client of an <see cref="EventHub"/> over an in-memory connection: records every event the explorer
/// publishes (JSON, in order) and can send commands like the UI does.
/// </summary>
internal sealed class EventRecorder : IAsyncDisposable
{
    private readonly WebSocket _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _server;
    private readonly Task _reader;
    private readonly List<JsonObject> _events = [];

    public EventRecorder(EventHub hub, Labcoat.Explorer explorer, bool lean = false)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var serverSocket = WebSocket.CreateFromStream(new Duplex(toServer.Reader.AsStream(), toClient.Writer.AsStream()), isServer: true, null, Timeout.InfiniteTimeSpan);
        _client = WebSocket.CreateFromStream(new Duplex(toClient.Reader.AsStream(), toServer.Writer.AsStream()), isServer: false, null, Timeout.InfiniteTimeSpan);
        _server = hub.HandleAsync(serverSocket, explorer.Sync, () => new StateEvent(explorer.Snapshot(includeLogs: true, lean: lean)),
            explorer.HandleCommandAsync, _cts.Token, lean);
        _reader = Task.Run(ReadAsync);
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await _client.ReceiveAsync(buffer, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()))!.AsObject();
                ms.SetLength(0);
                lock (_events) _events.Add(node);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
    }

    /// <summary>Recorded events of this type ("state", "testsUpdated", "runFinished", ...).</summary>
    public List<JsonObject> Of(string type)
    {
        lock (_events) return _events.Where(e => (string?)e["type"] == type).ToList();
    }

    /// <summary>Every recorded event, in order.</summary>
    public List<JsonObject> All()
    {
        lock (_events) return [.. _events];
    }

    public List<string> Types()
    {
        lock (_events) return _events.Select(e => (string)e["type"]!).ToList();
    }

    /// <summary>
    /// Every test update in order, from full entries (<c>testsUpdated</c>) and result patches (<c>testResults</c>):
    /// (index of the event, projectId, id, outcome; null when a patch left the outcome unchanged).
    /// </summary>
    public List<(int Index, string ProjectId, string Id, string? Outcome)> TestUpdates()
    {
        var list = new List<(int, string, string, string?)>();
        var all = All();
        for (var i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if ((string?)e["type"] == "testsUpdated")
                foreach (var t in e["tests"]!.AsArray()) list.Add((i, (string)t!["projectId"]!, (string)t["id"]!, (string?)t["outcome"]));
            else if ((string?)e["type"] == "testResults")
                foreach (var g in e["projects"]!.AsArray())
                    foreach (var p in g!["results"]!.AsArray()) list.Add((i, (string)g["projectId"]!, (string)p!["id"]!, (string?)p["outcome"]));
        }
        return list;
    }

    /// <summary>
    /// The tests as a client has them after applying every recorded event like the UI does: the last <c>state</c>
    /// snapshot (lean: <c>fileIndex</c> resolved, omitted traits = []), full entries, result patches (set / clear /
    /// historyAdd / recorded). Keyed by "projectId|id".
    /// </summary>
    public Dictionary<string, JsonObject> ClientTests()
    {
        var tests = new Dictionary<string, JsonObject>();
        static string Key(JsonNode t) => $"{(string)t["projectId"]!}|{(string)t["id"]!}";
        static JsonObject Normalize(JsonObject t, JsonArray? files)
        {
            if (t["fileIndex"] is { } fi && files is not null)
            {
                t["filePath"] = (string?)files[(int)fi];
                t.Remove("fileIndex");
            }
            t["traits"] ??= new JsonArray();
            return t;
        }
        foreach (var e in All())
        {
            switch ((string?)e["type"])
            {
                case "state":
                    tests.Clear();
                    var files = e["state"]!["files"]?.AsArray();
                    foreach (var t in e["state"]!["tests"]!.AsArray()) tests[Key(t!)] = Normalize(t!.DeepClone().AsObject(), files);
                    break;
                case "testsUpdated":
                    foreach (var t in e["tests"]!.AsArray()) tests[Key(t!)] = Normalize(t!.DeepClone().AsObject(), null);
                    break;
                case "testResults":
                    foreach (var g in e["projects"]!.AsArray())
                    {
                        foreach (var p in g!["results"]!.AsArray())
                        {
                            var k = $"{(string)g["projectId"]!}|{(string)p!["id"]!}";
                            var t = tests[k]; // a patch for an unknown test is a protocol error
                            foreach (var f in p["clear"]?.AsArray() ?? []) t.Remove((string)f!);
                            foreach (var (name, value) in p.AsObject())
                                if (name is not ("id" or "clear" or "historyAdd" or "recorded")) t[name] = value?.DeepClone();
                            JsonObject? add = p["historyAdd"]?.DeepClone().AsObject();
                            if (p["recorded"] is not null)
                            {
                                add = new JsonObject { ["outcome"] = t["outcome"]?.DeepClone() };
                                if (t["durationMs"] is { } d) add["durationMs"] = d.DeepClone();
                                add["at"] = t["lastRunAt"]?.DeepClone();
                                add["runId"] = e["runId"]?.DeepClone();
                            }
                            if (add is not null)
                            {
                                var h = t["history"]?.AsArray() ?? [];
                                var last = h.Count > 0 ? h[^1] : null;
                                if (last is null || (string?)last["at"] != (string?)add["at"] || (string?)last["runId"] != (string?)add["runId"])
                                {
                                    var list = h.Select(x => x!.DeepClone()).Append(add).ToList();
                                    t["history"] = new JsonArray([.. list.Skip(Math.Max(0, list.Count - HistoryStore.MaxRuns))]);
                                }
                            }
                        }
                    }
                    break;
            }
        }
        return tests;
    }

    public Task SendAsync(string json) =>
        _client.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    public Task WaitForAsync(string type, Func<JsonObject, bool>? match = null, int timeoutMs = 60_000) =>
        ScratchWorkspace.WaitUntilAsync(() => Of(type).Any(e => match?.Invoke(e) ?? true), $"event '{type}'", timeoutMs);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _client.Dispose();
        _cts.Dispose();
    }

    /// <summary>Reads from one pipe, writes to the other.</summary>
    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => read.ReadAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => write.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
