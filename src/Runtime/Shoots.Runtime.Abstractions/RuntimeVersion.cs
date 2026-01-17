namespace Shoots.Runtime.Abstractions;

/// <summary>
/// Semantic version for runtime + modules. Keep it simple: Major.Minor.Patch.
/// Major changes break compatibility.
/// </summary>
public readonly record struct RuntimeVersion(int Major, int Minor, int Patch)
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public static RuntimeVersion Parse(string s)
    {
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new FormatException($"Invalid RuntimeVersion: '{s}'");
        return new RuntimeVersion(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
    }
}
