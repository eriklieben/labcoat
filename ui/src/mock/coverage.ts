// Fake product code + Cobertura-like coverage for mock mode.
import type { CoverageLineHit, CoverageReport, FileCoverage } from "../contract";

const ROOT = "/src/demo/samples";

interface SourceFile {
  module: string;
  /** test projects whose runs exercise this file */
  projects: string[];
  rel: string;
  /** substrings marking lines/blocks the tests never reach */
  uncovered: string[];
  /** substrings of branch lines where only one side is taken */
  partial: string[];
  text: string;
}

const FILES: SourceFile[] = [
  {
    module: "SampleApp.Core",
    projects: ["SampleTests"],
    rel: "SampleApp.Core/Math/Calculator.cs",
    uncovered: ["Pow(", "checked(", "OverflowException"],
    partial: ["if (b == 0)"],
    text: `namespace SampleApp.Core.Math;

public sealed class Calculator
{
    public int Add(int a, int b) => a + b;

    public int Subtract(int a, int b) => a - b;

    public int Multiply(int a, int b)
    {
        var result = a * b;
        return result;
    }

    public int Divide(int a, int b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("Cannot divide by zero.");
        }

        return a / b;
    }

    public long Pow(int value, int exponent)
    {
        long result = 1;
        for (var i = 0; i < exponent; i++)
        {
            result = checked(result * value);
        }

        return result;
    }
}
`,
  },
  {
    module: "SampleApp.Core",
    projects: ["SampleTests"],
    rel: "SampleApp.Core/Orders/OrderService.cs",
    uncovered: ["Refund", "refund", "RefundAsync", "_payments."],
    partial: ["if (order.Items.Count == 0)", "discount > 0 ?", "if (!sent)"],
    text: `using SampleApp.Core.Payments;

namespace SampleApp.Core.Orders;

public sealed class OrderService(IOrderStore store, IMailer mailer, IPaymentGateway payments)
{
    private readonly IPaymentGateway _payments = payments;

    public async Task<Order> PlaceOrderAsync(Order order, CancellationToken ct = default)
    {
        Validate(order);

        await store.SaveAsync(order, ct);
        var sent = await mailer.SendConfirmationAsync(order, ct);
        if (!sent)
        {
            order.Status = OrderStatus.ConfirmationPending;
        }

        return order;
    }

    public decimal CalculateTotal(Order order, decimal discount = 0m)
    {
        var subtotal = order.Items.Sum(i => i.Price * i.Quantity);
        return discount > 0 ? subtotal * (1 - discount) : subtotal;
    }

    public void Validate(Order order)
    {
        if (order.Items.Count == 0)
        {
            throw new InvalidOperationException("Cart is empty");
        }
    }

    public async Task<bool> RefundAsync(Order order, CancellationToken ct = default)
    {
        var refund = await _payments.RefundAsync(order.Id, order.Total, ct);
        order.Status = OrderStatus.Refunded;
        return refund.Succeeded;
    }
}
`,
  },
  {
    module: "SampleApp.Core",
    projects: ["SampleTests"],
    rel: "SampleApp.Core/Text/Slugifier.cs",
    uncovered: [],
    partial: ["char.IsLetterOrDigit(c)", "if (string.IsNullOrWhiteSpace(input))"],
    text: `using System.Globalization;
using System.Text;

namespace SampleApp.Core.Text;

public static class Slugifier
{
    public static string Slugify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = input.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var lastDash = false;

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }
}
`,
  },
  {
    module: "AspireSample.ApiService",
    projects: ["AspireSample.Tests"],
    rel: "AspireSample.ApiService/Program.cs",
    uncovered: ["UseExceptionHandler", "MapOpenApi"],
    partial: ["if (app.Environment.IsDevelopment())"],
    text: `var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisDistributedCache("cache");

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapWeatherEndpoints();
app.MapDefaultEndpoints();

app.Run();
`,
  },
  {
    module: "AspireSample.ApiService",
    projects: ["AspireSample.Tests"],
    rel: "AspireSample.ApiService/WeatherEndpoints.cs",
    uncovered: ["Results.BadRequest", "days > 14"],
    partial: ["if (days is < 1 or > 14)", "cached is not null"],
    text: `using Microsoft.Extensions.Caching.Distributed;

namespace AspireSample.ApiService;

public static class WeatherEndpoints
{
    private static readonly string[] Summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    public static IEndpointRouteBuilder MapWeatherEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/weatherforecast", async (IDistributedCache cache, int days = 5) =>
        {
            if (days is < 1 or > 14)
            {
                return Results.BadRequest("days must be between 1 and 14");
            }

            var cached = await cache.GetStringAsync($"forecast:{days}");
            var forecast = cached is not null
                ? System.Text.Json.JsonSerializer.Deserialize<WeatherForecast[]>(cached)!
                : Generate(days);

            await cache.SetStringAsync($"forecast:{days}", System.Text.Json.JsonSerializer.Serialize(forecast));
            return Results.Ok(forecast);
        });

        return app;
    }

    private static WeatherForecast[] Generate(int days) =>
        Enumerable.Range(1, days).Select(index => new WeatherForecast(
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            Summaries[Random.Shared.Next(Summaries.Length)]))
        .ToArray();
}

public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
`,
  },
  {
    module: "AspireSample.ApiService",
    projects: ["AspireSample.Tests"],
    rel: "AspireSample.ApiService/CacheService.cs",
    uncovered: ["Invalidate", "catch"],
    partial: ["if (bytes is null)"],
    text: `using Microsoft.Extensions.Caching.Distributed;

namespace AspireSample.ApiService;

public sealed class CacheService(IDistributedCache cache, ILogger<CacheService> logger)
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        try
        {
            var bytes = await cache.GetAsync(key, ct);
            if (bytes is null)
            {
                return default;
            }

            return System.Text.Json.JsonSerializer.Deserialize<T>(bytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache read failed for {Key}", key);
            return default;
        }
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default) =>
        cache.SetAsync(key, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value), ct);

    public Task Invalidate(string key, CancellationToken ct = default) =>
        cache.RemoveAsync(key, ct);
}
`,
  },
  {
    module: "AspireSample.Web",
    projects: ["AspireSample.Tests"],
    rel: "AspireSample.Web/WeatherApiClient.cs",
    uncovered: ["break;"],
    partial: ["if (forecasts?.Count >= maxItems)", "forecast is not null"],
    text: `namespace AspireSample.Web;

public class WeatherApiClient(HttpClient httpClient)
{
    public async Task<WeatherForecast[]> GetWeatherAsync(int maxItems = 10, CancellationToken cancellationToken = default)
    {
        List<WeatherForecast>? forecasts = null;

        await foreach (var forecast in httpClient.GetFromJsonAsAsyncEnumerable<WeatherForecast>("/weatherforecast", cancellationToken))
        {
            if (forecasts?.Count >= maxItems)
            {
                break;
            }
            if (forecast is not null)
            {
                forecasts ??= [];
                forecasts.Add(forecast);
            }
        }

        return forecasts?.ToArray() ?? [];
    }
}

public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
`,
  },
];

export const mockSource = new Map(FILES.map((f) => [`${ROOT}/${f.rel}`, f.text]));

function isExecutable(line: string) {
  const t = line.trim();
  if (!t || t === "{" || t === "}" || t === "[" || t === "];" || t === "else") return false;
  if (/^(using|namespace|\/\/|\[|public (sealed |static )?(class|record|interface)|private static readonly)/.test(t)) return false;
  if (/^"/.test(t)) return false;
  return true;
}

const BRANCH_RE = /\bif \(|\? |\?\?|\bforeach\b|\bfor \(|\bcatch\b| is < /;

function hash(s: string) {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
  return Math.abs(h);
}

function fileCoverage(f: SourceFile, scale: number): FileCoverage {
  const lines: CoverageLineHit[] = [];
  const src = f.text.split("\n");
  let depth = 0;
  let coldDepth = -1; // >= 0 while inside a block the tests never reach
  let coldExpr = false; // expression-bodied member: cold until the terminating ';'
  src.forEach((raw, i) => {
    const t = raw.trim();
    const depthBefore = depth;
    const marked = f.uncovered.some((u) => raw.includes(u));
    const startsBlock = (/\b(public|private)\b.*\(/.test(t) && !t.endsWith(";")) || t.startsWith("catch");
    if (marked && startsBlock && coldDepth < 0) {
      coldDepth = depthBefore;
      coldExpr = t.endsWith("=>");
    }
    for (const ch of raw) {
      if (ch === "{") depth++;
      if (ch === "}") depth--;
    }
    const cold = marked || coldDepth >= 0;
    if (coldDepth >= 0) {
      if (coldExpr ? t.endsWith(";") : depth === coldDepth && depthBefore > coldDepth) coldDepth = -1;
    }
    if (!isExecutable(raw)) return;
    const base = 1 + (hash(f.rel + i) % 24);
    const hits = cold ? 0 : Math.max(1, Math.round(base * scale));
    const hit: CoverageLineHit = { line: i + 1, hits };
    if (BRANCH_RE.test(raw)) {
      hit.branchesTotal = 2;
      hit.branchesCovered = cold ? 0 : f.partial.some((p) => raw.includes(p)) ? 1 : 2;
    }
    lines.push(hit);
  });
  const linesValid = lines.length;
  const linesCovered = lines.filter((l) => l.hits > 0).length;
  const branchesValid = lines.reduce((a, l) => a + (l.branchesTotal ?? 0), 0);
  const branchesCovered = lines.reduce((a, l) => a + (l.branchesCovered ?? 0), 0);
  return {
    path: `${ROOT}/${f.rel}`,
    relativePath: f.rel,
    module: f.module,
    linesCovered,
    linesValid,
    lineRate: linesValid ? linesCovered / linesValid : 1,
    branchesCovered,
    branchesValid,
    branchRate: branchesValid ? branchesCovered / branchesValid : 1,
    lines,
  };
}

/**
 * @param fraction share of the projects' tests that ran (single-test runs cover less — like Vitest)
 */
export function makeCoverage(runId: string, projectIds: string[], fraction: number, testIds?: string[]): CoverageReport | undefined {
  const files = FILES.filter((f) => f.projects.some((p) => projectIds.includes(p)));
  if (!files.length) return undefined;
  // Fake per-test attribution for subset runs: the selection "touches" one or two files (deterministic per selection).
  const seed = testIds?.length ? hash(testIds.join("|")) : 0;
  const touched = new Set<number>();
  if (testIds?.length) {
    touched.add(seed % files.length);
    if (testIds.length > 1 || seed % 3 === 0) touched.add((seed >> 4) % files.length);
  }
  const fc = files.map((f, i) => {
    const c = fileCoverage(f, Math.max(0.2, fraction));
    if (fraction < 0.5) {
      // narrow runs only touch part of the code: drop hits on a deterministic subset of lines
      for (const l of c.lines) {
        if (hash(f.rel + l.line + "n") % 100 > fraction * 100 + 35) {
          l.hits = 0;
          if (l.branchesTotal) l.branchesCovered = 0;
        }
      }
      c.linesCovered = c.lines.filter((l) => l.hits > 0).length;
      c.lineRate = c.linesValid ? c.linesCovered / c.linesValid : 1;
      c.branchesCovered = c.lines.reduce((a, l) => a + (l.branchesCovered ?? 0), 0);
      c.branchRate = c.branchesValid ? c.branchesCovered / c.branchesValid : 1;
    }
    if (testIds?.length && !touched.has(i)) {
      for (const l of c.lines) {
        l.hits = 0;
        if (l.branchesTotal) l.branchesCovered = 0;
      }
      c.linesCovered = 0;
      c.lineRate = 0;
      c.branchesCovered = 0;
      c.branchRate = c.branchesValid ? 0 : 1;
    }
    return c;
  });
  const sum = (k: "linesCovered" | "linesValid" | "branchesCovered" | "branchesValid") => fc.reduce((a, f) => a + f[k], 0);
  const linesCovered = sum("linesCovered");
  const linesValid = sum("linesValid");
  const branchesCovered = sum("branchesCovered");
  const branchesValid = sum("branchesValid");
  return {
    runId,
    generatedAt: new Date().toISOString(),
    projectIds,
    linesCovered,
    linesValid,
    branchesCovered,
    branchesValid,
    lineRate: linesValid ? linesCovered / linesValid : 1,
    branchRate: branchesValid ? branchesCovered / branchesValid : 1,
    files: fc,
    scope: testIds?.length ? "subset" : "all",
    ...(testIds?.length ? { testIds } : {}),
  };
}
