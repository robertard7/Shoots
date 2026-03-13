namespace ExternalRelatedLibrary;

public static class DescriptionFormatter
{
    public static string Describe(NumberPair pair)
    {
        return $"{pair.Left}+{pair.Right}={pair.Left + pair.Right}";
    }
}