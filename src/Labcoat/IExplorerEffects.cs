namespace Labcoat;

/// <summary>What the services extracted from <see cref="Explorer"/> may do to the outside: log, publish changed tests and send events to the clients.</summary>
internal interface IExplorerEffects
{
    void Log(string? projectId, string level, string message);

    /// <summary>Publishes changed tests (testsUpdated); the caller holds <see cref="Explorer.Sync"/>.</summary>
    void PublishTests(IEnumerable<TestNode> nodes);

    /// <summary>Sends an event to all clients (ordered after pending test updates); the caller holds <see cref="Explorer.Sync"/>.</summary>
    void Emit(ServerEvent evt);

    /// <summary>Sets a project's status (and its error, build output and build errors unless <paramref name="keepError"/>) and sends it.</summary>
    void SetStatus(ProjectEntry p, ProjectStatus status, string? error = null, string? buildOutput = null, bool keepError = false,
        BuildFailure? build = null);
}
