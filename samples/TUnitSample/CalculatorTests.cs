using SampleTests.Lib;

namespace TUnitSample;

public class CalculatorTests
{
    [Test]
    public async Task Add_ReturnsSum() => await Assert.That(Calculator.Add(2, 2)).IsEqualTo(4);

    [Test]
    [Arguments(2, 3, 6)]
    [Arguments(4, 5, 20)]
    public async Task Multiply_ReturnsProduct(int a, int b, int expected) => await Assert.That(Calculator.Multiply(a, b)).IsEqualTo(expected);

    [Test]
    public async Task Divide_ByZero_Throws() => await Assert.That(() => Calculator.Divide(1, 0)).Throws<DivideByZeroException>();

    // Deliberately failing: shows a failure with expected/actual in Labcoat.
    [Test]
    public async Task ApplyDiscount_RoundsToCents() => await Assert.That(OrderPricing.ApplyDiscount(10.05m, 15)).IsEqualTo(8.55m);

    [Test]
    [Skip("Waiting for the tax rules")]
    public void ApplyDiscount_IncludesTax() { }
}
