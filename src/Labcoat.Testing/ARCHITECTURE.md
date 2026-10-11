# Labcoat.Testing: architecture

Per-test traces for integration tests, split into a framework-neutral core and one thin adapter per test framework.

| Package | Depends on | Contains |
| --- | --- | --- |
| `Labcoat.Testing` | `Aspire.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` | `LabcoatTracing` (OTLP config from `LABCOAT_OTLP_*`, exporter, `BeginTest` / `EndTest`, `ConfigureAspireTesting`), `LabcoatTest`, `LabcoatTestOutcome`, `LabcoatTestSpan`, `LabcoatLiveOutput` |
| `Labcoat.Testing.MSTest` | core, `MSTest.TestFramework` | `LabcoatMSTest` (hooks), `LabcoatTracedTest` (base class), `LabcoatOutput` (live output with a `TestContext`) |
| `Labcoat.Testing.TUnit` | core, `TUnit.Core` | `LabcoatTracingAttribute` (start / end event receiver), `build/Labcoat.Testing.TUnit.targets` (adds `[assembly: LabcoatTracing]`) |
| `Labcoat.Testing.NUnit` | core, `NUnit` (4.4+) | `LabcoatTracingAttribute` (`ITestAction` on every test), `LabcoatOutput` (live output for the current test), `build/Labcoat.Testing.NUnit.targets` |
| `Labcoat.Testing.XUnit` | core, `xunit.v3.extensibility.core` (3.2+) | `LabcoatTracingAttribute` (`BeforeAfterTestAttribute`), `build/Labcoat.Testing.XUnit.targets` |

All types live in the `Labcoat.Testing` namespace, so one `using` covers the core and the adapter. The self-registering
adapters (TUnit, NUnit, xUnit) all name their attribute `LabcoatTracingAttribute`, so the build targets and the docs are
the same for each (`[assembly: LabcoatTracing]`, opt out with `LabcoatTracingAutoRegister=false`); a test project
references one adapter.

## How a test is linked to its trace

1. Labcoat (hosted by an AppHost, tracing on) starts the test host with `LABCOAT_OTLP_ENDPOINT` / `_PROTOCOL` /
   `_HEADERS` (src/Labcoat/Telemetry.cs). Without them every call here is a no-op.
2. The adapter calls `LabcoatTracing.BeginTest(test, writeOutput, frameworkActivity)` when the test starts, **in the
   test's execution context**, so that the span is `Activity.Current` in the test body. HttpClient then sends its W3C
   `traceparent`; the services (`ConfigureAspireTesting` points them at the same dashboard) put their spans in the
   test's trace.
3. `BeginTest` writes `labcoat:traceId=<32 hex>` through `writeOutput` into the **test's own output**. The framework
   attaches that output to the test's result (MTP `StandardOutputProperty`), Labcoat strips the line and sets the
   row's `traceId` (`Telemetry.ExtractTraceId`). The output already belongs to exactly one test node, so the link is
   exact for data rows and parallel tests without any id matching.
4. `LabcoatTest.Id` is the MTP test node uid (the row id in Labcoat) where the framework exposes it. It is recorded as
   the `test.id` span tag (and `i` in live output markers) for correlation from the dashboard side; nothing depends on
   it yet. TUnit: `TestDetails.TestId` (equals the uid; TUnit also tags `tunit.test.node_uid` with it). NUnit:
   `ITest.FullName` (NUnit3TestAdapter's MTP runner sets `UseFullyQualifiedNameAsTestNodeUid`, and the fully qualified
   name of a test case is NUnit's full name unless the adapter setting `UseParentFQNForParametrizedTests` is on, which
   gives the data rows of a method one shared uid). xUnit v3: `TestCase.UniqueID` (xunit.v3.mtp-v2 sends the test case's
   unique id as the uid; theory data xUnit can't serialize runs several tests under one test case, so those rows share
   one uid and one row in Labcoat). MSTest 4.0: not exposed, so null.
5. `EndTest(span, outcome, frameworkOutcome)` tags `test.outcome` and sets the span status (Failed / Error / TimedOut
   / Cancelled = Error, Passed = Ok) and stops the span if Labcoat started it.

### Own span or adopted span

- **Own span** (MSTest, NUnit, xUnit; the default): `BeginTest` clears `Activity.Current` and starts a root span from
  the `Labcoat.Testing` source. Works when the framework runs the start hook, the test and the end hook in one flowing
  `ExecutionContext`: MSTest's `[GlobalTestInitialize]` / `[TestInitialize]` do; NUnit runs `ITestAction.BeforeTest`,
  `[SetUp]`, the test, `[TearDown]` and `AfterTest` synchronously on the test's thread (async tests are awaited there);
  xUnit v3 runs `BeforeAfterTestAttribute.Before`, the test method and `After` in one async method under the execution
  context captured when it created the test class. Neither NUnit nor xUnit v3 starts an `Activity` per test.
- **Adopted span** (TUnit): TUnit runs event receivers in an `ExecutionContext` that does not flow into the test body
  (AsyncLocal changes are lost, `AddAsyncLocalValues` included), but it starts a root `Activity` per test itself
  (`TestContext.Activity`, source `TUnit`, operation "test case"; TUnit 1.73 adds a "test body" child) and makes it
  current in the body. The adapter passes it as `frameworkActivity`: the core renames it to the test's display name,
  tags it, uses its trace id and never stops it. A framework activity that is not a root is not adopted (it would put
  many tests in one trace); the core starts its own span then.
- `LabcoatTracing.Initialize("TUnit")` (called from the attribute's constructor, during discovery) builds the exporter
  before the first test starts, listening to the framework source as well, so TUnit samples its activities even with
  no other listener. The exporter's `TestTraceFilter` (a `CompositeProcessor`, so the batch processor still gets the
  provider's resource) passes Labcoat's own spans and spans whose trace was registered by `BeginTest`; TUnit's session,
  assembly and class spans are dropped.

## Verified

- TUnit 1.68.17 and 1.73.19: the body runs in the adopted trace; the uid Labcoat shows for each row (MTP server mode,
  including data rows) equals `TestDetails.TestId` and the row's `traceId` equals the body's trace id; only test traces
  are exported (checked with Labcoat on a scratch project and an OTLP sink).
- samples/AspireSample: MSTest (`AspireSample.PassingTests`) and TUnit (`AspireSample.TUnitTests`) traces each hold the
  test span and the `api` request span.
- tests/Labcoat.Testing.TUnit.Tests runs on TUnit with the adapter registered by its build targets.
- NUnit 4.4.0 and 5.0.0 (NUnit3TestAdapter 6.3.0) and xUnit v3 3.2.2 and 4.0.1 (xunit.v3.mtp-v2), with Labcoat in MTP
  server mode on scratch projects (plain tests, `[TestCase]` rows with numbers and quoted strings, `[TestCaseSource]`,
  generic parameterized fixtures; `[InlineData]` rows and non-serializable theory data): the uid of every row equals
  the adapter's `test.id`, the row's `traceId` equals `Activity.Current`'s trace id in the body (the adapter's own span),
  and the exported spans carry the mapped outcomes (`Failed:Error`, `Skipped:Ignored`, `Inconclusive`, `Warning`,
  `Failed:Assertion`, `Failed:Exception`, `Skipped`). The xUnit adapter reads only the `Xunit.Sdk.ITest` /
  `ITestCaseMetadata` contract of xunit.v3.common: `IXunitTestCase.TestClassName` moved to `ICoreTestCase` in 4.0, so
  an adapter built against 3.2 that calls it fails with `MissingMethodException` on 4.x.
- samples/AspireSample: `AspireSample.NUnitTests` and `AspireSample.XUnitTests` traces each hold the test span and the
  `api` request span (OTLP sink), and each row's id equals the span's `test.id`.
- tests/Labcoat.Testing.NUnit.Tests (NUnit 5) and tests/Labcoat.Testing.XUnit.Tests (xUnit v3 4.0) run on their own
  framework with the adapter registered by its build targets.

## The NUnit and xUnit v3 adapters

- **NUnit** (`Labcoat.Testing.NUnit`): `LabcoatTracingAttribute : ITestAction`, `Targets => ActionTargets.Test`,
  applied as `[assembly: LabcoatTracing]`. `BeforeTest(ITest)` → `BeginTest(test, TestContext.Out.WriteLine)` (the
  test's own output, attached to the result as its standard output); the span is kept per `ITest` (a concurrent
  dictionary: tests may run in parallel), and a second `BeforeTest` for the same test ([Retry]) ends the previous one
  as `Retried`. `AfterTest` maps `TestContext.CurrentContext.Result.Outcome`: Passed → Passed; Failed with label
  `Error` → Error, `Cancelled` → Cancelled, `Invalid` → Skipped, other → Failed; Skipped and Warning → Skipped
  (NUnit3TestAdapter reports a warning as skipped by default); Inconclusive → Inconclusive; `test.outcome` is NUnit's
  `ResultState` (`Failed:Error`, `Skipped:Ignored`, ...). `LabcoatTest.ClassName` is the fixture part of the full
  name (`Ns.Fixture<Int32>(1)` for a parameterized fixture), as Labcoat shows it, so live markers match the row.
  NUnit aborts (`AbortRequested`) skip `AfterTest`; such a span is never ended and so not exported.
  Live output: NUnit attaches `TestContext.Out` to the result only at the end and `TestContext.Progress` goes to the
  host's console unattributed, so the adapter has `LabcoatOutput` (`WriteLine`, `WriteErrorLine`, `CreateWriter`) for
  the current test, like MSTest's.
- **xUnit v3** (`Labcoat.Testing.XUnit`): `LabcoatTracingAttribute : Xunit.v3.BeforeAfterTestAttribute`, applied as
  `[assembly: LabcoatTracing]` (`ITestPipelineStartup` is per assembly, not per test). `Before(MethodInfo,
  IXunitTest)` → `BeginTest(test, TestContext.Current.TestOutputHelper.WriteLine)` (xUnit does not capture `Console`);
  spans are kept per test unique id. `After` runs after xUnit recorded the test's result in
  `TestContext.Current.TestState` (only a failing `Dispose` changes it later): Passed → Passed; Failed by an assertion
  → Failed, by another exception → Error, by a timeout → TimedOut; Skipped (`Assert.Skip`) and NotRun → Skipped;
  `test.outcome` is `Passed` / `Failed:<cause>` / `Skipped`. Statically skipped tests run no hooks. Live output needs
  nothing: Labcoat passes `--show-live-output on` and parses xUnit's `OUTPUT: [<display name>]` lines.

## Adding a framework

An adapter is a small package `Labcoat.Testing.<Framework>` that references the core and the framework, maps the
framework's test identity to `LabcoatTest` and its outcome to `LabcoatTestOutcome`, and calls `BeginTest` / `EndTest`.
Prefer a hook that registers itself (an assembly attribute added by `build/<PackageId>.targets`, as for TUnit). Things
to verify for each: that `Activity.Current` set in the start hook is current in the test body (else adopt a framework
activity), that `writeOutput` lands in the result's standard output, and what the MTP uid is.
