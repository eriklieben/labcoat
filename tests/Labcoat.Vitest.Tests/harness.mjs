// Test harness for the Vitest runner (src/Labcoat.Vitest/runner): a scratch Vitest project, and the client side of the
// server protocol (what Labcoat's MtpHost / MtpConnection do: listen, start the host, initialize, send requests).
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { encodeMessage, MessageReader } from "../../src/Labcoat.Vitest/runner/protocol.mjs";

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
export const bridge = path.join(repo, "src/Labcoat.Vitest/runner/bridge.mjs");

/**
 * The node_modules with vitest and @vitest/coverage-v8 the scratch projects link to: $LABCOAT_VITEST_NODE_MODULES, else
 * the UI's (ui/, `npm ci`). Undefined when there is none: the tests that run Vitest are skipped then.
 */
export const vitestModules = [process.env.LABCOAT_VITEST_NODE_MODULES, path.join(repo, "ui", "node_modules")]
  .filter(Boolean)
  .find((dir) => fs.existsSync(path.join(dir, "vitest", "package.json")));

export const skipWithoutVitest = vitestModules ? false : "vitest isn't installed (npm ci in ui/, or set LABCOAT_VITEST_NODE_MODULES)";

/** A scratch Vitest project: `files` (relative path -> text) plus a package.json and node_modules linked to vitestModules. */
export function scratchProject(files) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "labcoat-vitest-"));
  const all = {
    "package.json": JSON.stringify({ name: "scratch", private: true, type: "module", devDependencies: { vitest: "*" } }),
    "vitest.config.mjs": `export default { test: { include: ["test/**/*.test.js"], coverage: { provider: "v8", include: ["src/**/*.js"] } } };`,
    ...files,
  };
  for (const [rel, text] of Object.entries(all)) {
    const file = path.join(dir, rel);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text);
  }
  if (vitestModules) fs.symlinkSync(vitestModules, path.join(dir, "node_modules"), process.platform === "win32" ? "junction" : "dir");
  return { dir, path: (rel) => path.join(dir, rel), remove: () => fs.rmSync(dir, { recursive: true, force: true }) };
}

/** Starts the bridge for `projectFile` and connects to it like Labcoat; `requests` / `updates` / `logs` are what it sent. */
export async function startHost(projectFile, extraArgs = []) {
  const server = net.createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const child = spawn(process.execPath, [bridge, "--project", projectFile, "--server", "--client-host", "localhost",
    "--client-port", String(server.address().port), ...extraArgs], { cwd: path.dirname(projectFile), stdio: ["ignore", "pipe", "pipe"] });
  let stderr = "";
  child.stderr.on("data", (d) => (stderr += d));
  const exited = new Promise((resolve) => child.on("exit", resolve));
  const socket = await Promise.race([
    new Promise((resolve) => server.once("connection", resolve)),
    exited.then((code) => { throw new Error(`bridge exited with ${code} before connecting:\n${stderr}`); }),
  ]);
  let nextId = 0;
  const pending = new Map();
  const host = {
    updates: [], // test nodes of testing/testUpdates/tests, in order
    ends: 0, // testing/testUpdates/tests with changes: null
    logs: [],
    exited,
    get stderr() { return stderr; },
    lastId: () => nextId,
    request(method, params) {
      const id = ++nextId;
      const response = new Promise((resolve) => pending.set(id, resolve));
      socket.write(encodeMessage({ jsonrpc: "2.0", id, method, params }));
      return response;
    },
    notify(method, params) {
      socket.write(encodeMessage({ jsonrpc: "2.0", method, params }));
    },
    /** The final results (not discovered / in-progress) since the last call, as uid -> state. */
    results() {
      const finals = host.updates.splice(0).filter((n) => !["discovered", "in-progress"].includes(n["execution-state"]));
      return Object.fromEntries(finals.map((n) => [n.uid, n["execution-state"]]));
    },
    async close() {
      host.notify("exit", {});
      const code = await exited;
      server.close();
      return code;
    },
  };
  const reader = new MessageReader((m) => {
    if (m.id !== undefined && pending.has(m.id)) {
      pending.get(m.id)(m);
      pending.delete(m.id);
    } else if (m.method === "testing/testUpdates/tests") {
      if (m.params.changes) host.updates.push(...m.params.changes.map((c) => c.node));
      else host.ends++;
    } else if (m.method === "client/log") host.logs.push(m.params);
  });
  socket.on("data", (d) => reader.push(d));
  const init = await host.request("initialize", { processId: process.pid, clientInfo: { name: "test" }, capabilities: {} });
  host.serverInfo = init.result.serverInfo;
  return host;
}
