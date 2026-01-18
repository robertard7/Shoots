using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class BuildPlanHasherToolStepTests
{
    [Fact]
    public void Hash_changes_when_tool_input_changes()
    {
        var request = TestRequestFactory.CreateBuildRequest("core.ping", new Dictionary<string, object?>());
        var authority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false
        );

        var outputs = new[]
        {
            new ToolOutputSpec("summary", "string", "Summary output.")
        };

        var steps = new BuildStep[]
        {
            new ToolBuildStep(
                "tool-1",
                "Run summary tool.",
                new ToolId("tools.summary"),
                new Dictionary<string, object?> { ["input"] = "alpha" },
                outputs)
        };

        var firstHash = BuildPlanHasher.ComputePlanId(request, authority, steps, new[] { new BuildArtifact("plan.json", "Plan payload.") });

        var updatedSteps = new BuildStep[]
        {
            new ToolBuildStep(
                "tool-1",
                "Run summary tool.",
                new ToolId("tools.summary"),
                new Dictionary<string, object?> { ["input"] = "beta" },
                outputs)
        };

        var secondHash = BuildPlanHasher.ComputePlanId(request, authority, updatedSteps, new[] { new BuildArtifact("plan.json", "Plan payload.") });

        Assert.NotEqual(firstHash, secondHash);
    }

    [Fact]
    public void Hash_changes_when_tool_output_changes()
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
            new ToolBuildStep(
                "tool-1",
                "Run summary tool.",
                new ToolId("tools.summary"),
                new Dictionary<string, object?> { ["input"] = "alpha" },
                new[] { new ToolOutputSpec("summary", "string", "Summary output.") })
        };

        var firstHash = BuildPlanHasher.ComputePlanId(request, authority, steps, new[] { new BuildArtifact("plan.json", "Plan payload.") });

        var updatedSteps = new BuildStep[]
        {
            new ToolBuildStep(
                "tool-1",
                "Run summary tool.",
                new ToolId("tools.summary"),
                new Dictionary<string, object?> { ["input"] = "alpha" },
                new[] { new ToolOutputSpec("summary", "string", "Alternate output.") })
        };

        var secondHash = BuildPlanHasher.ComputePlanId(request, authority, updatedSteps, new[] { new BuildArtifact("plan.json", "Plan payload.") });

        Assert.NotEqual(firstHash, secondHash);
    }
}
