#nullable enable
using System.Security.Cryptography;
using System.Text;

namespace Shoots.Ui.Tests;

internal static class JobSpecDigestBuilder
{
    public static string Build(string intent, string target, string attachments, string stack)
    {
        var normalized =
            $"{(intent ?? string.Empty).Trim()}|{(target ?? string.Empty).Trim()}|{(attachments ?? string.Empty).Trim()}|{(stack ?? string.Empty).Trim()}";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}