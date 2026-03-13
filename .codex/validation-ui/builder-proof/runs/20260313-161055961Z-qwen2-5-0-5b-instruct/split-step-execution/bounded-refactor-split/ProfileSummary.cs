using RefactorProof.Formatting;

namespace RefactorProof;

public static class ProfileSummary
{
    public static string Build(string first, string last)
    {
        return DisplayNameFormatter.Build(first, last);
    }
}