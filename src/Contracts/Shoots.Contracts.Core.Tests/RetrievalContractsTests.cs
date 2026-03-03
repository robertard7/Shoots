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
    public void Retrieval_result_normalize_orders_hits_by_score_then_path()
    {
        var result = new RetrievalResult
        {
            QueryHash = "q",
            SliceHash = "s",
            Hits = new[]
            {
                new RetrievalHit { Path = "z.cs", Score = 100, ReasonCodes = new[] { "token.match" } },
                new RetrievalHit { Path = "a.cs", Score = 100, ReasonCodes = new[] { "idf.boost" } },
                new RetrievalHit { Path = "b.cs", Score = 200, ReasonCodes = new[] { "token.match" } }
            }
        }.Normalize();

        Assert.Equal(new[] { "b.cs", "a.cs", "z.cs" }, result.Hits.Select(x => x.Path));
    }

    [Fact]
    public void Retrieval_result_serialization_matches_golden()
    {
        var result = new RetrievalResult
        {
            QueryHash = "qhash",
            SliceHash = "shash",
            Hits =
            [
                new RetrievalHit
                {
                    Path = "src/A.cs",
                    Score = 1230,
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
                TruncationFlags = ["retrieval.cap.max_files"]
            }
        }.Normalize();

        var json = JsonSerializer.Serialize(result, RepoSliceJson.Options);
        const string golden = "{\"queryHash\":\"qhash\",\"sliceHash\":\"shash\",\"hits\":[{\"path\":\"src/A.cs\",\"score\":1230,\"reasonCodes\":[\"idf.boost\",\"token.match\"],\"sliceRef\":\"slice/files/src_A.cs.txt\",\"excerpt\":\"class A {}\"}],\"stats\":{\"candidateFiles\":4,\"returnedFiles\":1,\"returnedBytes\":10,\"truncationFlags\":[\"retrieval.cap.max_files\"]},\"errorCode\":null,\"errorMessage\":null}";
        Assert.Equal(golden, json);
    }
}
