using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// Request checks against the browser-based attacks a localhost server is exposed to (see SECURITY.md):
/// <list type="bullet">
/// <item>DNS rebinding: the <c>Host</c> header must name an allowed host (loopback names / IPs, <c>*.localhost</c>, the bind
///   host, <c>$LABCOAT_ALLOWED_HOSTS</c>); anything else gets 400.</item>
/// <item>Cross-site requests (CSRF, cross-site WebSocket hijacking): <c>/ws</c> and every non-GET <c>/api/*</c> request must
///   come from the same origin (Origin host:port == Host header), from an origin listed in <c>$LABCOAT_ALLOWED_ORIGINS</c>,
///   or from a non-browser client (no Origin, no cross-site Sec-Fetch-Site) over loopback, which must also present
///   <c>X-Labcoat-Token</c> when <c>$LABCOAT_TOKEN</c> is set. Otherwise 403.</item>
/// <item>JSON-bodied requests must say <c>Content-Type: application/json</c> (415 otherwise), so a cross-site page can't
///   send one without a CORS preflight (which Labcoat never approves).</item>
/// </list>
/// </summary>
public sealed class RequestGuard
{
    public const string TokenHeader = "X-Labcoat-Token";

    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _anyHost;
    private readonly byte[]? _token;

    /// <param name="extraHosts">Host names allowed besides the loopback names (bind host, LABCOAT_ALLOWED_HOSTS). "*" = any host.</param>
    /// <param name="extraOrigins">Origins ("scheme://host[:port]") allowed besides the same origin (LABCOAT_ALLOWED_ORIGINS).</param>
    /// <param name="token">When set, non-browser (no Origin) state-changing requests must carry it in <see cref="TokenHeader"/>.</param>
    public RequestGuard(IEnumerable<string> extraHosts, IEnumerable<string>? extraOrigins = null, string? token = null)
    {
        foreach (var raw in extraHosts)
        {
            var h = NormalizeHost(raw);
            if (h is null) continue;
            if (h == "*") _anyHost = true;
            else _hosts.Add(h);
        }
        foreach (var o in extraOrigins ?? [])
            if (NormalizeOrigin(o) is { } n) _origins.Add(n);
        _token = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
    }

    /// <summary>From the environment: LABCOAT_ALLOWED_HOSTS, LABCOAT_ALLOWED_ORIGINS, LABCOAT_TOKEN (all optional).</summary>
    public static RequestGuard FromEnvironment(IEnumerable<string> bindHosts) => new(
        bindHosts.Concat(SplitList(Environment.GetEnvironmentVariable("LABCOAT_ALLOWED_HOSTS"))),
        SplitList(Environment.GetEnvironmentVariable("LABCOAT_ALLOWED_ORIGINS")),
        Environment.GetEnvironmentVariable("LABCOAT_TOKEN"));

    public bool AllowsAnyHost => _anyHost;
    public bool RequiresToken => _token is not null;

    public static IEnumerable<string> SplitList(string? value) =>
        (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Hosts of the listen URLs ("http://localhost:4300;http://0.0.0.0:5000") that can appear in a Host header.</summary>
    public static IEnumerable<string> HostsOfUrls(string? urls)
    {
        foreach (var u in SplitList(urls))
        {
            var s = u.Replace("://+", "://wildcard.invalid").Replace("://*", "://wildcard.invalid");
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) continue;
            var host = uri.Host;
            if (host is "wildcard.invalid" or "0.0.0.0" or "[::]" or "::") continue; // wildcard binds: no specific name
            yield return host;
        }
    }

    /// <summary>Lower-cased host without brackets / trailing dot; null for garbage.</summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        return h.Length == 0 ? null : h;
    }

    /// <summary>
    /// Is <paramref name="host"/> (no port) a name this server may be addressed by? Loopback names / IP literals and
    /// <c>*.localhost</c> (browsers resolve those to loopback themselves, RFC 6761) are always fine: a DNS-rebinding page
    /// always arrives with the attacker's own DNS name in Host.
    /// </summary>
    public bool IsAllowedHost(string? host)
    {
        var h = NormalizeHost(host);
        if (h is null) return false;
        if (_anyHost) return true;
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip)) return true;
        return _hosts.Contains(h);
    }

    /// <summary>The verdict for one request: null = allowed, else (status, message).</summary>
    public (int Status, string Message)? Check(HttpContext ctx)
    {
        var req = ctx.Request;
        if (!req.Host.HasValue || !IsAllowedHost(req.Host.Host))
            return (StatusCodes.Status400BadRequest,
                $"Host '{req.Host.Value}' is not allowed. Labcoat answers to localhost / 127.0.0.1 / [::1]; " +
                "add other names to LABCOAT_ALLOWED_HOSTS.");

        var isWs = req.Path.StartsWithSegments("/ws");
        var isApi = req.Path.StartsWithSegments("/api");
        var safeMethod = HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method) || HttpMethods.IsOptions(req.Method);
        if (!isWs && !(isApi && !safeMethod)) return null;

        var origin = req.Headers.Origin.ToString();
        var rejected = origin.Length > 0 ? CheckOrigin(origin, req) : CheckWithoutOrigin(ctx);
        if (rejected is not null) return rejected;

        if (isApi && !safeMethod && HasBody(req) && !IsJson(req.ContentType))
            return (StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json.");
        return null;
    }

    private (int Status, string Message)? CheckOrigin(string origin, HttpRequest req) =>
        IsAllowedOrigin(origin, req.Scheme, req.Host) ? null : (StatusCodes.Status403Forbidden, $"Cross-origin request from '{origin}' rejected.");

    /// <summary>
    /// No Origin: a non-browser client (curl, the Aspire dashboard commands). Browsers send Origin on every WebSocket
    /// handshake and every non-GET fetch/form; Sec-Fetch-Site is a second browser signal.
    /// </summary>
    private (int Status, string Message)? CheckWithoutOrigin(HttpContext ctx)
    {
        var req = ctx.Request;
        var site = req.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none"))
            return (StatusCodes.Status403Forbidden, $"Cross-site request rejected (Sec-Fetch-Site: {site}).");
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
            return (StatusCodes.Status403Forbidden, "Requests without an Origin header are only accepted over loopback.");
        if (_token is not null && !TokenMatches(req.Headers[TokenHeader].ToString()))
            return (StatusCodes.Status403Forbidden, $"Missing or wrong {TokenHeader} header (LABCOAT_TOKEN is set).");
        return null;
    }

    private bool TokenMatches(string presented) =>
        presented.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _token!);

    /// <summary>Same origin as the request (Origin's host:port == Host header, host allowed), or explicitly listed.</summary>
    public bool IsAllowedOrigin(string origin, string requestScheme, HostString requestHost)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized is null) return false; // "null" (sandboxed frame, file://), garbage
        if (_origins.Contains(normalized)) return true;
        var uri = new Uri(normalized);
        if (!IsAllowedHost(uri.Host)) return false;
        var reqPort = requestHost.Port ?? (string.Equals(requestScheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return string.Equals(NormalizeHost(uri.Host), NormalizeHost(requestHost.Host), StringComparison.Ordinal) && uri.Port == reqPort;
    }

    private static string? NormalizeOrigin(string origin)
    {
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{uri.Port}";
    }

    private static bool HasBody(HttpRequest req) =>
        req.ContentLength > 0 || (req.ContentLength is null && req.Headers.TransferEncoding.Count > 0);

    public static bool IsJson(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var media = contentType.Split(';')[0].Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
               || (media.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && media.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Response headers: CSP (incl. hashes of index.html's inline scripts), nosniff, no-referrer, no framing.</summary>
public sealed partial class SecurityHeaders(string webRoot)
{
    private readonly object _gate = new();
    private DateTime _indexStamp;
    private string _scriptHashes = "";

    /// <summary>
    /// <c>default-src 'self'</c>; scripts: own files + the hashes of index.html's inline scripts (the theme bootstrap);
    /// styles: own files + inline (the UI sets style attributes); fonts/images: self + data:; WebSocket back to the same
    /// host; never framed.
    /// </summary>
    public string ContentSecurityPolicy(HostString host)
    {
        var ws = host.HasValue ? $" ws://{host.Value} wss://{host.Value}" : "";
        return "default-src 'self'; " +
               $"script-src 'self'{InlineScriptHashes()}; " +
               "style-src 'self' 'unsafe-inline'; " +
               "img-src 'self' data: blob:; " +
               "font-src 'self' data:; " +
               $"connect-src 'self'{ws}; " +
               "worker-src 'self' blob:; " +
               "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    }

    public void Apply(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = ContentSecurityPolicy(ctx.Request.Host);
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h.XFrameOptions = "DENY";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
    }

    /// <summary>" 'sha256-…'" for every inline &lt;script&gt; of wwwroot/index.html; recomputed when the file changes.</summary>
    private string InlineScriptHashes()
    {
        var path = Path.Combine(webRoot, "index.html");
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); } catch { return ""; }
        lock (_gate)
        {
            if (stamp == _indexStamp) return _scriptHashes;
            string html;
            try { html = File.ReadAllText(path); } catch { return ""; }
            _scriptHashes = string.Concat(InlineScripts(html).Select(s => $" 'sha256-{ScriptHash(s)}'"));
            _indexStamp = stamp;
            return _scriptHashes;
        }
    }

    /// <summary>
    /// The CSP hash (base64 SHA-256) of an inline script body as the browser computes it: over the parsed text, in which
    /// the HTML parser has turned every CR LF and lone CR into LF. An index.html with CRLF line endings (a UI built from a
    /// Windows checkout) would otherwise get hashes that never match, and its inline scripts would be blocked.
    /// </summary>
    public static string ScriptHash(string body) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body.Replace("\r\n", "\n").Replace('\r', '\n'))));

    /// <summary>Bodies of &lt;script&gt; elements without a src attribute (exact text between the tags, as CSP hashes it).</summary>
    public static IEnumerable<string> InlineScripts(string html) =>
        ScriptElementRe().MatchesOrEmpty(html)
            .Where(m => !SrcAttributeRe().IsMatchOrFalse(m.Groups["attrs"].Value))
            .Select(m => m.Groups["body"].Value)
            .Where(body => body.Length > 0);

    [GeneratedRegex(@"<script(?<attrs>[^>]*)>(?<body>.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout.Milliseconds)]
    private static partial Regex ScriptElementRe();

    [GeneratedRegex(@"\bsrc\s*=", RegexOptions.IgnoreCase, RegexTimeout.Milliseconds)]
    private static partial Regex SrcAttributeRe();
}
