namespace ExternalLibrary;

public static class StringJoiner
{
    public static string JoinWithDash(string left, string right)
    {
        return $"{left}-{right}";
    }
}