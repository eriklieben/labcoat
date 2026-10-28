namespace Labcoat;

/// <summary>Content fingerprints of a test app's output directory: what tells a warm test host that its binaries are outdated.</summary>
internal static class OutputFingerprints
{
    /// <summary>
    /// Content fingerprint of the test app's output directory (every top-level dll/json). Covers the test assembly and
    /// copied ProjectReference outputs, which change without the test dll being rewritten (reference assemblies keep the
    /// test project's compile up to date). Content-based, so a rebuild that rewrites identical bytes (deterministic
    /// compile after a touch) keeps the warm host. File hashes are cached by (path, size, write time).
    /// </summary>
    public static string Of(string targetPath) => FingerprintOf(OutputHashes(targetPath));

    public static string FingerprintOf(IReadOnlyDictionary<string, long> hashes)
    {
        var hash = new HashCode();
        foreach (var (name, h) in hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            hash.Add(name);
            hash.Add(h);
        }
        return hash.ToHashCode().ToString("x8");
    }

    /// <summary>Content hash of every top-level dll/json of the test app's output directory, by file name.</summary>
    public static Dictionary<string, long> OutputHashes(string targetPath)
    {
        var hashes = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(targetPath)!);
            foreach (var f in dir.EnumerateFiles("*.dll").Concat(dir.EnumerateFiles("*.json")))
                hashes[f.Name] = ContentHash(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial hashes: a later compare sees a change and renews */ }
        return hashes;
    }

    /// <summary>Content hash of one file of the output directory (cached by size + write time).</summary>
    public static long ContentHash(string path) => ContentHash(new FileInfo(path));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Ticks, long Hash)> FileHashes = new();

    private static long ContentHash(FileInfo f)
    {
        var (length, ticks) = (f.Length, f.LastWriteTimeUtc.Ticks);
        if (FileHashes.TryGetValue(f.FullName, out var c) && c.Length == length && c.Ticks == ticks) return c.Hash;
        long h;
        try
        {
            using var stream = f.OpenRead();
            h = BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(stream), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { h = ticks ^ length; }
        FileHashes[f.FullName] = (length, ticks, h);
        return h;
    }
}
