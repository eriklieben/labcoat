using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Labcoat;

// "Run tests affected by my changes" (contract "Run affected tests (git)"): which files changed according to git, mapped
// to tests with the smart-watch strategy (test file → its tests, impact map → tests executing the changed lines, cached
// per-test coverage, else all tests of the projects containing the file).

/// <summary>GET /api/git</summary>
public sealed record GitInfo
{
    public bool Available { get; init; }
    public string? Error { get; init; }
    public string? Root { get; init; }
    public string? Branch { get; init; }
    public string? Head { get; init; }
    public bool? Detached { get; init; }
    public int? Dirty { get; init; }
    public string? Upstream { get; init; }
    public int? Ahead { get; init; }
    public int? Behind { get; init; }
    public bool? Shallow { get; init; }
    public string? DefaultBase { get; init; }
    public string? AffectedBase { get; init; }
}

/// <summary>GET /api/changed-tests</summary>
public sealed record ChangedTests
{
    /// <summary>False outside a repository (or when it can't answer): <see cref="Error"/> says why, <see cref="Tests"/> is empty.</summary>
    public bool Available { get; init; }
    public string? Error { get; init; }
    /// <summary>"branch" (merge base with the base branch → working tree) or "working" (HEAD → working tree, no base branch).</summary>
    public string? Base { get; init; }
    public string? Ref { get; init; }
    public required List<ChangedTest> Tests { get; init; }
}

public sealed record ChangedTest(string ProjectId, string Id);

/// <summary>GET /api/affected</summary>
public sealed record AffectedPlan
{
    public required string Base { get; init; }
    public string? Ref { get; init; }
    public string? MergeBase { get; init; }
    public required GitInfo Git { get; init; }
    public required List<AffectedFile> Files { get; init; }
    public required List<AffectedTest> Tests { get; init; }
    public required List<AffectedProject> Projects { get; init; }
    public required AffectedCounts Counts { get; init; }
    public required string Strategy { get; init; }
    public bool? NeedsImpactMap { get; init; }
    public ImpactMapInfo? ImpactMap { get; init; }
    public List<string>? Warnings { get; init; }

    /// <summary>Projects whose product files had no impact data (for <c>buildImpactMap: true</c>).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<string> ProjectsNeedingImpactMap { get; init; } = [];
    /// <summary>Run selection: projectId → test ids (null = every test of the project).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string, HashSet<string>?> Selection { get; init; } = [];
    /// <summary>Tests selected through class-level impact data (impactMap "auto" refines their classes per test).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<(string ProjectId, string Id)> ClassLevel { get; init; } = [];
}

public sealed record AffectedFile
{
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    public required string Status { get; init; }
    public string? OldPath { get; init; }
    public required string Kind { get; init; }
    public required string Mapping { get; init; }
    public int Tests { get; init; }
    public List<string>? ProjectIds { get; init; }
    public List<int[]>? Lines { get; init; }
    public List<string>? Methods { get; init; }
}

public sealed record AffectedTest(string ProjectId, string Id, List<string> Reasons)
{
    /// <summary>Selected through the impact map: "test" (per-test data) | "class" (class-level data, shared by the tests of a coverage process).</summary>
    public string? Granularity { get; init; }
}

public sealed record AffectedProject
{
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    public int Tests { get; init; }
    public bool? All { get; init; }
}

public sealed record AffectedCounts(int Files, int Tests, int Projects);

/// <summary>A run-affected request that cannot be answered: HTTP-like status + machine readable code.</summary>
public sealed class AffectedException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>AffectedPlan.base: what the changes are compared with.</summary>
internal static class AffectedBases
{
    /// <summary>Uncommitted changes (working tree + index + untracked files) against HEAD.</summary>
    public const string Working = "working";
    /// <summary>The branch against its merge base with a base ref.</summary>
    public const string Branch = "branch";
    /// <summary>A commit range or one commit.</summary>
    public const string Range = "range";
}

/// <summary>One file git reports as changed (paths relative to the repository root, '/' separators).</summary>
public sealed record GitChange(string Path, string Status, string? OldPath = null)
{
    /// <summary>Changed line ranges on the new side; null = whole file (added, deleted, untracked, binary).</summary>
    public List<LineSpan>? Lines { get; set; }
}

/// <summary>The changes of one base: files plus what was compared.</summary>
public sealed record GitChanges(List<GitChange> Files, string? Ref, string? MergeBase, List<string> Warnings);

/// <summary>
/// Thin wrapper around the <c>git</c> CLI for one repository. Every call is a child process with an argument list (no
/// shell), <c>GIT_OPTIONAL_LOCKS=0</c> (never takes the index lock the user's own git needs) and a timeout.
/// </summary>
public sealed partial class GitRepo
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    /// <summary>The git executable (tests point it at a missing file to simulate "git not installed").</summary>
    internal static string Executable { get; set; } = "git";

    // A ref / range: no whitespace, no leading '-' (would be read as an option), no control characters.
    [GeneratedRegex(@"^(?!-)[A-Za-z0-9._/@{}~^:+\-]+$", RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex RefPattern();

    public string Root { get; }

    private const string RevParse = "rev-parse";

    private GitRepo(string root) => Root = root;

    public static bool IsValidRef(string? value) => value is { Length: > 0 and <= 256 } && RefPattern().IsMatchOrFalse(value) && !value.Contains("@{-", StringComparison.Ordinal);

    /// <summary>Runs git in <paramref name="cwd"/>. Throws <see cref="AffectedException"/> 409 gitUnavailable when git cannot start.</summary>
    public static async Task<(int Code, string Out, string Err)> RunAsync(string cwd, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in (string[])["-c", "core.quotePath=false", "-c", "color.ui=false", .. args]) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        psi.Environment.Remove("GIT_DIR");
        psi.Environment.Remove("GIT_WORK_TREE");
        psi.Environment.Remove("GIT_INDEX_FILE");

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            throw new AffectedException(409, "gitUnavailable", "git is not available: " + ex.Message);
        }
        proc.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
            return (proc.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort: the process may have exited already */ }
            if (ct.IsCancellationRequested) throw;
            throw new AffectedException(504, "gitTimeout", $"git {string.Join(' ', args.Take(2))} did not finish within {Timeout.TotalSeconds:0} s.");
        }
    }

    private Task<(int Code, string Out, string Err)> Git(CancellationToken ct, params string[] args) => RunAsync(Root, args, ct);

    /// <summary>The repository containing <paramref name="dir"/>, null when there is none.</summary>
    public static async Task<GitRepo?> FindAsync(string dir, CancellationToken ct = default)
    {
        if (!Directory.Exists(dir)) return null;
        var (code, output, _) = await RunAsync(dir, [RevParse, "--show-toplevel"], ct);
        var root = output.Trim();
        if (code != 0 || root.Length == 0) return null;
        return new GitRepo(PathUtil.CanonicalizeOrSelf(Path.GetFullPath(root)));
    }

    public async Task<bool> RefExistsAsync(string rev, CancellationToken ct = default) =>
        (await Git(ct, RevParse, "--verify", "-q", rev + "^{commit}")).Code == 0;

    private async Task<string?> ShortShaAsync(string rev, CancellationToken ct)
    {
        var (code, output, _) = await Git(ct, RevParse, "--short", rev + "^{commit}");
        return code == 0 && output.Trim() is { Length: > 0 } sha ? sha : null;
    }

    /// <summary>
    /// The base of "branch vs …" when none is given: <paramref name="configured"/> (server option affectedBase), else the
    /// remote default branch (origin/HEAD), else the first of origin/main, origin/master, main, master that exists.
    /// </summary>
    public async Task<string?> DefaultBaseAsync(string? configured, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        var (code, output, _) = await Git(ct, "symbolic-ref", "-q", "--short", "refs/remotes/origin/HEAD");
        if (code == 0 && output.Trim() is { Length: > 0 } head) return head;
        foreach (var candidate in new[] { "origin/main", "origin/master", "main", "master" })
            if (await RefExistsAsync(candidate, ct)) return candidate;
        return null;
    }

    public async Task<GitInfo> InfoAsync(string? configuredBase, CancellationToken ct = default)
    {
        var (code, output, err) = await Git(ct, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all");
        if (code != 0) return new GitInfo { Available = false, Root = Root, Error = FirstLine(err) ?? "git status failed" };
        var status = StatusSummary.Parse(output);
        var detached = status.Branch == "(detached)";
        var shallow = await IsShallowAsync(ct);
        string? head = status.Oid is null or "(initial)" ? null : await ShortShaAsync("HEAD", ct);
        return new GitInfo
        {
            Available = true,
            Root = Root,
            Branch = detached ? null : status.Branch,
            Detached = detached ? true : null,
            Head = head,
            Dirty = status.Dirty,
            Upstream = status.Upstream,
            Ahead = status.Upstream is null ? null : status.Ahead,
            Behind = status.Upstream is null ? null : status.Behind,
            Shallow = shallow ? true : null,
            DefaultBase = await DefaultBaseAsync(configuredBase, ct),
            AffectedBase = string.IsNullOrWhiteSpace(configuredBase) ? null : configuredBase.Trim(),
        };
    }

    private async Task<bool> IsShallowAsync(CancellationToken ct) =>
        (await Git(ct, RevParse, "--is-shallow-repository")).Out.Trim() == "true";

    /// <summary>The branch headers and the number of changed entries of <c>git status --porcelain=v2 --branch -z</c>.</summary>
    private sealed class StatusSummary
    {
        public string? Branch { get; private set; }
        public string? Oid { get; private set; }
        public string? Upstream { get; private set; }
        public int? Ahead { get; private set; }
        public int? Behind { get; private set; }
        public int Dirty { get; private set; }

        public static StatusSummary Parse(string output)
        {
            var summary = new StatusSummary();
            var parts = output.Split('\0');
            var i = 0;
            while (i < parts.Length)
            {
                var e = parts[i++];
                if (e.Length == 0) continue;
                if (e.StartsWith("# ", StringComparison.Ordinal)) summary.ReadHeader(e);
                else if (e[0] is '1' or 'u' or '?') summary.Dirty++;
                else if (e[0] == '2')
                {
                    summary.Dirty++;
                    i++; // renamed / copied: the original path follows
                }
            }
            return summary;
        }

        private void ReadHeader(string e)
        {
            if (TryValue(e, "# branch.head ", out var value)) Branch = value;
            else if (TryValue(e, "# branch.oid ", out value)) Oid = value;
            else if (TryValue(e, "# branch.upstream ", out value)) Upstream = value;
            else if (TryValue(e, "# branch.ab ", out value)) ReadAheadBehind(value);
        }

        private void ReadAheadBehind(string value)
        {
            var ab = value.Split(' ');
            if (ab.Length == 2 && int.TryParse(ab[0].TrimStart('+'), out var a) && int.TryParse(ab[1].TrimStart('-'), out var b)) (Ahead, Behind) = (a, b);
        }

        private static bool TryValue(string entry, string prefix, out string value)
        {
            var match = entry.StartsWith(prefix, StringComparison.Ordinal);
            value = match ? entry[prefix.Length..] : "";
            return match;
        }
    }

    /// <summary>The changed files of a base ("working" | "branch" | "range"); throws <see cref="AffectedException"/>.</summary>
    public async Task<GitChanges> ChangesAsync(string baseKind, string? reference, string? configuredBase, CancellationToken ct = default)
    {
        var hasHead = await RefExistsAsync("HEAD", ct);
        return baseKind switch
        {
            AffectedBases.Working => await WorkingChangesAsync(hasHead, ct),
            AffectedBases.Branch => await BranchChangesAsync(hasHead, reference, configuredBase, ct),
            AffectedBases.Range => await RangeChangesAsync(reference, ct),
            _ => throw new AffectedException(400, "badBase", $"Unknown base '{baseKind}' (working, branch or range)."),
        };
    }

    private static List<GitChange> Sort(List<GitChange> files) =>
        files.GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => g.First()).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

    private async Task<GitChanges> WorkingChangesAsync(bool hasHead, CancellationToken ct)
    {
        var from = hasHead ? "HEAD" : await EmptyTreeAsync(ct);
        var files = await DiffAsync([from], ct);
        files.AddRange(await UntrackedAsync(ct));
        return new GitChanges(Sort(files), hasHead ? "HEAD" : null, null, []);
    }

    private async Task<GitChanges> BranchChangesAsync(bool hasHead, string? reference, string? configuredBase, CancellationToken ct)
    {
        var baseRef = reference ?? await DefaultBaseAsync(configuredBase, ct)
            ?? throw new AffectedException(404, "noBase", "No base branch found (no origin/HEAD, origin/main, origin/master, main or master). Pass a ref or set the affectedBase option.");
        if (!IsValidRef(baseRef)) throw new AffectedException(400, "badRef", $"'{baseRef}' is not a valid git ref.");
        if (!await RefExistsAsync(baseRef, ct)) throw new AffectedException(404, "unknownRef", $"Unknown git ref '{baseRef}'.");
        if (!hasHead) throw new AffectedException(409, "noCommits", "The repository has no commits yet.");
        var warnings = new List<string>();
        var (from, mergeBase) = await MergeBaseAsync(baseRef, warnings, ct);
        var files = await DiffAsync([from], ct);
        files.AddRange(await UntrackedAsync(ct));
        return new GitChanges(Sort(files), baseRef, mergeBase, warnings);
    }

    /// <summary>What "branch vs <paramref name="baseRef"/>" compares with: the merge base, else the base itself (warning).</summary>
    private async Task<(string From, string? MergeBase)> MergeBaseAsync(string baseRef, List<string> warnings, CancellationToken ct)
    {
        var (code, output, _) = await Git(ct, "merge-base", "HEAD", baseRef);
        if (code == 0 && output.Trim() is { Length: > 0 } mb)
            return (mb, await ShortShaAsync(mb, ct) ?? mb[..Math.Min(mb.Length, 12)]);
        var shallow = await IsShallowAsync(ct);
        warnings.Add(shallow
            ? $"Shallow clone: the merge base with {baseRef} is not available, comparing with {baseRef} directly (git fetch --unshallow fixes this)."
            : $"HEAD and {baseRef} have no common history; comparing with {baseRef} directly.");
        return (baseRef, null);
    }

    private async Task<GitChanges> RangeChangesAsync(string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) throw new AffectedException(400, "refRequired", "A commit range (A..B, A...B or a commit) is required.");
        if (!IsValidRef(reference)) throw new AffectedException(400, "badRef", $"'{reference}' is not a valid commit range.");
        if (RangeSeparator(reference) is { } sep)
        {
            await CheckRangeSidesAsync(reference, sep, ct);
            return new GitChanges(Sort(await DiffAsync([reference], ct)), reference, null, []);
        }
        if (!await RefExistsAsync(reference, ct)) throw new AffectedException(404, "unknownRef", $"Unknown git ref '{reference}'.");
        var parent = await RefExistsAsync(reference + "^1", ct) ? reference + "^1" : await EmptyTreeAsync(ct);
        return new GitChanges(Sort(await DiffAsync([parent, reference], ct)), reference, null, []);
    }

    /// <summary>"..." or ".." of a commit range, null for a single commit.</summary>
    private static string? RangeSeparator(string reference)
    {
        if (reference.Contains("...", StringComparison.Ordinal)) return "...";
        return reference.Contains("..", StringComparison.Ordinal) ? ".." : null;
    }

    private async Task CheckRangeSidesAsync(string reference, string sep, CancellationToken ct)
    {
        var sides = reference.Split(sep);
        if (sides.Length != 2) throw new AffectedException(400, "badRef", $"'{reference}' is not a valid commit range.");
        foreach (var side in sides)
        {
            var rev = side.Length == 0 ? "HEAD" : side;
            if (!await RefExistsAsync(rev, ct)) throw new AffectedException(404, "unknownRef", $"Unknown git ref '{rev}'.");
        }
    }

    private async Task<string> EmptyTreeAsync(CancellationToken ct)
    {
        var (code, output, _) = await Git(ct, "hash-object", "-t", "tree", "--stdin");
        return code == 0 && output.Trim() is { Length: > 0 } sha ? sha : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    }

    private static readonly string[] DiffOptions = ["--no-color", "--no-ext-diff", "--no-relative", "-M", "--src-prefix=a/", "--dst-prefix=b/"];

    /// <summary><c>git diff &lt;revs&gt;</c>: name-status (-z, robust paths) plus the -U0 hunks for the changed lines.</summary>
    private async Task<List<GitChange>> DiffAsync(string[] revs, CancellationToken ct)
    {
        var (code, output, err) = await Git(ct, ["diff", "--name-status", "-z", .. DiffOptions, .. revs, "--"]);
        if (code != 0) throw new AffectedException(500, "gitFailed", "git diff failed: " + (FirstLine(err) ?? $"exit code {code}"));
        var files = ParseNameStatus(output);
        if (files.Count == 0) return files;
        var (code2, patch, _) = await Git(ct, ["diff", "-U0", "--no-textconv", .. DiffOptions, .. revs, "--"]);
        if (code2 == 0)
        {
            var hunks = ParseHunks(patch);
            foreach (var f in files.Where(f => f.Status is "modified" or "renamed" or "copied" or "typeChanged"))
                if (hunks.TryGetValue(f.Path, out var lines)) f.Lines = lines;
        }
        return files;
    }

    private async Task<List<GitChange>> UntrackedAsync(CancellationToken ct)
    {
        var (code, output, _) = await Git(ct, "ls-files", "--others", "--exclude-standard", "-z", "--full-name");
        if (code != 0) return [];
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => new GitChange(p, "untracked")).ToList();
    }

    /// <summary>Parses <c>git diff --name-status -z</c>: status NUL path [NUL new path for R/C] NUL …</summary>
    internal static List<GitChange> ParseNameStatus(string output)
    {
        var parts = output.Split('\0');
        var result = new List<GitChange>();
        var i = 0;
        while (i + 1 < parts.Length)
        {
            var status = parts[i];
            if (status.Length == 0)
            {
                i++;
                continue;
            }
            if (status[0] is 'R' or 'C' && i + 2 < parts.Length)
            {
                result.Add(new GitChange(parts[i + 2], status[0] == 'R' ? "renamed" : "copied", parts[i + 1]));
                i += 3; // status, old path, new path
            }
            else
            {
                result.Add(new GitChange(parts[i + 1], StatusName(status[0])));
                i += 2; // status, path
            }
        }
        return result;
    }

    private static string StatusName(char status) => status switch
    {
        'A' => "added", 'D' => "deleted", 'T' => "typeChanged", 'U' => "unmerged", _ => "modified",
    };

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex HunkHeader();

    /// <summary>Changed line ranges (new side) per new path from a <c>git diff -U0</c> patch; adjacent ranges merged.</summary>
    internal static Dictionary<string, List<LineSpan>> ParseHunks(string patch)
    {
        var result = new Dictionary<string, List<LineSpan>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (TryReadFileHeader(line, ref current)) continue;
            if (current is not null && HunkHeader().MatchOrEmpty(line) is { Success: true } m) AddHunk(result, current, m);
        }
        return result;
    }

    /// <summary>A line that names the file the following hunks belong to: sets <paramref name="current"/>.</summary>
    private static bool TryReadFileHeader(string line, ref string? current)
    {
        if (line.StartsWith("diff --git ", StringComparison.Ordinal)) current = null;
        else if (line.StartsWith("rename to ", StringComparison.Ordinal) || line.StartsWith("copy to ", StringComparison.Ordinal))
            current = Unquote(line[(line.IndexOf(" to ", StringComparison.Ordinal) + 4)..]);
        else if (line.StartsWith("+++ ", StringComparison.Ordinal))
        {
            var p = Unquote(line[4..]);
            current = p.StartsWith("b/", StringComparison.Ordinal) ? p[2..] : null; // "/dev/null" for a deleted file
        }
        else return false;
        return true;
    }

    private static void AddHunk(Dictionary<string, List<LineSpan>> result, string file, Match m)
    {
        var start = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var count = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        // count 0 = lines removed after line `start`: the neighbours are what changed.
        var span = count == 0 ? new LineSpan(Math.Max(1, start), start + 1) : new LineSpan(start, start + count - 1);
        if (!result.TryGetValue(file, out var list)) result[file] = list = [];
        if (list.Count > 0 && list[^1].End + 1 >= span.Start) list[^1] = new LineSpan(list[^1].Start, Math.Max(list[^1].End, span.End));
        else list.Add(span);
    }

    /// <summary>Undoes git's C-style quoting of unusual paths (<c>"a/tab\there"</c>).</summary>
    internal static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var bytes = new List<byte>();
        var end = s.Length - 1;
        var i = 1;
        while (i < end)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= end)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                i++;
                continue;
            }
            var n = s[i + 1];
            if (n is >= '0' and <= '7' && i + 3 < end)
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 3), 8)); // \ooo: an octal byte
                i += 4;
                continue;
            }
            bytes.Add((byte)(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => n }));
            i += 2;
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}

public sealed partial class Explorer
{
    /// <summary>Server option <c>affectedBase</c>: the default base ref of "branch vs …" (null = origin/HEAD → main/master).</summary>
    public string? AffectedBase { get; init; }

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".rst", ".adoc" };
    private static readonly HashSet<string> ProjectFileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".fsproj", ".vbproj" };
    private static readonly HashSet<string> ProductCodeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb", ".razor", ".cshtml", ".xaml" };

    private sealed record GitInfoCacheEntry(DateTime At, GitInfo Info);
    private volatile GitInfoCacheEntry? _gitInfoCache;
    private readonly SemaphoreSlim _gitInfoLock = new(1, 1);

    /// <summary>The directory whose repository is the workspace's: the common root of the test projects.</summary>
    private string? WorkspaceDir()
    {
        lock (Sync)
        {
            var paths = _projects.Select(p => p.Project.Path).ToList();
            if (paths.Count == 0) return _inputRoots.FirstOrDefault();
            return PathUtil.CommonRoot(paths);
        }
    }

    private async Task<GitRepo> RepoAsync(CancellationToken ct)
    {
        var dir = WorkspaceDir();
        GitRepo? repo = dir is null ? null : await GitRepo.FindAsync(dir, ct);
        if (repo is null)
        {
            // Projects of several repositories: the first one that is in a repository.
            List<string> projectDirs;
            lock (Sync) projectDirs = _projects.Select(p => Path.GetDirectoryName(p.Project.Path)!).ToList();
            foreach (var d in projectDirs)
            {
                repo = await GitRepo.FindAsync(d, ct);
                if (repo is not null) break;
            }
        }
        return repo ?? throw new AffectedException(409, "notGitRepo", "The workspace is not in a git repository.");
    }

    /// <summary>GET /api/git: branch, HEAD, dirty count, default base (cached ~2 s).</summary>
    public async Task<GitInfo> GitInfoAsync(CancellationToken ct = default)
    {
        await _gitInfoLock.WaitAsync(ct);
        try
        {
            if (_gitInfoCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(2)) return c.Info;
            GitInfo info;
            try { info = await (await RepoAsync(ct)).InfoAsync(AffectedBase, ct); }
            catch (AffectedException ex) { info = new GitInfo { Available = false, Error = ex.Message, AffectedBase = AffectedBase }; }
            _gitInfoCache = new GitInfoCacheEntry(DateTime.UtcNow, info);
            return info;
        }
        finally { _gitInfoLock.Release(); }
    }

    private sealed record ChangedCacheEntry(DateTime At, int Tests, ChangedTests Result);
    private volatile ChangedCacheEntry? _changedCache;
    private readonly SemaphoreSlim _changedLock = new(1, 1);

    /// <summary>
    /// GET /api/changed-tests: the tests added or modified on the current branch, i.e. whose own lines (the method's extent, see IsChanged)
    /// overlap a changed range of their source file, or that sit in a newly added file. Same changes as Run affected's
    /// "branch" base (merge base with the base branch → working tree, untracked files included), else the uncommitted
    /// changes when there is no base branch. Cached ~2 s (and per discovered test count); outside a repository: Available false.
    /// </summary>
    public async Task<ChangedTests> ChangedTestsAsync(CancellationToken ct = default)
    {
        await _changedLock.WaitAsync(ct);
        try
        {
            int count;
            lock (Sync) count = _order.Count;
            if (_changedCache is { } c && c.Tests == count && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(2)) return c.Result;
            var result = await ComputeChangedTestsAsync(ct);
            _changedCache = new ChangedCacheEntry(DateTime.UtcNow, count, result);
            return result;
        }
        finally { _changedLock.Release(); }
    }

    /// <summary>Drops the cached answer of <see cref="ChangedTestsAsync"/> (the next call asks git again).</summary>
    internal void InvalidateChangedTests() => _changedCache = null;

    private async Task<ChangedTests> ComputeChangedTestsAsync(CancellationToken ct)
    {
        GitRepo repo;
        var kind = AffectedBases.Branch;
        GitChanges changes;
        try
        {
            repo = await RepoAsync(ct);
            try { changes = await repo.ChangesAsync(kind, null, AffectedBase, ct); }
            catch (AffectedException ex) when (ex.Code is "noBase" or "noCommits" or "unknownRef")
            {
                kind = AffectedBases.Working;
                changes = await repo.ChangesAsync(kind, null, AffectedBase, ct);
            }
        }
        catch (AffectedException ex) { return new ChangedTests { Available = false, Error = ex.Message, Tests = [] }; }

        var tests = new List<ChangedTest>();
        var candidates = new List<(TestNode Test, GitChange Change)>();
        lock (Sync)
        {
            var byFile = new Dictionary<string, List<TestNode>>(PathComparer);
            foreach (var k in _order)
            {
                if (_tests[k].FilePath is not { } fp) continue;
                var f = PathUtil.CanonicalizeOrSelf(fp);
                if (!byFile.TryGetValue(f, out var list)) byFile[f] = list = [];
                list.Add(_tests[k]);
            }
            foreach (var change in changes.Files.Where(c => c.Status != "deleted"))
            {
                var abs = PathUtil.CanonicalizeOrSelf(Path.Combine(repo.Root, change.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (byFile.TryGetValue(abs, out var inFile)) candidates.AddRange(inFile.Select(t => (t, change)));
            }
        }
        // outside the lock: reading the sources of tests without a usable line range (TestSourceRange.cs)
        var seen = new HashSet<(string, string)>();
        foreach (var (t, change) in candidates)
            if (IsChanged(t, change) && seen.Add((t.ProjectId, t.Id))) tests.Add(new ChangedTest(t.ProjectId, t.Id));
        return new ChangedTests { Available = true, Base = kind, Ref = changes.Ref, Tests = tests };
    }

    /// <summary>
    /// A test of a changed file is changed when the file is new or its line range overlaps a changed range. The range is
    /// the reported Line..LineEnd (TUnit); frameworks that report one line (MSTest, xUnit) or none (NUnit) get the method's
    /// extent from the source file (<see cref="TestSourceRange"/>), else the reported line, else the whole file counts.
    /// </summary>
    internal static bool IsChanged(TestNode test, GitChange change)
    {
        if (change.Status is "added" or "untracked") return true;
        if (change.Lines is not { } spans) return change.Status is not ("renamed" or "copied"); // a pure rename changed no line
        (int Start, int End)? range = test.Line is { } l && test.LineEnd is { } e && e > l ? (l, e) : TestSourceRange.Find(test.FilePath, test.Line, test.MethodName);
        if (range is not { } r)
        {
            if (test.Line is not { } only) return true;
            r = (only, only);
        }
        return spans.Any(s => s.Start <= r.End && r.Start <= s.End);
    }

    /// <summary>Normalizes a base name; throws 400 badBase / badRef.</summary>
    private static string NormalizeBase(string? baseKind, string? reference)
    {
        var kind = string.IsNullOrWhiteSpace(baseKind) ? AffectedBases.Working : baseKind.Trim().ToLowerInvariant();
        if (kind is not (AffectedBases.Working or AffectedBases.Branch or AffectedBases.Range)) throw new AffectedException(400, "badBase", $"Unknown base '{baseKind}' (working, branch or range).");
        if (kind != AffectedBases.Working && !string.IsNullOrEmpty(reference) && !GitRepo.IsValidRef(reference))
            throw new AffectedException(400, "badRef", $"'{reference}' is not a valid git ref.");
        return kind;
    }

    /// <summary>AffectedFile kind / mapping "project": the file selects every test of its projects.</summary>
    private const string WholeProject = "project";

    private sealed record ProjectScope(string Id, string Name, string Path, string Dir, List<string> Dirs);

    /// <summary>
    /// GET /api/affected: which tests the changes of <paramref name="baseKind"/> affect. Never runs anything.
    /// Throws <see cref="AffectedException"/> (400 / 404 / 409) when git can't answer.
    /// </summary>
    public async Task<AffectedPlan> PlanAffectedAsync(string? baseKind, string? reference, CancellationToken ct = default)
    {
        var kind = NormalizeBase(baseKind, reference);
        if (kind == AffectedBases.Working) reference = null;
        var repo = await RepoAsync(ct);
        var changes = await repo.ChangesAsync(kind, string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), AffectedBase, ct);
        var info = await repo.InfoAsync(AffectedBase, ct);

        // Project scopes: the project directory plus its ProjectReference closure (evaluated, else read from the files).
        List<(string Id, string Name, string Path, List<string> WatchDirs)> raw;
        lock (Sync) raw = _projects.Select(p => (p.Project.Id, p.Project.Name, p.Project.Path, p.WatchDirs.ToList())).ToList();
        var scopes = raw.Select(r =>
        {
            var dir = PathUtil.CanonicalizeOrSelf(Path.GetDirectoryName(r.Path)!);
            var dirs = (r.WatchDirs.Count > 0 ? r.WatchDirs : [Path.GetDirectoryName(r.Path)!, .. ClosureDirsFromProjectFile(r.Path)])
                .Select(PathUtil.CanonicalizeOrSelf).Distinct(PathComparer).ToList();
            if (!dirs.Contains(dir, PathComparer)) dirs.Insert(0, dir);
            return new ProjectScope(r.Id, r.Name, PathUtil.CanonicalizeOrSelf(r.Path), dir, dirs);
        }).ToList();

        lock (Sync)
        {
            var plan = BuildPlanLocked(kind, changes, info, repo.Root, scopes);
            QueueImpactRefinementLocked(plan.ClassLevel); // impactMap "auto" (Explorer.Warm.cs)
            return plan;
        }
    }

    private static StringComparer PathComparer => PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static bool InOrUnder(string path, string dir) => string.Equals(path, dir, PathUtil.Comparison) || PathUtil.IsUnder(path, dir);

    private AffectedPlan BuildPlanLocked(string kind, GitChanges changes, GitInfo info, string root, List<ProjectScope> scopes)
    {
        var warnings = new List<string>(changes.Warnings);
        var builder = new AffectedPlanBuilder(this, scopes);
        foreach (var change in changes.Files) builder.AddChange(root, change);

        // Selection + tests in tree order.
        var selection = builder.Selection();
        var tests = builder.TestsInTreeOrder(selection);
        var projects = new List<AffectedProject>();
        foreach (var s in scopes)
        {
            if (!selection.TryGetValue(s.Id, out var ids)) continue;
            var count = tests.Count(t => t.ProjectId == s.Id);
            projects.Add(new AffectedProject { ProjectId = s.Id, Name = s.Name, Tests = count, All = ids is null ? true : null });
            if (ids is null && count == 0) warnings.Add($"{s.Name} has no discovered tests yet; they are counted after its build.");
        }
        var strategy = builder.Mappings.Count switch { 0 => "none", 1 => builder.Mappings.First(), _ => "mixed" };
        return new AffectedPlan
        {
            Base = kind,
            Ref = changes.Ref,
            MergeBase = changes.MergeBase,
            Git = info,
            Files = builder.Files,
            Tests = tests,
            Projects = projects,
            Counts = new AffectedCounts(builder.Files.Count, tests.Count, projects.Count),
            Strategy = strategy,
            NeedsImpactMap = builder.NeedsImpact.Count > 0 ? true : null,
            ImpactMap = _impact.Info(),
            Warnings = warnings.Count > 0 ? warnings : null,
            ProjectsNeedingImpactMap = [.. builder.NeedsImpact],
            Selection = selection,
            ClassLevel = [.. builder.Granularity.Where(kv => kv.Value == "class").Select(kv => kv.Key)],
        };
    }

    /// <summary>What one changed file maps to while the plan is built (<see cref="AffectedPlanBuilder"/>).</summary>
    private sealed class FileMatch(string abs, string name, string verb, List<ProjectScope> containing)
    {
        public string Abs { get; } = abs;
        public string Name { get; } = name;
        public string Verb { get; } = verb;
        /// <summary>The project scopes whose directories contain the file.</summary>
        public List<ProjectScope> Containing { get; } = containing;
        public string Kind { get; set; } = "other";
        public string Mapping { get; set; } = "none";
        public HashSet<(string, string)> Selected { get; } = [];
        public List<string> ProjectIds { get; } = [];
        public List<string>? Methods { get; set; }
    }

    /// <summary>
    /// Maps the changed files of a plan to tests (test file → its tests, build file → whole projects, code → impact map,
    /// else cached per-test coverage, else the containing projects). Runs under <see cref="Sync"/>.
    /// </summary>
    private sealed class AffectedPlanBuilder
    {
        private readonly Explorer _explorer;
        private readonly List<ProjectScope> _scopes;
        /// <summary>Test files → tests (tree order).</summary>
        private readonly Dictionary<string, List<(string ProjectId, string Id)>> _byFile = new(PathComparer);
        private readonly Dictionary<(string ProjectId, string Id), List<string>> _reasons = [];
        private readonly Dictionary<string, List<string>> _wholeProjects = []; // projectId → reasons for "all tests"

        public HashSet<string> NeedsImpact { get; } = [];
        /// <summary>Tests selected by the impact map: "test" | "class".</summary>
        public Dictionary<(string ProjectId, string Id), string> Granularity { get; } = [];
        public HashSet<string> Mappings { get; } = [];
        public List<AffectedFile> Files { get; } = [];

        public AffectedPlanBuilder(Explorer explorer, List<ProjectScope> scopes)
        {
            _explorer = explorer;
            _scopes = scopes;
            foreach (var k in explorer._order)
            {
                if (explorer._tests[k].FilePath is not { } fp) continue;
                var f = PathUtil.CanonicalizeOrSelf(fp);
                if (!_byFile.TryGetValue(f, out var list)) _byFile[f] = list = [];
                list.Add(k);
            }
        }

        private static void AddDistinct<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            if (!list.Contains(value)) list.Add(value);
        }

        private void AddReason((string, string) key, string reason) => AddDistinct(_reasons, key, reason);

        private void AddWhole(string projectId, string reason) => AddDistinct(_wholeProjects, projectId, reason);

        private int CountTests(string projectId) => _explorer._order.Count(k => k.ProjectId == projectId);

        public void AddChange(string root, GitChange change)
        {
            var abs = PathUtil.CanonicalizeOrSelf(Path.Combine(root, change.Path.Replace('/', Path.DirectorySeparatorChar)));
            var name = Path.GetFileName(abs);
            var ext = Path.GetExtension(abs);
            var verb = change.Status switch
            {
                "added" or "untracked" => "added", "deleted" => "deleted", "renamed" => "renamed", _ => "changed",
            };
            var m = new FileMatch(abs, name, verb, _scopes.Where(s => s.Dirs.Any(d => PathUtil.IsUnder(abs, d))).ToList());

            if (_byFile.TryGetValue(abs, out var inFile)) MatchTestFile(m, inFile);
            else if (IsBuildFile(name, ext)) MatchBuildFile(m, ext);
            else if (DocExtensions.Contains(ext) || IsTestArtifact(change.Path))
                m.Kind = "other"; // documentation, or output of a test run (Verify *.received.*, TestResults/)
            else MatchSourceFile(m, change, ext);

            foreach (var (pid, _) in m.Selected) if (!m.ProjectIds.Contains(pid)) m.ProjectIds.Add(pid);
            if (m.Mapping != "none") Mappings.Add(m.Mapping);
            var fileTests = m.Mapping == WholeProject ? m.ProjectIds.Sum(CountTests) : m.Selected.Count;
            Files.Add(new AffectedFile
            {
                Path = abs,
                RelativePath = change.Path,
                Status = change.Status,
                OldPath = change.OldPath,
                Kind = m.Kind,
                Mapping = m.Mapping,
                Tests = fileTests,
                ProjectIds = m.ProjectIds.Count > 0 ? m.ProjectIds : null,
                Lines = change.Lines?.Select(l => new[] { l.Start, l.End }).ToList(),
                Methods = m.Methods,
            });
        }

        private void MatchTestFile(FileMatch m, List<(string ProjectId, string Id)> inFile)
        {
            m.Kind = "test";
            m.Mapping = "testFile";
            foreach (var k in inFile)
            {
                m.Selected.Add(k);
                AddReason(k, $"{m.Name} {m.Verb} (test file)");
            }
        }

        private void MatchBuildFile(FileMatch m, string ext)
        {
            m.Kind = WholeProject;
            var hit = ProjectsOfBuildFile(m, ext);
            if (hit.Count > 0) m.Mapping = WholeProject;
            foreach (var s in hit)
            {
                AddWhole(s.Id, $"{m.Name} {m.Verb}: all tests of {s.Name}");
                m.ProjectIds.Add(s.Id);
            }
        }

        private List<ProjectScope> ProjectsOfBuildFile(FileMatch m, string ext)
        {
            var dir = Path.GetDirectoryName(m.Abs)!;
            if (ProjectFileExtensions.Contains(ext))
                return _scopes.Where(s => string.Equals(s.Path, m.Abs, PathUtil.Comparison) || s.Dirs.Any(d => string.Equals(d, dir, PathUtil.Comparison))).ToList();
            if (m.Name.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase) || m.Name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
                || m.Name.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase))
                return _scopes.Where(s => s.Dirs.Any(d => InOrUnder(d, dir))).ToList();
            // Another .props / .targets: imported explicitly by whatever needs it → the projects around it, else all.
            return m.Containing.Count > 0 ? m.Containing : _scopes;
        }

        private void MatchSourceFile(FileMatch m, GitChange change, string ext)
        {
            m.Kind = m.Containing.Count > 0 || ProductCodeExtensions.Contains(ext) ? "product" : "other";
            var isCode = CodeExtensions.Contains(ext) || ProductCodeExtensions.Contains(ext);
            if (isCode)
            {
                MatchImpactMap(m, change.Status is "deleted" ? null : change.Lines);
                if (m.Mapping == "none") MatchCachedCoverage(m);
            }
            if (m.Mapping == "none" && m.Containing.Count > 0) MatchContainingProjects(m, isCode);
        }

        /// <summary>Impact map: tests whose executed methods overlap the changed lines.</summary>
        private void MatchImpactMap(FileMatch m, List<LineSpan>? lines)
        {
            var impact = _explorer._impact;
            foreach (var projectId in _scopes.Select(s => s.Id))
            {
                if (impact.AffectedByLines(projectId, m.Abs, lines, out var outside) is not { } hits) continue;
                m.Mapping = "impactMap";
                foreach (var (tid, methods) in hits.Where(h => _explorer._tests.ContainsKey((projectId, h.Key))))
                    SelectByImpact(m, (projectId, tid), methods, outside);
            }
        }

        private void SelectByImpact(FileMatch m, (string ProjectId, string Id) key, List<string> methods, bool outside)
        {
            var impact = _explorer._impact;
            m.Selected.Add(key);
            if (methods.Count > 0)
            {
                m.Methods ??= [];
                var known = m.Methods;
                foreach (var x in methods.Where(x => !known.Contains(x))) known.Add(x);
            }
            var perTest = impact.IsPerTest(key.ProjectId, key.Id);
            // A test is "class" when any file selected it through shared data.
            if (!perTest || !Granularity.ContainsKey(key)) Granularity[key] = perTest ? "test" : "class";
            var note = impact.DataNote(key.ProjectId, key.Id);
            AddReason(key, ImpactReason(m, methods, outside, note));
        }

        private static string ImpactReason(FileMatch m, List<string> methods, bool outside, string note)
        {
            if (methods.Count > 0) return $"{m.Name} {m.Verb} ({(methods.Count == 1 ? "method" : "methods")} {string.Join(", ", methods)}, {note})";
            return outside ? $"{m.Name} {m.Verb} (outside executed methods, {note})" : $"{m.Name} {m.Verb} ({note})";
        }

        /// <summary>Cached per-test coverage: tests whose own coverage includes the file.</summary>
        private void MatchCachedCoverage(FileMatch m)
        {
            var covering = _explorer._coverageCache.SingleTestReports()
                .Where(c => _explorer._tests.ContainsKey((c.ProjectId, c.TestId)))
                .Where(c => c.Report.Files.Any(f => f.LinesCovered > 0 && string.Equals(PathUtil.CanonicalizeOrSelf(f.Path), m.Abs, PathUtil.Comparison)));
            foreach (var (pid, tid, _, _) in covering)
            {
                m.Mapping = "coverage";
                m.Selected.Add((pid, tid));
                AddReason((pid, tid), $"{m.Name} {m.Verb} (covered by cached coverage)");
            }
        }

        private void MatchContainingProjects(FileMatch m, bool isCode)
        {
            m.Mapping = WholeProject;
            foreach (var s in m.Containing)
            {
                AddWhole(s.Id, isCode ? $"no impact data for {m.Name}: all tests of {s.Name}" : $"{m.Name} {m.Verb}: all tests of {s.Name}");
                m.ProjectIds.Add(s.Id);
                // Product code outside the test project itself: the impact map would narrow this down.
                if (isCode && !PathUtil.IsUnder(m.Abs, s.Dir)) NeedsImpact.Add(s.Id);
            }
        }

        /// <summary>Run selection: projectId → test ids (null = every test of the project).</summary>
        public Dictionary<string, HashSet<string>?> Selection()
        {
            var selection = new Dictionary<string, HashSet<string>?>();
            foreach (var pid in _wholeProjects.Keys) selection[pid] = null;
            foreach (var (pid, id) in _reasons.Keys.Where(k => !_wholeProjects.ContainsKey(k.ProjectId)))
            {
                if (!selection.TryGetValue(pid, out var set) || set is null)
                {
                    set = [];
                    selection[pid] = set;
                }
                set.Add(id);
            }
            return selection;
        }

        public List<AffectedTest> TestsInTreeOrder(Dictionary<string, HashSet<string>?> selection)
        {
            var tests = new List<AffectedTest>();
            foreach (var k in _explorer._order)
            {
                if (!selection.TryGetValue(k.ProjectId, out var ids)) continue;
                if (ids is not null && !ids.Contains(k.Id)) continue;
                tests.Add(new AffectedTest(k.ProjectId, k.Id, ReasonsFor(k, wholeProject: ids is null)) { Granularity = Granularity.GetValueOrDefault(k) });
            }
            return tests;
        }

        private List<string> ReasonsFor((string ProjectId, string Id) key, bool wholeProject)
        {
            var why = _reasons.TryGetValue(key, out var r) ? r.ToList() : [];
            if (!wholeProject) return why;
            foreach (var w in _wholeProjects[key.ProjectId].Where(w => !why.Contains(w))) why.Add(w);
            return why;
        }
    }

    /// <summary>Files test runs leave behind: Verify's <c>*.received.*</c> and anything under a <c>TestResults</c> folder.</summary>
    private static bool IsTestArtifact(string relativePath) =>
        relativePath.Split('/') is var parts
        && (parts[^1].Contains(".received.", StringComparison.OrdinalIgnoreCase)
            || parts[..^1].Any(p => p.Equals("TestResults", StringComparison.OrdinalIgnoreCase)));

    private static bool IsBuildFile(string name, string ext) =>
        ProjectFileExtensions.Contains(ext) || ext.Equals(".props", StringComparison.OrdinalIgnoreCase) || ext.Equals(".targets", StringComparison.OrdinalIgnoreCase)
        || name.Equals("global.json", StringComparison.OrdinalIgnoreCase) || name.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase);

    /// <summary>ProjectReference closure directories read from the project files (projects not evaluated yet).</summary>
    private static List<string> ClosureDirsFromProjectFile(string projectPath)
    {
        var refs = new System.Text.Json.Nodes.JsonArray();
        try
        {
            var dir = Path.GetDirectoryName(projectPath)!;
            foreach (var el in XDocument.Load(projectPath).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = (string?)el.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.Contains("$(")) continue;
                foreach (var part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    refs.Add(new System.Text.Json.Nodes.JsonObject { ["FullPath"] = Path.GetFullPath(Path.Combine(dir, part.Replace('\\', Path.DirectorySeparatorChar))) });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { /* unreadable project file: no references from it */ }
        return ProjectReferenceClosureDirs(refs);
    }

    private static readonly Dictionary<string, string> BaseLabels = new()
    {
        [AffectedBases.Working] = "uncommitted changes", [AffectedBases.Branch] = "branch", [AffectedBases.Range] = "range",
    };

    /// <summary>Result of <see cref="RunAffectedAsync"/>.</summary>
    public sealed record RunAffectedResult(RunRequestResult? Run, AffectedPlan? Plan, bool ImpactMapFirst = false);

    /// <summary>
    /// runAffected / POST /api/run-affected: plan, then queue one run with the exact selection. With
    /// <paramref name="buildImpactMap"/> and product files without impact data, the impact-map pass runs first (in the
    /// background) and the plan is recomputed before the run. Throws <see cref="AffectedException"/>.
    /// </summary>
    public async Task<RunAffectedResult> RunAffectedAsync(string? baseKind, string? reference, bool? coverage, bool buildImpactMap, string trigger,
        CancellationToken ct = default)
    {
        var plan = await PlanAffectedAsync(baseKind, reference, ct);
        // impactMap "auto": "build impact map first" also refines the classes the plan selected through class-level data.
        List<(string ProjectId, string Id)> refine = ImpactMapMode == ImpactMapModes.Auto ? plan.ClassLevel : [];
        if (buildImpactMap && (plan.ProjectsNeedingImpactMap.Count > 0 || refine.Count > 0))
        {
            var projects = plan.ProjectsNeedingImpactMap.ToList();
            Log(null, LogLevels.Info, projects.Count > 0
                ? $"Run affected: building the impact map for {string.Join(", ", projects)} first…"
                : "Run affected: refining the impact map per test first…");
            // Deliberately outlives the request (it answers right away): the job stops on shutdown, not on ct.
            _ = Task.Run(() => ImpactMapThenRunAffectedAsync(projects, plan, baseKind, reference, coverage, trigger), CancellationToken.None);
            return new RunAffectedResult(null, plan, ImpactMapFirst: true);
        }
        return new RunAffectedResult(StartAffectedRun(plan, coverage, trigger), plan);
    }

    /// <summary>The background part of "build impact map first": the impact map, then a new plan and its run.</summary>
    private async Task ImpactMapThenRunAffectedAsync(List<string> projects, AffectedPlan plan, string? baseKind, string? reference, bool? coverage,
        string trigger)
    {
        try
        {
            foreach (var pid in projects) await BuildImpactMapAsync(pid);
            if (ImpactMapMode == ImpactMapModes.Auto)
            {
                var mid = projects.Count > 0 ? await PlanAffectedAsync(baseKind, reference, _shutdownToken) : plan;
                await RefineImpactMapAsync(mid.ClassLevel);
            }
            var again = await PlanAffectedAsync(baseKind, reference, _shutdownToken);
            StartAffectedRun(again, coverage, trigger);
        }
        catch (AffectedException ex) { Log(null, LogLevels.Warning, "Run affected: " + ex.Message); }
        catch (Exception ex) when (!_shutdown.IsCancellationRequested) { Log(null, LogLevels.Error, "Run affected failed: " + ex.Message); }
        catch (Exception) { /* cancelled (shutdown or a newer request): not an error */ }
    }

    /// <summary>WS runAffected: errors become a commandRejected reply for the sending client.</summary>
    private async Task<ServerEvent?> RunAffectedCommandAsync(ClientCommand cmd)
    {
        try
        {
            var r = await RunAffectedAsync(cmd.Base, cmd.Ref, cmd.Coverage, cmd.BuildImpactMap ?? false, "user", _shutdownToken);
            if (r.Run is { Status: not 202 } run) return new CommandRejectedEvent(cmd.Type, run.Status, run.Error ?? "rejected", run.UnknownTestIds);
            return null;
        }
        catch (AffectedException ex)
        {
            Log(null, LogLevels.Warning, "runAffected: " + ex.Message);
            return new CommandRejectedEvent(cmd.Type, ex.Status, ex.Message);
        }
        catch (OperationCanceledException) { return null; }
    }

    private RunRequestResult? StartAffectedRun(AffectedPlan plan, bool? coverage, string trigger)
    {
        var what = plan.Base switch
        {
            AffectedBases.Branch => $"branch vs {plan.Ref}",
            AffectedBases.Range => plan.Ref ?? "range",
            _ => BaseLabels[plan.Base],
        };
        var fileCount = $"{plan.Counts.Files} file{(plan.Counts.Files == 1 ? "" : "s")}";
        if (plan.Selection.Count == 0)
        {
            Log(null, LogLevels.Info, $"Run affected ({what}): {fileCount} changed → no affected tests");
            return null;
        }
        Log(null, LogLevels.Info, $"Run affected ({what}): {fileCount} → {plan.Counts.Tests} test{(plan.Counts.Tests == 1 ? "" : "s")} in " +
            $"{plan.Counts.Projects} project{(plan.Counts.Projects == 1 ? "" : "s")} ({plan.Strategy})");
        var affected = new AffectedRun
        {
            Strategy = "git",
            ChangedFiles = plan.Files.Select(f => f.Path).ToList(),
            Base = plan.Base,
            Ref = plan.Ref,
            Tests = plan.Counts.Tests,
        };
        var r = RequestRun(new RunRequestOptions { Coverage = coverage, Trigger = trigger, Affected = affected, Selection = plan.Selection });
        if (r.Status != 202) Log(null, LogLevels.Warning, "Run affected: " + r.Error);
        return r;
    }
}
