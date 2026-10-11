#!/usr/bin/env node
// Summarize or compare Vitest bench results (the json reporter output of `npm run bench:ci`).
//
//   node scripts/bench-compare.mjs <results.json>                  table of every task (Markdown)
//   node scripts/bench-compare.mjs <baseline.json> <results.json>  before/after table with the change per task
//
// Options:
//   --threshold=<pct>   a change counts as faster/slower when it exceeds this percentage AND both runs' margins of
//                       error combined (default 10)
//   --changed           only list the tasks that changed (compare mode)
//   --out=<file>        also write the Markdown to <file> (e.g. $GITHUB_STEP_SUMMARY)
//
// Vitest 5 has no `vitest bench --compare <json>` / `--outputJson` (both were Vitest 1-3 flags); its json reporter
// carries the benchmark statistics per test (assertionResults[].benchmarks), which this script reads.
// Exit code is always 0: benchmarks inform, they do not gate.
import { appendFileSync, readFileSync } from "node:fs";
import { relative } from "node:path";

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const a = args.find((x) => x.startsWith(`--${name}=`));
  return a ? a.slice(name.length + 3) : fallback;
};
const flag = (name) => args.includes(`--${name}`);
const files = args.filter((a) => !a.startsWith("--"));
if (files.length < 1 || files.length > 2) {
  console.error("usage: bench-compare.mjs [baseline.json] <results.json> [--threshold=10] [--changed] [--out=file]");
  process.exit(2);
}
const threshold = Number(opt("threshold", "10"));

/** key → { file, group, task, mean (ms), hz, rme (%), samples } */
function load(path) {
  const json = JSON.parse(readFileSync(path, "utf8"));
  const rows = new Map();
  for (const file of json.testResults ?? []) {
    const rel = relative(process.cwd(), file.name).replace(/\\/g, "/");
    for (const t of file.assertionResults ?? []) {
      for (const b of t.benchmarks ?? []) {
        for (const task of b.tasks ?? []) {
          const group = t.ancestorTitles ? [...t.ancestorTitles, t.title].join(" › ") : (t.fullName ?? b.name);
          rows.set(`${rel} > ${group} > ${task.name}`, {
            file: rel,
            group,
            task: task.name,
            mean: task.latency?.mean ?? NaN,
            p99: task.latency?.p99 ?? NaN,
            hz: task.throughput?.mean ?? NaN,
            rme: task.latency?.rme ?? 0,
            samples: task.latency?.samplesCount ?? 0,
          });
        }
      }
      if (t.status === "failed") rows.set(`${rel} > ${t.fullName} > (failed)`, { file: rel, group: [...(t.ancestorTitles ?? []), t.title].join(" › "), task: "(failed)", failed: true });
    }
  }
  return rows;
}

const ms = (v) => (!Number.isFinite(v) ? "–" : v >= 100 ? `${v.toFixed(0)} ms` : v >= 1 ? `${v.toFixed(2)} ms` : v >= 0.001 ? `${(v * 1000).toFixed(2)} µs` : `${(v * 1e6).toFixed(0)} ns`);
const hz = (v) => (!Number.isFinite(v) ? "–" : v >= 1000 ? Math.round(v).toLocaleString("en") : v.toFixed(v >= 10 ? 1 : 2));
const esc = (s) => String(s).replace(/\|/g, "\\|");

const out = [];
if (files.length === 1) {
  const rows = load(files[0]);
  let group;
  out.push("| Benchmark | Task | Mean | p99 | ops/s | ±rme | Samples |", "|---|---|--:|--:|--:|--:|--:|");
  for (const r of rows.values()) {
    const g = `${r.file.replace(/^bench\//, "")} › ${r.group}`;
    if (r.failed) {
      out.push(`| ${esc(g)} | **failed** | | | | | |`);
      continue;
    }
    out.push(`| ${g === group ? "" : esc(g)} | ${esc(r.task)} | ${ms(r.mean)} | ${ms(r.p99)} | ${hz(r.hz)} | ${r.rme.toFixed(1)} % | ${r.samples} |`);
    group = g;
  }
  out.push("", `${rows.size} tasks.`);
} else {
  const base = load(files[0]);
  const head = load(files[1]);
  const keys = [...new Set([...head.keys(), ...base.keys()])];
  let faster = 0;
  let slower = 0;
  const lines = [];
  for (const k of keys) {
    const a = base.get(k);
    const b = head.get(k);
    const name = esc((b ?? a).file.replace(/^bench\//, "") + " › " + (b ?? a).group + " › " + (b ?? a).task);
    if (!a || !b || a.failed || b.failed) {
      lines.push({ changed: true, text: `| ${name} | ${a && !a.failed ? ms(a.mean) : a ? "failed" : "–"} | ${b && !b.failed ? ms(b.mean) : b ? "failed" : "–"} | | ${!a ? "new" : !b ? "removed" : "failed"} |` });
      continue;
    }
    const change = ((b.mean - a.mean) / a.mean) * 100;
    // noise: both runs' relative margins of error (rme is a 95 % interval of the mean)
    const noise = Math.max(threshold, a.rme + b.rme);
    const verdict = change <= -noise ? "faster" : change >= noise ? "slower" : "";
    if (verdict === "faster") faster++;
    if (verdict === "slower") slower++;
    const ratio = a.mean / b.mean;
    lines.push({
      changed: !!verdict,
      text: `| ${name} | ${ms(a.mean)} | ${ms(b.mean)} | ${change >= 0 ? "+" : ""}${change.toFixed(1)} % (${Math.abs(ratio - 1) < 0.005 ? "1.00×" : ratio > 1 ? `${ratio.toFixed(2)}× faster` : `${(1 / ratio).toFixed(2)}× slower`}) | ${verdict === "faster" ? "faster" : verdict === "slower" ? "**slower**" : "≈"} |`,
    });
  }
  out.push(`Baseline: \`${files[0]}\` → current: \`${files[1]}\`. A change counts when it exceeds ${threshold} % and both runs' margins of error combined.`, "");
  out.push(`**${slower} slower, ${faster} faster, ${keys.length - slower - faster} unchanged or new.**`, "");
  out.push("| Task | Baseline mean | Current mean | Change | |", "|---|--:|--:|--:|---|");
  for (const l of lines) if (!flag("changed") || l.changed) out.push(l.text);
}

const md = out.join("\n") + "\n";
process.stdout.write(md);
const target = opt("out", undefined);
if (target) appendFileSync(target, md);
