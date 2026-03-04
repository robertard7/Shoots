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
        var bytes = 0;
        foreach (var hit in hits)
        {
            if (selected.Count >= normalized.MaxFiles)
            {
                truncationFlags.Add("retrieval.cap.max_files");
                break;
            }

            var excerpt = hit.Excerpt;
            if (Encoding.UTF8.GetByteCount(excerpt) > normalized.MaxFileBytes)
            {
                excerpt = Truncate(excerpt, normalized.MaxFileBytes) + "\n[TRUNCATED_BYTES]";
                truncationFlags.Add("retrieval.cap.max_file_bytes");
            }

            var excerptBytes = Encoding.UTF8.GetByteCount(excerpt);
            if (bytes + excerptBytes > normalized.MaxTotalBytes)
            {
                truncationFlags.Add("retrieval.budget.exceeded");
                break;
            }

            bytes += excerptBytes;
            selected.Add(hit with { Excerpt = excerpt });
        }

        return new RetrievalResult
        {
            QueryHash = normalized.ComputeQueryHash(),
            SliceHash = slice.SliceId,
            Hits = selected,
            Stats = new RetrievalStats
            {
                CandidateFiles = slice.Files.Count,
                ReturnedFiles = selected.Count,
                ReturnedBytes = bytes,
                TruncatedFlags = truncationFlags.ToArray()
            }
        }.Normalize();
    }

    public static string BuildContextPack(string runId, string planHash, RetrievalResult retrieval, int maxTotalBytes)
    {
        var lines = new List<string>
        {
            "# Context Pack",
            $"runId: {runId}",
            $"planHash: {planHash}",
            $"retrievalHash: {ComputeRetrievalHash(retrieval)}",
            $"budget.maxTotalBytes: {maxTotalBytes}",
            string.Empty
        };

        foreach (var hit in retrieval.Hits)
        {
            lines.Add($"### file: {hit.Path}");
            lines.Add($"score: {hit.Score}");
            lines.Add($"reason: {string.Join(',', hit.ReasonCodes.OrderBy(x => x, StringComparer.Ordinal))}");
            lines.Add(hit.Excerpt);
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
            Stats = new RetrievalStats()
        };
}
