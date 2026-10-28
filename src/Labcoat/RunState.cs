namespace Labcoat;

/// <summary>
/// Where the runs are: the one in progress, the last finished one and the request waiting behind it. Read and written
/// under <see cref="WorkspaceState.Sync"/>.
/// </summary>
internal sealed class RunState
{
    /// <summary>The run in progress, null when idle.</summary>
    public RunSummary? Current { get; set; }

    /// <summary>The tests of the run in progress; Cancel() marks the ones still queued as cancelled at once.</summary>
    public List<(string ProjectId, string Id)>? CurrentRequested { get; set; }

    /// <summary>The last finished run.</summary>
    public RunSummary? Last { get; set; }

    /// <summary>The request waiting for the current run to end; at most one (further requests are merged into it).</summary>
    public RunRequest? Pending { get; set; }

    public bool IsRunning => Current is not null;
}
