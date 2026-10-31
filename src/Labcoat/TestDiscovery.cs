using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Labcoat;

// Discovery: asks the project's test host for its tests and replaces the project's part of the tree with them, keeping
// the results of tests that still exist and restoring the previous session's for the others (HistoryStore.cs).
internal sealed class TestDiscovery(WorkspaceState state, IExplorerEffects effects, TestHostPool hosts, HistoryStore history, Action publishState)
{
    // ExplorerState.historyStamps: per project, bumped when discovery restored a previous session's history (lock Sync)
    private readonly Dictionary<string, int> _historyStamps = [];

    /// <summary>The history stamp of a project (caller holds Sync): lean clients fetch its history again when it changes.</summary>
    public int HistoryStampLocked(string projectId) => _historyStamps.GetValueOrDefault(projectId);

    /// <summary>Discovers the project unless its output is unchanged since its last discovery.</summary>
    public async Task<bool> EnsureDiscoveredAsync(ProjectEntry p, CancellationToken ct)
    {
        var stamp = File.GetLastWriteTimeUtc(p.TargetPath!);
        if (stamp == p.DiscoveredDllStamp) return true;
        return await DiscoverAsync(p, ct);
    }

    public async Task<bool> DiscoverAsync(ProjectEntry p, CancellationToken ct)
    {
        effects.SetStatus(p, ProjectStatus.Discovering);
        var stamp = File.GetLastWriteTimeUtc(p.TargetPath!);
        var found = new List<TestNode>();
        var runId = Guid.NewGuid();
        try
        {
            var sw = Stopwatch.StartNew();
            await hosts.RequestAsync(p, (method, prm) => CollectDiscovered(p, found, method, prm), (conn, token) => conn.DiscoverAsync(runId, token), ct);
            effects.Log(p.Project.Id, LogLevels.Info, $"Discovered {found.Count} tests in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            effects.SetStatus(p, ProjectStatus.Idle);
            return false;
        }
        catch (Exception ex)
        {
            lock (state.Sync) p.Project.Diagnostics = ProjectDiagnostics.Diagnose(p.Properties, p.PackageReferences, p.Project.Path, p.TargetPath, ex);
            effects.SetStatus(p, ProjectStatus.Error, "Discovery failed: " + ex.Message);
            effects.Log(p.Project.Id, LogLevels.Error, "Discovery failed: " + ex.Message);
            return false;
        }

        ReplaceProjectTests(p, found, stamp);
        return true;
    }

    private static void CollectDiscovered(ProjectEntry p, List<TestNode> found, string method, JsonNode? prm)
    {
        if (method != "testing/testUpdates/tests" || prm?["changes"] is not JsonArray changes) return;
        lock (found)
        {
            foreach (var c in changes)
            {
                if (c?["node"] is JsonObject node && MtpMapping.IsTestNode(node))
                    found.Add(MtpMapping.ToTestNode(p.Project.Id, node));
            }
        }
    }

    /// <summary>Replaces the project's tests with the discovered ones, keeping previous results for tests that still exist.</summary>
    private void ReplaceProjectTests(ProjectEntry p, List<TestNode> found, DateTime stamp)
    {
        lock (state.Sync)
        {
            var newKeys = new HashSet<(string, string)>();
            var newOrder = new List<(string, string)>();
            var restoredHistory = false;
            foreach (var n in found)
            {
                var key = (p.Project.Id, n.Id);
                if (!newKeys.Add(key)) continue;
                if (state.Tests.TryGetValue(key, out var old)) CarryOverResult(old, n);
                else
                {
                    history.Restore(n); // previous session's result + history
                    if (n.History is { Count: > 0 }) restoredHistory = true;
                }
                state.Tests[key] = n;
                newOrder.Add(key);
            }
            foreach (var key in state.Order.Where(k => k.ProjectId == p.Project.Id && !newKeys.Contains(k)).ToList())
                state.Tests.Remove(key);

            // Keep project grouping order: insert at the position of the project's first test (or the end).
            var firstIndex = FirstTestIndexLocked(p);
            state.Order.RemoveAll(k => k.ProjectId == p.Project.Id);
            state.Order.InsertRange(firstIndex < 0 ? state.Order.Count : Math.Min(firstIndex, state.Order.Count), newOrder);

            // lean clients fetch history on demand: tell them this project's changed (ExplorerState.historyStamps)
            if (restoredHistory) _historyStamps[p.Project.Id] = _historyStamps.GetValueOrDefault(p.Project.Id) + 1;
            p.DiscoveredDllStamp = stamp;
            p.Project.Status = ProjectStatus.Idle;
            p.Project.LastError = null;
            p.Project.BuildOutput = null;
            p.Project.BuildErrors = null;
            p.Project.BuildErrorCount = null;
            p.Project.BuildWarningCount = null;
            p.Project.Diagnostics = found.Count == 0 ? ProjectDiagnostics.Diagnose(p.Properties, p.PackageReferences, p.Project.Path, p.TargetPath, null) : null;
            effects.Emit(new ProjectStatusEvent(p.Project.Copy()));
            publishState();
        }
    }

    /// <summary>Where a project's first discovered tests go in the test order (caller holds Sync): after the tests of the
    /// projects before it, so the tree keeps the project order however discoveries finish in parallel.</summary>
    private int FirstTestIndexLocked(ProjectEntry p)
    {
        var existing = state.Order.FindIndex(k => k.ProjectId == p.Project.Id);
        if (existing >= 0) return existing;
        var before = state.Projects.TakeWhile(x => x != p).Select(x => x.Project.Id).ToHashSet(StringComparer.Ordinal);
        var last = state.Order.FindLastIndex(k => before.Contains(k.ProjectId));
        return last + 1;
    }

    private static void CarryOverResult(TestNode old, TestNode n)
    {
        n.Outcome = old.Outcome;
        n.DurationMs = old.DurationMs;
        n.ErrorMessage = old.ErrorMessage;
        n.ErrorType = old.ErrorType;
        n.StackTrace = old.StackTrace;
        n.StdOut = old.StdOut;
        n.TraceId = old.TraceId;
        n.StdErr = old.StdErr;
        n.LastRunAt = old.LastRunAt;
        HistoryStore.CarryOver(old, n);
    }
}
