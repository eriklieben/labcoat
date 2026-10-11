#!/usr/bin/env node
// Generates a large MSTest suite for Labcoat performance runs against the real host (eng/perf-real.mjs):
// <projects> test projects × <tests> tests each, in deep namespaces, with data-driven tests ([DataRow], 5 rows), a few
// failing (~0.5%) and ignored (~0.3%) tests, and fast bodies (no I/O, no sleeps). Writes a PerfSuite.slnx next to them.
//
// Usage: node eng/gen-perf-suite.mjs [--out=<dir>] [--projects=8] [--tests=2500] [--mstest=4.0.2] [--clean]
//   --out      target directory (default: a new temp dir, printed at the end). Never inside the repository.
//   --clean    delete --out first.
// The projects use the MSTest package with the Microsoft.Testing.Platform runner (EnableMSTestRunner), like
// samples/SampleTests. The first build needs the MSTest package from the NuGet cache or feed.
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = Object.fromEntries(process.argv.slice(2).map((a) => { const m = /^--([^=]+)(?:=(.*))?$/.exec(a); return m ? [m[1], m[2] ?? true] : [a, true]; }));
const projects = Number(args.projects ?? 8);
const perProject = Number(args.tests ?? 2500);
const mstest = args.mstest ?? "4.0.2";
const repo = resolve(fileURLToPath(new URL("..", import.meta.url)));
const out = resolve(args.out ?? mkdtempSync(join(tmpdir(), "labcoat-perf-suite-")));
if (!relative(repo, out).startsWith("..")) throw new Error(`--out must be outside the repository (${repo})`);
if (args.clean && existsSync(out)) rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const PRODUCTS = ["Billing", "Catalog", "Identity", "Shipping", "Inventory", "Search", "Payments", "Notifications"];
const AREAS = ["Api", "Domain", "Infrastructure", "Application"];
const SUBS = ["Commands", "Queries", "Handlers", "Validators", "Mappers"];
const VERBS = ["Handle", "Validate", "Map", "Resolve", "Compute", "Persist", "Publish", "Parse"];
const SUBJECTS = ["Order", "Customer", "Invoice", "Product", "Account", "Address", "Token", "Shipment"];
const ROWS = 5; // rows per data-driven test
const PER_CLASS = 25; // test methods per class

let seed = 11;
const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
const csproj = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Exe</OutputType>
    <EnableMSTestRunner>true</EnableMSTestRunner>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);MSTEST0001;MSTEST0032;MSTEST0037;CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MSTest" Version="${mstest}" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" />
  </ItemGroup>
</Project>
`;

const totals = { tests: 0, failing: 0, ignored: 0, dataRows: 0, files: 0 };
const projectPaths = [];
for (let p = 0; p < projects; p++) {
  const name = `PerfSuite.${PRODUCTS[p % PRODUCTS.length]}${p >= PRODUCTS.length ? Math.floor(p / PRODUCTS.length) + 1 : ""}.Tests`;
  const dir = join(out, name);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, `${name}.csproj`), csproj);
  writeFileSync(join(dir, "MSTestSettings.cs"), "[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]\n");
  projectPaths.push(join(name, `${name}.csproj`));
  let count = 0;
  for (let c = 0; count < perProject; c++) {
    const area = AREAS[c % AREAS.length];
    const sub = SUBS[Math.floor(c / AREAS.length) % SUBS.length];
    const ns = `${name}.${area}.${sub}${c % 3 ? `.${VERBS[c % VERBS.length]}` : ""}`;
    const cls = `${SUBJECTS[c % SUBJECTS.length]}${sub.replace(/s$/, "")}Tests${c}`;
    const methods = [];
    for (let m = 0; m < PER_CLASS && count < perProject; m++) {
      const method = `${VERBS[(m + c) % VERBS.length]}_${SUBJECTS[(m * 3 + c) % SUBJECTS.length]}_${m}`;
      const x = rnd();
      const fail = x < 0.005;
      const ignore = !fail && x < 0.008;
      const attrs = ignore ? "    [Ignore(\"perf suite: ignored on purpose\")]\n" : "";
      if (m % 4 === 0 && count + ROWS <= perProject) {
        const rows = Array.from({ length: ROWS }, (_, r) => `    [DataRow(${r}, ${r * 7}, "${SUBJECTS[r % SUBJECTS.length].toLowerCase()}-${r}")]`).join("\n");
        methods.push(`    [TestMethod]\n${attrs}${rows}\n    public void ${method}(int a, int b, string s)\n    {\n        Assert.AreEqual(a * 7, b${fail ? " + 1" : ""});\n        Assert.IsFalse(string.IsNullOrEmpty(s));\n    }\n`);
        count += ROWS;
        totals.dataRows += ROWS;
      } else {
        methods.push(`    [TestMethod]\n${attrs}    public void ${method}()\n    {\n        var sum = 0;\n        for (var i = 0; i < 50; i++) sum += i;\n        Assert.AreEqual(1225, sum${fail ? " + 1" : ""});\n    }\n`);
        count++;
      }
      if (fail) totals.failing++;
      if (ignore) totals.ignored++;
    }
    const sub2 = join(dir, area, sub);
    mkdirSync(sub2, { recursive: true });
    writeFileSync(join(sub2, `${cls}.cs`), `namespace ${ns};\n\n[TestClass]\npublic sealed class ${cls}\n{\n${methods.join("\n")}}\n`);
    totals.files++;
  }
  totals.tests += count;
}
writeFileSync(
  join(out, "PerfSuite.slnx"),
  `<Solution>\n${projectPaths.map((p) => `  <Project Path="${p.replaceAll("\\", "/")}" />`).join("\n")}\n</Solution>\n`,
);
writeFileSync(join(out, "global.json"), JSON.stringify({ test: { runner: "Microsoft.Testing.Platform" } }, null, 2) + "\n");
console.log(JSON.stringify({ out, solution: join(out, "PerfSuite.slnx"), projects, ...totals }));
