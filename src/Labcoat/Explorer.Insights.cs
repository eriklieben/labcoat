namespace Labcoat;

// Queries answered from the explorer state: "which tests cover this line", a test's trace id, open in editor.
public sealed partial class Explorer
{
    // ------------------------------------------------------------------ which tests cover a line (GET /api/coverage/line)

    /// <summary>
    /// Tests covering <paramref name="line"/> of <paramref name="path"/>: exact (a cached single-test coverage report
    /// has hits on the line, source "perTest") or approximate (the impact map says the test executed a method whose
    /// span contains the line, source "impactMap"; class-granular for the impact-map pass). <c>Partial</c> = not every
    /// test has single-test coverage cached, so the list may be incomplete.
    /// </summary>
    public CoveringTestsResult CoveringTests(string path, int line)
    {
        var file = PathUtil.CanonicalizeOrSelf(path);
        var cmp = PathUtil.Comparison;
        var canonical = new Dictionary<string, string>();
        string Canon(string s)
        {
            if (canonical.TryGetValue(s, out var c)) return c;
            c = PathUtil.CanonicalizeOrSelf(s);
            canonical[s] = c;
            return c;
        }

        lock (Sync)
        {
            var fingerprints = new Dictionary<string, string?>();
            string? Fingerprint(string projectId)
            {
                if (fingerprints.TryGetValue(projectId, out var f)) return f;
                var project = _state.Projects.FirstOrDefault(p => p.Project.Id == projectId);
                f = project?.TargetPath is { } target && File.Exists(target) ? RunnerProjects.CodeStampOf(project) : null;
                fingerprints[projectId] = f;
                return f;
            }

            var order = new Dictionary<(string, string), int>();
            for (var i = 0; i < _state.Order.Count; i++) order[_state.Order[i]] = i;

            var exact = new List<CoveringTest>();
            var seen = new HashSet<(string, string)>();
            var withPerTest = 0;
            foreach (var (projectId, testId, report, stamp) in _coverageCache.SingleTestReports())
            {
                if (!_state.Tests.ContainsKey((projectId, testId))) continue;
                withPerTest++;
                var fc = report.Files.FirstOrDefault(f => string.Equals(Canon(f.Path), file, cmp));
                var hit = fc?.Lines.FirstOrDefault(l => l.Line == line);
                if (hit is not { Hits: > 0 }) continue;
                seen.Add((projectId, testId));
                exact.Add(new CoveringTest(projectId, testId, "perTest", hit.Hits, Fingerprint(projectId) != stamp ? true : null));
            }
            var approx = _impact.TestsCoveringLine(file, line)
                .Where(k => _state.Tests.ContainsKey(k) && !seen.Contains(k))
                .Select(k => new CoveringTest(k.ProjectId, k.TestId, "impactMap"))
                .ToList();
            var all = exact.Concat(approx)
                .OrderBy(t => t.Source == "perTest" ? 0 : 1)
                .ThenBy(t => order.GetValueOrDefault((t.ProjectId, t.Id), int.MaxValue))
                .ToList();
            return new CoveringTestsResult(all, withPerTest < _state.Tests.Count);
        }
    }

    // ------------------------------------------------------------------ Aspire failure context (GET /api/tests/telemetry)

    /// <summary>Trace id of a test (null when the test is unknown or has none).</summary>
    public (bool Found, string? TraceId) TestTraceId(string? projectId, string id)
    {
        lock (Sync)
        {
            var t = projectId is not null
                ? _state.Tests.GetValueOrDefault((projectId, id))
                : _state.Tests.Values.FirstOrDefault(x => x.Id == id);
            return (t is not null, t?.TraceId);
        }
    }

    // ------------------------------------------------------------------ open in editor (POST /api/open-in-editor)

    public (int Status, bool Ok, string? Message) OpenInEditor(string? editor, string? path, int? line, int? column)
    {
        if (string.IsNullOrEmpty(editor) || !EditorLauncher.Ids.Contains(editor))
            return (400, false, $"editor must be one of: {string.Join(", ", EditorLauncher.Ids)}");
        var access = CheckSourcePath(path);
        if (access.Result == SourceAccessResult.Forbidden) return (403, false, "The file is outside the served source roots.");
        if (access.Result == SourceAccessResult.NotFound) return (404, false, "File not found.");
        var (ok, message) = EditorLauncher.Open(editor, access.FullPath!, line is > 0 ? line : null, column is > 0 ? column : null);
        if (!ok) Log(null, LogLevels.Warning, $"Open in editor ({editor}): {message}");
        return (200, ok, message);
    }
}

/// <summary>One entry of GET /api/coverage/line.</summary>
/// <param name="Source">"perTest" (exact line hits from a cached single-test coverage run) | "impactMap" (method/class granular).</param>
/// <param name="Stale">perTest only: the test app was rebuilt since the coverage was collected.</param>
public sealed record CoveringTest(string ProjectId, string Id, string Source, int? Hits = null, bool? Stale = null);

/// <summary>GET /api/coverage/line response.</summary>
public sealed record CoveringTestsResult(List<CoveringTest> Tests, bool Partial);
