// Compares two BenchmarkDotNet runs of benchmarks/Labcoat.Benchmarks (their *-report-brief.json files): mean time
// and allocated bytes per benchmark case, as a Markdown table. Used for before/after comparisons on one machine
// (CONTRIBUTING.md "Benchmarks") and by the nightly Performance workflow against the previous night's results.
//
//   node eng/bdn-compare.mjs <base> <head> [--threshold 10] [--output compare.md] [--only-changes]
//
//   <base>, <head>   a BenchmarkDotNet results directory (…/results), an artifacts directory (…/BenchmarkDotNet.Artifacts),
//                    or one *-report-brief.json file
//   --threshold      a change of more than this many percent is marked (time: slower / faster; allocations: more / less)
//   --only-changes   list only the marked cases
//   --output         also write the table to this file
//
// Informational only: always exits 0 (timings depend on the machine; compare runs made on the same one). Cases that are
// in only one of the runs are listed as added / removed.
import { readFileSync, writeFileSync, readdirSync, statSync, existsSync } from "node:fs";
import path from "node:path";

const args = process.argv.slice(2);
const opts = { threshold: 10, output: null, onlyChanges: false, inputs: [] };
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === "--threshold") opts.threshold = Number(args[++i]);
  else if (a === "--output") opts.output = args[++i];
  else if (a === "--only-changes") opts.onlyChanges = true;
  else if (a === "-h" || a === "--help") usage(0);
  else opts.inputs.push(a);
}
if (opts.inputs.length !== 2 || !Number.isFinite(opts.threshold)) usage(1);

function usage(code) {
  console.log("usage: node eng/bdn-compare.mjs <base> <head> [--threshold 10] [--output compare.md] [--only-changes]");
  process.exit(code);
}

/** All *-report-brief.json (or *-report-full*.json) files under a path. */
function reportFiles(p) {
  if (!existsSync(p)) return [];
  if (statSync(p).isFile()) return [p];
  const out = [];
  for (const name of readdirSync(p)) {
    const full = path.join(p, name);
    if (statSync(full).isDirectory()) out.push(...reportFiles(full));
    else if (/-report(-brief|-full|-full-compressed)?\.json$/.test(name)) out.push(full);
  }
  return out;
}

/** FullName → { type, method, params, mean (ns), allocated (bytes) } */
function load(p) {
  const cases = new Map();
  for (const file of reportFiles(p)) {
    const report = JSON.parse(readFileSync(file, "utf8"));
    for (const b of report.Benchmarks ?? []) {
      if (!b.Statistics) continue; // failed case
      cases.set(b.FullName, {
        type: b.Type,
        method: b.Method,
        params: b.Parameters ?? "",
        mean: b.Statistics.Mean,
        allocated: b.Memory?.BytesAllocatedPerOperation ?? null,
      });
    }
  }
  return cases;
}

const [base, head] = opts.inputs.map(load);
if (base.size === 0 || head.size === 0) {
  console.log(`No BenchmarkDotNet JSON reports found in ${base.size === 0 ? opts.inputs[0] : opts.inputs[1]}.`);
  process.exit(0);
}

const time = (ns) =>
  ns == null ? "–" : ns >= 1e9 ? `${(ns / 1e9).toFixed(2)} s` : ns >= 1e6 ? `${(ns / 1e6).toFixed(2)} ms` : ns >= 1e3 ? `${(ns / 1e3).toFixed(2)} µs` : `${ns.toFixed(1)} ns`;
const bytes = (b) =>
  b == null ? "–" : b >= 1024 * 1024 ? `${(b / 1024 / 1024).toFixed(2)} MB` : b >= 1024 ? `${(b / 1024).toFixed(1)} KB` : `${b} B`;
const delta = (a, b) => (a == null || b == null || a === 0 ? null : ((b - a) / a) * 100);
const fmtDelta = (d, worse, better) =>
  d == null ? "" : `${d > 0 ? "+" : ""}${d.toFixed(1)}%${Math.abs(d) > opts.threshold ? ` ${d > 0 ? worse : better}` : ""}`;

const rows = [];
let slower = 0, faster = 0, moreAlloc = 0, lessAlloc = 0;
const names = [...new Set([...base.keys(), ...head.keys()])].sort();
for (const name of names) {
  const a = base.get(name), b = head.get(name);
  const c = a ?? b;
  const label = `${c.type}.${c.method}`;
  if (!a || !b) {
    rows.push({ changed: true, cells: [label, c.params, a ? time(a.mean) : "–", b ? time(b.mean) : "–", a ? "removed" : "added", a ? bytes(a.allocated) : "–", b ? bytes(b.allocated) : "–", ""] });
    continue;
  }
  const dt = delta(a.mean, b.mean), da = delta(a.allocated, b.allocated);
  // tiny allocations (a few bytes) jump by large percentages without meaning anything
  const allocMarked = da != null && Math.abs(da) > opts.threshold && Math.abs(b.allocated - a.allocated) > 64;
  const timeMarked = dt != null && Math.abs(dt) > opts.threshold;
  if (timeMarked) dt > 0 ? slower++ : faster++;
  if (allocMarked) da > 0 ? moreAlloc++ : lessAlloc++;
  rows.push({
    changed: timeMarked || allocMarked,
    cells: [label, c.params, time(a.mean), time(b.mean), fmtDelta(dt, "slower", "faster"), bytes(a.allocated), bytes(b.allocated),
      allocMarked ? fmtDelta(da, "more", "less") : da == null ? "" : `${da > 0 ? "+" : ""}${da.toFixed(1)}%`],
  });
}

const lines = [
  `Compared ${names.length} cases (threshold ${opts.threshold}%): ${slower} slower, ${faster} faster, ${moreAlloc} allocate more, ${lessAlloc} allocate less.`,
  "",
  "| Benchmark | Params | Time (base) | Time (head) | Δ time | Alloc (base) | Alloc (head) | Δ alloc |",
  "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |",
  ...rows.filter((r) => !opts.onlyChanges || r.changed).map((r) => `| ${r.cells.join(" | ")} |`),
];
const text = lines.join("\n") + "\n";
process.stdout.write(text);
if (opts.output) writeFileSync(opts.output, text);
