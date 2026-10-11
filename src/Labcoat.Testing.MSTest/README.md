# Labcoat.Testing.MSTest

MSTest helpers for [Labcoat](https://github.com/eriklieben/labcoat): give each MSTest integration test its own
OpenTelemetry trace, so Labcoat can show a **View trace** / **Structured logs** link that jumps straight to what the
services did during that test in the Aspire dashboard, and stream console output live while a test runs. Built on
[`Labcoat.Testing`](https://www.nuget.org/packages/Labcoat.Testing) (the framework-neutral core, brought in by this
package).

## Setup: two steps

1. **Add the package and the two hooks** (or derive the test classes from `LabcoatTracedTest` instead of the hooks):

   ```bash
   dotnet add MyApp.Tests package Labcoat.Testing.MSTest
   ```

   ```csharp
   using Labcoat.Testing;

   [TestClass]
   public static class LabcoatHooks
   {
       [GlobalTestInitialize] public static void Start(TestContext c) => LabcoatMSTest.StartTest(c);
       [GlobalTestCleanup]    public static void End(TestContext c)   => LabcoatMSTest.EndTest(c);
   }
   ```

2. **Call `LabcoatTracing.ConfigureAspireTesting(builder)` in the fixture that creates the app** (an
   `[AssemblyInitialize]` method, or the shared class the tests start the app from), after `CreateAsync` and before `BuildAsync`. Without it the services the tests start
   send their telemetry to the testing default (`localhost:4317`, usually nothing), and the dashboard shows the test's
   span but none of the services' spans or logs.

   ```csharp
   var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.MyAppHost>(ct);
   LabcoatTracing.ConfigureAspireTesting(builder);
   var app = await builder.BuildAsync(ct);
   ```

   A complete fixture:
   [AspireSample.Tests/Integration/AspireAppFixture.cs](https://github.com/eriklieben/labcoat/blob/main/samples/AspireSample/AspireSample.Tests/Integration/AspireAppFixture.cs).

Tracing is a no-op unless the tests run under Labcoat hosted by an Aspire AppHost (`builder.AddLabcoat(...)`) **with
trace collection turned on**. It is opt-in and off by default: enable it with `.WithTestTracing()` on the Labcoat
resource, the **Toggle test tracing** dashboard command or the toggle in the Labcoat UI. Safe to leave in place for
`dotnet test` and CI.

`StartTest` starts an `Activity` named after the test (exported to the dashboard under the test assembly's name, tagged
with the test's class, method and display name) and writes `labcoat:traceId=<hex>` to the test output; Labcoat strips
that line and links the test to `/traces/detail/<traceId>`. HTTP calls made during the test carry the W3C
`traceparent` header, so the service spans join the test's trace. `EndTest` tags the outcome (`test.outcome`; failed,
error, timeout and aborted tests get an error status).

## Trace only some tests

A trace filter in the Labcoat UI's search syntax (`cat:Integration`, `trait:Kind=Slow`, name text) limits tracing to
the matching tests. From the AppHost: `.WithTestTracing(filter: "cat:Integration")` (it wins). As the test project's
default:

```csharp
[assembly: LabcoatTracing(Filter = "cat:Integration")]   // or on a test class; or <LabcoatTracingFilter> in the .csproj
```

The adapter reads the test's `[TestCategory]` (method, class, base classes, assembly), `[TestProperty]`, `[Owner]`,
`[Priority]` and `[Description]`, the traits MSTest reports to Labcoat, so `cat:Integration` matches
`[TestCategory("Integration")]` and `trait:Kind=Slow` matches `[TestProperty("Kind", "Slow")]`. A test that doesn't
match gets no span and no trace id. The hooks still do the tracing; the attribute only narrows it. See
[Trace only some tests](https://github.com/eriklieben/labcoat/blob/main/docs/aspire.md#trace-only-some-tests).

## Live console output

MSTest attaches console output to a test's result only when the test ends. `LabcoatOutput` makes lines appear in
Labcoat's detail pane while the test is still running, and in the test's normal output afterwards:

```csharp
LabcoatOutput.WriteLine(TestContext, "Starting the Aspire app…");
using var log = LabcoatOutput.CreateWriter(TestContext); // a TextWriter (one live line per completed line)
```

Under Labcoat (`LABCOAT_TEST_HOST=1`) each line is also written to the process's raw stdout as a `labcoat:live:` marker
tagged with the test's class, method and display name, so Labcoat attributes it exactly, also for data rows and parallel
tests. Outside Labcoat it is just `Console.WriteLine`. No tracing setup is needed for this.

## Migrating from Labcoat.Testing 0.1 (MSTest)

The MSTest parts of `Labcoat.Testing` moved to this package; `Labcoat.Testing` is now framework neutral.

1. Replace the package: `Labcoat.Testing` → `Labcoat.Testing.MSTest` (it brings `Labcoat.Testing` along).
2. In your hooks, `LabcoatTracing.StartTest(c)` / `LabcoatTracing.EndTest(c)` become `LabcoatMSTest.StartTest(c)` /
   `LabcoatMSTest.EndTest(c)`.

Unchanged: the namespace (`using Labcoat.Testing;`), `LabcoatTracing.ConfigureAspireTesting`, `LabcoatTracing.IsEnabled`
and its constants, the `LabcoatTracedTest` base class and `LabcoatOutput`. The spans now also carry
`test.framework = mstest`.
