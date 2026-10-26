// Vitest tests as MTP test nodes (docs/protocol.md, "A node as observed"), plus the labcoat.* properties Labcoat reads
// instead of location.type / location.method (MtpMapping.ToTestNode):
//   labcoat.namespace  the test file, relative to the project ("test/math.test.ts"): a tree level per file
//   labcoat.class      the describe blocks around the test ("math > nested"); none for a test at the top of a file
//   labcoat.method     the test name; for test.each rows the name template ("add(%i, %i) = %i"), so the rows group
// The uid is the file and the full name ("test/math.test.ts > math > adds"): stable across processes and edits elsewhere
// in the file (Vitest's own ids are positions). Tests with the same full name get " #2", " #3", ... in file order.
import fs from "node:fs";
import path from "node:path";

const ANSI_RE = /\x1b\[[0-9;]*m/g;
const stripAnsi = (s) => (typeof s === "string" ? s.replace(ANSI_RE, "") : s);
const posix = (p) => p.split(path.sep).join("/");

export class TestIndex {
  #root;
  #byUid = new Map(); // uid -> { moduleId, taskId, project }
  #byTaskId = new Map(); // Vitest task id -> uid
  #uidsOfModule = new Map(); // moduleId -> uid[]
  #moduleStamp = new Map(); // moduleId -> mtime when its uids were computed
  #templates = new Map(); // `${moduleId}:${line}` -> test.each name template
  #templateStamp = new Map(); // moduleId -> mtime of the templates
  multiProject = false;

  constructor(root) {
    this.#root = root;
  }

  /** The uid is known (its module was collected in this process). */
  has(uid) {
    return this.#byUid.has(uid);
  }

  relative(moduleId) {
    return posix(path.relative(this.#root, moduleId));
  }

  /** The uid of a test (computed with its module when the module is unknown). */
  uidOf(test) {
    let uid = this.#byTaskId.get(test.id);
    if (uid === undefined) {
      this.addModule(test.module);
      uid = this.#byTaskId.get(test.id) ?? this.#baseUid(test);
    }
    return uid;
  }

  template(test) {
    if (!test.options?.each || !test.location) return undefined;
    return this.#templates.get(`${test.module.moduleId}:${test.location.line}`);
  }

  /** Assigns the uids of a collected (or running) module's tests. */
  addModule(module) {
    for (const uid of this.#uidsOfModule.get(module.moduleId) ?? []) {
      const entry = this.#byUid.get(uid);
      if (entry) this.#byTaskId.delete(entry.taskId);
      this.#byUid.delete(uid);
    }
    const uids = [];
    const seen = new Map();
    for (const test of module.children.allTests()) {
      const base = this.#baseUid(test);
      const n = (seen.get(base) ?? 0) + 1;
      seen.set(base, n);
      const uid = n === 1 ? base : `${base} #${n}`;
      this.#byUid.set(uid, { moduleId: module.moduleId, taskId: test.id, project: module.project });
      this.#byTaskId.set(test.id, uid);
      uids.push(uid);
    }
    this.#uidsOfModule.set(module.moduleId, uids);
    this.#moduleStamp.set(module.moduleId, mtime(module.moduleId));
  }

  #baseUid(test) {
    const project = this.multiProject && test.project?.name ? `[${test.project.name}] ` : "";
    return `${project}${this.relative(test.module.moduleId)} > ${test.fullName}`;
  }

  /** The test.each name templates of the modules (a static parse; only for modules changed since the last one). */
  async readTemplates(vitest, specs) {
    this.multiProject = vitest.projects.length > 1;
    const stale = specs.filter((s) => this.#templateStamp.get(s.moduleId) !== mtime(s.moduleId));
    if (stale.length === 0) return;
    const parse = (vitest.parseSpecifications ?? vitest.experimental_parseSpecifications)?.bind(vitest);
    if (!parse) return;
    let modules;
    try {
      modules = await parse(stale);
    } catch {
      return; // templates are cosmetic: the rows are listed without a group
    }
    for (const module of modules) {
      this.#templateStamp.set(module.moduleId, mtime(module.moduleId));
      for (const test of module.children.allTests()) {
        if (test.options?.each && test.location) this.#templates.set(`${module.moduleId}:${test.location.line}`, test.name);
      }
    }
    // The parsed modules stay in Vitest's state next to the collected ones (and would be listed with them): drop them.
    for (const spec of stale) vitest.state.clearFiles(spec.project, [spec.moduleId]);
  }

  /**
   * Specifications that run exactly the tests with these uids. A test file changed since its uids were computed is
   * collected again first (test positions, and so Vitest's ids, may have moved).
   */
  async specificationsFor(vitest, uids) {
    const changed = new Map();
    for (const uid of uids) {
      const entry = this.#byUid.get(uid);
      if (entry && this.#moduleStamp.get(entry.moduleId) !== mtime(entry.moduleId)) changed.set(entry.moduleId, entry.project);
    }
    if (changed.size > 0) {
      const result = await vitest.collectTests([...changed].map(([moduleId, project]) => project.createSpecification(moduleId)));
      for (const module of result.testModules) this.addModule(module);
    }
    const byModule = new Map();
    for (const uid of uids) {
      const entry = this.#byUid.get(uid);
      if (!entry) continue; // removed meanwhile
      const key = `${entry.project.name}\u0000${entry.moduleId}`;
      const group = byModule.get(key) ?? { project: entry.project, moduleId: entry.moduleId, testIds: [] };
      group.testIds.push(entry.taskId);
      byModule.set(key, group);
    }
    return [...byModule.values()].map((g) => g.project.createSpecification(g.moduleId, { testIds: g.testIds }));
  }
}

function mtime(file) {
  try {
    return fs.statSync(file).mtimeMs;
  } catch {
    return -1;
  }
}

/** The describe blocks around a test, outermost first. */
function suiteNames(test) {
  const names = [];
  for (let p = test.parent; p && p.type === "suite"; p = p.parent) names.unshift(p.name);
  return names;
}

/** MTP execution-state of a reported test (state = "discovered" / "in-progress" wins). */
function stateOf(test, result) {
  switch (result.state) {
    case "passed":
      return "passed";
    case "skipped":
      return "skipped";
    case "failed":
      return result.errors?.some((e) => /^Test timed out in \d+ms/.test(e?.message ?? "")) ? "timed-out" : "failed";
    default:
      return "in-progress";
  }
}

export function outputOf(buffer) {
  return { standardOutput: buffer.stdout || undefined, standardError: buffer.stderr || undefined };
}

/** `error.message`, `error.stacktrace` and the expected / actual values of a failure. */
export function errorProperties(errors) {
  const error = errors?.[0];
  if (!error) return {};
  let message = stripAnsi(error.message ?? String(error));
  if (error.name && error.name !== "Error" && error.name !== "AssertionError") message = `${error.name}: ${message}`;
  const props = {};
  if (typeof error.expected === "string" && typeof error.actual === "string") {
    // The MSTest object form the UI's expected/actual diff reads (ui/src/diff.ts, parseObjectForm).
    message += ` Expected:<${stripAnsi(error.expected)}>. Actual:<${stripAnsi(error.actual)}>.`;
    props["assert.expected"] = stripAnsi(error.expected);
    props["assert.actual"] = stripAnsi(error.actual);
  } else if (error.diff) {
    message += "\n\n" + stripAnsi(error.diff);
  }
  if (errors.length > 1) message += `\n\n(+${errors.length - 1} more error${errors.length > 2 ? "s" : ""})`;
  props["error.message"] = message;
  // .NET style frames ("   at fn in /path/file.ts:line 12"): the UI links them and shows the failing line.
  const frames = (error.stacks ?? [])
    .filter((f) => f?.file && !f.file.includes("/node_modules/"))
    .map((f) => `   at ${f.method || "<anonymous>"} in ${f.file}:line ${f.line}`);
  props["error.stacktrace"] = frames.length > 0 ? frames.join("\n") : stripAnsi(error.stack ?? error.stackStr ?? "");
  return props;
}

/** The MTP test node of a Vitest test case. */
export function toNode(index, test, state, output) {
  const result = test.result();
  const execution = state ?? stateOf(test, result);
  const suites = suiteNames(test);
  const node = {
    uid: index.uidOf(test),
    "display-name": test.name,
    "node-type": "action",
    "execution-state": execution,
    "location.file": test.module.moduleId,
    "labcoat.namespace": index.relative(test.module.moduleId),
    "labcoat.method": index.template(test) ?? test.name,
  };
  if (suites.length > 0) node["labcoat.class"] = suites.join(" > ");
  if (test.location) {
    node["location.line-start"] = test.location.line;
    node["location.line-end"] = test.location.line;
  }
  const tags = test.tags ?? [];
  if (tags.length > 0 || index.multiProject) {
    node.traits = tags.map((t) => ({ [t]: "" }));
    if (index.multiProject && test.project?.name) node.traits.push({ Project: test.project.name });
  }
  if (execution === "passed" || execution === "failed" || execution === "timed-out" || execution === "skipped") {
    const duration = test.diagnostic()?.duration;
    if (typeof duration === "number") node["time.duration-ms"] = duration;
  }
  if (execution === "failed" || execution === "timed-out") Object.assign(node, errorProperties(result.errors));
  if (execution === "skipped") {
    const reason = result.note ?? (test.options?.mode === "todo" ? "todo" : undefined);
    if (reason) node["error.message"] = reason;
  }
  if (output) Object.assign(node, Object.fromEntries(Object.entries(output).filter(([, v]) => v !== undefined)));
  return node;
}
