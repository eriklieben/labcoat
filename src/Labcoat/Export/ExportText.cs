using System.Text;
using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// Text helpers shared by the export writers (JUnit XML, HTML report, lcov, Cobertura): test names, messages and console
/// output are arbitrary text, so ANSI escape sequences are stripped, characters that XML 1.0 / HTML cannot carry are
/// replaced, long output is cut to its head and tail, and HTML special characters are escaped.
/// </summary>
internal static partial class ExportText
{
    /// <summary>What replaces a character that XML 1.0 cannot carry (control characters, lone surrogates).</summary>
    public const char Replacement = '\uFFFD';

    // CSI (ESC [ … final byte), OSC (ESC ] … BEL or ESC \) and the two-byte escapes.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex AnsiRe();

    /// <summary><paramref name="s"/> without ANSI escape sequences (colors, cursor movement, hyperlinks).</summary>
    public static string StripAnsi(string s)
    {
        if (s.IndexOf('\x1B') < 0) return s;
        try { return AnsiRe().Replace(s, ""); }
        catch (RegexMatchTimeoutException) { return s; } // the ESC characters are replaced by Clean() instead
    }

    /// <summary>
    /// ANSI sequences stripped, and every character XML 1.0 cannot carry (C0 controls other than tab / LF / CR, U+FFFE,
    /// U+FFFF, lone surrogates) replaced with U+FFFD. Safe for XML text and attributes and for HTML.
    /// </summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = StripAnsi(s);
        StringBuilder? sb = null;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb?.Append(c).Append(s[i + 1]);
                i += 2; // the pair as a whole
                continue;
            }
            var ok = !char.IsSurrogate(c) && (c >= ' ' ? c is not ('\uFFFE' or '\uFFFF') : c is '\t' or '\n' or '\r');
            if (ok)
            {
                sb?.Append(c);
            }
            else
            {
                sb ??= new StringBuilder(s.Length).Append(s, 0, i);
                sb.Append(Replacement);
            }
            i++;
        }
        return sb?.ToString() ?? s;
    }

    /// <summary>
    /// <paramref name="s"/> cut to about <paramref name="max"/> characters: the first quarter and the last three quarters
    /// with a marker line between them (the end of output usually says why a test failed). Never splits a surrogate pair.
    /// </summary>
    public static string Cap(string s, int max)
    {
        if (s.Length <= max || max <= 0) return s;
        var head = max / 4;
        var tailStart = s.Length - (max - head);
        if (head > 0 && char.IsHighSurrogate(s[head - 1])) head--;
        if (tailStart < s.Length && char.IsLowSurrogate(s[tailStart])) tailStart++;
        var omitted = tailStart - head;
        return string.Concat(s.AsSpan(0, head), $"\n… [{omitted:N0} characters omitted by Labcoat] …\n", s.AsSpan(tailStart));
    }

    /// <summary>The first line of <paramref name="s"/> (trimmed), at most <paramref name="max"/> characters.</summary>
    public static string FirstLine(string? s, int max = 300)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var nl = s.IndexOfAny(['\r', '\n']);
        var line = (nl >= 0 ? s[..nl] : s).Trim();
        return line.Length <= max ? line : string.Concat(line.AsSpan(0, max), "…");
    }

    /// <summary>
    /// <paramref name="s"/> as HTML text or attribute value: cleaned (<see cref="Clean"/>), then <c>&amp; &lt; &gt; " '</c>
    /// escaped. Every piece of test data in the HTML report goes through this.
    /// </summary>
    public static string Html(string? s)
    {
        var clean = Clean(s);
        if (clean.AsSpan().IndexOfAny("&<>\"'") < 0) return clean;
        var sb = new StringBuilder(clean.Length + 16);
        foreach (var c in clean)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// A file-name part: lower-case letters, digits, '.', '_' and '-' (anything else becomes '-'), at most 60 characters;
    /// <paramref name="fallback"/> when nothing is left.
    /// </summary>
    public static string Slug(string? s, string fallback = "workspace")
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var sb = new StringBuilder(Math.Min(s.Length, 60));
        foreach (var c in s.Trim().ToLowerInvariant())
        {
            if (sb.Length >= 60) break;
            var keep = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (keep) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-', '.');
        return slug.Length == 0 ? fallback : slug;
    }
}
