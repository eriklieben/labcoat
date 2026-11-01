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
    /// <summary>The changes the plan was made of (paths relative to <see cref="GitInfo.Root"/>): --min-diff-coverage of --run --affected.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public GitChanges? Changes { get; init; }
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
    /// <summary>How precise the impact data behind <see cref="Mapping"/> is: "per-test", "class-level" or "per-test and class-level" (run --affected prints it).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? DataQuality { get; init; }
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

/// <summary>GitChange.status: how git changed the file.</summary>
internal static class GitStatuses
{
    public const string Added = "added";
    public const string Deleted = "deleted";
    public const string Modified = "modified";
    public const string Renamed = "renamed";
    public const string Copied = "copied";
    public const string TypeChanged = "typeChanged";
    public const string Unmerged = "unmerged";
    /// <summary>Not tracked by git yet (a new file that isn't added).</summary>
    public const string Untracked = "untracked";
}

/// <summary>One file git reports as changed (paths relative to the repository root, '/' separators).</summary>
public sealed record GitChange(string Path, string Status, string? OldPath = null)
{
    /// <summary>Changed line ranges on the new side; null = whole file (added, deleted, untracked, binary).</summary>
    public List<LineSpan>? Lines { get; set; }
}

/// <summary>
/// The changes of one base: files plus what was compared. <paramref name="From"/> is the revision the changes start from
/// (the old side of every file), null when there is none (no commits, a root commit).
/// </summary>
public sealed record GitChanges(List<GitChange> Files, string? Ref, string? MergeBase, List<string> Warnings, string? From = null);

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
            ProcessUtil.TryKill(proc);
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

    /// <summary>The text of <paramref name="path"/> (relative to the repository root) at <paramref name="rev"/>; null when it does not exist there.</summary>
    public async Task<string?> FileAtAsync(string rev, string path, CancellationToken ct = default)
    {
        var (code, output, _) = await Git(ct, "show", $"{rev}:{path}");
        return code == 0 ? output : null;
    }

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
        return new GitChanges(Sort(files), hasHead ? "HEAD" : null, null, [], hasHead ? "HEAD" : null);
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
        return new GitChanges(Sort(files), baseRef, mergeBase, warnings, from);
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
            return new GitChanges(Sort(await DiffAsync([reference], ct)), reference, null, [], await RangeFromAsync(reference, sep, ct));
        }
        if (!await RefExistsAsync(reference, ct)) throw new AffectedException(404, "unknownRef", $"Unknown git ref '{reference}'.");
        var parent = await RefExistsAsync(reference + "^1", ct) ? reference + "^1" : await EmptyTreeAsync(ct);
        return new GitChanges(Sort(await DiffAsync([parent, reference], ct)), reference, null, [], await RefExistsAsync(reference + "^1", ct) ? parent : null);
    }

    /// <summary>The old side of "A..B" (A) or "A...B" (their merge base); null when it can't be determined.</summary>
    private async Task<string?> RangeFromAsync(string reference, string sep, CancellationToken ct)
    {
        var sides = reference.Split(sep).Select(s => s.Length == 0 ? "HEAD" : s).ToArray();
        if (sep == "..") return sides[0];
        var (code, output, _) = await Git(ct, "merge-base", sides[0], sides[1]);
        return code == 0 && output.Trim() is { Length: > 0 } mb ? mb : null;
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
            foreach (var f in files.Where(f => f.Status is GitStatuses.Modified or GitStatuses.Renamed or GitStatuses.Copied or GitStatuses.TypeChanged))
                if (hunks.TryGetValue(f.Path, out var lines)) f.Lines = lines;
        }
        return files;
    }

    private async Task<List<GitChange>> UntrackedAsync(CancellationToken ct)
    {
        var (code, output, _) = await Git(ct, "ls-files", "--others", "--exclude-standard", "-z", "--full-name");
        if (code != 0) return [];
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => new GitChange(p, GitStatuses.Untracked)).ToList();
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
                result.Add(new GitChange(parts[i + 2], status[0] == 'R' ? GitStatuses.Renamed : GitStatuses.Copied, parts[i + 1]));
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
        'A' => GitStatuses.Added, 'D' => GitStatuses.Deleted, 'T' => GitStatuses.TypeChanged, 'U' => GitStatuses.Unmerged, _ => GitStatuses.Modified,
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
