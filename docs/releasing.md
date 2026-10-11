# Releasing (maintainers)

Releases are made by [semantic-release](https://github.com/semantic-release/semantic-release) from the commit messages
on `main`: nobody picks a version, edits the changelog or pushes a tag by hand. How contributors write commits and what
each type releases: [CONTRIBUTING.md, "Commit messages and releases"](../CONTRIBUTING.md#commit-messages-and-releases).

| File | What |
| --- | --- |
| [`release.config.mjs`](../release.config.mjs) | Branches, tag format `v<version>`, plugins. |
| [`eng/release.mjs`](../eng/release.mjs) | The .NET steps: set `<VersionPrefix>`, build the UI, `dotnet pack`, the SBOM release assets, `dotnet nuget push`. |
| [`eng/release-commit.mjs`](../eng/release-commit.mjs) | With the GitHub App: the release commit through the GitHub API (signed, *Verified*). Tests: `node --test eng/release-commit.test.mjs`. |
| [`.github/workflows/release.yml`](../.github/workflows/release.yml) | Runs semantic-release: started by `ci.yml` (`workflow_dispatch`) after CI passed on `main` / `next` / `beta`, or by hand (dry run). |
| [`.github/workflows/commit-messages.yml`](../.github/workflows/commit-messages.yml) | Checks PR titles (and, as a warning, PR commits) with [`commitlint.config.mjs`](../commitlint.config.mjs). |
| [`package.json`](../package.json) | The pinned release and commit tooling (`npm ci` in the root). |
| [`.github/renovate.json5`](../.github/renovate.json5) | Dependency update PRs (Renovate): which titles release, groups, schedule, security PRs. |

## One-time setup

### Repository settings

- **Pull requests:** allow **squash merging** only, with *Default commit message* = **Pull request title** (or
  title and description). The PR title is then the commit message semantic-release reads, and CI has checked it.
- **Actions → General → Workflow permissions:** the default (read) is fine: the workflows ask for what they need, per
  job: `actions: write` in CI's `release` job (it starts `release.yml`), and `contents`, `issues` and `pull-requests:
  write`, `id-token: write` for NuGet and the attestations, `attestations: write` in `release.yml`'s release job
  (`id-token` and `attestations: write` also in its `sbom-attestation` job).
- **Actions → General → Actions permissions:** every `uses:` is pinned to a full commit SHA, so *Require actions to be
  pinned to a full-length commit SHA* can be turned on (see [Going public](#going-public)).
- **Environment `release`** (Settings → Environments → New environment): set *Deployment branches and tags* to
  *Selected branches* `main`, `next` and `beta`. The release job of `release.yml` runs in it, so its secrets and the
  NuGet trusted publishing policy are only available to release runs. Don't add required reviewers unless you want to approve every
  push to `main` (the job runs on each push, also when there is nothing to release).

### NuGet: trusted publishing (recommended)

nuget.org [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) swaps the job's GitHub
OIDC token for an API key that is valid for one hour: no long-lived key to store or rotate.

1. On nuget.org: your account → **Trusted Publishing** → add a policy: repository owner `eriklieben`, repository
   `labcoat`, workflow file **`release.yml`**, environment `release`. A policy created before the repository is public
   can start as *temporarily active* for 7 days and becomes permanent on its first use; restart the window on the same
   page if it expires before the first release.
2. On GitHub: Settings → Secrets and variables → Actions → **Variables** → `NUGET_USER` = the nuget.org **username of
   the account that created the policy** (here `erik.lieben`). Not the package owner (the organization `LabCoat`), not
   a display name, not the e-mail address: nuget.org looks up the policy among the policies *that user* created. With
   that variable set, the release job logs in with `NuGet/login` before semantic-release.

**Why CI starts `release.yml` instead of calling it.** `NuGet/login` fails inside a reusable workflow
(`workflow_call`) with *401 No matching trust policy*
([NuGet/login#6](https://github.com/NuGet/login/issues/6)), whichever workflow file the policy names. So CI's last job,
`release`, doesn't call `release.yml`: it starts it with `gh workflow run release.yml` (a `workflow_dispatch`, one of
the two events a `GITHUB_TOKEN` may trigger; pushes made with it start nothing). `release.yml` then always runs as a
top-level workflow, the way the first trusted-publishing release was made, and its OIDC token says `workflow_ref` =
`job_workflow_ref` = `.github/workflows/release.yml`, which the `release.yml` policy matches. The run gets
`commit=<sha>`: if the branch has moved on by the time it starts, it releases nothing (the CI run of the newer commit
starts its own release, which includes both). A policy for `ci.yml` isn't used by this setup and can be deleted.

#### Troubleshooting trusted publishing

When `NuGet/login` fails, the release job's next step, *Trusted publishing claims*, prints the user it sent and the
OIDC claims nuget.org compares with the policy (`repository`, `repository_owner`, `workflow_ref`, `job_workflow_ref`,
`environment`, `ref`, `event_name`); never the token. It only runs when the login failed.

```text
Error: Token exchange failed (401): No matching trust policy owned by user '<NUGET_USER>' was found.
```

means nuget.org found no active policy *created by that user* that matches the claims. The usual cause is the user
itself: `NUGET_USER` set to the package owner (`LabCoat`) or a display name instead of the username of the account that
created the policy (`erik.lieben`, which is what made the first trusted-publishing release work). What we checked and ruled out before
finding that:

- **The claims:** repository `eriklieben/labcoat`, owner `eriklieben`, as in the policy.
- **The workflow file:** `release.yml` in both `workflow_ref` and `job_workflow_ref` (a top-level run); a second policy
  for `ci.yml` made no difference.
- **The environment:** `release` in the claim and in the policy (the job declares `environment: release`).
- **Activation:** the policy was (temporarily) active, not expired.
- **The package owner / glob:** not part of the token exchange; the 401 comes before anything is pushed (a key that may
  not push a package fails later, with a 403 on `dotnet nuget push`).

Check, in this order: `NUGET_USER` is the policy creator's nuget.org username; the run is a top-level `release.yml`
run (not a reusable-workflow call); the environment is `release`; the policy is active.

### NuGet: API key (fallback)

Without `NUGET_USER`, the job uses the secret **`NUGET_API_KEY`** (repository or `release` environment secret): a
nuget.org API key with *Push new packages and package versions* for the glob `Labcoat*`. Keys expire (365 days at
most): put a reminder in your calendar.

### Dependency updates: Renovate, and Dependabot alerts only

[Renovate](https://docs.renovatebot.com) opens the dependency update PRs; GitHub's Dependabot only reports
vulnerabilities (it opens no PRs: the repository has no `.github/dependabot.yml`).

1. Install the **Mend Renovate** GitHub App (https://github.com/apps/renovate) on `eriklieben/labcoat` only
   (*Only select repositories*). The repository already has its config, so Renovate skips the onboarding PR and opens
   the **Dependency Dashboard** issue, then the first PRs on the next Monday morning (Europe/Amsterdam).
2. Settings → **Code security**:
   - **Dependency graph**: on.
   - **Dependabot alerts**: on. The security signal; Renovate reads these alerts (and the OSV database) and opens a
     fix PR for them.
   - **Dependabot security updates**: **off**. Renovate opens the security PRs; with both on you get two PRs per alert.
   - **Dependabot version updates**: nothing to do (they only run with a `.github/dependabot.yml`, which the
     repository doesn't have).
3. Create the labels `dependencies`, `security` and `needs-review` (Issues → Labels), or let Renovate create them.

What Renovate does ([`.github/renovate.json5`](../.github/renovate.json5)):

| PR title | For | Release |
| --- | --- | --- |
| `fix(deps): ...` | NuGet packages of `src/` (versions in `Directory.Packages.props`), runtime dependencies of `ui/` and `ui/design-system` (bundled into the tool) | patch, listed under *Dependencies* |
| `build(deps): ...` | samples, the .NET SDK in `global.json`, SourceLink and `Microsoft.Sbom.Targets`, range pinning, the monthly lock file refresh | none; listed under *Dependencies* when something else releases |
| `build(deps-dev): ...` | npm `devDependencies` (Vite, Vitest, TypeScript, Playwright, semantic-release, commitlint), test packages | none |
| `ci(deps): ...` | GitHub Actions (pinned to commit SHAs: Renovate updates the SHA and the version comment) | none |

- **Schedule:** weekly (Monday before 6:00); the root tooling, GitHub Actions and lock file maintenance monthly. At
  most 10 open PRs, 4 per hour; a release must be 3 days old before Renovate proposes it.
- **Groups:** MSTest + Microsoft.Testing.\*, NUnit (+ NUnit3TestAdapter, NUnit.Analyzers), xunit.v3\*, Aspire.\*, OpenTelemetry\*, Microsoft.Extensions.\*, the NuGet build tooling (SourceLink + Microsoft.Sbom.Targets), Vite + Vitest, Playwright,
  the release tooling (semantic-release, its plugins, commitlint), GitHub Actions, the .NET SDK. A group that mixes
  shipped and non-shipped packages is titled `fix(deps)` (Renovate picks the type that matters most).
- **Needs review:** `alien-signals` gets its own PR with the label `needs-review` (the reactive core's canary test,
  see [ui/ARCHITECTURE.md](../ui/ARCHITECTURE.md)). `MSTest.TestFramework`, `Microsoft.Testing.Platform`, `TUnit.Core`,
  `NUnit` and `xunit.v3.extensibility.core` in `Directory.Packages.props` are the minimum versions for users of `Labcoat.Testing*` / `Labcoat.TestingPlatform`:
  Renovate only lists them on the Dependency Dashboard and opens the PR when you tick the box.
- **Security:** a vulnerable dependency gets its own PR at once (not grouped, outside the schedule, no release-age wait),
  titled `... [security]` with the label `security`. Its type follows the table above, so a shipped dependency
  releases a patch.
- **Runner images:** `runs-on: ubuntu-24.04` is not updated by Renovate (the `github-runners` datasource is disabled),
  see [Runner images](#runner-images).
- **Automerge: off.** Every merge to `main` runs the release job, and the first PRs are worth reading. Once branch
  protection requires the CI checks, automerge for tooling is one rule (see the comment at the top of
  `renovate.json5`); keep shipped dependencies and `alien-signals` manual.
- After changing the config: `npx --yes --package renovate@44.115.1 -- renovate-config-validator --strict`.

### Branch protection

semantic-release puts a commit (`chore(release): x.y.z [skip ci]`, `CHANGELOG.md` and `src/Directory.Build.props`)
and a tag `vx.y.z` straight on the release branch. With a ruleset that requires pull requests or status checks on `main`
(recommended: require PRs, the CI jobs and *PR title (Angular format)*), the default `GITHUB_TOKEN` can't push that
commit: the GitHub App of [Verified release commits](#verified-release-commits-github-app) makes it and is the
ruleset's **bypass actor** (also on a tag ruleset for `v*`, if you add one). The release commit carries `[skip ci]`, so
it doesn't start CI (and with it another release) again; that also holds for the App's commit and tag pushes, which,
unlike `GITHUB_TOKEN` pushes, would otherwise start workflows.

### Verified release commits (GitHub App)

Without an App, `@semantic-release/git` commits and pushes the release commit with `GITHUB_TOKEN` as
`semantic-release-bot`: GitHub shows it as **Unverified**. With the **labcoat-release** GitHub App, the release job
makes the commit through the GitHub API instead (GraphQL `createCommitOnBranch`, [`eng/release-commit.mjs`](../eng/release-commit.mjs)),
with the App's installation token: GitHub signs it, so it shows **Verified**, authored by `labcoat-release[bot]`.

How a release runs with the App (`release.yml`, `release.config.mjs`):

1. *GitHub App key present* fails the job with a clear error when `RELEASE_APP_ID` is set but the key secret is
   missing (rather than silently releasing unverified).
2. *GitHub App token* (`actions/create-github-app-token`, input `client-id`, which takes the App ID too) mints a token
   for this repository only, limited to *Contents: write*, valid for one hour and revoked at the end of the job.
3. The *semantic-release* step then sets `LABCOAT_RELEASE_COMMIT=api` (the config uses `eng/release-commit.mjs` instead
   of `@semantic-release/git`) and `GIT_CREDENTIALS` (semantic-release's own git pushes use the App token).
4. In semantic-release's *prepare* step, after the changelog and `eng/release.mjs prepare`, the plugin creates the commit
   on the branch with `expectedHeadOid` = the commit being released, fetches it and moves the local branch to it.
   semantic-release re-reads `HEAD` after every prepare plugin, then tags that commit `v<version>`, adds its git note
   (`refs/notes/semantic-release-v<version>`, the channel) and pushes both with the App token, then publishes.
   If `main` moved on meanwhile, GitHub refuses the commit and the release stops before any tag (*"main has moved on
   from ...: no release commit and no tag were created"*); the CI run of the newer commit releases.
5. The GitHub release, its assets, the comments on released issues and PRs and the "release is failing" issue still use
   the job's `GITHUB_TOKEN`, so the App needs no *Issues* or *Pull requests* permission.

Without `RELEASE_APP_ID`, nothing changes: the git plugin and `GITHUB_TOKEN`, as before.

One-time setup:

1. **Create the App:** <https://github.com/settings/apps> (Settings → Developer settings → GitHub Apps) → **New GitHub
   App**:
   - *GitHub App name:* `labcoat-release`; *Homepage URL:* `https://github.com/eriklieben/labcoat`.
   - *Webhook:* untick **Active** (no webhook URL or secret).
   - *Repository permissions:* **Contents: Read and write**. *Metadata: Read-only* is added by itself. Nothing else (no
     account or organization permissions).
   - *Where can this GitHub App be installed?* **Only on this account**.
   - **Create GitHub App**, then note the **App ID** on the App's *General* page.
2. **Install it on the repository only:** the App's page → *Install App* → *Install* next to `eriklieben` → **Only
   select repositories** → `eriklieben/labcoat` → *Install*.
3. **Generate a private key:** the App's *General* page → *Private keys* → **Generate a private key**: a `.pem` file is
   downloaded.
4. **Store the ID and the key** (the key as a secret of the `release` environment, so only release runs see it):

   ```bash
   gh variable set RELEASE_APP_ID --repo eriklieben/labcoat --body <App ID>
   gh secret set RELEASE_APP_PRIVATE_KEY --repo eriklieben/labcoat --env release < ~/Downloads/labcoat-release.<date>.private-key.pem
   ```

5. **Delete the downloaded key:** `rm ~/Downloads/labcoat-release.*.private-key.pem` (it is in the secret now; a new key
   can be generated any time, and old ones deleted on the App's page).
6. **Check:** run *Actions → Release → Run workflow* (dry run): the log shows *"Release commit: through the GitHub API
   as the GitHub App (eriklieben/labcoat main is at ...)"*. After the next real release, the commit
   `chore(release): <version> [skip ci]` on `main` shows **Verified** with author `labcoat-release[bot]`
   (`gh api repos/eriklieben/labcoat/commits/v<version> --jq '.commit.verification.verified, .author.login'` prints
   `true` and `labcoat-release[bot]`), and the tag `v<version>` points at it.

Rotating the key: generate a new one, `gh secret set` it as in step 4, then delete the old key on the App's page.
To go back to unverified commits: `gh variable delete RELEASE_APP_ID --repo eriklieben/labcoat`.

### Runner images

All workflows run on `ubuntu-24.04`, not `ubuntu-latest`: GitHub moves `ubuntu-latest` to Ubuntu 26.04 on 2026-10-19,
which changes the preinstalled tools (and Playwright's system dependencies) under an unchanged label. `windows-latest`
and `macos-latest` stay floating: the matrix is there to find platform differences early. Renovate doesn't touch
`runs-on` (`github-runners` is disabled in `.github/renovate.json5`). To move to the next Ubuntu LTS, by hand: replace
`ubuntu-24.04` in every file under `.github/workflows/` (also the `matrix.os == 'ubuntu-24.04'` conditions in
`ci.yml`), open a `ci:` PR and check that every job, including the smoke test and the Playwright checks, passes.

### Code analysis (Snyk / SonarQube Cloud)

**SonarQube Cloud** is prepared but off: CI's `sonar` job is skipped until the repository variable `SONAR_ORGANIZATION`
is set. Turn it on after the repository is public ([Going public](#going-public)), with the free **OSS plan** (public
open source projects: branch and pull request analysis, custom quality gates, unlimited members). Create the
organization only then: an organization created on the Free plan can't be switched to the OSS plan. The Free plan
(private projects up to 50k lines of code per organization, Labcoat is around 43k after the exclusions) analyzes only
`main` and PRs into it and has no custom quality gates; more costs a Team plan (from about $32 a month for 100k lines).

What the job does (`sonar` in [`ci.yml`](../.github/workflows/ci.yml)): after the `ui` job and the build matrix, on
ubuntu-24.04, for pushes to `main` and for pull requests from branches of this repository. SonarScanner for .NET
(`dotnet-sonarscanner` in [`.config/dotnet-tools.json`](../.config/dotnet-tools.json), updated by Renovate as
`ci(deps)`) wraps one Release build (`begin` / `dotnet build` / `end`) and analyzes the C# of `Labcoat.slnx` plus the
TypeScript, JavaScript and CSS (multi-language analysis). It runs no tests: it imports the coverage of the existing
jobs, the raw Cobertura reports of `eng/coverage.mjs` (`sonar.cs.cobertura.reportsPaths`, no conversion needed) and
Vitest's `ui/coverage/lcov.info` (`sonar.javascript.lcov.reportPaths`: singular *report*, unlike the C# key; with
the wrong key the UI silently shows 0% coverage). The coverage gates (85% per assembly, the Vitest thresholds) don't
change. The settings are the `/d:` arguments of the *Begin analysis* step (the .NET scanner doesn't read a
`sonar-project.properties`). The release job doesn't wait for it, and it isn't a required check.

The analysis settings, and why:

- **Excluded** (`sonar.exclusions`, main code only): samples, benchmarks, build output, generated code, `wwwroot`,
  `node_modules`, the mock backend `ui/src/mock/**` (only loaded with `?mock`), the design system gallery
  `ui/design-system/gallery/**` (a showcase, not shipped), the build and release scripts (`eng/**`, the root `*.mjs`)
  and the tool configuration `ui/*.config.ts`.
- **Duplication**: `sonar.cpd.exclusions` leaves out `ui/design-system/src/themes/families.ts`, the theme data, whose
  families repeat the same token blocks on purpose.
- **Test code** (`tests/**`, `ui/test/**`) stays in the analysis, classified as test code by the scanner: test rules
  apply, it doesn't count as lines of code or towards coverage. The test patterns in `sonar.exclusions` are only a
  guard in case a test file is ever classified as main code.
- **Compiler and analyzer warnings aren't imported** (`sonar.cs.roslyn.ignoreIssues=true`). The build job already
  gates them with `-warnaserror`, so a warning can't reach `main`; importing them would add the info-level .NET
  analyzer suggestions (CA*, MSTEST*, SYSLIB*) as a second, ungated issue list next to Sonar's own C# rules. Sonar's
  C# rules (`csharpsquid:*`) are still reported.
- **The three adapter packages** `src/Labcoat.Testing.{MSTest,NUnit,XUnit}` reference a test framework, so the scanner
  would treat them as test projects (no main-code rules, coverage or lines of code). Their csproj files set
  `<SonarQubeTestProject>false</SonarQubeTestProject>`: an MSBuild property, it can't be set with `/d:`.
- **Issue ignores** (`sonar.issue.ignore.multicriteria`), only for conventions and false positives that can't be fixed
  without making the code worse:
  - `typescript:S1444` (public static fields should be read-only) in `ui/**/components/**`: `static defaults`,
    `hostClass` and `observedAttributes` are the custom-element convention, and subclasses override them.
  - `typescript:S2245` (`Math.random`) in `ui/src/confetti.ts` (the confetti animation) and `ui/src/transport.ts`
    (reconnect backoff jitter): neither is security-relevant, and a predictable value does no harm. The mock
    backend uses it too, but it is excluded.
  - `typescript:S4782` (redundant `| undefined` on an optional property) in `ui/design-system/src/**`: the `undefined`
    comes from shared aliases (`Child`, `ClassValue`, `AttrValue`, `MaybeReactive<T | undefined>`), not from the
    declarations. Fixing it would mean writing the aliases out at every property.
  - `typescript:S1874` (deprecated API) in `ui/design-system/src/components/copy-button.ts`: `execCommand("copy")` is
    the fallback when `navigator.clipboard` is unavailable (an insecure context). There's no non-deprecated
    replacement.
  - `typescript:S7758` (`codePointAt`) in `ui/src/util.ts`: the project accent colour hash works over UTF-16 code units
    (Java's `String.hashCode`). With code points, the colours of project ids with characters outside the BMP would
    change.
  - `typescript:S5906` (more specific matcher) in `ui/test/app/tree-filter.test.ts`: one identity check avoids
    `.not.toBe` on purpose, because that matcher's failure message deep-compares two large trees.

  Nothing else is ignored or marked in SonarQube Cloud: with these settings the local analysis has no open issues and
  no security hotspots. A new false positive gets the same treatment: fix the code if that doesn't make it worse,
  otherwise add a scoped ignore here and in `ci.yml` with its reason. On first connect, check that the Cloud analysis
  matches. If it reports issues the local Community Build didn't (Cloud sometimes runs newer rules), handle them the
  same way: fix them, or mark them in Cloud (*Accept* or *False positive*, with a comment). Markings made in a local
  SonarQube don't carry over.
- **Rule thresholds** are the *Sonar way* defaults (cognitive complexity 15, duplicated string literals 3, and so
  on): the code is kept within them. The .NET scanner can't change rule parameters from the command line; changing one
  would need a custom quality profile in SonarQube Cloud (Quality Profiles, copy *Sonar way*, change the rule, set it as
  the project's profile), which isn't needed now.

One-time steps:

1. Sign in at https://sonarcloud.io with GitHub, **+ → Create new organization** bound to the GitHub account
   `eriklieben`, install the SonarQube Cloud GitHub App on `eriklieben/labcoat` only, and at the plan step pick
   **Get SonarQube for OSS** (needs the public repository).
2. **Analyze new project** → `labcoat`. Keep the project key `eriklieben_labcoat` (otherwise set the repository
   variable `SONAR_PROJECT_KEY`). Project → Administration → Analysis Method: turn **Automatic Analysis off** (it can't
   import coverage, and with it on the CI analysis fails).
3. New code definition: *Previous version* or *Number of days* (30). **AI Code Assurance**: Project → Administration →
   AI Code Assurance → *This project contains AI-generated code*. The project then needs a quality gate qualified for
   AI code: the built-in **Sonar way for agentic AI** (it replaced the legacy *Sonar way for AI Code*). Its conditions,
   all on new code: no new reliability issues of Low severity or higher, no new security issues of Low or higher, no new
   maintainability issues of Medium or higher, coverage at least 80%, duplication at most 3%, and no new dependency
   risks of Low or higher (that one needs SonarQube Advanced Security; without it the condition is greyed out and
   skipped). Plan: Sonar's pricing page lists AI Code Assurance under the Team plan, while the SonarQube Cloud docs
   describe the AI code badge as available on every plan and the OSS plan as having the paid features; check the
   project's Administration menu once it's on the OSS plan. Don't make the check required until a few months of PRs
   show it's not noisy.
4. A token: My Account → Security → generate a token for the project (or a scoped organization token), then:

   ```bash
   gh secret set SONAR_TOKEN --repo eriklieben/labcoat          # paste the token at the prompt, never in the command
   gh variable set SONAR_ORGANIZATION --repo eriklieben/labcoat --body "<organization key>"
   ```

5. Nothing to add to the Actions allow-list: the job uses GitHub-owned actions only (`actions/cache`, pinned) and the
   scanner as a .NET tool. `SonarSource/sonarqube-scan-action` isn't used: it doesn't analyze C#.
6. Once public, badges for `README.md`:

   ```markdown
   [![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=eriklieben_labcoat&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=eriklieben_labcoat)
   [![Coverage](https://sonarcloud.io/api/project_badges/measure?project=eriklieben_labcoat&metric=coverage)](https://sonarcloud.io/summary/new_code?id=eriklieben_labcoat)
   ```

**Fork pull requests** get no secrets, so the job is skipped for them (the other jobs and CodeQL still run); the
analysis of `main` after the merge picks the change up. A `workflow_run` analysis of fork PRs would have to build the
fork's code (MSBuild runs project code) with the token in reach: not worth it.

**Snyk: not set up.** Everything it would add is covered: Dependabot alerts (GitHub Advisory Database) plus Renovate
(also OSV) for vulnerable NuGet and npm dependencies, with security PRs; CodeQL (free once public) for SAST next to
Snyk Code. With central package management Snyk can't open fix PRs (a `PackageReference` without a version), so it would
only report what Dependabot already reports. If you want it anyway (e.g. its own vulnerability database, or license
checks on a paid plan): the **Snyk GitHub App** import (Snyk → Integrations → GitHub, no workflow, no secret, resolves
`Directory.Packages.props` with the .NET SDK and reads `ui/package-lock.json`), set *Fix PRs* and *Dependency upgrade
PRs* off so they don't compete with Renovate. Public repositories don't count towards the Free plan's test limits.
A CLI job in Actions would need the secret `SNYK_TOKEN`, the allow-list pattern `snyk/actions/*` and a SHA pin.

## Versioning

Labcoat follows [Semantic Versioning](https://semver.org) from 1.0.0 on, with semantic-release's default rules (no
custom release rules in `release.config.mjs`):

| Commit | Release |
| --- | --- |
| `fix: ...`, `perf: ...`, `revert: ...` | patch: 1.4.2 → 1.4.3 |
| `feat: ...` | minor: 1.4.2 → 1.5.0 |
| `feat!: ...` / `BREAKING CHANGE:` footer | major: 1.4.2 → 2.0.0 |
| `docs`, `chore`, `refactor`, `test`, `ci`, `build`, `style` | no release |

A major version covers the command line, the settings files and the public APIs of the library packages: think twice
before a `!`, and say in the `BREAKING CHANGE:` footer what users have to change.

## Package validation

`dotnet pack` runs [package validation](https://learn.microsoft.com/dotnet/fundamentals/apicompat/package-validation/overview)
for the library packages: `Labcoat.Aspire.Hosting`, `Labcoat.Testing`, `Labcoat.Testing.MSTest`, `.TUnit`, `.NUnit`,
`.XUnit` and `Labcoat.TestingPlatform` (settings in `src/Directory.Build.props`). Each package is compared with the same
package at `<PackageValidationBaselineVersion>`, downloaded from nuget.org on restore: a public type or member that was
removed or changed fails the pack (`CP0002`, `CP0006`, ...). That happens in CI (`dotnet pack --no-build`, every PR)
and in the release (`eng/release.mjs prepare`), so a break can't be released by accident. The `Labcoat` tool package is
left out (`EnablePackageValidation=false` in `Labcoat.csproj`): it has no `lib/` assembly and no public API, its
contract is the command line.

**The baseline moves by itself.** semantic-release passes the previous release (`lastRelease.version`) to
`node eng/release.mjs prepare <version> --baseline=<previous>`: that release's pack is validated against it, and the
version is written to `src/Directory.Build.props` and committed with the release. Between releases, builds therefore
compare with the second-to-last release, which is always on nuget.org by then. With no previous release
(`--baseline=` empty, as for 1.0.0, the first release after the history was squashed) the pack runs without a
baseline and the committed baseline stays as it is: `0.1.0-preview.8`, the last preview (unlisted on nuget.org, but
restore still finds it by its exact version), until the release after 1.0.0 writes `1.0.0`.

**An intentional break.** It releases the next major version (see [Versioning](#versioning)) and must be marked:

1. Make the change. `dotnet pack` fails and lists what broke.
2. Write the commit (PR title) as a breaking change: `feat(testing)!: ...` with a `BREAKING CHANGE:` footer that tells
   users what to do.
3. Accept the break for the project: `dotnet build <project> -c Release && dotnet pack <project> -c Release --no-build -p:ApiCompatGenerateSuppressionFile=true`
   writes `CompatibilitySuppressions.xml` next to the project file. Review it (only the breaks you meant) and commit it in
   the same PR.
4. After the *next* release (when the baseline has moved past the break) the file is no longer needed: delete it
   (unneeded suppressions are permitted, `ApiCompatPermitUnnecessarySuppressions`, so it doesn't fail anything
   meanwhile).

To skip the baseline comparison for one project instead (e.g. a rewrite where a suppression file would be noise), set
`<DisablePackageBaselineValidation>true</DisablePackageBaselineValidation>` in its project file, with a comment, and
remove it again after the next release. The same goes for a **new package**: it isn't on nuget.org at the baseline
version (restore fails with `NU1102: Unable to find package ... with version (= <baseline>)`), so it opts out until the
baseline is a release that contains it, i.e. until the second release after it was added.

Locally: `dotnet pack Labcoat.slnx -c Release -o <dir>` (or `node eng/release.mjs prepare 0.0.0-local.1`) runs the same
check.

## Release notes

`CHANGELOG.md` and the GitHub release get the same notes (the `conventionalcommits` preset, configured in
`release.config.mjs`): a `##` heading per version, then *⚠ BREAKING CHANGES*, *Features*, *Bug Fixes*, *Performance*,
*Reverts* and *Dependencies* (`fix(deps)` and `build(deps)`). Scopes are shown in bold. `docs`, `style`, `chore`,
`refactor`, `test`, `build` and `ci` commits are left out unless they carry a breaking change. For example:

```markdown
## [2.0.0](https://github.com/eriklieben/labcoat/compare/v1.4.2...v2.0.0) (2026-10-09)

### ⚠ BREAKING CHANGES

* **cli:** the --theme option is now --ui-theme; update scripts and launch profiles.

### Features

* **cli:** rename --theme to --ui-theme ([abc1234](https://github.com/eriklieben/labcoat/commit/abc1234))
* **ui:** add a duration filter to the test tree ([#41](https://github.com/eriklieben/labcoat/issues/41)) ([abc1235](https://github.com/eriklieben/labcoat/commit/abc1235))

### Bug Fixes

* **host:** keep the warm test host alive after a build error, closes [#37](https://github.com/eriklieben/labcoat/issues/37) ([abc1236](https://github.com/eriklieben/labcoat/commit/abc1236))

### Dependencies

* **deps:** update dependency alien-signals to v3.3.0 ([abc1237](https://github.com/eriklieben/labcoat/commit/abc1237))
```

## Verifying packages

After semantic-release, the release job attests the build provenance of the released `.nupkg` and `.snupkg` files
([`actions/attest-build-provenance`](https://github.com/actions/attest-build-provenance)): a Sigstore-signed SLSA
statement that ties each file's SHA-256 to this repository, the workflow and the commit. Anyone can check a package:

```bash
# a release tag such as v1.0.0, or the newest release (prereleases of next / beta: pass their tag)
tag=$(gh release view --repo eriklieben/labcoat --json tagName --jq .tagName)
gh release download "$tag" --repo eriklieben/labcoat --pattern '*.nupkg' --pattern '*.snupkg' --dir release
gh attestation verify "release/Labcoat.${tag#v}.nupkg" --repo eriklieben/labcoat
```

The SBOMs ([SBOM](#sbom)) are attested too: each `.nupkg` with the SPDX SBOM it carries, and the three packages that
bundle the UI with the UI's CycloneDX SBOM (job `sbom-attestation`, `actions/attest` with `sbom-path`). The SBOM files
of the release also have build provenance, like the packages.

```bash
gh attestation verify "release/Labcoat.${tag#v}.nupkg" --repo eriklieben/labcoat --predicate-type https://spdx.dev/Document/v2.2
gh attestation verify "release/Labcoat.${tag#v}.nupkg" --repo eriklieben/labcoat --predicate-type https://cyclonedx.org/bom
# the attested SBOM itself (the predicate), e.g. its package list:
gh attestation verify "release/Labcoat.Testing.${tag#v}.nupkg" --repo eriklieben/labcoat \
  --predicate-type https://spdx.dev/Document/v2.2 --format json --jq '.[0].verificationResult.statement.predicate.packages[].name'
```

Artifact attestations need a public repository (or GitHub Enterprise Cloud), so the release job skips the provenance
step, and the `sbom-attestation` job doesn't run, while the repository is private: releases made before it went public
have no attestation to verify (their SBOMs are still in the packages and on the GitHub release).

The attestations are listed under the repository's **Actions → Attestations**. Verify the files of the GitHub release
(or the `release-nupkgs` workflow artifact): nuget.org adds a repository signature to each package, which changes the
file, so a package downloaded from nuget.org doesn't match its attestation (check that one with
`dotnet nuget verify --all <file>`).

If the attestation step fails after a release, the release itself is fine but has no attestation; re-running the job
doesn't add it (semantic-release then finds nothing to release). The next release is attested again. A failed
`sbom-attestation` job can be re-run on its own (*Re-run failed jobs*): it only needs the `release-nupkgs` artifact.

## SBOM

Every release ships a software bill of materials for each package, in two places: inside the package and as a GitHub
release asset.

| What | Where | Made by |
| --- | --- | --- |
| SPDX 2.2 SBOM of each package: every file in the `.nupkg` with its SHA-256 (also the bundled runner and UI of `Labcoat.Aspire.Hosting` / `Labcoat.TestingPlatform`), and the NuGet packages it depends on, transitively, as `pkg:nuget` purls | in the `.nupkg` at `_manifest/spdx_2.2/manifest.spdx.json` (+ `.sha256`); release asset `<id>.<version>.spdx.json` | [`Microsoft.Sbom.Targets`](https://github.com/microsoft/sbom-tool) (sbom-tool's MSBuild integration), on every `dotnet pack` |
| CycloneDX SBOM of the web UI's npm production dependencies (alien-signals, highlight.js, the Fluent UI icons, the design system) | in `Labcoat`, `Labcoat.Aspire.Hosting` and `Labcoat.TestingPlatform` at `sbom/ui.cdx.json` next to `wwwroot`; release asset `labcoat-ui.<version>.cdx.json` | `npm sbom --omit dev` (built into npm 10+) in `eng/release.mjs prepare` |

How it works:

- **Package SBOM.** `src/Directory.Build.props` (*SBOM*) turns on `GenerateSBOM` for every package; after `Pack`,
  `Microsoft.Sbom.Targets` unzips the `.nupkg`, runs sbom-tool and zips it again (the `.snupkg` is left alone). Supplier
  `Erik Lieben`, namespace `https://github.com/eriklieben/labcoat/<id>/<version>/<unique id>`, no network lookups.
  CI's `dotnet pack --no-build` and the release pack both make it.
- **What component detection sees.** By default sbom-tool scans the project folder, which lists the wrong packages:
  the nuspec files each pack leaves in `obj/` (the package itself and every older version packed in the clone), the
  build-only references (SourceLink, `Microsoft.Sbom.Targets`), the `PackageDownload`s (the package validation baseline
  and the net8.0 targeting packs), and *not* the `Labcoat.Testing` package that the four adapters depend on (a project
  reference in the source). `src/Directory.Build.targets` therefore gives it only a rewritten copy of
  `project.assets.json` (`obj/sbom-components/`): build-only references and downloads removed, project references that
  flow to the package turned into package references of the same version. The result matches the `<dependencies>` of
  each nuspec, resolved transitively. The bundled runner (`Labcoat`) has no NuGet dependencies of its own; its files
  are in the file list.
- **UI SBOM.** The UI is built from npm packages, which aren't NuGet inputs, so the package SBOM lists the built UI
  files, not the npm packages. `eng/release.mjs prepare` writes the CycloneDX SBOM from `ui/package-lock.json`
  (`--package-lock-only`, production dependencies only) and passes it to the pack as `-p:LabcoatUiSbom=<file>`
  (`src/Labcoat/Labcoat.csproj` publishes it next to `wwwroot`). A plain `dotnet pack` (CI, local) has no UI SBOM. The
  fonts in `ui/public/fonts` aren't npm packages: they are in the file list of the package SBOM and in
  `THIRD-PARTY-NOTICES.md`.
- **Release assets.** `eng/release.mjs prepare` checks every package (the SBOM is there, SPDX 2.2, for this package
  and version; the UI SBOM is in the three UI packages; it fails the release otherwise) and copies each SBOM to
  `artifacts/<id>.<version>.spdx.json`. `release.config.mjs` attaches `artifacts/*.spdx.json` and
  `artifacts/*.cdx.json` to the GitHub release; they are also in the `release-nupkgs` workflow artifact.
- **Attestations:** see [Verifying packages](#verifying-packages).

Look at one:

```bash
unzip -p Labcoat.Testing.<version>.nupkg _manifest/spdx_2.2/manifest.spdx.json \
  | jq -r '.packages[] | select(.SPDXID != "SPDXRef-RootPackage") | "\(.name) \(.versionInfo)"' | sort
unzip -p Labcoat.<version>.nupkg tools/net10.0/any/sbom/ui.cdx.json | jq -r '.components[] | "\(.name) \(.version)"'
```

Every pack makes the SBOM again, with a new document namespace and creation time, so two packs of the same commit
are not byte-identical (the packages weren't before either: the zip entry times differ).

## History: the previews

Before 1.0, Labcoat was released as previews: `0.1.0-preview.1` to `.4` packed and uploaded by hand, `.5` to `.8` by
semantic-release from `main` as a prerelease branch (with a placeholder `stable` branch and tag `v0.0.0`, because
semantic-release needs one normal release branch). For 1.0.0 the history was squashed into one commit (`feat: initial
move to GitHub`), the preview tags and GitHub releases and the `stable` branch were deleted, and `main` became the
normal release branch: with no tag left, semantic-release's first release was `1.0.0`. The preview packages are
unlisted on nuget.org.

While the repository is **private**, the build provenance attestation step and the SBOM attestations are skipped
(attestations need a public repository or GitHub Enterprise Cloud); the SBOMs themselves are made and released.

## Prereleases

Push a `beta` (or `next`) branch, e.g. `git switch -c beta main && git push -u origin beta`. CI on that branch
releases prereleases: `1.3.0-beta.1`, `1.3.0-beta.2`, ... as NuGet prerelease versions and GitHub prereleases. Merge
`beta` into `main` (a merge commit, not a squash, so the commits keep their tags) to release `1.3.0`, then delete
or reset the branch.

## Dry run

- **GitHub:** Actions → **Release** → *Run workflow* → branch `main`, *Dry run* checked (the default), *commit* empty.
  The log shows the commits it analyzed, the next version and the release notes. Unchecking *Dry run* releases for
  real; that needs a CI run of the branch head in which every job except *Release* passed.
- **Local:** `GITHUB_TOKEN=$(gh auth token) npx semantic-release --dry-run --no-ci` (needs push access: semantic-release
  checks it first). `node eng/release.mjs prepare 0.0.0-local.1` runs just the packing step, with package validation
  against the committed baseline (add `--baseline=<version>` to try another one; revert `src/Directory.Build.props`
  afterwards).

## When something goes wrong

- **Where:** CI's *Release* job only starts the release; the release itself is a run of the **Release** workflow
  (`Release of <sha>`), linked from the CI run's summary. A run that says the branch has moved on released nothing on
  purpose.
- **Failed before the tag** (verify, UI build, pack, package validation, or the push of the release commit): nothing
  was published. Fix the cause and push it (the next CI run releases everything since the last tag), or, for a
  transient failure such as NuGet credentials, re-run the failed jobs of the Release run (or Run workflow by hand with
  *Dry run* unchecked). semantic-release also opens an issue, *The automated release is failing*, which the next
  successful release closes.
- **Package validation failed in the release** (`CP...` errors in `eng/release.mjs prepare`): an API break without a
  suppression; see [Package validation](#package-validation).
- **NuGet login failed:** see [Troubleshooting trusted publishing](#troubleshooting-trusted-publishing).
- **The NuGet push failed after the tag** (the GitHub release exists): the packages are attached to the GitHub release
  and to the workflow run (artifact `release-nupkgs`). Push them by hand:

  ```bash
  gh release download vX.Y.Z --pattern '*.nupkg' --pattern '*.snupkg' --dir release
  dotnet nuget push "release/*.nupkg" --api-key <key> --source https://api.nuget.org/v3/index.json --skip-duplicate
  ```

- **A broken version on nuget.org:** versions can't be deleted, only unlisted (nuget.org → package → Manage). Merge a
  `fix:` to release the next patch.
- **The wrong version came out** (e.g. a `feat` that should have been a `fix`): versions aren't reused. Keep going;
  for future PRs, check the PR title before merging.

## Going public

A checklist for switching the repository from private to public. Nothing here is automated; do it in this order.

1. **Squash the history to one commit** (done, for 1.0.0): one root commit `feat: initial move to GitHub`, the old
   tags (`v0.0.0`, `v0.1.0-preview.*`), their GitHub releases and the `stable` branch deleted, `main` force-pushed;
   see [History: the previews](#history-the-previews). `npx semantic-release --dry-run --no-ci` then says "There is
   no previous release, the next release version is 1.0.0".
2. **Normal releases instead of previews** (done, for 1.0.0): `release.config.mjs` has `"main"` as its release branch
   and no pre-1.0 rule; the first release, 1.0.0, is packed without a baseline (see
   [Package validation](#package-validation)).
3. **Attestations** turn on by themselves: the build provenance step and the `sbom-attestation` job in `release.yml`
   only skip while the repository is private. The next release has them ([Verifying packages](#verifying-packages)).
4. **Delete the old Actions runs** first: their logs and artifacts (test results, packages, coverage) become public
   for up to 90 days. They hold no secrets (GitHub masks those), but they are the private phase's internals:
   `gh run list --repo eriklieben/labcoat --limit 500 --json databaseId --jq '.[].databaseId' | xargs -n1 gh run delete --repo eriklieben/labcoat`.
   Then **make it public:** Settings → General → Danger zone → Change visibility.
5. **Fork pull requests:** Settings → Actions → General → *Approval for running fork pull request workflows from
   contributors* → **Require approval for all outside collaborators**.
6. **Code security** (free for public repositories): Settings → Code security → **CodeQL analysis** → Set up (default
   setup, languages C# and JavaScript/TypeScript, plus Actions); **Secret scanning** on, and **Push protection** on;
   **Private vulnerability reporting** on (SECURITY.md sends reporters to Security → *Report a vulnerability*, which
   only exists on public repositories).
7. **Branch ruleset for `main`** (Settings → Rules → Rulesets → New branch ruleset, target the default branch): require
   a pull request (squash merge only), require the status checks *UI (typecheck, checks, build)*, *Build and test
   (ubuntu-24.04 / windows-latest / macos-latest)*, *Smoke test (...)* (the same three) and *PR title (Angular format)*
   (not *Release*: it is skipped on pull requests), and block force pushes and deletion. Add the **labcoat-release** GitHub App as the
   **bypass actor** (*Bypass list* → *Add bypass* → *Apps* → `labcoat-release`, mode *Always*): it makes the release
   commit and pushes the tag ([Verified release commits](#verified-release-commits-github-app); `RELEASE_APP_ID` must
   be set). Without the bypass, the release fails when it creates `chore(release): ...`. A tag ruleset for `v*`, if you
   add one, needs the same bypass. Optionally also require *signed commits*: the App's release commits are signed by
   GitHub.
8. **Require SHA pinning:** Settings → Actions → General → *Require actions to be pinned to a full-length commit SHA*.
   Every `uses:` in `.github/workflows/` is already pinned (`owner/repo@<sha> # vX.Y.Z`) and Renovate keeps it that way
   (`helpers:pinGitHubActionDigests`), so this can be turned on right away; check the next Renovate PR keeps the format.
9. **NuGet trusted publishing:** the policy of a private repository may still be *temporarily active*; a release after
   the switch makes it permanent. Nothing changes in the workflow.
10. **README and links:** check the badges and links in `README.md`, the package READMEs (`src/*/README.md`, shown on
    nuget.org) and `docs/` that pointed at the private repository (CI badge, release and attestation links, `gh`
    commands with `--repo eriklieben/labcoat`): they resolve for everyone once the repository is public; remove any
    that only made sense while it was private.
11. **SonarQube Cloud** (free for public projects): the one-time steps in
    [Code analysis (Snyk / SonarQube Cloud)](#code-analysis-snyk--sonarqube-cloud), then add its badges to `README.md`.
