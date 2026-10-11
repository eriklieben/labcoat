# Labcoat benchmarks (C#)

[BenchmarkDotNet](https://benchmarkdotnet.org) micro-benchmarks of the Labcoat host's hot paths
(`benchmarks/Labcoat.Benchmarks`). They measure one function at a time, time and allocations per operation, at the
sizes a large solution has (1,000 and 20,000 tests). They add to the end-to-end perf, soak and leak checks
(CONTRIBUTING.md, "Performance and soak checks"); they don't replace them and they don't gate CI.

The project is in `Labcoat.slnx` (so CI builds it with `-warnaserror`), but it is not a test project (`dotnet test`
skips it), not packable, and not part of the coverage run (`eng/coverage.mjs` runs only `tests/` projects).

## Run

Always in Release. BenchmarkDotNet needs a filter; without one it shows an interactive menu.

```bash
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --list flat            # every benchmark
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*' --quick   # everything, short job
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*HistoryStore*'
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*EventHub*StateSnapshot*' --quick
```

| Option | What it does |
| --- | --- |
| `--filter <glob>` | Benchmarks to run, matched against `Namespace.Class.Method` (`'*'`, `'*Coverage*'`, several globs separated by spaces). |
| `--quick` | Labcoat's short job: 1 launch, 1 warm-up and 3 measured iterations. A full `--filter '*' --quick` run takes about 10 minutes. Good enough to see a 2× change. |
| *(no job option)* | BenchmarkDotNet's default job: as many iterations as it needs for a stable result. Use it for small differences. |
| `--job short` / `--job dry` | BenchmarkDotNet's own short job, or a single cold run (does every benchmark still work?). |
| `--inProcess` | Run in the benchmark process instead of a generated child process (faster to start, slightly less isolated). |
| `--artifacts <dir>` | Where the reports go (default `BenchmarkDotNet.Artifacts/` in the working directory, ignored by git). |

Reports per class in `<artifacts>/results`: `*-report-github.md` (the table), `*-report-brief.json` (statistics and
memory, for comparing), `*-report.csv` and `*-report.html`. Every class has `[MemoryDiagnoser]`, so the tables show
`Allocated` and GC counts next to the time.

## Compare before and after

```bash
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*Coverage*' --artifacts artifacts/bdn-base
# ... make the change ...
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*Coverage*' --artifacts artifacts/bdn-head
node eng/bdn-compare.mjs artifacts/bdn-base artifacts/bdn-head --threshold 5 [--only-changes] [--output compare.md]
```

`eng/bdn-compare.mjs` matches the cases by name and parameters and prints mean time and allocated bytes of both runs,
marking changes above the threshold (default 10%). Compare runs from the same machine only, with nothing heavy running
next to them; for differences under ~10% use the default job, not `--quick`.

## What is measured

Inputs are generated in code (`Synthetic/`): a deterministic suite shaped like the UI's `?mock=huge` scenario
(`ui/src/mock/scale.ts`: same name lists and seed, 5-6 level namespaces, data rows, ~1% failures), one product source
file per test class (~80 files for 1,000 tests, ~1,550 for 20,000), and Cobertura XML in the shape of
Microsoft.Testing.Extensions.CodeCoverage's output. Files the code under test reads or writes go to a temp directory
that is deleted afterwards. Nothing large is committed.

| Class | Product code | Cases |
| --- | --- | --- |
| `MtpParsingBenchmarks` | `MtpClient.cs` (`MtpConnection.TryParseMessage` / `Consume`), `MtpMapping` | A whole run's `testing/testUpdates/tests` stream (in-progress + final update per test, 100 changes per message) read in 64 KB chunks: framing only, + `JsonNode.Parse`, + mapping to `TestNode`; and one message carrying every node. Tests = 1k / 20k. |
| `EventHubBenchmarks` | `PooledMessage`, `Models.cs`, `TestUpdateEncoder` | `state` snapshot (full and lean), `testsUpdated` of every test (full and lean), `testResults` patches of a whole run (diff + serialize). Tests = 1k / 20k. |
| `HistoryStoreBenchmarks` | `HistoryStore` (history.json v2) | `Save` and `Load` of a full store (20 history entries per test, 50 past runs: ~2 MB / ~38 MB), `Record` of one run. Tests = 1k / 20k. |
| `FullCoverageStoreBenchmarks` | `FullCoverageStore` (coverage.json) | `Save`, `Load`, and the startup `Restore` (load + rebuild + stale check). Tests = 1k / 20k. |
| `CoverageBenchmarks` | `Coverage.cs`, `FullCoverage.cs` | Parse every project's Cobertura report, build the report, `SetFull` of every project (clone + SHA-256 stamps + rebuild), `MergeSubset`, `RefreshStale`. Tests = 1k / 20k. |
| `ImpactMapBenchmarks` | `ImpactMap` | `AffectedByLines` (a changed range inside / outside executed methods), `Affected` of an edited file (re-read + diff), `TestsCoveringLine`, `ParseCobertura`. Tests = 1k / 20k. |
| `GitAffectedBenchmarks` | `GitRepo.ParseNameStatus`, `GitRepo.ParseHunks` | `git diff --name-status -z` and a `-U0` patch of 50 / 1,000 changed files (modified, added, deleted, renamed, a quoted path). |
| `TestHostEnvironmentBenchmarks` | `TestHostEnvironment.Sanitize` | The environment copy + filter per test host start, hosted by Aspire (~110 variables, pass-through list) and from a terminal. |
| `LiveOutputBenchmarks` | `LiveOutput` | 2,000 lines attributed and drained in 100 ms flushes, with 1 / 8 tests running: plain host lines, Labcoat.Testing marker lines, xUnit `OUTPUT:` lines, MTP's cumulative in-progress output. |
| `SecurityBenchmarks` | `RequestGuard.Check` | A static GET, a same-origin API POST, a WebSocket handshake, a token-authenticated POST, and two rejections (DNS-rebinding Host, cross-origin). |

Benchmarks that would silently measure an early return check their input in `[GlobalSetup]` and throw instead.

## When the product code changes

The benchmarks call the product code directly. Most of it is public; `Labcoat.csproj` has
`InternalsVisibleTo Labcoat.Benchmarks` for `MtpConnection.TryParseMessage` / `Consume`, `FullCoverageStore.Load` /
`Schedule` and `GitRepo.ParseNameStatus` / `ParseHunks`. `HistoryStoreBenchmarks` sets two private fields of
`HistoryStore` by reflection (`_dirty` to save again, `_saveTimer` to stop the debounced background save); it throws
with a clear message when they are renamed. If a signature changes, the benchmark project stops compiling in CI's build:
update the benchmark in the same PR. When a payload shape changes on purpose (e.g. the WebSocket events in
docs/contract.md), the numbers of `EventHubBenchmarks` and `MtpParsingBenchmarks` change with it; say so in the PR.

## Nightly CI

The `dotnet-bench` job of the [Performance workflow](../.github/workflows/perf.yml) builds the project, runs
`--filter '*' --quick`, writes every table to the job summary and uploads the `bdn-results` artifact (Markdown, JSON,
CSV). When an earlier run of the workflow on the same branch has that artifact, the summary starts with
`eng/bdn-compare.mjs --only-changes` against it. The job never fails the workflow (`continue-on-error`): shared runners
are noisy, so read the trend over several nights before acting on one number.

## Adding a benchmark

A `public` (not sealed) class with `[MemoryDiagnoser]`, `[Params]` for realistic sizes, inputs built in
`[GlobalSetup]` (from `Synthetic/` where possible), temp files cleaned up in `[GlobalCleanup]`, and methods that return
a value so the work isn't optimized away. Keep per-invocation setup out of the measured method unless it is part of
the real cost (and then say so in the class comment).
