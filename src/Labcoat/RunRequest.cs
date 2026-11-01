namespace Labcoat;

/// <summary>A validated run request. Selection: projectId -> test ids (null = every test of the project).</summary>
internal sealed class RunRequest
{
    public required string RunId { get; init; }
    public required string RequestedAt { get; init; }
    public required Dictionary<string, HashSet<string>?> Selection { get; set; }
    public bool? Coverage { get; init; }
    public int Iterations { get; init; } = 1;
    /// <summary>"random" (<see cref="RandomOrder.Random"/>) with its seed, or null = the test framework's own order.</summary>
    public string? Order { get; init; }
    public int? Seed { get; init; }
    public string? Trigger { get; set; }
    public AffectedRun? Affected { get; set; }
    public WatchContext? Watch { get; set; }
    public bool Debug { get; init; }
    public int Requests { get; set; } = 1;
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsSubset => Selection.Values.All(ids => ids is not null);

    public PendingRunInfo Info() => new()
    {
        RunId = RunId,
        RequestedAt = RequestedAt,
        Scope = IsSubset ? "subset" : "all",
        ProjectIds = [.. Selection.Keys],
        TestCount = IsSubset ? Selection.Values.Sum(ids => ids!.Count) : null,
        Coverage = Coverage,
        Repeat = Iterations > 1 ? Iterations : null,
        Order = Order,
        Seed = Seed,
        Trigger = Trigger,
        Requests = Requests,
    };
}
