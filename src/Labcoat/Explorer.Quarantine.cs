namespace Labcoat;

// The shared quarantine file next to the UI's quarantine: QuarantineFile.cs.
public sealed partial class Explorer
{
    /// <summary>The shared quarantine list (<c>--quarantine-file</c>, <c>quarantineFile</c>): additive to the tests quarantined in the UI.</summary>
    public QuarantineFile? QuarantineFile { get; init; }

    private QuarantineService Quarantine => LazyInitializer.EnsureInitialized(ref _quarantine, () => new QuarantineService(_state, _settings, QuarantineFile));
    private QuarantineService? _quarantine;
}
