using System.Text.Json;
using Shoots.Contracts.Core;
using Xunit;

namespace Shoots.Contracts.Core.Tests;

public sealed class PlanSynthesisContractsTests
{
    [Fact]
    public void Request_hash_is_deterministic_for_constraint_order()
    {
        var a = new PlanSynthesisRequest
        {
            PlanKind = "builder_v1",
            RetrievalHash = "abc",
            ProviderKind = "Local",
            EnvironmentKind = "host-local",
            ProjectRoot = "project",
            Constraints = new[] { "network=off", "tests=required" }
        };

        var b = a with { Constraints = new[] { "tests=required", "network=off" } };

        Assert.Equal(a.ComputeRequestHash(), b.ComputeRequestHash());
    }


    [Fact]
    public void Request_normalize_enforces_positive_budgets()
    {
        var normalized = new PlanSynthesisRequest { MaxSteps = 0, MaxArgsBytes = -1, MaxTotalPlanBytes = 0 }.Normalize();

        Assert.Equal(1, normalized.MaxSteps);
        Assert.Equal(1, normalized.MaxArgsBytes);
        Assert.Equal(1, normalized.MaxTotalPlanBytes);
    }

    [Fact]
    public void Result_serialization_contains_evidence()
    {
        var result = new PlanSynthesisResult
        {
            PlanJson = "{\"steps\":[]}",
            PlanHash = "phash",
            RequestHash = "rhash",
            EvidenceHash = "ehash",
            Evidence = new[] { new PlanStepEvidence { StepId = "s1", HitId = "h1", Path = "a.cs", SnippetHash = "x", Range = "1-*" } },
            Stats = new PlanSynthesisStats { RetrievedHitCount = 2, StepCount = 3, ToolCount = 1 }
        };

        var json = JsonSerializer.Serialize(result, RepoSliceJson.Options);
        Assert.Contains("\"evidenceHash\":\"ehash\"", json);
        Assert.Contains("\"evidence\":[", json);
    }
}
