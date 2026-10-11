namespace Labcoat;

// Run export (JUnit XML, HTML report; LabcoatApp.Export.cs, CliRun.cs): a persisted run joined with what the explorer
// knows about its tests.
public sealed partial class Explorer
{
    /// <summary>The run id <c>latest</c> stands for: the newest persisted run.</summary>
    public const string LatestRun = "latest";

    /// <summary>
    /// <paramref name="runId"/> (or <see cref="LatestRun"/>) as an export: the persisted run's results in tree order with
    /// the tests' names; error type and console output come from the test's current result when that is still this
    /// run's (a past run only keeps outcome, duration, error message and stack trace). A finished run without any
    /// result (nothing ran) is exported empty. Null when the run is unknown.
    /// </summary>
    public RunExport? ExportRun(string runId)
    {
        var id = runId == LatestRun ? Runs().FirstOrDefault()?.RunId ?? LastRunId() : runId;
        if (id is null) return null;
        var details = _history.GetRun(id);
        lock (Sync)
        {
            if (details is null)
            {
                if (_lastRun?.RunId != id) return null;
                details = new RunDetails(_lastRun.Copy(), []);
            }
            var quarantined = _settings.Quarantined().ToHashSet(StringComparer.Ordinal);
            var order = new Dictionary<(string, string), int>(_order.Count);
            for (var i = 0; i < _order.Count; i++) order[_order[i]] = i;
            var tests = details.Results
                .Select(r => ExportTestLocked(r, id, quarantined))
                .OrderBy(t => order.TryGetValue((t.ProjectId, t.Id), out var i) ? i : int.MaxValue)
                .ToList();
            var withResults = tests.Select(t => t.ProjectId).ToHashSet(StringComparer.Ordinal);
            var root = _inputRoots.FirstOrDefault();
            var projects = _projects.Select(p => p.Project)
                .Where(p => withResults.Contains(p.Id))
                .Select(p => new ExportProject(p.Id, p.Name, p.TargetFramework, RelativeTo(root, p.Path)))
                .ToList();
            // results of projects that are gone since (removed from the workspace): still exported, under their id
            projects.AddRange(withResults.Where(pid => projects.TrueForAll(p => p.Id != pid)).Select(pid => new ExportProject(pid, pid, null, null)));
            return new RunExport
            {
                WorkspaceName = _workspaceName,
                Version = Cli.Version,
                Run = details.Run,
                Projects = projects,
                Tests = tests,
                GeneratedAt = Json.Now(),
            };
        }
    }

    private string? LastRunId()
    {
        lock (Sync) return _lastRun?.RunId;
    }

    private ExportTest ExportTestLocked(RunResult r, string runId, HashSet<string> quarantined)
    {
        _tests.TryGetValue((r.ProjectId, r.Id), out var t);
        // The test's current result is this run's: it has the full error, its type and the console output.
        var live = t is not null && t.Outcome == r.Outcome && t.History is { Count: > 0 } h && h[^1].RunId == runId;
        return new ExportTest
        {
            ProjectId = r.ProjectId,
            Id = r.Id,
            Name = t?.DisplayName ?? r.Id,
            Namespace = t?.Namespace,
            ClassName = t?.ClassName,
            Outcome = r.Outcome,
            DurationMs = r.DurationMs,
            ErrorMessage = live ? t!.ErrorMessage : r.ErrorMessage,
            ErrorType = live ? t!.ErrorType : null,
            StackTrace = live ? t!.StackTrace : r.StackTrace,
            StdOut = live ? t!.StdOut : null,
            StdErr = live ? t!.StdErr : null,
            Quarantined = quarantined.Contains(WorkspaceSettings.QuarantineKey(r.ProjectId, r.Id)),
        };
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> with '/' separators, or its file name when outside it.</summary>
    internal static string RelativeTo(string? root, string path)
    {
        if (root is not null && PathUtil.IsUnder(Path.GetFullPath(path), Path.GetFullPath(root)))
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        return Path.GetFileName(path);
    }

    /// <summary>
    /// The workspace root coverage exports make paths relative to: the git repository root of the workspace (what CI
    /// coverage services expect), else the common root of the input paths, the test projects and the projects they
    /// reference. Null when that is a file system root.
    /// </summary>
    public string? ExportRoot
    {
        get
        {
            List<string> roots;
            lock (Sync) roots = SourceRootsLocked();
            var common = PathUtil.CommonRoot(roots) ?? _inputRoots.FirstOrDefault();
            if (common is null) return null;
            var root = GitRoot(common) ?? common;
            return Path.GetPathRoot(root) == root ? null : root;
        }
    }

    /// <summary>The nearest directory at or above <paramref name="dir"/> that contains .git (a directory or a worktree's file).</summary>
    private static string? GitRoot(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return null;
    }

    /// <summary>The workspace name for export file names.</summary>
    public string ExportWorkspaceName => _workspaceName;
}
