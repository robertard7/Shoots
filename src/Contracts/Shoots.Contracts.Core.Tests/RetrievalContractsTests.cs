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
            SliceDecisionTrace =
            [
                new RepoSliceDecision
                {
                    Path = "src/A.cs",
                    DecisionCode = "slice.include",
                    Detail = "matched include globs"
                }
            ],
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
                TruncatedFlags = ["retrieval.cap.max_files"]
            }
        }.Normalize();

        var json = JsonSerializer.Serialize(result, RepoSliceJson.Options);
        const string golden = "{\"queryHash\":\"qhash\",\"sliceHash\":\"shash\",\"hits\":[{\"hitId\":\"\",\"path\":\"src/A.cs\",\"score\":1230,\"tokensMatched\":0,\"firstMatchOffset\":0,\"pathHash\":\"\",\"reasonCodes\":[\"idf.boost\",\"token.match\"],\"sliceRef\":\"slice/files/src_A.cs.txt\",\"excerpt\":\"class A {}\"}],\"sliceDecisionTrace\":[{\"path\":\"src/A.cs\",\"decisionCode\":\"slice.include\",\"detail\":\"matched include globs\"}],\"scoringTrace\":[],\"stats\":{\"candidateFiles\":4,\"returnedFiles\":1,\"returnedBytes\":10,\"bytesOut\":0,\"linesOut\":0,\"filesOut\":0,\"truncatedFlags\":[\"retrieval.cap.max_files\"]},\"errorCode\":null,\"errorMessage\":null}";
        Assert.Equal(golden, json);
    }

    [Fact]
    public void Retrieval_result_round_trip_preserves_slice_decisions()
    {
        var payload = "{\"queryHash\":\"q\",\"sliceHash\":\"s\",\"hits\":[],\"sliceDecisionTrace\":[{\"path\":\"src/Z.cs\",\"decisionCode\":\"slice.exclude\",\"detail\":\"matched obj/**\"}],\"scoringTrace\":[],\"stats\":{\"candidateFiles\":0,\"returnedFiles\":0,\"returnedBytes\":0,\"bytesOut\":0,\"linesOut\":0,\"filesOut\":0,\"truncatedFlags\":[]},\"errorCode\":null,\"errorMessage\":null}";

        var model = JsonSerializer.Deserialize<RetrievalResult>(payload, RepoSliceJson.Options);

        Assert.NotNull(model);
        Assert.Single(model.SliceDecisionTrace);
        Assert.Equal("slice.exclude", model.SliceDecisionTrace[0].DecisionCode);
    }
}
