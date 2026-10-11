using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>
/// Which workspace a set of test projects belongs to, and so which data folder (history, settings, coverage report) it
/// uses: the git repository root when there is one, else the solution file found walking up from the projects, else
/// their common root directory. The folder is <c>&lt;name&gt;-&lt;hash of the normalized path&gt;</c> under
/// <see cref="BaseDirectory"/>, so adding, removing or renaming a project keeps the folder (and its history).
/// </summary>
public sealed class WorkspaceIdentity
{
    /// <summary>"git", "solution", "directory" or "none" (no projects).</summary>
    public string Kind { get; }
    /// <summary>The identity path: the repository root, the solution file or the common root (full path, no trailing separator).</summary>
    public string Path { get; }
    /// <summary>The directory project paths are made relative to (<see cref="RelativeProjectPath"/>).</summary>
    public string Root { get; }
    /// <summary><see cref="Path"/>, case-folded where the file system is case-insensitive: what the folder name hashes.</summary>
    public string Key { get; }
    /// <summary>The data folder's name under <see cref="BaseDirectory"/>.</summary>
    public string FolderName { get; }
    /// <summary>
    /// A common root that is the home or temp directory or a file system root (projects spread over unrelated places):
    /// usable as a key, but too broad to claim the old data folders of everything below it.
    /// </summary>
    public bool IsBroad { get; private init; }

    private WorkspaceIdentity(string kind, string path, string root, bool caseInsensitive)
    {
        Kind = kind;
        Path = path;
        Root = root;
        Key = Normalize(path, caseInsensitive);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + Key)))[..16];
        FolderName = $"{FolderLabel(kind, Key)}-{hash}"; // from the key: C:\Repo and c:\repo are one folder
    }

    /// <summary>Windows and macOS (by default) have case-insensitive file systems: <c>C:\Repo</c> and <c>c:\repo</c> are one workspace.</summary>
    public static bool CaseInsensitiveFileSystem => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Compares project paths (relative or full) the way the file system does.</summary>
    public static StringComparer PathComparer => CaseInsensitiveFileSystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Labcoat's data directory: <c>$LABCOAT_HISTORY_DIR</c>, else LocalApplicationData/labcoat.</summary>
    public static string BaseDirectory =>
        Environment.GetEnvironmentVariable("LABCOAT_HISTORY_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "labcoat");

    /// <summary>This workspace's data folder.</summary>
    public string DataDirectory => System.IO.Path.Combine(BaseDirectory, FolderName);

    /// <summary>
    /// Full path, trailing separator trimmed, and lower case when <paramref name="caseInsensitive"/> (the hashed key only:
    /// paths shown or stored keep their case).
    /// </summary>
    public static string Normalize(string path, bool caseInsensitive)
    {
        if (path.Length == 0) return "";
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        return caseInsensitive ? full.ToLowerInvariant() : full;
    }

    /// <summary>The workspace of <paramref name="projectPaths"/> (see the class remarks).</summary>
    /// <param name="home">The home directory (never a workspace root; default: the user's).</param>
    public static WorkspaceIdentity Resolve(IEnumerable<string> projectPaths, string? home = null) =>
        Resolve(projectPaths, home, CaseInsensitiveFileSystem);

    internal static WorkspaceIdentity Resolve(IEnumerable<string> projectPaths, string? home, bool caseInsensitive)
    {
        var projects = projectPaths.Select(p => PathUtil.CanonicalizeOrSelf(System.IO.Path.GetFullPath(p))).ToList();
        if (PathUtil.CommonRoot(projects) is not { } common) return new WorkspaceIdentity("none", "", "", caseInsensitive);
        common = System.IO.Path.TrimEndingDirectorySeparator(common);
        var boundaries = Boundaries(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (FindGitRoot(common, boundaries) is { } git) return new WorkspaceIdentity("git", git, git, caseInsensitive);
        if (FindSolution(common, boundaries) is { } solution)
            return new WorkspaceIdentity("solution", solution, System.IO.Path.GetDirectoryName(solution)!, caseInsensitive);
        return new WorkspaceIdentity("directory", common, common, caseInsensitive)
        {
            IsBroad = boundaries.Contains(common) || System.IO.Path.GetPathRoot(common) is { } fsRoot && System.IO.Path.TrimEndingDirectorySeparator(fsRoot) == common,
        };
    }

    /// <summary>Directories never taken as a workspace root, nor searched above: the home and temp directories.</summary>
    private static HashSet<string> Boundaries(string home)
    {
        var set = new HashSet<string>(PathComparer);
        foreach (var d in new[] { home, System.IO.Path.GetTempPath() }.Where(d => !string.IsNullOrEmpty(d)))
            set.Add(System.IO.Path.TrimEndingDirectorySeparator(PathUtil.CanonicalizeOrSelf(System.IO.Path.GetFullPath(d))));
        return set;
    }

    /// <summary>The nearest directory with <c>.git</c> (a directory, or a file in a worktree / submodule), walking up.</summary>
    private static string? FindGitRoot(string start, HashSet<string> boundaries)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        {
            var dir = System.IO.Path.TrimEndingDirectorySeparator(d.FullName);
            if (boundaries.Contains(dir)) return null; // a dotfiles repository in ~ is not every project's workspace
            var git = System.IO.Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return dir;
        }
        return null;
    }

    /// <summary>The nearest .slnx (else .sln) walking up, below the home / temp directory and the file system root.</summary>
    private static string? FindSolution(string start, HashSet<string> boundaries)
    {
        for (var d = new DirectoryInfo(start); d.Parent is not null; d = d.Parent)
        {
            var dir = System.IO.Path.TrimEndingDirectorySeparator(d.FullName);
            if (boundaries.Contains(dir)) return null;
            try
            {
                foreach (var pattern in new[] { "*.slnx", "*.sln" })
                {
                    var found = Directory.GetFiles(dir, pattern).Where(f => !f.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
                        .Order(StringComparer.Ordinal).FirstOrDefault();
                    if (found is not null) return found;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    /// <summary>A readable prefix for the folder name: the repository / solution / directory name (safe characters only).</summary>
    private static string FolderLabel(string kind, string path)
    {
        var name = kind == "solution" ? System.IO.Path.GetFileNameWithoutExtension(path) : System.IO.Path.GetFileName(path);
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (sb.Length == 40) break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }
        return sb.Length == 0 ? "workspace" : sb.ToString();
    }

    /// <summary>
    /// A project's path relative to <see cref="Root"/> with '/' separators (the key its history is kept under); the full
    /// path for a project outside the root.
    /// </summary>
    public string RelativeProjectPath(string projectPath)
    {
        var full = PathUtil.CanonicalizeOrSelf(System.IO.Path.GetFullPath(projectPath));
        if (Root.Length > 0)
        {
            var rel = System.IO.Path.GetRelativePath(Root, full);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(rel)) return rel.Replace('\\', '/');
        }
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// The project ids the explorer gives <paramref name="projectPaths"/> (in this order): the file name without extension,
    /// with "-2", "-3", … for repeated names (case-insensitive).
    /// </summary>
    public static List<string> ProjectIds(IEnumerable<string> projectPaths)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (var path in projectPaths)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var id = name;
            var suffix = 2;
            while (!used.Add(id)) id = $"{name}-{suffix++}";
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>workspace.json in the data folder: what the folder belongs to (for people looking at the folder, and the migration marker).</summary>
    internal JsonObject Describe() => new()
    {
        ["kind"] = Kind,
        ["path"] = Path,
        ["root"] = Root,
    };
}

/// <summary>
/// The current project ids and their paths relative to the workspace root (<see cref="WorkspaceIdentity.RelativeProjectPath"/>):
/// what the workspace data files key per-project data by, since an id can change across restarts (a project with the
/// file name of another one is "Name-2" depending on the order the search lists them, or the one it repeated is gone).
/// <see cref="HistoryStore"/> keeps it up to date while projects are added and removed.
/// </summary>
public interface IProjectKeys
{
    /// <summary>The relative path of a current project id; null when no current project has it.</summary>
    string? ProjectPathOf(string projectId);

    /// <summary>The id of the current project at a relative path; null when it is not part of the workspace now.</summary>
    string? ProjectIdOf(string relativePath);
}

/// <summary>
/// One Labcoat process per workspace data folder may write it: an exclusive lock on <c>instance.lock</c> (released by the
/// OS when the process ends, so a crash leaves no stale lock), with the owner's pid in <c>instance.json</c>. Explorers in
/// the same process share the lock. A process that doesn't get it reads the data but writes nothing
/// (<see cref="CanWrite"/>), until it can take the lock over (<see cref="Lease.TryUpgrade"/>).
/// </summary>
public static class WorkspaceLock
{
    public const string LockFileName = "instance.lock";
    public const string OwnerFileName = "instance.json";

    private sealed class Holder(FileStream stream)
    {
        public FileStream Stream { get; } = stream;
        public int Refs;
    }

    private static readonly object s_gate = new();
    private static readonly Dictionary<string, Holder> s_held = new(WorkspaceIdentity.PathComparer);
    private static readonly HashSet<string> s_denied = new(WorkspaceIdentity.PathComparer);

    /// <summary>A use of a data folder; <see cref="Owner"/> = this process may write it.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly string _directory;
        private bool _disposed;

        internal Lease(string directory, bool owner, int? otherPid)
        {
            _directory = directory;
            Owner = owner;
            OtherPid = otherPid;
        }

        public bool Owner { get; private set; }
        /// <summary>The pid of the process holding the lock (when known and not us).</summary>
        public int? OtherPid { get; }

        /// <summary>Not the owner: try to take the lock (the other instance ended); true when this process owns it now.</summary>
        public bool TryUpgrade()
        {
            lock (s_gate)
            {
                if (_disposed) return false;
                if (Owner) return true;
                if (!TryHoldLocked(_directory, out _)) return false;
                Owner = true;
                return true;
            }
        }

        public void Dispose()
        {
            lock (s_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (!Owner || !s_held.TryGetValue(_directory, out var h) || --h.Refs > 0) return;
                s_held.Remove(_directory);
                h.Stream.Dispose();
            }
        }
    }

    private static string Key(string directory) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));

    /// <summary>Takes (or shares, within this process) the lock of <paramref name="directory"/>, creating the folder.</summary>
    public static Lease Acquire(string directory)
    {
        var dir = Key(directory);
        lock (s_gate)
        {
            if (TryHoldLocked(dir, out var otherPid)) return new Lease(dir, owner: true, null);
            return new Lease(dir, owner: false, otherPid);
        }
    }

    private static bool TryHoldLocked(string dir, out int? otherPid)
    {
        otherPid = null;
        if (s_held.TryGetValue(dir, out var held))
        {
            held.Refs++;
            return true;
        }
        try
        {
            Directory.CreateDirectory(dir);
            var stream = new FileStream(System.IO.Path.Combine(dir, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            s_held[dir] = new Holder(stream) { Refs = 1 };
            s_denied.Remove(dir);
            WriteOwner(dir);
            return true;
        }
        catch (IOException)
        {
            s_denied.Add(dir);
            otherPid = ReadOwnerPid(dir);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // a read-only data folder: writing fails later with its own warning; nothing to coordinate
        }
    }

    private static void WriteOwner(string dir)
    {
        try
        {
            var owner = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["startedAt"] = Json.Now(),
                ["version"] = Cli.Version,
            };
            File.WriteAllText(System.IO.Path.Combine(dir, OwnerFileName), owner.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only informational */ }
    }

    internal static int? ReadOwnerPid(string dir)
    {
        try
        {
            var text = File.ReadAllText(System.IO.Path.Combine(dir, OwnerFileName));
            return JsonNode.Parse(text)?["pid"]?.GetValue<int>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// False while another process holds <paramref name="directory"/>'s lock and this one doesn't: then the stores of this
    /// process don't write there. True for a folder nobody leased (e.g. a store used on its own).
    /// </summary>
    public static bool CanWrite(string directory)
    {
        var dir = Key(directory);
        lock (s_gate) return !s_denied.Contains(dir);
    }
}
