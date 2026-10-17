# Releasing

Releases are made by [semantic-release](https://github.com/semantic-release/semantic-release) from the commit messages
on `main`: nobody picks a version, edits the changelog or pushes a tag by hand. How to write commits and what each type
releases: [CONTRIBUTING.md, "Commit messages and releases"](../CONTRIBUTING.md#commit-messages-and-releases).

| File | What |
| --- | --- |
| [`release.config.mjs`](../release.config.mjs) | Branches, tag format `v<version>`, plugins, release notes. |
| [`eng/release.mjs`](../eng/release.mjs) | The .NET steps: set `<VersionPrefix>`, build the UI, `dotnet pack`, the SBOM release assets, `dotnet nuget push`. |
| [`eng/release-commit.mjs`](../eng/release-commit.mjs) | The release commit through the GitHub API (signed, *Verified*). Tests: `node --test eng/release-commit.test.mjs`. |
| [`.github/workflows/release.yml`](../.github/workflows/release.yml) | Runs semantic-release; started by `ci.yml` after CI passed, or by hand (dry run). |
| [`.github/workflows/commit-messages.yml`](../.github/workflows/commit-messages.yml) | Checks PR titles (and, as a warning, PR commits) with [`commitlint.config.mjs`](../commitlint.config.mjs). |
| [`.github/renovate.json5`](../.github/renovate.json5) | Dependency update PRs (Renovate). |

## How a release works

1. A pull request is squash-merged into `main`; its title (checked by CI) is the commit message semantic-release reads.
2. When every CI job passed, CI's last job, `release`, starts the **Release** workflow (`release.yml`) for that commit.
   If the branch has moved on by then, it releases nothing: the CI run of the newer commit releases both.
3. semantic-release looks at the commits since the last tag `v*`. If one of them releases (see
   [Versioning](#versioning)), it writes `CHANGELOG.md`, sets the version and the package validation baseline in
   `src/Directory.Build.props`, builds the UI and packs (`eng/release.mjs prepare`; a failure stops the release before
   anything is pushed).
4. The release commit `chore(release): <version> [skip ci]` is made by the **labcoat-release** GitHub App through the
   GitHub API, so it shows as *Verified*; `[skip ci]` keeps it from starting CI (and another release). The commit is
   tagged `v<version>`.
5. The GitHub release is created with the packages and SBOMs, the packages are pushed to nuget.org, and the build
   provenance and SBOM attestations are made ([Verifying packages](#verifying-packages)).

Publishing uses NuGet trusted publishing and a GitHub App; the repository's maintainers configure these once.

## Versioning

Labcoat follows [Semantic Versioning](https://semver.org) with semantic-release's default rules:

| Commit | Release |
| --- | --- |
| `fix: ...`, `perf: ...`, `revert: ...` | patch: 1.4.2 → 1.4.3 |
| `feat: ...` | minor: 1.4.2 → 1.5.0 |
| `feat!: ...` / `BREAKING CHANGE:` footer | major: 1.4.2 → 2.0.0 |
| `docs`, `chore`, `refactor`, `test`, `ci`, `build`, `style` | no release |

A major version covers the command line, the settings files and the public APIs of the library packages: think twice
before a `!`, and say in the `BREAKING CHANGE:` footer what users have to change. `CHANGELOG.md` and the GitHub release
get the same notes: *⚠ BREAKING CHANGES*, *Features*, *Bug Fixes*, *Performance*, *Reverts* and *Dependencies*.

## Prereleases

Push a `beta` (or `next`) branch, e.g. `git switch -c beta main && git push -u origin beta`. CI on that branch releases
prereleases: `1.3.0-beta.1`, `1.3.0-beta.2`, ... as NuGet prerelease versions and GitHub prereleases. Merge `beta` into
`main` (a merge commit, not a squash, so the commits keep their tags) to release `1.3.0`, then delete or reset the branch.

## What a release contains

- **Nine NuGet packages:** `Labcoat` (the .NET tool), `Labcoat.Aspire.Hosting`, `Labcoat.Testing`,
  `Labcoat.Testing.MSTest`, `Labcoat.Testing.NUnit`, `Labcoat.Testing.XUnit`, `Labcoat.Testing.TUnit`,
  `Labcoat.TestingPlatform` and `Labcoat.Vitest`, each with a symbol package (`.snupkg`, on the NuGet symbol server)
  and SourceLink, except `Labcoat.Vitest`, which has no assembly (a Node.js runner only).
- **SBOMs** (see [SBOM](#sbom)): an SPDX 2.2 SBOM inside each `.nupkg`, and a CycloneDX SBOM of the web UI's npm
  dependencies in the three packages that bundle the UI.
- **The GitHub release** `v<version>`: the release notes, the `.nupkg` and `.snupkg` files, each package's SBOM as
  `<id>.<version>.spdx.json`, and `labcoat-ui.<version>.cdx.json`.

### SBOM

| What | Where | Made by |
| --- | --- | --- |
| SPDX 2.2 SBOM of each package: every file in the `.nupkg` with its SHA-256 (also the bundled runner and UI), and the NuGet packages it depends on, transitively, as `pkg:nuget` purls | in the `.nupkg` at `_manifest/spdx_2.2/manifest.spdx.json`; release asset `<id>.<version>.spdx.json` | [`Microsoft.Sbom.Targets`](https://github.com/microsoft/sbom-tool), on every `dotnet pack` |
| CycloneDX SBOM of the web UI's npm production dependencies | in `Labcoat`, `Labcoat.Aspire.Hosting` and `Labcoat.TestingPlatform` at `sbom/ui.cdx.json` next to `wwwroot`; release asset `labcoat-ui.<version>.cdx.json` | `npm sbom --omit dev` in `eng/release.mjs prepare` |

sbom-tool only sees a cleaned copy of `project.assets.json` (`src/Directory.Build.targets`): build-only references and
downloads removed, project references that flow to the package turned into package references, so the SBOM matches the
nuspec's dependencies. A plain `dotnet pack` (CI, local) has no UI SBOM; `eng/release.mjs prepare` checks that every
package has its SBOMs and fails the release otherwise. To look at one:

```bash
unzip -p Labcoat.Testing.<version>.nupkg _manifest/spdx_2.2/manifest.spdx.json \
  | jq -r '.packages[] | select(.SPDXID != "SPDXRef-RootPackage") | "\(.name) \(.versionInfo)"' | sort
unzip -p Labcoat.<version>.nupkg tools/net10.0/any/sbom/ui.cdx.json | jq -r '.components[] | "\(.name) \(.version)"'
```

## Verifying packages

The release attests the build provenance of every `.nupkg` and `.snupkg` and of the SBOM files
([`actions/attest-build-provenance`](https://github.com/actions/attest-build-provenance)): a Sigstore-signed SLSA
statement that ties each file's SHA-256 to this repository, the workflow and the commit. Each `.nupkg` also has an SBOM
attestation for the SPDX SBOM it carries, and the three packages with the UI one for the UI's CycloneDX SBOM.

```bash
# a release tag such as v1.0.0, or the newest release
tag=$(gh release view --repo eriklieben/labcoat --json tagName --jq .tagName)
gh release download "$tag" --repo eriklieben/labcoat --pattern '*.nupkg' --pattern '*.snupkg' --dir release
gh attestation verify "release/Labcoat.${tag#v}.nupkg" --repo eriklieben/labcoat
gh attestation verify "release/Labcoat.${tag#v}.nupkg" --repo eriklieben/labcoat --predicate-type https://spdx.dev/Document/v2.2
gh attestation verify "release/Labcoat.${tag#v}.nupkg" --repo eriklieben/labcoat --predicate-type https://cyclonedx.org/bom
# the attested SBOM itself (the predicate), e.g. its package list:
gh attestation verify "release/Labcoat.Testing.${tag#v}.nupkg" --repo eriklieben/labcoat \
  --predicate-type https://spdx.dev/Document/v2.2 --format json --jq '.[0].verificationResult.statement.predicate.packages[].name'
```

Verify the files of the GitHub release: nuget.org adds a repository signature to each package, which changes the file,
so a package downloaded from nuget.org doesn't match its attestation (check that one with
`dotnet nuget verify --all <file>`). The attestations are listed under the repository's **Actions → Attestations**.

## Package validation

`dotnet pack` runs [package validation](https://learn.microsoft.com/dotnet/fundamentals/apicompat/package-validation/overview)
for the library packages (all but the `Labcoat` tool, whose contract is the command line). Each package is compared with
the same package at `<PackageValidationBaselineVersion>` in `src/Directory.Build.props`, downloaded from nuget.org: a
public type or member that was removed or changed fails the pack (`CP0002`, `CP0006`, ...). That happens in CI on every
PR and in the release, so a break can't be released by accident. The baseline moves by itself: each release is
validated against the previous one and commits that version as the new baseline. Locally:
`dotnet pack Labcoat.slnx -c Release -o <dir>`.

**An intentional break** releases the next major version and must be marked:

1. Make the change. `dotnet pack` fails and lists what broke.
2. Write the commit (PR title) as a breaking change: `feat(testing)!: ...` with a `BREAKING CHANGE:` footer that tells
   users what to do.
3. Accept the break for the project: `dotnet build <project> -c Release && dotnet pack <project> -c Release --no-build -p:ApiCompatGenerateSuppressionFile=true`
   writes `CompatibilitySuppressions.xml` next to the project file. Review it (only the breaks you meant) and commit it
   in the same PR.
4. After the *next* release the file is no longer needed: delete it (unneeded suppressions don't fail anything).

A **new package** isn't on nuget.org at the baseline version (restore fails with `NU1102`): set
`<DisablePackageBaselineValidation>true</DisablePackageBaselineValidation>` in its project file, with a comment, until
the baseline is a release that contains it.

## Dependency updates

[Renovate](https://docs.renovatebot.com) opens the dependency update PRs ([`.github/renovate.json5`](../.github/renovate.json5));
Dependabot only raises vulnerability alerts. PR titles decide what releases: `fix(deps)` for dependencies that ship
(NuGet packages of `src/`, the UI's runtime dependencies: a patch release), `build(deps)` for samples, the SDK and build
tooling, `build(deps-dev)` for npm dev dependencies and test packages, `ci(deps)` for GitHub Actions (pinned to commit
SHAs). Updates are weekly and grouped; a vulnerable dependency gets its own PR at once. Automerge is off. After changing
the config: `pnpm dlx --package renovate@44.115.1 -- renovate-config-validator --strict`.

## Dry run

- **GitHub:** Actions → **Release** → *Run workflow* → branch `main`, *Dry run* checked (the default). The log shows the
  commits it analyzed, the next version and the release notes.
- **Local:** `GITHUB_TOKEN=$(gh auth token) pnpm exec semantic-release --dry-run --no-ci` (needs push access).
  `node eng/release.mjs prepare 0.0.0-local.1` runs just the packing step, with package validation; revert
  `src/Directory.Build.props` afterwards.

## Code analysis

- **CodeQL** (GitHub code scanning) analyses the C#, the JavaScript/TypeScript and the GitHub Actions workflows on
  every push and pull request.
- **SonarQube Cloud** analyses `main` and pull requests from branches of this repository in CI's `sonar` job:
  [dashboard](https://sonarcloud.io/summary/new_code?id=eriklieben_labcoat), badges at the top of the
  [README](../README.md). Quality profile *Sonar way comprehensive* for every language; new code is measured against
  the previous version. The job wraps one Release build with SonarScanner for .NET (C#, plus the TypeScript,
  JavaScript and CSS) and imports the coverage of the other jobs (Cobertura from `eng/coverage.mjs`, Vitest's lcov); it
  runs no tests. Fork pull requests get no secrets, so the job is skipped for them; the analysis of `main` after the
  merge picks the change up.

The analysis settings are the `/d:` arguments of the *Begin analysis* step in [`ci.yml`](../.github/workflows/ci.yml):

- **Excluded:** samples, benchmarks, build output, generated code, `node_modules`, the mock backend `ui/src/mock/**`,
  the design system gallery, the build and release scripts and the tool configuration. Duplication leaves out the theme
  data (`ui/design-system/src/themes/families.ts`). Test code (`tests/**`, `ui/test/**`) is analysed with the test rules.
- **Compiler and analyzer warnings aren't imported** (`sonar.cs.roslyn.ignoreIssues=true`): the build job gates them
  with `-warnaserror`. The adapter packages `src/Labcoat.Testing.{MSTest,NUnit,XUnit}` set
  `<SonarQubeTestProject>false</SonarQubeTestProject>`, or their test framework reference would make them test projects.
- **Issue ignores** (`sonar.issue.ignore.multicriteria`), only for conventions and false positives that can't be fixed
  without making the code worse:
  - `typescript:S1444` (public static fields should be read-only) in `ui/**/components/**`: `static defaults`,
    `hostClass` and `observedAttributes` are the custom-element convention, and subclasses override them.
  - `typescript:S2245` (`Math.random`) in `ui/src/confetti.ts` (the confetti animation) and `ui/src/transport.ts`
    (reconnect backoff jitter): neither is security-relevant.
  - `typescript:S4782` (redundant `| undefined`) in `ui/design-system/src/**`: the `undefined` comes from shared
    aliases (`Child`, `ClassValue`, `AttrValue`, `MaybeReactive<T | undefined>`), not from the declarations.
  - `typescript:S1874` (deprecated API) in `ui/design-system/src/components/copy-button.ts`: `execCommand("copy")` is
    the fallback when `navigator.clipboard` is unavailable (an insecure context).
  - `typescript:S7758` (`codePointAt`) in `ui/src/util.ts`: the project accent colour hash works over UTF-16 code units;
    with code points, the colours of project ids with characters outside the BMP would change.
  - `typescript:S5906` (more specific matcher) in `ui/test/app/tree-filter.test.ts`: one identity check avoids
    `.not.toBe` on purpose, because that matcher's failure message deep-compares two large trees.
- **Marked in SonarQube Cloud** (one issue each, narrower than a file-wide ignore):
  - `tssecurity:S8480` (connection hijacking) on `new WebSocket(...)` in `ui/src/transport.ts`: *False positive*. The
    URL is `./ws` resolved against the page's own URL, so the host is always the one that served the page; the other
    parts are a fixed scheme (`ws:` / `wss:`) and a fixed `?lean=1`. The path always starts with `/`, so nothing in the
    page URL can add an authority, and the server checks Origin and Host as well.
  - `csharpsquid:S1075` (hard-coded URI) on `TestPlatform.DocsUrl` in `src/Labcoat/TestPlatform.cs`: *Accept*. It is
    a named constant already: the link to Labcoat's own troubleshooting page, the same for every user, so there is
    nothing to configure.

A new finding gets the same treatment: fix the code if that doesn't make it worse; otherwise a scoped ignore here and in
`ci.yml` with its reason, or, for a single issue, *Accept* / *False positive* in SonarQube Cloud with a comment.
