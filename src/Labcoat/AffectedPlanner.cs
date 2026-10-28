using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Labcoat;

// Which tests a git change affects (docs/features.md "Run affected"), the branch's changed tests, and the repository facts the UI shows.
internal sealed class AffectedPlanner
{
    private readonly WorkspaceState _state;
    private readonly IReadOnlyList<string> _inputRoots;
    private readonly string? _affectedBase;
    private readonly ImpactMap _impact;
    private readonly CoverageCache _coverageCache;
    private readonly Action<List<(string ProjectId, string Id)>> _queueImpactRefinementLocked;

    /// <param name="affectedBase">Server option <c>affectedBase</c>: the default base ref of "branch vs …" (null = origin/HEAD → main/master).</param>
    /// <param name="queueImpactRefinementLocked">Called with the tests a plan selected through class-level impact data (caller holds the workspace lock).</param>
    public AffectedPlanner(WorkspaceState state, IReadOnlyList<string> inputRoots, string? affectedBase, ImpactMap impact, CoverageCache coverageCache,
        Action<List<(string ProjectId, string Id)>> queueImpactRefinementLocked)
    {
        (_state, _inputRoots, _affectedBase, _impact, _coverageCache, _queueImpactRefinementLocked) =
            (state, inputRoots, affectedBase, impact, coverageCache, queueImpactRefinementLocked);
    }

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".rst", ".adoc" };
    private static readonly HashSet<string> ProjectFileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".fsproj", ".vbproj" };
    private static readonly HashSet<string> ProductCodeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb", ".razor", ".cshtml", ".xaml" };

    private sealed record GitInfoCacheEntry(DateTime At, GitInfo Info);
    private volatile GitInfoCacheEntry? _gitInfoCache;
    private readonly SemaphoreSlim _gitInfoLock = new(1, 1);

    /// <summary>The directory whose repository is the workspace's: the common root of the test projects.</summary>
    private string? WorkspaceDir()
    {
        lock (_state.Sync)
        {
            var paths = _state.Projects.Select(p => p.Project.Path).ToList();
            if (paths.Count == 0) return _inputRoots.FirstOrDefault();
            return PathUtil.CommonRoot(paths);
        }
    }

    private async Task<GitRepo> RepoAsync(CancellationToken ct)
    {
        var dir = WorkspaceDir();
        GitRepo? repo = dir is null ? null : await GitRepo.FindAsync(dir, ct);
        if (repo is null)
        {
            // Projects of several repositories: the first one that is in a repository.
            List<string> projectDirs;
            lock (_state.Sync) projectDirs = _state.Projects.Select(p => Path.GetDirectoryName(p.Project.Path)!).ToList();
            foreach (var d in projectDirs)
            {
                repo = await GitRepo.FindAsync(d, ct);
                if (repo is not null) break;
            }
        }
        return repo ?? throw new AffectedException(409, "notGitRepo", "The workspace is not in a git repository.");
    }

    /// <summary>GET /api/git: branch, HEAD, dirty count, default base (cached ~2 s).</summary>
    public async Task<GitInfo> GitInfoAsync(CancellationToken ct = default)
    {
        await _gitInfoLock.WaitAsync(ct);
        try
        {
            if (_gitInfoCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(2)) return c.Info;
            GitInfo info;
            try { info = await (await RepoAsync(ct)).InfoAsync(_affectedBase, ct); }
            catch (AffectedException ex) { info = new GitInfo { Available = false, Error = ex.Message, AffectedBase = _affectedBase }; }
            _gitInfoCache = new GitInfoCacheEntry(DateTime.UtcNow, info);
            return info;
        }
        finally { _gitInfoLock.Release(); }
    }

    private sealed record ChangedCacheEntry(DateTime At, int Tests, ChangedTests Result);
    private volatile ChangedCacheEntry? _changedCache;
    private readonly SemaphoreSlim _changedLock = new(1, 1);

    /// <summary>
    /// GET /api/changed-tests: the tests added or modified on the current branch, i.e. whose own lines (the method's extent, see IsChanged)
    /// overlap a changed range of their source file, or that sit in a newly added file. Same changes as Run affected's
    /// "branch" base (merge base with the base branch → working tree, untracked files included), else the uncommitted
    /// changes when there is no base branch. Cached ~2 s (and per discovered test count); outside a repository: Available false.
    /// </summary>
    public async Task<ChangedTests> ChangedTestsAsync(CancellationToken ct = default)
    {
        await _changedLock.WaitAsync(ct);
        try
        {
            int count;
            lock (_state.Sync) count = _state.Order.Count;
            if (_changedCache is { } c && c.Tests == count && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(2)) return c.Result;
            var result = await ComputeChangedTestsAsync(ct);
            _changedCache = new ChangedCacheEntry(DateTime.UtcNow, count, result);
            return result;
        }
        finally { _changedLock.Release(); }
    }

    /// <summary>Drops the cached answer of <see cref="ChangedTestsAsync"/> (the next call asks git again).</summary>
    public void InvalidateChangedTests() => _changedCache = null;

    private async Task<ChangedTests> ComputeChangedTestsAsync(CancellationToken ct)
    {
        GitRepo repo;
        var kind = AffectedBases.Branch;
        GitChanges changes;
        try
        {
            repo = await RepoAsync(ct);
            try { changes = await repo.ChangesAsync(kind, null, _affectedBase, ct); }
            catch (AffectedException ex) when (ex.Code is "noBase" or "noCommits" or "unknownRef")
            {
                kind = AffectedBases.Working;
                changes = await repo.ChangesAsync(kind, null, _affectedBase, ct);
            }
        }
        catch (AffectedException ex) { return new ChangedTests { Available = false, Error = ex.Message, Tests = [] }; }

        var tests = new List<ChangedTest>();
        var candidates = new List<(TestNode Test, GitChange Change)>();
        lock (_state.Sync)
        {
            var byFile = new Dictionary<string, List<TestNode>>(PathComparer);
            foreach (var k in _state.Order)
            {
                if (_state.Tests[k].FilePath is not { } fp) continue;
                var f = PathUtil.CanonicalizeOrSelf(fp);
                if (!byFile.TryGetValue(f, out var list)) byFile[f] = list = [];
                list.Add(_state.Tests[k]);
            }
            foreach (var change in changes.Files.Where(c => c.Status != "deleted"))
            {
                var abs = PathUtil.CanonicalizeOrSelf(Path.Combine(repo.Root, change.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (byFile.TryGetValue(abs, out var inFile)) candidates.AddRange(inFile.Select(t => (t, change)));
            }
        }
        // outside the lock: reading the sources of tests without a usable line range (TestSourceRange.cs)
        var seen = new HashSet<(string, string)>();
        foreach (var (t, change) in candidates)
            if (IsChanged(t, change) && seen.Add((t.ProjectId, t.Id))) tests.Add(new ChangedTest(t.ProjectId, t.Id));
        return new ChangedTests { Available = true, Base = kind, Ref = changes.Ref, Tests = tests };
    }

    /// <summary>
    /// A test of a changed file is changed when the file is new or its line range overlaps a changed range. The range is
    /// the reported Line..LineEnd (TUnit); frameworks that report one line (MSTest, xUnit) or none (NUnit) get the method's
    /// extent from the source file (<see cref="TestSourceRange"/>), else the reported line, else the whole file counts.
    /// </summary>
    internal static bool IsChanged(TestNode test, GitChange change)
    {
        if (change.Status is "added" or "untracked") return true;
        if (change.Lines is not { } spans) return change.Status is not ("renamed" or "copied"); // a pure rename changed no line
        (int Start, int End)? range = test.Line is { } l && test.LineEnd is { } e && e > l ? (l, e) : TestSourceRange.Find(test.FilePath, test.Line, test.MethodName);
        if (range is not { } r)
        {
            if (test.Line is not { } only) return true;
            r = (only, only);
        }
        return spans.Any(s => s.Start <= r.End && r.Start <= s.End);
    }

    /// <summary>Normalizes a base name; throws 400 badBase / badRef.</summary>
    private static string NormalizeBase(string? baseKind, string? reference)
    {
        var kind = string.IsNullOrWhiteSpace(baseKind) ? AffectedBases.Working : baseKind.Trim().ToLowerInvariant();
        if (kind is not (AffectedBases.Working or AffectedBases.Branch or AffectedBases.Range)) throw new AffectedException(400, "badBase", $"Unknown base '{baseKind}' (working, branch or range).");
        if (kind != AffectedBases.Working && !string.IsNullOrEmpty(reference) && !GitRepo.IsValidRef(reference))
            throw new AffectedException(400, "badRef", $"'{reference}' is not a valid git ref.");
        return kind;
    }

    /// <summary>AffectedFile kind / mapping "project": the file selects every test of its projects.</summary>
    private const string WholeProject = "project";

    private sealed record ProjectScope(string Id, string Name, string Path, string Dir, List<string> Dirs);

    /// <summary>
    /// GET /api/affected: which tests the changes of <paramref name="baseKind"/> affect. Never runs anything.
    /// Throws <see cref="AffectedException"/> (400 / 404 / 409) when git can't answer.
    /// </summary>
    public async Task<AffectedPlan> PlanAsync(string? baseKind, string? reference, CancellationToken ct = default)
    {
        var kind = NormalizeBase(baseKind, reference);
        if (kind == AffectedBases.Working) reference = null;
        var repo = await RepoAsync(ct);
        var changes = await repo.ChangesAsync(kind, string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), _affectedBase, ct);
        var info = await repo.InfoAsync(_affectedBase, ct);

        // Project scopes: the project directory plus its ProjectReference closure (evaluated, else read from the files).
        List<(string Id, string Name, string Path, List<string> WatchDirs)> raw;
        lock (_state.Sync) raw = _state.Projects.Select(p => (p.Project.Id, p.Project.Name, p.Project.Path, p.WatchDirs.ToList())).ToList();
        var scopes = raw.Select(r =>
        {
            var dir = PathUtil.CanonicalizeOrSelf(Path.GetDirectoryName(r.Path)!);
            var dirs = (r.WatchDirs.Count > 0 ? r.WatchDirs : [Path.GetDirectoryName(r.Path)!, .. ClosureDirsFromProjectFile(r.Path)])
                .Select(PathUtil.CanonicalizeOrSelf).Distinct(PathComparer).ToList();
            if (!dirs.Contains(dir, PathComparer)) dirs.Insert(0, dir);
            return new ProjectScope(r.Id, r.Name, PathUtil.CanonicalizeOrSelf(r.Path), dir, dirs);
        }).ToList();

        lock (_state.Sync)
        {
            var plan = BuildPlanLocked(kind, changes, info, repo.Root, scopes);
            _queueImpactRefinementLocked(plan.ClassLevel); // impactMap "auto" (Explorer.ImpactAndWatch.cs)
            return plan;
        }
    }

    private static StringComparer PathComparer => PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static bool InOrUnder(string path, string dir) => string.Equals(path, dir, PathUtil.Comparison) || PathUtil.IsUnder(path, dir);

    private AffectedPlan BuildPlanLocked(string kind, GitChanges changes, GitInfo info, string root, List<ProjectScope> scopes)
    {
        var warnings = new List<string>(changes.Warnings);
        var builder = new AffectedPlanBuilder(this, scopes);
        foreach (var change in changes.Files) builder.AddChange(root, change);

        // Selection + tests in tree order.
        var selection = builder.Selection();
        var tests = builder.TestsInTreeOrder(selection);
        var projects = new List<AffectedProject>();
        foreach (var s in scopes)
        {
            if (!selection.TryGetValue(s.Id, out var ids)) continue;
            var count = tests.Count(t => t.ProjectId == s.Id);
            projects.Add(new AffectedProject { ProjectId = s.Id, Name = s.Name, Tests = count, All = ids is null ? true : null });
            if (ids is null && count == 0) warnings.Add($"{s.Name} has no discovered tests yet; they are counted after its build.");
        }
        var strategy = builder.Mappings.Count switch { 0 => "none", 1 => builder.Mappings.First(), _ => "mixed" };
        return new AffectedPlan
        {
            Base = kind,
            Ref = changes.Ref,
            MergeBase = changes.MergeBase,
            Git = info,
            Files = builder.Files,
            Tests = tests,
            Projects = projects,
            Counts = new AffectedCounts(builder.Files.Count, tests.Count, projects.Count),
            Strategy = strategy,
            NeedsImpactMap = builder.NeedsImpact.Count > 0 ? true : null,
            ImpactMap = _impact.Info(),
            Warnings = warnings.Count > 0 ? warnings : null,
            ProjectsNeedingImpactMap = [.. builder.NeedsImpact],
            Selection = selection,
            ClassLevel = [.. builder.Granularity.Where(kv => kv.Value == "class").Select(kv => kv.Key)],
        };
    }

    /// <summary>What one changed file maps to while the plan is built (<see cref="AffectedPlanBuilder"/>).</summary>
    private sealed class FileMatch(string abs, string name, string verb, List<ProjectScope> containing)
    {
        public string Abs { get; } = abs;
        public string Name { get; } = name;
        public string Verb { get; } = verb;
        /// <summary>The project scopes whose directories contain the file.</summary>
        public List<ProjectScope> Containing { get; } = containing;
        public string Kind { get; set; } = "other";
        public string Mapping { get; set; } = "none";
        public HashSet<(string, string)> Selected { get; } = [];
        public List<string> ProjectIds { get; } = [];
        public List<string>? Methods { get; set; }
    }

    /// <summary>
    /// Maps the changed files of a plan to tests (test file → its tests, build file → whole projects, code → impact map,
    /// else cached per-test coverage, else the containing projects). Runs under the workspace lock.
    /// </summary>
    private sealed class AffectedPlanBuilder
    {
        private readonly AffectedPlanner _planner;
        private readonly List<ProjectScope> _scopes;
        /// <summary>Test files → tests (tree order).</summary>
        private readonly Dictionary<string, List<(string ProjectId, string Id)>> _byFile = new(PathComparer);
        private readonly Dictionary<(string ProjectId, string Id), List<string>> _reasons = [];
        private readonly Dictionary<string, List<string>> _wholeProjects = []; // projectId → reasons for "all tests"

        public HashSet<string> NeedsImpact { get; } = [];
        /// <summary>Tests selected by the impact map: "test" | "class".</summary>
        public Dictionary<(string ProjectId, string Id), string> Granularity { get; } = [];
        public HashSet<string> Mappings { get; } = [];
        public List<AffectedFile> Files { get; } = [];

        public AffectedPlanBuilder(AffectedPlanner planner, List<ProjectScope> scopes)
        {
            _planner = planner;
            _scopes = scopes;
            foreach (var k in planner._state.Order)
            {
                if (planner._state.Tests[k].FilePath is not { } fp) continue;
                var f = PathUtil.CanonicalizeOrSelf(fp);
                if (!_byFile.TryGetValue(f, out var list)) _byFile[f] = list = [];
                list.Add(k);
            }
        }

        private static void AddDistinct<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            if (!list.Contains(value)) list.Add(value);
        }

        private void AddReason((string, string) key, string reason) => AddDistinct(_reasons, key, reason);

        private void AddWhole(string projectId, string reason) => AddDistinct(_wholeProjects, projectId, reason);

        private int CountTests(string projectId) => _planner._state.Order.Count(k => k.ProjectId == projectId);

        public void AddChange(string root, GitChange change)
        {
            var abs = PathUtil.CanonicalizeOrSelf(Path.Combine(root, change.Path.Replace('/', Path.DirectorySeparatorChar)));
            var name = Path.GetFileName(abs);
            var ext = Path.GetExtension(abs);
            var verb = change.Status switch
            {
                "added" or "untracked" => "added", "deleted" => "deleted", "renamed" => "renamed", _ => "changed",
            };
            var m = new FileMatch(abs, name, verb, _scopes.Where(s => s.Dirs.Any(d => PathUtil.IsUnder(abs, d))).ToList());

            if (_byFile.TryGetValue(abs, out var inFile)) MatchTestFile(m, inFile);
            else if (IsBuildFile(name, ext)) MatchBuildFile(m, ext);
            else if (DocExtensions.Contains(ext) || IsTestArtifact(change.Path))
                m.Kind = "other"; // documentation, or output of a test run (Verify *.received.*, TestResults/)
            else MatchSourceFile(m, change, ext);

            foreach (var (pid, _) in m.Selected) if (!m.ProjectIds.Contains(pid)) m.ProjectIds.Add(pid);
            if (m.Mapping != "none") Mappings.Add(m.Mapping);
            var fileTests = m.Mapping == WholeProject ? m.ProjectIds.Sum(CountTests) : m.Selected.Count;
            Files.Add(new AffectedFile
            {
                Path = abs,
                RelativePath = change.Path,
                Status = change.Status,
                OldPath = change.OldPath,
                Kind = m.Kind,
                Mapping = m.Mapping,
                Tests = fileTests,
                ProjectIds = m.ProjectIds.Count > 0 ? m.ProjectIds : null,
                Lines = change.Lines?.Select(l => new[] { l.Start, l.End }).ToList(),
                Methods = m.Methods,
            });
        }

        private void MatchTestFile(FileMatch m, List<(string ProjectId, string Id)> inFile)
        {
            m.Kind = "test";
            m.Mapping = "testFile";
            foreach (var k in inFile)
            {
                m.Selected.Add(k);
                AddReason(k, $"{m.Name} {m.Verb} (test file)");
            }
        }

        private void MatchBuildFile(FileMatch m, string ext)
        {
            m.Kind = WholeProject;
            var hit = ProjectsOfBuildFile(m, ext);
            if (hit.Count > 0) m.Mapping = WholeProject;
            foreach (var s in hit)
            {
                AddWhole(s.Id, $"{m.Name} {m.Verb}: all tests of {s.Name}");
                m.ProjectIds.Add(s.Id);
            }
        }

        private List<ProjectScope> ProjectsOfBuildFile(FileMatch m, string ext)
        {
            var dir = Path.GetDirectoryName(m.Abs)!;
            if (ProjectFileExtensions.Contains(ext))
                return _scopes.Where(s => string.Equals(s.Path, m.Abs, PathUtil.Comparison) || s.Dirs.Any(d => string.Equals(d, dir, PathUtil.Comparison))).ToList();
            if (m.Name.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase) || m.Name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
                || m.Name.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase))
                return _scopes.Where(s => s.Dirs.Any(d => InOrUnder(d, dir))).ToList();
            // Another .props / .targets: imported explicitly by whatever needs it → the projects around it, else all.
            return m.Containing.Count > 0 ? m.Containing : _scopes;
        }

        private void MatchSourceFile(FileMatch m, GitChange change, string ext)
        {
            m.Kind = m.Containing.Count > 0 || ProductCodeExtensions.Contains(ext) ? "product" : "other";
            var isCode = SourceExtensions.Code.Contains(ext) || ProductCodeExtensions.Contains(ext);
            if (isCode)
            {
                MatchImpactMap(m, change.Status is "deleted" ? null : change.Lines);
                if (m.Mapping == "none") MatchCachedCoverage(m);
            }
            if (m.Mapping == "none" && m.Containing.Count > 0) MatchContainingProjects(m, isCode);
        }

        /// <summary>Impact map: tests whose executed methods overlap the changed lines.</summary>
        private void MatchImpactMap(FileMatch m, List<LineSpan>? lines)
        {
            var impact = _planner._impact;
            foreach (var projectId in _scopes.Select(s => s.Id))
            {
                if (impact.AffectedByLines(projectId, m.Abs, lines, out var outside) is not { } hits) continue;
                m.Mapping = "impactMap";
                foreach (var (tid, methods) in hits.Where(h => _planner._state.Tests.ContainsKey((projectId, h.Key))))
                    SelectByImpact(m, (projectId, tid), methods, outside);
            }
        }

        private void SelectByImpact(FileMatch m, (string ProjectId, string Id) key, List<string> methods, bool outside)
        {
            var impact = _planner._impact;
            m.Selected.Add(key);
            if (methods.Count > 0)
            {
                m.Methods ??= [];
                var known = m.Methods;
                foreach (var x in methods.Where(x => !known.Contains(x))) known.Add(x);
            }
            var perTest = impact.IsPerTest(key.ProjectId, key.Id);
            // A test is "class" when any file selected it through shared data.
            if (!perTest || !Granularity.ContainsKey(key)) Granularity[key] = perTest ? "test" : "class";
            var note = impact.DataNote(key.ProjectId, key.Id);
            AddReason(key, ImpactReason(m, methods, outside, note));
        }

        private static string ImpactReason(FileMatch m, List<string> methods, bool outside, string note)
        {
            if (methods.Count > 0) return $"{m.Name} {m.Verb} ({(methods.Count == 1 ? "method" : "methods")} {string.Join(", ", methods)}, {note})";
            return outside ? $"{m.Name} {m.Verb} (outside executed methods, {note})" : $"{m.Name} {m.Verb} ({note})";
        }

        /// <summary>Cached per-test coverage: tests whose own coverage includes the file.</summary>
        private void MatchCachedCoverage(FileMatch m)
        {
            var covering = _planner._coverageCache.SingleTestReports()
                .Where(c => _planner._state.Tests.ContainsKey((c.ProjectId, c.TestId)))
                .Where(c => c.Report.Files.Any(f => f.LinesCovered > 0 && string.Equals(PathUtil.CanonicalizeOrSelf(f.Path), m.Abs, PathUtil.Comparison)));
            foreach (var (pid, tid, _, _) in covering)
            {
                m.Mapping = "coverage";
                m.Selected.Add((pid, tid));
                AddReason((pid, tid), $"{m.Name} {m.Verb} (covered by cached coverage)");
            }
        }

        private void MatchContainingProjects(FileMatch m, bool isCode)
        {
            m.Mapping = WholeProject;
            foreach (var s in m.Containing)
            {
                AddWhole(s.Id, isCode ? $"no impact data for {m.Name}: all tests of {s.Name}" : $"{m.Name} {m.Verb}: all tests of {s.Name}");
                m.ProjectIds.Add(s.Id);
                // Product code outside the test project itself: the impact map would narrow this down.
                if (isCode && !PathUtil.IsUnder(m.Abs, s.Dir)) NeedsImpact.Add(s.Id);
            }
        }

        /// <summary>Run selection: projectId → test ids (null = every test of the project).</summary>
        public Dictionary<string, HashSet<string>?> Selection()
        {
            var selection = new Dictionary<string, HashSet<string>?>();
            foreach (var pid in _wholeProjects.Keys) selection[pid] = null;
            foreach (var (pid, id) in _reasons.Keys.Where(k => !_wholeProjects.ContainsKey(k.ProjectId)))
            {
                if (!selection.TryGetValue(pid, out var set) || set is null)
                {
                    set = [];
                    selection[pid] = set;
                }
                set.Add(id);
            }
            return selection;
        }

        public List<AffectedTest> TestsInTreeOrder(Dictionary<string, HashSet<string>?> selection)
        {
            var tests = new List<AffectedTest>();
            foreach (var k in _planner._state.Order)
            {
                if (!selection.TryGetValue(k.ProjectId, out var ids)) continue;
                if (ids is not null && !ids.Contains(k.Id)) continue;
                tests.Add(new AffectedTest(k.ProjectId, k.Id, ReasonsFor(k, wholeProject: ids is null)) { Granularity = Granularity.GetValueOrDefault(k) });
            }
            return tests;
        }

        private List<string> ReasonsFor((string ProjectId, string Id) key, bool wholeProject)
        {
            var why = _reasons.TryGetValue(key, out var r) ? r.ToList() : [];
            if (!wholeProject) return why;
            foreach (var w in _wholeProjects[key.ProjectId].Where(w => !why.Contains(w))) why.Add(w);
            return why;
        }
    }

    /// <summary>Files test runs leave behind: Verify's <c>*.received.*</c> and anything under a <c>TestResults</c> folder.</summary>
    private static bool IsTestArtifact(string relativePath) =>
        relativePath.Split('/') is var parts
        && (parts[^1].Contains(".received.", StringComparison.OrdinalIgnoreCase)
            || parts[..^1].Any(p => p.Equals("TestResults", StringComparison.OrdinalIgnoreCase)));

    private static bool IsBuildFile(string name, string ext) =>
        ProjectFileExtensions.Contains(ext) || ext.Equals(".props", StringComparison.OrdinalIgnoreCase) || ext.Equals(".targets", StringComparison.OrdinalIgnoreCase)
        || name.Equals("global.json", StringComparison.OrdinalIgnoreCase) || name.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase);

    /// <summary>ProjectReference closure directories read from the project files (projects not evaluated yet).</summary>
    private static List<string> ClosureDirsFromProjectFile(string projectPath)
    {
        var refs = new System.Text.Json.Nodes.JsonArray();
        try
        {
            var dir = Path.GetDirectoryName(projectPath)!;
            foreach (var el in XDocument.Load(projectPath).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = (string?)el.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.Contains("$(")) continue;
                foreach (var part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    refs.Add(new System.Text.Json.Nodes.JsonObject { ["FullPath"] = Path.GetFullPath(Path.Combine(dir, part.Replace('\\', Path.DirectorySeparatorChar))) });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { /* unreadable project file: no references from it */ }
        return ProjectReferences.ClosureDirs(refs);
    }
}
