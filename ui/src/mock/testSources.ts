// Fake (but realistic) test source files served by the mock /api/source.
export const ROOT = "/src/demo/samples";

export const TEST_SOURCES: Record<string, string> = {
  [`${ROOT}/SampleTests/Math/CalculatorTests.cs`]: `namespace SampleTests.Math;

[TestClass]
public sealed class CalculatorTests
{
    [TestMethod]
    public void Add_ReturnsSum() => Assert.AreEqual(4, 2 + 2);

    [TestMethod]
    public void Subtract_ReturnsDifference() => Assert.AreEqual(1, 3 - 2);

    [TestMethod]
    [DataRow(2, 3, 6)]
    [DataRow(4, 5, 20)]
    [DataRow(3, 3, 10)]
    public void Multiply_ReturnsProduct(int a, int b, int expected) => Assert.AreEqual(expected, a * b);

    [TestMethod]
    public void Divide_ByZero_Throws()
    {
        var zero = 0;
        Assert.ThrowsExactly<DivideByZeroException>(() => _ = 1 / zero);
    }
}
`,
  [`${ROOT}/SampleTests/Orders/OrderServiceTests.cs`]: `namespace SampleTests.Orders;

[TestClass]
public sealed class OrderServiceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PlaceOrder_PersistsOrder()
    {
        TestContext.WriteLine("Connecting to the order store...");
        await Task.Delay(600, TestContext.CancellationToken);
        Console.WriteLine("Order 42 persisted");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PlaceOrder_SendsConfirmationEmail()
    {
        await Task.Delay(900, TestContext.CancellationToken);
        Assert.AreEqual("sent", "queued", "Confirmation email was not dispatched");
    }

    [TestMethod]
    [Ignore("Payment provider sandbox is down")]
    public void Refund_ReturnsMoney() { }

    [TestMethod]
    public void CalculateTotal_AppliesDiscount()
    {
        decimal total = 100m * 0.9m;
        Assert.AreEqual(90m, total);
    }

    [TestMethod]
    public void Validate_RejectsEmptyCart()
    {
        var items = new List<string>();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            if (items.Count == 0) throw new InvalidOperationException("Cart is empty");
        });
    }
}
`,
  [`${ROOT}/SampleTests/Text/SlugifyTests.cs`]: `using SampleApp.Core.Text;

namespace SampleTests.Text;

[TestClass]
public sealed class SlugifyTests
{
    [TestMethod]
    [DataRow("Hello World", "hello-world")]
    [DataRow("  Trim me  ", "trim-me")]
    [DataRow("Ünïcödé café", "unicode-cafe")]
    [DataRow("C# & .NET", "c-net")]
    [DataRow("already-a-slug", "already-a-slug")]
    public void Slugify_ProducesUrlSafeSlug(string input, string expected)
    {
        var slug = Slugifier.Slugify(input);
        Assert.AreEqual(expected, slug);
    }

    [TestMethod]
    public void Slugify_Null_ReturnsEmpty() => Assert.AreEqual(string.Empty, Slugifier.Slugify(null));
}
`,
  [`${ROOT}/AspireSample.Tests/WebFrontendTests.cs`]: `using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace AspireSample.Tests;

[TestClass]
public class WebFrontendTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task GetWebResourceRootReturnsOkStatusCode()
    {
        var cancellationToken = new CancellationTokenSource(DefaultTimeout).Token;
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AspireSample_AppHost>(cancellationToken);
        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire.", LogLevel.Debug);
        });

        await using var app = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);

        var httpClient = app.CreateHttpClient("webfrontend");
        await app.ResourceNotifications.WaitForResourceHealthyAsync("webfrontend", cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        var response = await httpClient.GetAsync("/", cancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task WeatherPage_RendersForecastTable()
    {
        await using var app = await AppFixture.StartAsync();
        var html = await app.CreateHttpClient("webfrontend").GetStringAsync("/weather");

        StringAssert.Contains(html, "<table class=\\"table\\">");
    }
}
`,
  [`${ROOT}/AspireSample.Tests/ApiServiceTests.cs`]: `using System.Net.Http.Json;

namespace AspireSample.Tests;

[TestClass]
public class ApiServiceTests
{
    private static DistributedApplication _app = null!;

    [ClassInitialize]
    public static async Task Init(TestContext _) => _app = await AppFixture.StartAsync();

    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetWeatherForecast_ReturnsFiveDays()
    {
        var client = _app.CreateHttpClient("apiservice");

        var forecast = await client.GetFromJsonAsync<WeatherForecast[]>("/weatherforecast");

        Assert.IsNotNull(forecast);
        Assert.HasCount(5, forecast);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task HealthEndpoint_ReportsHealthy()
    {
        var client = _app.CreateHttpClient("apiservice");
        Console.WriteLine("Waiting for resource 'cache' to become healthy...");

        var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Redis")]
    [Timeout(30_000)]
    public async Task Cache_RoundTripsValue()
    {
        var cache = _app.Services.GetRequiredService<CacheService>();

        await cache.SetAsync("greeting", "hello");
        var value = await cache.GetAsync<string>("greeting");

        Assert.AreEqual("hello", value);
    }

    [TestMethod]
    [TestCategory("Database")]
    [Ignore("Requires Docker; set RUN_DB_TESTS=1 to enable")]
    public async Task Postgres_MigrationsApplied()
    {
        await using var db = await _app.GetDbContextAsync("catalogdb");
        Assert.IsTrue(await db.Database.CanConnectAsync());
    }
}
`,
  [`${ROOT}/Inventory.Tests/StockLevelTests.cs`]: `namespace Inventory.Tests;

[TestClass]
public sealed class StockLevelTests
{
    private readonly Warehouse _warehouse = new("WH-1");

    [TestInitialize]
    public void Seed()
    {
        _warehouse.Receive("SKU-1", 10);
        _warehouse.Receive("SKU-2", 3);
    }

    [TestMethod]
    public void Reserve_DecrementsAvailable()
    {
        _warehouse.Reserve("SKU-1", 4);
        Assert.AreEqual(6, _warehouse.Available("SKU-1"));
    }

    [TestMethod]
    public void Reserve_BeyondStock_Throws()
    {
        Assert.ThrowsExactly<InsufficientStockException>(() => _warehouse.Reserve("SKU-2", 5));
    }

    [TestMethod]
    public void Restock_IncrementsAvailable()
    {
        _warehouse.Receive("SKU-2", 2);
        Assert.AreEqual(5, _warehouse.Available("SKU-2"));
    }
}
`,
};

/** 1-based line of the first line containing `needle`, or undefined. */
export function lineOf(path: string, needle: string, after = 0): number | undefined {
  const text = TEST_SOURCES[path];
  if (!text) return undefined;
  const lines = text.split("\n");
  for (let i = after; i < lines.length; i++) if (lines[i].includes(needle)) return i + 1;
  return undefined;
}

/** [line, lineEnd] of a method declaration (brace-matched, or single-line expression body). */
export function methodRange(path: string, method: string): [number, number] | undefined {
  const text = TEST_SOURCES[path];
  if (!text) return undefined;
  const lines = text.split("\n");
  const start = lines.findIndex((l) => new RegExp(`\\b${method}\\(`).test(l) && /\b(public|private|internal)\b/.test(l));
  if (start < 0) return undefined;
  let depth = 0;
  let opened = false;
  for (let i = start; i < lines.length; i++) {
    for (const ch of lines[i]) {
      if (ch === "{") {
        depth++;
        opened = true;
      } else if (ch === "}") depth--;
    }
    if (opened && depth === 0) return [start + 1, i + 1];
    if (!opened && lines[i].trim().endsWith(";")) return [start + 1, i + 1];
  }
  return [start + 1, start + 1];
}

/** Generated source for the synthetic Contoso.Billing project (?mock=large). */
export function contosoSource(path: string): string | undefined {
  const m = /Contoso\.Billing\.Tests\/(\w+)\/(\w+)\.cs$/.exec(path);
  if (!m) return undefined;
  const [, area, cls] = m;
  const out = [`namespace Contoso.Billing.Tests.${area};`, "", "[TestClass]", `public sealed class ${cls}`, "{", "    private readonly BillingFixture _fx = new();", "", "", ""];
  while (out.length < 200) {
    const i = out.length;
    out.push(`    // scenario ${i}`, "    [TestMethod]", `    public void Scenario_${i}()`, "    {", `        var result = _fx.Execute(${i});`, "        Assert.IsTrue(result.Succeeded);", "    }", "", "");
  }
  out.push("}");
  return out.join("\n");
}

TEST_SOURCES[`${ROOT}/SampleTests/Orders/ReceiptTests.cs`] = `namespace SampleTests.Orders;

/// <summary>Snapshot-style value: equality by content, ToString renders the content (so failures show the full text).</summary>
public sealed record Snapshot(string Text)
{
    public override string ToString() => Text;
}

[TestClass]
public sealed class ReceiptTests
{
    [TestMethod]
    public void Render_FormatsReceipt()
    {
        var expected = """
            Order #42
            ---------
            2 x Coffee      7.00
            1 x Croissant   3.50
            Discount       -1.05
            Total           9.45
            """;
        var actual = """
            Order #42
            ---------
            2 x Coffee      7.00
            1 x Croissant   3.50
            Total          10.50
            """;
        Assert.AreEqual(new Snapshot(expected), new Snapshot(actual));
    }

    [TestMethod]
    public void Serialize_ProducesExpectedJson()
    {
        var expected = """{"id":42,"status":"sent","items":[{"sku":"COF","qty":2},{"sku":"CRO","qty":1}],"total":9.45}""";
        var actual = """{"id":42,"status":"queued","items":[{"sku":"COF","qty":2},{"sku":"CRO","qty":1}],"total":10.5}""";
        Assert.AreEqual(new Snapshot(expected), new Snapshot(actual));
    }

    [TestMethod]
    public void Header_MatchesTemplate() => Assert.AreEqual("Order #42 for Contoso Coffee (paid by card)", "Order #42 for Contoso Café (paid by card)");
}
`;
