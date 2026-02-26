using System.Text;

namespace Shoots.Tools.Linux;

public static class LinuxToolHandlers
{
    public static bool IsPathWithin(string repoRoot, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedRoot, normalizedPath, comparison))
            return true;

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, comparison);
    }
}

public static class LinuxToolText
{
    public static string TruncateUtf8(string text, int maxBytesOut)
    {
        if (maxBytesOut <= 0 || string.IsNullOrEmpty(text))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytesOut)
            return text;

        var end = maxBytesOut;
        var runeStart = end - 1;
        while (runeStart >= 0 && IsContinuationByte(bytes[runeStart]))
            runeStart--;

        if (runeStart < 0)
            return string.Empty;

        var sequenceLength = GetSequenceLength(bytes[runeStart]);
        if (sequenceLength <= 0 || runeStart + sequenceLength > maxBytesOut)
            end = runeStart;

        return end <= 0 ? string.Empty : Encoding.UTF8.GetString(bytes, 0, end);
    }

    private static bool IsContinuationByte(byte value)
        => (value & 0b1100_0000) == 0b1000_0000;

    private static int GetSequenceLength(byte value)
    {
        if ((value & 0b1000_0000) == 0)
            return 1;

        if ((value & 0b1110_0000) == 0b1100_0000)
            return 2;

        if ((value & 0b1111_0000) == 0b1110_0000)
            return 3;

        if ((value & 0b1111_1000) == 0b1111_0000)
            return 4;

        return -1;
    }
}
