using SampleTests.Lib;

namespace SampleTests.Math;

[TestClass]
public sealed class CalculatorTests
{
    [TestMethod]
    public void Add_ReturnsSum() => Assert.AreEqual(4, Calculator.Add(2, 2));

    [TestMethod]
    public void Subtract_ReturnsDifference() => Assert.AreEqual(1, Calculator.Subtract(3, 2));

    [TestMethod]
    [DataRow(2, 3, 6)]
    [DataRow(4, 5, 20)]
    [DataRow(3, 3, 10)]
    public void Multiply_ReturnsProduct(int a, int b, int expected) => Assert.AreEqual(expected, Calculator.Multiply(a, b));

    [TestMethod]
    public void Divide_ByZero_Throws() => Assert.ThrowsExactly<DivideByZeroException>(() => Calculator.Divide(1, 0));
}
