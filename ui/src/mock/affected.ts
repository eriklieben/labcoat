// AFFECTED mock (?mock): GET /api/git and GET /api/affected for the "Run affected" button, and the run it sends.
//   - `?nogit`: the workspace is not in a git repository (GitInfo.available false, /api/affected 409 notGitRepo)
//   - a range ref containing "nope" → 404 unknownRef
// The plans are derived from the mock catalog's tests (their filePath), so the reasons name real mock tests.
import type { AffectedBase, AffectedFile, AffectedPlan, AffectedProject, AffectedTest, ChangedTests, GitInfo, TestNode } from "../contract";
import { AffectedError } from "../components/run-affected";

const ROOT = "/src/demo/samples";
const noGit = () => new URLSearchParams(location.search).has("nogit");

export function mockGitInfo(): Promise<GitInfo> {
  const g: GitInfo = noGit()
    ? { available: false, error: "Not a git repository (or any of the parent directories)." }
    : { available: true, root: ROOT.replace(/\/samples$/, ""), branch: "feature/orders-validation", head: "3f9c2ab", dirty: 3, upstream: "origin/feature/orders-validation", ahead: 2, behind: 0, defaultBase: "origin/main" };
  return Promise.resolve(g);
}

interface FileSpec {
  rel: string;
  status: AffectedFile["status"];
  kind: AffectedFile["kind"];
  mapping: AffectedFile["mapping"];
  /** which tests it selects */
  pick: (t: TestNode) => boolean;
  reason: (t: TestNode) => string;
  methods?: string[];
  lines?: [number, number][];
  oldPath?: string;
}

const base = (p: string | undefined) => (p ?? "").split("/").pop() ?? "";

function specs(b: AffectedBase): FileSpec[] {
  const working: FileSpec[] = [
    {
      rel: "samples/SampleTests/Orders/OrderServiceTests.cs",
      status: "modified",
      kind: "test",
      mapping: "testFile",
      lines: [[30, 34]],
      pick: (t) => base(t.filePath) === "OrderServiceTests.cs",
      reason: () => "OrderServiceTests.cs changed (test file)",
    },
    {
      rel: "samples/SampleTests.Lib/Orders/OrderService.cs",
      status: "modified",
      kind: "product",
      mapping: "impactMap",
      methods: ["Validate"],
      lines: [[41, 47]],
      pick: (t) => t.projectId === "SampleTests" && /Validate|Place/.test(t.methodName ?? ""),
      reason: () => "OrderService.cs changed (method Validate)",
    },
    { rel: "README.md", status: "untracked", kind: "other", mapping: "none", pick: () => false, reason: () => "" },
  ];
  if (b === "working") return working;
  if (b === "branch")
    return [
      ...working.slice(0, 2),
      {
        rel: "samples/AspireSample/AspireSample.Api/Program.cs",
        status: "modified",
        kind: "product",
        mapping: "project",
        lines: [[12, 18]],
        pick: (t) => t.projectId === "AspireSample.Tests",
        reason: () => "no impact data for Program.cs: all tests of AspireSample.Tests",
      },
      {
        rel: "samples/SampleTests.Lib/Text/Slug.cs",
        oldPath: "samples/SampleTests.Lib/Text/Slugify.cs",
        status: "renamed",
        kind: "product",
        mapping: "coverage",
        pick: (t) => base(t.filePath) === "SlugifyTests.cs",
        reason: () => "Slug.cs changed (covered by cached coverage)",
      },
      { rel: "docs/orders.md", status: "added", kind: "other", mapping: "none", pick: () => false, reason: () => "" },
    ];
  return [
    {
      rel: "samples/SampleTests.Lib/Math/Calculator.cs",
      status: "modified",
      kind: "product",
      mapping: "impactMap",
      methods: ["Multiply", "Divide"],
      lines: [
        [16, 18],
        [24, 24],
      ],
      pick: (t) => t.projectId === "SampleTests" && /Multiply|Divide/.test(t.methodName ?? ""),
      reason: (t) => `Calculator.cs changed (method ${/Multiply/.test(t.methodName ?? "") ? "Multiply" : "Divide"})`,
    },
  ];
}

/** GET /api/changed-tests: the tests of the mock's edited test file (see the working plan above). */
export async function mockChangedTests(tests: TestNode[]): Promise<ChangedTests> {
  const git = await mockGitInfo();
  if (!git.available) return { available: false, error: git.error, tests: [] };
  return { available: true, base: "branch", ref: git.defaultBase, tests: tests.filter((t) => base(t.filePath) === "OrderServiceTests.cs").map((t) => ({ projectId: t.projectId, id: t.id })) };
}

/** GET /api/affected?base=&ref= against the mock catalog. */
export function mockAffectedPlan(b: AffectedBase, ref: string | undefined, tests: TestNode[], signal?: AbortSignal): Promise<AffectedPlan> {
  return new Promise((resolve, reject) => {
    const id = window.setTimeout(async () => {
      const git = await mockGitInfo();
      if (!git.available) return reject(new AffectedError("The workspace is not in a git repository.", 409, "notGitRepo"));
      if (b === "range" && !ref) return reject(new AffectedError("base=range needs a ref (A..B, A...B or one commit).", 400, "refRequired"));
      if (ref?.includes("nope")) return reject(new AffectedError(`Unknown revision '${ref}'.`, 404, "unknownRef"));
      resolve(buildPlan(b, ref, tests, git));
    }, 180);
    signal?.addEventListener("abort", () => {
      window.clearTimeout(id);
      reject(new DOMException("Aborted", "AbortError"));
    });
  });
}

export function buildPlan(b: AffectedBase, ref: string | undefined, tests: TestNode[], git: GitInfo): AffectedPlan {
  const fileSpecs = specs(b);
  const reasons = new Map<string, { t: TestNode; reasons: string[] }>();
  const files: AffectedFile[] = fileSpecs.map((f) => {
    const hit = tests.filter(f.pick);
    for (const t of hit) {
      const k = `${t.projectId}|${t.id}`;
      const e = reasons.get(k) ?? { t, reasons: [] };
      e.reasons.push(f.reason(t));
      reasons.set(k, e);
    }
    return {
      path: `${git.root}/${f.rel}`,
      relativePath: f.rel,
      status: f.status,
      oldPath: f.oldPath,
      kind: f.kind,
      mapping: f.mapping,
      tests: hit.length,
      projectIds: hit.length ? [...new Set(hit.map((t) => t.projectId))] : undefined,
      lines: f.lines,
      methods: f.methods,
    };
  });
  const order = new Map(tests.map((t, i) => [`${t.projectId}|${t.id}`, i]));
  const selected: AffectedTest[] = [...reasons.entries()]
    .sort((a, z) => (order.get(a[0]) ?? 0) - (order.get(z[0]) ?? 0))
    .map(([, v]) => ({ projectId: v.t.projectId, id: v.t.id, reasons: v.reasons }));
  const projects: AffectedProject[] = [];
  for (const t of selected) {
    let p = projects.find((x) => x.projectId === t.projectId);
    if (!p) projects.push((p = { projectId: t.projectId, name: t.projectId, tests: 0 }));
    p.tests++;
  }
  for (const p of projects) if (fileSpecs.some((f) => f.mapping === "project" && tests.some((t) => t.projectId === p.projectId && f.pick(t)))) p.all = true;
  const mappings = new Set(files.filter((f) => f.tests > 0).map((f) => f.mapping));
  return {
    base: b,
    ref: b === "branch" ? (ref ?? git.defaultBase) : b === "range" ? ref : undefined,
    mergeBase: b === "branch" ? "9e1d04c" : undefined,
    git,
    files: files.sort((a, z) => a.relativePath.localeCompare(z.relativePath)),
    tests: selected,
    projects,
    counts: { files: files.length, tests: selected.length, projects: projects.length },
    strategy: mappings.size === 0 ? "none" : mappings.size > 1 ? "mixed" : ([...mappings][0] as AffectedPlan["strategy"]),
    needsImpactMap: files.some((f) => f.mapping === "project") || undefined,
    warnings: b === "branch" ? ["Tests of Inventory.Tests are not discovered yet (the build failed); they are not included."] : undefined,
  };
}
