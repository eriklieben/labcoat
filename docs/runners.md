# Test runners

Labcoat runs .NET test projects on Microsoft.Testing.Platform (MSTest, NUnit, xUnit v3, TUnit) itself. Other test
frameworks come as **runner packages**: [`Labcoat.Vitest`](../src/Labcoat.Vitest/README.md) runs the
[Vitest](https://vitest.dev) tests of JavaScript and TypeScript projects. Their tests show up in the same tree as the
.NET tests, with the same run, watch, history and coverage features.

## Choosing the runners

| Runner | What it runs |
| --- | --- |
| `dotnet` | .NET test projects on Microsoft.Testing.Platform. Built in. `mstest`, `nunit`, `xunit`, `tunit`, `mtp` and `.net` mean the same. |
| `vitest` | Vitest projects. Needs the `Labcoat.Vitest` package. |

Without a choice Labcoat uses `dotnet` plus every runner package it finds (see [below](#where-labcoat-finds-runner-packages)).
So without runner packages nothing changes: only .NET test projects.

To choose, name the runners (comma-separated). The first one set wins:

| Where | Example |
| --- | --- |
| Command line | `labcoat --runners dotnet,vitest` |
| Environment | `LABCOAT_RUNNERS=vitest` |
| [Settings file](configuration.md#settings-files) | `"runners": ["dotnet", "vitest"]` (or `"dotnet,vitest"`) |
| Aspire AppHost | `.WithRunners("vitest")`, `labcoat.Runners.Add("vitest")`, or `"Labcoat": { "Runners": ["vitest"] }` in appsettings.json |

A named runner replaces the default: `--runners vitest` finds and runs only Vitest projects. A named runner that isn't
installed is fetched from NuGet (see below); when that fails too, Labcoat stops with an error that says which package
to add.

In an AppHost each Labcoat resource has its own runners:

```csharp
builder.AddLabcoat("tests-dotnet").WithRunners("dotnet");             // .NET only
builder.AddLabcoat("tests-web").WithTestProject("../MyApp.Web")
       .WithRunners("vitest");                                        // Vitest only
builder.AddLabcoat("tests");                                          // both (the default with Labcoat.Vitest referenced)
```

## Where Labcoat finds runner packages

A runner package is a folder with a `runner.json` (see [Writing a runner](#writing-a-runner)). Labcoat looks in, in
order:

1. `runners/` next to Labcoat itself. In an Aspire AppHost, a `Labcoat.Vitest` package reference copies the runner to
   `bin/<config>/<tfm>/labcoat/runners/vitest/`, next to the Labcoat that `Labcoat.Aspire.Hosting` copies there.
   `AddLabcoat` also passes that folder as `LABCOAT_RUNNER_PATH`, for when Labcoat runs through `dotnet tool exec`.
2. `--runner-path <dir>` (repeatable).
3. `LABCOAT_RUNNER_PATH`: folders separated like `PATH`.

Each folder may hold a `runner.json` itself or one runner per subfolder. When two folders have a runner with the same
id, the first one wins.

The `labcoat` tool can't reference other packages. When `--runners` (or `LABCOAT_RUNNERS`, or `"runners"`) names a
runner that isn't found, Labcoat downloads `Labcoat.<Id>` (`Labcoat.Vitest` for `vitest`) in its own version into the
NuGet global packages folder (`$NUGET_PACKAGES`, else `~/.nuget/packages`) and uses it from there. The download is a
`dotnet restore` of a scratch project with a `PackageDownload`, using the nearest `nuget.config` of the current
directory, so private feeds and their credentials work. The next start finds it in the packages folder without a
download.

```bash
labcoat --runners dotnet,vitest     # first start: fetches Labcoat.Vitest, then runs both
labcoat --runner-path ~/src/labcoat/src/Labcoat.Vitest/runner --runners vitest   # a runner from a checkout
```

## Vitest

### Requirements

- Node.js 20 or later on `PATH`.
- Vitest 5 or later installed in the project (`npm install`, `pnpm install`, `yarn`). Labcoat uses the project's own
  Vitest, found like `npx vitest` would (the project's `node_modules`, or a parent's in a workspace).
- For coverage: a Vitest coverage provider, such as `@vitest/coverage-v8`.

### Which projects

A folder is a Vitest project when it has a `vitest.config.*` (`.ts`, `.mts`, `.cts`, `.js`, `.mjs`, `.cjs`) or a
`package.json` that depends on `vitest` (`dependencies`, `devDependencies`, `peerDependencies` or
`optionalDependencies`). The project's name is the folder's name.

- A directory is scanned for such folders (skipping `node_modules`, `bin`, `obj` and dot-folders). The scan doesn't
  look below a project it found: a monorepo with a root Vitest config (`test.projects`) is one project, run by its root
  config. To list the packages of a monorepo separately, pass their folders.
- A solution doesn't list JavaScript projects, so for a `.sln` / `.slnx` / `.slnf` its folder is scanned too. Without
  paths Labcoat picks the nearest solution, as for .NET (with only non-.NET runners: the current directory).
- An explicitly given `package.json` or `vitest.config.*` is used as is.
- Labcoat notices new Vitest projects (a new `package.json` or `vitest.config.*` under the workspace) like new .NET
  projects.

### The tree

| Level | From Vitest |
| --- | --- |
| Project | the project folder |
| Namespace | the test file, relative to the project (`test/orders.test.ts`) |
| Class | the `describe` blocks around the test (`receiptLines > currency`); none for a test at the top of a file |
| Test | the test's name |

The rows of a `test.each` are grouped under the name template (`add(%i, %i) = %i`), like the data rows of a .NET data
driven test. A test's id is its file and full name (`test/orders.test.ts > receiptLines > shows the currency`), so its
history follows it when tests above it are added or moved; tests with the same full name in one file get ` #2`,
` #3`, … in file order. Vitest tags are traits (`cat:` in the search). With several Vitest projects in one config
(`test.projects`), each test has a `Project` trait.

### What works

- Discovery, run all, run a selection (a file, a `describe`, one test or one `test.each` row), cancel.
- A warm test host: one Vitest process per project stays running. Changed source files are reloaded before each run;
  a change to `package.json`, a lock file, `tsconfig.json` or the Vitest config starts a new process.
- Live console output of running tests (`console.log` and friends, per test).
- Failures: the message, an expected/actual diff for `toEqual` and friends, and the stack frames in your source files
  (source-mapped, clickable). Frames inside `node_modules` are left out.
- Timeouts (`Test timed out in 5000ms`) show as **Timed out**; `test.skip` and `test.todo` as skipped.
- Coverage, with the coverage settings of your Vitest config (`test.coverage`: provider, include, exclude). Labcoat
  only chooses where the report goes (Cobertura). `--coverage-include` / `--coverage-exclude` still filter the report.
  The coverage tab shows "vitest.config.ts (Vitest settings)" as the project's settings.
- Watch mode: a changed test file reruns its tests; any other change under the project reruns all of the project's
  tests.
- History, flaky detection, quarantine, run compare, repeat, exports (JUnit, HTML, LCOV, Cobertura) and `--run`, as for
  .NET projects.

### Limitations

- Debug isn't supported: the debug command says so. Debug the test in your editor.
- No per-test Aspire traces.
- The impact map (smart watch, **Run affected**) has no data for Vitest projects: a change to a source file that isn't
  a test file runs all of the project's tests.
- Discovery loads the test files, as `vitest run` does, so the first discovery of a project takes about as long as
  running it. After that only new and changed test files are loaded again (runs refresh the files they load too). A test
  file whose tests are made from another file (a shared table, a loop over fixtures) keeps its list until it runs or
  changes, or the test host restarts.
- Vitest's browser mode needs its browser provider installed (Playwright or WebdriverIO), as for `vitest run`.

## Writing a runner

A runner package is a folder with a `runner.json` and a test host. Labcoat starts the host for each project and talks
to it with the Microsoft.Testing.Platform server-mode protocol it uses for .NET test apps
([protocol.md](protocol.md#mtp-server-mode-what-we-observed)). `Labcoat.Vitest` ([`src/Labcoat.Vitest/runner`](../src/Labcoat.Vitest/runner))
is the reference: a Node.js script without dependencies.

### runner.json

```json
{
  "id": "vitest",
  "displayName": "Vitest",
  "command": ["node", "{runner}/bridge.mjs"],
  "configFiles": ["vitest.config.ts", "vitest.config.mts", "vitest.config.js", "vitest.config.mjs"],
  "packageDependencies": ["vitest"],
  "sourceExtensions": [".ts", ".tsx", ".js", ".jsx", ".mjs", ".json"],
  "coverage": true
}
```

| Key | Meaning |
| --- | --- |
| `id` | The name in `--runners`, `LABCOAT_RUNNERS` and `TestProject.runner`. Lowercase; not `dotnet`. Required. |
| `displayName` | The name in messages and the coverage tab. Default: the id. |
| `command` | The host's command line. The first item is looked up on `PATH` (with `PATHEXT` on Windows); `{runner}` is replaced by the folder of the `runner.json`. Required. |
| `configFiles` | A folder with one of these files is a project. |
| `packageDependencies` | A folder whose `package.json` depends on one of these packages is a project. |
| `sourceExtensions` | The files watch mode and the up-to-date checks look at. |
| `coverage` | The host supports the coverage arguments below. Default `false`. |
| `version` | Informational. |

### The test host

Labcoat starts it in the project's folder:

```text
<command> --project <package.json or config file> --server --client-host localhost --client-port <port>
          [--coverage --coverage-output-format cobertura --coverage-output <file>]
```

- It connects to `localhost:<port>` and speaks JSON-RPC 2.0 framed with `Content-Length` headers.
- Requests: `initialize`, `testing/discoverTests`, `testing/runTests` (all tests, or `tests: [{ uid, display-name }]`);
  notifications `$/cancelRequest` (answer the request with error `-32800`) and `exit`. Requests come one at a time on
  one connection, which stays open between runs (the warm host). Exit when the connection closes.
- Results are `testing/testUpdates/tests` notifications with test nodes, ended by one with `changes: null`, before the
  request's response. A run sends an `in-progress` node when a test starts (with `standardOutput` /
  `standardError` so far for live output) and a final one (`passed`, `failed`, `skipped`, `timed-out`) with
  `time.duration-ms`, `error.message`, `error.stacktrace` and the output.
- A subset run must report only the requested tests. A new process may get a subset without a discovery first (coverage
  runs start a new process): the ids must work across processes.
- Besides the MTP node properties a runner sets the tree levels itself, because file and test names hold dots and
  parentheses that `location.type` / `location.method` can't carry:

  | Property | Meaning |
  | --- | --- |
  | `labcoat.namespace` | The namespace level (Vitest: the test file). When set, Labcoat uses the three properties instead of `location.type` / `location.method`. |
  | `labcoat.class` | The class level; absent = the test sits directly under the namespace. |
  | `labcoat.method` | The method level. Tests with the same namespace, class and method are grouped as data rows. |

- Failures: `error.stacktrace` lines in the .NET form (`   at <function> in <file>:line <n>`) become links in the UI.
  An expected/actual pair in the message as `Expected:<…>. Actual:<…>.` gets the diff view.
- Coverage: with `--coverage`, write Cobertura to the `--coverage-output` path by the time the run request is answered,
  and list it in the result's `attachments` (`{ "uri": "<file>" }`).
