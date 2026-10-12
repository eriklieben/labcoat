namespace Labcoat;

public enum SourceAccessResult { Ok, Forbidden, NotFound }

public readonly record struct SourceAccess(SourceAccessResult Result, string? FullPath);

public static class PathUtil
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>GetFullPath + resolve symlinks on every existing path component (like realpath).</summary>
    public static string Canonicalize(string path) => Canonicalize(path, 0);

    // A link's target may itself contain links (a symlinked checkout inside /var -> /private/var): resolve it again.
    private static string Canonicalize(string path, int depth)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) current = depth < 32 ? Canonicalize(target.FullName, depth + 1) : Path.GetFullPath(target.FullName);
            }
        }
        return current;
    }

    public static string CanonicalizeOrSelf(string path)
    {
        try { return Canonicalize(path); } catch { return path; }
    }

    public static bool IsUnder(string path, string root)
    {
        var r = Path.TrimEndingDirectorySeparator(root);
        if (r.Length == 0 || r == Path.GetPathRoot(r)?.TrimEnd(Path.DirectorySeparatorChar)) return false; // never allow "/"
        return path.StartsWith(r + Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary>Common ancestor directory of the given paths (files contribute their directory).</summary>
    public static string? CommonRoot(IEnumerable<string> paths)
    {
        string[]? common = null;
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
            var parts = Path.TrimEndingDirectorySeparator(dir).Split(Path.DirectorySeparatorChar);
            if (common is null) { common = parts; continue; }
            var i = 0;
            while (i < common.Length && i < parts.Length && string.Equals(common[i], parts[i], Comparison)) i++;
            common = common[..i];
        }
        if (common is null || common.Length == 0) return null;
        var joined = string.Join(Path.DirectorySeparatorChar, common);
        return joined.Length == 0 ? null : joined;
    }
}
