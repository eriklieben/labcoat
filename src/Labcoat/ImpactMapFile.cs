using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Labcoat;

// --impact-map-file (CliRun.cs): the impact map as a small versioned JSON file, so a CI job on main can build it and
// pull request jobs can load it on a fresh runner. The file is keyed on stable names (project path relative to the
// workspace root, Namespace.Class, method, display name), never on test ids or absolute paths, and its profiles are
// shared between tests: tests of one class have the same coverage in the (default) class pass.

/// <summary>A file that is not an impact map (or one this Labcoat cannot read).</summary>
public sealed class ImpactMapFileException(string message) : Exception(message);

/// <summary>The stored form of the impact map (format "labcoat-impact-map", <see cref="ImpactMapFile.CurrentVersion"/>).</summary>
internal sealed class ImpactMapDocument
{
    public int Version { get; set; } = ImpactMapFile.CurrentVersion;
    public string? Labcoat { get; set; }
    public string? BuiltAt { get; set; }
    public string? Commit { get; set; }
    public string? Mode { get; set; }
    /// <summary>Source files: path relative to the workspace root, '/' separators.</summary>
    public List<string> Files { get; } = [];
    /// <summary>Per file (same index): hash of its text when the coverage was recorded (<see cref="ImpactMapFile.HashText"/>).</summary>
    public List<string> Hashes { get; } = [];
    /// <summary>Method names; a profile entry points into this list.</summary>
    public List<string> Names { get; } = [];
    /// <summary>Covered methods: file index, first line, last line, name index (-1 = none). Tests share them.</summary>
    public List<int[]> Spans { get; } = [];
    /// <summary>What a test (or the tests of a class) executed: ascending indices into <see cref="Spans"/>.</summary>
    public List<int[]> Profiles { get; } = [];
    public List<DocProject> Projects { get; } = [];
}

internal sealed record DocProject(string Path, List<DocClass> Classes);
internal sealed record DocClass(string Name, List<DocTest> Tests);
/// <summary>One test: its method, display name (when it differs from the method), profile and whether the data is per-test.</summary>
internal sealed record DocTest(string Method, string DisplayName, int Profile, bool PerTest);

internal static class ImpactMapFile
{
    public const int CurrentVersion = 1;
    private const string FormatName = "labcoat-impact-map";

    /// <summary>Writes <paramref name="path"/> atomically (a temp file in the same folder, then a move); ".gz" compresses it.</summary>
    public static void Save(string path, ImpactMapDocument doc)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (IsGzip(full))
                {
                    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                    Write(gzip, doc);
                }
                else Write(file, doc);
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: a temp file left beside the map */ }
        }
    }

    private static bool IsGzip(string path) => path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    internal static void Write(Stream stream, ImpactMapDocument doc)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = Json.Options.Encoder });
        w.WriteStartObject();
        w.WriteString("format", FormatName);
        w.WriteNumber("version", doc.Version);
        WriteOptional(w, "labcoat", doc.Labcoat);
        WriteOptional(w, "builtAt", doc.BuiltAt);
        WriteOptional(w, "commit", doc.Commit);
        WriteOptional(w, "mode", doc.Mode);
        WriteStrings(w, "files", doc.Files);
        WriteStrings(w, "hashes", doc.Hashes);
        WriteStrings(w, "names", doc.Names);
        w.WriteStartArray("spans");
        foreach (var span in doc.Spans)
        {
            w.WriteStartArray();
            foreach (var n in span) w.WriteNumberValue(n);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        // profiles: the span indices, each as the difference to the previous one (they ascend)
        w.WriteStartArray("profiles");
        foreach (var profile in doc.Profiles)
        {
            w.WriteStartArray();
            var previous = 0;
            foreach (var n in profile)
            {
                w.WriteNumberValue(n - previous);
                previous = n;
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteStartArray("projects");
        foreach (var p in doc.Projects)
        {
            w.WriteStartObject();
            w.WriteString("path", p.Path);
            w.WriteStartArray("classes");
            foreach (var c in p.Classes)
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteStartArray("tests");
                foreach (var t in c.Tests) WriteTest(w, t);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    // [method, profile, perTest 0|1] plus the display name when it is not the method name.
    private static void WriteTest(Utf8JsonWriter w, DocTest t)
    {
        w.WriteStartArray();
        w.WriteStringValue(t.Method);
        w.WriteNumberValue(t.Profile);
        w.WriteNumberValue(t.PerTest ? 1 : 0);
        if (t.DisplayName != t.Method) w.WriteStringValue(t.DisplayName);
        w.WriteEndArray();
    }

    private static void WriteOptional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null) w.WriteString(name, value);
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, List<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    /// <summary>Reads and validates a file; throws <see cref="ImpactMapFileException"/> when it is not a usable impact map.</summary>
    public static ImpactMapDocument Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (!IsGzip(path)) return Read(file);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return Read(gzip);
        }
        // Not JSON or not gzip, or JSON of another shape: a missing key, a value of the wrong kind, a too short array.
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
        {
            throw new ImpactMapFileException($"{path} is not a valid impact map file ({ex.Message})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImpactMapFileException($"{path} could not be read: {ex.Message}");
        }
    }

    internal static ImpactMapDocument Read(Stream stream)
    {
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Str(root, "format") != FormatName)
            throw new ImpactMapFileException("not a Labcoat impact map (no \"format\": \"labcoat-impact-map\")");
        var version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
        if (version != CurrentVersion)
            throw new ImpactMapFileException(version > CurrentVersion
                ? $"it was written by a newer Labcoat (format version {version}, this one reads {CurrentVersion})"
                : $"unsupported format version {version} (this Labcoat reads {CurrentVersion})");

        var doc = new ImpactMapDocument { Version = version, Labcoat = Str(root, "labcoat"), BuiltAt = Str(root, "builtAt"), Commit = Str(root, "commit"), Mode = Str(root, "mode") };
        doc.Files.AddRange(Strings(root, "files"));
        doc.Hashes.AddRange(Strings(root, "hashes"));
        doc.Names.AddRange(Strings(root, "names"));
        if (doc.Hashes.Count != doc.Files.Count) throw new ImpactMapFileException("files and hashes do not match");
        ReadSpans(root, doc);
        ReadProfiles(root, doc);
        ReadProjects(root, doc);
        return doc;
    }

    private static void ReadSpans(JsonElement root, ImpactMapDocument doc)
    {
        foreach (var span in root.GetProperty("spans").EnumerateArray())
        {
            var values = span.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (values.Length != 4 || (uint)values[0] >= (uint)doc.Files.Count || values[3] >= doc.Names.Count || values[3] < -1)
                throw new ImpactMapFileException("a span points outside the file and name lists");
            doc.Spans.Add(values);
        }
    }

    /// <summary>The profiles, back from differences to span indices (see <see cref="Write"/>).</summary>
    private static void ReadProfiles(JsonElement root, ImpactMapDocument doc)
    {
        foreach (var p in root.GetProperty("profiles").EnumerateArray())
        {
            var values = p.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) values[i] += values[i - 1];
                if ((uint)values[i] >= (uint)doc.Spans.Count) throw new ImpactMapFileException("a profile points at a missing span");
            }
            doc.Profiles.Add(values);
        }
    }

    private static void ReadProjects(JsonElement root, ImpactMapDocument doc)
    {
        foreach (var p in root.GetProperty("projects").EnumerateArray())
        {
            var classes = new List<DocClass>();
            foreach (var c in p.GetProperty("classes").EnumerateArray())
                classes.Add(new DocClass(c.GetProperty("name").GetString() ?? "", c.GetProperty("tests").EnumerateArray().Select(t => ReadTest(t, doc.Profiles.Count)).ToList()));
            doc.Projects.Add(new DocProject(p.GetProperty("path").GetString() ?? "", classes));
        }
    }

    private static DocTest ReadTest(JsonElement t, int profiles)
    {
        var method = t[0].GetString() ?? "";
        var profile = t[1].GetInt32();
        if ((uint)profile >= (uint)profiles) throw new ImpactMapFileException("a test points at a missing profile");
        return new DocTest(method, t.GetArrayLength() > 3 ? t[3].GetString() ?? method : method, profile, t[2].GetInt32() == 1);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().Select(x => x.GetString() ?? "");

    /// <summary>
    /// A hash of a source file's text that survives a different checkout: line endings and a byte order mark are left
    /// out (autocrlf on a Windows runner, a file that git reports with another EOL).
    /// </summary>
    public static string HashText(string text)
    {
        var normalized = text.TrimStart('\uFEFF').Replace("\r", "", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>The hash of a file's text; null when it can't be read (the map then treats it as changed).</summary>
    public static string? HashFile(string path)
    {
        try { return HashText(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>What <see cref="ImpactMapFileService.Load"/> found.</summary>
public sealed record ImpactMapLoadResult(bool Loaded, int Tests, int Unmatched, int Files, string? BuiltAt, string? Commit, string? Error);

/// <summary>What <see cref="ImpactMapFileService.SaveAsync"/> wrote.</summary>
public sealed record ImpactMapSaveResult(int Tests, int Files, long Bytes);

/// <summary>
/// --impact-map-file: loads a saved impact map into the explorer's <see cref="ImpactMap"/> and saves it, with paths
/// relative to the workspace root (<paramref name="root"/>). <paramref name="publishSettingsLocked"/> sends the changed
/// impact map info (caller holds Sync); <paramref name="headCommitAsync"/> is the commit recorded in a saved file.
/// </summary>
internal sealed class ImpactMapFileService(WorkspaceState state, ImpactMap impact, Func<string> root, Action publishSettingsLocked,
    Func<CancellationToken, Task<string?>> headCommitAsync)
{
    private const char Sep = '/';

    /// <summary>A project file relative to <paramref name="root"/>: both sides as real paths (a symlinked checkout, /var -> /private/var on macOS, must not leak absolute paths).</summary>
    private static string RelProject(string root, string projectPath) => Rel(root, PathUtil.CanonicalizeOrSelf(projectPath));

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, Sep);

    private static string Abs(string root, string rel) =>
        PathUtil.CanonicalizeOrSelf(Path.GetFullPath(Path.Combine(root, rel.Replace(Sep, Path.DirectorySeparatorChar))));

    /// <summary>
    /// Loads an impact map file into the map: the tests of the file are matched to the discovered ones by project path,
    /// class, method and display name (not by id, which a machine may derive differently); entries of projects or tests
    /// that no longer exist are ignored. Call after discovery. A missing or unusable file is reported in the result.
    /// </summary>
    public ImpactMapLoadResult Load(string path)
    {
        ImpactMapDocument doc;
        try { doc = ImpactMapFile.Load(path); }
        catch (ImpactMapFileException ex) { return new ImpactMapLoadResult(false, 0, 0, 0, null, null, ex.Message); }
        var rootDir = root();
        var files = doc.Files.Select(f => Abs(rootDir, f)).ToList();
        var profiles = new Dictionary<int, Dictionary<string, List<LineSpan>>>();
        Dictionary<string, List<LineSpan>> ProfileOf(int index)
        {
            if (profiles.TryGetValue(index, out var known)) return known;
            var coverage = CoverageOf(doc, files, index);
            profiles[index] = coverage;
            return coverage;
        }

        int loaded = 0, unmatched = 0;
        var usedFiles = new HashSet<int>();
        lock (state.Sync)
        {
            foreach (var project in doc.Projects)
            {
                var entry = state.Projects.FirstOrDefault(p => string.Equals(RelProject(rootDir, p.Project.Path), project.Path, PathUtil.Comparison));
                if (entry is null)
                {
                    unmatched += project.Classes.Sum(c => c.Tests.Count);
                    continue;
                }
                var pid = entry.Project.Id;
                var current = state.Order.Where(k => k.ProjectId == pid).Select(k => state.Tests[k]).ToList();
                var matches = Match(project, current, out var missing);
                unmatched += missing;
                foreach (var (test, t) in matches)
                {
                    impact.Import(pid, test.Id, t.PerTest, ProfileOf(t.Profile));
                    loaded++;
                    usedFiles.UnionWith(doc.Profiles[t.Profile].Select(i => doc.Spans[i][0]));
                }
            }
            foreach (var i in usedFiles) impact.SetHash(files[i], doc.Hashes[i]);
            if (loaded > 0 && DateTime.TryParse(doc.BuiltAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var built)) impact.BuiltAt = built;
            publishSettingsLocked();
        }
        return new ImpactMapLoadResult(true, loaded, unmatched, usedFiles.Count, doc.BuiltAt, doc.Commit, null);
    }

    /// <summary>The line spans of profile <paramref name="index"/> by file (<paramref name="files"/>: the document's files as absolute paths).</summary>
    private static Dictionary<string, List<LineSpan>> CoverageOf(ImpactMapDocument doc, List<string> files, int index)
    {
        var coverage = new Dictionary<string, List<LineSpan>>(ImpactMap.PathComparer);
        foreach (var s in doc.Profiles[index].Select(i => doc.Spans[i]))
        {
            var span = new LineSpan(s[1], s[2]) { Name = s[3] >= 0 ? doc.Names[s[3]] : null };
            if (!coverage.TryGetValue(files[s[0]], out var list)) coverage[files[s[0]]] = list = [];
            list.Add(span);
        }
        return coverage;
    }

    /// <summary>
    /// The discovered tests (<paramref name="current"/>) each test of <paramref name="project"/> in the file gives its data
    /// to, a discovered test at most once. Exact matches first; a test whose display name changed (data rows) shares the
    /// data of its method. <paramref name="unmatched"/>: the tests of the file that match none.
    /// </summary>
    private static List<(TestNode Test, DocTest Doc)> Match(DocProject project, List<TestNode> current, out int unmatched)
    {
        var exact = current.ToLookup(t => (Explorer.ClassKeyOf(t), t.MethodName ?? "", t.DisplayName));
        var byMethod = current.ToLookup(t => (Explorer.ClassKeyOf(t), t.MethodName ?? ""));
        var matches = new List<(TestNode, DocTest)>();
        var imported = new HashSet<string>();
        var pending = new List<(DocClass Class, DocTest Test)>();
        foreach (var c in project.Classes)
            foreach (var t in c.Tests)
            {
                var hits = exact[(c.Name, t.Method, t.DisplayName)].ToList();
                if (hits.Count == 0) pending.Add((c, t));
                AddMatches(matches, imported, hits, t);
            }
        unmatched = 0;
        foreach (var (c, t) in pending)
        {
            var hits = byMethod[(c.Name, t.Method)].Where(x => !imported.Contains(x.Id)).ToList();
            if (hits.Count == 0) unmatched++;
            AddMatches(matches, imported, hits, t with { PerTest = false });
        }
        return matches;
    }

    private static void AddMatches(List<(TestNode, DocTest)> matches, HashSet<string> imported, List<TestNode> hits, DocTest t)
    {
        foreach (var hit in hits)
        {
            if (!imported.Add(hit.Id)) continue; // matched already, by an exact name
            matches.Add((hit, t));
        }
    }

    /// <summary>
    /// Writes the impact map to <paramref name="path"/> (atomically): the tests that exist now, with the content hash of
    /// every source file. Null when the map holds nothing to write.
    /// </summary>
    public async Task<ImpactMapSaveResult?> SaveAsync(string path, CancellationToken ct = default)
    {
        var doc = new ImpactMapDocument { Labcoat = Cli.Version, Commit = await headCommitAsync(ct) };
        lock (state.Sync)
        {
            if (impact.BuiltAt is { } built) doc.BuiltAt = built.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            doc.Mode = impact.Mode;
            BuildDocumentLocked(doc);
        }
        if (doc.Projects.Count == 0) return null;
        ImpactMapFile.Save(path, doc);
        return new ImpactMapSaveResult(doc.Projects.Sum(p => p.Classes.Sum(c => c.Tests.Count)), doc.Files.Count, new FileInfo(path).Length);
    }

    /// <summary>Fills <paramref name="doc"/> from the map (caller holds Sync).</summary>
    private void BuildDocumentLocked(ImpactMapDocument doc)
    {
        var rootDir = root();
        var fileIndex = new Dictionary<string, int>(ImpactMap.PathComparer);
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var spanIndex = new Dictionary<(int, int, int, int), int>();
        var profileIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = impact.Entries()
            .Where(e => state.Tests.ContainsKey((e.ProjectId, e.TestId)))
            .Select(e => (Test: state.Tests[(e.ProjectId, e.TestId)], e.PerTest, e.Files))
            .OrderBy(e => e.Test.ProjectId, StringComparer.Ordinal).ThenBy(e => Explorer.ClassKeyOf(e.Test), StringComparer.Ordinal)
            .ThenBy(e => e.Test.MethodName ?? "", StringComparer.Ordinal).ThenBy(e => e.Test.DisplayName, StringComparer.Ordinal).ToList();

        int FileId(string abs)
        {
            if (fileIndex.TryGetValue(abs, out var i)) return i;
            doc.Files.Add(Rel(rootDir, abs));
            doc.Hashes.Add(ImpactMapFile.HashFile(abs) ?? "");
            return fileIndex[abs] = doc.Files.Count - 1;
        }
        int NameId(string? name)
        {
            if (name is null) return -1;
            if (nameIndex.TryGetValue(name, out var i)) return i;
            doc.Names.Add(name);
            return nameIndex[name] = doc.Names.Count - 1;
        }
        int SpanId(int file, LineSpan s)
        {
            var key = (file, s.Start, s.End, NameId(s.Name));
            if (spanIndex.TryGetValue(key, out var i)) return i;
            doc.Spans.Add([key.file, key.Start, key.End, key.Item4]);
            return spanIndex[key] = doc.Spans.Count - 1;
        }
        int ProfileId(Dictionary<string, List<LineSpan>> files)
        {
            var ids = files.SelectMany(f => f.Value.Select(s => SpanId(FileId(f.Key), s))).Distinct().Order().ToArray();
            var key = string.Join(',', ids);
            if (profileIndex.TryGetValue(key, out var i)) return i;
            doc.Profiles.Add(ids);
            return profileIndex[key] = doc.Profiles.Count - 1;
        }

        foreach (var byProject in entries.GroupBy(e => e.Test.ProjectId))
        {
            var project = state.Projects.First(p => p.Project.Id == byProject.Key);
            var classes = new List<DocClass>();
            foreach (var byClass in byProject.GroupBy(e => Explorer.ClassKeyOf(e.Test)))
                classes.Add(new DocClass(byClass.Key, byClass.Select(e => new DocTest(e.Test.MethodName ?? "", e.Test.DisplayName, ProfileId(e.Files), e.PerTest)).ToList()));
            doc.Projects.Add(new DocProject(RelProject(rootDir, project.Project.Path), classes));
        }
    }
}
