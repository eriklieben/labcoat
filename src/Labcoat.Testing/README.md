# Labcoat.Testing

The framework-neutral core of [Labcoat](https://github.com/eriklieben/labcoat)'s test-side helpers: per-test
OpenTelemetry traces for Aspire integration tests, so Labcoat can show a **View trace** / **Structured logs** link that
jumps straight to what the services did during that test in the Aspire dashboard.

## Setup: two steps

1. **Add the adapter package for your test framework** (it brings this one; you normally don't reference this package
   yourself):

   | Test framework | Package | Setup |
   | --- | --- | --- |
   | MSTest | [`Labcoat.Testing.MSTest`](https://www.nuget.org/packages/Labcoat.Testing.MSTest) | Two hooks (`LabcoatMSTest.StartTest` / `EndTest`) or a base class. Also live console output (`LabcoatOutput`). |
   | TUnit | [`Labcoat.Testing.TUnit`](https://www.nuget.org/packages/Labcoat.Testing.TUnit) | None: the package registers itself. |
   | NUnit | [`Labcoat.Testing.NUnit`](https://www.nuget.org/packages/Labcoat.Testing.NUnit) | None: the package registers itself. Also live console output (`LabcoatOutput`). |
   | xUnit v3 | [`Labcoat.Testing.XUnit`](https://www.nuget.org/packages/Labcoat.Testing.XUnit) | None: the package registers itself. |
   | Other | this package | Call the API below from your own hooks. |

2. **Call `LabcoatTracing.ConfigureAspireTesting(builder)` in the fixture that creates the app**, in every framework
   (also with the adapters that need no code), after `CreateAsync` and before `BuildAsync`. Without it the services the
   tests start send their telemetry to the testing default (`localhost:4317`, usually nothing), and the dashboard shows
   the test's span but none of the services' spans or logs.

   ```csharp
   using Labcoat.Testing;

   var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.MyAppHost>(ct);
   LabcoatTracing.ConfigureAspireTesting(builder);
   var app = await builder.BuildAsync(ct);
   ```

Everything is a no-op unless the tests run under Labcoat hosted by an Aspire AppHost (`builder.AddLabcoat(...)`)
**with trace collection turned on**. Tracing is opt-in and off by default: enable it with `.WithTestTracing()` on the
Labcoat resource, the **Toggle test tracing** dashboard command or the toggle in the Labcoat UI.

## How it works

While tracing is on, Labcoat passes the dashboard's OTLP endpoint (and API key) to the test host as
`LABCOAT_OTLP_ENDPOINT`, `LABCOAT_OTLP_PROTOCOL` and `LABCOAT_OTLP_HEADERS`. For each test the adapter calls
`LabcoatTracing.BeginTest`: the test gets a span that is the root of its own trace (exported under the test
assembly's name), and `labcoat:traceId=<hex>` is written to the test's own output. Labcoat strips that line from the
result and links the test to `/traces/detail/<traceId>`. HTTP calls made during the test carry the W3C `traceparent`
header, so the service spans join the test's trace. `ConfigureAspireTesting` sets `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`
(or `..._HTTP_ENDPOINT_URL`) and `AppHost:OtlpApiKey` on the testing builder.

## API for adapters (and for your own hooks)

```csharp
// When the test starts, in the test's execution context (the span becomes Activity.Current):
var span = LabcoatTracing.BeginTest(
    new LabcoatTest("PlacesOrder(2)") { Id = uid, ClassName = "Shop.Tests.OrderTests", MethodName = "PlacesOrder", Framework = "nunit" },
    writeOutput: line => Console.WriteLine(line),   // to the test's own (captured) output
    frameworkActivity: null);                       // or the framework's own per-test root Activity, to adopt it

// When it ends:
LabcoatTracing.EndTest(span, LabcoatTestOutcome.Failed, frameworkOutcome: "Failed");
```

- `LabcoatTest.Id` is the Microsoft.Testing.Platform test node uid (the id of the test's row in Labcoat) when the
  framework exposes it; it becomes the `test.id` span tag. The link from a test to its trace does not depend on it: the
  trace id travels in the test's own output.
- `frameworkActivity`: frameworks that start an `Activity` per test themselves and make it current in the test body
  (TUnit) pass it here. Labcoat then tags and renames it instead of starting a span the test body would not see, and
  `LabcoatTracing.Initialize("<framework source>")` (called before the first test) makes the exporter listen to that
  source. Only spans of test traces are exported from it, not the framework's session or class spans.
- **Trace filter:** `LabcoatTest.Categories` and `LabcoatTest.Traits` (key / value pairs, as Labcoat shows them) are
  what a trace filter matches (`cat:Integration`, `trait:Kind=Slow`, name text; the Labcoat UI's search syntax).
  `BeginTest` applies the filter Labcoat passes (`LABCOAT_OTLP_FILTER`) and returns null for a test that doesn't match:
  no span, no `traceparent`, no trace id. An adapter with a default filter of its own (`[assembly: LabcoatTracing(Filter
  = "...")]`) calls `LabcoatTracing.ShouldTrace(test, defaultFilter)` first; Labcoat's filter wins. Each filter is parsed
  once per process; an invalid one is reported once on standard error and traces nothing. See
  [Trace only some tests](https://github.com/eriklieben/labcoat/blob/main/docs/aspire.md#trace-only-some-tests).
- `LabcoatLiveOutput.Send(test, text)` sends a line live to Labcoat's detail pane while the test runs (a
  `labcoat:live:` marker on the raw stdout; nothing outside Labcoat).

The design, the hook each adapter uses and how to add one for another framework are in
[ARCHITECTURE.md](https://github.com/eriklieben/labcoat/blob/main/src/Labcoat.Testing/ARCHITECTURE.md).
