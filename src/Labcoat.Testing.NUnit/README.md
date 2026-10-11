# Labcoat.Testing.NUnit

NUnit support for [Labcoat](https://github.com/eriklieben/labcoat): give each NUnit integration test its own
OpenTelemetry trace, so Labcoat can show a **View trace** / **Structured logs** link that jumps straight to what the
services did during that test in the Aspire dashboard, and stream output live while a test runs. Built on
[`Labcoat.Testing`](https://www.nuget.org/packages/Labcoat.Testing) (the framework-neutral core, brought in by this
package). For NUnit 4.4 or later (NUnit 5 included) on Microsoft.Testing.Platform (NUnit3TestAdapter 6 or later,
`<EnableNUnitRunner>true</EnableNUnitRunner>`). The library is trim- and AOT-compatible (`IsAotCompatible`).

## Setup: two steps

1. **Add the package** to the test project. That registers the tracing, no hooks to write: the package adds
   `[assembly: LabcoatTracing]` (an NUnit action attribute that wraps every test).

   ```bash
   dotnet add MyApp.Tests package Labcoat.Testing.NUnit
   ```

2. **Call `LabcoatTracing.ConfigureAspireTesting(builder)` in the fixture that creates the app** (for NUnit, the
   `[OneTimeSetUp]` of a `[SetUpFixture]`), after `CreateAsync` and before `BuildAsync`. The package can't do this
   step for you: without it the services the tests start send their telemetry to the testing default
   (`localhost:4317`, usually nothing), and the dashboard shows the test's span but none of the services' spans or
   logs.

   ```csharp
   using Labcoat.Testing;

   var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.MyAppHost>(ct);
   LabcoatTracing.ConfigureAspireTesting(builder);
   var app = await builder.BuildAsync(ct);
   ```

   A complete fixture:
   [AspireSample.NUnitTests/AspireApp.cs](https://github.com/eriklieben/labcoat/blob/main/samples/AspireSample/AspireSample.NUnitTests/AspireApp.cs).

Everything is a no-op unless the tests run under Labcoat hosted by an Aspire AppHost (`builder.AddLabcoat(...)`)
**with trace collection turned on** (off by default: `.WithTestTracing()`, the **Toggle test tracing** dashboard
command or the toggle in the Labcoat UI). Safe to leave in place for `dotnet test` and CI.

## How it works

NUnit calls the attribute's `BeforeTest` right before each test's `[SetUp]` and `AfterTest` after its `[TearDown]`, on
the test's own thread. `BeforeTest` starts an `Activity` named after the test (a new trace per test, exported under the
test assembly's name) that is current in `[SetUp]`, the test body and `[TearDown]`, so HTTP calls the test makes carry
its W3C `traceparent` and the service spans join the test's trace. It writes `labcoat:traceId=<hex>` to the test's
output (`TestContext.Out`); Labcoat strips that line and links the test to `/traces/detail/<traceId>`. `AfterTest` tags
the outcome from NUnit's result (`test.outcome`, e.g. `Passed`, `Failed:Error`, `Skipped:Ignored`; failures, errors and
cancellations get an error status) and ends the span.

The span's `test.id` is the test node uid Labcoat shows for the row: NUnit3TestAdapter's runner uses the test's fully
qualified name (`ITest.FullName`, e.g. `MyApp.Tests.OrderTests.Create(2,"EUR")`), so data rows (`[TestCase]`,
`[TestCaseSource]`) and parameterized fixtures each get their own trace and id. With the adapter setting
`UseParentFQNForParametrizedTests` turned on, data rows share one uid in Labcoat; each row still gets its own trace.

## Live output

NUnit attaches a test's output to its result only when the test ends, and `TestContext.Progress` goes to the console
without saying which test wrote it (Labcoat then attributes it by timing). `LabcoatOutput` makes lines appear under the
exact test in Labcoat's detail pane while it is still running, and in the test's normal output afterwards:

```csharp
LabcoatOutput.WriteLine("Starting the Aspire app…");
using var log = LabcoatOutput.CreateWriter(); // a TextWriter (one live line per completed line)
```

Under Labcoat (`LABCOAT_TEST_HOST=1`) each line is also written to the process's raw stdout as a `labcoat:live:` marker
tagged with the test's id, class, method and name; outside Labcoat it is just `TestContext.Out`. No tracing setup is
needed for this.

## Options

- **Only some tests, by category or trait:** a trace filter in the Labcoat UI's search syntax (`cat:Integration`,
  `trait:Kind=Slow`, name text). From the AppHost: `.WithTestTracing(filter: "cat:Integration")` (it wins). As this
  project's default:

  ```xml
  <LabcoatTracingFilter>cat:Integration</LabcoatTracingFilter>
  ```

  The adapter passes the test's `[Category]` and `[Property]` of the test, its fixture and the assembly as Labcoat shows them, so `cat:Integration` matches a test marked
  `[Category("Integration")]` and `trait:Kind=Slow` one marked `[Property("Kind", "Slow")]`. A test that doesn't match gets no span
  and no trace id. See [Trace only some tests](https://github.com/eriklieben/labcoat/blob/main/docs/aspire.md#trace-only-some-tests).
- **Only some classes:** set `<LabcoatTracingAutoRegister>false</LabcoatTracingAutoRegister>` in the test project and put
  `[LabcoatTracing]` (optionally with `Filter = "..."`) on the fixtures or methods to trace.
- **`GenerateAssemblyInfo` is false**, or the package is referenced through another project: the attribute is not
  added for you; write `[assembly: LabcoatTracing]` in the test project.
