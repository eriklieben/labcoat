# Contributing to Labcoat

Thanks for helping out! Bug reports, ideas, docs fixes and code are all welcome. By participating you agree to the
[Code of Conduct](CODE_OF_CONDUCT.md). Security problems: see [SECURITY.md](SECURITY.md), not the issue tracker.

## What goes into Labcoat

Labcoat is maintained by [Erik Lieben](https://github.com/eriklieben), who decides what goes into the repository. Every
idea is read and considered, but not every one fits what Labcoat aims to be, and saying no to a good idea is sometimes
the right call for the project. A feature request or a pull request can be declined even when it is well made: that is
a decision about scope and maintenance, not about your work.

So, to avoid wasted effort:

- **Small fixes** (typos, docs, an obvious bug with a clear fix): a pull request is fine.
- **Anything bigger** (a new feature, a new option, a change in behavior, a new dependency): open an issue or a
  [discussion](https://github.com/eriklieben/labcoat/discussions) first and wait for a go-ahead before you write code.
  The go-ahead is the **`accepted`** label on the issue; **`out-of-scope`** means it won't go in.
- **A pull request without that agreement** may be closed. You are of course free to keep the change in your fork.

New issues start with **`needs-triage`**; an **`area: …`** label (ui, host, aspire, coverage, tracing, cli, docs) says
which part of Labcoat it is about. Questions go to [Discussions → Q&A](https://github.com/eriklieben/labcoat/discussions),
release news to Announcements.

## Repository layout

| Path | What |
| --- | --- |
| `Labcoat.slnx` | The product: everything under `src/` and `tests/`, plus `benchmarks/`. |
| `src/Labcoat` | The host and `dotnet tool`: ASP.NET Core minimal API that builds test projects, drives them over Microsoft.Testing.Platform's JSON-RPC server mode and serves the UI from `wwwroot`. |
| `src/Labcoat.Aspire.Hosting` | Aspire hosting integration (`builder.AddLabcoat(...)`); bundles a publish of `src/Labcoat`. |
| `src/Labcoat.Testing` | Test-side core, framework neutral: per-test OpenTelemetry traces for Aspire integration tests (design: [ARCHITECTURE.md](src/Labcoat.Testing/ARCHITECTURE.md)). |
| `src/Labcoat.Testing.MSTest`, `.TUnit`, `.NUnit`, `.XUnit` | The framework adapters of `Labcoat.Testing` (hooks, live output). |
| `src/Labcoat.TestingPlatform` | Preview MTP extension: `dotnet run -- --labcoat`; bundles a publish of `src/Labcoat`. |
| `src/Labcoat.Vitest` | The Vitest runner package ([docs/runners.md](docs/runners.md)): `runner/` is a Node.js test host that speaks MTP server mode and drives the project's own Vitest; no .NET code. Its tests: `tests/Labcoat.Vitest.Tests` (`node --test`). |
| `tests/Labcoat.Tests` | Tests of the host (MSTest on MTP): unit tests, the HTTP / WebSocket API in-process, end-to-end runs of small scratch test projects. |
| `tests/Labcoat.*.Tests` | Tests of the other packages (`Aspire.Hosting`, `Testing`, `Testing.MSTest`, `TestingPlatform`; `Testing.TUnit`, `Testing.NUnit` and `Testing.XUnit` run on their own framework). |
| `ui/` | The web UI: TypeScript + native custom elements, built with Vite into `src/Labcoat/wwwroot`. npm workspace root. |
| `ui/design-system` | `@eriklieben/labcoat-design-system`: reactive core, `h()` DOM builder, tokens, theme families, `lc-*` components, gallery. |
| `samples/Samples.slnx` | One small test project per framework (MSTest, NUnit, xUnit v3, TUnit) testing `SampleTests.Lib`. |
| `samples/AspireSample` | Aspire app + integration tests with two Labcoat resources (own solution). |
| `benchmarks/Labcoat.Benchmarks` | BenchmarkDotNet micro-benchmarks of the host (not shipped, not a test project); see [Benchmarks](#benchmarks). |
| `eng/` | Build and CI scripts (`smoke-test.mjs`, `coverage.mjs`, `check-links.mjs`, `release.mjs`, `bdn-compare.mjs`). |
| `package.json`, `release.config.mjs`, `commitlint.config.mjs` | Release and commit tooling in the root (semantic-release, commitlint); see [Commit messages and releases](#commit-messages-and-releases). |
| `docs/` | `contract.md` (browser <-> host API), `protocol.md` (host internals, MTP protocol), `standalone.md`. |

## Prerequisites

- **.NET 10 SDK** (`global.json` pins 10.0.100 or a later feature band).
- **Node.js 22** or later (npm comes with it).
- For the UI checks and screenshots: Playwright's Chromium, once: `cd ui && pnpm exec playwright install chromium`.
- `samples/AspireSample` needs nothing extra: it has no container resources.

## Build and run

The UI build output (`src/Labcoat/wwwroot`) is **not** checked in. Build it once before running or packing the host:

```bash
cd ui
pnpm install --frozen-lockfile
pnpm run build            # typecheck + vite build into ../src/Labcoat/wwwroot
cd ..

dotnet build Labcoat.slnx
dotnet test --solution Labcoat.slnx              # global.json selects the Microsoft.Testing.Platform runner

# run the host from source on the samples
dotnet run --project src/Labcoat -- samples/Samples.slnx
```

Without a UI build the host still runs, but the page only says that the UI isn't built. `dotnet pack` refuses to pack
without it; `dotnet pack -p:LabcoatBuildUi=true` runs `pnpm install --frozen-lockfile` (if needed) and `pnpm run build` for you.

Every `dotnet pack` also puts an SPDX SBOM into each package (`_manifest/spdx_2.2/manifest.spdx.json`, made by
`Microsoft.Sbom.Targets`, offline; about a second per package). `-p:GenerateSBOM=false` skips it for a quick local
pack. Only the release pack (`node eng/release.mjs prepare`) adds the UI's npm SBOM (`sbom/ui.cdx.json`) and copies
the SBOMs to `artifacts/` ([docs/releasing.md, "SBOM"](docs/releasing.md#sbom)).

### Tests and code coverage

`dotnet test --solution Labcoat.slnx` runs every test project (about a minute; the end-to-end tests build a few scratch
MSTest projects in a temp directory, so the first run needs the MSTest package from the NuGet cache or feed). To run
one project or a subset, start the test app directly, e.g.
`dotnet run --project tests/Labcoat.Tests -- --filter "FullyQualifiedName~Labcoat.Tests.Api"`.

- The web app is tested in-process: `LabcoatApp.Build(...)` creates the same app `Program.cs` runs, on a
  `TestServer` (see `tests/Labcoat.Tests/Api/ApiHost.cs`); `LabcoatApp.RunAsync(args, ct)` is the whole program.
- Tests that change environment variables, the current directory, `Console` or static test hooks are
  `[DoNotParallelize]` and restore what they changed; everything else runs in parallel.
- Real servers in tests use ports 4520-4549 only.

Code coverage of the shipped assemblies (`Labcoat`, `Labcoat.Aspire.Hosting`, `Labcoat.Testing`,
`Labcoat.Testing.MSTest`, `Labcoat.Testing.TUnit`, `Labcoat.Testing.NUnit`, `Labcoat.Testing.XUnit`, `Labcoat.TestingPlatform`) must stay at **85% of lines or more** per assembly; CI fails below that and uploads the merged
report (artifact `coverage`, plus a table in the job summary). Run the same check locally:

```bash
node eng/coverage.mjs                       # build, run all test projects with coverage, print the tables
node eng/coverage.mjs --no-build --files below
node eng/coverage.mjs --project tests/Labcoat.Tests/Labcoat.Tests.csproj --filter "FullyQualifiedName~Api"
```

It prints a per-assembly and a per-file table (lines and branches) and writes `artifacts/coverage/coverage.cobertura.xml`
(merged, for ReportGenerator or an editor's coverage gutter) and `summary.md`. What is counted is set in
[`tests/CodeCoverage.xml`](tests/CodeCoverage.xml): test assemblies, generated sources (`*.g.cs`, e.g. the
`[GeneratedRegex]` output) and `[GeneratedCode]` / `[ExcludeFromCodeCoverage]` code are left out. Write tests for
behavior (assert on outcomes), not to touch lines; prefer a small internal seam (see `Explorer.Seams.cs`) over a
refactor when a process, the network or a static gets in the way.

### Working on the UI

```bash
cd ui
pnpm run dev              # http://localhost:5174, proxies /api and /ws to a host on http://localhost:4300
pnpm run mock             # same, opened with ?mock: an in-browser mock backend, no .NET needed
```

Useful mock URLs: `/?mock`, `/?mock&coverage`, `/?mock=large` (2000 tests), `/?mock&allpass`, `/?mock&buildfail`.
To use a real host on another port, start it with the dev server's origin allowed, then point the dev server at it:

```bash
LABCOAT_HISTORY_DIR=$(mktemp -d) LABCOAT_ALLOWED_ORIGINS=http://localhost:5174 \
  dotnet run --project src/Labcoat -- samples/Samples.slnx --no-open --port 4370
MSTEST_EXPLORER_URL=http://localhost:4370 pnpm run dev
```

Read [`ui/ARCHITECTURE.md`](ui/ARCHITECTURE.md) (reactivity, elements, performance rules) and
[`ui/MOTION.md`](ui/MOTION.md) (motion rules) before changing UI code.

### Checks (in `ui/`)

Each check starts its own Vite dev server (port 5300 or the next free one; `LABCOAT_CHECK_PORT` changes it) and drives
Chromium with Playwright. CI runs the first six (unit tests with coverage); `pnpm run check` runs the typecheck, the unit tests and the next three in one go.
`pnpm run gallery` starts the design-system gallery.

| Command | What it checks |
| --- | --- |
| `pnpm run typecheck` | `tsc --noEmit` (strict) over the app and the design system. |
| `pnpm test` | Vitest unit tests (happy-dom) of the app and the design system, under `ui/test/` (`test/app/**` mirrors `src/`, `test/design-system/**` mirrors `design-system/src/`). `pnpm run test:watch` while editing; `pnpm run test:coverage` enforces ≥ 85 % lines / statements / functions overall and per package (report: `ui/coverage/index.html`). |
| `pnpm run check:reactive` | Regression tests of the reactive core (`design-system/test/`). |
| `pnpm run check:a11y` | axe-core on the design-system gallery (every theme family, light and dark, open overlays) and on the app (mock backend). |
| `pnpm run check:keyboard` | Keyboard behavior of the primitives (menu, tabs, dialog, split pane, switch, segmented control, tooltip). |
| `pnpm run build` | Typecheck + production build. |
| `pnpm run check:themes` | WCAG contrast of every theme family's token pairs, and the `families.ts` swatches against the CSS. |
| `pnpm run check:styles` | Style regressions for CSS refactors: freeze a baseline before the change (`-- --update`, stored in the gitignored `ui/.styles-baseline/`), then compare. Prints per screen and element `property before → after`, changed CSS rules and a pixel-diff % per screenshot; exit 1 on any change. See `ui/ARCHITECTURE.md`, "Style regressions". |
| `pnpm run check:leaks` | Repeats expand, scroll, filter and page switches on the 2000-test mock; live effect counts must stay flat. |
| `pnpm run check:perf` | Long tasks, frame times and live effect counts on the 2000-test mock. Reports numbers; compare before/after. |
| `pnpm run check:perf:huge` | Scale check on 20,000 tests (`?mock=huge`) against the budgets in `ui/perf-budgets.json`; `-- --repeat=3` compares medians. |
| `pnpm run check:perf:many-projects` | The same on 40 projects × 300 tests (`?mock=many-projects`). |
| `pnpm run check:soak` | Accelerated watch session on `?mock=huge&fast` (40 partial + 10 full runs): heap, DOM nodes, listeners, effects must stay flat. `check:soak:long`: 200 + 50 runs, with heap snapshots. |

### Performance and soak checks

The scale and soak checks build the UI into a temp directory and serve it with `vite preview` (production timings;
`--dev` uses the dev server), drive headless Chromium and write `ui/perf-results/<name>.md` + `.json` (ignored by git).
A budget miss fails the check (`--report-only` only reports). The nightly / manual
[`Performance` workflow](.github/workflows/perf.yml) runs them on the mock backend and uploads the reports.

- **Budgets** live in [`ui/perf-budgets.json`](ui/perf-budgets.json) (per scenario: initial render, time to interactive,
  scroll frame p95, filter / status-filter / selection response, long tasks while results stream, heap; soak: growth
  after warm-up). Change a budget in the same PR as the change that needs it, with before/after numbers.
- **Timings are machine dependent.** On a busy machine compare before/after on the same machine, use `--repeat=3`, and
  read the long-task table, not one number. Headless Chromium rasterizes in software, so paint costs are higher than
  in a desktop browser.
- **Mock scenarios:** `?mock=huge` (20,000 tests, 8 projects, deep namespaces, data rows, ~1% failures, flaky and slow
  tests, 20 runs of history), `?mock=many-projects` (40 × 300). Add `&fast` for no demo pacing. The mock generates its
  data in the page: that time is reported as `mockSetupMs` and left out of the initial render. The mock sends full
  (non-lean) payloads, a worst case for the UI.
- **Real backend (local only, too slow for CI):** `node eng/perf-real.mjs` generates 8 MSTest projects × 2,500 tests
  (`eng/gen-perf-suite.mjs`, data rows included) in a temp directory, publishes `src/Labcoat` (Release) into a temp
  directory with the current UI built into its `wwwroot`, starts it on port 4560 with a temp `LABCOAT_HISTORY_DIR`, and
  measures discovery, first run, warm re-run, `testsUpdated` throughput, lean vs full WebSocket payloads, REST payload
  sizes, host RSS (and GC heap when `dotnet-counters` is on PATH), the history file, and the UI loading the full history
  and streaming a run. `--soak=20` adds watch-triggered partial runs (it edits test files of the generated suite).
  Reuse a suite / publish with `--suite=<dir> --expect=20000 --labcoat=<dir> --build-ui`; `--keep` keeps the temp files.

### Benchmarks

Micro-benchmarks measure single hot paths in isolation (time and allocations per operation) at realistic sizes. They
add to the end-to-end perf, soak and leak checks above: those tell you *that* something got slower, a benchmark tells
you *which* function and makes a before/after comparison of one change cheap. They don't gate CI. The nightly
[`Performance` workflow](.github/workflows/perf.yml) runs them, adds the tables to the job summary and uploads the
reports.

#### C# (BenchmarkDotNet)

[`benchmarks/Labcoat.Benchmarks`](benchmarks/README.md) covers the host: MTP JSON-RPC framing and parsing, the
WebSocket event serialization (`state` snapshot, `testsUpdated`, `testResults`), history.json and coverage.json save /
load, Cobertura parsing and the coverage report build / merge, impact map lookups, `git diff` parsing, the test host
environment filter, live output attribution and the per-request security checks. Inputs are generated in code
(1,000 and 20,000 tests, shaped like `?mock=huge`); nothing large is committed. Every class uses `[MemoryDiagnoser]`.

```bash
# always Release; --filter is required (no filter = an interactive menu)
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --list flat                      # what exists
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*History*' --quick     # ~1 min
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*'                     # everything, BDN's default job (long)
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*MtpParsing*' --job short
```

`--quick` is a short job (1 launch, 1 warm-up and 3 measured iterations): enough to see a 2× change, too noisy for
5%. Any BenchmarkDotNet option works as well (`--job short`, `--inProcess`, `--memory`, `--anyCategories`...).
Reports go to `BenchmarkDotNet.Artifacts/results` in the working directory (ignored by git), or to `--artifacts <dir>`.

**Before / after on your machine:** run the same filter on both versions into two directories and compare them:

```bash
git stash   # or check out the base commit
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*Coverage*' --artifacts artifacts/bdn-base
git stash pop
dotnet run -c Release --project benchmarks/Labcoat.Benchmarks -- --filter '*Coverage*' --artifacts artifacts/bdn-head
node eng/bdn-compare.mjs artifacts/bdn-base artifacts/bdn-head --threshold 5
```

`eng/bdn-compare.mjs` prints a Markdown table (mean time and allocated bytes, base → head, changes over the threshold
marked); paste it into the PR of a `perf` change. Close other heavy programs, use the default job (not `--quick`) for
small differences, and compare runs from the same machine only.

#### UI (Vitest bench)

[`ui/bench/`](ui/bench) covers the web UI with Vitest's benchmark mode in happy-dom (`ui/vitest.bench.config.ts`;
`pnpm test` and coverage never pick up a `*.bench.ts` file):

| File | What it measures |
| --- | --- |
| `reactive.bench.ts` | The reactive core: signal / computed / effect propagation through wide (1 → 20,000 effects, fan-in) and deep (1,000-level) graphs, equality cut-off, `batch()` vs unbatched writes, writes queued from inside an effect, create + dispose. |
| `dom.bench.ts` | `h()` building (static and reactive rows), attribute / class / text binding updates, `keyed` / `show` / function regions, and `list()` reconciliation at 1,000 and 5,000 rows (initial render, append, prepend, remove, swap, reverse, item updates, replace, clear). |
| `tree.bench.ts` | `src/tree.ts` at 1,000 and 20,000 tests: full build, incremental patch from a results delta (1 test, 1 %, 5 %; with and without the store's delta), flatten, duration sort, regroup by project / outcome / trait. |
| `filter.bench.ts` | Query parsing and compiling, the filter pass for plain text, `is:`, `cat:`, `msg:`, combined terms and the status chips, and typing a query with `createFilterCache` (incremental narrowing) vs a full pass per keystroke. |
| `store.bench.ts` | `Store.apply` of a `state` snapshot (1,000 / 20,000 tests) and of `testsUpdated` / `testResults` batches into 20,000 tests, one frame each (apply + publish); `applyResultPatch`. |
| `diff.bench.ts` | Assertion message parsing, word / char diffs of single values, line diffs (200 - 5,000 lines) and JSON diffs (~250 / ~2,000 lines), context folding and the diff-view DOM. |
| `ansi.bench.ts` | ANSI SGR parsing for the live console (`ansi`, `stripAnsi`), the live output buffer (64 K head + tail cap), `LiveOutputStore.apply` of `testOutput` chunks, `LineRenderer` full render vs append. |

Inputs come from the mock's scale generator (`ui/src/mock/scale.ts`, the `?mock=huge` catalog) and seeded generators
in `ui/bench/fixtures.ts`, so every run sees the same data.

```bash
cd ui
pnpm run bench                              # everything (~5 min), a table per group
pnpm run bench bench/tree                # one file
pnpm run bench bench/filter -t "typing"  # one group
BENCH_TIME=100 pnpm run bench               # shorter time budget per task (default 400 ms): quicker, noisier
pnpm run bench:ci                           # the same + bench-results/ui-bench.json (Vitest json reporter)
```

**Before / after on your machine:** Vitest 5 has no `vitest bench --compare <json>` or `--outputJson` (those were
Vitest 1-3 flags), so `scripts/bench-compare.mjs` reads the json reporter output of `bench:ci`:

```bash
git stash                                  # or check out the base commit
pnpm run bench:ci bench/tree && cp bench-results/ui-bench.json bench-results/base.json
git stash pop
pnpm run bench:ci bench/tree
pnpm run bench:compare bench-results/base.json bench-results/ui-bench.json            # every task
pnpm run bench:compare bench-results/base.json bench-results/ui-bench.json --changed  # only the changed ones
```

It prints a Markdown table (mean time per task, base → head, the change and ×-factor). A task counts as faster or slower
when the change exceeds `--threshold=<pct>` (default 10) *and* both runs' margins of error combined. With one file it
prints the results table (what CI adds to the job summary). Paste the comparison into the PR of a `perf` change.

Reading the numbers:
- **happy-dom is not a browser.** Pure logic (reactive graph, tree, filter, store, diff, buffers) runs as in the app.
  DOM-heavy tasks measure happy-dom: its `nextSibling` / `insertBefore` are O(siblings) and style writes are slow, so
  list moves, big lists and colored ANSI spans look far worse than in Chromium. Use them for before/after comparisons of
  the same task; for real frame times use `pnpm run check:perf*`. That is also why `list()` stops at 5,000 rows: the
  app's tree is virtualized, and its 20,000-test work is benchmarked without DOM in `tree.bench.ts` / `filter.bench.ts`.
- Vitest runs the code through its module runner, which adds a small cost to calls across modules (it warns about
  "module export getters"; the warning is turned off in the bench config). It is the same in every run, so comparisons
  hold; absolute numbers are a little pessimistic.
- Compare runs from the same machine only, with nothing heavy running; rerun a surprising result.

Adding a benchmark: a `*.bench.ts` file under `ui/bench/` with `test("group", async ({ bench }) => { await
bench.compare(bench("a", fn), bench("b", fn), QUICK) })` (a single task: `await bench("a", fn).run(QUICK)`). Use
`QUICK` (time-boxed) for cheap tasks and `fixed(n)` (a fixed number of samples) when a task has an expensive
`beforeEach`, since tinybench samples until the *measured* time reaches the budget. Generate inputs with
`catalog(n)` and the other helpers in `fixtures.ts`; dispose roots you create.

### Docs

User docs live in [`docs/`](docs) (start at the [README](README.md#documentation)). Write plain, direct sentences and
use commands you have run. `node eng/check-links.mjs` checks every relative link and image in the Markdown files (CI
runs it). Screenshots go in `docs/images/`; [`docs/images/SHOTLIST.md`](docs/images/SHOTLIST.md) lists each one with
the exact state to capture.

### Samples

`samples/Samples.slnx` contains deliberate failures and skipped tests (that is what Labcoat shows off), so it is not part
of `Labcoat.slnx` and `dotnet test` doesn't run it. Expected results, also asserted by the CI smoke test
([`eng/smoke-test.mjs`](eng/smoke-test.mjs)):

| Project | Tests | Failed | Skipped |
| --- | --- | --- | --- |
| `SampleTests` (MSTest) | 17 | 7 | 1 |
| `NUnitSample` | 6 | 1 | 1 |
| `XUnitV3Sample` | 6 | 1 | 1 |
| `TUnitSample` | 6 | 1 | 1 |

When you change a sample, update these numbers here and in `.github/workflows/*.yml`. The samples keep their package
versions in their own project files (no central package management) so they read like a user's project.

To smoke test a packed tool by hand:

```bash
dotnet pack Labcoat.slnx -c Release -o artifacts
dotnet tool install Labcoat --tool-path .tools --add-source artifacts --version 1.0.0
node eng/smoke-test.mjs --tool .tools/labcoat --port 4500 --total 35 --failed 10 --skipped 4 -- samples/Samples.slnx
```

## Coding conventions

`.editorconfig` describes the style; it is not enforced in the build, and existing code shouldn't be reformatted just
to satisfy a tool. Match the code around you.

- **C#:** file-scoped namespaces, nullable enabled, `var`, `_camelCase` private fields, `s_camelCase` private static
  fields, `PascalCase` constants, 4-space indent, ~120 columns. CI builds with `-warnaserror`.
- **TypeScript:** strict mode, no UI framework: custom elements from the design system, `h()` for DOM, signals for
  state (never import `alien-signals` directly). 2-space indent, double quotes, semicolons.
- **CSS:** component CSS uses semantic `--lc-*` tokens only, never raw colors (see the design-system README).
- **Comments** explain *why*; keep them up to date when behavior changes.
- **Docs:** update the README, `docs/` and the package READMEs when options or behavior change. `CHANGELOG.md` is
  generated from the commit messages: don't edit it, write a good PR title instead (see below).
- **Dependencies:** package versions for `src/` and `tests/` live in `Directory.Packages.props` (central package
  management). Library dependencies (`Labcoat.Testing*`, `Labcoat.TestingPlatform`) are minimum versions for users:
  don't raise them without a reason. Updates come from Renovate (see [Dependency updates](#dependency-updates)).

## The design system

The UI is built on [`ui/design-system`](ui/design-system/README.md): a tiny reactive core, a typed `h()` DOM builder, a
custom element base class, design tokens and reusable `lc-*` components. Run its gallery with
`pnpm run gallery -w design-system` (http://localhost:5200, `?theme=<family>-<light|dark>`). New reusable UI goes into the
design system with a gallery section; app-specific components live in `ui/src/components`.

### Adding a theme family

1. Create `ui/design-system/src/themes/<id>.css` that assigns **every** semantic `--lc-*` token for
   `:root[data-theme-family="<id>"]` (light) and `:root[data-theme-family="<id>"][data-theme="dark"]`. Start from an
   existing family (for example `catppuccin.css`, which declares the palette once and swaps it for dark).
2. Import it in `ui/design-system/src/styles.css` next to the other themes.
3. Add an entry to `THEME_FAMILIES` in `ui/design-system/src/themes/families.ts` (id, label, flavors, description,
   swatches). The app's theme picker, command palette and the gallery read this list.
4. Run `pnpm run check:themes` (contrast + swatches) and `pnpm run check:a11y`, and look at the gallery and the app in
   both modes.

Details and the token reference: [design-system README, "Themes and tokens"](ui/design-system/README.md#themes-and-tokens).
User-facing theme docs: [docs/themes.md](docs/themes.md) (add the family there, and to the screenshot list).

## Pull requests

- Keep a PR focused on one change; describe what and why, and how you tested it (the PR template has a checklist).
- The PR title is the commit message: PRs are squash merged, and the title must follow the
  [commit message format](#commit-messages-and-releases) (checked by CI). It decides whether the merge releases a new
  version and is what the changelog says, so write it for users.
- UI changes: include before/after screenshots in light and dark mode.
- CI must pass: UI checks, build + tests on Linux, Windows and macOS, and the smoke test of the packed tool.

## Commit messages and releases

Releases are automatic. [semantic-release](https://github.com/semantic-release/semantic-release) reads the commit
messages on `main` since the last release, works out the next version, writes `CHANGELOG.md` and publishes the packages.
For that to work, commit messages follow the [Angular commit format](https://github.com/angular/angular/blob/main/contributing-docs/commit-message-guidelines.md):

```text
<type>(<scope>): <subject>          ← the header: max. 100 characters, imperative, lower case, no period

<body: what and why, optional>

<footer: BREAKING CHANGE: ..., Fixes #123, optional>
```

PRs are squash merged with the **PR title** as the commit message, so in practice the PR title is what has to follow the
format (CI checks it; the commits inside a PR are only checked as a warning).

| Type | Use for | Release |
| --- | --- | --- |
| `feat` | A new feature or option users can see. | minor (1.**2**.0) |
| `fix` | A bug fix users can notice. | patch (1.1.**1**) |
| `perf` | A faster or lighter implementation. | patch |
| `revert` | Reverting an earlier commit (`revert: feat(ui): ...`). | patch |
| `docs` | Documentation only. | none |
| `refactor` | Code change that neither fixes a bug nor adds a feature. | none |
| `test` | Adding or fixing tests. | none |
| `build` | Build system, packaging, dependencies of tooling. | none |
| `ci` | GitHub Actions workflows. | none |
| `style` | Formatting, no code change. | none |
| `chore` | Anything else that users don't see. | none |

Scopes are optional; when you use one, pick from: `ui` (the web app), `design-system`, `host` (the Labcoat server and
`dotnet tool`), `cli` (command-line options), `aspire` (Labcoat.Aspire.Hosting), `testing` (Labcoat.Testing and its adapters), `tpext`
(Labcoat.TestingPlatform), `docs`, `ci`, `deps`, `samples`. Another scope is a warning, not an error.

Examples:

```text
feat(ui): add a duration filter to the test tree
fix(host): keep the warm test host alive after a build error
perf(ui): virtualize the test tree for large solutions
docs: explain the coverage threshold
fix(deps): update dependency aspire.hosting to 13.7.0
```

**Breaking changes** add a `!` after the type or scope and/or a `BREAKING CHANGE:` footer that tells users what to do:

```text
feat(cli)!: rename --theme to --ui-theme

BREAKING CHANGE: the --theme option is now --ui-theme; update scripts and launch profiles.
```

A breaking change releases the next **major** version (1.4.2 → 2.0.0, see
[docs/releasing.md](docs/releasing.md#versioning)).

**Public API changes of the library packages** (`Labcoat.Aspire.Hosting`, `Labcoat.Testing*`,
`Labcoat.TestingPlatform`) are checked by package validation: `dotnet pack` compares each package with an earlier
release on nuget.org and fails on a removed or changed public type or member. Adding API is fine. To break it on
purpose: mark the commit as a breaking change (above) and commit the `CompatibilitySuppressions.xml` that
`dotnet pack <project> -c Release --no-build -p:ApiCompatGenerateSuppressionFile=true` writes next to the project, in
the same PR. Details, and new packages: [docs/releasing.md, "Package validation"](docs/releasing.md#package-validation).

**Local check (optional).** `pnpm install` in the repository root installs the release tooling and a `commit-msg` git
hook that runs commitlint on every commit (skip it once with `git commit --no-verify`, remove it with
`rm .git/hooks/commit-msg`, or don't install it: `SKIP_INSTALL_SIMPLE_GIT_HOOKS=1 pnpm install`). Without the hook,
check a message by hand: `echo "feat(ui): add a duration filter" | pnpm exec commitlint`.

### How a release happens

1. A PR is squash merged into `main`.
2. CI runs on `main`. When every job passed, its last job, `release`, starts the Release workflow
   (`.github/workflows/release.yml`) for that commit, which runs semantic-release (if `main` has moved on meanwhile,
   the newer commit's CI run releases instead).
3. No `feat`, `fix`, `perf`, `revert` or breaking-change commit since the last tag: nothing happens. Otherwise
   semantic-release
   - computes the version, e.g. 1.3.0, and writes its release notes to the top of `CHANGELOG.md`;
   - sets `<VersionPrefix>` and the package validation baseline (the previous release) in `src/Directory.Build.props`,
     builds the UI and runs `dotnet pack` with `-p:Version=1.3.0` for the nine packages, which also checks the public
     API against the previous release (`node eng/release.mjs prepare 1.3.0 --baseline=1.2.1`);
   - commits `CHANGELOG.md` and `Directory.Build.props` as `chore(release): 1.3.0 [skip ci]` and tags it `v1.3.0`;
   - creates the GitHub release with the notes, the `.nupkg` / `.snupkg` files and their SBOMs, then pushes the
     packages (with symbols) to nuget.org;
   - comments on the PRs and issues that went into the release.
   Then the Release workflow attests the build provenance of the packages and their SBOMs, and each package's SBOM
   (`gh attestation verify`, see [docs/releasing.md](docs/releasing.md#verifying-packages)).
4. Pushes to `next` or `beta` (optional branches) release prereleases such as `1.3.0-beta.1` the same way (NuGet and
   GitHub prereleases).

Don't change `<VersionPrefix>`, `CHANGELOG.md` or create `v*` tags by hand.

**Dry run.** Actions → Release → Run workflow (dry run is the default) prints the next version and its release notes
without publishing. Locally, with push access to the repository:

```bash
pnpm install
GITHUB_TOKEN=$(gh auth token) pnpm exec semantic-release --dry-run --no-ci
```

To check the packing step without releasing: `node eng/release.mjs prepare 0.0.0-local.1` (packs into `artifacts/`;
it also sets `<VersionPrefix>`, so revert `src/Directory.Build.props` afterwards).

More on releases (what a release contains, verifying packages, prereleases): [docs/releasing.md](docs/releasing.md).

### Dependency updates

[Renovate](https://docs.renovatebot.com) opens the dependency update PRs ([`.github/renovate.json5`](.github/renovate.json5)),
weekly, with titles in the format above, so merging one releases only when the dependency ships:

| PR title | For | Release |
| --- | --- | --- |
| `fix(deps): ...` | NuGet packages of `src/`, runtime dependencies of the UI (bundled into the tool) | patch |
| `build(deps): ...` | samples, the .NET SDK, build-only packages, lock file refreshes | none |
| `build(deps-dev): ...` | npm `devDependencies`, test packages, the release tooling | none |
| `ci(deps): ...` | GitHub Actions | none |

Vulnerable dependencies get a separate PR right away, titled `... [security]` (label `security`): GitHub's Dependabot
alerts are the signal, Renovate opens the fix (Dependabot itself opens no PRs). PRs labeled `needs-review`
(`alien-signals`, the test platform minimum versions) need a closer look; nothing is merged automatically. Review a
Renovate PR like any other: CI must pass, and check the title before squash merging. The Dependency Dashboard issue
lists everything pending.
