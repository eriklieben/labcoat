# Labcoat.Testing.TUnit

TUnit support for [Labcoat](https://github.com/eriklieben/labcoat): give each TUnit integration test its own
OpenTelemetry trace, so Labcoat can show a **View trace** / **Structured logs** link that jumps straight to what the
services did during that test in the Aspire dashboard. Built on
[`Labcoat.Testing`](https://www.nuget.org/packages/Labcoat.Testing) (the framework-neutral core, brought in by this
package). Requires TUnit 1.68 or later. AOT-compatible: works in Native AOT TUnit test apps.

## Setup: two steps

1. **Add the package** to the test project. That registers the tracing, no hooks to write: the package adds
   `[assembly: LabcoatTracing]` (a TUnit test start / end event receiver).

   ```bash
   dotnet add MyApp.Tests package Labcoat.Testing.TUnit
   ```

2. **Call `LabcoatTracing.ConfigureAspireTesting(builder)` in the fixture that creates the app** (for TUnit, the
   shared class the tests start the app from), after `CreateAsync` and before `BuildAsync`. The package can't do this
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
   [AspireSample.TUnitTests/AspireApp.cs](https://github.com/eriklieben/labcoat/blob/main/samples/AspireSample/AspireSample.TUnitTests/AspireApp.cs).

Everything is a no-op unless the tests run under Labcoat hosted by an Aspire AppHost (`builder.AddLabcoat(...)`)
**with trace collection turned on** (off by default: `.WithTestTracing()`, the **Toggle test tracing** dashboard
command or the toggle in the Labcoat UI). Safe to leave in place for `dotnet test` and CI.

## How it works

TUnit starts an `Activity` per test itself (`TestContext.Activity`, a new trace per test) and makes it current in the
test body, so HTTP calls the test makes carry its W3C `traceparent`. The adapter adopts that activity: it names it
after the test, tags it (`test.id` is the test node uid TUnit reports to Labcoat, i.e. `TestDetails.TestId`;
`test.class`, `test.name`, `test.outcome`, ...), writes `labcoat:traceId=<hex>` to the test's output (Labcoat strips
that line and links the test to `/traces/detail/<traceId>`) and exports the spans of that trace (TUnit's test spans
under the test assembly's name). TUnit's session, assembly and class spans are not exported.

## Options

- **Only some tests, by category or trait:** a trace filter in the Labcoat UI's search syntax (`cat:Integration`,
  `trait:Kind=Slow`, name text). From the AppHost: `.WithTestTracing(filter: "cat:Integration")` (it wins). As this
  project's default:

  ```xml
  <LabcoatTracingFilter>cat:Integration</LabcoatTracingFilter>
  ```

  The adapter passes the test's `[Category]` and `[Property]` as Labcoat shows them, so `cat:Integration` matches a test marked
  `[Category("Integration")]` and `trait:Kind=Slow` one marked `[Property("Kind", "Slow")]`. A test that doesn't match gets no span
  and no trace id. See [Trace only some tests](https://github.com/eriklieben/labcoat/blob/main/docs/aspire.md#trace-only-some-tests).
- **Only some classes:** set `<LabcoatTracingAutoRegister>false</LabcoatTracingAutoRegister>` in the test project and put
  `[LabcoatTracing]` (optionally with `Filter = "..."`) on the classes or methods to trace.
- **`GenerateAssemblyInfo` is false**, or the package is referenced through another project: the attribute is not
  added for you; write `[assembly: LabcoatTracing]` in the test project.

Live console output needs nothing extra with TUnit: Labcoat streams TUnit's own in-progress output.
