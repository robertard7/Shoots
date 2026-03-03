using System;

namespace Shoots.UI.Services.Backends;

public enum BackendKind
{
    Ollama = 0,
    Qdrant = 1
}

public sealed record BackendStatus(
    BackendKind Kind,
    bool IsAvailable,
    string? ErrorCode,
    string? Summary,
    DateTimeOffset ObservedAtUtc,
    string? Endpoint,
    string? Detail)
{
    public BackendStatus WithBounds(int maxChars = 512, int maxLines = 8)
    {
        return this with
        {
            Summary = Bound(Summary, maxChars, maxLines),
            Detail = Bound(Detail, maxChars, maxLines),
            ErrorCode = Bound(ErrorCode, 128, 1),
            Endpoint = Bound(Endpoint, 256, 1)
        };
    }

    private static string? Bound(string? value, int maxChars, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > maxLines)
        {
            lines = lines[..maxLines];
        }

        var joined = string.Join("\n", lines);
        if (joined.Length <= maxChars) return joined;
        return joined[..maxChars];
    }
}
