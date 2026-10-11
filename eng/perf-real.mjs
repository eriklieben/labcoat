#!/usr/bin/env node
// Performance run against a REAL Labcoat host and a generated MSTest suite (eng/gen-perf-suite.mjs). Local only: it
// builds and runs 20,000 tests, which is too slow for CI. Measures, against ui/perf-budgets.json "real":
//   discovery (cold build + list), first full run, re-run on warm test hosts, testsUpdated throughput, WebSocket
//   payload sizes (lean vs full, per event type), REST payload sizes (/api/state, ?lean=1, /api/tests/history),
//   host memory (RSS from /proc, sampled every 250 ms; GC heap via dotnet-counters when it is on PATH), history file
//   size, UI initial render / time to interactive with full history, UI long tasks and frame times while a run streams,
//   and optionally a watch soak (--soak=N: N watch-triggered partial runs after editing a test file, with a full run
//   every 4th) with RSS / history / buffer growth.
//
// Usage (from the repository root):
//   node eng/perf-real.mjs [--projects=8] [--tests=2500] [--suite=<dir>] [--labcoat=<publish dir>] [--port=4560]
//                          [--soak=0] [--out=<dir>] [--keep] [--report-only] [--profile]
//   --suite    an existing generated suite (default: generate one into a temp dir, deleted afterwards unless --keep)
//   --labcoat  a `dotnet publish src/Labcoat -c Release -o <dir>` output (default: publish into a temp dir and build
//              the current UI into its wwwroot; src/Labcoat/wwwroot is never touched). --build-ui rebuilds the UI into
//              an existing --labcoat dir's wwwroot.
//   --expect   number of tests to wait for in discovery (default: projects × tests; needed with --suite)
//   --out      report directory (default ui/perf-results): perf-real.md / perf-real.json
//   --profile  also write a CPU profile of the UI during the run and the 5 s after it (<out>/perf-real-run.cpuprofile)
// Ports: --port (default 4560). History goes to a temp LABCOAT_HISTORY_DIR; user settings files are ignored.
import { spawn, spawnSync } from "node:child_process";
import { appendFileSync, existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";

const repo = resolve(fileURLToPath(new URL("..", import.meta.url)));
const kit = await import(new URL("../ui/scripts/lib/perf-kit.mjs", import.meta.url).href);
const { evaluate, frameStats, launch, loadBudgets, markdownTable, mb, newPage, parseArgs, pct, round, setPhase, splitBulk, writeReport } = kit;
const args = parseArgs();
const port = Number(args.port ?? 4560);
const base = `http://127.0.0.1:${port}`;
const outDir = args.out ?? join(repo, "ui", "perf-results");
const scratch = mkdtempSync(join(tmpdir(), "labcoat-perf-real-"));
const t00 = Date.now();
const log = (...a) => console.log(`[real ${((Date.now() - t00) / 1000).toFixed(0)}s]`, ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const cleanups = [];
const m = {};
const notes = [];

// ---- suite + host binaries --------------------------------------------------------------------------------------
let suite = args.suite && resolve(args.suite);
if (!suite) {
  suite = join(scratch, "suite");
  const r = spawnSync(process.execPath, [join(repo, "eng/gen-perf-suite.mjs"), `--out=${suite}`, `--projects=${args.projects ?? 8}`, `--tests=${args.tests ?? 2500}`], { encoding: "utf8" });
  if (r.status) throw new Error(r.stderr);
  log("generated", r.stdout.trim());
}
const expected = Number(args.expect ?? (args.suite ? 0 : Number(args.projects ?? 8) * Number(args.tests ?? 2500)));
let pub = args.labcoat && resolve(args.labcoat);
if (!pub) {
  pub = join(scratch, "labcoat");
  log("publishing src/Labcoat (Release) →", pub);
  const r = spawnSync("dotnet", ["publish", join(repo, "src/Labcoat"), "-c", "Release", "-o", pub, "-nologo", "-v:q"], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  if (r.status) throw new Error(r.stdout + r.stderr);
}
if (!args.labcoat || args["build-ui"]) {
  // the current UI (not whatever src/Labcoat/wwwroot holds), built straight into the publish output
  log("building the UI into", join(pub, "wwwroot"));
  const req = createRequire(join(repo, "ui", "package.json"));
  const { build } = await import(pathToFileURL(req.resolve("vite")).href);
  await build({ configFile: false, root: join(repo, "ui"), base: "./", logLevel: "error", build: { outDir: join(pub, "wwwroot"), emptyOutDir: true, chunkSizeWarningLimit: 4000 } });
}
const exe = existsSync(join(pub, "Labcoat")) ? join(pub, "Labcoat") : null;

// ---- host -------------------------------------------------------------------------------------------------------
const historyDir = join(scratch, "history");
const hostLog = join(scratch, "host.log");
const startHost = () => {
  const cmd = exe ?? "dotnet";
  const argv = [...(exe ? [] : [join(pub, "Labcoat.dll")]), join(suite, "PerfSuite.slnx"), "--no-open", "--port", String(port), "--no-user-config"];
  const p = spawn(cmd, argv, { cwd: suite, env: { ...process.env, LABCOAT_HISTORY_DIR: historyDir, LABCOAT_NO_USER_CONFIG: "1", CI: "1" }, stdio: ["ignore", "pipe", "pipe"] });
  p.stdout.on("data", (d) => appendFileSync(hostLog, d));
  p.stderr.on("data", (d) => appendFileSync(hostLog, d));
  return p;
};
const stopHost = async (p) => {
  if (p.exitCode != null) return;
  p.kill("SIGINT");
  for (let i = 0; i < 100 && p.exitCode == null; i++) await sleep(100);
  if (p.exitCode == null) p.kill("SIGKILL");
};
const api = async (path, init) => {
  const r = await fetch(base + path, init);
  const text = await r.text();
  return { status: r.status, bytes: Buffer.byteLength(text), json: () => JSON.parse(text) };
};
const post = (path, body = {}) => api(path, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
const summary = async () => (await api("/api/summary")).json();
const rssOf = (pid) => {
  try {
    const kb = Number(/VmRSS:\s+(\d+)/.exec(readFileSync(`/proc/${pid}/status`, "utf8"))?.[1] ?? 0);
    return kb * 1024;
  } catch {
    return 0;
  }
};
const historyBytes = () => {
  let n = 0;
  const walk = (d) => { for (const f of readdirSync(d, { withFileTypes: true })) f.isDirectory() ? walk(join(d, f.name)) : (n += statSync(join(d, f.name)).size); };
  if (existsSync(historyDir)) walk(historyDir);
  return n;
};

/** WebSocket listener (Node's WebSocket, no Origin header = a script): event counts, bytes per type, testsUpdated times. */
function listen(lean) {
  const stats = { lean, messages: 0, bytes: 0, byType: Object.create(null), updates: [], finished: [], started: [] };
  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws${lean ? "?lean=1" : ""}`);
  ws.onmessage = (e) => {
    const data = typeof e.data === "string" ? e.data : Buffer.from(e.data).toString();
    const bytes = Buffer.byteLength(data);
    stats.messages++;
    stats.bytes += bytes;
    let ev;
    try { ev = JSON.parse(data); } catch { return; }
    const b = (stats.byType[ev.type] ??= { n: 0, bytes: 0, maxBytes: 0, tests: 0 });
    b.n++;
    b.bytes += bytes;
    b.maxBytes = Math.max(b.maxBytes, bytes);
    // full entries (testsUpdated) and, since contract Round 7, result patches (testResults)
    const n = ev.type === "testsUpdated" ? ev.tests.length : ev.type === "testResults" ? ev.projects.reduce((x, g) => x + g.results.length, 0) : 0;
    if (n) {
      b.tests += n;
      stats.updates.push({ t: Date.now(), n });
    }
    if (ev.type === "runFinished") stats.finished.push({ t: Date.now(), run: ev.run });
    if (ev.type === "runStarted") stats.started.push({ t: Date.now(), run: ev.run });
  };
  const opened = new Promise((r) => (ws.onopen = r));
  return { stats, ws, opened, reset: () => Object.assign(stats, { messages: 0, bytes: 0, byType: Object.create(null), updates: [], finished: [], started: [] }) };
}

let host;
let rssMax = 0;
const rssSamples = [];
let sampler;
try {
  // ---- discovery ----------------------------------------------------------------------------------------------
  const t0 = Date.now();
  host = startHost();
  cleanups.push(() => stopHost(host));
  sampler = setInterval(() => { const r = rssOf(host.pid); rssMax = Math.max(rssMax, r); rssSamples.push({ t: Date.now() - t0, rss: r }); }, 250);
  let counters;
  const which = spawnSync("sh", ["-c", "command -v dotnet-counters"], { encoding: "utf8" });
  if (which.status === 0) {
    const file = join(scratch, "counters.json");
    counters = { file, p: spawn(which.stdout.trim(), ["collect", "-p", String(host.pid), "--format", "json", "-o", file, "--refresh-interval", "1", "--counters", "System.Runtime"], { stdio: "ignore" }) };
    cleanups.push(() => counters.p.kill("SIGINT"));
  } else notes.push("dotnet-counters not on PATH: server memory is RSS from /proc only (no GC heap size).");
  for (;;) {
    await sleep(250);
    if (host.exitCode != null) throw new Error(`host exited (${host.exitCode}), see ${hostLog}:\n${readFileSync(hostLog, "utf8").slice(-3000)}`);
    let s;
    try { s = await summary(); } catch { continue; }
    const busy = s.projects?.some((p) => ["building", "discovering", "queued"].includes(p.status)) || s.activeOperation;
    if (s.tests?.total >= (expected || 1) && !busy && s.projects?.length) {
      m.discoveryMs = Date.now() - t0;
      m.discoveredTests = s.tests.total;
      break;
    }
    if (Date.now() - t0 > 15 * 60_000) throw new Error("discovery timed out");
  }
  log("discovered", m.discoveredTests, "tests in", m.discoveryMs, "ms");
  m.rssAfterDiscoveryMB = mb(rssOf(host.pid));

  const lean = listen(true);
  const full = listen(false);
  await Promise.all([lean.opened, full.opened]);
  await sleep(500);
  lean.reset();
  full.reset();

  /** POST /api/run and wait for its runFinished on the lean socket */
  const run = async (body = {}) => {
    const before = lean.stats.finished.length;
    const ts = Date.now();
    const r = await post("/api/run", body);
    if (r.status >= 400) throw new Error(`POST /api/run → ${r.status}`);
    while (lean.stats.finished.length === before) {
      await sleep(100);
      if (Date.now() - ts > 30 * 60_000) throw new Error("run timed out");
    }
    return { ms: Date.now() - ts, run: lean.stats.finished.at(-1).run };
  };
  const throughput = (st) => {
    const u = st.updates;
    if (u.length < 2) return {};
    const dur = (u.at(-1).t - u[0].t) / 1000;
    return { events: u.length, eventsPerSec: round(u.length / dur, 1), testsPerSec: round(u.reduce((a, x) => a + x.n, 0) / dur, 0), maxTestsPerEvent: Math.max(...u.map((x) => x.n)) };
  };

  // ---- first full run (cold test hosts) ----------------------------------------------------------------------
  const r1 = await run();
  m.firstRunMs = r1.ms;
  m.firstRun = { total: r1.run.total, passed: r1.run.passed, failed: r1.run.failed, skipped: r1.run.skipped, hostDurationMs: r1.run.durationMs };
  m.firstRunThroughput = throughput(lean.stats);
  m.wsLean = structuredClone(lean.stats.byType);
  m.wsFull = structuredClone(full.stats.byType);
  m.wsLeanBytes = lean.stats.bytes;
  m.wsFullBytes = full.stats.bytes;
  log("first run", m.firstRunMs, "ms", JSON.stringify(m.firstRun), JSON.stringify(m.firstRunThroughput));

  // ---- re-run (warm hosts) ------------------------------------------------------------------------------------
  lean.reset();
  full.reset();
  const r2 = await run();
  m.rerunMs = r2.ms;
  m.rerunThroughput = throughput(lean.stats);
  log("re-run", m.rerunMs, "ms", JSON.stringify(m.rerunThroughput));

  // ---- REST payload sizes ------------------------------------------------------------------------------------
  m.stateKB = round((await api("/api/state")).bytes / 1024, 0);
  m.leanStateKB = round((await api("/api/state?lean=1")).bytes / 1024, 0);
  m.historyApiKB = round((await api("/api/tests/history")).bytes / 1024, 0);
  m.historyFileKB = round(historyBytes() / 1024, 0);
  m.serverRssMB = mb(rssOf(host.pid));
  log("payloads", { state: m.stateKB, lean: m.leanStateKB, history: m.historyApiKB, historyFile: m.historyFileKB, rss: m.serverRssMB });

  // ---- UI: load with full history, then a run streaming into the page ------------------------------------------
  const browser = await launch();
  cleanups.push(() => browser.close());
  const { page, cdp, errors } = await newPage(browser);
  await page.goto(base + "/");
  await page.waitForSelector(".tree-row", { timeout: 120000 });
  await page.waitForFunction((n) => window.__labcoat?.store?.get().tests.size >= n, m.discoveredTests, { timeout: 120000 });
  m.initialRenderMs = round(await page.evaluate(() => window.__perf.firstRow), 0);
  // history arrives after the snapshot (lean mode): wait for it, then for a quiet second
  await page.waitForFunction(() => [...window.__labcoat.store.get().tests.values()].some((t) => t.history?.length), null, { timeout: 60000 }).catch(() => notes.push("UI: no history seen within 60 s"));
  for (const t1 = Date.now(); ; ) {
    const { lastEnd, now } = await page.evaluate(() => { const l = window.__perf.long; const x = l.at(-1); return { lastEnd: x ? x.start + x.d : 0, now: performance.now() }; });
    if (now - lastEnd >= 1000 || Date.now() - t1 > 30000) { m.ttiMs = round(Math.max(lastEnd, m.initialRenderMs), 0); break; }
    await sleep(200);
  }
  m.loadLongestTaskMs = await page.evaluate(() => window.__perf.long.reduce((a, x) => Math.max(a, x.d), 0));
  m.uiHeapAfterLoadMB = mb((await cdp.send("Runtime.getHeapUsage")).usedSize);
  log("UI load", { initialRender: m.initialRenderMs, tti: m.ttiMs, longest: m.loadLongestTaskMs });
  await setPhase(page, "run");
  const framesDone = page.evaluate(async () => {
    const out = [];
    let last = performance.now();
    while (!window.__labcoat.store.get().currentRun) await new Promise((r) => requestAnimationFrame(r));
    for (;;) {
      await new Promise((r) => requestAnimationFrame(r));
      const t = performance.now();
      out.push(t - last);
      last = t;
      if (!window.__labcoat.store.get().currentRun) break;
    }
    return out;
  });
  if (args.profile) {
    await cdp.send("Profiler.enable");
    await cdp.send("Profiler.setSamplingInterval", { interval: 200 });
    await cdp.send("Profiler.start");
  }
  await page.getByRole("button", { name: "Run all" }).click();
  const frames = await framesDone;
  // after runFinished: whatever the UI does once a run is over (e.g. loading the run's history), until it is quiet
  await setPhase(page, "afterRun");
  await page.waitForTimeout(5000);
  if (args.profile) {
    const { profile } = await cdp.send("Profiler.stop");
    const { writeFileSync } = await import("node:fs");
    const file = join(outDir, "perf-real-run.cpuprofile");
    writeFileSync(file, JSON.stringify(profile));
    notes.push(`CPU profile of the UI run + 5 s after it: ${file}`);
  }
  const { runLong, bulk, afterLong } = await page.evaluate(() => ({ runLong: window.__perf.long.filter((x) => x.phase === "run"), bulk: window.__perf.bulk, afterLong: window.__perf.long.filter((x) => x.phase === "afterRun") }));
  m.afterRunLongestTaskMs = afterLong.length ? Math.max(...afterLong.map((x) => x.d)) : 0;
  m.afterRunLongTasks = afterLong.map((x) => x.d);
  const split = splitBulk(runLong, bulk);
  m.streamingLongestTaskMs = split.streaming.length ? Math.max(...split.streaming.map((x) => x.d)) : 0;
  m.streamingLongTasks = split.streaming.length;
  m.bulkUpdateLongestTaskMs = split.bulk.length ? Math.max(...split.bulk.map((x) => x.d)) : 0;
  m.streamingFrames = frameStats(frames);
  m.uiWs = await page.evaluate(() => window.__perf.ws);
  delete m.uiWs.updates;
  m.uiHeapAfterRunMB = mb((await cdp.send("Runtime.getHeapUsage")).usedSize);
  m.uiErrors = errors;
  log("UI run", { streamingLongest: m.streamingLongestTaskMs, n: m.streamingLongTasks, bulk: m.bulkUpdateLongestTaskMs, afterRun: m.afterRunLongTasks, frames: m.streamingFrames });

  // ---- optional watch soak ------------------------------------------------------------------------------------
  const soakN = Number(args.soak ?? 0);
  if (soakN > 0) {
    await post("/api/watch", { enabled: true });
    await sleep(2000);
    const files = [];
    const walk = (d) => { for (const f of readdirSync(d, { withFileTypes: true })) { const p = join(d, f.name); if (f.isDirectory() && f.name !== "bin" && f.name !== "obj") walk(p); else if (f.name.endsWith("Tests0.cs") || /Tests[1-3]\.cs$/.test(f.name)) files.push(p); } };
    walk(suite);
    const soak = [];
    for (let i = 0; i < soakN; i++) {
      if (i % 4 === 3) await run();
      else {
        const f = files[i % files.length];
        const before = lean.stats.finished.length;
        const ts = Date.now();
        appendFileSync(f, `\n// perf soak edit ${i}\n`);
        while (lean.stats.finished.length === before && Date.now() - ts < 10 * 60_000) await sleep(100);
      }
      await page.waitForTimeout(700);
      await cdp.send("HeapProfiler.collectGarbage");
      soak.push({ i, rss: rssOf(host.pid), history: historyBytes(), uiHeap: (await cdp.send("Runtime.getHeapUsage")).usedSize, uiNodes: (await cdp.send("Memory.getDOMCounters")).nodes });
      log(`soak ${i + 1}/${soakN}`, `rss ${mb(soak.at(-1).rss)} MB, history ${round(soak.at(-1).history / 1024, 0)} KB, ui heap ${mb(soak.at(-1).uiHeap)} MB`);
    }
    m.soak = soak;
    const warm = soak.slice(Math.min(4, soak.length - 2));
    const growth = (k) => round(kit.trend(warm.map((x) => x[k])).growth * 100, 1);
    m.soakRssGrowthPct = growth("rss");
    m.soakHistoryGrowthPct = growth("history");
    m.soakUiHeapGrowthPct = growth("uiHeap");
    m.soakMonotonic = ["rss", "history", "uiHeap", "uiNodes"].filter((k) => kit.monotonic(warm.map((x) => x[k])));
  }

  m.serverRssMaxMB = mb(rssMax);
  m.serverRssMB = mb(rssOf(host.pid));
  if (counters) {
    counters.p.kill("SIGINT");
    await sleep(1500);
    try {
      // the file is cut off when dotnet-counters is stopped mid-write: close the Events array ourselves
      let text = readFileSync(counters.file, "utf8").trim().replace(/,\s*$/, "");
      if (!text.endsWith("]}")) text += "]}";
      const j = JSON.parse(text);
      // .NET 9+ System.Runtime meter: dotnet.gc.last_collection.heap.size (bytes, one value per generation and
      // timestamp); older EventCounters: "GC Heap Size (MB)"
      const ev = j.Events ?? [];
      const perTs = new Map();
      for (const e of ev) {
        const name = e.name ?? e.displayName ?? "";
        if (/last_collection\.heap\.size/.test(name)) perTs.set(e.timestamp, (perTs.get(e.timestamp) ?? 0) + e.value / 1048576);
        else if (/gc-heap-size|GC Heap Size/i.test(name)) perTs.set(e.timestamp, Math.max(perTs.get(e.timestamp) ?? 0, e.value));
      }
      const gc = [...perTs.values()];
      if (gc.length) {
        m.serverGcHeapMaxMB = round(Math.max(...gc), 1);
        m.serverGcHeapEndMB = round(gc.at(-1), 1);
      } else notes.push(`dotnet-counters: no GC heap counter in ${[...new Set(ev.map((e) => e.name))].slice(0, 12).join(", ")}`);
    } catch (e) {
      notes.push(`dotnet-counters output not readable: ${e.message}`);
    }
  }
} finally {
  clearInterval(sampler);
  for (const c of cleanups.reverse()) await Promise.resolve(c()).catch(() => {});
}

// ---- report -----------------------------------------------------------------------------------------------------
const { rows, ok } = evaluate(m, loadBudgets().real.budgets);
const kb = (b) => `${round(b / 1024, 0)} KB`;
const types = [...new Set([...Object.keys(m.wsLean ?? {}), ...Object.keys(m.wsFull ?? {})])];
const md = [
  `## Labcoat against a real host: ${m.discoveredTests?.toLocaleString("en")} generated MSTest tests`,
  "",
  markdownTable("Budgets", rows),
  "",
  `First run: ${JSON.stringify(m.firstRun)}; testsUpdated ${JSON.stringify(m.firstRunThroughput)}. Re-run: ${JSON.stringify(m.rerunThroughput)}.`,
  "",
  "### WebSocket payloads during the first run (lean vs full)",
  "",
  "| Event | Lean: count | Lean: bytes | Lean: largest | Full: count | Full: bytes | Full: largest |",
  "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
  ...types.map((t) => { const l = m.wsLean?.[t] ?? {}; const f = m.wsFull?.[t] ?? {}; return `| ${t} | ${l.n ?? 0} | ${kb(l.bytes ?? 0)} | ${kb(l.maxBytes ?? 0)} | ${f.n ?? 0} | ${kb(f.bytes ?? 0)} | ${kb(f.maxBytes ?? 0)} |`; }),
  `| **total** | | ${kb(m.wsLeanBytes ?? 0)} | | | ${kb(m.wsFullBytes ?? 0)} | |`,
  "",
  `REST: /api/state ${m.stateKB} KB, ?lean=1 ${m.leanStateKB} KB, /api/tests/history ${m.historyApiKB} KB; history files ${m.historyFileKB} KB.`,
  `Host memory: RSS after discovery ${m.rssAfterDiscoveryMB} MB, max ${m.serverRssMaxMB} MB, at the end ${m.serverRssMB} MB${m.serverGcHeapMaxMB ? `, GC heap max ${m.serverGcHeapMaxMB} MB (at the end ${m.serverGcHeapEndMB} MB)` : ""}.`,
  `UI: heap ${m.uiHeapAfterLoadMB} MB after load, ${m.uiHeapAfterRunMB} MB after a run; streaming frames ${JSON.stringify(m.streamingFrames)}, ${m.streamingLongTasks} long tasks while streaming (longest ${m.streamingLongestTaskMs} ms; with a bulk update ${m.bulkUpdateLongestTaskMs} ms); long tasks in the 5 s after the run: ${JSON.stringify(m.afterRunLongTasks)}; longest load task ${m.loadLongestTaskMs} ms.`,
  `UI message handling (JSON.parse + store.apply, ms per event type during the UI run): ${JSON.stringify(Object.fromEntries(Object.entries(m.uiWs?.byType ?? {}).map(([k, v]) => [k, { n: v.n, kb: round(v.bytes / 1024, 0), ms: v.handleMs }])))}.`,
  m.soak ? `\nWatch soak (${m.soak.length} steps): RSS growth ${m.soakRssGrowthPct} %, history growth ${m.soakHistoryGrowthPct} %, UI heap growth ${m.soakUiHeapGrowthPct} %, monotonic: ${m.soakMonotonic.join(", ") || "none"}.` : "",
  notes.length ? `\nNotes:\n${notes.map((n) => `- ${n}`).join("\n")}` : "",
  m.uiErrors?.length ? `\nPage errors:\n${m.uiErrors.map((e) => `- ${e}`).join("\n")}` : "",
].join("\n");
writeReport(outDir, "perf-real", { at: new Date().toISOString(), metrics: m, budgets: rows, rssSamples, notes }, md);
console.log(md);
if (!args.keep) rmSync(scratch, { recursive: true, force: true });
else console.log("kept:", scratch);
process.exit(!ok && !args["report-only"] ? 1 : 0);
