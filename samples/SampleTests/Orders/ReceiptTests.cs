namespace SampleTests.Orders;

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
