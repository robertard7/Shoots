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
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string TruncateUtf8(string text, int maxBytesOut)
    {
        if (maxBytesOut <= 0 || string.IsNullOrEmpty(text))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytesOut)
            return text;

        var count = maxBytesOut;
        while (count > 0)
        {
            try
            {
                return StrictUtf8.GetString(bytes, 0, count);
            }
            catch (DecoderFallbackException)
            {
                count--;
            }
        }

        return string.Empty;
    }
}
