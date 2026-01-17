using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class AuthorityInvariantTests
{
    [Fact]
    public void Plan_hash_includes_authority()
    {
        var request = new BuildRequest("core.ping", new Dictionary<string, object?>());
        var steps = new[] { new BuildStep("s1", "step") };
        var artifacts = new[] { new BuildArtifact("a1", "artifact") };

        var localHash = BuildPlanHasher.ComputePlanId(
            request,
            new DelegationAuthority(
                new ProviderId("local"),
                ProviderKind.Local,
                "local-only",
                false),
            steps,
            artifacts);

        var delegatedHash = BuildPlanHasher.ComputePlanId(
            request,
            new DelegationAuthority(
                new ProviderId("remote"),
                ProviderKind.Delegated,
                "local-only",
                false),
            steps,
            artifacts);

        Assert.NotEqual(localHash, delegatedHash);
    }

    [Fact]
    public void Plan_hash_changes_when_policy_changes()
    {
        var request = new BuildRequest("core.ping", new Dictionary<string, object?>());
        var steps = new[] { new BuildStep("s1", "step") };
        var artifacts = new[] { new BuildArtifact("a1", "artifact") };

        var localHash = BuildPlanHasher.ComputePlanId(
            request,
            new DelegationAuthority(
                new ProviderId("local"),
                ProviderKind.Local,
                "local-only",
                false),
            steps,
            artifacts);

        var alternateHash = BuildPlanHasher.ComputePlanId(
            request,
            new DelegationAuthority(
                new ProviderId("local"),
                ProviderKind.Local,
                "alternate-policy",
                false),
            steps,
            artifacts);

        Assert.NotEqual(localHash, alternateHash);
    }

    [Fact]
    public void BuildPlan_contract_shape_is_frozen()
    {
        var props = typeof(BuildPlan).GetProperties();
        Assert.Equal(5, props.Length);
    }

}
