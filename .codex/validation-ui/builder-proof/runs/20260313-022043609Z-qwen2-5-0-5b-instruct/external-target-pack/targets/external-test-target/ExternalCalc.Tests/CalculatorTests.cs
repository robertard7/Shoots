using ExternalCalc;
using Xunit;

namespace ExternalCalc.Tests;

public sealed class CalculatorTests
{
    [Fact]
    public void Multiply_returns_expected_product()
    {
        Assert.Equal(12, Calculator.Multiply(3, 4));
    }
}