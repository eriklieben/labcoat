using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>
/// Client for the Microsoft.Testing.Platform "server mode" protocol.
///
/// We host a TCP listener, launch the test app with <c>--server --client-host localhost --client-port N</c>
/// and speak JSON-RPC 2.0 framed with LSP-style <c>Content-Length: N\r\n\r\n</c> headers.
///
/// IMPORTANT (observed): when an out-of-process extension is active (e.g. <c>--coverage</c>), the launched process is a
/// "test host controller" that connects first (its initialize result says <c>supportsDiscovery: false</c>), then spawns
/// the real test host as a child process which opens a SECOND connection to the same port. Test updates and the final
/// result (incl. attachments such as the coverage file) arrive on the child's connection; the controller never answers
/// the run request. So we accept connections in a loop, run the same operation (initialize + request) on each one, and
/// take the first request that completes.
///
/// One <see cref="MtpHost"/> = one launched process = one discover or run operation.
/// </summary>
public sealed class MtpHost : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TcpListener _listener;
    private readonly StringBuilder _output;
    private readonly Queue<string> _stderr; // last StderrLines lines of stderr (lock on itself)
    private const int StderrLines = 20;
    private readonly Action<string, JsonNode?> _onNotification;
    private readonly Action<string, string> _log;
    private readonly List<MtpConnection> _connections = [];
    private readonly OutputSink _sink;

    /// <summary>Receives every line of the process's stdout (false) / stderr (true), e.g. for live test output (LiveOutput.cs).</summary>
    private sealed class OutputSink
    {
        public volatile Action<string, bool>? Line;
    }

    /// <summary>Called for each stdout (<c>false</c>) / stderr (<c>true</c>) line of the process, on its output reader threads.</summary>
    public Action<string, bool>? OutputLine
    {
        get => _sink.Line;
        set => _sink.Line = value;
    }

    private MtpHost(Process process, TcpListener listener, StringBuilder output, Queue<string> stderr, Action<string, JsonNode?> onNotification, Action<string, string> log,
        OutputSink sink)
    {
        _sink = sink;
        _process = process;
        _listener = listener;
        _output = output;
        _stderr = stderr;
        _onNotification = onNotification;
        _log = log;
    }

    /// <summary>
    /// The test host's environment: Labcoat's own, plus its markers and minus the Aspire run's variables; returns the
    /// names removed. No DOTNET_CLI_UI_LANGUAGE: the tests run in the user's UI culture, as under <c>dotnet test</c>
    /// (MTP sets CurrentUICulture from it). Only Labcoat's own dotnet / msbuild / <c>--help</c> calls, whose output it
    /// parses, run in English.
    /// </summary>
    internal static List<string> ConfigureTestHostEnvironment(IDictionary<string, string?> env, IReadOnlyDictionary<string, string>? environment,
        bool tracingEnabled, string? traceFilter = null)
    {
        // Marks the process as started by Labcoat, so in-process helpers (e.g. Labcoat.TestingPlatform's --labcoat
        // launcher) stay inert and never start another Labcoat.
        env["LABCOAT_TEST_HOST"] = "1";
        VerifySnapshots.ConfigureTestHost(env); // DiffEngine_Disabled: Verify never opens a diff tool (the UI diffs)
        // Without the Aspire run's variables (TestHostEnvironment.cs); the dashboard's OTLP endpoint for Labcoat.Testing.
        var removed = Telemetry.ConfigureTestHost(env, tracingEnabled, traceFilter);
        if (environment is not null)
            foreach (var (k, v) in environment) env[k] = v;
        return removed;
    }

    /// <param name="onNotification">Called (on a connection's read loop, in order) for every notification: (method, params).</param>
    /// <param name="log">(level, message) sink for diagnostics.</param>
    /// <param name="environment">Extra environment variables for the test host process.</param>
    /// <param name="tracingEnabled">Pass the Aspire dashboard's OTLP endpoint as LABCOAT_OTLP_* (see <see cref="Telemetry"/>).</param>
    /// <param name="traceFilter">With tracing on: only the tests matching it are traced (LABCOAT_OTLP_FILTER).</param>
    public static MtpHost Start(string targetPath, IEnumerable<string> extraArgs, Action<string, JsonNode?> onNotification, Action<string, string> log,
        IReadOnlyDictionary<string, string>? environment = null, bool tracingEnabled = false, string? traceFilter = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var psi = new ProcessStartInfo(Explorer.DotnetPath)
        {
            WorkingDirectory = Path.GetDirectoryName(targetPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(targetPath);
        psi.ArgumentList.Add("--server");
        psi.ArgumentList.Add("--client-host");
        psi.ArgumentList.Add("localhost");
        psi.ArgumentList.Add("--client-port");
        psi.ArgumentList.Add(port.ToString());
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);
        var removed = ConfigureTestHostEnvironment(psi.Environment, environment, tracingEnabled, traceFilter);
        if (TestHostEnvironment.DescribeOnce(removed) is { } removedInfo) log("info", removedInfo);

        var output = new StringBuilder();
        var stderr = new Queue<string>();
        var sink = new OutputSink();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            sink.Line?.Invoke(e.Data, false);
            // Labcoat.Testing's live output markers are a side channel, not process output.
            if (!e.Data.StartsWith(LiveOutput.Marker, StringComparison.Ordinal)) lock (output) AppendCapped(output, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            sink.Line?.Invoke(e.Data, true);
            lock (output) AppendCapped(output, e.Data);
            lock (stderr)
            {
                stderr.Enqueue(e.Data);
                while (stderr.Count > StderrLines) stderr.Dequeue();
            }
            if (!string.IsNullOrWhiteSpace(e.Data)) log(LogLevels.Warning, "[test host stderr] " + e.Data);
        };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            listener.Stop();
            process.Dispose();
            throw;
        }
        log("info", $"Started test host (pid {process.Id}): dotnet {Path.GetFileName(targetPath)} --server --client-port {port} {string.Join(' ', extraArgs)}".TrimEnd());
        return new MtpHost(process, listener, output, stderr, onNotification, log, sink);
    }

    /// <summary>
    /// Accepts connections until <paramref name="operation"/> completes on one of them, and returns that result.
    /// Fails when the process exits (or nothing connects within 60s) before any operation completed.
    /// </summary>
    /// <param name="connectTimeout">How long to wait for the first connection (default 60 s; debug runs wait for the debugger).</param>
    public async Task<JsonNode?> RunOperationAsync(Func<MtpConnection, CancellationToken, Task<JsonNode?>> operation, CancellationToken ct,
        TimeSpan? connectTimeout = null)
    {
        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var op = new PendingOperation(operation, ct);
        var connectWait = connectTimeout ?? TimeSpan.FromSeconds(60);
        // Stopped (not disposed) by the accept loop at the first connection; disposed when the operation is over.
        using var connectTimeoutCts = new CancellationTokenSource(connectWait);
        // The loop ends through opCts.Token (passed to it); it is awaited in the finally below.
        var acceptLoop = Task.Run(() => AcceptLoopAsync(op, connectTimeoutCts, opCts.Token), CancellationToken.None);

        try
        {
            var exited = _process.WaitForExitAsync(opCts.Token);
            var timeout = Task.Delay(Timeout.Infinite, connectTimeoutCts.Token);

            var first = await Task.WhenAny(op.Done.Task, exited, timeout);
            if (first == op.Done.Task) return await op.Done.Task;
            if (first == exited && _process.HasExited) return await ResultAfterExitAsync(op, ct);
            ct.ThrowIfCancellationRequested();
            if (first == timeout) throw new TimeoutException($"Test host did not connect within {connectWait.TotalSeconds:0}s.");
            return await op.Done.Task;
        }
        finally
        {
            await opCts.CancelAsync();
            try { await acceptLoop; } catch { /* it ends with the cancellation above; only waiting for it */ }
        }
    }

    /// <summary>An operation of <see cref="RunOperationAsync"/>: run on every connection, the first result completes it.</summary>
    private sealed class PendingOperation(Func<MtpConnection, CancellationToken, Task<JsonNode?>> operation, CancellationToken ct)
    {
        private readonly List<Exception> _errors = [];

        public TaskCompletionSource<JsonNode?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? FirstError()
        {
            lock (_errors) return _errors.FirstOrDefault();
        }

        public async Task RunAsync(MtpConnection conn)
        {
            try { Done.TrySetResult(await operation(conn, ct)); }
            catch (Exception ex)
            {
                lock (_errors) _errors.Add(ex);
                if (ct.IsCancellationRequested) Done.TrySetCanceled(ct);
            }
        }
    }

    /// <summary>Accepts connections (each runs the operation) until <paramref name="token"/> is cancelled or the listener stops.</summary>
    private async Task AcceptLoopAsync(PendingOperation op, CancellationTokenSource connectTimeoutCts, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(token);
                connectTimeoutCts.CancelAfter(Timeout.Infinite); // connected: stop the connect timeout (a no-op after the first)
                tcp.NoDelay = true;
                var conn = new MtpConnection(tcp, _onNotification, _log, () => TryKill(_process));
                lock (_connections) _connections.Add(conn);
                _ = Task.Run(() => op.RunAsync(conn), CancellationToken.None); // the operation itself takes the request's ct
            }
        }
        // The accept loop ends when the operation ends (cancelled, listener stopped): not an error.
        catch (OperationCanceledException) { /* see above */ }
        catch (ObjectDisposedException) { /* see above */ }
        catch (SocketException) { /* see above */ }
    }

    /// <summary>The process exited first: the result when it still arrives, else <see cref="TestHostExitedException"/>.</summary>
    private async Task<JsonNode?> ResultAfterExitAsync(PendingOperation op, CancellationToken ct)
    {
        // Give in-flight responses a moment to be dispatched.
        if (await Task.WhenAny(op.Done.Task, Task.Delay(500, ct)) == op.Done.Task) return await op.Done.Task;
        ct.ThrowIfCancellationRequested();
        string text;
        lock (_output) text = _output.ToString();
        var inner = op.FirstError();
        throw new TestHostExitedException(
            $"Test host exited with code {_process.ExitCode} before completing the request." +
            (inner is null ? " Is this a Microsoft.Testing.Platform test app?" : " " + inner.Message) +
            (text.Length > 0 ? "\n" + Tail(text, 3000) : ""), _process.ExitCode, StderrTail(), inner);
    }

    /// <summary>
    /// Sends <c>exit</c> on every connection and waits for the process to go away (kills it after
    /// <paramref name="timeout"/>). Out-of-process extensions (coverage) write their files on the way out, so callers
    /// that need those files should call this before reading them.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        List<MtpConnection> conns;
        lock (_connections) conns = [.. _connections];
        if (conns.Count == 0)
        {
            // Never connected (e.g. still waiting for a debugger, or hung in startup): nobody to send `exit` to.
            TryKill(_process);
            return;
        }
        // Child test hosts first (later connections), then the controller.
        foreach (var c in Enumerable.Reverse(conns))
            await c.SendNotificationAsync("exit", new JsonObject());
        if (!_process.HasExited)
        {
            using var cts = new CancellationTokenSource(timeout);
            try { await _process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                _log(LogLevels.Warning, $"Test host did not exit within {timeout.TotalSeconds:0}s; killing it.");
                TryKill(_process);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await ShutdownAsync(TimeSpan.FromSeconds(10)); } catch { TryKill(_process); }
        _listener.Stop();
        List<MtpConnection> conns;
        lock (_connections) conns = [.. _connections];
        foreach (var c in conns) await c.DisposeAsync();
        TryKill(_process);
        _process.Dispose();
    }

    public void Kill() => TryKill(_process);

    public bool HasExited => _process.HasExited;
    public int ProcessId => _process.Id;
    public int? ExitCode { get { try { return _process.HasExited ? _process.ExitCode : null; } catch { return null; } } }
    public Task WaitForExitAsync(CancellationToken ct = default) => _process.WaitForExitAsync(ct);

    public string OutputTail(int max)
    {
        lock (_output) return Tail(_output.ToString(), max).Trim();
    }

    /// <summary>The last lines the process wrote to stderr (max 20).</summary>
    public List<string> StderrTail()
    {
        lock (_stderr) return [.. _stderr];
    }

    /// <summary>Keeps the captured output bounded (warm test hosts live for a long time).</summary>
    private static void AppendCapped(StringBuilder output, string line)
    {
        output.AppendLine(line);
        if (output.Length > 256 * 1024) output.Remove(0, output.Length - 128 * 1024);
    }

    /// <summary>
    /// For a long-lived (warm) session: accepts the first connection and initializes it. The caller then sends any
    /// number of requests over it. Fails when the process exits or nothing connects within 60s.
    /// </summary>
    public async Task<MtpConnection> ConnectAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var accept = _listener.AcceptTcpClientAsync(cts.Token).AsTask();
        var exited = _process.WaitForExitAsync(cts.Token);
        await Task.WhenAny(accept, exited);
        if (!accept.IsCompletedSuccessfully)
        {
            await cts.CancelAsync();
            try { await accept; } catch { /* cancelled above; only waiting for it */ }
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                await Task.Delay(200, CancellationToken.None); // let the output readers drain
                var text = OutputTail(3000);
                throw new TestHostExitedException($"Test host exited with code {_process.ExitCode} before connecting. " +
                    "Is this a Microsoft.Testing.Platform test app?" + (text.Length > 0 ? "\n" + text : ""), _process.ExitCode, StderrTail());
            }
            throw new TimeoutException("Test host did not connect within 60s.");
        }
        var tcp = await accept;
        tcp.NoDelay = true;
        var conn = new MtpConnection(tcp, _onNotification, _log, () => TryKill(_process));
        lock (_connections) _connections.Add(conn);
        await conn.InitializeAsync(ct);
        return conn;
    }

    internal static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort: the process may have exited already */ }
    }

    internal static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}

/// <summary>One JSON-RPC connection from a test host process.</summary>
public sealed class MtpConnection : IAsyncDisposable
{
    // JSON-RPC 2.0 message members.
    private const string JsonRpc = "jsonrpc";
    private const string JsonRpcVersion = "2.0";
    private const string ErrorMember = "error";

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly Action<string, JsonNode?> _onNotification;
    private readonly Action<string, string> _log;
    private readonly Action _kill;
    private readonly Task _readLoop;
    private int _nextId;

    internal MtpConnection(TcpClient tcp, Action<string, JsonNode?> onNotification, Action<string, string> log, Action kill)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _onNotification = onNotification;
        _log = log;
        _kill = kill;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// How long a cancelled request may take to finish (after <c>$/cancelRequest</c>) before the process tree is killed.
    /// Default 3 s (one-off processes); warm hosts use a longer grace so they survive a cancel.
    /// </summary>
    public TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Initialize result; <c>capabilities.testing.supportsDiscovery == false</c> identifies a test host controller.</summary>
    public JsonNode? InitializeResult { get; private set; }

    public async Task<JsonNode?> InitializeAsync(CancellationToken ct)
    {
        InitializeResult = await RequestAsync("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["clientInfo"] = new JsonObject { ["name"] = "labcoat", ["version"] = "0.1.0" },
            ["capabilities"] = new JsonObject
            {
                ["testing"] = new JsonObject
                {
                    ["debuggerProvider"] = false,
                    ["attachmentsSupport"] = true,
                },
            },
        }, ct);
        return InitializeResult;
    }

    public Task<JsonNode?> DiscoverAsync(Guid runId, CancellationToken ct) =>
        RequestAsync("testing/discoverTests", new JsonObject { ["runId"] = runId.ToString() }, ct);

    /// <param name="tests">null = run everything. Otherwise minimal node objects ({uid, display-name}) are enough.</param>
    public Task<JsonNode?> RunAsync(Guid runId, IEnumerable<(string Uid, string DisplayName)>? tests, CancellationToken ct)
    {
        var p = new JsonObject { ["runId"] = runId.ToString() };
        if (tests is not null)
        {
            var arr = new JsonArray();
            foreach (var (uid, name) in tests)
                arr.Add(new JsonObject { ["uid"] = uid, ["display-name"] = name });
            p["tests"] = arr;
        }
        return RequestAsync("testing/runTests", p, ct);
    }

    /// <summary>
    /// Sends a request. When <paramref name="ct"/> fires we send <c>$/cancelRequest</c> and keep waiting for the
    /// server to answer for up to <paramref name="cancelGrace"/>, then kill the process tree.
    /// </summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? @params, CancellationToken ct, TimeSpan? cancelGrace = null)
    {
        ct.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await SendAsync(new JsonObject { [JsonRpc] = JsonRpcVersion, ["id"] = id, ["method"] = method, ["params"] = @params });

        await using var reg = ct.Register(() =>
        {
            _ = SendAsync(new JsonObject { [JsonRpc] = JsonRpcVersion, ["method"] = "$/cancelRequest", ["params"] = new JsonObject { ["id"] = id } });
            var grace = cancelGrace ?? CancelGrace;
            // The kill watchdog must not be cancellable: it runs because the request was cancelled.
            _ = Task.Delay(grace, CancellationToken.None).ContinueWith(_ =>
            {
                if (!tcs.Task.IsCompleted)
                {
                    _log(LogLevels.Warning, $"Test host did not stop within {grace.TotalSeconds:0}s after cancellation (the running test does not " +
                        "observe the cancellation token); killing it. The next run starts a new test host.");
                    _kill();
                    tcs.TrySetCanceled(CancellationToken.None); // as before: a cancelled task without a token
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        });

        return await tcs.Task;
    }

    public Task SendNotificationAsync(string method, JsonNode? @params) =>
        SendAsync(new JsonObject { [JsonRpc] = JsonRpcVersion, ["method"] = method, ["params"] = @params });

    private async Task SendAsync(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(header);
            await _stream.WriteAsync(body);
            await _stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Connection gone; the read loop fails pending requests.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var data = new MemoryStream();
        try
        {
            while (true)
            {
                var n = await _stream.ReadAsync(buffer);
                if (n == 0) break;
                await data.WriteAsync(buffer.AsMemory(0, n), CancellationToken.None); // an in-memory copy: completes at once
                var consumed = 0;
                while (TryParseMessage(data.GetBuffer().AsSpan(consumed, (int)data.Length - consumed), out var bodyStart, out var bodyLength))
                {
                    // parsed straight from the UTF-8 bytes: a discovery / results batch can be MBs, no string copy of it
                    try { Dispatch(JsonNode.Parse(data.GetBuffer().AsSpan(consumed + bodyStart, bodyLength))); }
                    catch (Exception ex) { _log(LogLevels.Error, "Failed to handle message from test host: " + ex.Message); }
                    consumed += bodyStart + bodyLength;
                }
                data = Consume(data, consumed);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { /* the connection closed: the read loop ends */ }
        finally
        {
            foreach (var p in _pending.Values)
                p.TrySetException(new IOException("Connection to the test host was closed."));
        }
    }

    /// <summary>Drops the first <paramref name="count"/> bytes; a large buffer is let go once it is empty (it is not kept for the connection's lifetime).</summary>
    internal static MemoryStream Consume(MemoryStream data, int count)
    {
        if (count == 0) return data;
        var rest = (int)data.Length - count;
        if (rest == 0 && data.Capacity > 1024 * 1024) return new MemoryStream();
        var buf = data.GetBuffer();
        Buffer.BlockCopy(buf, count, buf, 0, rest);
        data.SetLength(rest);
        return data;
    }

    /// <summary>One "Content-Length: n\r\n\r\n&lt;body&gt;" message at the start of <paramref name="data"/>: the body's offset and length.</summary>
    internal static bool TryParseMessage(ReadOnlySpan<byte> data, out int bodyStart, out int bodyLength)
    {
        bodyStart = bodyLength = 0;
        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return false;
        var header = Encoding.ASCII.GetString(data[..headerEnd]);
        int contentLength = -1;
        foreach (var line in header.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line[(colon + 1)..].Trim());
        }
        if (contentLength < 0) throw new IOException("Missing Content-Length header: " + header);
        var start = headerEnd + 4;
        if (data.Length - start < contentLength) return false;
        (bodyStart, bodyLength) = (start, contentLength);
        return true;
    }

    private void Dispatch(JsonNode? msg)
    {
        if (msg is not JsonObject obj) return;
        var method = obj["method"]?.GetValue<string>();
        var idNode = obj["id"];

        if (method is null && idNode is not null)
        {
            // Response to one of our requests.
            var id = idNode.GetValue<int>();
            if (_pending.TryRemove(id, out var tcs))
            {
                if (obj[ErrorMember] is JsonObject err)
                    tcs.TrySetException(new MtpRpcException(
                        err["code"]?.GetValue<int>() ?? 0, err["message"]?.GetValue<string>() ?? "error", err["data"]?.ToJsonString()));
                else
                    tcs.TrySetResult(obj["result"]);
            }
            return;
        }

        if (method is not null && idNode is not null)
        {
            // Request from the test host to us (e.g. client/attachDebugger). We support none of them.
            _log("info", $"Test host sent request '{method}', which is not supported; replying with an error.");
            _ = SendAsync(new JsonObject
            {
                [JsonRpc] = JsonRpcVersion,
                ["id"] = idNode.DeepClone(),
                [ErrorMember] = new JsonObject { ["code"] = -32601, ["message"] = $"Method '{method}' is not supported by labcoat." },
            });
            return;
        }

        if (method is not null)
            _onNotification(method, obj["params"]);
    }

    public async ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        try { await _readLoop; } catch { /* it ends with the socket closed above; only waiting for it */ }
    }
}

/// <summary>The test host process exited before it connected / answered a request.</summary>
public sealed class TestHostExitedException(string message, int? exitCode, List<string> stderrTail, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public int? ExitCode { get; } = exitCode;
    /// <summary>Last lines of the process's stderr.</summary>
    public List<string> StderrTail { get; } = stderrTail;
}

public sealed class MtpRpcException(int code, string message, string? data)
    : Exception($"JSON-RPC error {code}: {message}{(data is null ? "" : " " + data)}")
{
    public int Code { get; } = code;
}
