using RelatedLibrary.Operations;

namespace RelatedLibrary;

public static class NumberSummary
{
    public static string Describe(int left, int right)
    {
        return $"sum={Adder.Add(left, right)}; ratio={Divider.Divide(left, right)}";
    }
}