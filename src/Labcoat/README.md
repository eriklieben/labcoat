# Labcoat

**A web UI for Microsoft.Testing.Platform.** Labcoat builds your
[Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro) test
projects and shows their tests in your browser: a live test tree, failures with the failing line in its source and an
expected/actual diff, code coverage per file and per test, watch mode that reruns only the affected tests, and run
history with flaky-test detection.

![Labcoat showing a failed test with an expected/actual diff](https://raw.githubusercontent.com/eriklieben/labcoat/main/docs/images/hero-dark.png)

## Install and run

Labcoat is a .NET tool and needs the .NET 10 SDK.

```bash
# no install: download and run (.NET 10 SDK)
dnx Labcoat

# global tool
dotnet tool install -g Labcoat
labcoat

# local tool, pinned per repository
dotnet new tool-manifest     # once per repository
dotnet tool install Labcoat
dotnet labcoat
```

Run it in your repository: it finds the test projects (the test project in the current directory, else the nearest
`.slnx`/`.sln` walking up), builds them and opens the UI in your browser (http://localhost:4300, or the next free port).

```bash
labcoat                                   # tests of the current project / solution
labcoat tests/MyApp.Tests --watch         # one project, watch mode
labcoat MyApp.slnx --coverage --no-open   # a solution, coverage on, don't open a browser
labcoat --help                            # all options and environment variables
```

## Requirements

Your test projects must run on Microsoft.Testing.Platform. For MSTest:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <EnableMSTestRunner>true</EnableMSTestRunner>
</PropertyGroup>
```

MSTest 4, NUnit (`EnableNUnitRunner`), xUnit v3 (`xunit.v3.mtp-v2`) and TUnit are covered by the samples in the
repository. Code coverage uses `Microsoft.Testing.Extensions.CodeCoverage` (included in the MSTest and TUnit packages),
with the test project's own `coverage.settings.xml` / `CodeCoverage.xml` / `*.runsettings` when it has one; otherwise
Labcoat counts the projects the tests reference and leaves out Labcoat's assemblies, test frameworks and generated code.

## Aspire

Using [Aspire](https://aspire.dev)? Add [`Labcoat.Aspire.Hosting`](https://www.nuget.org/packages/Labcoat.Aspire.Hosting)
to your AppHost instead: Labcoat then runs as a resource in the dashboard, with run/watch/coverage commands and
optional per-test links to traces and structured logs.

```csharp
builder.AddLabcoat("tests");
```

## More

- Documentation, screenshots and samples: https://github.com/eriklieben/labcoat
- Running without Aspire, `dnx` details and the `dotnet run -- --labcoat` preview:
  [docs/standalone.md](https://github.com/eriklieben/labcoat/blob/main/docs/standalone.md)
- Changelog: [CHANGELOG.md](https://github.com/eriklieben/labcoat/blob/main/CHANGELOG.md)

Labcoat listens on localhost only by default; see
[SECURITY.md](https://github.com/eriklieben/labcoat/blob/main/SECURITY.md) before exposing it on a network.

## License

MIT. Labcoat is a community project and is not affiliated with or endorsed by Microsoft or the .NET Foundation.
