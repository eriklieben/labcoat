namespace Labcoat;

/// <summary>
/// A shareable quarantine list (<c>--quarantine-file</c>, "quarantineFile" in .labcoat.json): one test per line by its
/// stable name, committed to the repository. A line is the whole, case-insensitive name of a test — "Namespace.Class.Method"
/// (all data rows), "Namespace.Class.Display name (1,2)" (one row) or "Namespace.Class" (every test of the class) — and
/// may use '*' as a wildcard. Empty lines and lines starting with '#' are ignored.
/// Quarantined tests behave as the ones quarantined in the UI (they run, are counted separately and do not fail the run),
/// in addition to them. The file is only ever read: the UI cannot release a test it lists, edit the file instead.
/// </summary>
public sealed class QuarantineFile
{
    private readonly List<(string Pattern, string[]? Parts)> _entries;

    private QuarantineFile(string path, List<(string Pattern, string[]? Parts)> entries)
    {
        Path = path;
        _entries = entries;
    }

    /// <summary>The full path of the file.</summary>
    public string Path { get; }
    public int Count => _entries.Count;

    /// <summary>Reads the file; null + <paramref name="error"/> when it does not exist or cannot be read.</summary>
    public static QuarantineFile? Load(string path, out string? error)
    {
        error = null;
        var full = System.IO.Path.GetFullPath(path);
        try
        {
            return Parse(full, File.ReadAllLines(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex is FileNotFoundException or DirectoryNotFoundException
                ? $"The quarantine file {path} does not exist."
                : $"The quarantine file {path} could not be read: {ex.Message}";
            return null;
        }
    }

    internal static QuarantineFile Parse(string path, IEnumerable<string> lines)
    {
        var entries = new List<(string Pattern, string[]? Parts)>();
        foreach (var line in lines)
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry[0] == '#') continue;
            entries.Add((entry, entry.Contains('*') ? entry.Split('*') : null));
        }
        return new QuarantineFile(path, entries);
    }

    /// <summary>The test is listed: one of its names ("Namespace.Class", "….Method", "….Display name") is an entry.</summary>
    public bool Matches(TestNode t)
    {
        if (_entries.Count == 0) return false;
        foreach (var name in Names(t))
            foreach (var (pattern, parts) in _entries)
                if (parts is null ? name.Equals(pattern, StringComparison.OrdinalIgnoreCase) : CliRun.WildcardMatch(name, parts))
                    return true;
        return false;
    }

    private static IEnumerable<string> Names(TestNode t)
    {
        var cls = string.Join('.', new[] { t.Namespace, t.ClassName }.Where(s => !string.IsNullOrEmpty(s)));
        if (cls.Length > 0) yield return cls;
        var method = t.MethodName ?? t.DisplayName;
        yield return cls.Length > 0 ? $"{cls}.{method}" : method;
        if (t.DisplayName != method) yield return cls.Length > 0 ? $"{cls}.{t.DisplayName}" : t.DisplayName;
    }
}
