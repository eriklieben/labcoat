using System.Text.Json;

namespace Labcoat;

/// <summary>
/// Small per-workspace settings file (<c>settings.json</c> next to <c>history.json</c>, see <see cref="HistoryStore"/>):
/// runtime choices that survive a restart — the trace collection toggle, the quarantined tests and the chosen target
/// framework of multi-targeted projects.
/// Quarantined tests are kept by the project's path relative to the workspace root (<see cref="IProjectKeys"/>, version 2),
/// not by its id, which can change across restarts; the API still speaks "projectId|id" keys of the current ids. A
/// version 1 file (id keys) is migrated when it is read, through the current ids (the same ids unless a project file
/// name repeats); keys of an id that no current project has stay id keys.
/// Written synchronously on every change (changes are rare user actions). Thread safe.
/// </summary>
public sealed class WorkspaceSettings
{
    public const int FileVersion = 2;

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        /// <summary>Last runtime choice of the trace toggle; null = never toggled (the CLI / hosting default applies).</summary>
        public bool? TracingEnabled { get; set; }
        /// <summary>"projectId|id" keys: all of them in version 1; in version 2 those whose project was unknown.</summary>
        public List<string> Quarantined { get; set; } = [];
        /// <summary>Version 2: quarantined tests by project path relative to the workspace root.</summary>
        public List<QuarantinedTest> QuarantinedTests { get; set; } = [];
        /// <summary>Chosen target framework per multi-targeted project (key: project file path).</summary>
        public Dictionary<string, string> TargetFrameworks { get; set; } = [];
    }

    private sealed class QuarantinedTest
    {
        /// <summary>The project's path relative to the workspace root.</summary>
        public string Project { get; set; } = "";
        public string Id { get; set; } = "";
    }

    private readonly object _gate = new();
    private readonly FileModel _model;
    private readonly Action<string, string>? _log;
    private readonly IProjectKeys? _keys;

    public string FilePath { get; }

    /// <param name="keys">The project paths of the current ids; null: quarantined tests are kept by id (tests, tools).</param>
    public WorkspaceSettings(string directory, Action<string, string>? log = null, IProjectKeys? keys = null)
    {
        _log = log;
        _keys = keys;
        FilePath = Path.Combine(directory, "settings.json");
        _model = Load();
        MigrateQuarantine();
    }

    private FileModel Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), Json.Options) ?? new FileModel();
        }
        catch (Exception ex)
        {
            _log?.Invoke("warning", $"Could not read workspace settings ({FilePath}): {ex.Message}");
        }
        return new FileModel();
    }

    /// <summary>Id keys of current projects become path entries (version 1 files, and keys set while a project was unknown).</summary>
    private void MigrateQuarantine()
    {
        if (_keys is null) return;
        lock (_gate)
        {
            var moved = 0;
            foreach (var key in _model.Quarantined.ToList())
            {
                var bar = key.IndexOf('|');
                if (bar < 0 || _keys.ProjectPathOf(key[..bar]) is not { } rel) continue;
                _model.Quarantined.Remove(key);
                if (FindLocked(rel, key[(bar + 1)..]) < 0) _model.QuarantinedTests.Add(new QuarantinedTest { Project = rel, Id = key[(bar + 1)..] });
                moved++;
            }
            _model.Version = FileVersion;
            if (moved > 0) Save();
        }
    }

    public bool? TracingEnabled { get { lock (_gate) return _model.TracingEnabled; } }

    public void SetTracingEnabled(bool enabled)
    {
        lock (_gate)
        {
            _model.TracingEnabled = enabled;
            Save();
        }
    }

    public static string QuarantineKey(string projectId, string id) => projectId + "|" + id;

    /// <summary>The quarantined tests of the current projects as "projectId|id" keys (plus the id keys of unknown projects).</summary>
    public List<string> Quarantined()
    {
        lock (_gate)
        {
            var keys = new List<string>(_model.QuarantinedTests.Count + _model.Quarantined.Count);
            if (_keys is not null)
            {
                foreach (var t in _model.QuarantinedTests)
                    if (_keys.ProjectIdOf(t.Project) is { } projectId) keys.Add(QuarantineKey(projectId, t.Id));
            }
            keys.AddRange(_model.Quarantined.Where(k => !keys.Contains(k)));
            return keys;
        }
    }

    public bool IsQuarantined(string projectId, string id)
    {
        var rel = _keys?.ProjectPathOf(projectId);
        lock (_gate) return IsQuarantinedLocked(rel, projectId, id);
    }

    private bool IsQuarantinedLocked(string? rel, string projectId, string id) =>
        (rel is not null && FindLocked(rel, id) >= 0) || _model.Quarantined.Contains(QuarantineKey(projectId, id));

    private int FindLocked(string rel, string id) =>
        _model.QuarantinedTests.FindIndex(t => t.Id == id && WorkspaceIdentity.PathComparer.Equals(t.Project, rel));

    /// <summary>Adds / removes a test; returns true when the set changed.</summary>
    public bool SetQuarantined(string projectId, string id, bool quarantined)
    {
        var rel = _keys?.ProjectPathOf(projectId);
        var key = QuarantineKey(projectId, id);
        lock (_gate)
        {
            if (IsQuarantinedLocked(rel, projectId, id) == quarantined) return false;
            if (!quarantined)
            {
                _model.Quarantined.Remove(key);
                if (rel is not null) _model.QuarantinedTests.RemoveAll(t => t.Id == id && WorkspaceIdentity.PathComparer.Equals(t.Project, rel));
            }
            else if (rel is null) _model.Quarantined.Add(key);
            else _model.QuarantinedTests.Add(new QuarantinedTest { Project = rel, Id = id });
            Save();
            return true;
        }
    }

    /// <summary>The target framework chosen for a multi-targeted project (setTargetFramework), or null.</summary>
    public string? TargetFramework(string projectPath)
    {
        lock (_gate) return _model.TargetFrameworks.GetValueOrDefault(projectPath);
    }

    /// <summary>Remembers (or with null forgets) the target framework of a project.</summary>
    public void SetTargetFramework(string projectPath, string? tfm)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(tfm)) _model.TargetFrameworks.Remove(projectPath);
            else _model.TargetFrameworks[projectPath] = tfm;
            Save();
        }
    }

    private void Save()
    {
        // Another Labcoat owns this workspace's data folder (WorkspaceLock): the change applies to this session only.
        if (!WorkspaceLock.CanWrite(Path.GetDirectoryName(FilePath)!)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_model, Json.Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Invoke("warning", $"Could not save workspace settings ({FilePath}): {ex.Message}");
        }
    }
}
