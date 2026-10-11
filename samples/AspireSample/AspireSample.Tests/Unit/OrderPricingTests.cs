using AspireSample.Api;

namespace AspireSample.Tests.Unit;

/// <summary>Fast, pure unit tests - no Aspire app is started for these.</summary>
[TestClass]
[TestCategory("Unit")]
public class OrderPricingTests
{
    [TestMethod]
    [DataRow(1, 10.00, 10.00)]
    [DataRow(3, 2.50, 7.50)]
    [DataRow(10, 0.25, 2.50)]
    public void CalculateTotal_multiplies_quantity_by_unit_price(int quantity, double unitPrice, double expected)
    {
        var total = OrderPricing.CalculateTotal([new OrderItem("SKU", quantity, (decimal)unitPrice)]);

        Assert.AreEqual((decimal)expected, total);
    }

    [TestMethod]
    public void CalculateTotal_sums_all_items()
    {
        var items = new[]
        {
            new OrderItem("A", 2, 9.99m),
            new OrderItem("B", 1, 24.50m),
        };

        Assert.AreEqual(44.48m, OrderPricing.CalculateTotal(items));
    }

    [TestMethod]
    public void CalculateTotal_of_no_items_is_zero()
    {
        Assert.AreEqual(0m, OrderPricing.CalculateTotal([]));
    }

    [TestMethod]
    public void Validate_accepts_valid_order()
    {
        var errors = OrderPricing.Validate(new CreateOrderRequest("Contoso", [new OrderItem("A", 1, 1m)]));

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_rejects_null_items()
    {
        var errors = OrderPricing.Validate(new CreateOrderRequest("Contoso", null));

        Assert.IsTrue(errors.ContainsKey("items"));
    }
}
