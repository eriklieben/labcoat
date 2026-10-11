using AspireSample.Api;

namespace AspireSample.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public class OrderValidationTests
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => AspireAppFixture.EnsureStartedAsync(context);

    [TestMethod]
    [TestCategory("Validation")]
    [DataRow("Contoso", "WIDGET-1", 0, 1.00, DisplayName = "Zero quantity")]
    [DataRow("Contoso", "WIDGET-1", -5, 1.00, DisplayName = "Negative quantity")]
    [DataRow("Contoso", "", 1, 1.00, DisplayName = "Missing SKU")]
    [DataRow("Contoso", "WIDGET-1", 1, -2.50, DisplayName = "Negative unit price")]
    [DataRow("", "WIDGET-1", 1, 1.00, DisplayName = "Missing customer")]
    public async Task Post_invalid_item_returns_400(string customer, string sku, int quantity, double unitPrice)
    {
        using var client = AspireAppFixture.CreateApiClient();
        var request = new CreateOrderRequest(customer, [new OrderItem(sku, quantity, (decimal)unitPrice)]);

        using var response = await client.PostAsJsonAsync("/orders", request, TestContext.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        TestContext.WriteLine($"POST /orders -> {(int)response.StatusCode}: {body}");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(body, "errors");
    }

    [TestMethod]
    [TestCategory("Validation")]
    public async Task Post_order_without_items_returns_400()
    {
        using var client = AspireAppFixture.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/orders", new CreateOrderRequest("Contoso", []), TestContext.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        Console.WriteLine(body);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(body, "at least one item");
    }

    /// <summary>
    /// INTENTIONALLY FAILING (demo only): shows how a failed assertion looks in Labcoat.
    /// The API correctly returns 400 Bad Request for an empty order, but this test wrongly expects 201 Created.
    /// </summary>
    [TestMethod]
    [TestCategory("Demo")]
    public async Task Demo_intentionally_failing_expects_wrong_status_code()
    {
        using var client = AspireAppFixture.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/orders", new CreateOrderRequest("Contoso", []), TestContext.CancellationToken);
        TestContext.WriteLine($"Actual status: {(int)response.StatusCode} {response.StatusCode} (this test is meant to fail)");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, "Intentionally failing demo test: the API returns 400 for an empty order.");
    }

    [TestMethod]
    [Ignore("Pagination is not implemented in the orders API yet.")]
    public async Task Get_orders_supports_pagination()
    {
        using var client = AspireAppFixture.CreateApiClient();

        var orders = await client.GetFromJsonAsync<List<Order>>("/orders?page=1&pageSize=1", TestContext.CancellationToken);

        Assert.AreEqual(1, orders?.Count);
    }
}
