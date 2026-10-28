namespace Labcoat;

// Verify snapshot mismatches: the work is in SnapshotService; this part connects it to the explorer and keeps the
// Verify-specific test host policy.
public sealed partial class Explorer
{
    /// <summary>GET /api/tests/snapshots, POST /api/snapshots/accept and /reject (SnapshotService.cs).</summary>
    internal SnapshotService Snapshots => _snapshots ??= new SnapshotService(_state, this, RequestRun);
    private SnapshotService? _snapshots;

    void IExplorerEffects.PublishTests(IEnumerable<TestNode> nodes) => PublishTests(nodes);

    /// <summary>Opt back into warm hosts for Verify projects (e.g. when the tests call VerifierSettings.DisableRequireUniquePrefix()).</summary>
    public const string VerifyWarmEnvVar = "LABCOAT_VERIFY_WARM";

    /// <summary>
    /// Verify allows each snapshot file prefix once per process (a static set; "The prefix has already been used"), so a
    /// warm test host could run a Verify test only once. Projects that reference a Verify package therefore start a new
    /// test host for every request, unless LABCOAT_VERIFY_WARM=1.
    /// </summary>
    private bool ColdForVerify(ProjectEntry p)
    {
        if (!UsesVerify(p.PackageReferences) || Environment.GetEnvironmentVariable(VerifyWarmEnvVar) is "1" or "true") return false;
        if (!p.VerifyColdLogged)
        {
            p.VerifyColdLogged = true;
            Log(p.Project.Id, LogLevels.Info, "Verify snapshot tests: every request starts a new test host (Verify allows each snapshot once per " +
                $"process). Set {VerifyWarmEnvVar}=1 to keep it warm if your tests call VerifierSettings.DisableRequireUniquePrefix().");
        }
        return true;
    }

    /// <summary>Verify, Verify.MSTest, Verify.NUnit, Verify.XunitV3, Verify.TUnit, … among the package references.</summary>
    public static bool UsesVerify(IEnumerable<string> packageReferences) =>
        packageReferences.Any(r => r.Equals("Verify", StringComparison.OrdinalIgnoreCase) || r.StartsWith("Verify.", StringComparison.OrdinalIgnoreCase));
}
