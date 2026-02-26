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

        var cutoff = maxBytesOut;
        while (cutoff > 0 && IsUtf8ContinuationByte(bytes[cutoff - 1]))
            cutoff--;

        if (cutoff == 0)
            return string.Empty;

        var lastRuneStart = cutoff - 1;
        var sequenceLength = GetUtf8SequenceLength(bytes[lastRuneStart]);
        var bytesRemaining = maxBytesOut - lastRuneStart;
        if (sequenceLength > bytesRemaining)
            cutoff = lastRuneStart;
        else
            cutoff = maxBytesOut;

        return cutoff <= 0 ? string.Empty : Encoding.UTF8.GetString(bytes, 0, cutoff);
    }

    private static bool IsUtf8ContinuationByte(byte b)
        => (b & 0b1100_0000) == 0b1000_0000;

    private static int GetUtf8SequenceLength(byte b)
    {
        if ((b & 0b1000_0000) == 0) return 1;
        if ((b & 0b1110_0000) == 0b1100_0000) return 2;
        if ((b & 0b1111_0000) == 0b1110_0000) return 3;
        if ((b & 0b1111_1000) == 0b1111_0000) return 4;
        return 1;
    }
}
