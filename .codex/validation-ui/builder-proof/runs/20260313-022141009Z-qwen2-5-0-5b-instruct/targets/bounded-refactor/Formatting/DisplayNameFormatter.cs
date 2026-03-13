namespace RefactorProof.Formatting;

public static class DisplayNameFormatter
{
    public static string Build(string first, string last)
    {
        return $"{last}, {first}";
    }
}