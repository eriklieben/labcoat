namespace Labcoat;

// Verify snapshot mismatches (Snapshots.cs): find a failed test's received / verified files, accept or reject them.
public sealed partial class Explorer
{
    /// <summary>GET /api/tests/snapshots: the pending snapshots of a test; null = unknown test.</summary>
    public TestSnapshots? GetSnapshots(string? projectId, string id)
    {
        TestNode t;
        string projectDir;
        lock (Sync)
        {
            var key = _order.FirstOrDefault(k => k.Id == id && (projectId is null || k.ProjectId == projectId));
            if (key.Id is null) return null;
            t = _tests[key].Copy();
            projectDir = ProjectDirLocked(t.ProjectId);
        }
        if (!t.HasSnapshotMismatch.GetValueOrDefault()) return new TestSnapshots(t.ProjectId, t.Id, null, []);
        var (dir, files) = VerifySnapshots.Find(t, projectDir);
        return new TestSnapshots(t.ProjectId, t.Id, dir, files);
    }

    /// <summary>
    /// POST /api/snapshots/accept (<paramref name="accept"/>) and /reject. One test (<c>Id</c>, optionally only
    /// <c>Names</c>) or every test with a snapshot mismatch (of <c>ProjectId</c>). Accept moves received -> verified,
    /// reject deletes the received files; then the tests' <c>hasSnapshotMismatch</c> clears (testsUpdated), and accept
    /// re-runs them unless <c>Rerun</c> is false. 404 unknown project / test / name, 409 while the test runs.
    /// </summary>
    public (int Status, SnapshotActionResult? Result, string? Error) ResolveSnapshots(SnapshotRequest req, bool accept)
    {
        var (targets, status, error) = SnapshotTargets(req);
        if (error is not null) return (status, null, error);

        var done = new List<SnapshotTestResult>();
        var errors = new List<SnapshotError>();
        foreach (var (t, projectDir) in targets)
        {
            if (ResolveTestSnapshots(t, projectDir, req, accept, done, errors) is { } notFound) return (404, null, notFound);
        }

        // The test's received files are handled (or there were none): its mismatch marker goes until its next result.
        MarkSnapshotsResolved(done.Where(d => IsFullyResolved(d, req, targets, errors)).ToList());

        var runId = accept && (req.Rerun ?? true) ? RerunAccepted(done) : null;
        return (200, new SnapshotActionResult(done, errors.Count > 0 ? errors : null, runId), null);
    }

    /// <summary>The tests a snapshot request is about (copies, with their project directory), or the error.</summary>
    private (List<(TestNode Test, string ProjectDir)> Targets, int Status, string? Error) SnapshotTargets(SnapshotRequest req)
    {
        lock (Sync)
        {
            if (req.ProjectId is not null && !_projects.Any(p => p.Project.Id == req.ProjectId)) return ([], 404, $"Unknown project '{req.ProjectId}'");
            if (req.Id is not null) return SnapshotTargetLocked(req);
            if (req.Names is { Count: > 0 }) return ([], 400, "names needs an id");
            var targets = _order.Select(k => _tests[k])
                .Where(t => (req.ProjectId is null || t.ProjectId == req.ProjectId) && t.HasSnapshotMismatch == true
                            && t.Outcome is not (Outcome.Running or Outcome.Queued))
                .Select(t => (t.Copy(), ProjectDirLocked(t.ProjectId)))
                .ToList();
            return (targets, 200, null);
        }
    }

    private (List<(TestNode Test, string ProjectDir)> Targets, int Status, string? Error) SnapshotTargetLocked(SnapshotRequest req)
    {
        var key = _order.FirstOrDefault(k => k.Id == req.Id && (req.ProjectId is null || k.ProjectId == req.ProjectId));
        if (key.Id is null) return ([], 404, "Unknown test");
        var t = _tests[key];
        if (t.Outcome is Outcome.Running or Outcome.Queued) return ([], 409, "The test is running.");
        return ([(t.Copy(), ProjectDirLocked(t.ProjectId))], 200, null);
    }

    /// <summary>Accepts / rejects one test's pending snapshots; the error when a requested name is not pending.</summary>
    private string? ResolveTestSnapshots(TestNode t, string projectDir, SnapshotRequest req, bool accept, List<SnapshotTestResult> done,
        List<SnapshotError> errors)
    {
        var (_, found) = t.HasSnapshotMismatch == true ? VerifySnapshots.Find(t, projectDir) : (null, []);
        var selected = req.Names is { Count: > 0 } names ? found.Where(f => names.Contains(f.Name)).ToList() : found;
        if (req.Id is not null && req.Names is { Count: > 0 } && selected.Count == 0)
            return $"No pending snapshot named {string.Join(", ", req.Names)} (pending: {string.Join(", ", found.Select(f => f.Name))})";
        var files = ResolveSnapshotFiles(t, projectDir, selected, accept, errors);
        if (files.Count > 0 || found.Count == 0) done.Add(new SnapshotTestResult(t.ProjectId, t.Id, files));
        if (files.Count > 0)
            Log(t.ProjectId, LogLevels.Info, $"{(accept ? "Accepted" : "Rejected")} {files.Count} snapshot{Plural(files.Count)} of {t.DisplayName}: {string.Join(", ", files)}");
        return null;
    }

    /// <summary>Moves (accept) or deletes (reject) the received files; returns the names done, problems go to <paramref name="errors"/>.</summary>
    private static List<string> ResolveSnapshotFiles(TestNode t, string projectDir, List<SnapshotFile> selected, bool accept, List<SnapshotError> errors)
    {
        var files = new List<string>();
        foreach (var f in selected)
        {
            if (VerifySnapshots.Validate(f, projectDir) is { } invalid)
            {
                errors.Add(new SnapshotError(t.ProjectId, t.Id, f.Name, invalid));
                continue;
            }
            try
            {
                if (accept) VerifySnapshots.Accept(f);
                else VerifySnapshots.Reject(f);
                files.Add(f.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new SnapshotError(t.ProjectId, t.Id, f.Name, ex.Message));
            }
        }
        return files;
    }

    /// <summary>No errors for the test, and (when only some names were handled) no pending snapshot left.</summary>
    private static bool IsFullyResolved(SnapshotTestResult d, SnapshotRequest req, List<(TestNode Test, string ProjectDir)> targets, List<SnapshotError> errors)
    {
        if (errors.Any(e => e.ProjectId == d.ProjectId && e.Id == d.Id)) return false;
        if (req.Names is not { Count: > 0 }) return true;
        var target = targets.First(x => x.Test.ProjectId == d.ProjectId && x.Test.Id == d.Id);
        return VerifySnapshots.Find(target.Test, target.ProjectDir).Files.Count == 0;
    }

    private void MarkSnapshotsResolved(List<SnapshotTestResult> resolved)
    {
        lock (Sync)
        {
            var changed = new List<TestNode>();
            foreach (var d in resolved)
                if (_tests.TryGetValue((d.ProjectId, d.Id), out var node) && node.HasSnapshotMismatch == true)
                {
                    node.SnapshotsResolved = true;
                    changed.Add(node);
                }
            PublishTests(changed);
        }
    }

    /// <summary>Re-runs the tests whose snapshots were accepted; the run id, null when none were.</summary>
    private string? RerunAccepted(List<SnapshotTestResult> done)
    {
        var ran = done.Where(d => d.Files.Count > 0).ToList();
        if (ran.Count == 0) return null;
        var pids = ran.Select(d => d.ProjectId).Distinct().ToList();
        var r = RequestRun(new RunRequestOptions { ProjectId = pids.Count == 1 ? pids[0] : null, TestIds = ran.Select(d => d.Id).Distinct().ToList(), Trigger = "user" });
        return r.RunId;
    }

    /// <summary>Opt back into warm hosts for Verify projects (e.g. when the tests call VerifierSettings.DisableRequireUniquePrefix()).</summary>
    public const string VerifyWarmEnvVar = "LABCOAT_VERIFY_WARM";

    private sealed partial class ProjectEntry
    {
        /// <summary>The "Verify: every request starts a new test host" line was logged.</summary>
        public bool VerifyColdLogged;
    }

    /// <summary>
    /// Verify allows each snapshot file prefix once per process (a static set; "The prefix has already been used"), so a
    /// warm test host could run a Verify test only once. Projects that reference a Verify package therefore start a new
    /// test host for every request, unless LABCOAT_VERIFY_WARM=1.
    /// </summary>
    private bool ColdForVerify(ProjectEntry p)
    {
        if (!UsesVerify(p.PackageReferences) || Environment.GetEnvironmentVariable(VerifyWarmEnvVar) is "1" or "true") return false;
        if (!p.VerifyColdLogged)
        {
            p.VerifyColdLogged = true;
            Log(p.Project.Id, LogLevels.Info, "Verify snapshot tests: every request starts a new test host (Verify allows each snapshot once per " +
                $"process). Set {VerifyWarmEnvVar}=1 to keep it warm if your tests call VerifierSettings.DisableRequireUniquePrefix().");
        }
        return true;
    }

    /// <summary>Verify, Verify.MSTest, Verify.NUnit, Verify.XunitV3, Verify.TUnit, … among the package references.</summary>
    public static bool UsesVerify(IEnumerable<string> packageReferences) =>
        packageReferences.Any(r => r.Equals("Verify", StringComparison.OrdinalIgnoreCase) || r.StartsWith("Verify.", StringComparison.OrdinalIgnoreCase));

    private string ProjectDirLocked(string projectId) =>
        Path.GetDirectoryName(_projects.First(p => p.Project.Id == projectId).Project.Path)!;
}
