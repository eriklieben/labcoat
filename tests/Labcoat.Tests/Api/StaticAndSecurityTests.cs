using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Labcoat;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Labcoat.Tests.Api;

/// <summary>The UI files (index.html, assets, caching) and the security middleware, through the whole pipeline.</summary>
[TestClass]
public class StaticAndSecurityTests
{
    public TestContext TestContext { get; set; }

    private static string InlineHash => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ApiHost.InlineScript)));

    [TestMethod]
    public async Task Index_is_served_uncached_with_the_ui_defaults_and_a_csp_hash_of_its_inline_script()
    {
        var defaults = JsonNode.Parse("""{"theme":"dracula","locked":["theme"]}""")!.AsObject();
        await using var h = await ApiHost.StartAsync(uiDefaults: defaults);
        foreach (var path in new[] { "/", "/index.html" })
        {
            using var res = await h.Client.GetAsync(path, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode, path);
            Assert.AreEqual("text/html", res.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("no-cache", res.Headers.CacheControl!.ToString());
            var html = await res.Content.ReadAsStringAsync(TestContext.CancellationToken);
            Assert.Contains("<html data-labcoat-defaults=\"{&quot;theme&quot;:&quot;dracula&quot;,&quot;locked&quot;:[&quot;theme&quot;]}\" lang=\"en\">", html);
            var csp = res.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains($"script-src 'self' 'sha256-{InlineHash}';", csp);
            Assert.Contains("connect-src 'self' ws://localhost wss://localhost;", csp);
        }
        // The same defaults are in the state snapshot.
        var state = await h.GetJson("/api/state");
        Assert.AreEqual("dracula", state["uiDefaults"]!["theme"]!.GetValue<string>());

        using var head = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.IsEmpty(await head.Content.ReadAsByteArrayAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Hashed_assets_are_immutable_other_files_revalidate()
    {
        await using var h = await ApiHost.StartAsync();
        using (var res = await h.Client.GetAsync("/assets/app-1234.js", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.AreEqual("public, max-age=31536000, immutable", res.Headers.CacheControl!.ToString());
            Assert.AreEqual("console.log('app');", await res.Content.ReadAsStringAsync(TestContext.CancellationToken));
        }
        using (var res = await h.Client.GetAsync("/favicon.svg", TestContext.CancellationToken))
        {
            Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.AreEqual("no-cache", res.Headers.CacheControl!.ToString());
        }
    }

    [TestMethod]
    public async Task Without_a_ui_build_the_page_says_so()
    {
        await using var h = await ApiHost.StartAsync(withUi: false);
        foreach (var path in new[] { "/", "/some/route" })
        {
            using var res = await h.Client.GetAsync(path, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Contains("UI not built yet", await res.Content.ReadAsStringAsync(TestContext.CancellationToken));
        }
        // The CSP has no script hashes then, but is still sent.
        using var api = await h.Client.GetAsync("/api/health", TestContext.CancellationToken);
        Assert.Contains("script-src 'self';", api.Headers.GetValues("Content-Security-Policy").Single());
    }

    [TestMethod]
    public async Task Every_response_carries_the_security_headers()
    {
        await using var h = await ApiHost.StartAsync();
        foreach (var path in new[] { "/api/health", "/", "/assets/app-1234.js", "/api/nope" })
        {
            using var res = await h.Client.GetAsync(path, TestContext.CancellationToken);
            Assert.AreEqual("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single(), path);
            Assert.AreEqual("DENY", res.Headers.GetValues("X-Frame-Options").Single(), path);
            Assert.AreEqual("no-referrer", res.Headers.GetValues("Referrer-Policy").Single(), path);
            Assert.AreEqual("same-origin", res.Headers.GetValues("Cross-Origin-Opener-Policy").Single(), path);
            Assert.AreEqual("same-origin", res.Headers.GetValues("Cross-Origin-Resource-Policy").Single(), path);
            Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        }
    }

    [TestMethod]
    public async Task Foreign_host_names_are_rejected_before_routing()
    {
        await using var h = await ApiHost.StartAsync();
        foreach (var path in new[] { "/api/health", "/", "/ws" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Host = "attacker.example:4300";
            using var res = await h.Client.SendAsync(req, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.BadRequest, res.StatusCode, path);
            Assert.Contains("Host 'attacker.example:4300' is not allowed", (await ApiHost.Json(res))!["error"]!.GetValue<string>());
            Assert.AreEqual("DENY", res.Headers.GetValues("X-Frame-Options").Single()); // headers even on rejections
        }
        using var ok = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        ok.Headers.Host = "tests.dev.localhost";
        Assert.AreEqual(HttpStatusCode.OK, (await h.Client.SendAsync(ok, TestContext.CancellationToken)).StatusCode);
    }

    [TestMethod]
    public async Task Cross_origin_commands_are_rejected_same_origin_ones_accepted()
    {
        await using var h = await ApiHost.StartAsync(withProject: false);
        async Task<HttpStatusCode> Post(string? origin, string? site = null, string contentType = "application/json")
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/watch") { Content = new StringContent("""{"enabled":false}""", Encoding.UTF8) };
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            if (origin is not null) req.Headers.Add("Origin", origin);
            if (site is not null) req.Headers.Add("Sec-Fetch-Site", site);
            using var res = await h.Client.SendAsync(req, TestContext.CancellationToken);
            return res.StatusCode;
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, await Post("http://evil.example"));
        Assert.AreEqual(HttpStatusCode.Forbidden, await Post("http://localhost:5174")); // another local port is another origin
        Assert.AreEqual(HttpStatusCode.Forbidden, await Post("null"));
        Assert.AreEqual(HttpStatusCode.Forbidden, await Post(null, site: "cross-site"));
        Assert.AreEqual(HttpStatusCode.OK, await Post("http://localhost"));
        Assert.AreEqual(HttpStatusCode.OK, await Post(null, site: "same-origin"));
        Assert.AreEqual(HttpStatusCode.OK, await Post(null));
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, await Post("http://localhost", contentType: "text/plain"));
        // GET API calls are not origin-checked (no side effects; the browser blocks cross-origin reads).
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        get.Headers.Add("Origin", "http://evil.example");
        Assert.AreEqual(HttpStatusCode.OK, (await h.Client.SendAsync(get, TestContext.CancellationToken)).StatusCode);
    }

    [TestMethod]
    public async Task Listed_origins_and_the_token_rule_for_scripts()
    {
        await using var h = await ApiHost.StartAsync(withProject: false,
            guard: new RequestGuard(["devbox"], ["http://localhost:5174"], token: "s3cret"));
        async Task<HttpStatusCode> Post(Action<HttpRequestMessage> setup)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/cancel");
            setup(req);
            using var res = await h.Client.SendAsync(req, TestContext.CancellationToken);
            return res.StatusCode;
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, await Post(_ => { }));
        Assert.AreEqual(HttpStatusCode.Forbidden, await Post(r => r.Headers.Add(RequestGuard.TokenHeader, "wrong")));
        Assert.AreEqual(HttpStatusCode.Accepted, await Post(r => r.Headers.Add(RequestGuard.TokenHeader, "s3cret")));
        Assert.AreEqual(HttpStatusCode.Accepted, await Post(r => r.Headers.Add("Origin", "http://localhost:5174")));
        Assert.AreEqual(HttpStatusCode.Accepted, await Post(r => r.Headers.Add("Origin", "http://localhost"))); // the browser UI needs no token
        Assert.AreEqual(HttpStatusCode.Accepted, await Post(r =>
        {
            r.Headers.Host = "devbox";
            r.Headers.Add("Origin", "http://devbox");
        }));
        // Reads stay open.
        Assert.AreEqual(HttpStatusCode.OK, (await h.Client.GetAsync("/api/health", TestContext.CancellationToken)).StatusCode);
    }

    [TestMethod]
    public async Task Requests_without_origin_must_come_over_loopback()
    {
        await using var h = await ApiHost.StartAsync(withProject: false);
        async Task<int> Send(IPAddress remote) => (await h.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/cancel";
            ctx.Request.Host = new HostString("localhost");
            ctx.Connection.RemoteIpAddress = remote;
        }, TestContext.CancellationToken)).Response.StatusCode;
        Assert.AreEqual(StatusCodes.Status403Forbidden, await Send(IPAddress.Parse("192.168.1.20")));
        Assert.AreEqual(StatusCodes.Status202Accepted, await Send(IPAddress.Loopback));
        Assert.AreEqual(StatusCodes.Status202Accepted, await Send(IPAddress.IPv6Loopback));
    }
}
