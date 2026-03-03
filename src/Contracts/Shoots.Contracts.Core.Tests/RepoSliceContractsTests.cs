using System.Text.Json;
using Shoots.Contracts.Core;
using Xunit;

namespace Shoots.Contracts.Core.Tests;

public sealed class RepoSliceContractsTests
{
    [Fact]
    public void RepoSliceRequest_inputs_hash_is_deterministic_for_unsorted_globs()
    {
        var a = new RepoSliceRequest
        {
            Root = "repo",
            IncludeGlobs = new[] { "src/**/*.cs", "docs/*.md" },
            ExcludeGlobs = new[] { "bin/**", "obj/**" }
        };

        var b = new RepoSliceRequest
        {
            Root = "repo",
            IncludeGlobs = new[] { "docs/*.md", "src/**/*.cs" },
            ExcludeGlobs = new[] { "obj/**", "bin/**" }
        };

        Assert.Equal(a.ComputeInputsHash(), b.ComputeInputsHash());
    }

    [Fact]
    public void RepoSliceResult_normalize_sorts_files_flags_and_decisions()
    {
        var result = new RepoSliceResult
        {
            SliceId = "slice",
            InputsHash = "inputs",
            Files = new[]
            {
                new RepoSliceFile { RelPath = "z.cs", Sha256 = "2" },
                new RepoSliceFile { RelPath = "a.cs", Sha256 = "1" }
            },
            DecisionTrace = new[]
            {
                new RepoSliceDecision { Path = "z.cs" },
                new RepoSliceDecision { Path = "a.cs" }
            },
            TruncationFlags = new[]
            {
                "slice.truncated.line_cap",
                "slice.binary.disallowed"
            }
        }.Normalize();

        Assert.Equal(new[] { "a.cs", "z.cs" }, result.Files.Select(x => x.RelPath));
        Assert.Equal(new[] { "a.cs", "z.cs" }, result.DecisionTrace.Select(x => x.Path));
        Assert.Equal(new[] { "slice.binary.disallowed", "slice.truncated.line_cap" }, result.TruncationFlags);
    }

    [Fact]
    public void RepoSliceResult_serialization_contains_decision_trace()
    {
        var result = new RepoSliceResult
        {
            SliceId = "slice-1",
            InputsHash = "hash-1",
            Files =
            [
                new RepoSliceFile
                {
                    RelPath = "src/a.cs",
                    Sha256 = "abc",
                    Bytes = 10,
                    Lines = 2,
                    MimeHint = "text/x-csharp",
                    Excerpt = "class A {}",
                    Truncated = false
                }
            ],
            DecisionTrace =
            [
                new RepoSliceDecision { Path = "src/a.cs", IncludeMatch = true, Hash = "abc", Size = 10, BytesIncluded = 10, LinesIncluded = 2 }
            ],
            TruncationFlags = ["slice.truncated.line_cap"],
            Stats = new RepoSliceStats { SelectedFiles = 1, SelectedBytes = 10, TruncatedFiles = 0, RejectedBinaryFiles = 0 }
        };

        var json = JsonSerializer.Serialize(result, RepoSliceJson.Options);
        Assert.Contains("\"decisionTrace\":[", json);
    }
}
