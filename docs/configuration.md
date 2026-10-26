# Configuration

Labcoat works without configuration. This page lists what you can change: command-line options, environment
variables, settings files, where data is kept, and the settings in the UI (and how to give them defaults). For the Aspire integration's options see
[aspire.md](aspire.md), for the `--labcoat` extension [standalone.md](standalone.md#the---labcoat-extension-preview).

## Command line

```text
labcoat [paths...] [options]
```

| Option | Meaning |
| --- | --- |
| `paths` | `.csproj`, `.sln`, `.slnx`, `.slnf` files or directories (scanned recursively). Default: the test project in the current directory, else the nearest `.slnx` or `.sln` walking up to the repository root, else the current directory. |
| `-p`, `--port <n>` | HTTP port. Default 4300; when that is busy the next free port is used. An explicit port is used as is and fails when busy. |
| `--no-open` | Don't open the browser. It is also not opened when `CI` is set or no display is available. |
| `-w`, `--watch` | Start in watch mode. |
| `--coverage` | Start with code coverage collection on. |
| `--coverage-settings <file>` | The coverage settings file for every test project (a Microsoft.CodeCoverage configuration or a `.runsettings` with a CodeCoverage section). Relative: next to each test project, then the current directory. Default: each project's own settings, else Labcoat's defaults (see [Which code counts](features.md#which-code-counts)). |
| `--coverage-include <globs>` | Modules or path globs to count although Labcoat's coverage defaults leave them out, e.g. `Labcoat.*`. Comma-separated, repeatable. |
| `--coverage-exclude <globs>` | Modules or path globs never to count, e.g. `MyApp.Tools,**/Migrations/**`. Comma-separated, repeatable. |
| `--traces` | Start with per-test traces on. Only has an effect when hosted by an Aspire AppHost. A choice made in the UI is remembered per workspace and wins. |
| `--trace-filter <expr>` | While per-test traces are on, trace only the tests that match `<expr>`, in the UI's search syntax: `cat:Integration`, `trait:Kind=Slow`, `proj:Orders.Tests` or name text; every term must match (see [Trace only some tests](aspire.md#trace-only-some-tests)). An invalid filter stops Labcoat with an error. |
| `--root <dir>` | An extra directory whose source files the UI may show, for example the solution root for stack-trace frames outside the test projects. Repeatable. |
| `--impact-map <mode>` | Granularity of the [impact map](features.md#the-impact-map): `class`, `test` or `auto` (the default). |
| `--max-parallel <n>` | Test projects discovered and run at the same time (1-64). Default: the number of processors, at most 4. Builds are one `dotnet build` for all projects that need one either way (see [Parallel projects, slow tests and timeouts](#parallel-projects-slow-tests-and-timeouts)). |
| `--hang-threshold <duration>` | A test running this long is shown as **possibly hanging** (default `60s`; `0` turns it off). |
| `--test-timeout <duration>` | Per-test timeout: a test running longer is marked **Timed out** and its project's run is cancelled. Off by default. |
| `--affected-base <ref>` | The base of **Run affected → Branch vs …** (see [features.md](features.md#run-affected-tests)), for example `origin/develop`. Default: the remote's default branch (`origin/HEAD`), else `origin/main`, `origin/master`, `main` or `master`. |
| `--affected [base]` | With `--run`: only the tests the changes in git affect: `working`, `branch` (the default, against `--affected-base`) or a commit range such as `origin/main..HEAD` (`--affected=<ref>` for one commit). See [Running only the affected tests in CI](standalone.md#running-only-the-affected-tests-in-ci). |
| `--impact-map-file <file>` | With `--run`: the [impact map](features.md#the-impact-map) file that `--affected` loads or `--build-impact-map` saves (`.gz` compresses it). |
| `--build-impact-map` | With `--run`: run the impact-map pass and save the map to `--impact-map-file`. |
| `--config <file>` | The settings file to use instead of the nearest `.labcoat.json` / `.config/labcoat.json` (see [Settings files](#settings-files)). Fails when the file is missing or not valid JSON. |
| `--no-user-config` | Ignore the user settings file (`~/.labcoat.json`). |
| `--theme <name>` | Default theme: `catppuccin`, `aspire`, `github`, `one`, `dracula`, `solarized`, `gruvbox`. |
| `--mode <mode>` | Default mode: `system`, `light`, `dark`. |
| `--editor <id>` | Default editor for "open in editor": `vscode`, `rider`, `visualstudio`. |
| `--motion <m>` | Default motion: `system`, `reduced`, `off`. |
| `--lock <names>` | Lock UI settings (comma-separated names from the [table below](#ui-settings), or `all`). |
| `-v`, `--verbose` | Log more of the host (ASP.NET Core) at Debug level, like `LABCOAT_LOG_LEVEL=debug`. |
| `--log-file <path>` | Also write the log to a file: Labcoat's own lines (the Output panel's) and the host's at the log level. The file is capped at 10 MB, then moved to `<path>.1` (one older copy is kept). `auto` writes `labcoat.log` in the [workspace's data folder](#where-labcoat-keeps-its-data). |
| `--diagnostics` | Print a [diagnostics report](#diagnostics-for-a-bug-report) for a bug report, copy it to the clipboard when the output is a terminal, and exit. |
| `--version` | Print the version. |
| `-h`, `--help` | Show the help. |

With `dnx`, options go after the package id: `dnx Labcoat samples/Samples.slnx --port 5000`. `dnx` keeps `--help` and
`--version` for itself, so use `dnx Labcoat -- --help`.

Examples:

```bash
labcoat                                   # the current project or solution
labcoat tests/MyApp.Tests --watch         # one project, watch mode
labcoat MyApp.slnx --port 5000 --no-open  # a solution on a fixed port
labcoat src tests --coverage              # every test project under two directories
labcoat --theme dracula --mode dark       # UI defaults for this start
```

The value options also accept `--name=value` (`--theme=dracula`). An invalid value stops Labcoat with an error.

## Environment variables

| Variable | Meaning |
| --- | --- |
| `PORT`, `ASPNETCORE_URLS` | Listen address. `--port` wins over `PORT`, which wins over `ASPNETCORE_URLS`. |
| `LABCOAT_DEFAULT_PORT` | The first port tried when no port is given (default 4300). |
| `LABCOAT_HISTORY_DIR` | Where history, past runs, the full coverage report and per-workspace settings are kept (see [below](#where-labcoat-keeps-its-data)). |
| `LABCOAT_NAME` | The workspace name shown in the UI (default: the solution or project name). |
| `LABCOAT_ALLOWED_HOSTS` | Extra host names the server answers to, comma-separated. The default allows `localhost`, `*.localhost`, `127.0.0.1`, `[::1]` and the listen host. `*` turns the check off. |
| `LABCOAT_ALLOWED_ORIGINS` | Extra browser origins that may send commands, for example `http://localhost:5174` for a Vite dev server. |
| `LABCOAT_TOKEN` | When set, scripts (requests without an `Origin` header) must send it as `X-Labcoat-Token` on `POST /api/*` and `/ws`. The Aspire integration sets it. |
| `LABCOAT_CANCEL_GRACE_SECONDS` | How long a cancelled run may take before the test host is killed (default 10). |
| `LABCOAT_VERIFY_WARM` | `1` keeps a warm test host for projects that use [Verify](features.md#snapshot-testing). By default they get a new test host per run, because Verify allows each snapshot once per process; use it when your tests call `VerifierSettings.DisableRequireUniquePrefix()`. |
| `LABCOAT_PATH` | For the `--labcoat` extension: the `Labcoat.dll` or `labcoat` executable to start. |
| `LABCOAT_UI_SETTINGS` | UI defaults as JSON, the same object as `"ui"` in a [settings file](#settings-files), e.g. `{"theme":"dracula","locked":["theme"]}`. For scripts, containers and CI. |
| `LABCOAT_NO_USER_CONFIG` | `1` ignores the user settings file, like `--no-user-config` (keeps CI and scripted runs hermetic). |
| `LABCOAT_AFFECTED_BASE` | The base ref of **Run affected → Branch vs …**, like `--affected-base`. `Labcoat.Aspire.Hosting` sets it from `AffectedBase` / `WithAffectedBase("origin/develop")`. |
| `LABCOAT_MAX_PARALLEL`, `LABCOAT_HANG_THRESHOLD`, `LABCOAT_TEST_TIMEOUT` | Like `--max-parallel`, `--hang-threshold`, `--test-timeout` (the command line wins). `Labcoat.Aspire.Hosting` sets them from `WithMaxParallelProjects` / `WithHangThreshold` / `WithTestTimeout`. |
| `LABCOAT_IMPACT_MAP` | `class`, `test` or `auto`, like `--impact-map`. `Labcoat.Aspire.Hosting` sets it from `ImpactMap` / `WithImpactMap("test")`. |
| `LABCOAT_TRACE_FILTER` | The tests to trace, like `--trace-filter` (which wins). `Labcoat.Aspire.Hosting` sets it from `TestTracingFilter` / `WithTestTracing(filter: "cat:Integration")`. An invalid filter stops Labcoat with an error. |
| `LABCOAT_LIVE_OUTPUT` | `0` (or `false`) turns [live output](features.md#live-console-output) of running tests off: no extra test host options or variables, no `liveOutput` events. On by default. |
| `LABCOAT_LOG_LEVEL` | The host's log level: `trace`, `debug`, `information`, `warning` (the default), `error`, `critical` or `none`. `--verbose` lowers it to `debug`. |
| `LABCOAT_LOG_FILE` | Like `--log-file` (which wins). |
| `LABCOAT_APPHOST_UI_SETTINGS` | Set by `Labcoat.Aspire.Hosting`: the UI settings from the AppHost (see [aspire.md](aspire.md#ui-settings)). |
| `LABCOAT_TEST_ENV_PASSTHROUGH` | Variables the test processes keep although they look like variables of an Aspire run, separated by `;` or `,`; `NAME*` matches a prefix. `Labcoat.Aspire.Hosting` sets it from `WithPassThroughEnvironment` / `WithTestEnvironment` / `PassThroughEnvironment`. See [aspire.md](aspire.md#the-tests-environment). |
| `BROWSER` | The command used to open the browser (default: `xdg-open`, `open` or `start`). |
| `NO_COLOR` | Turn off colored console output. |

Test processes get Labcoat's environment without the variables of an Aspire run and Labcoat's own settings (`ASPIRE_*`,
`DCP_*`, `DEBUG_SESSION_*`, the Labcoat resource's `OTEL_*`, `PORT`, `ASPNETCORE_URLS`, `LABCOAT_TOKEN`, ...; the full
list is in [aspire.md](aspire.md#the-tests-environment)), unless `LABCOAT_TEST_ENV_PASSTHROUGH` names them.

Labcoat sets these on the test processes it starts, so your tests can react to them:

| Variable | Meaning |
| --- | --- |
| `LABCOAT_TEST_HOST=1` | Set on every test host Labcoat starts. |
| `LABCOAT_WARM_HOST=1` | The test host is kept alive between runs. See [aspire.md](aspire.md#integration-tests-and-warm-test-hosts). |
| `LABCOAT_OTLP_ENDPOINT`, `LABCOAT_OTLP_PROTOCOL`, `LABCOAT_OTLP_HEADERS` | Only while per-test traces are on: where to send them. Used by `Labcoat.Testing`. |
| `LABCOAT_OTLP_FILTER` | Only while per-test traces are on and a trace filter is set (`--trace-filter`, `LABCOAT_TRACE_FILTER`, `traceFilter`): `Labcoat.Testing` traces only the tests that match it. |

## Where Labcoat keeps its data

History, past runs, the saved coverage report and per-workspace settings (trace collection on or off, quarantined
tests, the chosen target framework) are stored in one folder per **workspace** under:

| OS | Default folder |
| --- | --- |
| Linux | `~/.local/share/labcoat` |
| macOS | `~/Library/Application Support/labcoat` |
| Windows | `%LOCALAPPDATA%\labcoat` |

The workspace is the git repository the test projects are in; without git, the nearest `.slnx` / `.sln` above them
(not above your home or temp directory); else their common directory. Its folder is named after it plus a hash of its
full path, e.g. `labcoat/shop-1a2b3c4d5e6f7a8b/` (case-insensitive on Windows and macOS, so `C:\Repo` and `c:\repo` are
one workspace); `workspace.json` in it says which path it belongs to. So the history doesn't depend on which projects
you open: `labcoat`, `labcoat tests/Shop.Tests` and a solution with a project more or less all use the same folder.
Inside it each test's history, the quarantined tests and each project's part of the saved coverage report are kept by
the project's path relative to the workspace root (not by its id: two projects with the same file name are `Name` and
`Name-2` depending on the order they are found in, which can change), so:

- an added project starts with an empty history, the others keep theirs; this holds for a project found while Labcoat
  runs too;
- a removed or renamed project's history (and coverage) stays in the folder for 30 days (in case it comes back, also
  while Labcoat runs), then it is dropped.

Each folder holds `history.json` (results, history and past runs), `settings.json`, `coverage.json` (the Coverage
page's report) and, with `--log-file auto`, `labcoat.log`. Early versions before 1.0 used one folder per set of
project paths (16 hex characters): the first start in a workspace merges those of its folders whose projects are
all inside the workspace (on conflicts the newer result wins; `settings.json` and `coverage.json` come from the newest
folder) and leaves a `migrated.json` in each of them. They are not deleted; remove them when you like.

**Two Labcoats on one workspace.** Only one Labcoat at a time writes a workspace's folder (an `instance.lock` file the
operating system releases when that Labcoat ends, also after a crash). A second one shows the saved history but logs
`Another Labcoat is using this workspace's history: pid 12345` and saves nothing (history, settings, coverage report)
while the first one runs; once it has ended, the second one takes over on its next save and merges what the first one
saved meanwhile.

Set `LABCOAT_HISTORY_DIR` to use another folder, for example a temporary one on CI. Delete a workspace's folder to
reset its history. Nothing is written to your repository.

## Diagnostics for a bug report

`labcoat --diagnostics` (in the workspace, with the same arguments you start Labcoat with), **Copy diagnostics** in the
command palette (Ctrl+K) or **Settings → About → Copy diagnostics** give a markdown report to paste into a
[bug report](https://github.com/eriklieben/labcoat/issues/new?template=bug_report.yml): the Labcoat version and how it
was started, the OS, the .NET runtime and SDK, the workspace and its data folder, the test projects with their target
frameworks, test framework, Microsoft.Testing.Platform and extension (coverage, TRX, ...) package versions (from
`obj/project.assets.json`), the settings in effect, the `LABCOAT_*` and related environment variables and the last 100
log lines. The values of secrets (`LABCOAT_TOKEN`, OTLP headers, anything named like a token, key, password or
header) are never included, credentials in URLs are masked, and paths in your home directory are shown as `~`. The
API is `GET /api/diagnostics` (`{ markdown, report }`). The command line has no running host, so its log lines come
from the log file (`--log-file`, else `labcoat.log` in the data folder if one exists). Read it before you share it.

## Settings files

Labcoat reads up to two JSON files. Both have the same shape; every key is optional:

```json
{
  "$schema": "https://raw.githubusercontent.com/eriklieben/labcoat/main/docs/labcoat.schema.json",
  "ui": {
    "theme": "dracula",
    "mode": "dark",
    "editor": "rider",
    "confetti": true,
    "locked": ["editor"]
  },
  "watch": true,
  "coverage": {
    "enabled": false,
    "settings": "coverage.settings.xml",
    "include": ["Labcoat.*"],
    "exclude": ["**/Migrations/**"]
  },
  "traces": false,
  "traceFilter": "cat:Integration",
  "port": 4300,
  "affectedBase": "origin/develop",
  "impactMap": "auto",
  "maxParallelProjects": 4,
  "hangThreshold": "60s",
  "testTimeout": "5m"
}
```

| File | Where | Meant for |
| --- | --- | --- |
| **Workspace file** | `.labcoat.json`, else `.config/labcoat.json`, in the workspace directory or a parent, up to the repository root (the directory with `.git`); the nearest one wins. Never the home directory. `--config <file>` names one explicitly. | Team defaults, checked in with the code. |
| **User file** | `~/.labcoat.json` on every OS, else `$XDG_CONFIG_HOME/labcoat/config.json` (`~/.config/labcoat/config.json`) on Linux and macOS or `%APPDATA%\Labcoat\config.json` on Windows. | Your own defaults for every workspace, e.g. your editor. |

- `$schema` points editors (VS Code, Rider) at [labcoat.schema.json](labcoat.schema.json) for completion and checks.
- Comments and trailing commas are allowed.
- `ui` holds the [UI settings](#ui-settings) and `locked`: a list of setting names, `"all"`, or `true` (all).
- `watch`, `coverage`, `traces`: `true` starts with that mode on, like the command-line flags (a flag or a file can
  only turn it on).
- `traceFilter`: while per-test traces are on, only the tests matching it are traced, like `--trace-filter`. The
  workspace file wins over the user file; `LABCOAT_TRACE_FILTER` and `--trace-filter` win over both. Unlike other
  values, an invalid filter is an error that stops Labcoat (skipping it would trace every test).
- `coverage` is `true` / `false`, or an object:
  - `enabled`: like `coverage: true`.
  - `settings`: the coverage settings file for every test project, like `--coverage-settings`. Relative: next to each
    test project first, then next to the settings file. The workspace file wins over the user file; the command line
    wins over both.
  - `include` / `exclude`: module names or path globs (lists from both files and the command line add up). See
    [Which code counts](features.md#which-code-counts) for how Labcoat chooses the settings and what the globs match.
- `port`: used when neither `--port` nor `PORT` / `ASPNETCORE_URLS` is set (so under Aspire it never applies).
- `affectedBase`: the git ref that **Run affected → Branch vs …** compares with (default: `origin/HEAD`, else
  `origin/main`, `origin/master`, `main` or `master`). The workspace file wins over the user file;
  `LABCOAT_AFFECTED_BASE` and `--affected-base` win over both. It is a server option, not a UI setting: the UI shows
  the effective base, and a ref that doesn't exist in the repository is reported when you use it.
- `impactMap`: `"class"`, `"test"` or `"auto"` (default), the granularity of the
  [impact map](features.md#the-impact-map). The workspace file wins over the user file; `LABCOAT_IMPACT_MAP` and
  `--impact-map` win over both.
- `maxParallelProjects`, `hangThreshold`, `testTimeout`: see
  [Parallel projects, slow tests and timeouts](#parallel-projects-slow-tests-and-timeouts). Durations are seconds (`90`)
  or a number with a unit (`500ms`, `30s`, `5m`, `1.5h`); `0` or `"off"` turns them off. The workspace file wins over
  the user file; the environment variables and the command line win over both.
- When `.labcoat.json` and `.config/labcoat.json` are both in the same directory (or `~/.labcoat.json` and the config
  dir file both exist), the first is used and a warning is logged.
- Unknown keys and invalid values are skipped with a warning in the log (`Settings: .labcoat.json: 'theme' must be
  one of …; ignored.`); the rest of the file still applies. A file that isn't valid JSON is skipped with a warning,
  except one given with `--config`, which stops Labcoat with an error.
- The startup banner names the files in use: `Settings:  ~/.labcoat.json, .labcoat.json`.
- `--no-user-config` or `LABCOAT_NO_USER_CONFIG=1` ignores the user file (the smoke test and CI use it).

## Parallel projects, slow tests and timeouts

**Builds.** Labcoat builds every project that needs a build in one `dotnet build` of a generated solution (written to
`obj/labcoat/` of the first project and deleted afterwards), so MSBuild builds them in parallel and a shared dependency
once, like `dotnet build` of your solution. Build errors are still shown per project: each project gets the errors of
its own files and of the projects it references. A project without source changes since its last build is skipped. A
multi-targeted project that runs on one chosen framework is built on its own afterwards (`-f`).

**Discovery and runs.** Up to `maxParallelProjects` projects (default: the number of processors, at most 4) are
discovered and run at the same time, each on its own [warm test host](features.md#running-tests). Results stream in as
they arrive; the tree keeps the project order. **Stop** cancels every project that runs. `maxParallelProjects: 1` runs
one project after the other. Inside a project, the test framework decides what
runs in parallel (MSTest `[Parallelize]`, xUnit collections, ...).

**Slow and hanging tests.** While a run is in progress the run summary shows the test that has been running the longest,
with its elapsed time; the button opens the list of the tests running now. After `hangThreshold` (default 60 s) a test
is marked **possibly hanging** and a warning is logged once.

**Test timeout.** `testTimeout` (off by default) is a per-test limit: when a test runs longer, Labcoat marks it **Timed
out** ("Timed out after 30 s: the test ran longer than the test timeout …"), cancels the run of its project (the other
projects go on) and the run ends as *aborted* with the reason. Cancelling asks the test host to stop and kills it after
`LABCOAT_CANCEL_GRACE_SECONDS` (default 10) when the test doesn't observe the cancellation (`Thread.Sleep`, a blocking
call); the next run then starts a new test host. The project's other tests that had not run yet show as cancelled.

### TestingPlatformCommandLineArguments

A test project's `TestingPlatformCommandLineArguments` MSBuild property is what `dotnet test` passes to the test app.
Labcoat passes these options to its test hosts too, so runs select and configure tests the same way: `--settings`
(a relative path that exists next to the project is made absolute), `--config-file`, `--filter`,
`--filter-class` / `--filter-method` / `--filter-trait` / `--filter-query` (xUnit), `--treenode-filter` (TUnit),
`--test-parameter`, `--maximum-failed-tests`, `--retry-failed-tests`, `--report-trx`, `--diagnostic*` and framework
options. A filter also limits discovery, so the tree shows the tests `dotnet test` would run.

Options that conflict with server mode or with what Labcoat controls are skipped and logged once per project
(`TestingPlatformCommandLineArguments: passed to the test host: …; skipped: …`): `--server`, `--client-host`,
`--client-port`, `--port`, `--dotnet-test-pipe`, `--list-tests`, `--help`, `--info`, `--debug`, `--coverage`,
`--coverage-output`, `--coverage-output-format` (Labcoat's **Collect coverage** sets them; `--coverage-settings` is used
for coverage runs, see [Which code counts](features.md#which-code-counts)), `--show-live-output` (Labcoat turns live
output on itself), `--minimum-expected-tests` (Labcoat often runs a selection), `--exit-on-process-exit`,
`--results-directory`, `--output`, `--no-progress`, `--no-ansi` and `--ignore-exit-code`.

**`--timeout` vs `testTimeout`.** MTP's `--timeout <duration>` limits a whole test session: under `dotnet test` the
test app ends when its session takes longer. A warm test host lives across many runs, so Labcoat does not pass it on;
it applies it to **each run of the project** instead: when one run of the project takes longer, the tests running then
are marked timed out and the project's run is cancelled (logged as "The run of X exceeded its --timeout 5m"). Labcoat's
`testTimeout` limits **each test** and applies to every project.

## UI settings

The Settings page (the gear at the bottom of the left rail) has the appearance, keyboard, editor, coverage, history and
output settings described in [features.md](features.md#settings). A choice made there is stored in the browser's local
storage, so it applies per browser and per Labcoat address (`localhost:4300` and `localhost:4301` are different
origins), and only the settings you changed are stored.

Every setting can also get a **default** from outside the browser: the settings files, the Aspire AppHost,
`LABCOAT_UI_SETTINGS` or a command-line flag.

| Setting | Values | Built-in default | In the UI | Settings file (`"ui"`) / `LABCOAT_UI_SETTINGS` | Aspire (`o.Ui.…` / `WithUiSettings`) | CLI |
| --- | --- | --- | --- | --- | --- | --- |
| Theme | `catppuccin`, `aspire`, `github`, `one`, `dracula`, `solarized`, `gruvbox` | `catppuccin` | Appearance → Theme (and More themes) | `theme` | `Theme = LabcoatTheme.Dracula` | `--theme` |
| Mode | `system`, `light`, `dark` | `system` | Appearance → Mode | `mode` | `Mode = LabcoatThemeMode.Dark` | `--mode` |
| Motion | `system`, `reduced`, `off` | `system` | Appearance → Motion | `motion` | `Motion = LabcoatMotion.Reduced` | `--motion` |
| Editor | `vscode`, `rider`, `visualstudio` | `vscode` | Editor → Open files in | `editor` | `Editor = LabcoatEditor.Rider` | `--editor` |
| Coverage tint | `line`, `gutter` | `line` | Coverage → Coverage tint | `coverageTint` | `CoverageTint = LabcoatCoverageTint.Gutter` | |
| Confetti | `true`, `false` | `false` | Celebrations → Confetti when tests turn green | `confetti` | `Confetti = true` | |
| Partial runs in history | `true`, `false` | `false` | History → Show partial runs in history | `partialRunsInHistory` | `PartialRunsInHistory = true` | |
| Open output on errors | `true`, `false` | `false` | Output → Open the output panel on errors | `openOutputOnError` | `OpenOutputOnError = true` | |
| Single-key shortcuts | `true`, `false` | `true` | Keyboard → Single-key shortcuts | `singleKeyShortcuts` | `SingleKeyShortcuts = false` | |
| *(lock)* | setting names, `all` | none | shown disabled | `locked` | `Lock(s => s.Theme)`, `Lock(LabcoatUiSetting.Theme, …)`, `LockAll()` | `--lock` |

Remembered view state (panel sizes, tree filter / sort / grouping, the open page) is not a setting and stays per
browser. Trace collection is a per-workspace setting of the host (see [aspire.md](aspire.md#traces-and-logs-per-test-opt-in)).

### Precedence

The host merges the defaults; for each setting the highest layer that sets it wins:

| Layer (lowest first) | Source |
| --- | --- |
| 1. Built-in | the defaults in the table above |
| 2. User file | `~/.labcoat.json` (or the config dir) |
| 3. Workspace file | `.labcoat.json` / `.config/labcoat.json` / `--config` |
| 4. AppHost | `AddLabcoat("tests", o => o.Ui…)`, `.WithUiSettings(…)`, appsettings.json section `Labcoat` |
| 5. Environment | `LABCOAT_UI_SETTINGS` |
| 6. Command line | `--theme`, `--mode`, `--editor`, `--motion` |

In the browser, **this browser's own choice wins over every default**, unless the setting is **locked**. Locks come
from any layer (they add up) and beat both the defaults of lower layers and the browser: a locked setting always has
the merged default (or the built-in value when no layer sets one), and the Settings page shows it disabled with
*Set by the AppHost (Program.cs)* / *Set by .labcoat.json* / … . A lock in your user file only affects you; locks are
meant for workspace files and AppHosts.

The Settings page shows where each value comes from (*Default from .labcoat.json*, *Set in this browser · default
from the AppHost (Program.cs)*), and **Reset to defaults** forgets this browser's choices. The theme defaults are
part of the served `index.html` (as an `<html data-labcoat-defaults>` attribute), so the first paint already uses
them. The merged defaults are also in `GET /api/state` as `uiDefaults` (see [contract.md](contract.md#round-5-additions)).

## API

The UI talks to the host over a small REST and WebSocket API, which you can also use from scripts. For example, run
all tests and read the result:

```bash
curl -X POST http://localhost:4300/api/run -H 'Content-Type: application/json' -d '{}'
curl http://localhost:4300/api/summary
```

The full API is in [contract.md](contract.md). [`eng/smoke-test.mjs`](../eng/smoke-test.mjs) is a complete example.
