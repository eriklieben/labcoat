using SampleTests.Lib;

namespace SampleTests.Orders;

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
        Assert.AreEqual(90m, OrderPricing.ApplyDiscount(100m, 10));
    }

    [TestMethod]
    public void Validate_RejectsEmptyCart()
    {
        var items = new List<string>();
        Assert.ThrowsExactly<InvalidOperationException>(() => OrderPricing.EnsureNotEmpty(items));
    }
}
