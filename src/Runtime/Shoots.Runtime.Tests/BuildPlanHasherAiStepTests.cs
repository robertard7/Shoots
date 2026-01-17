using System.Collections.Generic;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class BuildPlanHasherAiStepTests
{
    [Fact]
    public void Hash_changes_when_ai_prompt_changes()
    {
        var request = new BuildRequest("core.ping", new Dictionary<string, object?>());
        var authority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false
        );

        var steps = new BuildStep[]
        {
            new AiBuildStep("ai-1", "Request AI output.", "prompt-a", "{\"type\":\"object\"}")
        };
        var artifacts = new[] { new BuildArtifact("plan.json", "Plan payload.") };

        var firstHash = BuildPlanHasher.ComputePlanId(request, authority, steps, artifacts);

        var updatedSteps = new BuildStep[]
        {
            new AiBuildStep("ai-1", "Request AI output.", "prompt-b", "{\"type\":\"object\"}")
        };
        var secondHash = BuildPlanHasher.ComputePlanId(request, authority, updatedSteps, artifacts);

        Assert.NotEqual(firstHash, secondHash);
    }

    [Fact]
    public void Hash_changes_when_ai_schema_changes()
    {
        var request = new BuildRequest("core.ping", new Dictionary<string, object?>());
        var authority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false
        );

        var steps = new BuildStep[]
        {
            new AiBuildStep("ai-1", "Request AI output.", "prompt-a", "{\"type\":\"object\"}")
        };
        var artifacts = new[] { new BuildArtifact("plan.json", "Plan payload.") };

        var firstHash = BuildPlanHasher.ComputePlanId(request, authority, steps, artifacts);

        var updatedSteps = new BuildStep[]
        {
            new AiBuildStep("ai-1", "Request AI output.", "prompt-a", "{\"type\":\"array\"}")
        };
        var secondHash = BuildPlanHasher.ComputePlanId(request, authority, updatedSteps, artifacts);

        Assert.NotEqual(firstHash, secondHash);
    }
}
