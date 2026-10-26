#!/usr/bin/env node
// Labcoat's Vitest runner: a test host that speaks the Microsoft.Testing.Platform server-mode protocol (docs/protocol.md)
// on one side and drives the project's own Vitest through its Node API on the other, so Labcoat runs Vitest projects
// like any test app on Microsoft.Testing.Platform (warm host, live results, subsets, cancel, coverage).
//
//   node bridge.mjs --project <package.json | vitest config> --server --client-host localhost --client-port <port>
//                   [--coverage --coverage-output-format cobertura --coverage-output <file>]
//
// Vitest is loaded from the project (its node_modules, or a parent's in a workspace); this file has no dependencies.
// The mapping of Vitest tests onto MTP test nodes is in mapping.mjs.
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";
import { parseArgs, encodeMessage, MessageReader } from "./protocol.mjs";
import { TestIndex, toNode, outputOf } from "./mapping.mjs";

const VERSION = "1.0.0";
const args = parseArgs(process.argv.slice(2));
const projectFile = path.resolve(args.project ?? "package.json");
const root = path.dirname(projectFile);
const configFile = path.basename(projectFile) === "package.json" ? undefined : projectFile;

if (args.help) {
  // Labcoat probes a test app's --help for coverage support (Explorer.SupportsCoverageAsync).
  process.stdout.write("Labcoat Vitest runner\n  --project <file>\n  --coverage\n  --coverage-output-format cobertura\n  --coverage-output <file>\n");
  process.exit(0);
}
if (!args.server || !args["client-port"]) {
  process.stderr.write("labcoat-vitest: expected --server --client-port <port> (this is started by Labcoat).\n");
  process.exit(2);
}

/** Vitest's Node API, resolved from the project like `npx vitest` would. */
async function loadVitest() {
  const require = createRequire(path.join(root, "package.json"));
  let entry;
  try {
    entry = require.resolve("vitest/node");
  } catch {
    throw new Error(`Vitest is not installed for ${root}: run npm install (or pnpm / yarn) in the project first.`);
  }
  return import(pathToFileURL(entry).href);
}

const socket = net.connect({ host: args["client-host"] ?? "localhost", port: Number(args["client-port"]) });
socket.setNoDelay(true);
const send = (message) => socket.write(encodeMessage(message));
const notify = (method, params) => send({ jsonrpc: "2.0", method, params });
const log = (level, message) => notify("client/log", { level, message });

// --coverage: Vitest writes Cobertura into a reports folder of this process, copied to --coverage-output after each run.
const COBERTURA = "cobertura-coverage.xml";
const coverage = args.coverage
  ? (() => {
      const output = path.resolve(args["coverage-output"] ?? path.join(root, "coverage", "coverage.cobertura.xml"));
      return { output, reports: path.join(path.dirname(output), `${path.basename(output)}.vitest-${process.pid}`) };
    })()
  : undefined;

let vitest; // created on the first request after initialize
let current; // { id, runId, cancelled }
const index = new TestIndex(root);
const updates = new UpdateBatcher();

socket.on("error", () => process.exit(3));
socket.on("close", () => process.exit(3)); // Labcoat went away: don't leave the process behind (like MTP, exit code 3)

const reader = new MessageReader((msg) => dispatch(msg).catch((e) => process.stderr.write(`labcoat-vitest: ${e?.stack ?? e}\n`)));
socket.on("data", (chunk) => reader.push(chunk));

// Requests are handled one after the other (MTP: experimental_multiRequestSupport false).
let queue = Promise.resolve();

async function dispatch(msg) {
  if (msg.method === "$/cancelRequest") {
    if (current && current.id === msg.params?.id && !current.cancelled) {
      // Answered at once (MTP: the request fails with -32800); Vitest stops after the running tests end, and the next
      // request waits for that in the queue. Labcoat would kill a host that takes longer than a few seconds to answer.
      current.cancelled = true;
      updates.stop();
      send({ jsonrpc: "2.0", id: current.id, error: { code: -32800, message: "" } });
      await vitest?.cancelCurrentRun("keyboard-input");
    }
    return;
  }
  if (msg.method === "exit") {
    await shutdown(0);
    return;
  }
  if (msg.id === undefined) return; // other notifications
  queue = queue.then(() => handle(msg));
}

async function handle(msg) {
  try {
    const result = await request(msg);
    if (!current?.cancelled) send({ jsonrpc: "2.0", id: msg.id, result });
  } catch (e) {
    if (!current?.cancelled) send({ jsonrpc: "2.0", id: msg.id, error: { code: e?.code ?? -32000, message: e?.message ?? String(e), data: e?.stack } });
  } finally {
    current = undefined;
  }
}

async function request(msg) {
  switch (msg.method) {
    case "initialize":
      return {
        processId: process.pid,
        serverInfo: { name: "labcoat-vitest", version: VERSION },
        capabilities: {
          testing: {
            supportsDiscovery: true,
            experimental_multiRequestSupport: false,
            vstestProvider: false,
            attachmentsSupport: true,
            multipleConnectionProvider: false,
          },
        },
      };
    case "testing/discoverTests":
      current = { id: msg.id, runId: msg.params?.runId };
      return discover(msg.params?.runId);
    case "testing/runTests":
      current = { id: msg.id, runId: msg.params?.runId };
      return run(msg.params?.runId, msg.params?.tests);
    default: {
      const e = new Error(`Method '${msg.method}' is not supported by the Labcoat Vitest runner.`);
      e.code = -32601;
      throw e;
    }
  }
}

// ------------------------------------------------------------------ Vitest

async function ensureVitest() {
  if (vitest) return vitest;
  const { createVitest } = await loadVitest();
  const options = {
    root,
    config: configFile,
    watch: false,
    includeTaskLocation: true, // line numbers of tests and suites
    reporters: [reporter],
  };
  if (coverage) {
    options.coverage = {
      enabled: true,
      reporter: [["cobertura", { file: COBERTURA }]],
      // A folder of its own: Vitest cleans it (and needs to: it keeps its raw data in .tmp there), so not Labcoat's
      // coverage folder, which other projects' files share. The report is copied to --coverage-output after a run.
      reportsDirectory: coverage.reports,
      reportOnFailure: true,
    };
  }
  vitest = await createVitest("test", options, {}, {});
  return vitest;
}

/** Files of the Vite module graph whose modification time changed since the last run: invalidated before the next one. */
const mtimes = new Map();
function invalidateChanged() {
  for (const project of vitest.projects) {
    const graph = project.vite?.moduleGraph;
    if (!graph) continue;
    for (const file of graph.fileToModulesMap.keys()) {
      const mtime = fileTime(file);
      const before = mtimes.get(file);
      mtimes.set(file, mtime);
      if (before !== undefined && before !== mtime) vitest.invalidateFile(file);
    }
  }
}

/**
 * Discovered test nodes per test file, with the file's modification time. Discovery has to load a file to list its tests
 * exactly (test.each rows, tests made in loops; a static parse can't), which costs about as much as running it, so it
 * only collects new and changed files; runs refresh the files they collect too (reporter.onTestModuleCollected).
 * A file whose tests depend on another file that changed keeps its list until it runs (or the host restarts).
 */
const discovered = new Map(); // `${project}\0${moduleId}` -> { mtime, nodes }
const moduleKey = (project, moduleId) => `${project.name}\u0000${moduleId}`;

function remember(module) {
  index.addModule(module);
  const nodes = Array.from(module.children.allTests(), (test) => toNode(index, test, "discovered"));
  discovered.set(moduleKey(module.project, module.moduleId), { mtime: fileTime(module.moduleId), nodes });
}

async function discover(runId) {
  await ensureVitest();
  invalidateChanged();
  const specs = await collect();
  const changes = [];
  for (const spec of specs) {
    for (const node of discovered.get(moduleKey(spec.project, spec.moduleId))?.nodes ?? []) changes.push({ node, parent: null });
  }
  for (let i = 0; i < changes.length; i += 500) notify("testing/testUpdates/tests", { runId, changes: changes.slice(i, i + 500) });
  notify("testing/testUpdates/tests", { runId, changes: null });
  return {};
}

/** Globs the test files and collects the new and changed ones (see `discovered`); returns the specifications. */
async function collect() {
  const specs = await vitest.globTestSpecifications();
  const keys = new Set(specs.map((s) => moduleKey(s.project, s.moduleId)));
  for (const key of discovered.keys()) if (!keys.has(key)) discovered.delete(key); // deleted / no longer included
  const stale = specs.filter((s) => discovered.get(moduleKey(s.project, s.moduleId))?.mtime !== fileTime(s.moduleId));
  if (stale.length > 0) {
    await index.readTemplates(vitest, stale);
    const result = await vitest.collectTests(stale);
    for (const module of result.testModules) {
      remember(module);
      for (const error of module.errors()) log("Warning", `${index.relative(module.moduleId)}: ${error.message}`);
    }
  }
  return specs;
}

function fileTime(file) {
  try {
    return fs.statSync(file).mtimeMs;
  } catch {
    return -1;
  }
}

async function run(runId, tests) {
  await ensureVitest();
  invalidateChanged();
  let specs;
  if (Array.isArray(tests)) {
    // A new process (a coverage run, a host restarted after a crash) gets the ids Labcoat discovered in an earlier one.
    if (tests.some((t) => !index.has(t.uid))) await collect();
    specs = await index.specificationsFor(vitest, tests.map((t) => t.uid));
    if (specs.length === 0) {
      notify("testing/testUpdates/tests", { runId, changes: null });
      return {};
    }
  } else {
    specs = await vitest.globTestSpecifications();
    await index.readTemplates(vitest, specs);
  }
  // Coverage outside vitest.start(): the provider has to be created, and its folders (raw data in .tmp) set up, before
  // every run; without that the run fails with "Something removed the coverage directory".
  if (coverage) await (await vitest.createCoverageProvider())?.clean(true);
  // Vitest reports the tests a subset run filtered out as skipped: only the requested ones go to Labcoat.
  updates.start(runId, Array.isArray(tests) ? new Set(tests.map((t) => t.uid)) : undefined);
  try {
    const result = await vitest.runTestSpecifications(specs, !Array.isArray(tests));
    for (const error of result.unhandledErrors ?? []) log("Error", `Vitest: ${error?.message ?? error}`);
  } finally {
    if (!current?.cancelled) {
      updates.flush();
      notify("testing/testUpdates/tests", { runId, changes: null });
    }
    updates.stop();
  }
  if (coverage) {
    const report = path.join(coverage.reports, COBERTURA);
    if (fs.existsSync(report)) {
      fs.copyFileSync(report, coverage.output);
      return { attachments: [{ uri: coverage.output, producer: "labcoat-vitest", type: "cobertura" }] };
    }
    log("Warning", "Vitest wrote no coverage report (is a coverage provider such as @vitest/coverage-v8 installed?).");
  }
  return {};
}

// ------------------------------------------------------------------ reporter

/** Test updates go out in batches (like MTP's 200 ms), in-progress output at most twice a second per test. */
function UpdateBatcher() {
  let runId;
  let requested; // Set of uids, or undefined = every test
  let pending = [];
  let timer;
  const flush = () => {
    if (timer) clearTimeout(timer);
    timer = undefined;
    if (pending.length === 0 || !runId) return;
    notify("testing/testUpdates/tests", { runId, changes: pending });
    pending = [];
  };
  return {
    start(id, uids) { runId = id; requested = uids; },
    stop() {
      if (timer) clearTimeout(timer);
      timer = undefined;
      pending = [];
      runId = undefined;
      requested = undefined;
    },
    flush,
    add(node) {
      if (!runId || (requested && !requested.has(node.uid))) return;
      pending.push({ node, parent: null });
      timer ??= setTimeout(flush, 100);
    },
  };
}

// Console output by test id. Vitest reports a test's logs before the test itself (onTestCaseReady / onTestCaseResult
// come in batches), so the buffer starts with the first log; the final result takes the test's own logs (task.logs).
const output = new Map(); // test id -> { stdout, stderr, sent }

const reporter = {
  onTestModuleCollected(module) {
    remember(module);
  },
  onTestCaseReady(test) {
    const buffer = output.get(test.id);
    updates.add(toNode(index, test, "in-progress", buffer && outputOf(buffer)));
  },
  onUserConsoleLog(entry) {
    if (!entry.taskId) return;
    let buffer = output.get(entry.taskId);
    if (!buffer) output.set(entry.taskId, (buffer = { stdout: "", stderr: "", sent: 0 }));
    if (entry.type === "stderr") buffer.stderr += entry.content;
    else buffer.stdout += entry.content;
    const test = vitest.state.getReportedEntityById?.(entry.taskId);
    if (test?.type === "test" && test.result().state === "pending" && Date.now() - buffer.sent > 500) {
      buffer.sent = Date.now();
      updates.add(toNode(index, test, "in-progress", outputOf(buffer)));
    }
  },
  onTestCaseResult(test) {
    const buffer = output.get(test.id);
    output.delete(test.id);
    const logs = test.task?.logs;
    const final = Array.isArray(logs)
      ? { stdout: logs.filter((l) => l.type !== "stderr").map((l) => l.content).join(""), stderr: logs.filter((l) => l.type === "stderr").map((l) => l.content).join("") }
      : buffer;
    updates.add(toNode(index, test, undefined, final && outputOf(final)));
  },
};

async function shutdown(code) {
  try { await vitest?.close(); } catch { /* closing anyway */ }
  if (coverage) fs.rmSync(coverage.reports, { recursive: true, force: true });
  socket.end();
  process.exit(code);
}

process.on("SIGTERM", () => shutdown(0));
