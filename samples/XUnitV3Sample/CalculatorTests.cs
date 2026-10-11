using SampleTests.Lib;

namespace XUnitV3Sample;

public class CalculatorTests
{
    [Fact]
    public void Add_ReturnsSum() => Assert.Equal(4, Calculator.Add(2, 2));

    [Theory]
    [InlineData(2, 3, 6)]
    [InlineData(4, 5, 20)]
    public void Multiply_ReturnsProduct(int a, int b, int expected) => Assert.Equal(expected, Calculator.Multiply(a, b));

    [Fact]
    public void Divide_ByZero_Throws() => Assert.Throws<DivideByZeroException>(() => Calculator.Divide(1, 0));

    // Deliberately failing: shows a failure with expected/actual in Labcoat.
    [Fact]
    public void ApplyDiscount_RoundsToCents() => Assert.Equal(8.55m, OrderPricing.ApplyDiscount(10.05m, 15));

    [Fact(Skip = "Waiting for the tax rules")]
    public void ApplyDiscount_IncludesTax() { }
}
