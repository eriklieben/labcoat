// The Vitest runner end to end: a real Vitest (see harness.mjs) behind the server protocol, as Labcoat drives it.
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { after, before, describe, it } from "node:test";
import { scratchProject, skipWithoutVitest, startHost } from "./harness.mjs";

const files = {
  "src/math.js": "export const add = (a, b) => a + b;\nexport const unused = () => 42;\n",
  "test/math.test.js": `
import { describe, expect, it } from "vitest";
import { add } from "../src/math.js";
describe("math", () => {
  it("adds", () => { expect(add(2, 3)).toBe(5); });
  it.each([[1, 1, 2], [2, 2, 4]])("add(%i, %i) = %i", (a, b, c) => { expect(add(a, b)).toBe(c); });
  describe("nested", () => {
    it("fails", () => { console.log("about to fail"); expect({ a: 1 }).toEqual({ a: 2 }); });
    it.skip("skipped", () => {});
  });
});
it("top level", () => {});
`,
  "test/slow.test.js": `
import { it } from "vitest";
it("slow", async () => { await new Promise((r) => setTimeout(r, 3000)); });
`,
};

const MATH = "test/math.test.js";
const uid = (name) => `${MATH} > ${name}`;

describe("Vitest runner", { skip: skipWithoutVitest }, () => {
  let project;
  before(() => { project = scratchProject(files); });
  after(() => project?.remove());

  it("discovers the tests with the tree and ids Labcoat reads", async () => {
    const host = await startHost(project.path("package.json"));
    try {
      assert.equal(host.serverInfo.name, "labcoat-vitest");
      const response = await host.request("testing/discoverTests", { runId: "d" });
      assert.deepEqual(response.result, {});
      assert.equal(host.ends, 1);
      const nodes = Object.fromEntries(host.updates.map((n) => [n.uid, n]));
      assert.deepEqual(Object.keys(nodes).sort(), [
        uid("math > add(1, 1) = 2"), uid("math > add(2, 2) = 4"), uid("math > adds"), uid("math > nested > fails"),
        uid("math > nested > skipped"), uid("top level"), "test/slow.test.js > slow",
      ].sort());
      const row = nodes[uid("math > add(1, 1) = 2")];
      assert.equal(row["execution-state"], "discovered");
      assert.equal(row["labcoat.namespace"], MATH);
      assert.equal(row["labcoat.class"], "math");
      assert.equal(row["labcoat.method"], "add(%i, %i) = %i", "test.each rows group under the name template");
      assert.equal(row["display-name"], "add(1, 1) = 2");
      assert.equal(row["location.file"], project.path(MATH));
      assert.equal(row["location.line-start"], 6);
      assert.equal(nodes[uid("math > nested > fails")]["labcoat.class"], "math > nested");
      assert.equal(nodes[uid("top level")]["labcoat.class"], undefined);
    } finally {
      assert.equal(await host.close(), 0);
    }
  });

  it("runs everything, then exactly the tests asked for", async () => {
    const host = await startHost(project.path("package.json"));
    try {
      await host.request("testing/discoverTests", { runId: "d" });
      host.updates.length = 0;
      await host.request("testing/runTests", { runId: "all" });
      const all = host.results();
      assert.equal(all[uid("math > adds")], "passed");
      assert.equal(all[uid("math > nested > fails")], "failed");
      assert.equal(all[uid("math > nested > skipped")], "skipped");
      assert.equal(all["test/slow.test.js > slow"], "passed");

      await host.request("testing/runTests", { runId: "subset", tests: [{ uid: uid("math > adds") }, { uid: uid("math > nested > fails") }] });
      assert.deepEqual(host.results(), { [uid("math > adds")]: "passed", [uid("math > nested > fails")]: "failed" },
        "the tests Vitest filtered out (reported as skipped) don't reach Labcoat");
    } finally {
      await host.close();
    }
  });

  it("reports a failure with its message, expected / actual, frames and console output", async () => {
    const host = await startHost(project.path("package.json"));
    try {
      await host.request("testing/runTests", { runId: "r", tests: [{ uid: uid("math > nested > fails") }] });
      const failed = host.updates.find((n) => n["execution-state"] === "failed");
      assert.match(failed["error.message"], /^expected \{ a: 1 \} to deeply equal \{ a: 2 \} Expected:<\{\n {2}"a": 2,\n\}>\. Actual:<\{\n {2}"a": 1,\n\}>\.$/);
      assert.equal(failed["assert.expected"], '{\n  "a": 2,\n}');
      assert.match(failed["error.stacktrace"], new RegExp(`^ {3}at .+ in ${project.path(MATH).replace(/[\\.]/g, "\\$&")}:line 8$`, "m"));
      assert.equal(failed.standardOutput, "about to fail\n");
      assert.equal(typeof failed["time.duration-ms"], "number");
    } finally {
      await host.close();
    }
  });

  it("runs ids it hasn't discovered (a new process for a coverage run) and writes Cobertura", async () => {
    const output = project.path("coverage-out/run.cobertura.xml");
    fs.mkdirSync(path.dirname(output), { recursive: true });
    const host = await startHost(project.path("package.json"),
      ["--coverage", "--coverage-output-format", "cobertura", "--coverage-output", output]);
    try {
      const response = await host.request("testing/runTests", { runId: "c", tests: [{ uid: uid("math > adds") }] });
      assert.deepEqual(host.results(), { [uid("math > adds")]: "passed" });
      assert.deepEqual(response.result.attachments.map((a) => a.uri), [output]);
      const xml = fs.readFileSync(output, "utf8");
      assert.match(xml, /filename="src\/math\.js"|filename="math\.js"/);
      assert.deepEqual(host.logs.filter((l) => l.level === "Error"), []);
    } finally {
      await host.close();
    }
    assert.deepEqual(fs.readdirSync(path.dirname(output)), ["run.cobertura.xml"], "the runner's own reports folder is removed on exit");
  });

  it("answers a cancel at once and serves the next request after the run stopped", async () => {
    const host = await startHost(project.path("package.json"));
    try {
      await host.request("testing/discoverTests", { runId: "d" });
      const run = host.request("testing/runTests", { runId: "slow", tests: [{ uid: "test/slow.test.js > slow" }] });
      const id = host.lastId();
      await new Promise((r) => setTimeout(r, 500));
      const started = Date.now();
      host.notify("$/cancelRequest", { id });
      const response = await run;
      assert.equal(response.error?.code, -32800);
      assert.ok(Date.now() - started < 1000, "answered before the running test ends");
      host.updates.length = 0;
      await host.request("testing/runTests", { runId: "next", tests: [{ uid: uid("math > adds") }] });
      assert.deepEqual(host.results(), { [uid("math > adds")]: "passed" });
    } finally {
      await host.close();
    }
  });

  it("picks up edited sources and test files on the warm host", async () => {
    const edited = scratchProject(files);
    const host = await startHost(edited.path("package.json"));
    try {
      await host.request("testing/discoverTests", { runId: "d" });
      host.updates.length = 0;
      await host.request("testing/runTests", { runId: "1", tests: [{ uid: uid("math > adds") }] });
      assert.deepEqual(host.results(), { [uid("math > adds")]: "passed" });

      fs.writeFileSync(edited.path("src/math.js"), "export const add = (a, b) => a - b;\n");
      fs.writeFileSync(edited.path(MATH), fs.readFileSync(edited.path(MATH), "utf8").replace('it("top level"', 'it("added", () => {});\nit("top level"'));
      bumpTime(edited.path("src/math.js"));
      bumpTime(edited.path(MATH));
      await host.request("testing/discoverTests", { runId: "d2" });
      assert.ok(host.updates.some((n) => n.uid === uid("added")), "the new test is discovered");
      host.updates.length = 0;
      await host.request("testing/runTests", { runId: "2", tests: [{ uid: uid("math > adds") }, { uid: uid("added") }] });
      assert.deepEqual(host.results(), { [uid("math > adds")]: "failed", [uid("added")]: "passed" });
    } finally {
      await host.close();
      edited.remove();
    }
  });

  it("explains a project without Vitest", async () => {
    const empty = scratchProject({});
    fs.rmSync(empty.path("node_modules"), { force: true, recursive: true });
    const host = await startHost(empty.path("package.json"));
    try {
      const response = await host.request("testing/discoverTests", { runId: "d" });
      assert.match(response.error.message, /^Vitest is not installed for .+: run npm install/);
    } finally {
      await host.close();
      empty.remove();
    }
  });
});

/** A later modification time (file systems with coarse timestamps would otherwise see no change). */
function bumpTime(file) {
  const later = new Date(Date.now() + 5000);
  fs.utimesSync(file, later, later);
}
