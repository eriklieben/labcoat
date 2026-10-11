using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Labcoat;

// Snapshot testing with Verify (https://github.com/VerifyTests/Verify): a failed Verify assertion leaves
// "<Class>.<Method>[_<params>].received.<ext>" next to "<…>.verified.<ext>" (the approved snapshot, committed) and
// throws a VerifyException whose message names both files. Labcoat finds those files for a failed test, serves them
// to the UI's diff view and accepts (received -> verified) or rejects (deletes received) them. See
// docs/contract.md "Round 6" and docs/features.md "Snapshot testing".

[JsonConverter(typeof(JsonStringEnumConverter<SnapshotKind>))]
public enum SnapshotKind
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("binary")] Binary,
}

/// <summary>One pending snapshot of a test: a received file waiting to replace (or create) its verified file.</summary>
/// <param name="Name">The verified file name, e.g. "ReceiptTests.Render.verified.txt" (what accept creates; selects it in accept/reject).</param>
/// <param name="IsNew">There is no verified file yet (Verify's "New:"): accepting creates it.</param>
public sealed record SnapshotFile(string Name, string ReceivedPath, string VerifiedPath, SnapshotKind Kind, bool IsNew);

/// <summary>GET /api/tests/snapshots: the pending snapshots of one test.</summary>
/// <param name="Directory">The snapshot directory Verify reported (or the test file's directory).</param>
public sealed record TestSnapshots(string ProjectId, string Id, string? Directory, List<SnapshotFile> Snapshots);

/// <summary>POST /api/snapshots/accept and /reject body. No <c>Id</c> = every test with a snapshot mismatch (of <c>ProjectId</c>).</summary>
/// <param name="Names">Only these snapshots (SnapshotFile.Name); null = all of the test's.</param>
/// <param name="Rerun">Accept only: run the tests again afterwards (default true).</param>
public sealed record SnapshotRequest(string? ProjectId, string? Id, List<string>? Names, bool? Rerun);

/// <summary>What accept / reject did, per test.</summary>
public sealed record SnapshotTestResult(string ProjectId, string Id, List<string> Files);
public sealed record SnapshotError(string ProjectId, string Id, string? Name, string Error);
public sealed record SnapshotActionResult(List<SnapshotTestResult> Tests, List<SnapshotError>? Errors, string? RunId);

/// <summary>Verify conventions: the exception message, file names, kinds, the test host environment.</summary>
public static partial class VerifySnapshots
{
    /// <summary>DiffEngine (Verify's diff tool launcher) reads this: "true" = never open a diff tool.</summary>
    public const string DiffEngineDisabledEnvVar = "DiffEngine_Disabled";

    public const string ReceivedMarker = ".received.";
    public const string VerifiedMarker = ".verified.";

    /// <summary>Largest snapshot file GET /api/snapshots/file serves.</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".avif" };

    /// <summary>
    /// Test hosts started by Labcoat never launch an external diff tool (Verify would open one per mismatch on a
    /// developer machine); the snapshot diff is shown in the UI instead.
    /// </summary>
    public static void ConfigureTestHost(IDictionary<string, string?> env) => env[DiffEngineDisabledEnvVar] = "true";

    // "Directory: /path/to/tests\nNotEqual:\n  - Received: …" (MSTest prefixes "Test method X threw exception:\nVerifyException: ").
    [GeneratedRegex(@"Directory:[ \t]*(?<dir>[^\r\n]+?)[ \t]*\r?\n[ \t]*(?:New|NotEqual):[ \t]*\r?\n", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Header();

    [GeneratedRegex(@"^[ \t]*(?:-[ \t]*)?(?<what>Received|Verified):[ \t]*(?<name>.+?)[ \t]*$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex EntryLine();

    /// <summary>The failure is a Verify snapshot mismatch (new or changed snapshot).</summary>
    public static bool IsMismatchMessage(string? message) =>
        message is not null && message.Contains("Directory:", StringComparison.Ordinal) && Header().IsMatchOrFalse(message);

    public sealed record ParsedEntry(string Received, string Verified, bool IsNew);

    /// <summary>
    /// The snapshot directory and the (received, verified) file name pairs of Verify's exception message (sections
    /// "New:" and "NotEqual:", up to "FileContent:"). (null, []) when the message is not a Verify mismatch.
    /// </summary>
    public static (string? Directory, List<ParsedEntry> Entries) Parse(string? message)
    {
        if (message is null) return (null, []);
        var m = Header().MatchOrEmpty(message);
        if (!m.Success) return (null, []);
        var dir = m.Groups["dir"];
        var reader = new EntryReader();
        // Read returns false at the end of the file lists ("FileContent:"): All stops reading there.
        _ = message[(dir.Index + dir.Length)..].Split('\n').Select(raw => raw.TrimEnd('\r')).All(reader.Read);
        return (dir.Value, reader.Entries);
    }

    /// <summary>The (received, verified) pairs of the "New:" and "NotEqual:" sections, line by line.</summary>
    private sealed class EntryReader
    {
        private string? _section;
        private string? _received;

        public List<ParsedEntry> Entries { get; } = [];

        /// <summary>Reads one line; false at the end of the file lists ("FileContent:").</summary>
        public bool Read(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return true;
            if (trimmed is "FileContent:") return false;
            if (trimmed.EndsWith(':') && !trimmed.Contains(' '))
            {
                _section = trimmed[..^1];
                _received = null;
            }
            else if (_section is "New" or "NotEqual") ReadEntry(line);
            return true;
        }

        private void ReadEntry(string line)
        {
            var e = EntryLine().MatchOrEmpty(line);
            if (!e.Success) return;
            if (e.Groups["what"].Value == "Received") _received = e.Groups["name"].Value;
            else if (_received is not null)
            {
                Entries.Add(new ParsedEntry(_received, e.Groups["name"].Value, _section == "New"));
                _received = null;
            }
        }
    }

    /// <summary>"A.B.received.txt" -> "A.B.verified.txt"; null when the name is not a received file name.</summary>
    public static string? VerifiedNameFor(string receivedName)
    {
        var i = receivedName.LastIndexOf(ReceivedMarker, StringComparison.Ordinal);
        if (i <= 0 || i + ReceivedMarker.Length >= receivedName.Length) return null;
        return string.Concat(receivedName.AsSpan(0, i), VerifiedMarker, receivedName.AsSpan(i + ReceivedMarker.Length));
    }

    /// <summary>A file name Verify writes: "&lt;prefix&gt;.received.&lt;ext&gt;" or "&lt;prefix&gt;.verified.&lt;ext&gt;".</summary>
    public static bool IsSnapshotFileName(string fileName)
    {
        foreach (var marker in new[] { ReceivedMarker, VerifiedMarker })
        {
            var i = fileName.LastIndexOf(marker, StringComparison.Ordinal);
            if (i > 0 && i + marker.Length < fileName.Length) return true;
        }
        return false;
    }

    /// <summary>image (shown as pictures), text (diffed) or binary, from the extension and a NUL-byte sniff of the content.</summary>
    public static SnapshotKind KindOf(string path, string? otherPath = null)
    {
        if (ImageExtensions.Contains(Path.GetExtension(path))) return SnapshotKind.Image;
        foreach (var p in new[] { path, otherPath })
        {
            if (p is null || !File.Exists(p)) continue;
            try
            {
                using var fs = File.OpenRead(p);
                var buf = new byte[8000];
                var n = fs.Read(buf, 0, buf.Length);
                return buf.AsSpan(0, n).IndexOf((byte)0) >= 0 ? SnapshotKind.Binary : SnapshotKind.Text;
            }
            catch (IOException) { /* unreadable: the kind falls back below */ }
            catch (UnauthorizedAccessException) { /* same */ }
        }
        return SnapshotKind.Text;
    }

    /// <summary>Content type for GET /api/snapshots/file.</summary>
    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// The pending snapshots of a failed test: the received files named by Verify's message that still exist (in the
    /// message's order), or — when the message can't be parsed (e.g. cut short) — the "&lt;Class&gt;.&lt;Method&gt;*.received.*"
    /// files in the test file's directory. Every file must be inside <paramref name="projectDir"/> (canonical, symlinks
    /// resolved); received and verified file share one directory and differ only in the marker. Others are skipped.
    /// </summary>
    public static (string? Directory, List<SnapshotFile> Files) Find(TestNode t, string projectDir)
    {
        var root = Path.TrimEndingDirectorySeparator(PathUtil.CanonicalizeOrSelf(projectDir));
        var (dir, entries) = Parse(t.ErrorMessage);
        if (dir is not null && entries.Count > 0) return (dir, FilesOfEntries(dir, entries, root));
        // Fallback: scan the test file's directory (or the reported one) for this test's received files.
        var scanDir = dir ?? (t.FilePath is { } fp ? Path.GetDirectoryName(fp) : null);
        var cls = t.ClassName?.Split(TypeNameSeparators)[^1];
        if (scanDir is null || cls is null || t.MethodName is null || !Directory.Exists(scanDir)) return (scanDir, []);
        return (scanDir, ScanReceivedFiles(scanDir, $"{cls}.{t.MethodName}", root));
    }

    /// <summary>The received files named by Verify's message that still exist (in the message's order).</summary>
    private static List<SnapshotFile> FilesOfEntries(string dir, List<ParsedEntry> entries, string root)
    {
        var files = new List<SnapshotFile>();
        foreach (var e in entries)
        {
            if (Path.GetFileName(e.Received) != e.Received || VerifiedNameFor(e.Received) != e.Verified) continue;
            if (Resolve(Path.Combine(dir, e.Received), root) is { } f && !files.Any(x => x.Name == f.Name))
                files.Add(f with { IsNew = f.IsNew || e.IsNew });
        }
        return files;
    }

    /// <summary>The "&lt;prefix&gt;[._#]*.received.*" files in <paramref name="dir"/>.</summary>
    private static List<SnapshotFile> ScanReceivedFiles(string dir, string prefix, string root)
    {
        var files = new List<SnapshotFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, prefix + "*" + ReceivedMarker + "*").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (name.Length <= prefix.Length || name[prefix.Length] is not ('.' or '_' or '#')) continue;
                if (Resolve(path, root) is { } f) files.Add(f);
            }
        }
        catch (IOException) { /* unreadable folder: no snapshot files from it */ }
        catch (UnauthorizedAccessException) { /* same */ }
        return files;
    }

    /// <summary>Namespace / nesting separators of a type name ("Ns.Outer+Inner").</summary>
    private static readonly char[] TypeNameSeparators = ['.', '+'];

    /// <summary>A received file as a <see cref="SnapshotFile"/> when it exists and both paths are contained in <paramref name="root"/>.</summary>
    private static SnapshotFile? Resolve(string receivedPath, string root)
    {
        if (!File.Exists(receivedPath)) return null;
        string received;
        try { received = PathUtil.Canonicalize(receivedPath); } catch { return null; }
        var dir = Path.GetDirectoryName(received);
        var verifiedName = VerifiedNameFor(Path.GetFileName(received));
        if (dir is null || verifiedName is null || !PathUtil.IsUnder(received, root)) return null;
        string verified;
        try { verified = PathUtil.Canonicalize(Path.Combine(dir, verifiedName)); } catch { return null; }
        if (!PathUtil.IsUnder(verified, root) || !string.Equals(Path.GetDirectoryName(verified), dir, PathUtil.Comparison)) return null;
        var exists = File.Exists(verified);
        return new SnapshotFile(verifiedName, received, verified, KindOf(received, exists ? verified : null), !exists || new FileInfo(verified).Length == 0);
    }

    /// <summary>
    /// Re-checks a snapshot right before a file operation (the files may have changed since it was found): the received
    /// file exists, is not a link, and both paths are inside <paramref name="projectDir"/> in one directory.
    /// </summary>
    public static string? Validate(SnapshotFile f, string projectDir)
    {
        var root = Path.TrimEndingDirectorySeparator(PathUtil.CanonicalizeOrSelf(projectDir));
        if (!File.Exists(f.ReceivedPath)) return "The received file no longer exists.";
        if (new FileInfo(f.ReceivedPath).LinkTarget is not null) return "The received file is a link.";
        string received, verified;
        try { (received, verified) = (PathUtil.Canonicalize(f.ReceivedPath), PathUtil.Canonicalize(f.VerifiedPath)); }
        catch { return "The path could not be resolved."; }
        if (!PathUtil.IsUnder(received, root) || !PathUtil.IsUnder(verified, root)) return "The snapshot is outside the test project.";
        if (!string.Equals(Path.GetDirectoryName(received), Path.GetDirectoryName(verified), PathUtil.Comparison)
            || VerifiedNameFor(Path.GetFileName(received)) != Path.GetFileName(verified))
            return "The received and verified files don't match.";
        return null;
    }

    /// <summary>Accept: the received file replaces the verified one (a rename: atomic on the same volume).</summary>
    public static void Accept(SnapshotFile f) => File.Move(f.ReceivedPath, f.VerifiedPath, overwrite: true);

    /// <summary>Reject: delete the received file (the verified snapshot stays as it is).</summary>
    public static void Reject(SnapshotFile f) => File.Delete(f.ReceivedPath);
}

/// <summary>The snapshot endpoints (docs/contract.md "Round 6"), mapped by <see cref="LabcoatApp.Build"/>.</summary>
public static class SnapshotEndpoints
{
    public static void Map(WebApplication app, Explorer explorer)
    {
        app.MapGet("/api/tests/snapshots", (string? projectId, string? id) => TestSnapshots(explorer, projectId, id));

        // The content of a received / verified file: same path rules as /api/source plus the Verify file name pattern.
        app.MapGet("/api/snapshots/file", (string? path, HttpContext ctx) => SnapshotFileAsync(explorer, path, ctx));

        app.MapPost("/api/snapshots/accept", (HttpRequest req) => ResolveAsync(explorer, req, accept: true));
        app.MapPost("/api/snapshots/reject", (HttpRequest req) => ResolveAsync(explorer, req, accept: false));
    }

    private static IResult TestSnapshots(Explorer explorer, string? projectId, string? id)
    {
        if (string.IsNullOrEmpty(id)) return Results.BadRequest(new { error = "id is required" });
        return explorer.GetSnapshots(projectId, id) is { } s ? Results.Json(s, Json.Options) : Results.NotFound(new { error = "Unknown test" });
    }

    private static async Task<IResult> SnapshotFileAsync(Explorer explorer, string? path, HttpContext ctx)
    {
        var access = explorer.CheckSourcePath(path);
        if (access.Result == SourceAccessResult.Forbidden || !VerifySnapshots.IsSnapshotFileName(Path.GetFileName(access.FullPath ?? path ?? "")))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (access.Result == SourceAccessResult.NotFound) return Results.NotFound();
        var info = new FileInfo(access.FullPath!);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (info.Length > VerifySnapshots.MaxFileBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var bytes = await File.ReadAllBytesAsync(info.FullName, ctx.RequestAborted);
        ctx.Response.Headers.CacheControl = "no-store";
        var kind = VerifySnapshots.KindOf(info.FullName);
        if (kind == SnapshotKind.Text)
            return Results.Text(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), "text/plain; charset=utf-8");
        if (kind == SnapshotKind.Binary) ctx.Response.Headers.ContentDisposition = "attachment";
        return Results.Bytes(bytes, VerifySnapshots.ContentType(info.FullName));
    }

    private static async Task<IResult> ResolveAsync(Explorer explorer, HttpRequest req, bool accept)
    {
        SnapshotRequest? body;
        try
        {
            using var reader = new StreamReader(req.Body);
            var text = await reader.ReadToEndAsync(req.HttpContext.RequestAborted);
            body = string.IsNullOrWhiteSpace(text) ? new SnapshotRequest(null, null, null, null) : JsonSerializer.Deserialize<SnapshotRequest>(text, Json.Options);
        }
        catch (JsonException ex) { return Results.BadRequest(new { error = "Malformed JSON: " + ex.Message }); }
        var (status, result, error) = explorer.ResolveSnapshots(body ?? new SnapshotRequest(null, null, null, null), accept);
        return result is not null ? Results.Json(result, Json.Options, statusCode: status) : Results.Json(new { error }, Json.Options, statusCode: status);
    }
}
