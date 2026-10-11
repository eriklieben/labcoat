using System.Net;
using System.Security.Cryptography;
using System.Text;
using Labcoat;
using Microsoft.AspNetCore.Http;

namespace Labcoat.Tests;

[TestClass]
public class SecurityTests
{
    private static DefaultHttpContext Request(string method, string path, string host = "localhost:4300", string? origin = null,
        string? contentType = null, string? body = null, IPAddress? remote = null, (string, string)? header = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "http";
        ctx.Request.Path = path;
        ctx.Request.Host = new HostString(host);
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        if (contentType is not null) ctx.Request.ContentType = contentType;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }
        if (header is { } h) ctx.Request.Headers[h.Item1] = h.Item2;
        ctx.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        return ctx;
    }

    private static int? Status(RequestGuard guard, DefaultHttpContext ctx) => guard.Check(ctx)?.Status;

    [TestMethod]
    public void Host_allow_list_covers_loopback_names_and_configured_hosts()
    {
        var guard = new RequestGuard(["myhost.internal"]);
        foreach (var ok in new[] { "localhost", "LOCALHOST", "tests.dev.localhost", "127.0.0.1", "127.0.0.2", "::1", "[::1]", "myhost.internal", "localhost." })
            Assert.IsTrue(guard.IsAllowedHost(ok), ok);
        foreach (var bad in new[] { "attacker.example", "localhost.attacker.example", "evillocalhost", "10.0.0.5", "", null })
            Assert.IsFalse(guard.IsAllowedHost(bad), bad ?? "null");
        Assert.IsTrue(new RequestGuard(["*"]).IsAllowedHost("anything.example"));
    }

    [TestMethod]
    public void Bind_hosts_come_from_listen_urls_without_wildcards()
    {
        CollectionAssert.AreEquivalent(new[] { "localhost", "devbox" },
            RequestGuard.HostsOfUrls("http://localhost:4300;http://0.0.0.0:5000;http://+:80;http://*:81;http://devbox:4300").ToList());
    }

    [TestMethod]
    public void Foreign_host_is_rejected_dns_rebinding()
    {
        var guard = new RequestGuard([]);
        Assert.AreEqual(400, Status(guard, Request("GET", "/api/state", host: "attacker.example:4300")));
        Assert.AreEqual(400, Status(guard, Request("GET", "/api/source", host: "attacker.example:4300")));
        Assert.AreEqual(400, Status(guard, Request("GET", "/", host: "attacker.example")));
        Assert.IsNull(Status(guard, Request("GET", "/api/state")));
        Assert.IsNull(Status(guard, Request("GET", "/api/state", host: "[::1]:4300")));
    }

    [TestMethod]
    public void Cross_origin_commands_and_websockets_are_rejected()
    {
        var guard = new RequestGuard([]);
        Assert.AreEqual(403, Status(guard, Request("POST", "/api/watch", origin: "http://evil.example", contentType: "text/plain", body: "{\"enabled\":true}")));
        Assert.AreEqual(403, Status(guard, Request("POST", "/api/run", origin: "http://evil.example", contentType: "application/json", body: "{}")));
        Assert.AreEqual(403, Status(guard, Request("POST", "/api/open-in-editor", origin: "http://evil.example", contentType: "application/json", body: "{}")));
        Assert.AreEqual(403, Status(guard, Request("GET", "/ws", origin: "http://evil.example")));
        Assert.AreEqual(403, Status(guard, Request("GET", "/ws", origin: "null")));
        // A different port on localhost is a different origin (e.g. another local dev server).
        Assert.AreEqual(403, Status(guard, Request("POST", "/api/run", origin: "http://localhost:5174", contentType: "application/json", body: "{}")));
        // Browser signal without Origin.
        Assert.AreEqual(403, Status(guard, Request("POST", "/api/run", contentType: "application/json", body: "{}", header: ("Sec-Fetch-Site", "cross-site"))));
    }

    [TestMethod]
    public void Same_origin_and_listed_origins_are_accepted()
    {
        var guard = new RequestGuard([], ["http://localhost:5174"]);
        Assert.IsNull(Status(guard, Request("POST", "/api/run", origin: "http://localhost:4300", contentType: "application/json", body: "{}")));
        Assert.IsNull(Status(guard, Request("POST", "/api/run", host: "127.0.0.1:4300", origin: "http://127.0.0.1:4300", contentType: "application/json; charset=utf-8", body: "{}")));
        Assert.IsNull(Status(guard, Request("GET", "/ws", origin: "http://localhost:4300")));
        Assert.IsNull(Status(guard, Request("GET", "/ws", host: "tests.dev.localhost:20530", origin: "http://tests.dev.localhost:20530")));
        Assert.IsNull(Status(guard, Request("POST", "/api/run", origin: "http://localhost:5174", contentType: "application/json", body: "{}")));
        // GET API calls need no Origin check (side-effect free; cross-origin reads are blocked by the browser).
        Assert.IsNull(Status(guard, Request("GET", "/api/state", origin: "http://evil.example")));
    }

    [TestMethod]
    public void Scripts_without_origin_need_loopback_and_the_token_when_configured()
    {
        var open = new RequestGuard([]);
        Assert.IsNull(Status(open, Request("POST", "/api/cancel")));
        Assert.IsNull(Status(open, Request("POST", "/api/run", contentType: "application/json", body: "{}")));
        Assert.AreEqual(403, Status(open, Request("POST", "/api/run", contentType: "application/json", body: "{}", remote: IPAddress.Parse("192.168.1.20"))));

        var withToken = new RequestGuard([], token: "s3cret");
        Assert.AreEqual(403, Status(withToken, Request("POST", "/api/cancel")));
        Assert.AreEqual(403, Status(withToken, Request("POST", "/api/cancel", header: (RequestGuard.TokenHeader, "wrong"))));
        Assert.IsNull(Status(withToken, Request("POST", "/api/cancel", header: (RequestGuard.TokenHeader, "s3cret"))));
        Assert.AreEqual(403, Status(withToken, Request("GET", "/ws")));
        // The browser UI (same origin) does not need the token.
        Assert.IsNull(Status(withToken, Request("POST", "/api/cancel", origin: "http://localhost:4300")));
        Assert.IsNull(Status(withToken, Request("GET", "/api/health")));
    }

    [TestMethod]
    public void Json_bodies_need_a_json_content_type()
    {
        var guard = new RequestGuard([]);
        Assert.AreEqual(415, Status(guard, Request("POST", "/api/watch", contentType: "text/plain", body: "{\"enabled\":true}")));
        Assert.AreEqual(415, Status(guard, Request("POST", "/api/watch", contentType: "application/x-www-form-urlencoded", body: "enabled=true")));
        Assert.AreEqual(415, Status(guard, Request("POST", "/api/watch", body: "{\"enabled\":true}")));
        Assert.IsNull(Status(guard, Request("POST", "/api/watch", contentType: "application/json", body: "{\"enabled\":true}")));
        Assert.IsNull(Status(guard, Request("POST", "/api/watch", contentType: "application/merge-patch+json", body: "{}")));
        Assert.IsNull(Status(guard, Request("POST", "/api/cancel"))); // no body: no content type needed
    }

    [TestMethod]
    public void Csp_hashes_inline_scripts_and_forbids_framing()
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-csp-");
        try
        {
            const string inline = "\n      document.documentElement.dataset.x = 1;\n    ";
            File.WriteAllText(Path.Combine(dir.FullName, "index.html"),
                $"<html><head><script>{inline}</script><script type=\"module\" src=\"./assets/a.js\"></script></head></html>");
            var csp = new SecurityHeaders(dir.FullName).ContentSecurityPolicy(new HostString("localhost:4300"));
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(inline)));
            Assert.Contains($"script-src 'self' 'sha256-{hash}';", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
            Assert.Contains("default-src 'self'", csp);
            Assert.Contains("connect-src 'self' ws://localhost:4300 wss://localhost:4300", csp);
            Assert.HasCount(1, SecurityHeaders.InlineScripts(File.ReadAllText(Path.Combine(dir.FullName, "index.html"))).ToList());

            var ctx = new DefaultHttpContext();
            ctx.Request.Host = new HostString("localhost:4300");
            new SecurityHeaders(dir.FullName).Apply(ctx);
            Assert.AreEqual("nosniff", ctx.Response.Headers.XContentTypeOptions.ToString());
            Assert.AreEqual("no-referrer", ctx.Response.Headers["Referrer-Policy"].ToString());
        }
        finally { dir.Delete(recursive: true); }
    }

    [TestMethod]
    public void Csp_hashes_inline_scripts_with_the_line_endings_the_browser_sees()
    {
        // The HTML parser turns CR LF and lone CR into LF before the browser hashes a script: an index.html with CRLF line
        // endings (a UI built from a Windows checkout) must get the hash of the LF text.
        var dir = Directory.CreateTempSubdirectory("labcoat-csp-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "index.html"), "<html>\r\n<script>\r\n  a();\r  b();\r\n</script>\r\n</html>\r\n");
            var csp = new SecurityHeaders(dir.FullName).ContentSecurityPolicy(new HostString("localhost"));
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("\n  a();\n  b();\n")));
            Assert.Contains($"script-src 'self' 'sha256-{hash}';", csp);
            Assert.AreEqual(hash, SecurityHeaders.ScriptHash("\n  a();\n  b();\n"));
        }
        finally { dir.Delete(recursive: true); }
    }
}
