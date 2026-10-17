using System.Text.Json;

namespace Labcoat;

// The REST API and /ws of LabcoatApp.Build, per area: the endpoints map to the handlers below.
public static partial class LabcoatApp
{
    private const string IdRequired = "id is required";
    private const string UnknownTest = "Unknown test";

    /// <summary>State, health, summary and the persisted runs.</summary>
    private static void MapStateEndpoints(WebApplication app, Explorer explorer)
    {
        app.MapGet("/api/state", (string? lean) => Results.Json(explorer.Snapshot(includeLogs: true, lean: lean is "1" or "true"), Json.Options));

        // Cheap liveness for health checks (Aspire): no snapshot, no locks held for long.
        app.MapGet("/api/health", () => Results.Json(new { status = "ok", version = Cli.Version, running = explorer.IsRunning }, Json.Options));
        // Counts by outcome + current / last / queued run + project states (Aspire dashboard state text, scripts).
        app.MapGet("/api/summary", () => Results.Json(explorer.Summary(), Json.Options));

        app.MapGet("/api/runs/{runId}/status", (string runId) =>
            explorer.GetRunStatus(runId) is { } st ? Results.Json(st, Json.Options) : Results.NotFound(new { error = "Unknown run id", runId }));

        // Persisted past runs (HistoryStore.cs): summaries newest first, and one run's per-test results.
        app.MapGet("/api/runs", () => Results.Json(explorer.Runs(), Json.Options));
        app.MapGet("/api/runs/{runId}", (string runId) =>
            explorer.GetRun(runId) is { } run ? Results.Json(run, Json.Options) : Results.NotFound());
    }

    /// <summary>The commands: discover, run, debug, cancel, the toggles, quarantine, host restart, impact map, target framework.</summary>
    private static void MapCommandEndpoints(WebApplication app, Explorer explorer)
    {
        app.MapPost("/api/discover", (HttpRequest req) => StartForProjectAsync(req, explorer, explorer.DiscoverCommandAsync));

        app.MapPost("/api/run", (HttpRequest req) => RunTestsAsync(req, explorer));

        // Run tests in a fresh test host that waits for a debugger (event debuggerWaiting carries the pid + attach hints).
        app.MapPost("/api/debug", (HttpRequest req) => DebugTestsAsync(req, explorer));

        app.MapPost("/api/cancel", () =>
        {
            var r = explorer.Cancel();
            return Results.Accepted(value: new { accepted = true, cancelledRunning = r.Running, activeOperation = r.ActiveOperation,
                clearedQueuedRunId = r.ClearedRunId, clearedQueuedRequests = r.ClearedRequests });
        });

        app.MapPost("/api/watch", (HttpRequest req) => SetFlagAsync(req, enabled =>
        {
            explorer.SetWatch(enabled);
            return new { watch = enabled };
        }));

        app.MapPost("/api/coverage", (HttpRequest req) => SetFlagAsync(req, enabled =>
        {
            explorer.SetCoverage(enabled);
            return new { coverageEnabled = enabled };
        }));

        app.MapPost("/api/tracing", (HttpRequest req) => SetFlagAsync(req, enabled =>
        {
            explorer.SetTracing(enabled);
            return new { tracingEnabled = explorer.TracingEnabled };
        }));

        app.MapPost("/api/quarantine", (HttpRequest req) => SetQuarantineAsync(req, explorer));

        app.MapPost("/api/restart-host", (HttpRequest req) => StartForProjectAsync(req, explorer, explorer.RestartHostAsync));

        app.MapPost("/api/impact-map", (HttpRequest req) => StartForProjectAsync(req, explorer, projectId => explorer.BuildImpactMapAsync(projectId)));

        // Multi-targeted projects: run on another target framework (null / "" = the default, the highest .NET version).
        app.MapPost("/api/target-framework", (HttpRequest req) => SetTargetFrameworkAsync(req, explorer));
    }

    /// <summary>Run affected (git, GitAffected.cs): branch / dirty state, the plan (preview, never runs), plan + run.</summary>
    private static void MapAffectedEndpoints(WebApplication app, Explorer explorer)
    {
        app.MapGet("/api/git", async (CancellationToken ct) => Results.Json(await explorer.GitInfoAsync(ct), Json.Options));
        app.MapGet("/api/affected", (string? @base, string? @ref, CancellationToken ct) => PlanAffectedAsync(explorer, @base, @ref, ct));
        app.MapPost("/api/run-affected", (HttpRequest req, CancellationToken ct) => RunAffectedAsync(req, explorer, ct));
    }

    /// <summary>Per-test details on demand (lean clients) and a test's Aspire failure context.</summary>
    private static void MapTestEndpoints(WebApplication app, Explorer explorer)
    {
        // The full node, its history, its full console output.
        app.MapGet("/api/tests/details", (string? projectId, string? id) => TestDetails(explorer, projectId, id));
        app.MapGet("/api/tests/history", (string? projectId, string? id) => TestHistory(explorer, projectId, id));
        app.MapGet("/api/tests/output", (string? projectId, string? id) => TestOutput(explorer, projectId, id));

        // Live console output of a running test (LiveOutput.cs): what was streamed so far, for clients that join mid-test.
        app.MapGet("/api/tests/live-output", (string? projectId, string? id) => TestLiveOutput(explorer, projectId, id));

        // Aspire failure context: error spans + warning/error logs of a test's trace, from the dashboard's telemetry API.
        app.MapGet("/api/tests/telemetry", (string? projectId, string? id, string? traceId, CancellationToken ct) =>
            TestTelemetryAsync(explorer, projectId, id, traceId, ct));
    }

    /// <summary>The coverage reports: full / last run, cached per-test coverage, the tests covering a line.</summary>
    private static void MapCoverageEndpoints(WebApplication app, Explorer explorer)
    {
        // The full coverage report (Coverage page; FullCoverage.cs), or with scope=last the latest coverage run's own
        // report (a subset run: just those tests). 404 when there is none yet.
        app.MapGet("/api/coverage", (string? scope) => CoverageReportOf(explorer, scope));

        // Cached per-test coverage (CoverageCache.cs): never starts a run; 404 when nothing is cached for exactly these ids.
        app.MapGet("/api/coverage/test", (string? projectId, string[]? id) => CachedTestCoverage(explorer, projectId, id));

        // Which tests cover a line (Explorer.Insights.cs): per-test coverage cache (exact) + impact map (approximate).
        app.MapGet("/api/coverage/line", (string? path, int? line) => CoveringTests(explorer, path, line));
    }

    /// <summary>Source files: open one in an editor, read one for the code / coverage view.</summary>
    private static void MapSourceEndpoints(WebApplication app, Explorer explorer)
    {
        // Open a source file in VS Code / Rider / Visual Studio (EditorLauncher.cs); same path rules as /api/source.
        app.MapPost("/api/open-in-editor", (HttpRequest req) => OpenInEditorAsync(req, explorer));

        // Source files for the code / coverage view (see Explorer.CheckSourcePath for what is allowed).
        app.MapGet("/api/source", (string? path) => SourceFileAsync(explorer, path));
    }

    // ------------------------------------------------------------------ command handlers

    /// <summary>discover / restart-host / impact-map: starts <paramref name="start"/> for the body's project (all when none).</summary>
    private static async Task<IResult> StartForProjectAsync(HttpRequest req, Explorer explorer, Func<string?, Task> start)
    {
        var (body, error) = await ReadBody(req);
        if (error is not null) return error;
        if (body?.ProjectId is { } pid && !explorer.HasProject(pid)) return UnknownProject(pid);
        _ = start(body?.ProjectId);
        return Results.Accepted(value: new { accepted = true });
    }

    private static async Task<IResult> RunTestsAsync(HttpRequest req, Explorer explorer)
    {
        var (body, error) = await ReadBody(req);
        if (error is not null) return error;
        return RunResult(explorer.RequestRun(new() { ProjectId = body?.ProjectId, TestIds = body?.TestIds, Coverage = body?.Coverage, Trigger = "api", Repeat = body?.Repeat }));
    }

    private static async Task<IResult> DebugTestsAsync(HttpRequest req, Explorer explorer)
    {
        var (body, error) = await ReadBody(req);
        if (error is not null) return error;
        if (body?.TestIds is not { Count: > 0 }) return Results.BadRequest(new { error = "testIds are required" });
        return RunResult(explorer.RequestRun(new() { ProjectId = body.ProjectId, TestIds = body.TestIds, Coverage = false, Trigger = "debug", Debug = true }));
    }

    /// <summary>watch / coverage / tracing: a required <c>enabled</c> flag; 200 with what <paramref name="apply"/> returns.</summary>
    private static async Task<IResult> SetFlagAsync<T>(HttpRequest req, Func<bool, T> apply)
    {
        var (body, error) = await ReadBody(req);
        if (error is not null) return error;
        if (body?.Enabled is not { } enabled) return Results.BadRequest(new { error = "'enabled' (boolean) is required" });
        return Results.Ok(apply(enabled));
    }

    private static async Task<IResult> SetQuarantineAsync(HttpRequest req, Explorer explorer)
    {
        var (body, error) = await ReadBody(req);
        if (error is not null) return error;
        if (body?.ProjectId is null || body.Id is null) return Results.BadRequest(new { error = "projectId and id are required" });
        if (explorer.SetQuarantine(body.ProjectId, body.Id, body.Quarantined ?? true) is { } conflict) return Results.Conflict(new { error = conflict });
        return Results.Ok(new { quarantined = explorer.Snapshot().Quarantined });
    }

    private static async Task<IResult> SetTargetFrameworkAsync(HttpRequest req, Explorer explorer)
    {
        var (body, error) = await ReadBody(req);
        if (error is not null) return error;
        if (body?.ProjectId is null) return Results.BadRequest(new { error = "projectId is required" });
        var (status, message) = explorer.SetTargetFramework(body.ProjectId, body.TargetFramework);
        return status == 202 ? Results.Accepted(value: new { accepted = true }) : Results.Json(new { error = message }, statusCode: status);
    }

    private static async Task<IResult> PlanAffectedAsync(Explorer explorer, string? baseKind, string? reference, CancellationToken ct)
    {
        try { return Results.Json(await explorer.PlanAffectedAsync(baseKind, reference, ct), Json.Options); }
        catch (AffectedException ex) { return AffectedError(ex); }
    }

    private static async Task<IResult> RunAffectedAsync(HttpRequest req, Explorer explorer, CancellationToken ct)
    {
        var (body, error) = await ReadBody(req);
        if (error is not null) return error;
        Explorer.RunAffectedResult r;
        try { r = await explorer.RunAffectedAsync(body?.Base, body?.Ref, body?.Coverage, body?.BuildImpactMap ?? false, "api", ct); }
        catch (AffectedException ex) { return AffectedError(ex); }
        var counts = r.Plan!.Counts;
        if (r.ImpactMapFirst)
            return Results.Json(new { accepted = true, impactMapFirst = true, tests = counts.Tests, files = counts.Files, projects = counts.Projects },
                Json.Options, statusCode: StatusCodes.Status202Accepted);
        if (r.Run is null)
            return Results.Json(new { accepted = false, reason = $"No tests are affected by the {counts.Files} changed file{(counts.Files == 1 ? "" : "s")}.", files = counts.Files }, Json.Options);
        if (r.Run.Status != 202) return RunResult(r.Run);
        return Results.Json(new { accepted = true, runId = r.Run.RunId, queued = r.Run.Queued, merged = r.Run.Merged, tests = counts.Tests, files = counts.Files, projects = counts.Projects },
            Json.Options, statusCode: StatusCodes.Status202Accepted);
    }

    // ------------------------------------------------------------------ test, coverage and source handlers

    private static IResult TestDetails(Explorer explorer, string? projectId, string? id)
    {
        if (string.IsNullOrEmpty(id)) return Results.BadRequest(new { error = IdRequired });
        return explorer.GetTest(projectId, id) is { } t ? Results.Json(t, Json.Options) : Results.NotFound(new { error = UnknownTest });
    }

    private static IResult TestHistory(Explorer explorer, string? projectId, string? id)
    {
        if (projectId is not null && !explorer.HasProject(projectId)) return UnknownProject(projectId);
        var list = explorer.GetHistory(projectId, id);
        return id is not null && list.Count == 0 ? Results.NotFound(new { error = UnknownTest }) : Results.Json(new { tests = list }, Json.Options);
    }

    private static IResult TestOutput(Explorer explorer, string? projectId, string? id)
    {
        if (string.IsNullOrEmpty(id)) return Results.BadRequest(new { error = IdRequired });
        return explorer.GetOutput(projectId, id) is { } output ? Results.Json(output, Json.Options) : Results.NotFound(new { error = UnknownTest });
    }

    private static IResult TestLiveOutput(Explorer explorer, string? projectId, string? id)
    {
        if (string.IsNullOrEmpty(id)) return Results.BadRequest(new { error = IdRequired });
        return explorer.GetLiveOutput(projectId, id) is { } live ? Results.Json(live, Json.Options) : Results.NotFound(new { error = "The test is not running" });
    }

    private static async Task<IResult> TestTelemetryAsync(Explorer explorer, string? projectId, string? id, string? traceId, CancellationToken ct)
    {
        if (!explorer.TracingEnabled) return Results.Json(new { error = "Trace collection is off (setTracing)." }, statusCode: StatusCodes.Status409Conflict);
        if (!DashboardTelemetry.Available) return Results.Json(new { error = "No Aspire dashboard is known (Labcoat is not hosted by an Aspire AppHost)." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        if (string.IsNullOrEmpty(traceId))
        {
            var (testTraceId, error) = TestTraceId(explorer, projectId, id);
            if (error is not null) return error;
            traceId = testTraceId!;
        }
        if (traceId.Length is < 8 or > 64 || !traceId.All(Uri.IsHexDigit)) return Results.BadRequest(new { error = "traceId must be hex" });
        try { return Results.Json(await DashboardTelemetry.GetAsync(traceId.ToLowerInvariant(), ct), Json.Options); }
        catch (Exception ex) when (ex is DashboardTelemetryException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>The trace id of a test (telemetry without a traceId), or the error result.</summary>
    private static (string? TraceId, IResult? Error) TestTraceId(Explorer explorer, string? projectId, string? id)
    {
        if (string.IsNullOrEmpty(id)) return (null, Results.BadRequest(new { error = "id (or traceId) is required" }));
        var (found, tid) = explorer.TestTraceId(projectId, id);
        if (!found) return (null, Results.NotFound(new { error = UnknownTest }));
        if (tid is null) return (null, Results.NotFound(new { error = "The test has no trace id (does it use Labcoat.Testing?)" }));
        return (tid, null);
    }

    private static IResult CoverageReportOf(Explorer explorer, string? scope)
    {
        if (scope is not (null or "" or "full" or "last")) return Results.BadRequest(new { error = "scope must be full or last" });
        var report = scope == "last" ? explorer.LastCoverageReport : explorer.FullCoverageReport;
        return report is not null ? Results.Json(report, Json.Options) : Results.NotFound();
    }

    private static IResult CachedTestCoverage(Explorer explorer, string? projectId, string[]? id)
    {
        var ids = (id ?? []).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (ids.Count == 0) return Results.BadRequest(new { error = IdRequired });
        return explorer.GetCachedTestCoverage(projectId, ids) is { } report ? Results.Json(report, Json.Options) : Results.NotFound();
    }

    private static IResult CoveringTests(Explorer explorer, string? path, int? line)
    {
        if (string.IsNullOrWhiteSpace(path) || line is not > 0) return Results.BadRequest(new { error = "path and line (>= 1) are required" });
        return Results.Json(explorer.CoveringTests(path, line.Value), Json.Options);
    }

    private static async Task<IResult> OpenInEditorAsync(HttpRequest req, Explorer explorer)
    {
        OpenInEditorRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<OpenInEditorRequest>(req.Body, Json.Options); }
        catch (JsonException ex) { return Results.BadRequest(new { error = "Malformed JSON: " + ex.Message }); }
        var (status, ok, message) = explorer.OpenInEditor(body?.Editor, body?.Path, body?.Line, body?.Column);
        return Results.Json(new { ok, message }, Json.Options, statusCode: status);
    }

    private static async Task<IResult> SourceFileAsync(Explorer explorer, string? path)
    {
        var access = explorer.CheckSourcePath(path);
        if (access.Result == SourceAccessResult.Forbidden) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (access.Result == SourceAccessResult.NotFound) return Results.NotFound();
        var info = new FileInfo(access.FullPath!);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (info.Length > 2 * 1024 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var bytes = await File.ReadAllBytesAsync(info.FullName);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        return Results.Text(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿'), "text/plain; charset=utf-8");
    }

    // ------------------------------------------------------------------ WebSocket

    private static async Task HandleWebSocketAsync(HttpContext ctx, EventHub hub, Explorer explorer, CancellationToken stopping)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // ?lean=1: lean snapshots / test updates (no history, no console output) and settingsChanged instead of state on toggles.
        var lean = ctx.Request.Query["lean"] is { Count: > 0 } lv && lv[0] is "1" or "true";
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        // Also stop on app shutdown, otherwise open sockets hold up shutdown for the full host timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        await hub.HandleAsync(socket, explorer.Sync, () => new StateEvent(explorer.Snapshot(includeLogs: true, lean: lean)), explorer.HandleCommandAsync, cts.Token, lean);
    }
}
