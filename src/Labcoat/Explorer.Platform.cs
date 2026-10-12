namespace Labcoat;

// Projects that aren't on Microsoft.Testing.Platform (docs/troubleshooting.md "My project uses VSTest"): Labcoat never
// builds or starts them; the project gets one message (log line, TestProject.diagnostics) with the switch for its framework.
public sealed partial class Explorer
{
    private const string ProjectAssetsFile = "ProjectAssetsFile";

    private sealed partial class ProjectEntry
    {
        /// <summary>The last pre-build check (<see cref="CheckPlatformAsync"/>) and the stamp it was made for.</summary>
        public TestPlatformInfo? Platform;
        public string? PlatformStamp;
        public bool PlatformRestored;
        /// <summary>The verdict keeps the project from being built and started.</summary>
        public bool PlatformBlocks;
        /// <summary>The message last logged for a project that isn't on MTP (logged again only when it changes).</summary>
        public string? PlatformMessage;
    }

    /// <summary>The projects that may be built and started: the others are marked (<see cref="MarkNotMtp"/>) and left out.</summary>
    private async Task<List<ProjectEntry>> ProjectsOnMtpAsync(IReadOnlyList<ProjectEntry> projects, CancellationToken ct) =>
        await ForEachProjectAsync(projects, p => CheckPlatformAsync(p, ct), ct);

    /// <summary>
    /// Before a build: false (and the project marked) when it is known not to be on Microsoft.Testing.Platform. The project
    /// and Directory.* files are read first (no restore needed); a VSTest verdict is confirmed by an MSBuild evaluation,
    /// which also sees conditions and imports. Undecided projects build, and <see cref="AcceptsPlatformAfterBuild"/>
    /// decides on the restored evaluation. Cached by the files' timestamps.
    /// </summary>
    private async Task<bool> CheckPlatformAsync(ProjectEntry p, CancellationToken ct)
    {
        if (p.Plugin is not null) return true; // a runner package's project (Explorer.Runners.cs)
        var stamp = ProjectDiscovery.DetectionStamp(p.Project.Path);
        if (p.PlatformStamp != stamp || p.Platform is null)
        {
            var info = TestPlatform.Classify(TestPlatform.ReadStatic(p.Project.Path));
            var restored = File.Exists(Path.Combine(Path.GetDirectoryName(p.Project.Path)!, "obj", "project.assets.json"));
            if (info.Runner == TestRunner.VsTest && await EvaluatePlatformAsync(p, ct) is { } evaluated)
                (info, restored) = evaluated;
            if (ct.IsCancellationRequested) return false;
            p.Platform = info;
            p.PlatformStamp = stamp;
            p.PlatformRestored = restored;
            p.PlatformBlocks = info.Runner == TestRunner.VsTest;
        }
        if (p.PlatformBlocks)
        {
            MarkNotMtp(p, p.Platform, p.PlatformRestored);
            return false;
        }
        ClearNotMtp(p);
        return true;
    }

    /// <summary>The MSBuild evaluation of the platform settings (no restore needed); null when it fails.</summary>
    private async Task<(TestPlatformInfo Info, bool Restored)?> EvaluatePlatformAsync(ProjectEntry p, CancellationToken ct)
    {
        string[] args = ["msbuild", p.Project.Path, "-nologo", .. TestPlatform.Properties.Append(ProjectAssetsFile).Select(n => "-getProperty:" + n), "-getItem:PackageReference"];
        try
        {
            var (code, output) = await RunDotnetAsync(args, Path.GetDirectoryName(p.Project.Path)!, ct);
            if (code != 0 || ProjectDiscovery.EvaluationFacts(output) is not { } facts) return null;
            return (TestPlatform.Classify(facts), IsRestored(facts.Properties));
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null; // no evaluation: the build and the check after it decide
        }
    }

    private static bool IsRestored(IReadOnlyDictionary<string, string> properties) =>
        properties.GetValueOrDefault(ProjectAssetsFile) is { Length: > 0 } assets && File.Exists(assets);

    /// <summary>
    /// After a build (restored): false (and the project marked) when the evaluated settings say it isn't on MTP: a VSTest
    /// project, or a restored project with no test framework at all (such as a console app passed explicitly).
    /// </summary>
    private bool AcceptsPlatformAfterBuild(ProjectEntry p)
    {
        var restored = IsRestored(p.Properties);
        var info = TestPlatform.Classify(p.Properties, p.PackageReferences);
        if (info.Runner == TestRunner.VsTest || (info.Runner == TestRunner.None && restored))
        {
            p.Platform = info;
            p.PlatformStamp = ProjectDiscovery.DetectionStamp(p.Project.Path);
            p.PlatformRestored = restored;
            p.PlatformBlocks = true;
            MarkNotMtp(p, info, restored);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The project isn't on MTP: its message (logged once per change), diagnostics and TestProject.testPlatform; its tests,
    /// its output path and its warm test host are dropped, so nothing starts it.
    /// </summary>
    private void MarkNotMtp(ProjectEntry p, TestPlatformInfo info, bool restored)
    {
        var message = TestPlatform.Message(p.Project.Name, info, restored);
        WarmSession? warm;
        bool log;
        lock (Sync)
        {
            log = p.PlatformMessage != message;
            p.PlatformMessage = message;
            warm = p.Warm;
            p.TargetPath = null;
            p.DiscoveredDllStamp = default;
            var id = p.Project.Id;
            foreach (var key in _order.Where(k => k.ProjectId == id).ToList()) _tests.Remove(key);
            _order.RemoveAll(k => k.ProjectId == id);
            p.Project.TestPlatform = info.Runner == TestRunner.VsTest ? "vstest" : "none";
            p.Project.Diagnostics = TestPlatform.Diagnostics(p.Project.Name, info, restored);
            p.Project.Status = ProjectStatus.Idle;
            p.Project.LastError = null;
            p.Project.BuildOutput = null;
            p.Project.BuildErrors = null;
            p.Project.BuildErrorCount = null;
            p.Project.BuildWarningCount = null;
            p.Project.ResultsOutdated = null;
            Emit(new ProjectStatusEvent(p.Project.Copy()));
            PublishState();
        }
        if (warm is not null) RetireWarm(p, warm, "the project isn't on Microsoft.Testing.Platform");
        if (log) Log(p.Project.Id, LogLevels.Warning, message);
    }

    /// <summary>The project is on MTP (again): its "not on MTP" marks go.</summary>
    private void ClearNotMtp(ProjectEntry p)
    {
        lock (Sync)
        {
            if (p.Project.TestPlatform is null) return;
            p.Project.TestPlatform = null;
            p.Project.Diagnostics = null;
            p.PlatformMessage = null;
            Emit(new ProjectStatusEvent(p.Project.Copy()));
        }
    }
}
