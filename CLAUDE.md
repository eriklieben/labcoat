# Labcoat: notes for coding agents

Read [CONTRIBUTING.md](CONTRIBUTING.md) first: the repository layout, how to build and test, the coding conventions and
the commit format are there. This file adds what an agent tends to get wrong in this codebase.

## Before writing code

- **Match the code around you**: its naming, its comment density, its idioms. `...Locked` methods expect the caller to
  hold `Sync`; say "(caller holds Sync)" when a method relies on it.
- **Look for what exists before adding something.** Static helpers live in their own classes in `src/Labcoat`
  (`PathUtil`, `ProjectReferences`, `RunCounts`, `SourceExtensions`, `DotnetCli` for dotnet child processes, ...);
  search before writing a new one.
- **Don't grow `Explorer`.** It is over the class coupling limit (CA1506, see `src/CodeMetricsConfig.txt`). A feature
  goes into a service that takes `WorkspaceState` and `IExplorerEffects`, like `AffectedService`, `CoverageService`,
  `PlatformCheck`, `TestHostPool`, `HangMonitor`, `LiveOutputService`, `ExportService`, `SnapshotService` and
  `WatchMode`; `Explorer` only delegates to it, and an endpoint calls the service directly (`explorer.Affected.*`).
- **A change to an endpoint, a WebSocket message or an event** updates [docs/contract.md](docs/contract.md);
  host internals and the MTP protocol are in [docs/protocol.md](docs/protocol.md), the UI in
  [ui/ARCHITECTURE.md](ui/ARCHITECTURE.md).

## C# to avoid here

- **Catch-alls.** Catch the exceptions you expect. A best-effort catch names the type and says why:
  `catch (IOException) { /* unreadable folder: keep walking up */ }`. Cancellation is the pair
  `catch (Exception ex) when (!ct.IsCancellationRequested) { Log(...); }` + `catch (Exception) { /* cancelled ... */ }`.
- **Defensive noise.** Nullable is on: no null checks of non-nullable parameters, no `!` or `?.` to quiet a warning
  you haven't checked.
- **Abstractions nobody needs**: an interface with one implementation, a factory, a `*Manager`, an options type for two
  values. What tests need to replace goes in a seam in `Explorer.Seams.cs`.
- **Comments that repeat the code** or describe the change ("now uses X", "fixed the race"). Comments say why.
- **Leftovers**: unused parameters, compatibility overloads, `[Obsolete]` shims, code kept "just in case".
- **Async mistakes**: pass the `CancellationToken` on (last parameter); no `.Result` / `.Wait()`, no `Task.Run` around
  synchronous code, no `async void`.
- **Culture-dependent strings**: `StringComparison.Ordinal` / `StringComparer.Ordinal`, `CultureInfo.InvariantCulture`
  for parsing and formatting.
- **Disposables without an owner**: whoever creates a timer, watcher or process disposes it, and the explorer stops its
  timers on shutdown (`Explorer.DisposeAsync`).

## Warnings, analyzers, tests

- CI builds with `-warnaserror`, with the .NET analyzers on. Fix the finding. Don't make it pass with `NoWarn`,
  `#pragma warning disable`, a `[SuppressMessage]` without a `Justification`, a skipped test, a weaker assertion or a
  package version outside `Directory.Packages.props`. If none of that fits, stop and say so.
- A local `-warnaserror` build can fail on NU1507 when your NuGet config has more than one package source; add
  `-warnAsMessage:NU1507`.
- Tests assert behavior, not lines touched. For timing, pass a `FakeTimeProvider` (`LiveOutput`, `ProjectWatcher`)
  rather than `Task.Delay`, and add a `TimeProvider` parameter where a test needs one.
- Run a subset while working (`dotnet run --project tests/Labcoat.Tests -- --filter "FullyQualifiedName~..."`), the
  whole suite before you're done (`dotnet test --solution Labcoat.slnx`, several minutes).
