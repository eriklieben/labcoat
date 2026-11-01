// The pieces of the Vitest runner that need no Vitest: the protocol framing and the mapping of results onto test nodes.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { encodeMessage, MessageReader, parseArgs } from "../../src/Labcoat.Vitest/runner/protocol.mjs";
import { errorProperties, outputOf, TestIndex, toNode } from "../../src/Labcoat.Vitest/runner/mapping.mjs";

describe("protocol", () => {
  it("parses --name value, --name=value and flags", () => {
    assert.deepEqual(parseArgs(["--project", "a/package.json", "--server", "--client-port=123", "--coverage", "--coverage-output", "x.xml", "stray"]),
      { project: "a/package.json", server: true, "client-port": "123", coverage: true, "coverage-output": "x.xml" });
  });

  it("frames messages and reads them back from any chunking", () => {
    const messages = [{ id: 1, method: "a", params: { text: "héllo ✓" } }, { method: "b" }];
    const bytes = Buffer.concat(messages.map(encodeMessage));
    assert.match(encodeMessage(messages[0]).toString("utf8"), /^Content-Length: \d+\r\n\r\n\{/);
    for (const size of [1, 7, bytes.length]) {
      const read = [];
      const reader = new MessageReader((m) => read.push(m));
      for (let i = 0; i < bytes.length; i += size) reader.push(bytes.subarray(i, i + size));
      assert.deepEqual(read, messages, `chunks of ${size}`);
    }
  });

  it("rejects a message without Content-Length", () => {
    const reader = new MessageReader(() => {});
    assert.throws(() => reader.push(Buffer.from("X-Other: 1\r\n\r\n{}")), /Missing Content-Length/);
  });
});

describe("errorProperties", () => {
  it("puts expected / actual in the MSTest object form the UI diffs", () => {
    const props = errorProperties([{ name: "AssertionError", message: "expected 1 to be 2", expected: "2", actual: "1", stacks: [] , stack: "AssertionError: x\n    at y" }]);
    assert.equal(props["error.message"], "expected 1 to be 2 Expected:<2>. Actual:<1>.");
    assert.equal(props["assert.expected"], "2");
    assert.equal(props["assert.actual"], "1");
  });

  it("keeps the diff, names other errors and counts the rest", () => {
    const props = errorProperties([{ name: "TypeError", message: "x is not a function", diff: "\x1b[32m- a\x1b[39m" }, { message: "second" }]);
    assert.equal(props["error.message"], "TypeError: x is not a function\n\n- a\n\n(+1 more error)");
  });

  it("writes .NET style frames, without node_modules", () => {
    const props = errorProperties([{ message: "m", stacks: [
      { method: "inner", file: "/w/node_modules/lib/index.js", line: 3 },
      { method: "", file: "/w/test/a.test.ts", line: 12 },
    ] }]);
    assert.equal(props["error.stacktrace"], "   at <anonymous> in /w/test/a.test.ts:line 12");
  });

  it("falls back to the raw stack without frames", () => {
    assert.equal(errorProperties([{ message: "m", stacks: [], stack: "Error: m\n    at x (y.js:1:1)" }])["error.stacktrace"], "Error: m\n    at x (y.js:1:1)");
  });
});

/** A reported Vitest test case as far as mapping.mjs reads it. */
function fakeTest({ id, name, suites = [], file = "/w/test/a.test.ts", line = 3, each = false, mode = "run", result = { state: "pending" }, tags = [] }) {
  let module = modules.get(file);
  if (!module) {
    const m = { moduleId: file, project: { name: "" }, children: { allTests: () => tests.filter((t) => t.module === m) } };
    modules.set(file, (module = m));
  }
  let parent = module;
  for (const s of suites) parent = { type: "suite", name: s, parent };
  const test = { id, name, module, parent, location: { line }, options: { each, mode }, tags, project: { name: "" },
    fullName: [...suites, name].join(" > "), result: () => result, diagnostic: () => ({ duration: 1.5 }) };
  return test;
}
const tests = [];
const modules = new Map();

describe("toNode", () => {
  it("maps a test onto the node Labcoat reads", () => {
    const index = new TestIndex("/w");
    const test = fakeTest({ id: "1", name: "adds", suites: ["math", "nested"], result: { state: "passed" }, tags: ["slow"] });
    tests.push(test);
    assert.deepEqual(toNode(index, test, undefined, outputOf({ stdout: "out\n", stderr: "" })), {
      uid: "test/a.test.ts > math > nested > adds",
      "display-name": "adds",
      "node-type": "action",
      "execution-state": "passed",
      "location.file": "/w/test/a.test.ts",
      "labcoat.namespace": "test/a.test.ts",
      "labcoat.method": "adds",
      "labcoat.class": "math > nested",
      "location.line-start": 3,
      "location.line-end": 3,
      traits: [{ slow: "" }],
      "time.duration-ms": 1.5,
      standardOutput: "out\n",
    });
  });

  it("gives tests with the same full name their own uids, in file order", () => {
    const file = "/w/test/dup.test.ts";
    const a = fakeTest({ id: "a", name: "same", file });
    const b = fakeTest({ id: "b", name: "same", file });
    tests.push(a, b);
    const index = new TestIndex("/w");
    index.addModule(a.module);
    assert.equal(index.uidOf(a), "test/dup.test.ts > same");
    assert.equal(index.uidOf(b), "test/dup.test.ts > same #2");
  });

  it("maps timeouts, skips with a note and todos", () => {
    const index = new TestIndex("/w");
    const timedOut = fakeTest({ id: "t", name: "t", result: { state: "failed", errors: [{ message: "Test timed out in 5000ms." }] } });
    const skipped = fakeTest({ id: "s", name: "s", result: { state: "skipped", note: "not on CI" } });
    const todo = fakeTest({ id: "d", name: "d", mode: "todo", result: { state: "skipped" } });
    tests.push(timedOut, skipped, todo);
    assert.equal(toNode(index, timedOut)["execution-state"], "timed-out");
    assert.equal(toNode(index, skipped)["error.message"], "not on CI");
    assert.equal(toNode(index, todo)["error.message"], "todo");
    assert.equal(toNode(index, skipped, "discovered")["execution-state"], "discovered");
  });
});
