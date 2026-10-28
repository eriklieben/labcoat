using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>One test project of the workspace: its identity and what the explorer knows about its build, test host and watchers.</summary>
internal sealed class ProjectEntry(TestProject project)
{
    public TestProject Project { get; } = project;
    public string? TargetPath;
    public DateTime TargetPathStamp;           // csproj write time when TargetPath was computed
    public List<string> WatchDirs = [];
    public DateTime DiscoveredDllStamp;        // dll write time at last successful discovery
    public DateTime? LastBuildStartUtc;        // start time of the last successful build
    public List<FileSystemWatcher> Watchers = [];
    public Timer? Debounce;
    public MtpHost? ActiveSession;
    public bool? SupportsCoverage;             // cached result of probing `--help` for --coverage
    public DateTime SupportsCoverageStamp;
    public Dictionary<string, string> Properties = new(StringComparer.OrdinalIgnoreCase); // evaluated MSBuild properties (diagnostics)
    public List<string> PackageReferences = [];

    /// <summary>The MSBuild property TestingPlatformCommandLineArguments (may name --coverage-settings).</summary>
    public string? TestingPlatformArguments;
    /// <summary>Assembly names of the projects the test project references, transitively.</summary>
    public List<string> ReferencedAssemblies = [];

    /// <summary>TestingPlatformArguments sorted for server mode (cached per value).</summary>
    public TestingPlatformArguments? HostArguments;
    public string? HostArgumentsFrom;

    /// <summary>The last pre-build check (Explorer.CheckPlatformAsync) and the stamp it was made for.</summary>
    public TestPlatformInfo? Platform;
    public string? PlatformStamp;
    public bool PlatformRestored;
    /// <summary>The verdict keeps the project from being built and started.</summary>
    public bool PlatformBlocks;
    /// <summary>The message last logged for a project that isn't on MTP (logged again only when it changes).</summary>
    public string? PlatformMessage;

    /// <summary>The "Verify: every request starts a new test host" line was logged.</summary>
    public bool VerifyColdLogged;

    public WarmSession? Warm;
    /// <summary>The test app runs behind a test host controller (always-on out-of-process extension): no warm host.</summary>
    public bool WarmUnsupported;
    /// <summary>Changed files collected by the watcher during the debounce window.</summary>
    public readonly HashSet<string> PendingChanges = [];
}
