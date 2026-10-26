using Labcoat;

namespace Labcoat.Tests.Unit;

/// <summary>Run in random order (RandomOrder.cs): the seeded shuffle and the order option.</summary>
[TestClass]
public class RandomOrderTests
{
    private static readonly string[] Ids = [.. Enumerable.Range(0, 20).Select(i => $"t{i:00}")];

    [TestMethod]
    public void The_same_seed_gives_the_same_order_whatever_order_the_tests_came_in()
    {
        var a = RandomOrder.Shuffle(Ids, 42, id => id);
        var b = RandomOrder.Shuffle(Ids.Reverse(), 42, id => id);
        CollectionAssert.AreEqual(a, b);
        CollectionAssert.AreNotEqual(Ids, a);
        CollectionAssert.AreEquivalent(Ids, a); // a permutation: nothing lost, nothing twice
    }

    [TestMethod]
    public void Another_seed_gives_another_order()
    {
        CollectionAssert.AreNotEqual(RandomOrder.Shuffle(Ids, 1, id => id), RandomOrder.Shuffle(Ids, 2, id => id));
    }

    [TestMethod]
    public void The_order_of_a_seed_is_pinned_so_a_seed_from_a_log_reproduces_on_any_machine()
    {
        // If this fails the algorithm changed: seeds noted in old reports no longer reproduce their order.
        var order = RandomOrder.Shuffle(Ids.Take(8), 12345, id => id);
        Assert.AreEqual("t02 t04 t01 t05 t07 t06 t03 t00", string.Join(' ', order));
    }

    [TestMethod]
    public void Empty_and_single_lists_are_fine()
    {
        Assert.IsEmpty(RandomOrder.Shuffle(Array.Empty<string>(), 1, id => id));
        CollectionAssert.AreEqual(new[] { "x" }, RandomOrder.Shuffle(new[] { "x" }, 1, id => id));
    }

    [TestMethod]
    public void New_seeds_are_positive()
    {
        for (var i = 0; i < 100; i++) Assert.IsGreaterThan(0, RandomOrder.NewSeed());
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("default", null)]
    [DataRow("random", "random")]
    [DataRow(" Random ", "random")]
    public void Order_values_parse(string? value, string? expected)
    {
        Assert.AreEqual((expected, (string?)null), RandomOrder.ParseOrder(value));
    }

    [TestMethod]
    public void An_unknown_order_is_an_error()
    {
        var (order, error) = RandomOrder.ParseOrder("alphabetical");
        Assert.IsNull(order);
        Assert.Contains("must be \"random\"", error!);
    }
}
