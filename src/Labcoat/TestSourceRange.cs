using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// The lines a test method spans in its C# source file, for "changed tests" (GitAffected.cs). MSTest and xUnit report one
/// line per test (the attribute or the declaration), NUnit none; only TUnit reports a real range. This finds the method in
/// the file (from the reported line, else by name), extends it up over its attribute lines and brace-matches its body
/// (skipping strings, chars and comments). Files are cached by modification time; a method it can't delimit gives null.
/// </summary>
internal static class TestSourceRange
{
    private const int MaxScanLines = 3000;
    private const int MaxCachedFiles = 256;

    private sealed record Source(DateTime Written, long Length, string[] Lines);

    private static readonly ConcurrentDictionary<string, Source> Cache = new(PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    /// <summary>First and last line (1-based, inclusive) of the test's method; null when the file isn't C# or the method can't be delimited.</summary>
    public static (int Start, int End)? Find(string? file, int? line, string? methodName)
    {
        if (file is null || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;
        var lines = Read(file);
        if (lines is null) return null;
        var from = line is > 0 && line <= lines.Length ? line.Value : FindDeclaration(lines, methodName);
        if (from is null) return null;
        var end = BodyEnd(lines, from.Value);
        if (end is null) return null;
        var start = from.Value;
        while (start > 1 && lines[start - 2].TrimStart().StartsWith('[')) start--; // attributes above ([Test], [TestCase(…)], …)
        return (start, end.Value);
    }

    private static string[]? Read(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            if (Cache.TryGetValue(file, out var hit) && hit.Written == info.LastWriteTimeUtc && hit.Length == info.Length) return hit.Lines;
            if (Cache.Count >= MaxCachedFiles) Cache.Clear();
            var lines = File.ReadAllLines(file);
            Cache[file] = new Source(info.LastWriteTimeUtc, info.Length, lines);
            return lines;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static readonly HashSet<string> NotATypeName = new(["await", "return", "new", "throw", "else", "case", "yield", "in", "is", "as"], StringComparer.Ordinal);

    /// <summary>The line declaring <paramref name="methodName"/> ("Name" or "Name(1, 2)" for data rows); null when unknown.</summary>
    private static int? FindDeclaration(string[] lines, string? methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName)) return null;
        var name = methodName[..(methodName.IndexOf('(') is var p and >= 0 ? p : methodName.Length)].Trim();
        name = name[(name.LastIndexOf('.') + 1)..];
        if (name.Length == 0) return null;
        var rx = new Regex(@"(?<![\w.])(?<type>[\w<>\[\]?,.]+)\s+" + Regex.Escape(name) + @"\s*(?:<[^>(]*>)?\s*\(", RegexOptions.CultureInvariant, RegexTimeout.Value);
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i];
            if (text.TrimStart().StartsWith("//", StringComparison.Ordinal) || !text.Contains(name, StringComparison.Ordinal)) continue;
            var m = rx.MatchOrEmpty(text);
            if (m.Success && !NotATypeName.Contains(m.Groups["type"].Value)) return i + 1;
        }
        return null;
    }

    /// <summary>The line where the body of the member declared at <paramref name="startLine"/> ends: the matching "}", or the ";" of an expression body.</summary>
    private static int? BodyEnd(string[] lines, int startLine)
    {
        var scan = new Scanner();
        var depth = 0;
        var opened = false;
        var last = Math.Min(lines.Length, startLine + MaxScanLines);
        for (var i = startLine - 1; i < last; i++)
        {
            foreach (var c in scan.Code(lines[i] + "\n"))
            {
                switch (c)
                {
                    case '(' or '[': if (!opened) scan.Nest++; break;
                    case ')' or ']': if (!opened) scan.Nest--; break;
                    case '{' when opened || scan.Nest <= 0:
                        opened = true;
                        depth++;
                        break;
                    case '}' when opened:
                        if (--depth == 0) return i + 1;
                        break;
                    case ';' when !opened && scan.Nest <= 0:
                        return i + 1; // expression-bodied member
                }
            }
        }
        return null;
    }

    /// <summary>Yields the characters of C# source that are code (not in strings, chars or comments), across lines.</summary>
    private sealed class Scanner
    {
        private enum Mode { Code, Line, Block, Str, Verbatim, Raw, Char }
        private Mode _mode;
        private int _raw; // quotes of a raw string literal's delimiter

        public int Nest { get; set; }

        public IEnumerable<char> Code(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                var next = i + 1 < s.Length ? s[i + 1] : '\0';
                switch (_mode)
                {
                    case Mode.Line:
                        if (c == '\n') _mode = Mode.Code;
                        break;
                    case Mode.Block:
                        if (c == '*' && next == '/') { _mode = Mode.Code; i++; }
                        break;
                    case Mode.Str:
                        if (c == '\\') i++;
                        else if (c == '"' || c == '\n') _mode = Mode.Code;
                        break;
                    case Mode.Verbatim:
                        if (c == '"' && next == '"') i++;
                        else if (c == '"') _mode = Mode.Code;
                        break;
                    case Mode.Raw:
                        if (c == '"' && Quotes(s, i) is var closing && closing >= _raw) { i += closing - 1; _mode = Mode.Code; }
                        break;
                    case Mode.Char:
                        if (c == '\\') i++;
                        else if (c == '\'' || c == '\n') _mode = Mode.Code;
                        break;
                    default:
                        if (c == '/' && next == '/') _mode = Mode.Line;
                        else if (c == '/' && next == '*') { _mode = Mode.Block; i++; }
                        else if (c == '"')
                        {
                            var q = Quotes(s, i);
                            if (q >= 3) { _mode = Mode.Raw; _raw = q; i += q - 1; }
                            else _mode = i > 0 && s[i - 1] == '@' || i > 1 && s[i - 1] == '$' && s[i - 2] == '@' ?Mode.Verbatim : Mode.Str;
                        }
                        else if (c == '\'') _mode = Mode.Char;
                        else yield return c;
                        break;
                }
            }
        }

        private static int Quotes(string s, int i)
        {
            var n = 0;
            while (i + n < s.Length && s[i + n] == '"') n++;
            return n;
        }
    }
}
