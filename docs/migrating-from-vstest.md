# Migrating from VSTest

Labcoat runs test projects on Microsoft.Testing.Platform (MTP). A project on VSTest shows up in the tree with a
**VSTest** pill and a message that names the switch for its framework (see
[troubleshooting](troubleshooting.md#my-project-uses-vstest)); Labcoat doesn't build or start it. This page is the
whole switch: the project settings, `dotnet test`, coverage and the things that tend to go wrong.

## 1. Switch the test projects

Every MTP test project is an executable: set `<OutputType>Exe</OutputType>`. Then turn on the runner of your
framework. A `Directory.Build.props` next to the solution switches all test projects at once (it is read before the
packages' settings, so select the test projects by name, not by `IsTestProject`):

```xml
<Project>
  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('.Tests'))">
    <OutputType>Exe</OutputType>
    <EnableMSTestRunner>true</EnableMSTestRunner>   <!-- MSTest -->
    <EnableNUnitRunner>true</EnableNUnitRunner>     <!-- NUnit -->
  </PropertyGroup>
</Project>
```

| Framework | What to change |
| --- | --- |
| MSTest | `<EnableMSTestRunner>true</EnableMSTestRunner>` (MSTest 3.2 or later; Labcoat needs MSTest 4, which is on MTP 2). Or use `<Project Sdk="MSTest.Sdk/4.x">`: it is on MTP unless `<UseVSTest>true</UseVSTest>` is set. |
| NUnit | `<EnableNUnitRunner>true</EnableNUnitRunner>` with NUnit3TestAdapter 6 or later (adapter 5 is on MTP 1, Labcoat needs MTP 2). |
| xUnit v2 | xUnit v2 has no MTP runner of its own (the third-party `YTest.MTP.XUnit2` package adds one; xUnit.net and Microsoft don't support it): move to xUnit v3. Replace `xunit` and `xunit.runner.visualstudio` with `xunit.v3.mtp-v2` (or `xunit.v3` 4.x), and set `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` so `dotnet test` and `dotnet run` use the MTP command line. See [xUnit v3 migration](https://xunit.net/docs/getting-started/v3/migration). |
| xUnit v3 | Already on MTP. Add `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` for `dotnet test`. |
| TUnit | Always on MTP: nothing to change. |

`Microsoft.NET.Test.Sdk` can stay: MSTest and NUnit projects then support both platforms (Visual Studio 17.14+ and
VS Code's C# Dev Kit run MTP projects). Check a project with `dotnet run --project tests/MyApp.Tests`: it prints an MTP
`Test run summary`.

## 2. Keep `dotnet test` working: global.json

The .NET 10 SDK has a native MTP mode of `dotnet test`. Turn it on in the `global.json` at the repository root:

```json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

Without it, `dotnet test` on the .NET 10 SDK runs in VSTest mode, and an MTP 2 project (MSTest 4, NUnit3TestAdapter
6, xUnit v3 with `xunit.v3.mtp-v2`, TUnit 1) fails the build with:

```text
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.
If you use dotnet test, you should opt-in to the new dotnet test experience.
For more information, see https://aka.ms/dotnet-test-mtp-error
```

In MTP mode, `dotnet test` takes `--project <csproj>`, `--solution <sln>` or `--test-modules <dll glob>` instead of a
bare path, and test app options (`--filter`, `--report-trx`, `--coverage`) go straight on the command line (a `--`
before them still works). Labcoat itself doesn't use `dotnet test`, so it doesn't need this setting, but your CI does.

**.NET 9 SDK and older** have no MTP mode. Set `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`
in `Directory.Build.props` instead, and pass MTP options after an extra `--`: `dotnet test -- --report-trx`. Remove the
property when you move to the .NET 10 SDK and `global.json`: it is no longer needed there.

## 3. Coverage

The VSTest data collectors don't run on MTP:

| VSTest | MTP |
| --- | --- |
| `dotnet test --collect "Code Coverage"` (Microsoft.CodeCoverage via Microsoft.NET.Test.Sdk) | the `Microsoft.Testing.Extensions.CodeCoverage` package and `--coverage --coverage-output-format cobertura` |
| `coverlet.collector` with `--collect "XPlat Code Coverage"` | `coverlet.MTP` and `--coverlet`, or switch to Microsoft.Testing.Extensions.CodeCoverage |
| `coverlet.msbuild` (`/p:CollectCoverage=true`) | as above |

Labcoat's coverage needs `Microsoft.Testing.Extensions.CodeCoverage` (the MSTest and TUnit packages bring it; add it to
NUnit and xUnit v3 projects). One difference to expect: the test assembly itself is no longer counted by default
(`IncludeTestAssembly` is false on MTP; it was true on VSTest). See [features.md](features.md#which-code-counts) for the
settings file Labcoat and `dotnet test --coverage` share.

## 4. Gotchas

- **Mixed solutions.** In MTP mode, `dotnet test` fails on a test project that supports only VSTest; in VSTest mode MTP
  2 projects fail. Switch every test project in the solution together (a `Directory.Build.props` helps), or exclude
  the old ones from the solution you test.
- **Zero tests is a failure.** MTP exits with code 8 when no test ran (VSTest succeeded). Pass `--ignore-exit-code 8`,
  or set it per project in `<TestingPlatformCommandLineArguments>`.
- **`--filter` for xUnit.** MSTest and NUnit keep the VSTest filter syntax; xUnit v3 on MTP uses `--filter-class`,
  `--filter-method`, `--filter-trait` or `--filter-query`.
- **Loggers are reporters.** `--logger trx` becomes `--report-trx` (package `Microsoft.Testing.Extensions.TrxReport`);
  `--blame-hang` / `--blame-crash` become `--hangdump` / `--crashdump` (their extension packages).
- **`.runsettings`.** MSTest and NUnit still read it (`--settings`); MTP's own configuration file is `testconfig.json`.
- **`OutputType` Exe without the runner** is the confusing case: `Microsoft.NET.Test.Sdk` generates a `Main` that does
  nothing, so the test app starts and exits with code 0. Labcoat recognizes it and shows the VSTest message instead of
  starting it.
- **CI tasks.** The Azure DevOps VSTest task runs VSTest only; use `dotnet test` (the .NET Core CLI task) with the
  `global.json` above.

## Sources

- [Migration guide from VSTest to Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/migrating-vstest-microsoft-testing-platform)
- [Testing with `dotnet test`](https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test)
- [Migrating from MTP v1 to v2](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-migration-from-v1-to-v2)
  (the `global.json` requirement and the error above)
- [MTP code coverage](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-code-coverage)
- [Microsoft.Testing.Platform overview](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro)
