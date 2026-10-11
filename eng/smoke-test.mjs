// End-to-end smoke test of an installed Labcoat tool: start it on the sample tests, wait for discovery, run everything
// through the REST API, check the outcome counts and the run's JUnit / HTML exports. Cross-platform (Node 22, no dependencies); used by CI.
//
//   node eng/smoke-test.mjs --tool <labcoat executable> [--port 4500] [--total 35] [--failed 10] [--skipped 4]
//                           [--timeout 600] -- <paths passed to labcoat...>
//
// Exit code 0 when the run finished with exactly the expected counts, 1 otherwise.
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const argv = process.argv.slice(2);
const sep = argv.indexOf("--");
const opts = {};
const own = sep < 0 ? argv : argv.slice(0, sep);
for (let i = 0; i < own.length; i += 2) {
  if (!own[i].startsWith("--") || own[i + 1] === undefined) {
    console.error(`bad option: ${own[i]}`);
    process.exit(2);
  }
  opts[own[i].slice(2)] = own[i + 1];
}
const paths = sep < 0 ? [] : argv.slice(sep + 1);
const tool = opts.tool;
if (!tool) {
  console.error("usage: node eng/smoke-test.mjs --tool <labcoat> [--port n] [--total n] [--failed n] [--skipped n] -- <paths...>");
  process.exit(2);
}
const port = Number(opts.port ?? 4500);
const expected = {
  total: opts.total === undefined ? undefined : Number(opts.total),
  failed: opts.failed === undefined ? undefined : Number(opts.failed),
  skipped: opts.skipped === undefined ? undefined : Number(opts.skipped),
};
const deadline = Date.now() + Number(opts.timeout ?? 600) * 1000;
const base = `http://127.0.0.1:${port}`;

const historyDir = mkdtempSync(path.join(tmpdir(), "labcoat-smoke-"));
const child = spawn(tool, [...paths, "--no-open", "--port", String(port)], {
  // LABCOAT_NO_USER_CONFIG: never pick up the developer's ~/.labcoat.json (hermetic)
  env: { ...process.env, CI: "true", NO_COLOR: "1", LABCOAT_HISTORY_DIR: historyDir, LABCOAT_NO_USER_CONFIG: "1" },
  stdio: ["ignore", "pipe", "pipe"],
});
let exited = false;
child.on("exit", (code) => {
  exited = true;
  console.log(`[labcoat] exited with code ${code}`);
});
const prefix = (stream) => stream.on("data", (d) => process.stdout.write(String(d).replace(/^(?=.)/gm, "[labcoat] ")));
prefix(child.stdout);
prefix(child.stderr);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function summary() {
  try {
    const res = await fetch(`${base}/api/summary`);
    return res.ok ? await res.json() : undefined;
  } catch {
    return undefined;
  }
}
async function waitFor(what, predicate) {
  let last;
  while (Date.now() < deadline) {
    if (exited) throw new Error(`labcoat exited while waiting for ${what}`);
    last = await summary();
    if (last && predicate(last)) return last;
    await sleep(1000);
  }
  throw new Error(`timed out waiting for ${what}; last summary: ${JSON.stringify(last)}`);
}

async function stop() {
  if (exited) return;
  child.kill(process.platform === "win32" ? undefined : "SIGTERM");
  for (let i = 0; i < 60 && !exited; i++) await sleep(500);
  if (!exited) child.kill("SIGKILL");
}

let ok = false;
try {
  const discovered = await waitFor("discovery", (s) => {
    const failed = s.projects.filter((p) => p.status === "error");
    if (failed.length) throw new Error(`build/discovery failed for ${failed.map((p) => p.name).join(", ")}`);
    return s.projects.length > 0 && s.projects.every((p) => p.status === "idle" && p.tests > 0);
  });
  console.log(`discovered ${discovered.tests.total} tests in ${discovered.projects.length} projects: ` +
    discovered.projects.map((p) => `${p.name} (${p.tests})`).join(", "));

  const res = await fetch(`${base}/api/run`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
  if (!res.ok) throw new Error(`POST /api/run: HTTP ${res.status} ${await res.text()}`);
  const { runId } = await res.json();
  console.log(`run ${runId} started`);

  const done = await waitFor("the run to finish", (s) => s.lastRun?.runId === runId && s.tests.running === 0 && s.tests.queued === 0);
  const r = done.lastRun;
  console.log(`run finished: total ${r.total}, passed ${r.passed}, failed ${r.failed}, skipped ${r.skipped} (${Math.round(r.durationMs)} ms)`);
  const mismatches = Object.entries(expected).filter(([k, v]) => v !== undefined && r[k] !== v).map(([k, v]) => `${k}: expected ${v}, got ${r[k]}`);
  if (mismatches.length) throw new Error(`unexpected outcome counts: ${mismatches.join("; ")}`);

  // The run's exports (docs/contract.md "Export"): JUnit XML with one testcase per result, and the HTML report.
  const junitRes = await fetch(`${base}/api/runs/${runId}/export?format=junit`);
  const junit = await junitRes.text();
  if (!junitRes.ok) throw new Error(`JUnit export: HTTP ${junitRes.status} ${junit}`);
  if (!/attachment;.*\.junit\.xml/.test(junitRes.headers.get("content-disposition") ?? "")) throw new Error("JUnit export: no attachment file name");
  const cases = (junit.match(/<testcase /g) ?? []).length;
  const problems = (junit.match(/<(failure|error) /g) ?? []).length;
  if (cases !== r.total || problems !== r.failed) throw new Error(`JUnit export: ${cases} test cases / ${problems} failures, expected ${r.total} / ${r.failed}`);
  const htmlRes = await fetch(`${base}/api/runs/latest/export?format=html`);
  const html = await htmlRes.text();
  if (!htmlRes.ok || !html.startsWith("<!doctype html>")) throw new Error(`HTML export: HTTP ${htmlRes.status}`);
  console.log(`exports: JUnit ${cases} test cases (${problems} failed), HTML report ${Math.round(html.length / 1024)} KB`);

  const health = await (await fetch(`${base}/api/health`)).json();
  console.log(`health: ${JSON.stringify(health)}`);
  ok = true;
} catch (e) {
  console.error(`SMOKE TEST FAILED: ${e.message}`);
} finally {
  await stop();
  try { rmSync(historyDir, { recursive: true, force: true }); } catch { /* best effort */ }
}
console.log(ok ? "SMOKE TEST PASSED" : "");
process.exit(ok ? 0 : 1);
