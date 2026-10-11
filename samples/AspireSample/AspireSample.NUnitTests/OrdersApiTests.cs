using System.Diagnostics;
using AspireSample.Api;
using Labcoat.Testing;
using NUnit.Framework.Internal;

namespace AspireSample.NUnitTests;

/// <summary>API integration tests on NUnit. Each test gets its own trace when run from Labcoat with test tracing on.</summary>
[Category("Integration")]
public class OrdersApiTests
{
    [Test]
    public async Task Get_orders_returns_seeded_orders()
    {
        using var client = AspireApp.CreateApiClient();

        var orders = await client.GetFromJsonAsync<List<Order>>("/orders", TestContext.CurrentContext.CancellationToken);

        Assert.That(orders, Is.Not.Null);
        // Live in Labcoat's detail pane while the test runs, and in the test's output afterwards.
        LabcoatOutput.WriteLine($"Received {orders!.Count} orders");
        Assert.That(orders, Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(orders.Any(o => o.Customer == "Contoso"), Is.True);
    }

    [TestCase("", "A-1", 1)]
    [TestCase("Contoso", "A-1", 0)]
    public async Task Invalid_orders_are_rejected(string customer, string sku, int quantity)
    {
        using var client = AspireApp.CreateApiClient();
        var request = new CreateOrderRequest(customer, [new OrderItem(sku, quantity, 9.99m)]);

        // The API logs a "Rejected order" warning: under Labcoat it shows up with this test's trace.
        using var response = await client.PostAsJsonAsync("/orders", request, TestContext.CurrentContext.CancellationToken);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>
    /// The NUnit + Aspire tracing check: under Labcoat with test tracing on, the test runs in its own trace, tagged with
    /// the id of its row in Labcoat (the test's full name), and the request to the api carries that trace. Outside
    /// Labcoat: nothing to check.
    /// </summary>
    [Test]
    public async Task Requests_carry_the_tests_own_trace()
    {
        if (!LabcoatTracing.IsEnabled) return;
        var root = Activity.Current;
        while (root?.Parent is not null) root = root.Parent;

        Assert.That(root, Is.Not.Null);
        Assert.That(root!.GetTagItem("test.id"), Is.EqualTo(TestContext.CurrentContext.Test.FullName));
        Assert.That(TestExecutionContext.CurrentContext.CurrentResult.Output, Does.Contain(LabcoatTracing.TraceIdMarker + root.TraceId.ToHexString()));

        using var client = AspireApp.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/1");
        using var response = await client.SendAsync(request, TestContext.CurrentContext.CancellationToken);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(request.Headers.TryGetValues("traceparent", out var traceparent), Is.True);
        Assert.That(traceparent!.Single(), Does.Contain(root.TraceId.ToHexString()));
    }
}
