# Getting started

This page takes you from nothing to a first test run. You need the .NET 10 SDK and a test project on
Microsoft.Testing.Platform (MTP).

## 1. Make sure your tests run on Microsoft.Testing.Platform

Labcoat talks to test projects through MTP's server mode, so a test project must be an MTP executable. Check it by
running the project directly:

```bash
dotnet run --project tests/MyApp.Tests
```

If that prints an MTP summary (`Test run summary: Passed!` with `total`, `failed`, `succeeded`, `skipped`), you are set.
If not, add the settings for your framework:

```xml
<!-- MSTest 4 -->
<OutputType>Exe</OutputType>
<EnableMSTestRunner>true</EnableMSTestRunner>

<!-- NUnit 4.4+ with NUnit3TestAdapter 6 or later (adapter 5 is on MTP 1.x) -->
<OutputType>Exe</OutputType>
<EnableNUnitRunner>true</EnableNUnitRunner>

<!-- xUnit v3: reference xunit.v3.mtp-v2 instead of xunit.v3 -->
<OutputType>Exe</OutputType>
<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>

<!-- TUnit: nothing extra, it is built on MTP -->
<OutputType>Exe</OutputType>
```

The projects in [`samples/`](../samples) show complete project files for each framework. Coming from VSTest
(`Microsoft.NET.Test.Sdk` with an adapter)? [Migrating from VSTest](migrating-from-vstest.md) has the switch per
framework and the .NET 10 `global.json` setting (`"test": { "runner": "Microsoft.Testing.Platform" }`) without which
`dotnet test` fails once the projects are on MTP. Labcoat lists a project that is still on VSTest with what to change,
and doesn't start it.

## 2. Start Labcoat

From the root of your repository:

```bash
dnx Labcoat
```

`dnx` downloads the tool into the NuGet cache and runs it without installing anything. Labcoat then:

1. picks the workspace: the test project in the current directory, otherwise the nearest `.slnx` or `.sln` walking up
   to the repository root, otherwise the current directory;
2. builds the test projects and discovers their tests;
3. opens http://localhost:4300 in your browser (or the next free port).

```text
   LABCOAT  v1.0.0  test explorer for Microsoft.Testing.Platform

  ➜  Local:     http://localhost:4300
  ➜  Workspace: /src/shop/Shop.slnx
  ➜  Tests:     2 test projects
                · tests/Shop.Tests/Shop.Tests.csproj
                · tests/Shop.IntegrationTests/Shop.IntegrationTests.csproj

  Press Ctrl+C to stop.
```

Press Ctrl+C in the terminal to stop Labcoat and the test processes it started.

To point it somewhere else, pass paths: `labcoat tests/MyApp.Tests`, `labcoat MyApp.slnx` or a directory. All options
are in [configuration.md](configuration.md).

## 3. Run tests

- **Run all** in the toolbar (or `Ctrl+Enter`) runs everything. **Run failed** reruns the failures of the last run.
- Click the run button on a row, double-click it, or select it and press `Enter`, to run one test, a class or a
  project.
- Select a test to see its details: the result, the error with the failing line in its source, the stack trace, the
  console output and its history.
- Turn on **Watch** (`W`) and save a file: Labcoat rebuilds and reruns the affected tests.
- Turn on **Collect coverage** to see line and branch coverage on the Coverage page.

[features.md](features.md) walks through everything else.

## 4. Install it for good

```bash
# global tool: the `labcoat` command everywhere
dotnet tool install -g Labcoat
labcoat

# local tool: version pinned in .config/dotnet-tools.json, the same for the whole team
dotnet new tool-manifest
dotnet tool install Labcoat
dotnet labcoat
```

Using Aspire? Add Labcoat to your AppHost instead: [aspire.md](aspire.md). Labcoat can then link each integration test
that starts the Aspire app to its own trace and logs in the dashboard (opt-in). That takes two steps in the test
project, in every framework:

> [!IMPORTANT]
> 1. **Add the package for your test framework:** `Labcoat.Testing.MSTest` (plus two hooks), `Labcoat.Testing.TUnit`,
>    `Labcoat.Testing.NUnit` or `Labcoat.Testing.XUnit` (no hooks).
>
>    ```bash
>    dotnet add MyApp.Tests package Labcoat.Testing.NUnit
>    ```
>
> 2. **Call `LabcoatTracing.ConfigureAspireTesting(builder)` in the fixture that creates the app**, after
>    `CreateAsync` and before `BuildAsync`, so the services send their spans and logs to the dashboard too:
>
>    ```csharp
>    using Labcoat.Testing;
>
>    var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.MyAppHost>(ct);
>    LabcoatTracing.ConfigureAspireTesting(builder);
>    var app = await builder.BuildAsync(ct);
>    ```

Details per framework: [per-test traces by test framework](aspire.md#per-test-traces-by-test-framework).

## Try it on the samples

The repository has one small test project per framework, all testing the same little library. They contain
deliberate failures and skipped tests so there is something to look at.

```bash
git clone https://github.com/eriklieben/labcoat
cd labcoat
dnx Labcoat samples/Samples.slnx
```

You should see 35 tests in 4 projects; **Run all** ends with 21 passed, 10 failed and 4 skipped. The Aspire sample in
`samples/AspireSample` shows the dashboard integration, with per-test traces for MSTest, TUnit, NUnit and xUnit v3. It references the integration from source, so build the UI
first:

```bash
(cd ui && pnpm install --frozen-lockfile && pnpm run build)
dotnet run --project samples/AspireSample/AspireSample.AppHost
```

Something not working? See [troubleshooting.md](troubleshooting.md).
