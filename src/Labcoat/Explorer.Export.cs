namespace Labcoat;

// Run export (JUnit XML, HTML report) and the roots of coverage exports: ExportService.cs.
public sealed partial class Explorer
{
    internal ExportService Exports => LazyInitializer.EnsureInitialized(ref _exports, () => new ExportService(_state, _history, QuarantinedKeysLocked, _inputRoots, _workspaceName, SourceRootsLocked));
    private ExportService? _exports;
}
