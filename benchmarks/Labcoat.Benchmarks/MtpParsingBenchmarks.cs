using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Labcoat.Benchmarks.Synthetic;

namespace Labcoat.Benchmarks;

/// <summary>
/// The test host → Labcoat direction of MTP's JSON-RPC server mode (MtpClient.cs): the <c>Content-Length</c> framing
/// of the read loop (<see cref="MtpConnection.TryParseMessage"/> + <see cref="MtpConnection.Consume"/> over 64 KB socket
/// reads), <see cref="JsonNode.Parse(ReadOnlySpan{byte}, JsonNodeOptions?, JsonDocumentOptions)"/> straight from the
/// bytes, and the mapping of every changed node onto a <see cref="TestNode"/> (<see cref="MtpMapping.ToTestNode"/>).
/// Input: a whole run's <c>testing/testUpdates/tests</c> notifications (an in-progress and a final update per test,
/// 100 changes per message, like MSTest's batching), or one message carrying every node (the large-buffer path).
/// </summary>
[MemoryDiagnoser]
public class MtpParsingBenchmarks
{
    private const int ReadSize = 64 * 1024; // MtpConnection's socket read buffer
    private const int ChangesPerMessage = 100;

    [Params(1_000, 20_000)]
    public int Tests { get; set; }

    private byte[] _stream = [];
    private byte[] _single = [];
    private string _projectId = "";

    [GlobalSetup]
    public void Setup()
    {
        var suite = SyntheticSuite.Create(Tests, "/repo", historyRuns: 0);
        _projectId = suite.Projects[0].Id;
        var runId = Guid.NewGuid().ToString();
        using var all = new MemoryStream();
        var changes = new List<JsonObject>();
        void FlushBatch()
        {
            if (changes.Count == 0) return;
            WriteMessage(all, Notification(runId, changes));
            changes.Clear();
        }
        foreach (var t in suite.Tests)
        {
            changes.Add(Change(t, "in-progress"));
            if (changes.Count == ChangesPerMessage) FlushBatch();
        }
        FlushBatch();
        foreach (var t in suite.Tests)
        {
            changes.Add(Change(t, t.Outcome switch
            {
                Outcome.Failed => "failed", Outcome.Skipped => "skipped", Outcome.TimedOut => "timed-out", _ => "passed",
            }));
            if (changes.Count == ChangesPerMessage) FlushBatch();
        }
        FlushBatch();
        _stream = all.ToArray();

        using var one = new MemoryStream();
        WriteMessage(one, Notification(runId, suite.Tests.Select(t => Change(t, "discovered")).ToList()));
        _single = one.ToArray();
    }

    /// <summary>Framing only: split the byte stream into message bodies.</summary>
    [Benchmark(Baseline = true)]
    public int Frame() => ReadLoop(_stream, parse: false, map: false);

    /// <summary>Framing + <c>JsonNode.Parse</c> of every body.</summary>
    [Benchmark]
    public int FrameAndParse() => ReadLoop(_stream, parse: true, map: false);

    /// <summary>Framing + parse + <see cref="MtpMapping.ToTestNode"/> / <see cref="MtpMapping.MapState"/> per change.</summary>
    [Benchmark]
    public int FrameParseAndMap() => ReadLoop(_stream, parse: true, map: true);

    /// <summary>Every node in one message (discovery of a large project): the buffer grows to the message size.</summary>
    [Benchmark]
    public int SingleLargeMessage() => ReadLoop(_single, parse: true, map: true);

    /// <summary>The loop of <c>MtpConnection.ReadLoopAsync</c>, reading from memory instead of the socket.</summary>
    private int ReadLoop(byte[] input, bool parse, bool map)
    {
        var data = new MemoryStream();
        var nodes = 0;
        for (var offset = 0; offset < input.Length; offset += ReadSize)
        {
            data.Write(input, offset, Math.Min(ReadSize, input.Length - offset));
            var consumed = 0;
            while (MtpConnection.TryParseMessage(data.GetBuffer().AsSpan(consumed, (int)data.Length - consumed), out var bodyStart, out var bodyLength))
            {
                if (parse)
                {
                    var msg = JsonNode.Parse(data.GetBuffer().AsSpan(consumed + bodyStart, bodyLength));
                    if (map && msg?["params"]?["changes"] is JsonArray changes)
                    {
                        foreach (var c in changes)
                        {
                            if (c?["node"] is not JsonObject node) continue;
                            _ = MtpMapping.MapState(node["execution-state"]?.GetValue<string>());
                            _ = MtpMapping.ToTestNode(_projectId, node);
                            nodes++;
                        }
                    }
                }
                nodes++;
                consumed += bodyStart + bodyLength;
            }
            data = MtpConnection.Consume(data, consumed);
        }
        return nodes;
    }

    private static JsonObject Notification(string runId, List<JsonObject> changes)
    {
        var arr = new JsonArray();
        foreach (var c in changes) arr.Add(c);
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "testing/testUpdates/tests",
            ["params"] = new JsonObject { ["runId"] = runId, ["changes"] = arr },
        };
    }

    /// <summary>A node as MSTest's MTP server mode reports it.</summary>
    private static JsonObject Change(TestNode t, string state)
    {
        var node = new JsonObject
        {
            ["uid"] = t.Id,
            ["display-name"] = t.DisplayName,
            ["node-type"] = "action",
            ["execution-state"] = state,
            ["location.namespace"] = t.Namespace,
            ["location.type"] = t.Namespace + "." + t.ClassName,
            ["location.method"] = t.MethodName + (t.DisplayName.Contains('(') ? "(System.Int32,System.String,System.Double)" : "()"),
            ["location.file"] = t.FilePath,
            ["location.line-start"] = t.Line,
            ["location.line-end"] = t.LineEnd,
        };
        if (t.Traits.Count > 0)
            node["traits"] = new JsonArray(t.Traits.Select(tr => (JsonNode)new JsonObject { [tr.Value] = "" }).ToArray());
        if (state is not ("in-progress" or "discovered"))
        {
            node["time.duration-ms"] = t.DurationMs;
            if (t.ErrorMessage is not null) node["error.message"] = t.ErrorMessage;
            if (t.StackTrace is not null) node["error.stacktrace"] = t.StackTrace;
            if (t.StdOut is not null) node["standardOutput"] = t.StdOut;
        }
        return new JsonObject { ["node"] = node, ["parent"] = t.Namespace + "." + t.ClassName };
    }

    private static void WriteMessage(Stream s, JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        s.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        s.Write(body);
    }
}
