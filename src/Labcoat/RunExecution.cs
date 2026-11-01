using System.Diagnostics;

namespace Labcoat;

/// <summary>The state of one RunExecutor.ExecuteAsync: what was requested, the results so far and the coverage collected.</summary>
internal sealed class RunExecution(RunRequest request, bool collectCoverage, HashSet<string> quarantined)
{
    public RunRequest Request { get; } = request;
    public int Iterations => Request.Iterations;
    public bool CollectCoverage { get; } = collectCoverage;
    public HashSet<string> Quarantined { get; } = quarantined;
    public RunSummary? Run { get; set; }
    public Stopwatch? Stopwatch { get; set; }
    public Dictionary<(string, string), Outcome> Results { get; } = [];
    /// <summary>Outcome before the run, restored when a test never ran.</summary>
    public Dictionary<(string, string), Outcome> Previous { get; } = [];
    public List<(string ProjectId, string Id)> Requested { get; } = [];
    public CoverageAccumulator Accumulator { get; } = new();
    public List<string> CoveredProjects { get; } = [];
    public List<CoverageSettingsInfo> CoverageSettings { get; } = [];
    /// <summary>Per project: its coverage of this run, and whether it ran every test of the project (FullCoverage.cs).</summary>
    public List<(string ProjectId, HashSet<string>? Ids, CoverageAccumulator Data, CoverageSettingsInfo Settings)> ProjectCoverage { get; } = [];
    public Dictionary<(string ProjectId, string Id), RepeatTestResult> RepeatStats { get; } = [];
    /// <summary>Repeat: the most recent failing repetition per test.</summary>
    public Dictionary<(string, string), TestNode> LastFailure { get; } = [];
    public List<string> Aborts { get; } = [];
}

/// <summary>One project's part of a run (RunExecutor.RunProjectAsync).</summary>
/// <param name="Quarantined">"projectId|id" keys whose results are counted separately (RunSummary.quarantined).</param>
/// <param name="RequestedKeys">The tests of this project the run asked for (to settle them when the host crashes).</param>
/// <param name="Debug">Debug run: a one-off host that waits for a debugger (TESTINGPLATFORM_WAIT_ATTACH_DEBUGGER).</param>
internal sealed record ProjectRun(ProjectEntry Project, HashSet<string>? Ids, RunSummary Run, Dictionary<(string, string), Outcome> Results,
    IReadOnlySet<string> Quarantined, List<(string ProjectId, string Id)> RequestedKeys, bool Debug);
