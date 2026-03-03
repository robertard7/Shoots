using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Language;

public sealed class RetrievalService
{
    private readonly RepoSliceService _sliceService = new();
    private readonly LexicalRankerV1 _ranker = new();

    public RetrievalResult Retrieve(RetrievalQueryRequest request)
    {
        var normalized = request.Normalize();
        if (string.IsNullOrWhiteSpace(normalized.Root) || !Directory.Exists(normalized.Root))
        {
            return Error(normalized, "retrieval.root.missing", "Retrieval root does not exist.");
        }

        if (string.IsNullOrWhiteSpace(normalized.QueryText))
        {
            return Error(normalized, "retrieval.query.empty", "Query text is empty.");
        }

        var sliceRequest = normalized.SliceRequest with { Root = normalized.Root };
        var slice = _sliceService.BuildSlice(sliceRequest);
        if (!string.IsNullOrWhiteSpace(slice.ErrorCode))
        {
            return Error(normalized, $"retrieval.slice.failed:{slice.ErrorCode}", slice.ErrorMessage ?? "Slice failed.");
        }

        IReadOnlyList<RetrievalHit> hits;
        try
        {
            hits = _ranker.Rank(normalized.QueryText, slice.Files);
        }
        catch (Exception ex)
        {
            return Error(normalized, "retrieval.rank.failed", ex.Message);
        }

        var truncationFlags = new SortedSet<string>(slice.TruncationFlags, StringComparer.Ordinal);
        var selected = new List<RetrievalHit>();
        var scoringTrace = new List<RetrievalScoringTrace>();
        var bytes = 0;
        var lines = 0;
        var tokensEstimate = 0;

        foreach (var hit in hits)
        {
            if (selected.Count >= normalized.Budget.MaxFiles)
            {
                truncationFlags.Add("retrieval.budget.exceeded.max_files");
                break;
            }

            var excerpt = hit.Excerpt;
            var excerptLines = excerpt.Length == 0 ? 0 : excerpt.Count(c => c == '\n') + 1;
            if (excerptLines > normalized.MaxLinesPerFile)
            {
                excerpt = string.Join("\n", excerpt.Split('\n').Take(normalized.MaxLinesPerFile));
                excerptLines = normalized.MaxLinesPerFile;
                truncationFlags.Add("retrieval.budget.exceeded.max_lines_per_file");
            }

            if (Encoding.UTF8.GetByteCount(excerpt) > normalized.MaxFileBytes)
            {
                excerpt = Truncate(excerpt, normalized.MaxFileBytes) + "\n[TRUNCATED_BYTES]";
                excerptLines = excerpt.Count(c => c == '\n') + 1;
                truncationFlags.Add("retrieval.budget.exceeded.max_file_bytes");
            }

            var excerptBytes = Encoding.UTF8.GetByteCount(excerpt);
            if (bytes + excerptBytes > normalized.Budget.MaxBytes)
            {
                truncationFlags.Add("retrieval.budget.exceeded.max_bytes");
                break;
            }

            if (lines + excerptLines > normalized.Budget.MaxLines)
            {
                truncationFlags.Add("retrieval.budget.exceeded.max_lines");
                break;
            }

            var tokenEstimate = Math.Max(1, excerpt.Length / 4);
            if (normalized.Budget.MaxTokensEstimate is int maxTokens && tokensEstimate + tokenEstimate > maxTokens)
            {
                truncationFlags.Add("retrieval.budget.exceeded.max_tokens_estimate");
                break;
            }

            bytes += excerptBytes;
            lines += excerptLines;
            tokensEstimate += tokenEstimate;

            selected.Add(hit with { Excerpt = excerpt });
            scoringTrace.Add(new RetrievalScoringTrace
            {
                HitId = hit.HitId,
                Path = hit.Path,
                TokensMatched = hit.TokensMatched,
                Score = hit.Score,
                PathHash = hit.PathHash,
                FirstMatchOffset = hit.FirstMatchOffset
            });
        }

        var hasBudgetExceeded = truncationFlags.Any(x => x.StartsWith("retrieval.budget.exceeded", StringComparison.Ordinal));

        return new RetrievalResult
        {
            QueryHash = normalized.ComputeQueryHash(),
            SliceHash = slice.SliceId,
            Hits = selected,
            SliceDecisionTrace = slice.DecisionTrace,
            ScoringTrace = scoringTrace,
            Stats = new RetrievalStats
            {
                CandidateFiles = slice.Files.Count,
                ReturnedFiles = selected.Count,
                ReturnedBytes = bytes,
                BytesOut = bytes,
                LinesOut = lines,
                FilesOut = selected.Count,
                TruncatedFlags = truncationFlags.ToArray()
            },
            ErrorCode = hasBudgetExceeded && selected.Count == 0 ? "retrieval.budget.exceeded" : null,
            ErrorMessage = hasBudgetExceeded && selected.Count == 0 ? string.Join(",", truncationFlags.Where(x => x.StartsWith("retrieval.budget.exceeded", StringComparison.Ordinal))) : null
        }.Normalize();
    }

    public static string BuildContextPack(string runId, string planHash, RetrievalResult retrieval, ContextBudget budget)
    {
        var lines = new List<string>
        {
            "# Context Pack",
            $"runId: {runId}",
            $"planHash: {planHash}",
            $"retrievalHash: {ComputeRetrievalHash(retrieval)}",
            $"budget.maxBytes: {budget.MaxBytes}",
            $"budget.maxLines: {budget.MaxLines}",
            $"budget.maxFiles: {budget.MaxFiles}",
            $"budget.maxTokensEstimate: {(budget.MaxTokensEstimate?.ToString() ?? "none")}",
            string.Empty
        };

        foreach (var hit in retrieval.Hits)
        {
            lines.Add($"--- file: {hit.Path}");
            lines.Add($"score: {hit.Score}");
            lines.Add($"tokensMatched: {hit.TokensMatched}");
            lines.Add($"tieBreak: pathHash={hit.PathHash};offset={hit.FirstMatchOffset}");
            lines.Add($"reason: {string.Join(',', hit.ReasonCodes.OrderBy(x => x, StringComparer.Ordinal))}");
            var i = 1;
            foreach (var line in hit.Excerpt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                lines.Add($"{i:00000}: {line}");
                i++;
            }

            if (hit.Excerpt.Contains("[TRUNCATED_BYTES]", StringComparison.Ordinal))
            {
                lines.Add("TRUNCATED: max_file_bytes");
            }

            lines.Add("--- endfile");
            lines.Add(string.Empty);
        }

        return string.Join("\n", lines) + "\n";
    }

    public static string ComputeRetrievalHash(RetrievalResult retrieval)
    {
        var payload = JsonSerializer.Serialize(retrieval.Normalize(), RepoSliceJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string Truncate(string value, int maxBytes)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var b = 0;
        var selected = new List<string>();
        foreach (var line in lines)
        {
            var candidate = selected.Count == 0 ? line : "\n" + line;
            var bytes = Encoding.UTF8.GetByteCount(candidate);
            if (b + bytes > maxBytes)
            {
                break;
            }

            b += bytes;
            selected.Add(line);
        }

        return string.Join("\n", selected);
    }

    private static RetrievalResult Error(RetrievalQueryRequest req, string code, string message)
        => new()
        {
            QueryHash = req.ComputeQueryHash(),
            SliceHash = string.Empty,
            ErrorCode = code,
            ErrorMessage = message,
            Hits = Array.Empty<RetrievalHit>(),
            SliceDecisionTrace = Array.Empty<RepoSliceDecision>(),
            ScoringTrace = Array.Empty<RetrievalScoringTrace>(),
            Stats = new RetrievalStats()
        };
}
