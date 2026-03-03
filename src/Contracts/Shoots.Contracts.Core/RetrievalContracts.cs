using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.Contracts.Core;

public enum RetrievalScoring
{
    LexicalTfidfV1 = 0,
    EmbeddingCosineV1 = 1
}

public sealed record RetrievalQueryRequest
{
    public string Root { get; init; } = string.Empty;
    public string QueryText { get; init; } = string.Empty;
    public RepoSliceRequest SliceRequest { get; init; } = new();
    public int MaxFiles { get; init; } = 12;
    public int MaxTotalBytes { get; init; } = 120_000;
    public int MaxFileBytes { get; init; } = 12_000;
    public RetrievalScoring Scoring { get; init; } = RetrievalScoring.LexicalTfidfV1;
    public string DeterminismSalt { get; init; } = string.Empty;

    public RetrievalQueryRequest Normalize()
    {
        var normalizedQuery = string.Join(" ", (QueryText ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        var normalizedSalt = (DeterminismSalt ?? string.Empty).Trim();
        return this with
        {
            Root = (Root ?? string.Empty).Replace('\\', '/'),
            QueryText = normalizedQuery,
            SliceRequest = (SliceRequest ?? new RepoSliceRequest()).Normalize(),
            DeterminismSalt = normalizedSalt
        };
    }

    public string ComputeQueryHash()
    {
        var payload = JsonSerializer.Serialize(Normalize(), RepoSliceJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

public sealed record RetrievalHit
{
    public string Path { get; init; } = string.Empty;
    public long Score { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();
    public string SliceRef { get; init; } = string.Empty;
    public string Excerpt { get; init; } = string.Empty;

    public RetrievalHit Normalize() => this with
    {
        Path = (Path ?? string.Empty).Replace('\\', '/'),
        ReasonCodes = ReasonCodes.OrderBy(x => x, StringComparer.Ordinal).ToArray()
    };
}

public sealed record RetrievalStats
{
    public int CandidateFiles { get; init; }
    public int ReturnedFiles { get; init; }
    public int ReturnedBytes { get; init; }
    public IReadOnlyList<string> TruncationFlags { get; init; } = Array.Empty<string>();
}

public sealed record RetrievalResult
{
    public string QueryHash { get; init; } = string.Empty;
    public string SliceHash { get; init; } = string.Empty;
    public IReadOnlyList<RetrievalHit> Hits { get; init; } = Array.Empty<RetrievalHit>();
    public RetrievalStats Stats { get; init; } = new();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public RetrievalResult Normalize() => this with
    {
        Hits = Hits.Select(h => h.Normalize()).OrderByDescending(h => h.Score).ThenBy(h => h.Path, StringComparer.Ordinal).ToArray(),
        Stats = Stats with { TruncationFlags = Stats.TruncationFlags.OrderBy(x => x, StringComparer.Ordinal).ToArray() }
    };
}
