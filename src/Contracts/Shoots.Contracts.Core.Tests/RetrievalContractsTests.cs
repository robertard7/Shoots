using System.Text.Json;
using Shoots.Contracts.Core;
using Xunit;

namespace Shoots.Contracts.Core.Tests;

public sealed class RetrievalContractsTests
{
    [Fact]
    public void Query_hash_is_deterministic_for_whitespace_variants()
    {
        var a = new RetrievalQueryRequest
        {
            Root = "repo",
            QueryText = "find   backend\n status",
            SliceRequest = new RepoSliceRequest { Root = "repo", IncludeGlobs = new[] { "src/**/*.cs" } }
        };

        var b = new RetrievalQueryRequest
        {
            Root = "repo",
            QueryText = "find backend status",
            SliceRequest = new RepoSliceRequest { Root = "repo", IncludeGlobs = new[] { "src/**/*.cs" } }
        };

        Assert.Equal(a.ComputeQueryHash(), b.ComputeQueryHash());
    }

    [Fact]
    public void Retrieval_result_normalize_orders_hits_by_score_then_ties()
    {
        var result = new RetrievalResult
        {
            QueryHash = "q",
            SliceHash = "s",
            Hits = new[]
            {
                new RetrievalHit { HitId = "z", Path = "z.cs", Score = 100, TokensMatched = 1, FirstMatchOffset = 1, ReasonCodes = new[] { "token.match" } },
                new RetrievalHit { HitId = "a", Path = "a.cs", Score = 100, TokensMatched = 2, FirstMatchOffset = 5, ReasonCodes = new[] { "idf.boost" } },
                new RetrievalHit { HitId = "b", Path = "b.cs", Score = 200, TokensMatched = 1, FirstMatchOffset = 3, ReasonCodes = new[] { "token.match" } }
            }
        }.Normalize();

        Assert.Equal(new[] { "b.cs", "a.cs", "z.cs" }, result.Hits.Select(x => x.Path));
    }

    [Fact]
    public void Retrieval_result_serialization_contains_stats_shape()
    {
        var result = new RetrievalResult
        {
            QueryHash = "qhash",
            SliceHash = "shash",
            Hits =
            [
                new RetrievalHit
                {
                    HitId = "h1",
                    Path = "src/A.cs",
                    Score = 1230,
                    TokensMatched = 2,
                    FirstMatchOffset = 9,
                    PathHash = "abc",
                    ReasonCodes = ["idf.boost", "token.match"],
                    SliceRef = "slice/files/src_A.cs.txt",
                    Excerpt = "class A {}"
                }
            ],
            Stats = new RetrievalStats
            {
                CandidateFiles = 4,
                ReturnedFiles = 1,
                ReturnedBytes = 10,
                BytesOut = 10,
                LinesOut = 1,
                FilesOut = 1,
                TruncatedFlags = ["retrieval.budget.exceeded.max_files"]
            }
        }.Normalize();

        var json = JsonSerializer.Serialize(result, RepoSliceJson.Options);
        Assert.Contains("\"bytesOut\":10", json);
        Assert.Contains("\"filesOut\":1", json);
    }
}
