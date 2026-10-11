using System.Text.Json;
using System.Text.Json.Nodes;
using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>MTP test node mapping (MtpMapping) and the JSON shape of the contract types (Models.cs).</summary>
[TestClass]
public class ModelTests
{
    private static JsonObject Node(string json) => JsonNode.Parse(json)!.AsObject();

    [TestMethod]
    public void Mtp_nodes_map_onto_test_nodes()
    {
        var t = MtpMapping.ToTestNode("P", Node("""
            {"uid":"u1","display-name":"Multiply (2,3,6)","node-type":"action","execution-state":"passed",
             "location.file":"/src/CalcTests.cs","location.line-start":12,"location.line-end":14,
             "location.type":"Shop.Math.Outer+CalcTests","location.method":"Multiply(System.Int32,System.Int32)",
             "traits":[{"Integration":""},{"Owner":"erik"},{"Priority":1},"not an object"],
             "time.duration-ms":103.98765,
             "standardOutput":"line 1\r\nlabcoat:traceId=0123456789abcdef0123456789abcdef\r\nline 2",
             "standardError":"","error.message":"msg","error.stacktrace":"at x"}
            """));
        Assert.AreEqual(("u1", "P", "Multiply (2,3,6)"), (t.Id, t.ProjectId, t.DisplayName));
        Assert.AreEqual(("Shop.Math", "Outer+CalcTests", "Multiply"), (t.Namespace, t.ClassName, t.MethodName));
        Assert.AreEqual(("/src/CalcTests.cs", 12, 14), (t.FilePath, t.Line, t.LineEnd));
        CollectionAssert.AreEqual(new[] { new Trait("Category", "Integration"), new Trait("Owner", "erik"), new Trait("Priority", "1") }, t.Traits);
        Assert.AreEqual(103.99, t.DurationMs);
        Assert.AreEqual("line 1\nline 2", t.StdOut); // CRLF normalized, trace id marker removed
        Assert.AreEqual("0123456789abcdef0123456789abcdef", t.TraceId);
        Assert.IsNull(t.StdErr);
        Assert.AreEqual(("msg", "at x"), (t.ErrorMessage, t.StackTrace));
        Assert.AreEqual(Outcome.NotRun, t.Outcome); // the outcome is applied separately

        var bare = MtpMapping.ToTestNode("P", Node("""{"uid":"u2","location.type":"NoNamespace","location.method":"M","location.line-start":"x"}"""));
        Assert.AreEqual("u2", bare.DisplayName);
        Assert.AreEqual((null, "NoNamespace", "M"), (bare.Namespace, bare.ClassName, bare.MethodName));
        Assert.IsNull(bare.Line);
        Assert.IsNull(bare.DurationMs);
        Assert.IsEmpty(bare.Traits);

        var empty = MtpMapping.ToTestNode("P", new JsonObject());
        Assert.AreEqual(("", ""), (empty.Id, empty.DisplayName));
        Assert.IsNull(empty.ClassName);
    }

    [TestMethod]
    [DataRow("in-progress", Outcome.Running)]
    [DataRow("passed", Outcome.Passed)]
    [DataRow("failed", Outcome.Failed)]
    [DataRow("error", Outcome.Failed)]
    [DataRow("skipped", Outcome.Skipped)]
    [DataRow("timed-out", Outcome.TimedOut)]
    [DataRow("cancelled", Outcome.Cancelled)]
    public void Execution_states_map_to_outcomes(string state, Outcome outcome) => Assert.AreEqual(outcome, MtpMapping.MapState(state));

    [TestMethod]
    public void Discovered_and_unknown_states_are_ignored()
    {
        Assert.IsNull(MtpMapping.MapState("discovered"));
        Assert.IsNull(MtpMapping.MapState(null));
    }

    private static TestNode Apply(Outcome outcome, string? message, string? stack = null, string? state = null)
    {
        var t = new TestNode { Id = "u", ProjectId = "P", DisplayName = "old", FilePath = "/old.cs", Line = 1, LineEnd = 2, Stale = true };
        var incoming = new TestNode { Id = "u", ProjectId = "P", DisplayName = "new", ErrorMessage = message, StackTrace = stack, DurationMs = 5, TraceId = "tid" };
        MtpMapping.ApplyResult(t, incoming, outcome, state);
        return t;
    }

    [TestMethod]
    public void Applying_a_result_guesses_the_exception_type()
    {
        Assert.AreEqual("System.InvalidOperationException",
            Apply(Outcome.Failed, "Test method X threw exception: System.InvalidOperationException: nope").ErrorType);
        Assert.AreEqual("System.ArgumentNullException", Apply(Outcome.Failed, "System.ArgumentNullException: value").ErrorType);
        Assert.AreEqual("Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
            Apply(Outcome.Failed, "Assert.AreEqual failed.", "   at Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual()\n   at T.M()").ErrorType);
        Assert.AreEqual("Xunit.Sdk.XunitException", Apply(Outcome.Failed, "x", "at Xunit.Assert.Equal()").ErrorType);
        Assert.AreEqual("NUnit.Framework.AssertionException", Apply(Outcome.Failed, "x", "at NUnit.Framework.Assert.That()").ErrorType);
        Assert.IsNull(Apply(Outcome.Failed, null, "at My.Code()").ErrorType);
        Assert.IsNull(Apply(Outcome.Failed, null, null).ErrorType);
        Assert.AreEqual("Timeout", Apply(Outcome.TimedOut, "took too long", state: "timed-out").ErrorType);
        Assert.IsNull(Apply(Outcome.Passed, "System.Exception: ignored").ErrorType);
    }

    [TestMethod]
    public void Applying_a_result_copies_it_and_keeps_known_locations()
    {
        var t = Apply(Outcome.Passed, null);
        Assert.AreEqual((Outcome.Passed, "new", 5.0, "tid"), (t.Outcome, t.DisplayName, t.DurationMs, t.TraceId));
        Assert.IsNull(t.Stale);
        Assert.IsNotNull(t.LastRunAt);
        Assert.AreEqual(("/old.cs", 1, 2), (t.FilePath, t.Line, t.LineEnd)); // incoming had no location

        var withLocation = new TestNode { Id = "u", ProjectId = "P", DisplayName = "n", FilePath = "/new.cs", Line = 7, LineEnd = 9 };
        MtpMapping.ApplyResult(t, withLocation, Outcome.Skipped, "skipped");
        Assert.AreEqual(("/new.cs", 7, 9), (t.FilePath, t.Line, t.LineEnd));
    }

    // ------------------------------------------------------------------ models

    [TestMethod]
    public void Copies_do_not_share_mutable_lists()
    {
        var t = new TestNode { Id = "1", ProjectId = "P", DisplayName = "T", Traits = [new("a", "b")] };
        var copy = t.Copy();
        copy.Traits.Add(new("c", "d"));
        Assert.HasCount(1, t.Traits);

        var p = new TestProject { Id = "P", Name = "P", Path = "/p.csproj", BuildErrors = [new BuildDiagnostic { Severity = "error", Message = "m" }],
            Diagnostics = [new ProjectDiagnostic("warning", "w")], AvailableFrameworks = ["net10.0"] };
        var pc = p.Copy();
        pc.BuildErrors!.Clear();
        pc.Diagnostics!.Clear();
        pc.AvailableFrameworks!.Clear();
        Assert.HasCount(1, p.BuildErrors!);
        Assert.HasCount(1, p.Diagnostics!);
        Assert.HasCount(1, p.AvailableFrameworks!);
        Assert.IsNull(new TestProject { Id = "Q", Name = "Q", Path = "/q" }.Copy().BuildErrors);

        var run = new RunSummary { RunId = "r", StartedAt = "s", Passed = 1 };
        var rc = run.Copy();
        rc.Passed = 2;
        Assert.AreEqual(1, run.Passed);
    }

    [TestMethod]
    public void Clearing_a_result_keeps_identity_and_outcome()
    {
        var t = new TestNode
        {
            Id = "1", ProjectId = "P", DisplayName = "T", Outcome = Outcome.Failed, DurationMs = 1, ErrorMessage = "e", ErrorType = "x",
            StackTrace = "s", StdOut = "o", StdErr = "r", TraceId = "t", OutputTruncated = true,
        };
        t.ClearResult();
        Assert.AreEqual(Outcome.Failed, t.Outcome);
        Assert.IsTrue(new object?[] { t.DurationMs, t.ErrorMessage, t.ErrorType, t.StackTrace, t.StdOut, t.StdErr, t.TraceId, t.OutputTruncated }.All(v => v is null));
        Assert.IsTrue(new TestNode { Id = "2", ProjectId = "P", DisplayName = "U", StdErr = "x" }.LeanCopy().HasOutput);
    }

    [TestMethod]
    public void Events_serialize_with_a_type_discriminator_camel_case_and_without_nulls()
    {
        var node = new TestNode { Id = "1", ProjectId = "P", DisplayName = "T", Outcome = Outcome.TimedOut };
        var json = JsonNode.Parse(JsonSerializer.Serialize<ServerEvent>(new TestsUpdatedEvent([node]), Json.Options))!;
        Assert.AreEqual("testsUpdated", json["type"]!.GetValue<string>());
        Assert.AreEqual("timedOut", json["tests"]![0]!["outcome"]!.GetValue<string>());
        Assert.AreEqual("T", json["tests"]![0]!["displayName"]!.GetValue<string>());
        Assert.IsFalse(json["tests"]![0]!.AsObject().ContainsKey("errorMessage"));

        var rejected = JsonNode.Parse(JsonSerializer.Serialize<ServerEvent>(new CommandRejectedEvent("run", 404, "nope", ["x"]), Json.Options))!;
        Assert.AreEqual("""{"type":"commandRejected","command":"run","status":404,"error":"nope","unknownTestIds":["x"]}""", rejected.ToJsonString());

        var status = JsonNode.Parse(JsonSerializer.Serialize<ServerEvent>(new ProjectStatusEvent(new TestProject { Id = "P", Name = "P", Path = "/p" }), Json.Options))!;
        Assert.AreEqual("projectStatus", status["type"]!.GetValue<string>());
        Assert.AreEqual("idle", status["project"]!["status"]!.GetValue<string>());
        Assert.AreEqual("cold", status["project"]!["hostState"]!.GetValue<string>());

        foreach (var (evt, type) in new (ServerEvent, string)[]
        {
            (new RunStartedEvent(new RunSummary { RunId = "r", StartedAt = "s" }, ["1"]), "runStarted"),
            (new RunFinishedEvent(new RunSummary { RunId = "r", StartedAt = "s" }), "runFinished"),
            (new RunsUpdatedEvent([]), "runsUpdated"),
            (new PendingRunEvent(null), "pendingRun"),
            (new SettingsChangedEvent(new SettingsInfo { Watch = true }), "settingsChanged"),
            (new DebuggerWaitingEvent("P", 1, ["1"], "r", Explorer.DebugHints(1)), "debuggerWaiting"),
            (new DebuggerAttachedEvent("P", 1, "r"), "debuggerAttached"),
            (new LogEvent(null, "info", "m", "t"), "log"),
            (new CoverageUpdatedEvent(new CoverageReport { RunId = "r", GeneratedAt = "g", ProjectIds = [], Files = [] }), "coverageUpdated"),
        })
            Assert.AreEqual(type, JsonNode.Parse(JsonSerializer.Serialize(evt, Json.Options))!["type"]!.GetValue<string>());

        var cmd = JsonSerializer.Deserialize<ClientCommand>("""{"type":"setQuarantine","projectId":"P","id":"x","quarantined":false,"repeat":3,"targetFramework":"net8.0"}""", Json.Options)!;
        Assert.AreEqual(("setQuarantine", "P", "x", false, 3, "net8.0"), (cmd.Type, cmd.ProjectId, cmd.Id, cmd.Quarantined, cmd.Repeat, cmd.TargetFramework));
        Assert.IsTrue(DateTimeOffset.TryParse(Json.Now(), out _));
    }

    [TestMethod]
    public void A_full_state_snapshot_round_trips_through_json()
    {
        var state = new ExplorerState
        {
            Projects = [new TestProject { Id = "P", Name = "P", Path = "/p.csproj" }],
            Tests = [new TestNode { Id = "1", ProjectId = "P", DisplayName = "T" }],
            CurrentRun = new RunSummary { RunId = "r2", StartedAt = "s", Repeat = new RepeatSummary { Count = 3, Completed = 1,
                PerTest = [new RepeatTestResult { ProjectId = "P", Id = "1", Passed = 1, Failed = 1, Skipped = 1, DurationsMs = [1.5] }] } },
            LastRun = new RunSummary { RunId = "r1", StartedAt = "s", Watch = new WatchContext { ChangedFiles = ["a.cs"], BuildMs = 12, Strategy = "testFile" },
                Affected = new AffectedRun { Strategy = "testFile", ChangedFiles = ["a.cs"] } },
            Watch = true,
            AspireDashboardUrl = "https://localhost:17000",
            CoverageEnabled = true,
            Coverage = new CoverageReport { RunId = "r1", GeneratedAt = "g", ProjectIds = ["P"], Files =
                [new FileCoverage { Path = "/a.cs", Module = "M", Lines = [new CoverageLineHit { Line = 1, Hits = 2, BranchesCovered = 1, BranchesTotal = 2 }] }] },
            ImpactMap = new ImpactMapInfo { Files = 1, Tests = 2, BuiltAt = "b", Building = true },
            Logs = [new LogEvent("P", "info", "m", "t")],
            Runs = [],
            TracingEnabled = true,
            TracingAvailable = true,
            WorkspaceName = "ws",
            SourceRoots = ["/src"],
            Quarantined = ["P|1"],
            Editors = [new EditorInfo("vscode", "Visual Studio Code", true)],
            TelemetryAvailable = true,
            PendingRun = new PendingRunInfo { RunId = "r3", RequestedAt = "q", Scope = "subset", ProjectIds = ["P"], TestCount = 1, Coverage = false,
                Repeat = 2, Trigger = "user", Requests = 2 },
            Lean = true,
            UiDefaults = JsonDocument.Parse("""{"theme":"one"}""").RootElement,
        };
        var json = JsonSerializer.Serialize<ServerEvent>(new StateEvent(state), Json.Options);
        var back = ((StateEvent)JsonSerializer.Deserialize<ServerEvent>(json, Json.Options)!).State;

        Assert.AreEqual(json, JsonSerializer.Serialize<ServerEvent>(new StateEvent(back), Json.Options));
        Assert.AreEqual("one", back.UiDefaults!.Value.GetProperty("theme").GetString());
        Assert.AreEqual((3, 1, 1.5), (back.CurrentRun!.Repeat!.Count, back.CurrentRun.Repeat.Completed, back.CurrentRun.Repeat.PerTest[0].DurationsMs[0]));
        Assert.AreEqual(("testFile", 12.0), (back.LastRun!.Watch!.Strategy, back.LastRun.Watch.BuildMs));
        Assert.AreEqual((2, "user", 1, "subset"), (back.PendingRun!.Requests, back.PendingRun.Trigger, back.PendingRun.TestCount, back.PendingRun.Scope));
        Assert.AreEqual(2, back.Coverage!.Files[0].Lines[0].Hits);
        Assert.IsTrue(back.Watch && back.CoverageEnabled && back.TracingEnabled && back.TracingAvailable);
        Assert.AreEqual(state.PendingRun, back.PendingRun with { ProjectIds = state.PendingRun.ProjectIds });
        Assert.Contains("Watch = True", new SettingsInfo { Watch = true }.ToString());
        Assert.AreEqual(new ClientCommand { Type = "run", Coverage = true }, new ClientCommand { Type = "run", Coverage = true });
    }
}
