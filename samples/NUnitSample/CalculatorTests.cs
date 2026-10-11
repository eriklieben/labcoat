using SampleTests.Lib;

namespace NUnitSample;

[TestFixture]
public class CalculatorTests
{
    [Test]
    public void Add_ReturnsSum() => Assert.That(Calculator.Add(2, 2), Is.EqualTo(4));

    [TestCase(2, 3, 6)]
    [TestCase(4, 5, 20)]
    public void Multiply_ReturnsProduct(int a, int b, int expected) => Assert.That(Calculator.Multiply(a, b), Is.EqualTo(expected));

    [Test]
    public void Divide_ByZero_Throws() => Assert.Throws<DivideByZeroException>(() => Calculator.Divide(1, 0));

    // Deliberately failing: shows a failure with expected/actual in Labcoat.
    [Test]
    public void ApplyDiscount_RoundsToCents() => Assert.That(OrderPricing.ApplyDiscount(10.05m, 15), Is.EqualTo(8.55m));

    [Test]
    [Ignore("Waiting for the tax rules")]
    public void ApplyDiscount_IncludesTax() { }
}
