namespace Labcoat;

/// <summary>
/// Expected / actual values from an assertion message and a line diff of them, for the HTML report (a smaller cousin
/// of the UI's ui/src/diff.ts). Recognizes MSTest's <c>Expected:&lt;X&gt;. Actual:&lt;Y&gt;.</c> and the line forms
/// <c>Expected: X</c> followed by <c>Actual: Y</c> (xUnit, MSTest 4) or <c>But was: Y</c> (NUnit).
/// </summary>
internal static class AssertionDiff
{
    /// <summary>Values longer than this many lines are not diffed (the message is shown as it is).</summary>
    public const int MaxLines = 400;

    public sealed record Values(string Expected, string Actual);

    /// <summary>One line of the diff: ' ' (both), '-' (expected only) or '+' (actual only).</summary>
    public readonly record struct DiffLine(char Op, string Text);

    private const string ObjectExpected = "Expected:<";
    private const string ObjectActual = ">. Actual:<";

    public static Values? Parse(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var msg = message.Replace("\r\n", "\n");
        return ParseObjectForm(msg) ?? ParseLineForm(msg);
    }

    private static Values? ParseObjectForm(string msg)
    {
        var e = msg.IndexOf(ObjectExpected, StringComparison.Ordinal);
        if (e < 0) return null;
        var sep = msg.IndexOf(ObjectActual, e, StringComparison.Ordinal);
        if (sep < 0) return null;
        var aStart = sep + ObjectActual.Length;
        var aEnd = msg.IndexOf(">.", aStart, StringComparison.Ordinal);
        if (aEnd < 0) return null;
        return new Values(msg[(e + ObjectExpected.Length)..sep], msg[aStart..aEnd]);
    }

    private static Values? ParseLineForm(string msg)
    {
        var lines = msg.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var expected = ValueAfter(lines[i], "Expected:");
            if (expected is null) continue;
            for (var j = i + 1; j < lines.Length && j <= i + 3; j++)
            {
                var actual = ValueAfter(lines[j], "Actual:") ?? ValueAfter(lines[j], "But was:");
                if (actual is not null) return new Values(Unquote(expected), Unquote(actual));
            }
        }
        return null;
    }

    private static string? ValueAfter(string line, string label)
    {
        var t = line.TrimStart();
        return t.StartsWith(label, StringComparison.Ordinal) ? t[label.Length..].Trim() : null;
    }

    private static string Unquote(string v) => v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;

    /// <summary>A line diff (longest common subsequence) of the two values; null when either has more than <see cref="MaxLines"/> lines.</summary>
    public static List<DiffLine>? Lines(string expected, string actual)
    {
        var a = expected.Replace("\r\n", "\n").Split('\n');
        var b = actual.Replace("\r\n", "\n").Split('\n');
        if (a.Length > MaxLines || b.Length > MaxLines) return null;
        // lcs[i, j] = length of the LCS of a[i..] and b[j..]
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var result = new List<DiffLine>(a.Length + b.Length);
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(' ', a[x++]));
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add(new DiffLine('-', a[x++]));
            else result.Add(new DiffLine('+', b[y++]));
        }
        while (x < a.Length) result.Add(new DiffLine('-', a[x++]));
        while (y < b.Length) result.Add(new DiffLine('+', b[y++]));
        return result;
    }
}
