using AspireSample.Api;

namespace AspireSample.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public class OrdersApiTests
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => AspireAppFixture.EnsureStartedAsync(context);

    [TestMethod]
    [TestCategory("Smoke")]
    public async Task Health_endpoint_reports_healthy()
    {
        using var client = AspireAppFixture.CreateApiClient();

        using var response = await client.GetAsync("/health", TestContext.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        TestContext.WriteLine($"GET /health -> {(int)response.StatusCode} '{body}'");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", body);
    }

    [TestMethod]
    [TestCategory("Smoke")]
    public async Task Get_orders_returns_seeded_orders()
    {
        using var client = AspireAppFixture.CreateApiClient();

        var orders = await client.GetFromJsonAsync<List<Order>>("/orders", TestContext.CancellationToken);

        Assert.IsNotNull(orders);
        Console.WriteLine($"Received {orders.Count} orders:");
        foreach (var order in orders)
        {
            Console.WriteLine($"  #{order.Id} {order.Customer} total={order.Total}");
        }

        Assert.IsGreaterThanOrEqualTo(2, orders.Count, "Expected at least the two seeded orders.");
        Assert.IsTrue(orders.Any(o => o.Customer == "Contoso"));
    }

    [TestMethod]
    public async Task Get_order_by_id_returns_order()
    {
        using var client = AspireAppFixture.CreateApiClient();

        using var response = await client.GetAsync("/orders/1", TestContext.CancellationToken);
        TestContext.WriteLine(await response.Content.ReadAsStringAsync(TestContext.CancellationToken));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<Order>(TestContext.CancellationToken);
        Assert.IsNotNull(order);
        Assert.AreEqual(1, order.Id);
        Assert.AreEqual("Contoso", order.Customer);
    }

    [TestMethod]
    public async Task Get_order_by_unknown_id_returns_404()
    {
        using var client = AspireAppFixture.CreateApiClient();

        using var response = await client.GetAsync("/orders/99999", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Post_valid_order_returns_201_and_can_be_fetched()
    {
        using var client = AspireAppFixture.CreateApiClient();
        var request = new CreateOrderRequest("Northwind", [new OrderItem("CHAI-01", 3, 4.50m)]);

        using var response = await client.PostAsJsonAsync("/orders", request, TestContext.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        TestContext.WriteLine($"POST /orders -> {(int)response.StatusCode}");
        TestContext.WriteLine(body);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.IsNotNull(response.Headers.Location);

        var created = await client.GetFromJsonAsync<Order>(response.Headers.Location, TestContext.CancellationToken);
        Assert.IsNotNull(created);
        Assert.AreEqual("Northwind", created.Customer);
        Assert.AreEqual(13.50m, created.Total);
    }
}
