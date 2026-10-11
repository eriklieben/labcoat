// Code coverage of the shipped assemblies: runs every test project of Labcoat.slnx with Microsoft.Testing.Platform's
// code coverage (Cobertura, settings in tests/CodeCoverage.xml), merges the reports, prints a per-assembly and a
// per-file table and fails when an assembly is below the line coverage threshold. Cross-platform (Node 22, no
// dependencies); used by CI.
//
//   node eng/coverage.mjs [--configuration Debug] [--no-build] [--threshold 85] [--files all|below|none]
//                         [--output artifacts/coverage] [--artifacts-path <dir>] [--project <test csproj>]... [--filter <expr>]
//
//   --configuration  build configuration (default Debug)
//   --no-build       use the existing build (e.g. after `dotnet build -c Release` in CI)
//   --threshold      minimum line coverage per shipped assembly, in percent (default 85)
//   --files          per-file table: every file (all, the default), only files below the threshold, or none
//   --output         where the merged report goes: coverage.cobertura.xml, summary.md and the raw per-project reports
//   --artifacts-path passed to dotnet build / run (keeps bin/obj out of the source tree)
//   --project        run only these test projects (repeatable; default: every project under tests/ in Labcoat.slnx)
//   --filter         test filter passed to every MSTest and NUnit test app (their --filter syntax); for local work on a
//                    few tests. TUnit and xUnit v3 test apps (tests/Labcoat.Testing.TUnit.Tests, .XUnit.Tests) have no
//                    --filter: they run in full.
//
// Exit code 0 when every test passed and every shipped assembly meets the threshold, 1 otherwise.
import { spawnSync } from "node:child_process";
import { appendFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const shipped = ["Labcoat", "Labcoat.Aspire.Hosting", "Labcoat.Testing", "Labcoat.Testing.MSTest", "Labcoat.Testing.TUnit", "Labcoat.Testing.NUnit",
  "Labcoat.Testing.XUnit", "Labcoat.TestingPlatform"];

// ------------------------------------------------------------------------------------------------ options
const opts = { configuration: "Debug", threshold: 85, files: "all", output: path.join(root, "artifacts", "coverage"), projects: [] };
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i++) {
  const a = argv[i];
  const value = () => {
    if (i + 1 >= argv.length) usage(`${a} needs a value`);
    return argv[++i];
  };
  switch (a) {
    case "--configuration": case "-c": opts.configuration = value(); break;
    case "--no-build": opts.noBuild = true; break;
    case "--threshold": opts.threshold = Number(value()); break;
    case "--files": opts.files = value(); break;
    case "--output": opts.output = path.resolve(value()); break;
    case "--artifacts-path": opts.artifactsPath = path.resolve(value()); break;
    case "--project": opts.projects.push(path.resolve(value())); break;
    case "--filter": opts.filter = value(); break;
    case "--help": case "-h": usage(); break;
    default: usage(`unknown option ${a}`);
  }
}
if (!Number.isFinite(opts.threshold) || !["all", "below", "none"].includes(opts.files)) usage("bad --threshold or --files");

function usage(error) {
  if (error) console.error(`error: ${error}`);
  console.error("usage: node eng/coverage.mjs [--configuration Debug] [--no-build] [--threshold 85] [--files all|below|none]\n" +
    "                           [--output artifacts/coverage] [--artifacts-path <dir>] [--project <test csproj>]... [--filter <expr>]");
  process.exit(error ? 2 : 0);
}

const settings = path.join(root, "tests", "CodeCoverage.xml");
const projects = opts.projects.length > 0 ? opts.projects : testProjects();
const rawDir = path.join(opts.output, "raw");
rmSync(opts.output, { recursive: true, force: true });
mkdirSync(rawDir, { recursive: true });

function testProjects() {
  const slnx = readFileSync(path.join(root, "Labcoat.slnx"), "utf8");
  return [...slnx.matchAll(/<Project\s+Path="([^"]+)"/g)]
    .map((m) => m[1].replaceAll("\\", "/"))
    .filter((p) => p.startsWith("tests/"))
    .map((p) => path.join(root, p));
}

function dotnet(args) {
  console.log(`\n> dotnet ${args.join(" ")}`);
  const r = spawnSync("dotnet", args, { cwd: root, stdio: "inherit", env: { ...process.env, DOTNET_NOLOGO: "1" } });
  return r.status ?? 1;
}

// ------------------------------------------------------------------------------------------------ run
const started = Date.now();
const artifacts = opts.artifactsPath ? ["--artifacts-path", opts.artifactsPath] : [];
if (!opts.noBuild && dotnet(["build", path.join(root, "Labcoat.slnx"), "-c", opts.configuration, "-nologo", ...artifacts]) !== 0) {
  console.error("error: the build failed");
  process.exit(1);
}

const failedProjects = [];
const reports = [];
for (const project of projects) {
  const name = path.basename(project, ".csproj");
  const report = path.join(rawDir, `${name}.cobertura.xml`);
  // MSTest and NUnit (NUnit3TestAdapter) take --filter; TUnit and xUnit v3 have other filter options.
  const filterable = /<PackageReference\s+Include="(MSTest|NUnit)"/.test(readFileSync(project, "utf8"));
  const code = dotnet(["run", "--project", project, "-c", opts.configuration, "--no-build", ...artifacts, "--",
    "--coverage", "--coverage-output-format", "cobertura", "--coverage-output", report, "--coverage-settings", settings,
    // a filter that matches nothing in a project is not a failure (exit code 8: zero tests ran)
    ...(opts.filter && filterable ? ["--filter", opts.filter, "--ignore-exit-code", "8"] : [])]);
  if (code !== 0) failedProjects.push(`${name} (exit code ${code})`);
  if (existsSync(report)) reports.push(report);
  else failedProjects.push(`${name} (no coverage report)`);
}

// ------------------------------------------------------------------------------------------------ merge
// assembly -> file -> line -> { hits, covered, total }. Within one report a line can appear in several classes
// (partial classes, state machines, lambdas): max. Across reports (several test projects): hits add up, branches max.
const merged = new Map();
const isGenerated = (file) => /\.g\.cs$/i.test(file) || /[\\/]obj[\\/]/i.test(file);
const attr = (s, name) => s.match(new RegExp(`\\b${name}="([^"]*)"`))?.[1];
const unescape = (s) => s.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, "&");

for (const report of reports) {
  const xml = readFileSync(report, "utf8");
  const sources = [...xml.matchAll(/<source>([^<]*)<\/source>/g)].map((m) => unescape(m[1]).trim()).filter(Boolean);
  const local = new Map(); // same shape as merged, for this report
  for (const pkg of xml.matchAll(/<package\b([^>]*)>([\s\S]*?)<\/package>/g)) {
    const assembly = unescape(attr(pkg[1], "name") ?? "");
    for (const cls of pkg[2].matchAll(/<class\b([^>]*)>([\s\S]*?)<\/class>/g)) {
      let file = unescape(attr(cls[1], "filename") ?? "");
      if (!file || isGenerated(file)) continue;
      if (!path.isAbsolute(file) && sources.length > 0) file = path.join(sources[0], file);
      const body = cls[2].replace(/<methods>[\s\S]*?<\/methods>/, ""); // class-level <lines>; method lines repeat them
      const files = local.get(assembly) ?? local.set(assembly, new Map()).get(assembly);
      const lines = files.get(file) ?? files.set(file, new Map()).get(file);
      for (const line of body.matchAll(/<line\b([^>]*?)\/?>/g)) {
        const number = Number(attr(line[1], "number"));
        const hits = Number(attr(line[1], "hits") ?? 0);
        const cond = (attr(line[1], "condition-coverage") ?? "").match(/\((\d+)\s*\/\s*(\d+)\)/);
        const covered = cond ? Number(cond[1]) : 0;
        const total = cond ? Number(cond[2]) : 0;
        const prev = lines.get(number);
        lines.set(number, prev
          ? { hits: Math.max(prev.hits, hits), covered: Math.max(prev.covered, covered), total: Math.max(prev.total, total) }
          : { hits, covered, total });
      }
    }
  }
  for (const [assembly, files] of local) {
    const target = merged.get(assembly) ?? merged.set(assembly, new Map()).get(assembly);
    for (const [file, lines] of files) {
      const t = target.get(file) ?? target.set(file, new Map()).get(file);
      for (const [n, l] of lines) {
        const p = t.get(n);
        t.set(n, p ? { hits: p.hits + l.hits, covered: Math.max(p.covered, l.covered), total: Math.max(p.total, l.total) } : { ...l });
      }
    }
  }
}

// ------------------------------------------------------------------------------------------------ report
const stats = (lineMaps) => {
  let lv = 0, lc = 0, bv = 0, bc = 0;
  for (const lines of lineMaps)
    for (const l of lines.values()) {
      lv++;
      if (l.hits > 0) lc++;
      bv += l.total;
      bc += l.covered;
    }
  return { lv, lc, bv, bc, lineRate: lv === 0 ? 1 : lc / lv, branchRate: bv === 0 ? 1 : bc / bv };
};
const pct = (r) => `${(r * 100).toFixed(1)}%`;
const rel = (f) => path.relative(root, f).replaceAll("\\", "/");

function table(headers, rows, align) {
  const widths = headers.map((h, i) => Math.max(h.length, ...rows.map((r) => String(r[i]).length)));
  const fmt = (r) => r.map((c, i) => (align[i] === "r" ? String(c).padStart(widths[i]) : String(c).padEnd(widths[i]))).join("  ");
  return [fmt(headers), widths.map((w) => "-".repeat(w)).join("  "), ...rows.map(fmt)].join("\n");
}
const markdown = (headers, rows, align) =>
  [`| ${headers.join(" | ")} |`, `| ${align.map((a) => (a === "r" ? "---:" : "---")).join(" | ")} |`, ...rows.map((r) => `| ${r.join(" | ")} |`)].join("\n");

const assemblyRows = [];
const failures = [];
for (const name of shipped) {
  const files = merged.get(name);
  if (!files) {
    assemblyRows.push([name, "-", "-", "-", "-", "MISSING"]);
    failures.push(`${name}: no coverage data (is it tested and loaded by a test project?)`);
    continue;
  }
  const s = stats(files.values());
  const ok = s.lineRate * 100 >= opts.threshold;
  if (!ok) failures.push(`${name}: ${pct(s.lineRate)} lines < ${opts.threshold}%`);
  assemblyRows.push([name, `${s.lc}/${s.lv}`, pct(s.lineRate), `${s.bc}/${s.bv}`, pct(s.branchRate), ok ? "ok" : "BELOW"]);
}
const all = stats([...merged.entries()].filter(([a]) => shipped.includes(a)).flatMap(([, f]) => [...f.values()]));
assemblyRows.push(["Total", `${all.lc}/${all.lv}`, pct(all.lineRate), `${all.bc}/${all.bv}`, pct(all.branchRate), ""]);
const assemblyHeaders = ["Assembly", "Lines", "Line %", "Branches", "Branch %", `>= ${opts.threshold}%`];
const assemblyAlign = ["l", "r", "r", "r", "r", "l"];

const fileHeaders = ["File", "Lines", "Line %", "Branches", "Branch %"];
const fileAlign = ["l", "r", "r", "r", "r"];
const fileRows = [];
for (const name of shipped) {
  for (const [file, lines] of [...(merged.get(name) ?? new Map())].sort(([a], [b]) => rel(a).localeCompare(rel(b)))) {
    const s = stats([lines]);
    if (opts.files === "below" && s.lineRate * 100 >= opts.threshold) continue;
    fileRows.push([rel(file), `${s.lc}/${s.lv}`, pct(s.lineRate), `${s.bc}/${s.bv}`, pct(s.branchRate)]);
  }
}

console.log(`\nCode coverage (${reports.length} report${reports.length === 1 ? "" : "s"}, ${((Date.now() - started) / 1000).toFixed(0)}s)\n`);
console.log(table(assemblyHeaders, assemblyRows, assemblyAlign));
if (opts.files !== "none" && fileRows.length > 0) {
  console.log(`\n${opts.files === "below" ? `Files below ${opts.threshold}%` : "Files"}\n`);
  console.log(table(fileHeaders, fileRows, fileAlign));
}

// Merged Cobertura (paths relative to the repository root) + a Markdown summary (also the GitHub job summary).
writeFileSync(path.join(opts.output, "coverage.cobertura.xml"), cobertura());
const summary = `## Code coverage\n\n${markdown(assemblyHeaders, assemblyRows, assemblyAlign)}\n\n` +
  (fileRows.length > 0 && opts.files !== "none"
    ? `<details><summary>${opts.files === "below" ? "Files below the threshold" : "Files"}</summary>\n\n${markdown(fileHeaders, fileRows, fileAlign)}\n\n</details>\n`
    : "");
writeFileSync(path.join(opts.output, "summary.md"), summary);
if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
console.log(`\nMerged report: ${rel(path.join(opts.output, "coverage.cobertura.xml"))}`);

function cobertura() {
  const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  const rate = (r) => r.toFixed(4);
  const out = [`<?xml version="1.0" encoding="utf-8"?>`,
    `<coverage line-rate="${rate(all.lineRate)}" branch-rate="${rate(all.branchRate)}" lines-covered="${all.lc}" lines-valid="${all.lv}" ` +
    `branches-covered="${all.bc}" branches-valid="${all.bv}" complexity="0" version="1.9" timestamp="${Math.floor(Date.now() / 1000)}">`,
    `  <sources><source>${esc(root)}</source></sources>`, "  <packages>"];
  for (const [name, files] of merged) {
    if (!shipped.includes(name)) continue;
    const ps = stats(files.values());
    out.push(`    <package name="${esc(name)}" line-rate="${rate(ps.lineRate)}" branch-rate="${rate(ps.branchRate)}" complexity="0">`, "      <classes>");
    for (const [file, lines] of files) {
      const fs = stats([lines]);
      out.push(`        <class name="${esc(path.basename(file, path.extname(file)))}" filename="${esc(rel(file))}" line-rate="${rate(fs.lineRate)}" ` +
        `branch-rate="${rate(fs.branchRate)}" complexity="0">`, "          <methods />", "          <lines>");
      for (const [n, l] of [...lines].sort(([a], [b]) => a - b))
        out.push(l.total > 0
          ? `            <line number="${n}" hits="${l.hits}" branch="true" condition-coverage="${Math.round((100 * l.covered) / l.total)}% (${l.covered}/${l.total})" />`
          : `            <line number="${n}" hits="${l.hits}" branch="false" />`);
      out.push("          </lines>", "        </class>");
    }
    out.push("      </classes>", "    </package>");
  }
  out.push("  </packages>", "</coverage>", "");
  return out.join("\n");
}

// ------------------------------------------------------------------------------------------------ verdict
if (failedProjects.length > 0) console.error(`\nerror: test runs failed: ${failedProjects.join(", ")}`);
if (failures.length > 0) console.error(`\nerror: line coverage below ${opts.threshold}%:\n  ${failures.join("\n  ")}`);
process.exit(failedProjects.length > 0 || failures.length > 0 ? 1 : 0);
