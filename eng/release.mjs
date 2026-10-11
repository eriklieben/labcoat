// Release steps for semantic-release (called by @semantic-release/exec, see release.config.mjs). Cross-platform (Node 22,
// no dependencies). Each step can also be run by hand, e.g. to check a pack without releasing anything.
//
//   node eng/release.mjs verify [--dry-run]   tools are there; NUGET_API_KEY is set (not checked in a dry run)
//   node eng/release.mjs prepare <version> [--baseline=<version>]
//                                             set <VersionPrefix> in src/Directory.Build.props, build the UI (ui/),
//                                             `dotnet pack` the eight packages with -p:Version=<version> into artifacts/.
//                                             --baseline: the package validation baseline (semantic-release passes
//                                             lastRelease.version): written to <PackageValidationBaselineVersion> and
//                                             used for this pack; `--baseline=` (no previous release) packs without
//                                             baseline validation (and the committed baseline is left as it
//                                             is). Without the option the committed baseline is used.
//                                             The package READMEs are packed with their repository links pinned to
//                                             the release tag (see pinReadme); the files are restored afterwards.
//                                             SBOMs (docs/releasing.md, "SBOM"): the UI's npm production
//                                             dependencies (`npm sbom`, CycloneDX) go into the packages that bundle
//                                             the UI and to artifacts/labcoat-ui.<version>.cdx.json; each package's
//                                             SPDX SBOM (Microsoft.Sbom.Targets, _manifest/spdx_2.2/ in the .nupkg)
//                                             is checked and copied to artifacts/<id>.<version>.spdx.json. Both are
//                                             release assets.
//   node eng/release.mjs readmes <version>    dry run of that rewrite: print each package README link it would pin,
//                                             write nothing. Exit code 1 if a README would keep a link to `main` that
//                                             should have been pinned.
//   node eng/release.mjs publish <version>    `dotnet nuget push` artifacts/*.nupkg (+ .snupkg symbols) to nuget.org
//
// Environment: NUGET_API_KEY (publish; from NuGet trusted publishing or a repository secret), NUGET_SOURCE (publish;
// default https://api.nuget.org/v3/index.json, a local folder works for testing), LABCOAT_RELEASE_SKIP_UI=1 (prepare;
// reuse an existing src/Labcoat/wwwroot build).
//
// `publish` prints a JSON release object on stdout (all tool output goes to stderr): semantic-release lists it in the
// GitHub release comments. Exit code 0 on success, 1 on failure, 2 on bad usage.
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { inflateRawSync } from "node:zlib";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const artifacts = path.join(root, "artifacts");
const props = path.join(root, "src", "Directory.Build.props");
const packages = ["Labcoat", "Labcoat.Aspire.Hosting", "Labcoat.Testing", "Labcoat.Testing.MSTest", "Labcoat.Testing.TUnit", "Labcoat.Testing.NUnit",
  "Labcoat.Testing.XUnit", "Labcoat.TestingPlatform"];
const semver = /^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/;

const log = (...args) => console.error("[release]", ...args);
const fail = (message) => {
  console.error(`[release] error: ${message}`);
  process.exit(1);
};

// Runs a command with its output on stderr, so stdout stays free for the JSON result of `publish`.
function run(command, args, { cwd = root, redact = [] } = {}) {
  const shown = args.map((a) => (redact.includes(a) ? "***" : a)).join(" ");
  log(`> ${command} ${shown}${cwd === root ? "" : `   (in ${path.relative(root, cwd)})`}`);
  const result = spawnSync(command, args, {
    cwd,
    stdio: ["ignore", process.stderr, process.stderr],
    // npm is a .cmd script on Windows.
    shell: process.platform === "win32",
  });
  if (result.error) fail(`${command}: ${result.error.message}`);
  if (result.status !== 0) fail(`${command} ${args[0] ?? ""} exited with code ${result.status}`);
}

function parseVersion(version) {
  const m = semver.exec(version ?? "");
  if (!m) {
    console.error(`usage: node eng/release.mjs ${process.argv[2]} <semver version, e.g. 1.2.0 or 1.2.0-beta.1>`);
    process.exit(2);
  }
  return { version, prefix: `${m[1]}.${m[2]}.${m[3]}` };
}

// Package READMEs: nuget.org shows the README packed into each version, so its images and documentation links should
// show that version too, not whatever `main` has now. At pack time (prepare only; CI's plain `dotnet pack` keeps the
// committed `main` links) links into the repository are pinned to the release tag v<version>, which semantic-release
// puts on the release commit, the commit these files are packed from:
//   raw.githubusercontent.com/eriklieben/labcoat/main/<path>          -> .../v<version>/<path>          (images)
//   github.com/eriklieben/labcoat/blob|tree/main/<path>               -> .../blob|tree/v<version>/<path> (docs, source)
// Kept as they are, because they should always show the latest: the repository root, issues, releases and other
// non-file pages (not matched), and CHANGELOG.md (lists the versions after this one too) and SECURITY.md (the current
// reporting policy and contacts).
const repo = "eriklieben/labcoat";
const alwaysLatest = new Set(["CHANGELOG.md", "SECURITY.md"]);
const readmeLinks = new RegExp(
  String.raw`(https://raw\.githubusercontent\.com/${repo}/|https://github\.com/${repo}/(?:blob|tree)/)main/([^\s)"'>\]]*)`, "g");
const packageReadmes = () => packages.map((id) => path.join(root, "src", id, "README.md")).filter((f) => existsSync(f));

// Returns the README text with its repository links pinned to `tag`, and the links it changed and kept.
function pinReadme(text, tag) {
  const pinned = [];
  const kept = [];
  const result = text.replace(readmeLinks, (url, prefix, file) => {
    if (alwaysLatest.has(file.split(/[?#]/)[0])) {
      kept.push(url);
      return url;
    }
    const to = `${prefix}${tag}/${file}`;
    pinned.push([url, to]);
    return to;
  });
  return { text: result, pinned, kept };
}

// Rewrites the package READMEs in place for the pack and returns a function that restores them. The originals are
// also restored when the process exits early (fail() calls process.exit; Ctrl+C / SIGTERM), so the working tree and
// the release commit keep the `main` links.
function pinPackageReadmes(tag) {
  const originals = new Map(packageReadmes().map((f) => [f, readFileSync(f, "utf8")]));
  let restored = false;
  const restore = () => {
    if (restored) return;
    restored = true;
    for (const [f, text] of originals) writeFileSync(f, text);
    log(`package READMEs restored`);
  };
  const onSignal = (signal) => process.exit(signal === "SIGINT" ? 130 : 143);
  process.on("exit", restore);
  process.once("SIGINT", onSignal);
  process.once("SIGTERM", onSignal);
  for (const [f, text] of originals) {
    const { text: pinnedText, pinned } = pinReadme(text, tag);
    if (pinned.length) writeFileSync(f, pinnedText);
    log(`${path.relative(root, f)}: ${pinned.length} link(s) pinned to ${tag}`);
  }
  return () => {
    restore();
    process.off("exit", restore);
    process.off("SIGINT", onSignal);
    process.off("SIGTERM", onSignal);
  };
}

function readmes(version) {
  const tag = `v${version}`;
  let leftover = 0;
  for (const f of packageReadmes()) {
    const { text, pinned, kept } = pinReadme(readFileSync(f, "utf8"), tag);
    console.log(path.relative(root, f));
    for (const [from, to] of pinned) console.log(`  pin   ${from}\n     -> ${to}`);
    for (const url of kept) console.log(`  keep  ${url}`);
    // Anything still pointing at main after the rewrite (another URL form) would show `main` on nuget.org.
    for (const m of text.matchAll(new RegExp(String.raw`https://[^\s)]*${repo}[^\s)]*/main/[^\s)]*`, "g"))) {
      if (!kept.includes(m[0])) {
        console.log(`  LEFT  ${m[0]}`);
        leftover++;
      }
    }
  }
  if (leftover) fail(`${leftover} link(s) to main that the rewrite doesn't pin`);
}

// SBOMs (docs/releasing.md, "SBOM").
const spdxManifest = "_manifest/spdx_2.2/manifest.spdx.json";
// The packages that bundle the UI (wwwroot), so they also carry its npm SBOM (sbom/ui.cdx.json next to wwwroot).
const uiPackages = ["Labcoat", "Labcoat.Aspire.Hosting", "Labcoat.TestingPlatform"];
const uiSbomFile = (version) => path.join(artifacts, `labcoat-ui.${version}.cdx.json`);
const spdxFile = (id, version) => path.join(artifacts, `${id}.${version}.spdx.json`);
const isSbomFile = (f) => f.endsWith(".spdx.json") || f.endsWith(".cdx.json");

// The entries of a .nupkg (a zip file): name -> () => Buffer. Enough for NuGet packages: no zip64, no encryption,
// stored or deflated entries only.
function readZip(file) {
  const zip = readFileSync(file);
  let eocd = zip.length - 22;
  while (eocd >= 0 && zip.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) fail(`${path.basename(file)}: not a zip file`);
  const entries = new Map();
  let at = zip.readUInt32LE(eocd + 16);
  for (let i = 0, count = zip.readUInt16LE(eocd + 10); i < count; i++) {
    if (zip.readUInt32LE(at) !== 0x02014b50) fail(`${path.basename(file)}: bad zip central directory`);
    const method = zip.readUInt16LE(at + 10);
    const size = zip.readUInt32LE(at + 20);
    const nameLength = zip.readUInt16LE(at + 28);
    const local = zip.readUInt32LE(at + 42);
    const name = zip.toString("utf8", at + 46, at + 46 + nameLength);
    entries.set(name, () => {
      const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
      const data = zip.subarray(start, start + size);
      if (method === 0) return data;
      if (method === 8) return inflateRawSync(data);
      fail(`${path.basename(file)}: ${name}: unsupported zip compression method ${method}`);
    });
    at += 46 + nameLength + zip.readUInt16LE(at + 30) + zip.readUInt16LE(at + 32);
  }
  return entries;
}

// The UI's production dependencies (no devDependencies: Vite, Vitest, TypeScript, ... aren't shipped) as CycloneDX,
// from ui/package-lock.json: `npm sbom` is built into npm 10+, no extra tool. The fonts in ui/public/fonts aren't npm
// packages: they are in the package SBOMs' file lists and in THIRD-PARTY-NOTICES.md.
function uiSbom(version) {
  const result = spawnSync("npm", ["sbom", "--omit", "dev", "--package-lock-only", "--sbom-format", "cyclonedx",
    "--sbom-type", "application"], {
    cwd: path.join(root, "ui"),
    stdio: ["ignore", "pipe", process.stderr],
    shell: process.platform === "win32",
    maxBuffer: 64 * 1024 * 1024,
  });
  if (result.error) fail(`npm sbom: ${result.error.message}`);
  if (result.status !== 0) fail(`npm sbom exited with code ${result.status} (needs npm 10 or later)`);
  const bom = JSON.parse(result.stdout.toString("utf8"));
  if (bom.bomFormat !== "CycloneDX" || !Array.isArray(bom.components)) fail("npm sbom: not a CycloneDX document");
  const file = uiSbomFile(version);
  writeFileSync(file, JSON.stringify(bom, null, 2) + "\n");
  log(`UI SBOM: ${path.relative(root, file)} (${bom.components.map((c) => `${c.name} ${c.version}`).join(", ")})`);
  return file;
}

// Every package must carry its SPDX SBOM for this package and version (and the UI SBOM where the UI is bundled);
// each SBOM is also copied next to the packages for the GitHub release.
function exportSboms(version) {
  for (const id of packages) {
    const nupkg = path.join(artifacts, `${id}.${version}.nupkg`);
    const entries = readZip(nupkg);
    const read = entries.get(spdxManifest);
    if (!read) fail(`${path.basename(nupkg)} has no ${spdxManifest} (Microsoft.Sbom.Targets, src/Directory.Build.props)`);
    const text = read().toString("utf8");
    const spdx = JSON.parse(text);
    const described = spdx.packages?.find((p) => p.SPDXID === "SPDXRef-RootPackage");
    if (spdx.spdxVersion !== "SPDX-2.2" || described?.name !== id || described?.versionInfo !== version) {
      fail(`${path.basename(nupkg)}: the SBOM describes ${described?.name} ${described?.versionInfo} (${spdx.spdxVersion}), not ${id} ${version} (SPDX-2.2)`);
    }
    if (uiPackages.includes(id) && ![...entries.keys()].some((n) => n.endsWith("/sbom/ui.cdx.json"))) {
      fail(`${path.basename(nupkg)} has no sbom/ui.cdx.json next to the UI (LabcoatUiSbom, src/Labcoat/Labcoat.csproj)`);
    }
    writeFileSync(spdxFile(id, version), text);
    log(`SBOM ${path.basename(spdxFile(id, version))}: ${spdx.packages.length - 1} package(s), ${spdx.files?.length ?? 0} file(s)`);
  }
}

const packageFiles = (version) =>
  packages.flatMap((id) => [`${id}.${version}.nupkg`, `${id}.${version}.snupkg`]).map((f) => path.join(artifacts, f));

function verify(dryRun) {
  run("dotnet", ["--version"]);
  run("npm", ["--version"]);
  if (!existsSync(props) || !/<VersionPrefix>[^<]*<\/VersionPrefix>/.test(readFileSync(props, "utf8"))) {
    fail(`no <VersionPrefix> in ${path.relative(root, props)}`);
  }
  if (!process.env.NUGET_API_KEY && !process.env.NUGET_SOURCE) {
    if (dryRun) log("NUGET_API_KEY isn't set: fine for a dry run, a real release would stop here.");
    else fail("NUGET_API_KEY isn't set (NuGet trusted publishing login or the NUGET_API_KEY secret; see docs/releasing.md).");
  }
  log("ok");
}

function prepare(version, prefix, baseline) {
  // 1. The version for local builds: <VersionPrefix> is committed with CHANGELOG.md by @semantic-release/git. So is the
  //    package validation baseline: the previous release, which this pack is checked against (a removed or changed
  //    public API fails the pack, see docs/releasing.md, "Package validation").
  const text = readFileSync(props, "utf8");
  let updated = text.replace(/<VersionPrefix>[^<]*<\/VersionPrefix>/, `<VersionPrefix>${prefix}</VersionPrefix>`);
  if (baseline) {
    if (!/<PackageValidationBaselineVersion>[^<]*<\/PackageValidationBaselineVersion>/.test(updated)) {
      fail(`no <PackageValidationBaselineVersion> in ${path.relative(root, props)}`);
    }
    updated = updated.replace(/<PackageValidationBaselineVersion>[^<]*<\/PackageValidationBaselineVersion>/,
      `<PackageValidationBaselineVersion>${baseline}</PackageValidationBaselineVersion>`);
  }
  if (updated !== text) writeFileSync(props, updated);
  log(`VersionPrefix = ${prefix} (package version ${version})`);
  const committed = /<PackageValidationBaselineVersion>([^<]*)<\/PackageValidationBaselineVersion>/.exec(updated)?.[1];
  if (baseline === "") log("package validation: no previous release, packing without a baseline");
  else log(`package validation baseline = ${baseline ?? committed}${baseline ? "" : " (committed)"}`);

  // 2. The UI: dotnet pack refuses to pack the tool (and the two packages that bundle it) without src/Labcoat/wwwroot.
  if (process.env.LABCOAT_RELEASE_SKIP_UI === "1" && existsSync(path.join(root, "src/Labcoat/wwwroot/index.html"))) {
    log("LABCOAT_RELEASE_SKIP_UI=1: reusing src/Labcoat/wwwroot");
  } else {
    run("npm", ["ci"], { cwd: path.join(root, "ui") });
    run("npm", ["run", "build"], { cwd: path.join(root, "ui") });
  }

  // 3. Pack. Version overrides VersionPrefix/VersionSuffix; SourceLink, the symbol packages and the package SBOMs come
  //    from src/Directory.Build.props. No stale packages or SBOMs from an earlier pack may end up in the release.
  mkdirSync(artifacts, { recursive: true });
  for (const f of readdirSync(artifacts)) {
    if (f.endsWith(".nupkg") || f.endsWith(".snupkg") || isSbomFile(f)) rmSync(path.join(artifacts, f));
  }
  const ui = uiSbom(version);
  const restoreReadmes = pinPackageReadmes(`v${version}`);
  try {
    run("dotnet", [
      "pack", "Labcoat.slnx",
      "-c", "Release",
      "-o", artifacts,
      `-p:Version=${version}`,
      "-p:ContinuousIntegrationBuild=true",
      `-p:LabcoatUiSbom=${ui}`,
      ...(baseline === "" ? ["-p:DisablePackageBaselineValidation=true"] : []),
    ]);
  } finally {
    restoreReadmes();
  }

  const missing = packageFiles(version).filter((f) => !existsSync(f));
  if (missing.length) fail(`dotnet pack didn't produce ${missing.map((f) => path.basename(f)).join(", ")}`);
  const extra = readdirSync(artifacts).filter((f) => /\.s?nupkg$/.test(f) && !packageFiles(version).includes(path.join(artifacts, f)));
  if (extra.length) fail(`unexpected packages in artifacts/: ${extra.join(", ")}`);
  exportSboms(version);
  log(`packed: ${packages.map((id) => `${id} ${version}`).join(", ")}`);
}

function publish(version) {
  const files = packageFiles(version).filter((f) => f.endsWith(".nupkg"));
  const missing = files.filter((f) => !existsSync(f));
  if (missing.length) fail(`missing ${missing.map((f) => path.basename(f)).join(", ")}: run prepare first`);
  const source = process.env.NUGET_SOURCE || "https://api.nuget.org/v3/index.json";
  const key = process.env.NUGET_API_KEY;
  // One package at a time: a clear log line per package. The .snupkg next to each .nupkg is pushed with it.
  // --skip-duplicate: rerunning after a partial failure pushes only what's missing.
  for (const file of files) {
    run("dotnet", [
      "nuget", "push", file,
      "--source", source,
      ...(key ? ["--api-key", key] : []),
      "--skip-duplicate",
    ], { redact: [key] });
  }
  const nugetOrg = /(^|\/\/)api\.nuget\.org\//.test(source);
  process.stdout.write(JSON.stringify({
    name: `NuGet (${packages.join(", ")})`,
    url: nugetOrg ? `https://www.nuget.org/packages/Labcoat/${version}` : source,
  }) + "\n");
}

const [step, ...rest] = process.argv.slice(2);
switch (step) {
  case "verify":
    verify(rest.includes("--dry-run") || rest.includes("--dry-run=true"));
    break;
  case "prepare": {
    const { version, prefix } = parseVersion(rest[0]);
    // --baseline=<version>: the previous release; --baseline= (empty): none; absent: keep the committed baseline.
    const option = rest.slice(1).find((a) => a.startsWith("--baseline="));
    const baseline = option === undefined ? undefined : option.slice("--baseline=".length);
    if (baseline && !semver.test(baseline)) {
      console.error(`invalid --baseline version: ${baseline}`);
      process.exit(2);
    }
    prepare(version, prefix, baseline);
    break;
  }
  case "readmes":
    readmes(parseVersion(rest[0]).version);
    break;
  case "publish":
    publish(parseVersion(rest[0]).version);
    break;
  default:
    console.error("usage: node eng/release.mjs verify [--dry-run] | prepare <version> [--baseline=<version>] | readmes <version> | publish <version>");
    process.exit(2);
}
