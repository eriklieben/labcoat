// In-browser fake explorer host. Emits ServerEvents that follow docs/contract.md so the UI can be
// developed and demoed without the ASP.NET Core backend. Enable with `?mock` (or `?mock=large` for ~2000 tests).

import type {
  RunDetails,
  ClientCommand,
  CoverageReport,
  ExplorerState,
  HistoryEntry,
  LogLevel,
  Outcome,
  RunSummary,
  SlowestTestsResponse,
  SlowTest,
  ServerEvent,
  TestNode,
  TestProject,
} from "../contract";
import type { Transport, TransportHandlers } from "../transport";
import { makeCoverage, mockSource } from "./coverage";
import { TEST_SOURCES, contosoSource, lineOf, methodRange } from "./testSources";
// COVERAGE mock
import { mockCoverageLine } from "./coverage-lines";
import { scaleCatalog, type ScaleKind } from "./scale";
// Round 7: testResults patches + the snapshot file table, like the host
import { encodeUpdates, resetSent, withFileTable } from "./results";
// SNAPSHOT mock (?snapshots): Verify snapshot tests
import { snapshotSpecs } from "./snapshots";

type Behavior = "pass" | "fail" | "skip" | "timeout" | "flaky";

interface Spec {
  node: TestNode;
  behavior: Behavior;
  ms: [number, number];
  /** Baseline of the seeded history when it should differ from the middle of `ms`. */
  typicalMs?: number;
  error?: { type: string; message: string; stack: string };
  stdOut?: string;
  stdErr?: string;
}

const ROOT = "/src/demo/samples";

const srcLine = (path: string, needle: string) => {
  const text = TEST_SOURCES[path] ?? mockSource.get(path) ?? "";
  const i = text.split("\n").findIndex((l) => l.includes(needle));
  return i + 1;
};

function t(
  projectId: string,
  ns: string,
  cls: string,
  method: string,
  opts: {
    args?: string;
    file: string;
    line: number;
    traits?: [string, string][];
    behavior?: Behavior;
    ms?: [number, number];
    typicalMs?: number;
    error?: Spec["error"];
    stdOut?: string;
    stdErr?: string;
    idx?: number;
  },
): Spec {
  const fq = `${ns}.${cls}.${method}`;
  const range = methodRange(opts.file, method);
  return {
    node: {
      id: opts.args ? `${fq}#${opts.idx ?? 0}` : fq,
      projectId,
      displayName: opts.args ? `${method} (${opts.args})` : method,
      namespace: ns,
      className: cls,
      methodName: method,
      filePath: opts.file,
      line: range?.[0] ?? opts.line,
      lineEnd: range?.[1],
      traits: (opts.traits ?? []).map(([key, value]) => ({ key, value })),
      outcome: "notRun",
    },
    behavior: opts.behavior ?? "pass",
    ms: opts.ms ?? [2, 40],
    typicalMs: opts.typicalMs,
    error: opts.error,
    stdOut: opts.stdOut,
    stdErr: opts.stdErr,
  };
}

function buildCatalog(large: boolean, scale?: ScaleKind): { projects: TestProject[]; specs: Spec[] } {
  if (scale) return scaleCatalog(scale, ROOT, t);
  const calc = `${ROOT}/SampleTests/Math/CalculatorTests.cs`;
  const orders = `${ROOT}/SampleTests/Orders/OrderServiceTests.cs`;
  const slug = `${ROOT}/SampleTests/Text/SlugifyTests.cs`;
  const receipt = `${ROOT}/SampleTests/Orders/ReceiptTests.cs`;
  const receiptError = (method: string, expected: string, actual: string) => ({
    type: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
    message: `Assert.AreEqual failed. Expected:<${expected}>. Actual:<${actual}>. 'expected' expression: 'new Snapshot(expected)', 'actual' expression: 'new Snapshot(actual)'.`,
    stack: `   at SampleTests.Orders.ReceiptTests.${method}() in ${receipt}:line ${lineOf(receipt, "Assert.AreEqual(new Snapshot", method === "Render_FormatsReceipt" ? 0 : 25)}`,
  });
  const web = `${ROOT}/AspireSample.Tests/WebFrontendTests.cs`;
  const api = `${ROOT}/AspireSample.Tests/ApiServiceTests.cs`;

  const specs: Spec[] = [
    t("SampleTests", "SampleTests.Math", "CalculatorTests", "Add_ReturnsSum", { file: calc, line: 7, ms: [1, 6] }),
    t("SampleTests", "SampleTests.Math", "CalculatorTests", "Subtract_ReturnsDifference", { file: calc, line: 10, ms: [1, 6] }),
    t("SampleTests", "SampleTests.Math", "CalculatorTests", "Multiply_ReturnsProduct", { args: "2,3,6", idx: 0, file: calc, line: 16, ms: [1, 4] }),
    t("SampleTests", "SampleTests.Math", "CalculatorTests", "Multiply_ReturnsProduct", { args: "4,5,20", idx: 1, file: calc, line: 16, ms: [1, 4] }),
    t("SampleTests", "SampleTests.Math", "CalculatorTests", "Multiply_ReturnsProduct", {
      args: "3,3,10",
      idx: 2,
      file: calc,
      line: 16,
      ms: [2, 9],
      behavior: "fail",
      error: {
        type: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
        message: "Assert.AreEqual failed. Expected:<10>. Actual:<9>. \n  'expected' expression: 'expected', 'actual' expression: 'a * b'.",
        stack: `   at SampleTests.Math.CalculatorTests.Multiply_ReturnsProduct(Int32 a, Int32 b, Int32 expected) in ${calc}:line ${lineOf(calc, "Multiply_ReturnsProduct(")}\n   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)\n   at System.Reflection.MethodBaseInvoker.InvokeWithFewArgs(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)`,
      },
    }),
    t("SampleTests", "SampleTests.Math", "CalculatorTests", "Divide_ByZero_Throws", { file: calc, line: 19, ms: [1, 8] }),

    t("SampleTests", "SampleTests.Orders", "OrderServiceTests", "PlaceOrder_PersistsOrder", {
      file: orders,
      line: 10,
      traits: [["TestCategory", "Integration"]],
      ms: [600, 680],
      typicalMs: 250,
      stdOut: "Connecting to the order store...\nOrder 42 persisted\n",
    }),
    t("SampleTests", "SampleTests.Orders", "OrderServiceTests", "PlaceOrder_SendsConfirmationEmail", {
      file: orders,
      line: 19,
      traits: [["TestCategory", "Integration"]],
      ms: [900, 960],
      typicalMs: 380,
      behavior: "fail",
      error: {
        type: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
        message:
          "Assert.AreEqual failed. Expected string length 4 but was 6. 'expected' expression: '\"sent\"', 'actual' expression: '\"queued\"'. Confirmation email was not dispatched\nExpected: \"sent\"\nBut was:  \"queued\"\n-----------^",
        stack: `   at SampleTests.Orders.OrderServiceTests.PlaceOrder_SendsConfirmationEmail() in ${orders}:line ${lineOf(orders, 'Assert.AreEqual("sent"')}\n   at System.Runtime.CompilerServices.AsyncTaskMethodBuilder\`1.AsyncStateMachineBox\`1.ExecutionContextCallback(Object s)\n   at Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodInfo.InvokeAsync(Object[] arguments)`,
      },
      stdOut: "Resolving SMTP relay smtp.contoso.local:2525\nEnqueued message 7f3c2a for order 42\n",
      stdErr: "warn: Mail.Dispatcher[0] Relay responded 451 4.3.0 – deferring delivery\n",
    }),
    t("SampleTests", "SampleTests.Orders", "OrderServiceTests", "Refund_ReturnsMoney", {
      file: orders,
      line: 27,
      behavior: "skip",
      error: { type: "", message: "Payment provider sandbox is down", stack: "" },
    }),
    t("SampleTests", "SampleTests.Orders", "OrderServiceTests", "CalculateTotal_AppliesDiscount", { file: orders, line: 30 }),
    t("SampleTests", "SampleTests.Orders", "ReceiptTests", "Render_FormatsReceipt", {
      file: receipt,
      line: 12,
      behavior: "fail",
      error: receiptError(
        "Render_FormatsReceipt",
        "Order #42\n---------\n2 x Coffee      7.00\n1 x Croissant   3.50\nDiscount       -1.05\nTotal           9.45",
        "Order #42\n---------\n2 x Coffee      7.00\n1 x Croissant   3.50\nTotal          10.50",
      ),
    }),
    t("SampleTests", "SampleTests.Orders", "ReceiptTests", "Serialize_ProducesExpectedJson", {
      file: receipt,
      line: 27,
      behavior: "fail",
      error: receiptError(
        "Serialize_ProducesExpectedJson",
        '{"id":42,"status":"sent","items":[{"sku":"COF","qty":2},{"sku":"CRO","qty":1}],"total":9.45}',
        '{"id":42,"status":"queued","items":[{"sku":"COF","qty":2},{"sku":"CRO","qty":1}],"total":10.5}',
      ),
    }),
    t("SampleTests", "SampleTests.Orders", "OrderServiceTests", "Validate_RejectsEmptyCart", { file: orders, line: 37, typicalMs: 9 }),

    ...["Hello World|hello-world", "  Trim me  |trim-me", "Ünïcödé café|unicode-cafe", "C# & .NET|c-net", "already-a-slug|already-a-slug"].map(
      (pair, i) => {
        const [input, expected] = pair.split("|");
        return t("SampleTests", "SampleTests.Text", "SlugifyTests", "Slugify_ProducesUrlSafeSlug", {
          args: `"${input}","${expected}"`,
          idx: i,
          file: slug,
          line: 14,
          ms: [1, 3],
          behavior: i === 2 ? "fail" : "pass",
          error:
            i === 2
              ? {
                  type: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
                  message: "Assert.AreEqual failed. Expected:<unicode-cafe>. Actual:<ncd-caf>. String lengths differ: 12 vs 7.",
                  stack: `   at SampleTests.Text.SlugifyTests.Slugify_ProducesUrlSafeSlug(String input, String expected) in ${slug}:line ${lineOf(slug, "Assert.AreEqual(expected, slug)")}`,
                }
              : undefined,
        });
      },
    ),
    t("SampleTests", "SampleTests.Text", "SlugifyTests", "Slugify_Null_ReturnsEmpty", { file: slug, line: 21 }),

    t("AspireSample.Tests", "AspireSample.Tests", "WebFrontendTests", "GetWebResourceRootReturnsOkStatusCode", {
      file: web,
      line: 12,
      traits: [["TestCategory", "Integration"], ["Owner", "web-team"]],
      ms: [2800, 4200],
      stdOut:
        "info: Aspire.Hosting.DistributedApplication[0]\n      Aspire version: 13.6.1\ninfo: Aspire.Hosting.DistributedApplication[0]\n      Distributed application starting.\ninfo: Aspire.Hosting.DistributedApplication[0]\n      Application host directory is: /src/demo/samples/AspireSample.AppHost\n\u001b[32minfo\u001b[0m: Aspire.Hosting.DistributedApplication[0]\n      Distributed application started. Press Ctrl+C to shut down.\nGET http://localhost:51234/ -> 200 OK (184 ms)\n",
    }),
    t("AspireSample.Tests", "AspireSample.Tests", "WebFrontendTests", "WeatherPage_RendersForecastTable", {
      file: web,
      line: 31,
      traits: [["TestCategory", "Integration"], ["Owner", "web-team"]],
      ms: [900, 1500],
      behavior: "timeout",
      error: {
        type: "System.TimeoutException",
        message: "Test 'WeatherPage_RendersForecastTable' exceeded execution timeout period (00:00:30).",
        stack: "",
      },
    }),
    t("AspireSample.Tests", "AspireSample.Tests", "ApiServiceTests", "GetWeatherForecast_ReturnsFiveDays", {
      file: api,
      line: 15,
      traits: [["TestCategory", "Integration"]],
      ms: [300, 700],
    }),
    t("AspireSample.Tests", "AspireSample.Tests", "ApiServiceTests", "HealthEndpoint_ReportsHealthy", {
      file: api,
      line: 28,
      traits: [["TestCategory", "Integration"]],
      ms: [150, 300],
      behavior: "flaky",
      error: {
        type: "System.Net.Http.HttpRequestException",
        message: "Connection refused (localhost:6379)",
        stack: `   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port, HttpRequestMessage initialRequest, Boolean async, CancellationToken cancellationToken)\n   at System.Net.Http.HttpClient.<SendAsync>g__Core|83_0(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationTokenSource cts, Boolean disposeCts, CancellationTokenSource pendingRequestsCts, CancellationToken originalCancellationToken)\n   at AspireSample.Tests.ApiServiceTests.HealthEndpoint_ReportsHealthy() in ${api}:line ${lineOf(api, 'client.GetAsync("/health")')}`,
      },
      stdOut: "Waiting for resource 'cache' to become healthy...\n",
      stdErr: "\u001b[31mfail\u001b[0m: Polly[3]\n      Execution attempt. Source: 'apiservice-standard-pipeline', Result: 'Connection refused'\n",
    }),
    t("AspireSample.Tests", "AspireSample.Tests", "ApiServiceTests", "Cache_RoundTripsValue", {
      file: api,
      line: 40,
      traits: [["TestCategory", "Integration"], ["TestCategory", "Redis"]],
      ms: [400, 900],
      behavior: "fail",
      error: {
        type: "StackExchange.Redis.RedisConnectionException",
        message:
          "It was not possible to connect to the redis server(s). Error connecting right now. To allow this multiplexer to continue retrying until it's able to connect, use abortConnect=false in your connection string or AbortOnConnectFail=false; in your code.",
        stack: `   at StackExchange.Redis.ConnectionMultiplexer.ConnectImpl(ConfigurationOptions configuration, TextWriter writer, Nullable\`1 serverType, EndPointCollection endpoints)\n   at Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache.Connect()\n   at AspireSample.ApiService.CacheService.SetAsync[T](String key, T value, CancellationToken ct) in ${ROOT}/AspireSample.ApiService/CacheService.cs:line ${srcLine(`${ROOT}/AspireSample.ApiService/CacheService.cs`, "cache.SetAsync(key")}\n   at AspireSample.Tests.ApiServiceTests.Cache_RoundTripsValue() in ${api}:line ${lineOf(api, 'await cache.SetAsync("greeting"')}`,
      },
      stdErr: "\u001b[33mwarn\u001b[0m: StackExchange.Redis[0]\n      Connection to localhost:6379 refused; retrying (attempt 3 of 3)\n",
    }),
    t("AspireSample.Tests", "AspireSample.Tests", "ApiServiceTests", "Postgres_MigrationsApplied", {
      file: api,
      line: 55,
      traits: [["TestCategory", "Database"]],
      behavior: "skip",
      error: { type: "", message: "Requires Docker; set RUN_DB_TESTS=1 to enable", stack: "" },
    }),
  ];

  if (new URLSearchParams(location.search).has("snapshots")) specs.push(...snapshotSpecs(ROOT, t));

  // Timed-out test shouldn't really take 30s in a demo.
  for (const s of specs) if (s.behavior === "timeout") s.ms = [1800, 2200];

  const projects: TestProject[] = [
    { id: "SampleTests", name: "SampleTests", path: `${ROOT}/SampleTests/SampleTests.csproj`, status: "idle" },
    { id: "AspireSample.Tests", name: "AspireSample.Tests", path: `${ROOT}/AspireSample.Tests/AspireSample.Tests.csproj`, status: "idle" },
    {
      id: "Inventory.Tests",
      name: "Inventory.Tests",
      path: `${ROOT}/Inventory.Tests/Inventory.Tests.csproj`,
      status: "error",
      lastError: "Build failed with 2 error(s)",
      buildOutput: INVENTORY_BUILD_OUTPUT,
    },
  ];

  if (large) {
    const pid = "Contoso.Billing.Tests";
    projects.push({ id: pid, name: pid, path: `${ROOT}/Contoso.Billing.Tests/Contoso.Billing.Tests.csproj`, status: "idle" });
    const areas = ["Invoices", "Payments", "Tax", "Subscriptions", "Ledger", "Refunds", "Reports", "Customers"];
    const verbs = ["Create", "Update", "Delete", "Get", "List", "Validate", "Calculate", "Export", "Import", "Reconcile"];
    const subjects = ["Invoice", "LineItem", "Customer", "Currency", "Discount", "Rate", "Period", "Batch"];
    let n = 0;
    for (const area of areas) {
      for (let c = 0; c < 6; c++) {
        const cls = `${subjects[c % subjects.length]}${["Service", "Validator", "Mapper", "Repository", "Calculator", "Handler"][c]}Tests`;
        const file = `${ROOT}/Contoso.Billing.Tests/${area}/${cls}.cs`;
        for (let m = 0; m < 10; m++) {
          const method = `${verbs[m]}_${subjects[(m + c) % subjects.length]}_${["Succeeds", "ReturnsExpected", "ThrowsOnInvalid", "IsIdempotent"][m % 4]}`;
          const rows = m % 3 === 0 ? 4 : 1;
          for (let r = 0; r < rows; r++) {
            n++;
            const fail = n % 97 === 0;
            const skip = n % 131 === 0;
            specs.push(
              t(pid, `Contoso.Billing.Tests.${area}`, cls, method, {
                args: rows > 1 ? `${r},${r * 10},"EUR"` : undefined,
                idx: r,
                file,
                line: 12 + m * 9,
                ms: [1, 25],
                behavior: fail ? "fail" : skip ? "skip" : "pass",
                error: fail
                  ? {
                      type: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
                      message: `Assert.AreEqual failed. Expected:<${n}.00>. Actual:<${n}.01>.`,
                      stack: `   at Contoso.Billing.Tests.${area}.${cls}.${method}() in ${file}:line ${15 + m * 9}`,
                    }
                  : skip
                    ? { type: "", message: "Pending upstream fix", stack: "" }
                    : undefined,
              }),
            );
          }
        }
      }
    }
    // pad to ~2000 total tests
    let i = 0;
    while (specs.length < 2000) {
      const area = areas[i % areas.length];
      const file = `${ROOT}/Contoso.Billing.Tests/${area}/RoundingTests.cs`;
      specs.push(
        t(pid, `Contoso.Billing.Tests.${area}`, "RoundingTests", "Round_HalfAwayFromZero", {
          args: `${(i * 0.125).toFixed(3)}`,
          idx: i,
          file,
          line: 18,
          ms: [1, 3],
        }),
      );
      i++;
    }
  }

  return { projects, specs };
}

const INVENTORY_BUILD_OUTPUT = `  Determining projects to restore...
  All projects are up-to-date for restore.
${ROOT}/Inventory.Tests/StockLevelTests.cs(18,41): error CS1002: ; expected [${ROOT}/Inventory.Tests/Inventory.Tests.csproj]
${ROOT}/Inventory.Tests/StockLevelTests.cs(24,13): error CS0103: The name 'warehouse' does not exist in the current context [${ROOT}/Inventory.Tests/Inventory.Tests.csproj]

Build FAILED.

    0 Warning(s)
    2 Error(s)

Time Elapsed 00:00:02.41`;

const rand = (a: number, b: number) => a + Math.random() * (b - a);

const FINAL = new Set<Outcome>(["passed", "failed", "skipped", "timedOut"]);

/** Same heuristic as the host (HistoryStore.IsFlaky): ≥ 2 pass/fail transitions within the last 10 runs. */
function isFlaky(h: HistoryEntry[]) {
  let flips = 0;
  let prev: boolean | undefined;
  for (const e of h.slice(-10)) {
    const ok = e.outcome === "passed" ? true : e.outcome === "failed" || e.outcome === "timedOut" ? false : undefined;
    if (ok === undefined) continue;
    if (prev !== undefined && prev !== ok) flips++;
    prev = ok;
  }
  return flips >= 2;
}

/**
 * Pretend a previous host session ran these tests: deterministic history (12 runs, hourly) and the last result
 * marked `stale`, like the real host after a restart. The "flaky" spec alternates; "fail" specs regressed recently.
 */
function seedPreviousSession(specs: Spec[], RUNS = 12): RunDetails[] {
  let seed = 42;
  const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  const flakyPattern = "PPFPPFFPPFPF";
  const t0 = Date.now() - 26 * 3600_000;
  // The matching persisted run list (summary + per-test results), like the host's history.json.
  const runs: RunDetails[] = Array.from({ length: RUNS }, (_, i) => {
    const startedAt = new Date(t0 + i * 2 * 3600_000).toISOString();
    return { run: { runId: `prev-${i + 1}`, startedAt, total: 0, passed: 0, failed: 0, skipped: 0, scope: "all", trigger: i % 4 === 3 ? "watch" : "user" }, results: [] };
  });
  specs.forEach((s) => {
    if (s.node.projectId === "Inventory.Tests") return; // that project "fails to build"
    const h: HistoryEntry[] = [];
    for (let i = 0; i < RUNS; i++) {
      let o: Outcome = "passed";
      if (s.behavior === "skip") o = "skipped";
      else if (s.behavior === "fail") o = i >= RUNS - 4 ? "failed" : "passed";
      else if (s.behavior === "timeout") o = i >= RUNS - 2 ? "timedOut" : "passed";
      else if (s.behavior === "flaky") o = flakyPattern[i % flakyPattern.length] === "F" ? "failed" : "passed";
      const base = s.typicalMs ?? (s.ms[0] + s.ms[1]) / 2;
      const durationMs = o === "skipped" ? 0 : Math.round(base * (0.6 + rnd() * 0.8) * (i === 7 ? 1.9 : 1) * 10) / 10;
      h.push({ outcome: o, durationMs, at: new Date(t0 + i * 2 * 3600_000 + Math.round(rnd() * 600_000)).toISOString(), runId: `prev-${i + 1}` });
      const failedNow = o === "failed" || o === "timedOut";
      runs[i].results.push({
        projectId: s.node.projectId,
        id: s.node.id,
        outcome: o,
        durationMs,
        ...(failedNow && s.error ? { errorMessage: s.error.message, stackTrace: s.error.stack || undefined } : {}),
      });
    }
    const last = h[h.length - 1];
    const n = s.node;
    n.history = h;
    n.flaky = isFlaky(h) || undefined;
    n.outcome = last.outcome;
    n.durationMs = last.durationMs;
    n.lastRunAt = last.at;
    n.stale = true;
    if (last.outcome !== "passed" && s.error) {
      n.errorMessage = s.error.message;
      n.errorType = s.error.type || undefined;
      n.stackTrace = s.error.stack || undefined;
    }
  });
  for (const r of runs) {
    const c = (o: Outcome[]) => r.results.filter((x) => o.includes(x.outcome)).length;
    Object.assign(r.run, {
      total: r.results.length,
      passed: c(["passed"]),
      failed: c(["failed", "timedOut"]),
      skipped: c(["skipped"]),
      durationMs: Math.round(r.results.reduce((a, x) => a + (x.durationMs ?? 0), 0) + 1800),
    });
    r.run.finishedAt = new Date(Date.parse(r.run.startedAt) + r.run.durationMs!).toISOString();
  }
  return runs;
}
const now = () => new Date().toISOString();
const clone = <T,>(v: T): T => JSON.parse(JSON.stringify(v));

export class MockTransport implements Transport {
  readonly kind = "mock" as const;
  private h?: TransportHandlers;
  private projects: TestProject[];
  private specs: Spec[];
  private byKey = new Map<string, Spec>();
  private watch = false;
  private watchTimer?: number;
  private timers = new Set<number>();
  private run?: { summary: RunSummary; pending: Spec[]; active: Set<Spec>; projects: Set<string>; t0: number; coverage: boolean; subsetIds?: string[] };
  private runCounter = 0;
  private aspireUrl?: string;
  private inventoryFixed = false;
  private discoverAttempts = 0;
  private lastRun?: RunSummary;
  /** persisted past runs, oldest first (max 50) */
  private pastRuns: RunDetails[] = [];

  private coverageEnabled = false;
  private coverage?: CoverageReport;
  /** the full report (Coverage page), like the host's FullCoverage: only a run of all tests replaces it */
  private fullCoverage?: CoverageReport;
  /** per-test coverage cache, like the host's CoverageCache: `${projectId}|${sorted ids}` -> report */
  private testCoverage = new Map<string, CoverageReport>();
  /** ?allpass: every test passes (or is skipped), e.g. to see the all-passed celebration */
  private allPass = false;
  /** ?mock=huge|many-projects: run like a big suite (many tests in parallel, host-style coalesced testsUpdated) */
  private scale?: ScaleKind;
  /** &fast: no demo pacing, durations / 10 (soak checks) */
  private fast = false;
  /** host-style coalescing (contract P2-13): testsUpdated buffered ~50 ms, merged per test, last write wins */
  private coalesce?: { tests: Map<string, TestNode>; timer?: number };
  /** what the client has of every test (the last emitted copy): testResults patches are computed against it */
  private sent = new Map<string, TestNode>();
  /** scale scenarios: JSON characters of the coalesced test updates per event type (perf checks report bytes per run) */
  wire: Record<string, number> = {};

  constructor(opts: { large?: boolean; aspireDashboardUrl?: string; coverage?: boolean; allPass?: boolean; scale?: ScaleKind; fast?: boolean } = {}) {
    const t0 = performance.now();
    this.coverageEnabled = !!opts.coverage;
    this.allPass = !!opts.allPass;
    this.scale = opts.scale;
    this.fast = !!opts.fast;
    if (opts.scale) this.coalesce = { tests: new Map() };
    const c = buildCatalog(!!opts.large, opts.scale);
    this.projects = c.projects;
    this.specs = c.specs;
    for (const s of this.specs) this.byKey.set(`${s.node.projectId}|${s.node.id}`, s);
    // scale scenarios start with full history (20 entries per test, the host's cap) and keep per-test results only for
    // the newest runs (see keepRecentResults), so the page heap measures the UI and not the mock's run store
    this.pastRuns = seedPreviousSession(this.specs, opts.scale ? 20 : 12);
    if (opts.scale) this.keepRecentResults();
    this.aspireUrl = opts.aspireDashboardUrl;
    // Fake per-test coverage "collected earlier" for a few passing tests; the second one is stale (code changed since).
    this.specs
      .filter((x) => x.node.projectId === "SampleTests" && x.behavior === "pass")
      .slice(0, 3)
      .forEach((x, i) => {
        const r = makeCoverage(`cached-${i + 1}`, [x.node.projectId], 0.1, [x.node.id]);
        if (r) this.testCoverage.set(MockTransport.coverageKey(x.node.projectId, [x.node.id]), i === 1 ? { ...r, stale: true } : r);
      });
    // perf checks subtract the mock's own data generation from the load timings (scripts/wc/perf-scale.mjs)
    performance.measure("labcoat:mock-setup", { start: t0, end: performance.now() });
  }

  private static coverageKey(projectId: string, ids: string[]) {
    return `${projectId}|${[...new Set(ids)].sort().join("\u001e")}`;
  }

  fetchTestCoverage(projectId: string | undefined, testIds: string[], signal?: AbortSignal): Promise<CoverageReport | null> {
    return new Promise((resolve, reject) => {
      const id = window.setTimeout(() => {
        const pids = projectId ? [projectId] : this.projects.map((p) => p.id);
        for (const pid of pids) {
          const r = this.testCoverage.get(MockTransport.coverageKey(pid, testIds));
          if (r) return resolve(r);
        }
        resolve(null);
      }, 40);
      signal?.addEventListener("abort", () => {
        window.clearTimeout(id);
        reject(new DOMException("Aborted", "AbortError"));
      });
    });
  }

  // COVERAGE mock: GET /api/coverage/line
  fetchCoverageLine(path: string, line: number, signal?: AbortSignal) {
    return mockCoverageLine(path, line, this.specs.map((x) => x.node), this.testCoverage.values(), signal);
  }

  // SLOWEST mock: GET /api/tests/slowest (the host's ranking over the mock tests' history)
  fetchSlowest(projectId: string, limit: number): Promise<SlowestTestsResponse> {
    const timed = this.specs.map((x) => x.node).filter((n) => n.projectId === projectId && (n.durationMs ?? 0) > 0 && n.outcome !== "skipped");
    const median = (xs: number[]) => {
      const s = [...xs].sort((a, b) => a - b);
      return s.length ? (s.length % 2 ? s[(s.length - 1) / 2] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2) : undefined;
    };
    const tests = timed
      .sort((a, b) => b.durationMs! - a.durationMs!)
      .slice(0, limit)
      .map((n): SlowTest => {
        const trend = (n.history ?? []).filter((e) => e.outcome !== "skipped" && e.durationMs != null).map((e) => ({ at: e.at, durationMs: e.durationMs!, outcome: e.outcome }));
        const m = median((trend.at(-1)?.at === n.lastRunAt ? trend.slice(0, -1) : trend).map((p) => p.durationMs));
        return {
          projectId: n.projectId,
          id: n.id,
          name: n.displayName,
          className: n.className,
          namespace: n.namespace,
          durationMs: n.durationMs!,
          medianMs: m,
          factor: m ? Math.round((n.durationMs! / m) * 100) / 100 : undefined,
          trend,
        };
      });
    return Promise.resolve({ projectId, total: timed.length, tests });
  }

  fetchRun(runId: string): Promise<RunDetails | null> {
    const r = this.pastRuns.find((x) => x.run.runId === runId);
    return new Promise((resolve) => window.setTimeout(() => resolve(r ? clone(r) : null), 60));
  }

  private runSummaries() {
    return this.pastRuns.map((r) => r.run).reverse();
  }

  start(h: TransportHandlers) {
    this.h = h;
    h.onStatus("connecting");
    this.later(250, () => {
      h.onStatus("open");
      this.emit({ type: "state", state: this.snapshot() });
      this.log("info", "Explorer host started (mock mode). Watching 3 test projects.");
      this.log("info", "Discovered 13 tests in SampleTests", "SampleTests");
      this.log("info", "Discovered 6 tests in AspireSample.Tests", "AspireSample.Tests");
      this.log("error", "Build failed for Inventory.Tests (2 errors). See project details.", "Inventory.Tests");
    });
  }

  fetchSource(path: string): Promise<string> {
    return new Promise((resolve, reject) =>
      window.setTimeout(() => {
        const text = mockSource.get(path) ?? TEST_SOURCES[path] ?? contosoSource(path);
        if (text != null) resolve(text);
        else if (path.startsWith(ROOT)) reject(Object.assign(new Error("404 Not Found"), { status: 404 }));
        else reject(Object.assign(new Error("403 Forbidden"), { status: 403 }));
      }, 120),
    );
  }

  stop() {
    for (const id of this.timers) window.clearTimeout(id);
    this.timers.clear();
    window.clearInterval(this.watchTimer);
  }

  send(cmd: ClientCommand) {
    switch (cmd.type) {
      case "run":
        this.startRun(cmd.projectId, cmd.testIds, cmd.coverage);
        break;
      case "cancel":
        this.cancel();
        break;
      case "discover":
        this.discover(cmd.projectId);
        break;
      case "setWatch":
        this.setWatch(cmd.enabled);
        break;
      case "setCoverage":
        this.coverageEnabled = cmd.enabled;
        this.log("info", cmd.enabled ? "Code coverage enabled. Next runs add --coverage --coverage-output-format cobertura." : "Code coverage disabled.");
        this.emit({ type: "state", state: this.snapshot() });
        break;
    }
  }

  // ---------------------------------------------------------------------------------------------

  private later(ms: number, fn: () => void) {
    const id = window.setTimeout(() => {
      this.timers.delete(id);
      fn();
    }, ms);
    this.timers.add(id);
    return id;
  }

  private emit(e: ServerEvent) {
    if (e.type === "state") {
      resetSent(e.state, this.sent);
      e = { ...e, state: withFileTable(e.state) };
    }
    const q = this.coalesce;
    if (q) {
      if (e.type === "testsUpdated") {
        for (const n of e.tests) {
          const k = `${n.projectId}|${n.id}`;
          q.tests.delete(k); // re-insert: keeps "last write wins" while the Map stays cheap to iterate
          q.tests.set(k, { ...n }); // a snapshot of the node (the mock mutates nodes in place; nested values are replaced, never mutated)
        }
        q.timer ??= window.setTimeout(() => this.flushTests(), 50);
        return;
      }
      if (e.type === "state") {
        q.tests.clear();
        if (!performance.getEntriesByName("labcoat:mock-state").length) performance.mark("labcoat:mock-state");
      }
      else if (e.type !== "log") this.flushTests();
    }
    if (q && e.type === "state") {
      // scale scenarios: JSON.stringify is the host's work, JSON.parse the browser's (perf checks subtract the first)
      const t0 = performance.now();
      const json = JSON.stringify(e);
      performance.measure("labcoat:mock-serialize", { start: t0, end: performance.now() });
      this.h?.onEvent(JSON.parse(json));
      return;
    }
    if (e.type === "testsUpdated") {
      for (const ev of encodeUpdates(e.tests, this.sent)) this.h?.onEvent(clone(ev));
      return;
    }
    this.h?.onEvent(clone(e));
  }

  private flushTests() {
    const q = this.coalesce;
    if (!q) return;
    window.clearTimeout(q.timer);
    q.timer = undefined;
    if (!q.tests.size) return;
    const tests = [...q.tests.values()];
    q.tests.clear();
    // through JSON like the real transport: the UI's parse cost is part of the measurement (and the bytes are counted)
    for (const ev of encodeUpdates(tests, this.sent)) {
      const json = JSON.stringify(ev);
      this.wire[ev.type] = (this.wire[ev.type] ?? 0) + json.length;
      this.h?.onEvent(JSON.parse(json));
    }
  }

  /** Scale scenarios: per-test results of the newest 8 runs only (older ones open as summaries without results). */
  private keepRecentResults() {
    for (let i = 0; i < this.pastRuns.length - 8; i++) if (this.pastRuns[i].results.length) this.pastRuns[i] = { ...this.pastRuns[i], results: [] };
  }

  /** Soak checks: a watch-triggered partial run of `testIds` (like the host after a file change). */
  simulateWatchRun(projectId: string | undefined, testIds: string[]) {
    this.log("info", `File changed: ${ROOT}/${projectId ?? "SampleTests"}/Changed.cs`, projectId);
    this.startRun(projectId, testIds, undefined, "watch");
  }

  private log(level: LogLevel, message: string, projectId?: string) {
    this.emit({ type: "log", level, message, projectId, timestamp: now() });
  }

  private snapshot(): ExplorerState {
    return {
      projects: this.projects,
      tests: this.specs.map((s) => s.node),
      currentRun: this.run?.summary,
      lastRun: this.lastRun,
      watch: this.watch,
      aspireDashboardUrl: this.aspireUrl,
      coverageEnabled: this.coverageEnabled,
      coverage: this.coverage,
      fullCoverage: this.fullCoverage,
      runs: this.runSummaries(),
    };
  }
  private setProject(id: string, patch: Partial<TestProject>) {
    const p = this.projects.find((x) => x.id === id);
    if (!p) return;
    Object.assign(p, patch);
    if (patch.status && patch.status !== "error") {
      delete p.lastError;
      delete p.buildOutput;
    }
    this.emit({ type: "projectStatus", project: p });
  }

  private buildLogs(pid: string, done: () => void, fail = false) {
    const p = this.projects.find((x) => x.id === pid)!;
    this.setProject(pid, { status: "building" });
    const lines = [
      `dotnet build "${p.path}" -c Debug --nologo`,
      "  Determining projects to restore...",
      "  All projects are up-to-date for restore.",
    ];
    lines.forEach((l, i) => this.later(60 * i, () => this.log("info", l, pid)));
    this.later(this.fast ? 30 : 450 + Math.random() * 300, () => {
      if (fail) {
        INVENTORY_BUILD_OUTPUT.split("\n")
          .filter((l) => l.includes("error"))
          .forEach((l) => this.log("error", l.trim(), pid));
        this.log("error", "Build FAILED. 0 Warning(s), 2 Error(s)", pid);
        this.setProject(pid, { status: "error", lastError: "Build failed with 2 error(s)", buildOutput: INVENTORY_BUILD_OUTPUT });
        return;
      }
      this.log("info", `  ${p.name} -> ${p.path.replace(/[^/]+$/, "")}bin/Debug/net10.0/${p.name}.dll`, pid);
      this.log("info", "Build succeeded. 0 Warning(s), 0 Error(s)", pid);
      done();
    });
  }

  private discover(projectId?: string) {
    if (this.run) {
      this.log("warning", "Discovery requested while a run is in progress; ignoring.");
      return;
    }
    const targets = this.projects.filter((p) => !projectId || p.id === projectId);
    for (const p of targets) {
      // Second rediscover "fixes" the inventory build so the error->recovery flow can be demoed.
      if (p.id === "Inventory.Tests" && p.status === "error" && this.discoverAttempts++ > 0) this.inventoryFixed = true;
      const failBuild = p.id === "Inventory.Tests" && !this.inventoryFixed;
      this.buildLogs(
        p.id,
        () => {
          this.setProject(p.id, { status: "discovering" });
          this.later(500, () => {
            if (p.id === "Inventory.Tests" && !this.specs.some((s) => s.node.projectId === p.id)) this.addInventoryTests();
            const count = this.specs.filter((s) => s.node.projectId === p.id).length;
            this.setProject(p.id, { status: "idle" });
            this.log("info", `Discovered ${count} tests in ${p.name}`, p.id);
            this.emit({ type: "state", state: this.snapshot() });
          });
        },
        failBuild,
      );
    }
  }
  private addInventoryTests() {
    const file = `${ROOT}/Inventory.Tests/StockLevelTests.cs`;
    const add = [
      t("Inventory.Tests", "Inventory.Tests", "StockLevelTests", "Reserve_DecrementsAvailable", { file, line: 40 }),
      t("Inventory.Tests", "Inventory.Tests", "StockLevelTests", "Reserve_BeyondStock_Throws", { file, line: 52 }),
      t("Inventory.Tests", "Inventory.Tests", "StockLevelTests", "Restock_IncrementsAvailable", { file, line: 60 }),
    ];
    for (const s of add) {
      this.specs.push(s);
      this.byKey.set(`${s.node.projectId}|${s.node.id}`, s);
    }
  }

  private setWatch(enabled: boolean) {
    this.watch = enabled;
    window.clearInterval(this.watchTimer);
    this.log("info", enabled ? "Watch mode enabled. Re-running affected tests on file changes." : "Watch mode disabled.");
    this.emit({ type: "state", state: this.snapshot() });
    if (enabled) {
      // like the host's "testFile" strategy (Explorer.Warm.cs): a saved test file reruns only the tests in that file
      const tick = () => {
        if (this.run) return;
        const file = `${ROOT}/SampleTests/Math/CalculatorTests.cs`;
        const ids = this.specs.filter((s) => s.node.projectId === "SampleTests" && s.node.filePath === file).map((s) => s.node.id);
        if (!ids.length) return this.log("info", "Watch: CalculatorTests.cs changed → no affected tests (tests in the changed file)", "SampleTests");
        this.log("info", `Watch: CalculatorTests.cs changed → rerunning ${ids.length} affected test${ids.length === 1 ? "" : "s"} (tests in the changed file)`, "SampleTests");
        this.startRun("SampleTests", ids, undefined, "watch");
      };
      this.later(3000, tick);
      this.watchTimer = window.setInterval(tick, 25000);
    }
  }

  private startRun(projectId?: string, testIds?: string[], coverage?: boolean, trigger: RunSummary["trigger"] = "user") {
    if (this.run) {
      this.log("warning", "A test run is already in progress. Cancel it first.");
      return;
    }
    const runnable = new Set(this.projects.filter((p) => p.status !== "error").map((p) => p.id));
    let targets = this.specs.filter((s) => runnable.has(s.node.projectId));
    if (projectId) targets = targets.filter((s) => s.node.projectId === projectId);
    if (testIds && testIds.length) {
      const ids = new Set(testIds);
      targets = targets.filter((s) => ids.has(s.node.id));
    }
    if (!targets.length) {
      this.log("warning", "Nothing to run.");
      return;
    }
    const projects = new Set(targets.map((s) => s.node.projectId));
    const summary: RunSummary = {
      runId: `run-${++this.runCounter}`,
      startedAt: now(),
      total: targets.length,
      passed: 0,
      failed: 0,
      skipped: 0,
      scope: testIds?.length ? "subset" : "all",
      trigger,
    };
    this.run = {
      summary,
      pending: [...targets],
      active: new Set(),
      projects,
      t0: performance.now(),
      coverage: coverage ?? this.coverageEnabled,
      subsetIds: testIds?.length ? targets.map((t) => t.node.id) : undefined,
    };
    this.log("info", `Run ${summary.runId} started: ${targets.length} test(s) in ${[...projects].join(", ")}`);

    for (const s of targets) {
      s.node.outcome = "queued";
    }
    this.emit({ type: "runStarted", run: summary, testIds: targets.map((s) => s.node.id) });
    this.emit({ type: "testsUpdated", tests: targets.map((s) => s.node) });

    let built = 0;
    for (const pid of projects) {
      this.buildLogs(pid, () => {
        this.setProject(pid, { status: "running" });
        if (++built === projects.size) this.pump();
      });
    }
  }

  private pump() {
    const r = this.run;
    if (!r) return;
    const concurrency = this.scale ? 200 : r.pending.length > 200 ? 8 : 3;
    const startBatch: Spec[] = [];
    while (r.active.size < concurrency && r.pending.length) {
      const s = r.pending.shift()!;
      r.active.add(s);
      s.node.outcome = "running";
      startBatch.push(s);
      const dur = rand(s.ms[0], s.ms[1]);
      // add a little pacing so the demo is watchable
      this.later(this.fast ? dur / 10 : dur + rand(40, 180), () => this.complete(s, dur));
    }
    if (startBatch.length) this.emit({ type: "testsUpdated", tests: startBatch.map((s) => s.node) });
    if (!r.pending.length && !r.active.size) this.finish();
  }

  private complete(s: Spec, dur: number) {
    const r = this.run;
    if (!r || !r.active.has(s)) return;
    r.active.delete(s);
    const n = s.node;
    let outcome: Outcome = "passed";
    if (this.allPass) outcome = s.behavior === "skip" ? "skipped" : "passed";
    else if (s.behavior === "fail") outcome = "failed";
    else if (s.behavior === "skip") outcome = "skipped";
    else if (s.behavior === "timeout") outcome = "timedOut";
    else if (s.behavior === "flaky") outcome = Math.random() < 0.6 ? "failed" : "passed";
    n.outcome = outcome;
    n.durationMs = outcome === "skipped" ? 0 : Math.round(dur * 10) / 10;
    n.lastRunAt = now();
    n.stdOut = s.stdOut;
    n.stdErr = outcome === "passed" ? undefined : s.stdErr;
    if (outcome === "failed" || outcome === "timedOut" || outcome === "skipped") {
      n.errorMessage = s.error?.message;
      n.errorType = s.error?.type || undefined;
      n.stackTrace = s.error?.stack || undefined;
    } else {
      delete n.errorMessage;
      delete n.errorType;
      delete n.stackTrace;
    }
    if (outcome === "passed") r.summary.passed++;
    else if (outcome === "skipped") r.summary.skipped++;
    else r.summary.failed++;
    this.emit({ type: "testsUpdated", tests: [n] });
    if (outcome === "failed" || outcome === "timedOut")
      // COVERAGE mock: like the host (Round 3), test failures are not logged as errors
      this.log("info", `failed ${n.namespace}.${n.className}.${n.displayName} (${Math.round(dur)}ms)`, n.projectId);
    this.pump();
  }

  private finish(cancelled = false) {
    const r = this.run;
    if (!r) return;
    r.summary.finishedAt = now();
    r.summary.durationMs = Math.round(performance.now() - r.t0);
    this.run = undefined;
    this.lastRun = r.summary;
    for (const pid of r.projects) this.setProject(pid, { status: "idle" });
    const s = r.summary;
    // Like the host: coverageUpdated is published before runFinished.
    if (r.coverage && !cancelled) {
      const pids = [...r.projects];
      const inProjects = this.specs.filter((x) => r.projects.has(x.node.projectId)).length;
      const report = makeCoverage(s.runId, pids, inProjects ? s.total / inProjects : 1, r.subsetIds);
      if (report) {
        this.coverage = report;
        if (r.subsetIds && pids.length === 1) this.testCoverage.set(MockTransport.coverageKey(pids[0], r.subsetIds), report);
        this.log(
          "info",
          `Coverage: ${(report.lineRate * 100).toFixed(1)}% lines, ${(report.branchRate * 100).toFixed(1)}% branches (${report.files.length} files) from ${pids.join(", ")}`,
        );
        if (!r.subsetIds) {
          this.fullCoverage = report;
          this.emit({ type: "fullCoverageUpdated", coverage: report });
        }
        this.emit({ type: "coverageUpdated", coverage: report });
      } else {
        this.log("warning", `No coverage data produced for ${pids.join(", ")}.`);
      }
    }
    // Like the host: append this run's results to each test's history (testsUpdated just before runFinished).
    const recorded: TestNode[] = [];
    for (const x of this.specs) {
      const n = x.node;
      if (!r.projects.has(n.projectId) || !n.lastRunAt || n.lastRunAt < s.startedAt || !FINAL.has(n.outcome)) continue;
      n.history = [...(n.history ?? []), { outcome: n.outcome, durationMs: n.durationMs, at: n.lastRunAt, runId: s.runId }].slice(-20);
      n.flaky = isFlaky(n.history) || undefined;
      delete n.stale;
      recorded.push(n);
    }
    if (recorded.length) this.emit({ type: "testsUpdated", tests: recorded });
    if (recorded.length) {
      this.pastRuns = [
        ...this.pastRuns,
        {
          run: clone(r.summary),
          results: recorded.map((n) => ({
            projectId: n.projectId,
            id: n.id,
            outcome: n.outcome,
            durationMs: n.durationMs,
            ...(n.outcome === "failed" || n.outcome === "timedOut" ? { errorMessage: n.errorMessage?.slice(0, 2048), stackTrace: n.stackTrace?.slice(0, 4096) } : {}),
          })),
        },
      ].slice(-50);
      if (this.scale) this.keepRecentResults();
      this.emit({ type: "runsUpdated", runs: this.runSummaries() });
    }
    this.emit({ type: "runFinished", run: r.summary });
    // COVERAGE mock: failed tests are not errors (Round 3 logging)
    this.log(
      "info",
      `Run ${s.runId} ${cancelled ? "cancelled" : "finished"}: ${s.passed} passed, ${s.failed} failed, ${s.skipped} skipped in ${(s.durationMs! / 1000).toFixed(1)}s`,
    );
  }

  private cancel() {
    const r = this.run;
    if (!r) return;
    for (const id of this.timers) window.clearTimeout(id);
    this.timers.clear();
    const affected = [...r.pending, ...r.active];
    for (const s of affected) s.node.outcome = "cancelled";
    r.pending = [];
    r.active.clear();
    this.emit({ type: "testsUpdated", tests: affected.map((s) => s.node) });
    this.log("warning", `Run ${r.summary.runId} cancelled by user.`);
    this.finish(true);
  }
}

// DETAIL mock
import { installDetailMock } from "./detail";
installDetailMock(MockTransport);

// RUNS mock
import { installRunsMock } from "./runs";
installRunsMock(MockTransport);
