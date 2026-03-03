using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shoots.Contracts.Core;

public enum RetrievalScoring
{
    LexicalTfidfV1 = 0,
    EmbeddingCosineV1 = 1
}

public sealed record ContextBudget
{
    public int MaxBytes { get; init; } = 120_000;
    public int MaxLines { get; init; } = 2_000;
    public int MaxFiles { get; init; } = 12;
    public int? MaxTokensEstimate { get; init; }

    public ContextBudget Normalize() => this with
    {
        MaxBytes = Math.Max(MaxBytes, 1),
        MaxLines = Math.Max(MaxLines, 1),
        MaxFiles = Math.Max(MaxFiles, 1),
        MaxTokensEstimate = MaxTokensEstimate is null ? null : Math.Max(MaxTokensEstimate.Value, 1)
    };
}

public sealed record RetrievalQueryRequest
{
    public string Root { get; init; } = string.Empty;
    public string QueryText { get; init; } = string.Empty;
    public RepoSliceRequest SliceRequest { get; init; } = new();
    public int MaxFiles { get; init; } = 12;
    public int MaxTotalBytes { get; init; } = 120_000;
    public int MaxFileBytes { get; init; } = 12_000;
    public int MaxLinesPerFile { get; init; } = 400;
    public int MaxContextBytes { get; init; } = 120_000;
    public RetrievalScoring Scoring { get; init; } = RetrievalScoring.LexicalTfidfV1;
    public string DeterminismSalt { get; init; } = string.Empty;
    public ContextBudget Budget { get; init; } = new();

    public RetrievalQueryRequest Normalize()
    {
        var normalizedQuery = string.Join(" ", (QueryText ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        var normalizedSalt = (DeterminismSalt ?? string.Empty).Trim();
        var normalizedBudget = (Budget ?? new ContextBudget
        {
            MaxBytes = MaxContextBytes,
            MaxFiles = MaxFiles
        }).Normalize();
        return this with
        {
            Root = (Root ?? string.Empty).Replace('\\', '/'),
            QueryText = normalizedQuery,
            SliceRequest = (SliceRequest ?? new RepoSliceRequest()).Normalize(),
            DeterminismSalt = normalizedSalt,
            MaxFiles = Math.Max(MaxFiles, 1),
            MaxTotalBytes = Math.Max(MaxTotalBytes, 1),
            MaxFileBytes = Math.Max(MaxFileBytes, 1),
            MaxLinesPerFile = Math.Max(MaxLinesPerFile, 1),
            MaxContextBytes = Math.Max(MaxContextBytes, 1),
            Budget = normalizedBudget
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
    public string HitId { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long Score { get; init; }
    public int TokensMatched { get; init; }
    public int FirstMatchOffset { get; init; }
    public string PathHash { get; init; } = string.Empty;
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();
    public string SliceRef { get; init; } = string.Empty;
    public string Excerpt { get; init; } = string.Empty;

    public RetrievalHit Normalize() => this with
    {
        Path = (Path ?? string.Empty).Replace('\\', '/'),
        ReasonCodes = ReasonCodes.OrderBy(x => x, StringComparer.Ordinal).ToArray()
    };
}

public sealed record RetrievalScoringTrace
{
    public string HitId { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public int TokensMatched { get; init; }
    public long Score { get; init; }
    public string PathHash { get; init; } = string.Empty;
    public int FirstMatchOffset { get; init; }
}

public sealed record RetrievalStats
{
    public int CandidateFiles { get; init; }
    public int ReturnedFiles { get; init; }
    public int ReturnedBytes { get; init; }
    public int BytesOut { get; init; }
    public int LinesOut { get; init; }
    public int FilesOut { get; init; }
    public IReadOnlyList<string> TruncatedFlags { get; init; } = Array.Empty<string>();
}

public sealed record RetrievalResult
{
    public string QueryHash { get; init; } = string.Empty;
    public string SliceHash { get; init; } = string.Empty;
    public IReadOnlyList<RetrievalHit> Hits { get; init; } = Array.Empty<RetrievalHit>();
    public IReadOnlyList<RepoSliceDecision> SliceDecisionTrace { get; init; } = Array.Empty<RepoSliceDecision>();
    public IReadOnlyList<RetrievalScoringTrace> ScoringTrace { get; init; } = Array.Empty<RetrievalScoringTrace>();
    public RetrievalStats Stats { get; init; } = new();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public RetrievalResult Normalize() => this with
    {
        Hits = Hits.Select(h => h.Normalize()).OrderByDescending(h => h.Score).ThenByDescending(h => h.TokensMatched).ThenBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.FirstMatchOffset).ToArray(),
        SliceDecisionTrace = SliceDecisionTrace.Select(x => x.Normalize()).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray(),
        ScoringTrace = ScoringTrace.OrderByDescending(x => x.Score).ThenByDescending(x => x.TokensMatched).ThenBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.FirstMatchOffset).ToArray(),
        Stats = Stats with { TruncatedFlags = Stats.TruncatedFlags.OrderBy(x => x, StringComparer.Ordinal).ToArray() }
    };
}
