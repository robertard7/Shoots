namespace ExternalRelatedLibrary;

public static class PairSummary
{
    public static string Build()
    {
        return DescriptionFormatter.Describe(new NumberPair(1, 2));
    }
}