# Labcoat.Vitest

Runs the [Vitest](https://vitest.dev) tests of your JavaScript / TypeScript projects in
[Labcoat](https://github.com/eriklieben/labcoat), next to your .NET tests: the same test tree, live results, warm reruns,
watch mode, failure diffs and coverage.

## In an Aspire AppHost

```bash
dotnet add MyApp.AppHost package Labcoat.Aspire.Hosting
dotnet add MyApp.AppHost package Labcoat.Vitest
```

With the package referenced, `AddLabcoat` finds Vitest projects (a folder with a `vitest.config.*`, or a `package.json`
that depends on `vitest`) next to the .NET test projects. Choose the runners per resource with `WithRunners`:

```csharp
builder.AddLabcoat("tests");                                    // .NET and Vitest
builder.AddLabcoat("dotnet-tests").WithRunners("dotnet");       // .NET only
builder.AddLabcoat("web-tests").WithRunners("vitest")           // Vitest only
       .WithTestProject("../web");
```

## With the Labcoat tool

```bash
labcoat --runners dotnet,vitest
```

The tool fetches this package (in its own version) the first time a runner is asked for.

## Requirements

- Node.js 20 or later on `PATH`.
- Vitest 5 or later installed in the project (`npm install`); Labcoat uses the project's own Vitest.
- Coverage: a Vitest coverage provider such as `@vitest/coverage-v8`.

More in [docs/runners.md](https://github.com/eriklieben/labcoat/blob/main/docs/runners.md).
