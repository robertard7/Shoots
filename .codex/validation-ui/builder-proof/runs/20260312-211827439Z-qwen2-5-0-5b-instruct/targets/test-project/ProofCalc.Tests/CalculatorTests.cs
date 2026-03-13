using ProofCalc;
using Xunit;

namespace ProofCalc.Tests;

public sealed class CalculatorTests
{
    [Fact]
    public void Add_returns_expected_sum()
    {
        Assert.Equal(5, Calculator.Add(2, 3));
    }
}