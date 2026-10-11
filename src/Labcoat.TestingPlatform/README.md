# Labcoat.TestingPlatform (preview)

A [Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro)
extension that adds a `--labcoat` option to your test application: it opens [Labcoat](https://github.com/eriklieben/labcoat),
a web UI for Microsoft.Testing.Platform tests, for **this** test project instead of running the tests.

```bash
dotnet add MyApp.Tests package Labcoat.TestingPlatform
dotnet run --project MyApp.Tests -- --labcoat
dotnet run --project MyApp.Tests -- --labcoat watch port=5000   # Labcoat options, without the dashes
```

The runner is bundled in the package (started from the NuGet cache; nothing is copied to `bin/`), so there's no tool
to install. It needs the .NET 10 runtime, whatever your test project targets.

- **Registration:** automatic through the `TestingPlatformBuilderHook` MSBuild item. With a hand-written entry point,
  call `builder.AddLabcoat()`.
- **No recursion:** the option is inert in server mode (`--server`: Labcoat itself, IDE test explorers, `dotnet test`)
  and when `LABCOAT_TEST_HOST` is set (Labcoat sets it on every test host it starts).
- **Lifetime:** the test process starts Labcoat, waits for it (Ctrl+C stops both) and exits with Labcoat's exit code.
  No tests run in that process.
- **Which runner:** `$LABCOAT_PATH` (a `Labcoat.dll` or `labcoat` executable) > the bundled runner (MSBuild property
  `LabcoatPath`) > `dotnet tool exec Labcoat@<version>`.

Requires Microsoft.Testing.Platform 2.x (MSTest 4, xUnit v3 3.x, NUnit 4.4+, TUnit). See
[docs/standalone.md](https://github.com/eriklieben/labcoat/blob/main/docs/standalone.md) for the design and its limits.
