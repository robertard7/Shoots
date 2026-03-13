namespace RelatedLibrary.Operations;

public static class Divider
{
    public static decimal Divide(int left, int right)
    {
        return right == 0 ? 0m : decimal.Divide(left, right);
    }
}