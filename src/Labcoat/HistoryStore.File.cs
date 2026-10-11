using System.Text.Json;

namespace Labcoat;

/// <summary>
/// history.json, version 2: per-test history and past runs in columns, with project ids, test keys and run ids
/// written once and referenced by index. Version 1 wrote every history entry and every result of every past run as an
/// object repeating property names, the test's project id + id and the run id (~140 bytes per history entry, ~115 per
/// past-run result), so 20,000 tests × 20 history entries + 50 runs reached ~80 MB; version 2 is ~3× smaller. Version 1
/// files are still read (and rewritten as version 2 on the next save). What the API returns is unchanged: the store
/// materializes the same <see cref="HistoryEntry"/> / <see cref="RunDetails"/> objects as before.
/// </summary>
public sealed partial class HistoryStore
{
    public const int FileVersion = 2;

    /// <summary>One character per <see cref="Outcome"/> (indexed by its value).</summary>
    private const string OutcomeCodes = "nqrpfstc";

    private static char Code(Outcome o) => OutcomeCodes[(int)o];

    private static Outcome FromCode(char c) =>
        OutcomeCodes.IndexOf(c) is var i and >= 0 ? (Outcome)i : throw new JsonException($"Unknown outcome code '{c}'.");

    private sealed record TestSnapshot(string ProjectId, string Id, List<HistoryEntry> Runs, StoredResult? Last);

    private sealed record Snapshot(List<string> Projects, TestSnapshot[] Tests, StoredRun[] Runs)
    {
        /// <summary>The workspace-relative path of a project id (written as <c>projectPaths</c>, parallel to <c>projectIds</c>).</summary>
        public Func<string, string?>? PathOf { get; init; }
    }

    // ---- the file model (what is read; Write produces the same shape)

    // Internal, not private: the properties are set by System.Text.Json only (init), which code analysis can't see.
    internal sealed class FileModel
    {
        public int Version { get; init; } = 1;
        public List<string> Projects { get; init; } = [];

        // version 1
        public Dictionary<string, V1Entry>? Tests { get; init; }
        public List<RunDetails>? Runs { get; init; }

        // version 2
        public List<string>? ProjectIds { get; init; }
        /// <summary>Per project id: its path relative to the workspace root (null = unknown); absent before workspace folders.</summary>
        public List<string?>? ProjectPaths { get; init; }
        public List<string?>? RunIds { get; init; }
        /// <summary>Per-test history; test key <c>i</c> = <c>History[i]</c>.</summary>
        public List<TestModel>? History { get; init; }
        /// <summary>Test keys only referenced by past runs (e.g. tests that were only ever cancelled); they follow the history keys.</summary>
        public List<KeyModel>? ExtraKeys { get; init; }
        public List<RunModel>? PastRuns { get; init; }
    }

    internal sealed class V1Entry
    {
        public List<HistoryEntry> Runs { get; init; } = [];
        public StoredResult? Last { get; init; }
    }

    internal sealed class KeyModel
    {
        public int Project { get; init; }
        public string Id { get; init; } = "";
    }

    internal sealed class TestModel
    {
        public int Project { get; init; }
        public string Id { get; init; } = "";
        /// <summary>One <see cref="OutcomeCodes"/> character per history entry (oldest first); the arrays below are parallel.</summary>
        public string Outcomes { get; init; } = "";
        public double?[]? Durations { get; init; }
        public string?[]? At { get; init; }
        /// <summary>Index into <see cref="FileModel.RunIds"/>, -1 = no run id.</summary>
        public int[]? RunIds { get; init; }
        /// <summary>Indexes of the entries with <c>flaky: true</c>.</summary>
        public int[]? Flaky { get; init; }
        public StoredResult? Last { get; init; }
    }

    internal sealed class RunModel
    {
        public RunSummary? Run { get; init; }
        /// <summary>Test key index per result.</summary>
        public int[] Tests { get; init; } = [];
        public string Outcomes { get; init; } = "";
        public double?[]? Durations { get; init; }
        public List<ErrorModel>? Errors { get; init; }
    }

    internal sealed class ErrorModel
    {
        /// <summary>Result index.</summary>
        public int I { get; init; }
        public string? ErrorMessage { get; init; }
        public string? StackTrace { get; init; }
    }

    // ---- read

    private static (Dictionary<(string, string), Entry>, List<StoredRun>) FromModel(FileModel model) =>
        model.Version < 2 ? FromV1(model) : FromV2(model);

    private static (Dictionary<(string, string), Entry>, List<StoredRun>) FromV1(FileModel model)
    {
        var tests = new Dictionary<(string, string), Entry>();
        foreach (var (key, v) in model.Tests ?? [])
        {
            var bar = key.IndexOf('|');
            if (bar < 0) continue;
            var (projectId, id) = (key[..bar], key[(bar + 1)..]);
            tests[(projectId, id)] = new Entry(projectId, id) { Runs = v.Runs ?? [], Last = v.Last };
        }
        var runs = (model.Runs ?? []).Where(r => r.Run is not null).Select(r => StoredRun.From(r.Run, r.Results ?? [])).ToList();
        return (tests, runs);
    }

    private static (Dictionary<(string, string), Entry>, List<StoredRun>) FromV2(FileModel model)
    {
        var tests = new Dictionary<(string, string), Entry>();
        var projectIds = model.ProjectIds ?? [];
        var runIds = model.RunIds ?? [];
        var keys = new List<(string ProjectId, string Id)>();
        foreach (var t in model.History ?? [])
        {
            var projectId = projectIds[t.Project];
            keys.Add((projectId, t.Id));
            tests[(projectId, t.Id)] = new Entry(projectId, t.Id) { Runs = HistoryEntries(t, runIds), Last = t.Last };
        }
        foreach (var k in model.ExtraKeys ?? []) keys.Add((projectIds[k.Project], k.Id));
        var runs = (model.PastRuns ?? []).Where(r => r.Run is not null).Select(r => StoredRunOf(r, keys)).ToList();
        return (tests, runs);
    }

    private static List<HistoryEntry> HistoryEntries(TestModel t, List<string?> runIds)
    {
        var n = t.Outcomes.Length;
        var entries = new List<HistoryEntry>(n);
        for (var i = 0; i < n; i++)
        {
            var run = t.RunIds is { } ri && ri[i] >= 0 ? runIds[ri[i]] : null;
            entries.Add(new HistoryEntry(FromCode(t.Outcomes[i]), t.Durations?[i], t.At?[i]!, run, t.Flaky is { } f && Array.IndexOf(f, i) >= 0 ? true : null));
        }
        return entries;
    }

    private static StoredRun StoredRunOf(RunModel r, List<(string ProjectId, string Id)> keys)
    {
        var n = r.Tests.Length;
        if (r.Outcomes.Length != n) throw new JsonException($"Run {r.Run!.RunId}: {n} tests but {r.Outcomes.Length} outcomes.");
        var stored = new StoredRun
        {
            Run = r.Run!,
            ProjectIds = new string[n],
            Ids = new string[n],
            Outcomes = new Outcome[n],
            Durations = new double[n],
            Errors = r.Errors is { Count: > 0 } ? r.Errors.ToDictionary(e => e.I, e => (e.ErrorMessage, e.StackTrace)) : null,
        };
        for (var i = 0; i < n; i++)
        {
            (stored.ProjectIds[i], stored.Ids[i]) = keys[r.Tests[i]];
            stored.Outcomes[i] = FromCode(r.Outcomes[i]);
            stored.Durations[i] = r.Durations?[i] ?? double.NaN;
        }
        return stored;
    }

    // ---- write

    /// <summary>Flush the JSON writer to the file whenever this much is buffered (keeps the buffer off the large object heap).</summary>
    private const int FlushBytes = 32 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = Json.Options.Encoder };

    /// <summary>The string tables of a version 2 file: project ids, run ids and test keys, each written once.</summary>
    private sealed class StringTables
    {
        public Dictionary<string, int> ProjectIndex { get; } = new(StringComparer.Ordinal);
        public List<string> ProjectIds { get; } = [];
        public Dictionary<string, int> RunIndex { get; } = new(StringComparer.Ordinal);
        public List<string> RunIds { get; } = [];
        public Dictionary<(string, string), int> KeyIndex { get; } = [];
        /// <summary>Test keys only referenced by past runs; they follow the history keys.</summary>
        public List<(string ProjectId, string Id)> ExtraKeys { get; } = [];

        public static StringTables Of(Snapshot s)
        {
            var tables = new StringTables();
            foreach (var t in s.Tests)
            {
                tables.AddProject(t.ProjectId);
                tables.KeyIndex[(t.ProjectId, t.Id)] = tables.KeyIndex.Count;
                foreach (var h in t.Runs)
                    if (h.RunId is { } r && tables.RunIndex.TryAdd(r, tables.RunIds.Count)) tables.RunIds.Add(r);
            }
            foreach (var r in s.Runs)
                for (var i = 0; i < r.Count; i++) tables.AddRunKey(r.ProjectIds[i], r.Ids[i]);
            return tables;
        }

        private void AddProject(string projectId)
        {
            if (ProjectIndex.TryAdd(projectId, ProjectIds.Count)) ProjectIds.Add(projectId);
        }

        private void AddRunKey(string projectId, string id)
        {
            if (!KeyIndex.TryAdd((projectId, id), KeyIndex.Count)) return;
            AddProject(projectId);
            ExtraKeys.Add((projectId, id));
        }
    }

    private static void MaybeFlush(Utf8JsonWriter w)
    {
        if (w.BytesPending > FlushBytes) w.Flush();
    }

    /// <summary>Streams <paramref name="s"/> as a version 2 file (see the class remarks).</summary>
    private static void Write(Stream stream, Snapshot s)
    {
        var tables = StringTables.Of(s);
        using var w = new Utf8JsonWriter(stream, WriterOptions);

        w.WriteStartObject();
        w.WriteNumber("version", FileVersion);
        w.WriteStartArray("projects");
        foreach (var p in s.Projects) w.WriteStringValue(p);
        w.WriteEndArray();
        w.WriteStartArray("projectIds");
        foreach (var p in tables.ProjectIds) w.WriteStringValue(p);
        w.WriteEndArray();
        if (s.PathOf is { } pathOf)
        {
            w.WriteStartArray("projectPaths");
            foreach (var p in tables.ProjectIds)
            {
                if (pathOf(p) is { } rel) w.WriteStringValue(rel);
                else w.WriteNullValue();
            }
            w.WriteEndArray();
        }
        w.WriteStartArray("runIds");
        foreach (var r in tables.RunIds)
        {
            w.WriteStringValue(r);
            MaybeFlush(w);
        }
        w.WriteEndArray();

        w.WriteStartArray("history");
        Span<char> codes = stackalloc char[MaxRuns];
        foreach (var t in s.Tests)
        {
            WriteTest(w, t, tables, codes);
            MaybeFlush(w);
        }
        w.WriteEndArray();

        w.WriteStartArray("extraKeys");
        foreach (var (p, id) in tables.ExtraKeys)
        {
            w.WriteStartObject();
            w.WriteNumber("project", tables.ProjectIndex[p]);
            w.WriteString("id", id);
            w.WriteEndObject();
            MaybeFlush(w);
        }
        w.WriteEndArray();

        w.WriteStartArray("pastRuns");
        foreach (var r in s.Runs)
        {
            WritePastRun(w, r, tables);
            MaybeFlush(w);
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
    }

    /// <summary>One test's history: parallel arrays of outcomes, durations, times and run ids.</summary>
    private static void WriteTest(Utf8JsonWriter w, TestSnapshot t, StringTables tables, Span<char> codes)
    {
        var runs = t.Runs;
        w.WriteStartObject();
        w.WriteNumber("project", tables.ProjectIndex[t.ProjectId]);
        w.WriteString("id", t.Id);
        var buffer = runs.Count <= codes.Length ? codes[..runs.Count] : new char[runs.Count];
        for (var i = 0; i < runs.Count; i++) buffer[i] = Code(runs[i].Outcome);
        w.WriteString("outcomes", buffer);
        w.WriteStartArray("durations");
        foreach (var h in runs) WriteNumberOrNull(w, h.DurationMs);
        w.WriteEndArray();
        w.WriteStartArray("at");
        foreach (var h in runs) w.WriteStringValue(h.At);
        w.WriteEndArray();
        w.WriteStartArray("runIds");
        foreach (var h in runs) w.WriteNumberValue(h.RunId is { } r ? tables.RunIndex[r] : -1);
        w.WriteEndArray();
        if (runs.Any(h => h.Flaky == true))
        {
            w.WriteStartArray("flaky");
            for (var i = 0; i < runs.Count; i++)
                if (runs[i].Flaky == true) w.WriteNumberValue(i);
            w.WriteEndArray();
        }
        if (t.Last is not null)
        {
            w.WritePropertyName("last");
            JsonSerializer.Serialize(w, t.Last, Json.Options);
        }
        w.WriteEndObject();
    }

    private static void WritePastRun(Utf8JsonWriter w, StoredRun r, StringTables tables)
    {
        w.WriteStartObject();
        w.WritePropertyName("run");
        JsonSerializer.Serialize(w, r.Run, Json.Options);
        w.WriteStartArray("tests");
        for (var i = 0; i < r.Count; i++)
        {
            w.WriteNumberValue(tables.KeyIndex[(r.ProjectIds[i], r.Ids[i])]);
            MaybeFlush(w);
        }
        w.WriteEndArray();
        // the outcome codes in chunks: one string value, never one large char[]
        w.WritePropertyName("outcomes");
        WriteOutcomes(w, r.Outcomes);
        w.WriteStartArray("durations");
        for (var i = 0; i < r.Count; i++)
        {
            WriteNumberOrNull(w, double.IsNaN(r.Durations[i]) ? null : r.Durations[i]);
            MaybeFlush(w);
        }
        w.WriteEndArray();
        if (r.Errors is { Count: > 0 }) WriteErrors(w, r.Errors);
        w.WriteEndObject();
    }

    private static void WriteErrors(Utf8JsonWriter w, Dictionary<int, (string? Message, string? Stack)> errors)
    {
        w.WriteStartArray("errors");
        foreach (var (i, (message, stack)) in errors.OrderBy(e => e.Key))
        {
            w.WriteStartObject();
            w.WriteNumber("i", i);
            if (message is not null) w.WriteString("errorMessage", message);
            if (stack is not null) w.WriteString("stackTrace", stack);
            w.WriteEndObject();
            MaybeFlush(w);
        }
        w.WriteEndArray();
    }

    private static void WriteNumberOrNull(Utf8JsonWriter w, double? value)
    {
        if (value is { } v) w.WriteNumberValue(v);
        else w.WriteNullValue();
    }

    /// <summary>The outcome codes as one JSON string, written as ASCII bytes in segments (no full-length char[] / string).</summary>
    private static void WriteOutcomes(Utf8JsonWriter w, Outcome[] outcomes)
    {
        const int Chunk = 4096;
        Span<byte> buffer = stackalloc byte[Chunk];
        if (outcomes.Length == 0)
        {
            w.WriteStringValue("");
            return;
        }
        for (var start = 0; start < outcomes.Length; start += Chunk)
        {
            var n = Math.Min(Chunk, outcomes.Length - start);
            for (var i = 0; i < n; i++) buffer[i] = (byte)Code(outcomes[start + i]);
            w.WriteStringValueSegment(buffer[..n], isFinalSegment: start + n >= outcomes.Length);
        }
    }
}
