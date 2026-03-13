namespace RefactorProof;

public static class ProfileSummary
{
    public static string Build(string first, string last)
    {
        return NameFormatter.FormatName(first, last);
    }
}