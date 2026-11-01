using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// The lines a test method spans in its C# source file, for "changed tests" (AffectedPlanner.cs). MSTest and xUnit report one
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
        var body = new Body();
        var last = Math.Min(lines.Length, startLine + MaxScanLines);
        for (var i = startLine - 1; i < last; i++)
        {
            if (scan.Code(lines[i] + "\n").Any(body.EndsAt)) return i + 1;
        }
        return null;
    }

    /// <summary>The brackets and braces of a member's code, character by character: where its body ends.</summary>
    private sealed class Body
    {
        private int _nest; // ( and [ before the body: parameter lists, attributes, default values
        private int _depth;
        private bool _opened;

        /// <summary>True at the "}" that closes the body, or at the ";" of an expression body.</summary>
        public bool EndsAt(char c)
        {
            switch (c)
            {
                case '(' or '[':
                    if (!_opened) _nest++;
                    return false;
                case ')' or ']':
                    if (!_opened) _nest--;
                    return false;
                case '{' when _opened || _nest <= 0:
                    _opened = true;
                    _depth++;
                    return false;
                case '}' when _opened:
                    return --_depth == 0;
                case ';':
                    return !_opened && _nest <= 0;
                default:
                    return false;
            }
        }
    }

    /// <summary>Yields the characters of C# source that are code (not in strings, chars or comments), across lines.</summary>
    private sealed class Scanner
    {
        private enum Mode { Code, Line, Block, Str, Verbatim, Raw, Char }
        private Mode _mode;
        private int _raw; // quotes of a raw string literal's delimiter

        public IEnumerable<char> Code(string s)
        {
            var i = 0;
            while (i < s.Length)
            {
                if (_mode != Mode.Code)
                {
                    i += InLiteral(s, i);
                    continue;
                }
                var (length, code) = InCode(s, i);
                if (code) yield return s[i];
                i += length;
            }
        }

        /// <summary>At a character of code: it starts a comment or a literal, or it is code. Returns how far to move on.</summary>
        private (int Length, bool Code) InCode(string s, int i)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && next == '/') { _mode = Mode.Line; return (1, false); }
            if (c == '/' && next == '*') { _mode = Mode.Block; return (2, false); }
            if (c == '\'') { _mode = Mode.Char; return (1, false); }
            if (c != '"') return (1, true);
            var quotes = Quotes(s, i);
            if (quotes >= 3)
            {
                _mode = Mode.Raw;
                _raw = quotes;
                return (quotes, false);
            }
            var verbatim = i > 0 && s[i - 1] == '@' || i > 1 && s[i - 1] == '$' && s[i - 2] == '@';
            _mode = verbatim ? Mode.Verbatim : Mode.Str;
            return (1, false);
        }

        /// <summary>At a character in a comment or literal: how far to move on; at its end the mode goes back to code.</summary>
        private int InLiteral(string s, int i)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            return _mode switch
            {
                Mode.Line => InLineComment(c),
                Mode.Block => InBlockComment(c, next),
                Mode.Str or Mode.Char => InQuoted(c),
                Mode.Verbatim => InVerbatim(c, next),
                _ => InRaw(s, i),
            };
        }

        private int InLineComment(char c)
        {
            if (c == '\n') _mode = Mode.Code;
            return 1;
        }

        private int InBlockComment(char c, char next)
        {
            if (c != '*' || next != '/') return 1;
            _mode = Mode.Code;
            return 2;
        }

        /// <summary>In a regular string or a char literal.</summary>
        private int InQuoted(char c)
        {
            if (c == '\\') return 2;
            if (c == '\n' || c == (_mode == Mode.Str ? '"' : '\'')) _mode = Mode.Code;
            return 1;
        }

        private int InVerbatim(char c, char next)
        {
            if (c == '"' && next == '"') return 2;
            if (c == '"') _mode = Mode.Code;
            return 1;
        }

        private int InRaw(string s, int i)
        {
            if (s[i] != '"' || Quotes(s, i) is var closing && closing < _raw) return 1;
            _mode = Mode.Code;
            return closing;
        }

        private static int Quotes(string s, int i)
        {
            var n = 0;
            while (i + n < s.Length && s[i + n] == '"') n++;
            return n;
        }
    }
}
