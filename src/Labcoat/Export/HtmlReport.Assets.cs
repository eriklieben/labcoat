using System.Text;

namespace Labcoat;

// The HTML report's inline assets: the default theme's tokens, the report CSS and the filter script. The page's CSP
// allows exactly these two inline blocks (by hash) and nothing else: no external requests, no other script.
public static partial class HtmlReport
{
    /// <summary>The default theme (Catppuccin, ui/design-system/src/themes/catppuccin.css), embedded into Labcoat.dll.</summary>
    internal const string ThemeResource = "Labcoat.Export.theme.css";

    private const string LightSelector = """:root[data-theme-family="catppuccin"]""";
    private const string DarkSelector = """:root[data-theme-family="catppuccin"][data-theme="dark"]""";

    private static readonly Lazy<string> s_theme = new(() => ThemeTokens(ReadThemeResource()));

    /// <summary>The theme tokens for the report: light on :root, dark under prefers-color-scheme: dark.</summary>
    internal static string Theme => s_theme.Value;

    private static string? ReadThemeResource()
    {
        using var stream = typeof(HtmlReport).Assembly.GetManifestResourceStream(ThemeResource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The theme file with its selectors rewritten for a standalone page: the light block applies to <c>:root</c>, the
    /// dark block (the file's last rule) to <c>:root</c> inside <c>@media (prefers-color-scheme: dark)</c>. A file that
    /// does not have that shape gives <see cref="FallbackTokens"/>.
    /// </summary>
    internal static string ThemeTokens(string? css)
    {
        if (string.IsNullOrEmpty(css)) return FallbackTokens;
        var dark = css.IndexOf(DarkSelector, StringComparison.Ordinal);
        var light = css.IndexOf(LightSelector + " {", StringComparison.Ordinal);
        if (dark < 0 || light < 0 || light > dark || css.Contains("url(", StringComparison.OrdinalIgnoreCase) || css.Contains("@import", StringComparison.Ordinal))
            return FallbackTokens;
        var lightPart = ":root" + css[(light + LightSelector.Length)..dark]; // without the file's header comment
        var darkPart = ":root" + css[(dark + DarkSelector.Length)..];
        return "/* Labcoat's default theme: Catppuccin Latte / Macchiato palette (MIT, © Catppuccin) */\n" +
               $"{StripComments(lightPart).TrimEnd()}\n@media (prefers-color-scheme: dark) {{\n{StripComments(darkPart).TrimEnd()}\n}}\n";
    }

    /// <summary><paramref name="css"/> without /* comments */ (smaller; the theme's comments are notes for its maintainers).</summary>
    internal static string StripComments(string css)
    {
        var sb = new StringBuilder(css.Length);
        var at = 0;
        while (at < css.Length)
        {
            var start = css.IndexOf("/*", at, StringComparison.Ordinal);
            if (start < 0) break;
            var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            sb.Append(css, at, start - at);
            at = end < 0 ? css.Length : end + 2;
        }
        if (at < css.Length) sb.Append(css, at, css.Length - at);
        return sb.ToString();
    }

    /// <summary>A minimal light / dark palette with the token names the report uses (when the theme is not embedded).</summary>
    internal const string FallbackTokens = """
        :root {
          color-scheme: light;
          --lc-bg-page: #eff1f5; --lc-bg-surface: #f9fafb; --lc-bg-subtle: #e4e7ee; --lc-bg-hover: #e8ebf1;
          --lc-fg: #4c4f69; --lc-fg-secondary: #5c5f77; --lc-fg-muted: #5f6279; --lc-stroke: #bcc0cc; --lc-stroke-subtle: #ccd0da;
          --lc-accent: #8839ef; --lc-link: #1e66f5; --lc-focus-ring: #7287fd;
          --lc-status-passed: #3a8a2a; --lc-status-failed: #d20f39; --lc-status-skipped: #c25a14; --lc-status-muted: #7c7f93;
          --lc-status-passed-text: #2d6b21; --lc-status-failed-text: #b40d31; --lc-status-skipped-text: #9b4810;
          --lc-msg-error-bg: #f9e3e7; --lc-msg-error-border: #e8a3b1; --lc-msg-success-bg: #e6f1e3; --lc-msg-success-border: #a6cf9c;
          --lc-msg-warning-bg: #fbeadf; --lc-msg-warning-border: #f2b48f;
          --lc-console-bg: #f9fafb; --lc-console-fg: #4c4f69; --lc-console-border: #bcc0cc;
          --lc-code-addition: #238501; --lc-code-deletion: #d20f39;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            color-scheme: dark;
            --lc-bg-page: #24273a; --lc-bg-surface: #2a2d41; --lc-bg-subtle: #363a4f; --lc-bg-hover: #303347;
            --lc-fg: #cad3f5; --lc-fg-secondary: #b8c0e0; --lc-fg-muted: #a5adcb; --lc-stroke: #494d64; --lc-stroke-subtle: #363a4f;
            --lc-accent: #c6a0f6; --lc-link: #8aadf4; --lc-focus-ring: #b7bdf8;
            --lc-status-passed: #a6da95; --lc-status-failed: #ed8796; --lc-status-skipped: #f5a97f; --lc-status-muted: #8087a2;
            --lc-status-passed-text: #a6da95; --lc-status-failed-text: #ee96a3; --lc-status-skipped-text: #f5a97f;
            --lc-msg-error-bg: #3a2c3d; --lc-msg-error-border: #7a4655; --lc-msg-success-bg: #2e3a3a; --lc-msg-success-border: #4f7353;
            --lc-msg-warning-bg: #3b3237; --lc-msg-warning-border: #7d5b4a;
            --lc-console-bg: #1e2030; --lc-console-fg: #cad3f5; --lc-console-border: #494d64;
            --lc-code-addition: #a6da95; --lc-code-deletion: #ed8796;
          }
        }
        """;

    /// <summary>The report's own CSS (tokens from the theme; system fonts, nothing loaded).</summary>
    internal const string ReportCss = """
        :root {
          --font-sans: "Segoe UI Variable", "Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif;
          --font-mono: "Cascadia Mono", Consolas, ui-monospace, "SF Mono", Menlo, monospace;
        }
        * { box-sizing: border-box; }
        html { background: var(--lc-bg-page); color: var(--lc-fg); }
        body { margin: 0 auto; max-width: 1200px; padding: 24px 16px 48px; font: 14px/20px var(--font-sans); }
        h1, h2, h3 { margin: 0; font-weight: 600; }
        h1 { font-size: 22px; line-height: 28px; }
        h2 { font-size: 16px; line-height: 24px; margin: 28px 0 10px; }
        a { color: var(--lc-link); }
        :focus-visible { outline: 2px solid var(--lc-focus-ring); outline-offset: 2px; }
        .muted { color: var(--lc-fg-muted); }
        .mono, pre, code { font-family: var(--font-mono); font-size: 13px; }
        .top { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 12px; }
        .brand { color: var(--lc-accent); font-weight: 700; letter-spacing: 0.04em; font-size: 12px; text-transform: uppercase; }
        .meta { margin: 6px 0 0; color: var(--lc-fg-secondary); font-size: 13px; }
        .banner { margin: 16px 0; padding: 10px 14px; border: 1px solid; border-radius: 8px; font-weight: 600; }
        .banner.ok { background: var(--lc-msg-success-bg); border-color: var(--lc-msg-success-border); }
        .banner.bad { background: var(--lc-msg-error-bg); border-color: var(--lc-msg-error-border); }
        .banner.warn { background: var(--lc-msg-warning-bg); border-color: var(--lc-msg-warning-border); }
        .banner .muted { font-weight: 400; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(130px, 1fr)); gap: 10px; margin: 0; padding: 0; }
        .card { padding: 10px 14px; border: 1px solid var(--lc-stroke-subtle); border-radius: 8px; background: var(--lc-bg-surface); }
        .card dt { color: var(--lc-fg-muted); font-size: 12px; }
        .card dd { margin: 2px 0 0; font-size: 22px; line-height: 28px; font-weight: 600; font-variant-numeric: tabular-nums; }
        .c-f { color: var(--lc-status-failed-text); }
        .c-p { color: var(--lc-status-passed-text); }
        .c-s { color: var(--lc-status-skipped-text); }
        .toolbar { position: sticky; top: 0; z-index: 1; display: flex; flex-wrap: wrap; align-items: center; gap: 8px 14px;
          margin: 20px -16px 0; padding: 10px 16px; background: var(--lc-bg-page); border-bottom: 1px solid var(--lc-stroke-subtle); }
        .toolbar input[type=search], .toolbar select, .toolbar button { font: inherit; color: var(--lc-fg); background: var(--lc-bg-surface);
          border: 1px solid var(--lc-stroke); border-radius: 6px; height: 30px; padding: 0 8px; }
        .toolbar input[type=search] { flex: 1 1 220px; min-width: 0; }
        .toolbar button { cursor: pointer; }
        .toolbar button:hover { background: var(--lc-bg-hover); }
        .toolbar label { display: inline-flex; align-items: center; gap: 4px; white-space: nowrap; }
        .outcomes { display: contents; }
        .toolbar .shown { margin-left: auto; color: var(--lc-fg-muted); font-size: 13px; }
        .visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
        details > summary { cursor: pointer; list-style: none; }
        details > summary::-webkit-details-marker { display: none; }
        details > summary::before { content: "\25B8"; display: inline-block; width: 1em; color: var(--lc-fg-muted); transition: transform 0.1s; }
        details[open] > summary::before { transform: rotate(90deg); }
        .fail { margin: 0 0 10px; border: 1px solid var(--lc-msg-error-border); border-radius: 8px; background: var(--lc-bg-surface); }
        .fail > summary { padding: 8px 12px; display: flex; flex-wrap: wrap; gap: 2px 10px; align-items: baseline; }
        .fail > summary .n { font-weight: 600; overflow-wrap: anywhere; }
        .fail .body { padding: 0 12px 12px; }
        .label { margin: 10px 0 4px; color: var(--lc-fg-muted); font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
        pre { margin: 0; padding: 8px 10px; max-height: 360px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere;
          background: var(--lc-console-bg); color: var(--lc-console-fg); border: 1px solid var(--lc-console-border); border-radius: 6px; }
        pre.msg { border-color: var(--lc-msg-error-border); background: var(--lc-msg-error-bg); color: var(--lc-fg); }
        .diff { padding: 4px 0; }
        .diff span { display: block; padding: 0 10px; }
        .diff .del { color: var(--lc-code-deletion); background: color-mix(in srgb, var(--lc-code-deletion) 10%, transparent); }
        .diff .add { color: var(--lc-code-addition); background: color-mix(in srgb, var(--lc-code-addition) 10%, transparent); }
        .proj { margin: 0 0 12px; border: 1px solid var(--lc-stroke-subtle); border-radius: 8px; background: var(--lc-bg-surface); }
        .proj > summary { padding: 10px 12px; font-weight: 600; }
        .proj > summary .muted, .cls > summary .muted { font-weight: 400; font-size: 13px; }
        .cls { border-top: 1px solid var(--lc-stroke-subtle); }
        .cls > summary { padding: 6px 12px 6px 24px; overflow-wrap: anywhere; }
        .cls > summary:hover, .r:hover { background: var(--lc-bg-hover); }
        .rows { list-style: none; margin: 0; padding: 0 0 6px; }
        .r { display: flex; gap: 10px; align-items: baseline; padding: 2px 12px 2px 46px; }
        .r .n { flex: 1 1 auto; min-width: 0; overflow-wrap: anywhere; }
        .r .d, .fail .d, .cls .d { margin-left: auto; color: var(--lc-fg-muted); font-size: 12px; font-variant-numeric: tabular-nums; white-space: nowrap; }
        .r .why { color: var(--lc-fg-muted); font-size: 12px; overflow-wrap: anywhere; }
        .r::before, .fail > summary .n::before { display: inline-block; width: 1.1em; font-weight: 700; }
        .r.f::before, .fail > summary .n::before { content: "\2715"; color: var(--lc-status-failed); }
        .r.p::before { content: "\2713"; color: var(--lc-status-passed); }
        .r.s::before { content: "\2212"; color: var(--lc-status-skipped); }
        .note { color: var(--lc-fg-muted); font-size: 13px; }
        footer { margin-top: 32px; color: var(--lc-fg-muted); font-size: 12px; }
        [hidden] { display: none !important; }
        @media print { .toolbar { display: none; } details { break-inside: avoid; } }
        """;

    /// <summary>
    /// Filters (search text, outcome, project), shown count, expand / collapse all. Rows carry <c>data-b</c> (f / p / s),
    /// classes <c>data-g</c> (their name), projects <c>data-p</c>. Reads and hides DOM only: no data is embedded in it.
    /// </summary>
    internal const string ReportJs = """
        (function () {
          "use strict";
          var q = document.getElementById("q");
          var proj = document.getElementById("proj");
          var shown = document.getElementById("shown");
          var boxes = Array.prototype.slice.call(document.querySelectorAll("input[data-bucket]"));
          var items = Array.prototype.slice.call(document.querySelectorAll("[data-b]"));
          var groups = Array.prototype.slice.call(document.querySelectorAll("[data-g], .proj, .fails"));
          var total = document.querySelectorAll("li[data-b]").length;
          var info = new Map();
          function meta(el) {
            var m = info.get(el);
            if (!m) {
              var g = el.closest("[data-g]");
              var p = el.closest("[data-p]");
              m = { text: ((g ? g.getAttribute("data-g") : "") + " " + el.textContent).toLowerCase(), project: p ? p.getAttribute("data-p") : "" };
              info.set(el, m);
            }
            return m;
          }
          function apply() {
            var text = q.value.trim().toLowerCase();
            var project = proj ? proj.value : "";
            var on = {};
            boxes.forEach(function (b) { on[b.getAttribute("data-bucket")] = b.checked; });
            var count = 0;
            items.forEach(function (el) {
              var m = meta(el);
              var ok = on[el.getAttribute("data-b")] && (!project || m.project === project) && (!text || m.text.indexOf(text) >= 0);
              el.hidden = !ok;
              if (ok && el.tagName === "LI") count++;
            });
            groups.forEach(function (g) {
              g.hidden = !g.querySelector("[data-b]:not([hidden])");
              if (text && !g.hidden && g.tagName === "DETAILS") g.open = true;
            });
            shown.textContent = count === total ? total + " tests" : count + " of " + total + " tests shown";
          }
          function setAll(open) {
            document.querySelectorAll("details").forEach(function (d) { d.open = open; });
          }
          q.addEventListener("input", apply);
          if (proj) proj.addEventListener("change", apply);
          boxes.forEach(function (b) { b.addEventListener("change", apply); });
          document.getElementById("expand").addEventListener("click", function () { setAll(true); });
          document.getElementById("collapse").addEventListener("click", function () { setAll(false); });
          apply();
        })();
        """;

    /// <summary>The CSP hash source of an inline block ('sha256-…'), as browsers compute it (over LF line endings).</summary>
    internal static string CspHash(string body) => $"'sha256-{SecurityHeaders.ScriptHash(body)}'";

    /// <summary>
    /// The page's Content-Security-Policy: nothing may load (default-src 'none'), only the report's own inline style
    /// and script run (by hash), no forms, no base URL. Escaped test output can't add markup, and if it ever could, it
    /// could not run script or call home.
    /// </summary>
    internal static string ContentSecurityPolicy(string style, string script) =>
        $"default-src 'none'; style-src {CspHash(style)}; script-src {CspHash(script)}; img-src data:; base-uri 'none'; form-action 'none'";
}
