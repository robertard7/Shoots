using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class BuildPlanHasherDecisionPolicyTests
{
    [Fact]
    public void Hash_changes_when_decision_policy_changes()
    {
        var baseRequest = TestRequestFactory.CreateBuildRequest("core.hash.policy");

        var requestHard = baseRequest with
        {
            RouteRules = new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }, DecisionPolicy.Hard),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, System.Array.Empty<string>())
            }
        };

        var requestError = requestHard with
        {
            RouteRules = new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }, DecisionPolicy.Error),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, System.Array.Empty<string>())
            }
        };

        var steps = new BuildStep[]
        {
            new RouteStep("select", "select", "select", RouteIntent.SelectTool, DecisionOwner.Ai, requestHard.WorkOrder.Id),
            new RouteStep("terminate", "terminate", "terminate", RouteIntent.Terminate, DecisionOwner.Rule, requestHard.WorkOrder.Id)
        };

        var authority = new DelegationAuthority(
            new ProviderId("fake.local"),
            ProviderKind.Local,
            "policy.v1",
            true);

        var artifacts = new[] { new BuildArtifact("plan.json", "Plan payload.") };

        var hardHash = BuildPlanHasher.ComputePlanId(requestHard, authority, steps, artifacts);
        var errorHash = BuildPlanHasher.ComputePlanId(requestError, authority, steps, artifacts);

        Assert.NotEqual(hardHash, errorHash);
    }

    [Fact]
    public void Hash_is_stable_for_equivalent_policy_data()
    {
        var baseRequest = TestRequestFactory.CreateBuildRequest("core.hash.policy");

        var requestA = baseRequest with
        {
            RouteRules = new[]
            {
                new RouteRule(
                    "select",
                    RouteIntent.SelectTool,
                    DecisionOwner.Ai,
                    "tool.selection",
                    MermaidNodeKind.Start,
                    new[] { "terminate" },
                    DecisionPolicy.Bypass,
                    new FallbackToolSelection(new ToolId("tools.echo"), new Dictionary<string, object?> { ["a"] = "1", ["b"] = "2" })),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, System.Array.Empty<string>())
            }
        };

        var requestB = baseRequest with
        {
            RouteRules = new[]
            {
                new RouteRule(
                    "select",
                    RouteIntent.SelectTool,
                    DecisionOwner.Ai,
                    "tool.selection",
                    MermaidNodeKind.Start,
                    new[] { "terminate" },
                    DecisionPolicy.Bypass,
                    new FallbackToolSelection(new ToolId("tools.echo"), new Dictionary<string, object?> { ["b"] = "2", ["a"] = "1" })),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, System.Array.Empty<string>())
            }
        };

        var steps = new BuildStep[]
        {
            new RouteStep("select", "select", "select", RouteIntent.SelectTool, DecisionOwner.Ai, requestA.WorkOrder.Id),
            new RouteStep("terminate", "terminate", "terminate", RouteIntent.Terminate, DecisionOwner.Rule, requestA.WorkOrder.Id)
        };

        var authority = new DelegationAuthority(
            new ProviderId("fake.local"),
            ProviderKind.Local,
            "policy.v1",
            true);

        var artifacts = new[] { new BuildArtifact("plan.json", "Plan payload.") };

        var hashA = BuildPlanHasher.ComputePlanId(requestA, authority, steps, artifacts);
        var hashB = BuildPlanHasher.ComputePlanId(requestB, authority, steps, artifacts);

        Assert.Equal(hashA, hashB);
    }
}
