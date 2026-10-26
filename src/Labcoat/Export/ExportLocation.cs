using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// Where a failed test points to in the source (workflow annotations, SARIF, the Markdown report): the first stack frame
/// with a file and line inside the workspace (the assertion, not the framework), else the test's own declaration.
/// </summary>
internal static partial class ExportLocation
{
    // ".NET stack frames: "at Ns.Class.Method() in /src/File.cs:line 42" (the path may contain spaces and a drive colon).
    [GeneratedRegex(@" in (?<file>.+?):line (?<line>\d+)\s*$", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex FrameRe();

    /// <summary>The source file ('/' separators, relative to <paramref name="root"/> when it is under it) and line, or nulls.</summary>
    public static (string? File, int? Line) Of(string? stackTrace, string? declaredFile, int? declaredLine, string? root)
    {
        if (!string.IsNullOrEmpty(stackTrace))
        {
            try
            {
                foreach (Match m in FrameRe().Matches(stackTrace))
                {
                    var file = m.Groups["file"].Value.Trim();
                    if (!int.TryParse(m.Groups["line"].Value, out var line) || line <= 0) continue;
                    if (root is not null ? Under(file, root) : Path.IsPathRooted(file)) return (Normalize(file, root), line);
                }
            }
            catch (RegexMatchTimeoutException) { /* fall back to the declaration */ }
        }
        if (string.IsNullOrEmpty(declaredFile)) return (null, null);
        return (Normalize(declaredFile, root), declaredLine is > 0 ? declaredLine : null);
    }

    private static bool Under(string file, string root)
    {
        try { return Path.IsPathRooted(file) && PathUtil.IsUnder(Path.GetFullPath(file), Path.GetFullPath(root)); }
        catch (ArgumentException) { return false; }
    }

    private static string Normalize(string file, string? root)
    {
        var path = root is not null && Under(file, root) ? Path.GetRelativePath(root, file) : file;
        return path.Replace('\\', '/');
    }
}
