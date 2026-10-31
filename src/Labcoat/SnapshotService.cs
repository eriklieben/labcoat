namespace Labcoat;

// Verify snapshot mismatches (Snapshots.cs): find a failed test's received / verified files, accept or reject them.
internal sealed class SnapshotService(WorkspaceState state, IExplorerEffects effects, Func<Explorer.RunRequestOptions, Explorer.RunRequestResult> requestRun)
{
    /// <summary>GET /api/tests/snapshots: the pending snapshots of a test; null = unknown test.</summary>
    public TestSnapshots? Get(string? projectId, string id)
    {
        TestNode t;
        string projectDir;
        lock (state.Sync)
        {
            var key = state.FindKey(projectId, id);
            if (key.Id is null) return null;
            t = state.Tests[key].Copy();
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
    public (int Status, SnapshotActionResult? Result, string? Error) Resolve(SnapshotRequest req, bool accept)
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
        lock (state.Sync)
        {
            if (req.ProjectId is not null && state.FindProject(req.ProjectId) is null) return ([], 404, $"Unknown project '{req.ProjectId}'");
            if (req.Id is not null) return SnapshotTargetLocked(req);
            if (req.Names is { Count: > 0 }) return ([], 400, "names needs an id");
            var targets = state.Order.Select(k => state.Tests[k])
                .Where(t => (req.ProjectId is null || t.ProjectId == req.ProjectId) && t.HasSnapshotMismatch == true
                            && t.Outcome is not (Outcome.Running or Outcome.Queued))
                .Select(t => (t.Copy(), ProjectDirLocked(t.ProjectId)))
                .ToList();
            return (targets, 200, null);
        }
    }

    private (List<(TestNode Test, string ProjectDir)> Targets, int Status, string? Error) SnapshotTargetLocked(SnapshotRequest req)
    {
        var key = state.FindKey(req.ProjectId, req.Id);
        if (key.Id is null) return ([], 404, "Unknown test");
        var t = state.Tests[key];
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
            effects.Log(t.ProjectId, LogLevels.Info, $"{(accept ? "Accepted" : "Rejected")} {files.Count} snapshot{Words.Plural(files.Count)} of {t.DisplayName}: {string.Join(", ", files)}");
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
        lock (state.Sync)
        {
            var changed = new List<TestNode>();
            foreach (var d in resolved)
                if (state.Tests.TryGetValue((d.ProjectId, d.Id), out var node) && node.HasSnapshotMismatch == true)
                {
                    node.SnapshotsResolved = true;
                    changed.Add(node);
                }
            effects.PublishTests(changed);
        }
    }

    /// <summary>Re-runs the tests whose snapshots were accepted; the run id, null when none were.</summary>
    private string? RerunAccepted(List<SnapshotTestResult> done)
    {
        var ran = done.Where(d => d.Files.Count > 0).ToList();
        if (ran.Count == 0) return null;
        var pids = ran.Select(d => d.ProjectId).Distinct().ToList();
        var r = requestRun(new Explorer.RunRequestOptions { ProjectId = pids.Count == 1 ? pids[0] : null, TestIds = ran.Select(d => d.Id).Distinct().ToList(), Trigger = "user" });
        return r.RunId;
    }

    private string ProjectDirLocked(string projectId) =>
        Path.GetDirectoryName(state.FindProject(projectId)!.Project.Path)!;
}
