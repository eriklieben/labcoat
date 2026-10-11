using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;

namespace Labcoat.Benchmarks;

/// <summary>
/// The per-request guard (Security.cs, <see cref="RequestGuard.Check"/>): Host allow-list (DNS rebinding), Origin /
/// Sec-Fetch-Site for state-changing API calls and WebSocket handshakes, the <c>X-Labcoat-Token</c> comparison for
/// non-browser clients, and the JSON content type. One request shape per benchmark; the contexts are built once.
/// </summary>
[MemoryDiagnoser]
public class SecurityBenchmarks
{
    private RequestGuard _guard = null!;
    private HttpContext _staticGet = null!;
    private HttpContext _apiPostSameOrigin = null!;
    private HttpContext _wsHandshake = null!;
    private HttpContext _apiPostToken = null!;
    private HttpContext _rebindingHost = null!;
    private HttpContext _crossOrigin = null!;

    [GlobalSetup]
    public void Setup()
    {
        _guard = new RequestGuard(["devbox.local"], ["https://dashboard.localhost:17000"], token: "0123456789abcdef0123456789abcdef"); // gitleaks:allow (a fake token for the benchmark)
        _staticGet = Request("GET", "/assets/index-abc123.js", "localhost:4670");
        _apiPostSameOrigin = Request("POST", "/api/run", "localhost:4670", origin: "http://localhost:4670", json: true);
        _wsHandshake = Request("GET", "/ws", "127.0.0.1:4670", origin: "http://127.0.0.1:4670");
        _apiPostToken = Request("POST", "/api/run", "localhost:4670", json: true, token: "0123456789abcdef0123456789abcdef"); // gitleaks:allow (a fake token for the benchmark)
        _rebindingHost = Request("GET", "/api/state", "attacker.example:4670");
        _crossOrigin = Request("POST", "/api/run", "localhost:4670", origin: "https://evil.example", json: true);
    }

    [Benchmark(Baseline = true)]
    public bool StaticGet() => _guard.Check(_staticGet) is null;

    [Benchmark]
    public bool ApiPost_SameOrigin() => _guard.Check(_apiPostSameOrigin) is null;

    [Benchmark]
    public bool WebSocket_Handshake() => _guard.Check(_wsHandshake) is null;

    [Benchmark]
    public bool ApiPost_Token() => _guard.Check(_apiPostToken) is null;

    [Benchmark]
    public bool Rejected_RebindingHost() => _guard.Check(_rebindingHost) is null;

    [Benchmark]
    public bool Rejected_CrossOrigin() => _guard.Check(_crossOrigin) is null;

    private static DefaultHttpContext Request(string method, string path, string host, string? origin = null, bool json = false, string? token = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "http";
        ctx.Request.Path = path;
        ctx.Request.Host = HostString.FromUriComponent(host);
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        if (origin is not null)
        {
            ctx.Request.Headers.Origin = origin;
            ctx.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        }
        if (json)
        {
            ctx.Request.ContentType = "application/json; charset=utf-8";
            ctx.Request.ContentLength = 42;
        }
        if (token is not null) ctx.Request.Headers[RequestGuard.TokenHeader] = token;
        return ctx;
    }
}
