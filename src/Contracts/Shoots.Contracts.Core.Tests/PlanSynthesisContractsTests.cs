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
    public void Result_serialization_matches_golden()
    {
        var result = new PlanSynthesisResult
        {
            PlanJson = "{\"steps\":[]}",
            PlanHash = "phash",
            RequestHash = "rhash",
            Stats = new PlanSynthesisStats { RetrievedHitCount = 2, StepCount = 3, ToolCount = 1 }
        };

        var json = JsonSerializer.Serialize(result, RepoSliceJson.Options);
        const string golden = "{\"planJson\":\"{\\\"steps\\\":[]}\",\"planHash\":\"phash\",\"requestHash\":\"rhash\",\"stats\":{\"retrievedHitCount\":2,\"stepCount\":3,\"toolCount\":1}}";
        Assert.Equal(golden, json);
    }
}
