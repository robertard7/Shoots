using ExtensionCalc;
using Xunit;

namespace ExtensionCalc.Tests;

public sealed class CalculatorTests
{
    [Fact]
    public void Add_returns_expected_sum()
    {
        Assert.Equal(5, Calculator.Add(2, 3));
    }
}