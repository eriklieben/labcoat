namespace Labcoat;

/// <summary>
/// The lifecycle of the latest 200 run requests (<c>GET /api/runs/{runId}/status</c>): set by the queue
/// (Explorer.Runs.cs) and by the run itself (RunExecutor.cs).
/// </summary>
internal sealed class RunStatuses
{
    private const int Kept = 200;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Explorer.RunRequestStatus> _byId = [];
    private readonly Queue<string> _order = new();

    public void Set(string runId, string state, string? reason = null, RunSummary? run = null, string? supersededBy = null)
    {
        lock (_gate)
        {
            if (!_byId.ContainsKey(runId))
            {
                _order.Enqueue(runId);
                while (_order.Count > Kept) _byId.Remove(_order.Dequeue());
            }
            _byId[runId] = new Explorer.RunRequestStatus(runId, state, reason, run?.Copy(), supersededBy);
        }
    }

    public Explorer.RunRequestStatus? Get(string runId)
    {
        lock (_gate) return _byId.GetValueOrDefault(runId);
    }
}
