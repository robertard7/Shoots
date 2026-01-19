using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RoutingStateTests
{
    [Fact]
    public void CreateInitial_rejects_terminate_first_step()
    {
        var plan = CreatePlan(new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>()));

        Assert.Throws<ArgumentException>(() => RoutingState.CreateInitial(plan));
    }

    [Fact]
    public void CreateInitial_uses_supplied_workorder()
    {
        var plan = CreatePlan(new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }));
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-custom"),
            "Original request.",
            "Validate routing.",
            new List<string>(),
            new List<string>());

        var state = RoutingState.CreateInitial(workOrder, plan);

        Assert.Equal(workOrder.Id, state.WorkOrderId);
        Assert.Equal("validate", state.CurrentNodeId);
        Assert.Equal(RouteIntent.Validate, state.CurrentRouteIntent);
        Assert.Equal(RoutingStatus.Pending, state.Status);
    }

    [Fact]
    public void RoutingState_is_immutable()
    {
        var setters = typeof(RoutingState)
            .GetProperties()
            .Select(p => p.SetMethod)
            .Where(setter => setter is not null && setter.IsPublic)
            .ToArray();

        Assert.Empty(setters);
    }

    private static BuildPlan CreatePlan(RouteRule rule)
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-plan"),
            "Original request.",
            "Validate routing.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[] { rule });

        var steps = new List<BuildStep>
        {
            new RouteStep(
                rule.NodeId,
                $"Route {rule.NodeId}.",
                rule.NodeId,
                rule.Intent,
                rule.Owner,
                workOrder.Id)
        };

        var authority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false);

        return new BuildPlan(
            "plan",
            request,
            HashTools.ComputeSha256Hash("graph"),
            HashTools.ComputeSha256Hash("nodes"),
            HashTools.ComputeSha256Hash("edges"),
            authority,
            steps,
            new[] { new BuildArtifact("plan.json", "Plan payload.") });
    }
}
