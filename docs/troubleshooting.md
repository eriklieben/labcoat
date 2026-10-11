# Troubleshooting

## A project shows no tests

Open the project in the tree (or its row in the Output panel): Labcoat explains what it found. The usual causes:

**The project isn't a Microsoft.Testing.Platform executable.** Labcoat starts test projects with `--server`, which only
MTP test apps support (a VSTest project shows a **VSTest** pill instead: see [My project uses VSTest](#my-project-uses-vstest)). Check with `dotnet run --project <test project>`: it should print an MTP `Test run summary`. The
settings per framework:

| Framework | Settings |
| --- | --- |
| MSTest 4 | `<OutputType>Exe</OutputType>` and `<EnableMSTestRunner>true</EnableMSTestRunner>`, or `<Project Sdk="MSTest.Sdk/…">` |
| NUnit | `<OutputType>Exe</OutputType>` and `<EnableNUnitRunner>true</EnableNUnitRunner>`, NUnit 4.4+ with NUnit3TestAdapter 6 or later (adapter 5 is on MTP 1.x, Labcoat needs MTP 2.x) |
| xUnit v3 | the `xunit.v3.mtp-v2` package, `<OutputType>Exe</OutputType>` and `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` |
| TUnit | `<OutputType>Exe</OutputType>` |

The [samples](../samples) have a working project file for each.

**`OutputType is 'Library'`.** MTP test projects are executables: add `<OutputType>Exe</OutputType>`.

**"The test host exited" or "did not connect within 60 s".** The test app crashed or hung on startup. Run it directly
to see why: `dotnet <path to the test dll> --list-tests`.

**The test classes aren't found.** Check that they are public and marked as tests (MSTest: `[TestClass]` and
`[TestMethod]`).

**The project isn't listed at all.** Labcoat finds test projects by asking MSBuild for their properties and package
references, so settings in `Directory.Build.props` count. A project it doesn't recognize can be passed explicitly:
`labcoat tests/MyApp.Tests/MyApp.Tests.csproj`. When you start Labcoat without paths it uses the nearest solution; make
sure the test project is in it, or pass a directory instead.

## My project uses VSTest

Labcoat runs Microsoft.Testing.Platform (MTP) test apps only: it starts them with `--server` and talks to them over
MTP's protocol. A VSTest test project (`Microsoft.NET.Test.Sdk` with `MSTest.TestAdapter`, `NUnit3TestAdapter` or
`xunit.runner.visualstudio`, and no MTP switch) doesn't answer that, so Labcoat doesn't build or start it. It lists
the project with a **VSTest** pill in the tree, and the project's first log line says what to change, for example:

```text
SampleTests uses VSTest (Microsoft.NET.Test.Sdk + MSTest.TestAdapter). Labcoat runs Microsoft.Testing.Platform
projects: set <EnableMSTestRunner>true</EnableMSTestRunner> and <OutputType>Exe</OutputType> (or use MSTest.Sdk).
```

Labcoat reads this from the project file and the `Directory.Build.props` / `Directory.Packages.props` files above it,
so it works on a fresh clone before `dotnet restore`; an MSBuild evaluation confirms it, and after a build the
restored settings decide before any test host starts. The switch per framework:

| Framework | Switch to Microsoft.Testing.Platform |
| --- | --- |
| MSTest | `<EnableMSTestRunner>true</EnableMSTestRunner>` and `<OutputType>Exe</OutputType>`, or `<Project Sdk="MSTest.Sdk/…">` (MTP by default; remove `<UseVSTest>true</UseVSTest>` if it is set) |
| NUnit | `<EnableNUnitRunner>true</EnableNUnitRunner>` and `<OutputType>Exe</OutputType>`, NUnit3TestAdapter 6 or later (5 is on MTP 1.x) |
| xUnit v2 | move to xUnit v3: replace `xunit` and `xunit.runner.visualstudio` with `xunit.v3.mtp-v2`, set `<OutputType>Exe</OutputType>` (and `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` for `dotnet test`) |
| xUnit v3 | already on MTP (`xunit.v3` 4.x and `xunit.v3.mtp-v2` are on MTP 2) |
| TUnit | always on MTP |

Put the switch in `Directory.Build.props` to switch every test project at once, then press **Rediscover** (or save the
project file: Labcoat picks the change up). Before you switch, add the .NET 10 `global.json` setting, or `dotnet test`
stops working: see [Migrating from VSTest](migrating-from-vstest.md).

A project with no test framework at all (for example a console app passed on the command line) is not started
either: its message says *is not a Microsoft.Testing.Platform test project*.

## The build fails

The banner and **Show errors** list the compiler errors with links to the source. Labcoat runs
`dotnet build <project>`, so the same errors appear when you build from the command line. Fix them and save (in watch
mode) or press **Run** again. Other projects keep working.

## Port 4300 is in use

Without `--port`, Labcoat takes the next free port (4301, 4302, ...) and prints the address in the banner. With an
explicit `--port` or `PORT` it fails instead, so scripts don't end up talking to another program. Pick another port, or
set `LABCOAT_DEFAULT_PORT` to change the first one tried.

## The browser doesn't open

Labcoat doesn't open a browser with `--no-open`, when `CI` is set, or when there is no display (SSH, containers). It
prints the URL instead. Over SSH, forward the port: `ssh -L 4300:localhost:4300 <host>`, then open
http://localhost:4300 on your machine. Set `BROWSER` to choose the browser command.

## "Invalid host" (400) or "Forbidden" (403)

Labcoat only answers to local host names and only accepts commands from its own page, to protect against DNS
rebinding and cross-site requests (see [SECURITY.md](../SECURITY.md)).

- Reaching it under another name (a container, a reverse proxy, a dev tunnel): add the name to `LABCOAT_ALLOWED_HOSTS`.
- A page on another origin that sends commands, such as the UI dev server on port 5174: add it to
  `LABCOAT_ALLOWED_ORIGINS`.
- Scripts: requests without an `Origin` header are allowed from loopback. When `LABCOAT_TOKEN` is set (always under
  Aspire), send it as the `X-Labcoat-Token` header.
- `POST` requests with a JSON body need `Content-Type: application/json` (415 otherwise).

Labcoat runs your tests and builds your code: only expose it on a network you trust.

## Coverage stays empty

Coverage needs `Microsoft.Testing.Extensions.CodeCoverage` in the test project. MSTest and TUnit include it; for NUnit
and xUnit v3 add the package. Labcoat checks the test app's `--help` for `--coverage-output-format` and logs a warning
for projects without it. Without a coverage settings file, the report only contains the projects the test project
references: the test assemblies themselves are left out, so code that lives in the test project shows no coverable
lines. Check the "Coverage settings: …" line in the log (and the list under the coverage page's title): it says which
settings file was used, or Labcoat's defaults and the modules they count.

## Coverage differs from `dotnet test --coverage`, or a project shows 0 lines covered

- **Different settings.** `dotnet test --coverage` without `--coverage-settings` counts every module that was loaded
  (Labcoat's own assemblies, tools the tests reference, generated code); Labcoat's defaults leave those out. With a
  settings file (`coverage.settings.xml` next to the test project, or see
  [Which code counts](features.md#which-code-counts) for the other places) both use the same file and report the same
  lines. Make sure the file Labcoat names in the log is the one you pass to `dotnet test`.
- **0 lines covered after a normal run** (early versions before 1.0, Linux / macOS). Static instrumentation
  rewrites the assemblies of the output folder before the coverage run. When another process holds one of them open,
  for example a warm test host that printed a stack trace with line numbers (the runtime keeps the module's `.pdb`
  open with a shared lock afterwards), the extension skips that module without an error and it shows 0 of N lines.
  Labcoat now uses dynamic instrumentation where it can and otherwise stops the warm host before a coverage run. The
  same thing can happen outside Labcoat: a `dotnet test --coverage` while another test process of the same output
  folder is running (a `dotnet watch test`, an IDE session) can report 0 lines for a module.
- **Dynamic instrumentation needs Microsoft.Testing.Extensions.CodeCoverage 18.11 or later on Linux / macOS** (TUnit
  and MSTest 4.5 bring one; MSTest 4.0 brings 18.1, whose dynamic instrumentation collects nothing in server mode).
  With an older extension Labcoat uses static instrumentation and restarts the warm host around coverage runs; update
  the package (`<PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" Version="18.11.2" />`) to keep
  the warm host. A settings file with `<EnableStaticManagedInstrumentation>True</EnableStaticManagedInstrumentation>`
  keeps static instrumentation.

## Warm test hosts keep old state

The test process stays alive between runs, so static state survives. If a test depends on a fresh process, use
**Restart test host** (right-click a project) or restart Labcoat. For fixtures that start an app, see
[aspire.md](aspire.md#integration-tests-and-warm-test-hosts).

## Tests fail with "Index not found (0x80131124)" or `BadImageFormatException`

Many tests fail at once with `BadImageFormatException`, `Could not load file or assembly '<Unknown>'. Index not found.
(0x80131124)`, a `ReflectionTypeLoadException` or garbled type names, and pass again after **Restart test host**. On
Linux and macOS a running process maps its assemblies into memory; when a file it has loaded is then rewritten *in
place* (not replaced), the process sees a mix of old and new bytes. Two things do that to a warm test host:

- **A code coverage run with static instrumentation.** On Linux / macOS the coverage extension can instrument the
  product assemblies in the output folder in place and put the originals back afterwards. That restore makes the folder
  look unchanged, so older Labcoat versions kept using the damaged warm host: the first run without coverage after a
  coverage run failed. Labcoat now uses dynamic instrumentation where the extension supports it (nothing is rewritten),
  and otherwise stops the warm host before the coverage run and starts a fresh one after it.
- **A build outside Labcoat** that overwrites a loaded assembly: a terminal `dotnet build` / `dotnet test`, the IDE, or
  another resource that builds the same project (e.g. one running `dotnet watch test`).

Labcoat now watches each warm host's output folder. When a loaded assembly is rewritten with different content, it
replaces the warm host: right away when it is idle, after the operation when one is running
(log: *Stopping the warm test host: X.dll was rewritten in place …*). A run during which that happened logs a warning
to run the tests again, and a run that fails this way also replaces the host. A rebuild that writes identical bytes
keeps the host. If you still see it, use **Restart test host** and tell us how it happened.

## Tests behave differently under Labcoat in the Aspire dashboard

Labcoat started by an AppHost runs tests without the variables of the Aspire run (`ASPIRE_*`, `DCP_*`, the `OTEL_*` of
the Labcoat resource, its `PORT` / `ASPNETCORE_URLS`, ...), so a nested AppHost in a test doesn't connect to the outer
`aspire` CLI or dashboard. The log lists the names left out ("Test hosts don't inherit …") at the first test host start.
If your tests need one of them, keep it with `WithPassThroughEnvironment("NAME")` (or `LABCOAT_TEST_ENV_PASSTHROUGH`
outside Aspire). See [aspire.md](aspire.md#the-tests-environment).

## The run still shows "Running" after Cancel

Cancel marks the tests that had not started as cancelled at once. The test in progress gets 10 s
(`LABCOAT_CANCEL_GRACE_SECONDS`) to finish before its test host is killed; meanwhile `/api/summary` has
`"cancelling": true` and the Aspire resource state shows **◼ Cancelling…**.

## Windows notes

- Windows locks loaded assemblies, so Labcoat stops a project's warm test host before it rebuilds that project. The
  first run after a change pays the host startup again.
- With Aspire, the integration tests reference the AppHost, so Labcoat rebuilds the AppHost while it runs. If the
  AppHost's own code changed, that build can fail with "file in use": restart the AppHost.
- The `--labcoat` extension keeps the test app running while Labcoat runs, which locks its output: a rebuild of that
  project after a source change can fail with MSB3027. See [standalone.md](standalone.md#limitations).

## Still stuck?

Open the **Output** panel (`` Ctrl+` ``) for Labcoat's log, and run with a fresh data folder to rule out old history:
`LABCOAT_HISTORY_DIR=$(mktemp -d) labcoat`. Then [open an issue](https://github.com/eriklieben/labcoat/issues/new/choose)
with the log and the steps to reproduce.
