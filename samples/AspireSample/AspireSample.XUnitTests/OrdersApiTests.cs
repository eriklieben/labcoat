using System.Diagnostics;
using AspireSample.Api;
using Labcoat.Testing;

namespace AspireSample.XUnitTests;

/// <summary>API integration tests on xUnit v3. Each test gets its own trace when run from Labcoat with test tracing on.</summary>
[Trait("Category", "Integration")]
public class OrdersApiTests(AspireAppFixture app, ITestOutputHelper output)
{
    [Fact]
    public async Task Get_orders_returns_seeded_orders()
    {
        using var client = app.CreateApiClient();

        var orders = await client.GetFromJsonAsync<List<Order>>("/orders", TestContext.Current.CancellationToken);

        Assert.NotNull(orders);
        // xUnit streams test output live (Labcoat shows it under this test while it runs).
        output.WriteLine($"Received {orders.Count} orders");
        Assert.True(orders.Count >= 2);
        Assert.Contains(orders, o => o.Customer == "Contoso");
    }

    [Theory]
    [InlineData("", "A-1", 1)]
    [InlineData("Contoso", "A-1", 0)]
    public async Task Invalid_orders_are_rejected(string customer, string sku, int quantity)
    {
        using var client = app.CreateApiClient();
        var request = new CreateOrderRequest(customer, [new OrderItem(sku, quantity, 9.99m)]);

        // The API logs a "Rejected order" warning: under Labcoat it shows up with this test's trace.
        using var response = await client.PostAsJsonAsync("/orders", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The xUnit + Aspire tracing check: under Labcoat with test tracing on, the test runs in its own trace, tagged with
    /// the id of its row in Labcoat (the test case's unique id), and the request to the api carries that trace.
    /// Outside Labcoat: nothing to check.
    /// </summary>
    [Fact]
    public async Task Requests_carry_the_tests_own_trace()
    {
        if (!LabcoatTracing.IsEnabled) return;
        var context = TestContext.Current;
        var root = Activity.Current;
        while (root?.Parent is not null) root = root.Parent;

        Assert.NotNull(root);
        Assert.Equal(context.TestCase!.UniqueID, root.GetTagItem("test.id"));
        Assert.Contains(LabcoatTracing.TraceIdMarker + root.TraceId.ToHexString(), context.TestOutputHelper!.Output);

        using var client = app.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/1");
        using var response = await client.SendAsync(request, context.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(request.Headers.TryGetValues("traceparent", out var traceparent));
        Assert.Contains(root.TraceId.ToHexString(), traceparent.Single());
    }
}
