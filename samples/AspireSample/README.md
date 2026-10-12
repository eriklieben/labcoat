# AspireSample

A small Aspire 13.6 solution used to showcase **Labcoat** against real, slow-starting
integration tests running on Microsoft.Testing.Platform (MTP).

| Project | What it is |
| --- | --- |
| `AspireSample.AppHost` | Aspire AppHost. Orchestrates the `api` resource and the `tests` resource (Labcoat, via `Labcoat.Aspire.Hosting`). |
| `AspireSample.ServiceDefaults` | Standard Aspire service defaults (OpenTelemetry, health checks, service discovery). |
| `AspireSample.Api` | Minimal-API "orders" service with in-memory storage (no Docker/Podman needed). |
| `AspireSample.Tests` | MSTest 4.0.2 + `Aspire.Hosting.Testing` tests, built as an MTP executable. |
| `AspireSample.PassingTests` | The all-green twin of `AspireSample.Tests` (same sources, minus the deliberate failures). |
| `AspireSample.TUnitTests` | TUnit + `Aspire.Hosting.Testing` API tests, with per-test traces from `Labcoat.Testing.TUnit` (no hooks). |
| `AspireSample.NUnitTests` | NUnit + `Aspire.Hosting.Testing` API tests, with per-test traces from `Labcoat.Testing.NUnit` (no hooks). |
| `AspireSample.XUnitTests` | xUnit v3 + `Aspire.Hosting.Testing` API tests, with per-test traces from `Labcoat.Testing.XUnit` (no hooks). |
| `AspireSample.Web` | The orders front end's pricing and validation in TypeScript, tested with Vitest (run by `Labcoat.Vitest`). Not in the solution; run `npm install` in it once. One test fails on purpose. |

## API

| Endpoint | Behavior |
| --- | --- |
| `GET /orders` | All orders (two are seeded on startup). |
| `GET /orders/{id}` | One order, or `404`. |
| `POST /orders` | Creates an order: `{ "customer": "Contoso", "items": [{ "sku": "A-1", "quantity": 2, "unitPrice": 9.99 }] }`. Returns `201` + `Location`, or `400` validation problem (missing customer, no items, quantity <= 0, missing SKU, negative price). |
| `GET /health`, `GET /alive` | Health endpoints from service defaults (Development environment). |

## Run the app

```bash
cd samples/AspireSample
dotnet run --project AspireSample.AppHost
```

The console prints the dashboard login URL. The default `https` profile needs the ASP.NET Core dev
certificate (`dotnet dev-certs https --trust`); use `--launch-profile http` to avoid it.

## Labcoat in the dashboard

The AppHost adds the test explorer with the `Labcoat.Aspire.Hosting` integration
(`src/Labcoat.Aspire.Hosting`, referenced here as a ProjectReference with `IsAspireProjectResource="false"`; in your own
app use the NuGet package):

```csharp
builder.AddLabcoat("tests")
       .WithTestProject("../AspireSample.Tests");
```

The AppHost has four Labcoat resources:

| Resource | Runners | Tests |
| --- | --- | --- |
| `tests-passing` | default | `AspireSample.PassingTests`, `.TUnitTests`, `.NUnitTests`, `.XUnitTests` (all green) |
| `tests-failing` | `dotnet` | `AspireSample.Tests` (MSTest, with deliberate failures) |
| `tests-vitest` | `vitest` | `AspireSample.Web` |
| `tests-combined` | `dotnet`, `vitest` | `AspireSample.Tests` and `AspireSample.Web` in one tree |

`Labcoat.Vitest` (`src/Labcoat.Vitest`) is referenced like `Labcoat.Aspire.Hosting`; building the AppHost copies its
runner to `labcoat/runners/vitest/`. Run `npm install` in `AspireSample.Web` before the first start, else its project
shows a discovery error. See [docs/runners.md](../../docs/runners.md).

Building the AppHost publishes `src/Labcoat` and places it in `AspireSample.AppHost/bin/Debug/net10.0/labcoat/`;
the `tests` resource runs it from there. In the dashboard the `tests` resource (type `Labcoat`) has a
**Labcoat UI** link and the commands **Run all tests**, **Run failed tests**, **Toggle watch mode** and
**Toggle code coverage** and **Toggle test tracing**. Stack-trace frames anywhere under `samples/AspireSample` (e.g. the Api) can be opened in the UI.

`AspireUseCliBundle` is on, so `dotnet run --project AspireSample.AppHost` goes through the Aspire CLI (`aspire run`).
Running the integration tests starts the AppHost via `Aspire.Hosting.Testing`; the explorer resource is skipped there.

### From a test to its trace and logs

Trace collection is opt-in and **off by default**: turn it on with the **Toggle test tracing** command on a Labcoat
resource, the toggle in the Labcoat UI, or `.WithTestTracing()` in `AppHost.cs`. It lets you jump from a failing
integration test to the service traces/logs it caused in the Aspire dashboard, and Labcoat shows the error spans and
warning logs of that test (e.g. `api: POST /orders → 400` and the API's "Rejected order ..." warning) inline.

The MSTest projects use `Labcoat.Testing.MSTest` (`src/Labcoat.Testing.MSTest`): `LabcoatHooks.cs` starts a span per
test (`[GlobalTestInitialize]`/`[GlobalTestCleanup]`) and `AspireAppFixture` calls
`LabcoatTracing.ConfigureAspireTesting(builder)`, so the `api` started by the tests sends its telemetry to *this*
dashboard. `AspireSample.TUnitTests` (in the `tests-passing` resource) uses `Labcoat.Testing.TUnit`, which needs no
hooks: it adopts the span TUnit starts for each test; its `AspireApp` calls `ConfigureAspireTesting` the same way, and
`Requests_carry_the_tests_own_trace` checks the trace context when run with tracing on. `AspireSample.NUnitTests` and
`AspireSample.XUnitTests` (also in `tests-passing`) do the same with `Labcoat.Testing.NUnit` and `Labcoat.Testing.XUnit`,
which start a span per test themselves (an NUnit action attribute, an xUnit `BeforeAfterTestAttribute`), with the app
started once per test assembly (an NUnit `[SetUpFixture]`, an xUnit assembly fixture). In Labcoat every test then has
**View trace** / **Logs** buttons (and links under a failure) that open `/traces/detail/<traceId>` and
`/structuredlogs?traceId=<traceId>`: the test span (resource `AspireSample.Tests`) with the `api` request spans beneath it,
and e.g. the API's "Rejected order ..." warning for the intentionally failing demo test. The test-started `api` shows up
as an extra `api` replica (e.g. `api-abcd1234`) next to the AppHost's own. Outside Labcoat (plain `dotnet test`) the
helpers do nothing.

![Trace of a failing test](../../docs/images/aspire-trace-dashboard.png)

## Run the tests

The test project is an MTP executable (`OutputType=Exe`, `EnableMSTestRunner=true`):

```bash
# from the repo root
dotnet run --project samples/AspireSample/AspireSample.Tests

# or run the built executable directly
samples/AspireSample/AspireSample.Tests/bin/Debug/net10.0/AspireSample.Tests --output Detailed

# only the fast unit tests (does not start the Aspire app)
samples/AspireSample/AspireSample.Tests/bin/Debug/net10.0/AspireSample.Tests --filter "TestCategory=Unit"

# list tests
samples/AspireSample/AspireSample.Tests/bin/Debug/net10.0/AspireSample.Tests --list-tests
```

Expected result: **20 tests - 18 passed, 1 failed, 1 skipped.** The failure is on purpose:

- `Demo_intentionally_failing_expects_wrong_status_code` - **intentionally fails** (expects `201` where the API correctly returns `400`) to demo failure output.
- `Get_orders_supports_pagination` - `[Ignore]`d (feature not implemented).

### Test layout

- `Unit/OrderPricingTests` (`TestCategory=Unit`) - fast pure tests of pricing/validation logic, including `DataRow`s.
- `Integration/AspireAppFixture` - starts the AppHost **once per test run** (lazily, from `[ClassInitialize]` of the
  integration classes), waits for `api` to be healthy and disposes it in `[AssemblyCleanup]` - or, in Labcoat's warm
  mode, keeps it running for the next run (see below).
- `Integration/OrdersApiTests` (`Integration`, some `Smoke`) - happy paths against the running API, logging response bodies via `TestContext`/`Console`.
- `Integration/OrderValidationTests` (`Integration`, `Validation`, `Demo`) - parameterized `400` tests, the intentionally failing test and the ignored one.

### Timing

Starting the Aspire app takes ~5-20s (attributed to the first integration test) and stopping it ~12s, so a full
run takes ~30-40s from the command line while the unit tests alone finish in under a second.

### Warm mode friendly fixture

Labcoat keeps the test host process alive between runs and sets `LABCOAT_WARM_HOST=1` for it. MSTest runs
`[ClassInitialize]` **and `[AssemblyCleanup]` for every run request** in that process, while static fields survive.
`AspireAppFixture` therefore:

1. starts the app once and keeps it in a static (`EnsureStartedAsync` returns immediately when it is already running);
2. skips the disposal in `[AssemblyCleanup]` when `LABCOAT_WARM_HOST=1`;
3. disposes the app on `AppDomain.ProcessExit` (when Labcoat stops/restarts the test host, or the process ends);
4. disposes a half-started app when startup is cancelled or fails, so nothing leaks into the long-lived process.

Result in Labcoat: the first run of an integration test pays the Aspire start (~4-8s here), every rerun of it takes
milliseconds, and the ~12s shutdown happens once, when the test host is restarted (new build, **Restart test host**)
or Labcoat exits. Outside Labcoat (`dotnet test`, CLI) behaviour is unchanged.
