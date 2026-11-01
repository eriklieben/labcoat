namespace Labcoat;

// The slowest tests with their duration trend (GET /api/tests/slowest): SlowestTests.cs.
public sealed partial class Explorer
{
    internal SlowestTestsService Slowest => LazyInitializer.EnsureInitialized(ref _slowest, () => new SlowestTestsService(_state, QuarantinedKeysLocked));
    private SlowestTestsService? _slowest;
}
