// Scale scenarios for the mock backend (performance / soak checks, see ui/perf-budgets.json):
//   ?mock=huge           20,000 tests in 8 projects: deep namespaces, many data rows, realistic durations and outcomes
//   ?mock=many-projects  40 projects × 300 tests (12,000)
// Deterministic (seeded), so runs of the perf scripts are comparable. Every test gets the 12-run "previous session"
// history like the other mock scenarios (seedPreviousSession in mockServer.ts).

import type { TestProject } from "../contract";

export type ScaleKind = "huge" | "many-projects";

type Behavior = "pass" | "fail" | "skip" | "timeout" | "flaky";
interface MkOpts {
  args?: string;
  file: string;
  line: number;
  traits?: [string, string][];
  behavior?: Behavior;
  ms?: [number, number];
  error?: { type: string; message: string; stack: string };
  stdOut?: string;
  idx?: number;
}
type Mk<S> = (projectId: string, ns: string, cls: string, method: string, opts: MkOpts) => S;

const PRODUCTS = ["Billing", "Catalog", "Identity", "Shipping", "Inventory", "Search", "Payments", "Notifications"];
const AREAS = ["Api", "Domain", "Infrastructure", "Application", "Integration"];
const SUBS = ["Commands", "Queries", "Handlers", "Validators", "Mappers", "Policies"];
const FEATURES = ["Create", "Update", "Delete", "Lookup", "Export", "Import", "Sync", "Audit"];
const SUBJECTS = ["Order", "Customer", "Invoice", "Product", "Account", "Address", "Token", "Shipment", "Price", "Report"];
const VERBS = ["Handle", "Validate", "Map", "Resolve", "Compute", "Persist", "Publish", "Retry", "Parse", "Format"];
const ENDINGS = ["Succeeds", "ReturnsExpected", "ThrowsOnInvalidInput", "IsIdempotent", "RespectsCancellation", "LogsWarning"];

/**
 * `perProject` tests in each of `projectCount` projects. Namespaces are 5-6 levels deep
 * (`Contoso.<Product>.Tests.<Area>.<Sub>[.<Feature>]`), classes have ~8 methods, every third method is a
 * data-driven test with 3-8 rows. Outcomes: ~1% failed, ~0.5% skipped, ~0.3% flaky, ~0.05% timed out; durations
 * are mostly 1-30 ms with a long tail (a few % 100 ms-2 s).
 */
export function scaleCatalog<S>(kind: ScaleKind, root: string, mk: Mk<S>): { projects: TestProject[]; specs: S[] } {
  const projectCount = kind === "huge" ? 8 : 40;
  const perProject = kind === "huge" ? 2500 : 300;
  let seed = 7;
  const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  const projects: TestProject[] = [];
  const specs: S[] = [];
  let n = 0;
  for (let p = 0; p < projectCount; p++) {
    const product = PRODUCTS[p % PRODUCTS.length] + (p >= PRODUCTS.length ? String(Math.floor(p / PRODUCTS.length) + 1) : "");
    const pid = `Contoso.${product}.Tests`;
    projects.push({ id: pid, name: pid, path: `${root}/${pid}/${pid}.csproj`, status: "idle" });
    let count = 0;
    for (let c = 0; count < perProject; c++) {
      const area = AREAS[c % AREAS.length];
      const sub = SUBS[Math.floor(c / AREAS.length) % SUBS.length];
      const deep = c % 3 !== 0;
      const feature = FEATURES[Math.floor(c / (AREAS.length * SUBS.length)) % FEATURES.length];
      const ns = `${pid}.${area}.${sub}${deep ? `.${feature}` : ""}`;
      const cls = `${SUBJECTS[c % SUBJECTS.length]}${sub.replace(/ies$/, "y").replace(/s$/, "")}Tests${c >= 30 ? c : ""}`;
      const file = `${root}/${pid}/${area}/${sub}/${cls}.cs`;
      for (let m = 0; m < 8 && count < perProject; m++) {
        const method = `${VERBS[(m + c) % VERBS.length]}_${SUBJECTS[(m * 3 + c) % SUBJECTS.length]}_${ENDINGS[(m + p) % ENDINGS.length]}`;
        const rows = m % 3 === 0 ? 3 + Math.floor(rnd() * 6) : 1;
        for (let r = 0; r < rows && count < perProject; r++) {
          n++;
          count++;
          const x = rnd();
          const behavior: Behavior = x < 0.01 ? "fail" : x < 0.015 ? "skip" : x < 0.018 ? "flaky" : x < 0.0185 ? "timeout" : "pass";
          const tail = rnd();
          const ms: [number, number] = tail < 0.03 ? [100, 2000] : tail < 0.15 ? [20, 90] : [1, 30];
          const failing = behavior === "fail" || behavior === "flaky" || behavior === "timeout";
          specs.push(
            mk(pid, ns, cls, method, {
              args: rows > 1 ? `${r},"${SUBJECTS[r % SUBJECTS.length].toLowerCase()}-${r * 7}",${(r * 1.25).toFixed(2)}` : undefined,
              idx: r,
              file,
              line: 14 + m * 11,
              ms,
              behavior,
              traits: n % 5 === 0 ? [["TestCategory", n % 10 === 0 ? "Integration" : "Unit"]] : undefined,
              error: failing
                ? {
                    type: behavior === "timeout" ? "System.TimeoutException" : "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
                    message: behavior === "timeout" ? "Test exceeded the 5000 ms timeout." : `Assert.AreEqual failed. Expected:<${n}>. Actual:<${n + 1}>.`,
                    stack: `   at ${ns}.${cls}.${method}() in ${file}:line ${17 + m * 11}`,
                  }
                : behavior === "skip"
                  ? { type: "", message: "Ignored: pending upstream fix", stack: "" }
                  : undefined,
              stdOut: n % 50 === 0 ? `Arrange ${cls}.${method}\nAct\nAssert ok\n` : undefined,
            }),
          );
        }
      }
    }
  }
  return { projects, specs };
}
