using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class BuildPlanHasherToolResultTests
{
    [Fact]
    public void Hash_changes_when_tool_result_changes()
    {
        var request = TestRequestFactory.CreateBuildRequest("core.ping", new Dictionary<string, object?>());
        var authority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false
        );

        var steps = new BuildStep[]
        {
            new BuildStep("step-1", "Step")
        };
        var artifacts = new[] { new BuildArtifact("plan.json", "Plan payload.") };

        var firstResult = new ToolResult(
            new ToolId("tools.result"),
            new Dictionary<string, object?> { ["value"] = "alpha" },
            true);
        var secondResult = new ToolResult(
            new ToolId("tools.result"),
            new Dictionary<string, object?> { ["value"] = "beta" },
            true);

        var firstHash = BuildPlanHasher.ComputePlanId(request, authority, steps, artifacts, firstResult);
        var secondHash = BuildPlanHasher.ComputePlanId(request, authority, steps, artifacts, secondResult);

        Assert.NotEqual(firstHash, secondHash);
    }
}
