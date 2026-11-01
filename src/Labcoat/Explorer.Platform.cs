namespace Labcoat;

// Projects that aren't on Microsoft.Testing.Platform: PlatformCheck.cs.
public sealed partial class Explorer
{
    private PlatformCheck? _platformCheck;

    private PlatformCheck PlatformCheck => LazyInitializer.EnsureInitialized(ref _platformCheck, () => new PlatformCheck(_state, this, Hosts,
        PublishState, RunDotnetAsync));

    /// <summary>The projects that may be built and started: the others are marked (PlatformCheck) and left out.</summary>
    private async Task<List<ProjectEntry>> ProjectsOnMtpAsync(IReadOnlyList<ProjectEntry> projects, CancellationToken ct) =>
        await ForEachProjectAsync(projects, p => PlatformCheck.CheckAsync(p, ct), ct);
}
