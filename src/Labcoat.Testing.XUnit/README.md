# Labcoat.Testing.XUnit

xUnit v3 support for [Labcoat](https://github.com/eriklieben/labcoat): give each xUnit integration test its own
OpenTelemetry trace, so Labcoat can show a **View trace** / **Structured logs** link that jumps straight to what the
services did during that test in the Aspire dashboard. Built on
[`Labcoat.Testing`](https://www.nuget.org/packages/Labcoat.Testing) (the framework-neutral core, brought in by this
package). For xUnit v3 3.2 or later on Microsoft.Testing.Platform (`xunit.v3.mtp-v2`).

## Setup: two steps

1. **Add the package** to the test project. That registers the tracing, no hooks to write: the package adds
   `[assembly: LabcoatTracing]` (an xUnit `BeforeAfterTestAttribute`).

   ```bash
   dotnet add MyApp.Tests package Labcoat.Testing.XUnit
   ```

2. **Call `LabcoatTracing.ConfigureAspireTesting(builder)` in the fixture that creates the app** (for xUnit v3, an
   assembly fixture's `InitializeAsync`), after `CreateAsync` and before `BuildAsync`. The package can't do this step
   for you: without it the services the tests start send their telemetry to the testing default (`localhost:4317`,
   usually nothing), and the dashboard shows the test's span but none of the services' spans or logs.

   ```csharp
   using Labcoat.Testing;

   var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.MyAppHost>(ct);
   LabcoatTracing.ConfigureAspireTesting(builder);
   var app = await builder.BuildAsync(ct);
   ```

   A complete fixture:
   [AspireSample.XUnitTests/AspireAppFixture.cs](https://github.com/eriklieben/labcoat/blob/main/samples/AspireSample/AspireSample.XUnitTests/AspireAppFixture.cs).

Everything is a no-op unless the tests run under Labcoat hosted by an Aspire AppHost (`builder.AddLabcoat(...)`)
**with trace collection turned on** (off by default: `.WithTestTracing()`, the **Toggle test tracing** dashboard
command or the toggle in the Labcoat UI). Safe to leave in place for `dotnet test` and CI.

## How it works

xUnit calls the attribute's `Before` after it created the test class and right before the test method, and `After`
right after the method, in the execution context the method runs in. `Before` starts an `Activity` named after the test
(a new trace per test, exported under the test assembly's name) that is current in the test method, so HTTP calls the
test makes carry its W3C `traceparent` and the service spans join the test's trace. It writes `labcoat:traceId=<hex>`
to the test's output (`ITestOutputHelper`; xUnit does not capture `Console`); Labcoat strips that line and links the
test to `/traces/detail/<traceId>`. `After` tags the outcome from xUnit's result (`test.outcome`: `Passed`,
`Failed:Assertion`, `Failed:Exception`, `Failed:Timeout`, `Skipped`; failures get an error status) and ends the span.
The test class's constructor and `IAsyncLifetime.InitializeAsync` run before `Before`, so they are not in the trace.

The span's `test.id` is the test node uid Labcoat shows for the row: xUnit's runner uses the test case's unique id
(`TestContext.Current.TestCase.UniqueID`), so `[InlineData]` rows and other theory data that xUnit can serialize each
get their own trace and id. Theory data xUnit cannot serialize (or `DisableDiscoveryEnumeration`) runs as one test
case with one row in Labcoat: each data row still gets its own trace, and the row links to the trace of the last one.

## Live output

Nothing to add: xUnit v3 reports test output live (`--show-live-output`, which Labcoat turns on), and Labcoat shows it
under the exact test while it runs.

## Options

- **Only some tests, by category or trait:** a trace filter in the Labcoat UI's search syntax (`cat:Integration`,
  `trait:Kind=Slow`, name text). From the AppHost: `.WithTestTracing(filter: "cat:Integration")` (it wins). As this
  project's default:

  ```xml
  <LabcoatTracingFilter>cat:Integration</LabcoatTracingFilter>
  ```

  The adapter passes the test's `[Trait]`s (a `Category` trait is a category) as Labcoat shows them, so `cat:Integration` matches a test marked
  `[Trait("Category", "Integration")]` and `trait:Kind=Slow` one marked `[Trait("Kind", "Slow")]`. A test that doesn't match gets no span
  and no trace id. See [Trace only some tests](https://github.com/eriklieben/labcoat/blob/main/docs/aspire.md#trace-only-some-tests).
- **Only some classes:** set `<LabcoatTracingAutoRegister>false</LabcoatTracingAutoRegister>` in the test project and put
  `[LabcoatTracing]` (optionally with `Filter = "..."`) on the classes or methods to trace.
- **`GenerateAssemblyInfo` is false**, or the package is referenced through another project: the attribute is not
  added for you; write `[assembly: LabcoatTracing]` in the test project.
