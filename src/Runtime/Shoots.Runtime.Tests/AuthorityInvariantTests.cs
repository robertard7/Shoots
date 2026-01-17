using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class AuthorityInvariantTests
{
    [Fact]
    public void Plan_hash_includes_authority()
    {
        var request = new BuildRequest("core.ping", new Dictionary<string, object?>());

        var localHash = BuildPlanHasher.ComputePlanId(
            request,
            new ProviderId("local"),
            ProviderKind.Local);

        var delegatedHash = BuildPlanHasher.ComputePlanId(
            request,
            new ProviderId("remote"),
            ProviderKind.Delegated);

        Assert.NotEqual(localHash, delegatedHash);
    }

    [Fact]
    public void Plan_hash_matches_authority_fields()
    {
        var services = new StubRuntimeServices();
        var policy = new DefaultDelegationPolicy();
        var planner = new DeterministicBuildPlanner(services, policy);
        var request = new BuildRequest("core.ping", new Dictionary<string, object?>());

        var plan = planner.Plan(request);
        var computed = BuildPlanHasher.ComputePlanId(
            plan.Request,
            plan.AuthorityProviderId,
            plan.AuthorityKind);

        Assert.Equal(plan.PlanId, computed);
    }

    private sealed class StubRuntimeServices : IRuntimeServices
    {
        public IReadOnlyList<RuntimeCommandSpec> GetAllCommands() => Array.Empty<RuntimeCommandSpec>();

        public RuntimeCommandSpec? GetCommand(string commandId) => null;
    }
}
