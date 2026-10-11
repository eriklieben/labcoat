using System.Diagnostics;
using AspireSample.Api;
using Labcoat.Testing;

namespace AspireSample.TUnitTests;

/// <summary>API integration tests on TUnit. Each test gets its own trace when run from Labcoat with test tracing on.</summary>
[Category("Integration")]
[NotInParallel] // one shared app, predictable demo output
public class OrdersApiTests
{
    [Before(Class)]
    public static Task StartApp(CancellationToken ct) => AspireApp.EnsureStartedAsync(ct);

    [Test]
    public async Task Get_orders_returns_seeded_orders(CancellationToken ct)
    {
        using var client = AspireApp.CreateApiClient();

        var orders = await client.GetFromJsonAsync<List<Order>>("/orders", ct);

        await Assert.That(orders).IsNotNull();
        Console.WriteLine($"Received {orders!.Count} orders");
        await Assert.That(orders.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(orders.Any(o => o.Customer == "Contoso")).IsTrue();
    }

    [Test]
    public async Task Get_unknown_order_returns_404(CancellationToken ct)
    {
        using var client = AspireApp.CreateApiClient();

        using var response = await client.GetAsync("/orders/999999", ct);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("", "A-1", 1)]
    [Arguments("Contoso", "A-1", 0)]
    public async Task Invalid_orders_are_rejected(string customer, string sku, int quantity, CancellationToken ct)
    {
        using var client = AspireApp.CreateApiClient();
        var request = new CreateOrderRequest(customer, [new OrderItem(sku, quantity, 9.99m)]);

        // The API logs a "Rejected order" warning: under Labcoat it shows up with this test's trace.
        using var response = await client.PostAsJsonAsync("/orders", request, ct);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The TUnit + Aspire tracing check: under Labcoat with test tracing on, the test runs in its own trace, tagged
    /// with the id of its row in Labcoat, and the request to the api carries that trace. Outside Labcoat: nothing to check.
    /// </summary>
    [Test]
    public async Task Requests_carry_the_tests_own_trace(CancellationToken ct)
    {
        var context = TestContext.Current!;
        // Traced only with tracing on and when the trace filter (if any) includes this test.
        if (!LabcoatTracing.ShouldTrace(LabcoatTracingAttribute.ToLabcoatTest(context))) return;
        var root = Activity.Current;
        while (root?.Parent is not null) root = root.Parent;

        await Assert.That(root).IsNotNull();
        await Assert.That(root!.GetTagItem("test.id")).IsEqualTo(context.Metadata.TestDetails.TestId);
        await Assert.That(context.GetStandardOutput()).Contains(LabcoatTracing.TraceIdMarker + root.TraceId.ToHexString());

        using var client = AspireApp.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/1");
        using var response = await client.SendAsync(request, ct);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(request.Headers.TryGetValues("traceparent", out var traceparent)).IsTrue();
        await Assert.That(traceparent!.Single()).Contains(root.TraceId.ToHexString());
    }
}
