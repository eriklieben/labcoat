# Labcoat host internals and the MTP protocol

Reference for contributors: how the Labcoat host (`src/Labcoat`, an ASP.NET Core minimal API) builds test projects,
drives them through Microsoft.Testing.Platform's JSON-RPC **server mode** and streams results to the browser UI
(served from `wwwroot`, built from `ui/`). The browser <-> host API is in [contract.md](contract.md); the user-facing
documentation is the [README](../README.md) and the [tool's package README](../src/Labcoat/README.md).

## Command line

```bash
dnx Labcoat                    # no install (.NET 10 SDK); options go after the package id, --help/--version after --
dotnet tool install -g Labcoat # global tool: `labcoat`
dotnet tool install Labcoat    # local tool (with a tool manifest): `dotnet labcoat`

labcoat [paths...] [--port 4300] [--no-open] [--watch] [--coverage] [--traces] [--root <dir>]
labcoat --help | --version

# from source
dotnet run --project src/Labcoat -- samples/SampleTests --no-open
```

| Option | Meaning |
| --- | --- |
| `paths` | `.csproj`/`.fsproj`/`.vbproj`, `.sln`/`.slnx`/`.slnf`, or directories. Default: the current directory when it contains a test project, else the nearest `.slnx` (preferred) / `.sln` walking up from it, stopping at the repository root (`.git`) or the home directory, else the current directory. |
| `--port`, `-p` | HTTP port. Precedence: `--port` > `$PORT` > `$ASPNETCORE_URLS` (left as is) > `4300` (`$LABCOAT_DEFAULT_PORT`). Only the default port falls back to the next free one (up to 20 tries, reported in the banner); an explicit port that is busy is an error. |
| `--no-open` | Don't open the browser. It is also not opened when `ASPIRE_DASHBOARD_URL` or `CI` is set. |
| `--watch`, `-w` | Start in watch mode. |
| `--coverage` | Start with code coverage collection enabled. |
| `--traces` | Start with per-test trace collection enabled (opt-in, off by default; only has an effect when hosted by an Aspire AppHost). Lets you jump from a failing integration test to the service traces/logs it caused in the Aspire dashboard. A choice made in the UI is remembered per workspace and wins. |
| `--root <dir>` | Extra directory whose files `/api/source` may serve (e.g. the solution root, for stack-trace frames outside the test projects). Repeatable. |
| `--version` | Print the version. |
| `--help`, `-h` | Usage. |

**Startup:** a banner with the version, URL (and the port fallback, if any), the workspace and the test projects found,
then the browser is opened: `$BROWSER` when set, else `start` (Windows) / `open` (macOS) / `xdg-open` (Linux; `wslview`
on WSL), started through `setsid` when available so the browser doesn't get the terminal's Ctrl+C. Without a display
(no `DISPLAY`/`WAYLAND_DISPLAY`, e.g. over SSH) or when the opener fails, Labcoat prints the URL (and an `ssh -L` hint)
instead; it never fails because of the browser. `NO_COLOR` or redirected output disables colors.

**Shutdown:** Ctrl+C / SIGTERM sends `exit` to the warm test hosts and waits for them (see Behaviour); exit code 0.

**Packaging:** `dotnet pack src/Labcoat` fails when the UI isn't built (`wwwroot/index.html` missing) and packs the UI
once, under `tools/net10.0/any/wwwroot/` (static web assets are disabled: no `staticwebassets/`, `build/` or `.br/.gz`
copies; the publish directory is emptied first so stale hashed assets never ship).

Environment: `ASPIRE_DASHBOARD_URL` is surfaced as `state.aspireDashboardUrl` (for hosting under Aspire). Every test
host Labcoat starts gets `LABCOAT_TEST_HOST=1` (besides `--server`), so `Labcoat.TestingPlatform`'s `--labcoat` launcher
stays inert in it.
To host the explorer in an Aspire AppHost, use the `Labcoat.Aspire.Hosting` integration (`src/Labcoat.Aspire.Hosting`).

### Test project discovery

- An explicitly passed project file is always used. A missing path, an unsupported file or a solution that can't be
  read (malformed `.slnx`/`.sln`/`.slnf`) is an error: Labcoat prints it and exits with code 1.
- From a directory scan (recursive, skipping `bin`, `obj`, `node_modules`, dot-folders) or a solution, a project is a
  test project when
  1. its project file text says so (fast path): `Sdk="MSTest.Sdk…"`, a `PackageReference` to `MSTest`,
     `Microsoft.Testing.Platform(.*)`, `xunit.v3(.*)` (incl. `xunit.v3.mtp-v2`) or `TUnit(.*)`, or one of
     `EnableMSTestRunner`, `EnableNUnitRunner`, `UseMicrosoftTestingPlatformRunner`, `IsTestProject`,
     `IsTestingPlatformApplication`, `TestingPlatformDotnetTestSupport` set to `true`; else
  2. MSBuild evaluation says so: for the remaining candidates (name contains "test"/"spec", or a
     `Directory.Build.props`/`.targets`/`Directory.Packages.props` above it mentions a test framework) Labcoat runs one
     `dotnet msbuild -getProperty:IsTestingPlatformApplication … -getItem:PackageReference` each, in parallel, and
     caches the answer in `<data dir>/project-detection.json` keyed by the timestamps of the project file, those
     `Directory.*` files and `obj/project.assets.json`. This sees settings from `Directory.Build.props`, central
     package management and package props (`IsTestingPlatformApplication` comes from the MTP MSBuild package once the
     project is restored).

### Behaviour

- **Build**: `dotnet build <proj> -nologo -tl:off -v:minimal`. On failure the project status becomes `error`
  with `lastError` (first `error` line) and `buildOutput` (last 60 lines). Before a **run**, the build is skipped when
  no `*.cs/*.csproj/*.props/*.targets/*.json/...` file in the project directory or its direct `ProjectReference`
  directories changed since the last successful build (`discover` always builds).
- **Output path**: `dotnet msbuild <proj> -getProperty:TargetPath -getProperty:TargetFrameworks -getProperty:TargetFramework -getItem:ProjectReference`.
  Multi-targeted projects run on the highest .NET TFM by default (`net10.0` over `net8.0`, plain over
  `-windows`), or the one chosen with `setTargetFramework` (persisted per workspace); later builds use `-f <tfm>`.
  `TestProject.targetFramework` / `availableFrameworks` show it.
- **Discovery** runs after every build that changed the output assembly. Previous results are kept for tests whose
  uid still exists.
- **Runs** are serialized (one operation at a time). At most one run request waits; further requests merge into it
  (see "Round 4" in `docs/contract.md`). Projects in a run execute sequentially. Requested tests become `queued`,
  `running` on `in-progress`, then their final outcome. Tests that never reported get `cancelled` on cancel, or their
  previous outcome back (e.g. when the build failed). When the test host exits mid-run the test(s) in progress fail
  with "Test host exited with code N", the rest become `notRun`, and the run summary gets `aborted`.
- **Warm test host**: one long-lived test host process per project (env `LABCOAT_WARM_HOST=1`); discovery and every
  non-coverage run are further requests on the same MTP connection (`hostState` cold/starting/warm). It is replaced
  when the content of the output directory changed (dll/json content hashes, so a no-op rebuild keeps it), when it
  died, or on `restartHost`. On Windows it is stopped before a build (loaded assemblies are locked). Coverage runs use a
  one-off process. Shutdown (Ctrl+C/SIGTERM) sends `exit` to warm hosts and waits up to 30 s for fixtures to dispose.
- **Cancel**: `$/cancelRequest` for the in-flight request; a warm host gets 10 s (`LABCOAT_CANCEL_GRACE_SECONDS`) to
  answer before its process tree is killed (logged with the reason), one-off processes 3 s. MSTest finishes the test in
  progress unless it observes `TestContext.CancellationToken`, so a warm host usually survives a cancel. Cancel also
  clears the queued run request.
- **Watch**: `FileSystemWatcher` on the project directory and its transitive `ProjectReference` closure
  (ignoring `bin`/`obj`), debounced 500 ms, then rebuild + rediscover + re-run the **affected** tests: tests located in a
  changed test file; for product files the tests the impact map says execute the changed methods; else all tests of the
  project. The impact map comes from per-test/subset coverage runs and the `buildImpactMap` pass (one coverage run per
  test class). See "Smart watch" in `docs/contract.md`.
- **Coverage** (`setCoverage`/`POST /api/coverage`): run operations add
  `--coverage --coverage-output-format cobertura --coverage-output <tmp>/labcoat/coverage/<project>-<runId>.cobertura.xml`.
  Support is detected by looking for `--coverage-output-format` in `dotnet <TargetPath> --help` (cached per build).
  The test assemblies themselves are excluded; reports of several projects in one run are merged.
- **`GET /api/source?path=`** serves a file only when it is a test location file, a file in the current coverage
  report, or lives under a discovered project directory (or its `ProjectReference` dirs) or the common root of the
  CLI input paths. Paths are canonicalized (`GetFullPath` + symlinks resolved). 403 outside, 404 missing,
  413 over 2 MB, 415 for binary files.

## MTP server mode: what we observed

Not officially documented; verified against Microsoft.Testing.Platform 2.0.2 / MSTest 4.0.2 (server reports
`serverInfo: { name: "test-anywhere", version: "2.0.2" }`). References: `microsoft/testfx`
`src/Platform/Microsoft.Testing.Platform/ServerMode/JsonRpc/{JsonRpcMethods,SerializerUtilities}.cs` and
`samples/Playground/ServerMode`.

### Transport

1. The **client** listens on a TCP port; launch `dotnet <TargetPath.dll> --server --client-host localhost --client-port <port>`
   (cwd = output dir). The test app connects to us (it resolves `localhost` to `127.0.0.1`, so listen on IPv4 loopback).
2. JSON-RPC 2.0, each message framed as `Content-Length: N\r\n\r\n<utf8 json>`. Messages from the server also carry
   `Content-Type: application/testingplatform`. Same framing in both directions.
3. **Multiple connections**: with an out-of-process extension active (e.g. `--coverage`), the launched process is a
   *test host controller*. It connects first (its `initialize` result has `supportsDiscovery: false`), then spawns the
   real test host, which opens a **second connection to the same port**. The client must `initialize` and send the
   request again on that second connection; test updates and the final result (with attachments) arrive there, and
   the controller never answers the run request. Accept connections in a loop and run the operation on each.

### Requests (client → server)

```jsonc
// initialize
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{
  "processId":1234,"clientInfo":{"name":"labcoat","version":"0.1.0"},
  "capabilities":{"testing":{"debuggerProvider":false,"attachmentsSupport":true}}}}
// -> result
{"processId":5678,"serverInfo":{"name":"test-anywhere","version":"2.0.2"},
 "capabilities":{"testing":{"supportsDiscovery":true,"experimental_multiRequestSupport":false,
   "vstestProvider":false,"attachmentsSupport":true,"multipleConnectionProvider":false}}}

// discover
{"jsonrpc":"2.0","id":2,"method":"testing/discoverTests","params":{"runId":"<guid>"}}
// -> result {}   (after all testUpdates notifications)

// run all / run a subset. Minimal node objects ({uid, display-name}) are enough for the subset,
// including individual DataRow cases (each has its own uid).
{"jsonrpc":"2.0","id":2,"method":"testing/runTests","params":{"runId":"<guid>",
  "tests":[{"uid":"140e76d8-e4c3-8ee8-b15e-4028bd1fd9bb","display-name":"Multiply_ReturnsProduct (3,3,10)"}]}}
// -> result {}   or, with coverage: {"attachments":[{"uri":"/abs/path/file.cobertura.xml","producer":"TestingPlatformCoverageDynamicTestSessionLifetime…", ...}]}

// cancel (notification) -> the pending request then fails with {"code":-32800,"message":""}
{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":2}}
// shut down (notification)
{"jsonrpc":"2.0","method":"exit","params":{}}
```

Requests from the server to the client (e.g. `client/attachDebugger`) are answered with error `-32601`.

### Notifications (server → client)

```jsonc
{"method":"client/log","params":{"level":"Information|Debug|Trace|Warning|Error","message":"..."}}
{"method":"telemetry/update","params":{"eventName":"dotnet/testingplatform/execution/testsrun","metrics":{...}}}
{"method":"testing/testUpdates/tests","params":{"runId":"<guid>","changes":[{"node":{...},"parent":null}, ...]}}
{"method":"testing/testUpdates/tests","params":{"runId":"<guid>","changes":null}}   // end of updates
```

A `node` as observed (MSTest 4.0.2). Discovery sends `execution-state: "discovered"`; a run sends `in-progress` and
then a final state, sometimes both for the same test inside one batch:

```jsonc
{
  "uid": "140e76d8-e4c3-8ee8-b15e-4028bd1fd9bb",           // stable across processes/builds
  "display-name": "Multiply_ReturnsProduct (3,3,10)",
  "node-type": "action",                                    // test case
  "execution-state": "failed",                              // discovered|in-progress|passed|failed|skipped|timed-out|error|cancelled
  "location.file": "/abs/path/Math/CalculatorTests.cs",
  "location.line-start": 12,                                // line of the [TestMethod] attribute (CallerLineNumber); line-end == line-start in MSTest 4
  "location.line-end": 12,
  "location.type": "SampleTests.Math.CalculatorTests",      // namespace + class (nested: Outer+Inner)
  "location.method": "Multiply_ReturnsProduct(System.Int32,System.Int32,System.Int32)",  // includes parameter types for parameterized tests
  "location.method-arity": 0,
  "traits": [{"Integration": ""}],                          // MSTest [TestCategory("Integration")] -> key=category, value ""
  "time.duration-ms": 103.9932,                             // final states only
  "error.message": "Assert.AreEqual failed. Expected:<10>. Actual:<9>. ...",   // failed; for skipped = the [Ignore] reason
  "error.stacktrace": "   at ...",
  "assert.actual": "", "assert.expected": "",
  "standardOutput": "Order 42 persisted\n\n\r\n\r\nTestContext Messages:\r\nConnecting to the order store...\n",
  "standardError": "..."
}
```

No `location.namespace` and no exception type were observed. Mapping to the contract's `TestNode`:
`namespace`/`className` = split of `location.type` at the last `.`; `methodName` = `location.method` up to `(`;
`line`/`lineEnd` = `location.line-start`/`line-end`; traits `{K: ""}` → `{key: "Category", value: K}`, `{K: V}` →
`{key: K, value: V}`; `error`/`failed` → `failed`, `timed-out` → `timedOut`; `errorType` is a best-effort guess
(from "threw exception: X" in the message, or Assert frames in the stack trace); `\r\n` in output is normalized to `\n`.

### Several requests per session (warm host)

`initialize` reports `experimental_multiRequestSupport: false`, but **sequential** requests on one connection work:
`discoverTests` followed by any number of `runTests` (subsets or all) each complete normally. Observed with MSTest 4.0.2:

- `[AssemblyInitialize]`/`[ClassInitialize]` run again for **every** `runTests` request, and `[ClassCleanup]`/
  `[AssemblyCleanup]` run at the end of every request (their console output is attributed to the last test).
- Static state survives between requests (same process / AppDomain).
- So a fixture that starts something expensive must be idempotent (start once, keep it in a static) and must not
  dispose it in `[AssemblyCleanup]` when it should survive; dispose on `AppDomain.ProcessExit` instead (it runs after
  `exit`). See `samples/AspireSample` ("warm mode friendly fixture").
- `exit` with an idle connection ends the process with code 0 (ProcessExit handlers run). If the client connection
  drops, the test host exits immediately (code 3), so a crashed explorer does not leave test hosts behind.

### Coverage

With `--coverage --coverage-output-format cobertura --coverage-output <abs path>` (Microsoft.Testing.Extensions.CodeCoverage,
shipped with the MSTest metapackage) the controller/child-host pattern above is used. The Cobertura file is reported
in the run result's `attachments` and is complete once the processes have exited. The coverage tool already leaves the
test assembly out by default (a project with only test code yields `<packages />`).

### Live console output (research, 2026-10)

Question: can Labcoat show a test's console output **while it runs**? Verified empirically with a scratch project per
framework (a test that writes a line per second for 5 s, dumped with a raw MTP server-mode client that timestamps every
notification and the process's own stdout/stderr), plus the sources (`microsoft/testfx` v4.0.2 / v4.5.1,
`xunit/xunit` v3-3.2.2, `thomhurst/TUnit` v1.73.19, `nunit/nunit3-vs-adapter`).

**MTP server mode.** The node serializer writes `standardOutput` / `standardError` for **any** execution state
(`SerializerUtilities`), but frameworks only attach output to the **final** node; the `in-progress` node is bare.
`testing/testUpdates/tests` are **batched every 200 ms** (`PerRequestServerDataConsumerService.TestNodeUpdateDelayInMs`),
so Labcoat learns about a test start / end 0–200 ms late. `IOutputDevice.DisplayAsync` text is forwarded as
`client/log` (Information / Warning / Error) **without a test uid**, and the same text is also printed on the process's
stdout. No public MTP extension point sees a test's output before it ends (an `IDataConsumer` of
`TestNodeUpdateMessage` sees exactly what the framework publishes; the output device cannot be wrapped).

| Framework (version tested) | Captured per test, in the final result | Live, without changes | Live, exact |
| --- | --- | --- | --- |
| MSTest 4.0.2 (MTP 2.0.2) | `Console.Out/Error`, `TestContext.WriteLine`, `Trace` | `TestContext.DisplayMessage` (stdout + `client/log`; **not** in the result); writes from code without the test's ExecutionContext (`ThreadPool.UnsafeQueueUserWorkItem`, `ExecutionContext.SuppressFlow`), child processes | Labcoat.Testing `LabcoatOutput` (below) |
| MSTest ≥ 4.4 (4.5.1 tested) | same | `"mstest": { "output": { "captureTrace": "Live" } }` in testconfig.json echoes captured output to the raw stdout while still capturing it. Not set by Labcoat: the env-var form (`mstest__output__captureTrace=Live`, MTP's env configuration source) had no effect in 4.5.1, and 4.0–4.3 treat the key as a boolean (warning). | `LabcoatOutput` |
| xUnit v3 (xunit.v3.mtp-v2 4.0.1, MTP 2.4) | `ITestOutputHelper` only (console only with `[assembly: CaptureConsole]`) | `Console.*` goes straight to the process stdout/stderr | `--show-live-output on`: `OUTPUT: [<display name>] <text>` on stdout (+ `client/log`), per helper line |
| NUnit (NUnit3TestAdapter 6.3, MTP 2.3) | `Console.Out`, `TestContext.Out` (+ echoed on stdout when the test ends) | `TestContext.Progress` / `TestContext.Error` / `Console.Error` (stdout + `client/log`, no uid) | — |
| TUnit 1.73.19 (MTP 2.5.1) | `Console.*`, `TestContext.Current.OutputWriter` | `Console.*` is also teed to the process stdout/stderr | `TUNIT_ENABLE_IDE_STREAMING=1`: in-progress nodes with the **cumulative** output so far, about once a second (then an empty in-progress "heartbeat"), tagged with the uid — exact also for parallel tests |

**What Labcoat does** (`src/Labcoat/LiveOutput.cs`, event `testOutput` in `docs/contract.md` "Live console output"):

1. **Zero config, exact:** TUnit test hosts get `TUNIT_ENABLE_IDE_STREAMING=1` (TUnit.Engine.dll in the output folder);
   in-progress nodes with output are diffed against what was already streamed. xUnit v3 hosts get
   `--show-live-output on` when an `xunit.v3*.dll` in the output folder contains the option (UTF-16 string scan, cached
   per folder; older runners without it are left alone, an unknown option would fail the launch); `OUTPUT: [name]`
   lines are matched to the running test by display name.
2. **Zero config, best effort:** every other line of the test host's own stdout/stderr (the pipes Labcoat already
   reads) is stream `"host"`, attributed **200 ms after it arrived** (to line up with MTP's 200 ms batches) to the single
   running test of that project, or to every running test with `shared: true` when several run; lines while no test
   runs and known platform noise ("Connecting to client host", "Test Parallelization enabled", NUnit adapter banners)
   are dropped. A line written within ~200 ms of a test boundary can land on the neighbouring test. Not used for TUnit
   projects (their tee would duplicate the exact stream).
3. **Opt-in, exact (MSTest):** `LabcoatOutput.WriteLine(TestContext, text)` (package `Labcoat.Testing.MSTest`, on the
   framework-neutral `LabcoatLiveOutput` of `Labcoat.Testing`) writes the line to
   `Console.Out` (captured as usual) and, under Labcoat (`LABCOAT_TEST_HOST=1`), a side-channel line
   `labcoat:live:<base64 UTF-8 JSON {s: "out"|"err", t, c: class, m: method, n: display name, i?: test node uid}>` to the **raw** stdout
   (`Console.OpenStandardOutput()`, which MSTest does not redirect). Labcoat matches it to the running test by class +
   method + display name (so data rows and parallel tests are exact), strips it from the process output, and never shows
   it as host output.

Rejected: (c) an in-process MTP extension (no hook sees output before the result; wrapping `Console.Out` would fight the
frameworks' own routers), a socket side channel (the raw stdout pipe already exists and needs no port/env plumbing),
`client/log` (no uid; same text as stdout). The final result's `standardOutput`/`standardError` stays authoritative:
streamed lines that are in it are dropped by the UI after the test finished; the rest ("live-only" lines, e.g. xUnit
console output or MSTest `DisplayMessage`) stay visible for that browser session. `LABCOAT_LIVE_OUTPUT=0` turns the
feature off (no extra test host arguments / variables, no events).
