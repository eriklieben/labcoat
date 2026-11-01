namespace Labcoat;

/// <summary>
/// The workspace the explorer shows: its test projects and their tests. Shared by the explorer and the services
/// extracted from it; every read and write happens under <see cref="Sync"/> (members ending in Locked of those services
/// say so), and events are published under it too (see <see cref="Explorer"/>).
/// </summary>
internal sealed class WorkspaceState
{
    public object Sync { get; } = new();

    public RunState Run { get; } = new();

    public List<ProjectEntry> Projects { get; } = [];

    public Dictionary<(string ProjectId, string Id), TestNode> Tests { get; } = [];

    /// <summary>The tests' keys in tree order.</summary>
    public List<(string ProjectId, string Id)> Order { get; } = [];

    public ProjectEntry? FindProject(string projectId) => Projects.FirstOrDefault(p => p.Project.Id == projectId);

    /// <summary>The key of the first test with this id (in <paramref name="projectId"/> when given); its Id is null when there is none.</summary>
    public (string ProjectId, string Id) FindKey(string? projectId, string? id) =>
        Order.FirstOrDefault(k => k.Id == id && (projectId is null || k.ProjectId == projectId));
}
