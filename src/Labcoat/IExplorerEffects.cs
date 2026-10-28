namespace Labcoat;

/// <summary>What the services extracted from <see cref="Explorer"/> may do to the outside: log, and publish changed tests to the clients.</summary>
internal interface IExplorerEffects
{
    void Log(string? projectId, string level, string message);

    /// <summary>Publishes changed tests (testsUpdated); the caller holds <see cref="Explorer.Sync"/>.</summary>
    void PublishTests(IEnumerable<TestNode> nodes);
}
