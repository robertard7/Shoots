using Xunit;

namespace ExtensionCalc.Tests;

public sealed class CalculatorExtensionTests
{
    [Fact]
    public void Subtract_returns_expected_difference()
    {
        Assert.Equal(3, Calculator.Subtract(5, 2));
    }
}