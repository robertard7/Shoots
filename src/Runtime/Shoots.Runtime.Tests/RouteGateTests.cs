using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RouteGateTests
{
    [Fact]
    public void TryAdvance_halts_on_workorder_mismatch()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation"),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination")
            });

        var state = new RoutingState(
            new WorkOrderId("wo-other"),
            0,
            RouteIntent.Validate,
            RoutingStatus.Pending);

        var result = RouteGate.TryAdvance(plan, state, null, new StubToolRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("route_workorder_mismatch", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    [Fact]
    public void TryAdvance_waits_when_select_tool_decision_missing()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection"),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination")
            });

        var state = RoutingState.CreateInitial(plan);

        var result = RouteGate.TryAdvance(plan, state, null, new StubToolRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.Null(error);
        Assert.Equal(RoutingStatus.Waiting, nextState.Status);
        Assert.Equal(state.CurrentRouteIndex, nextState.CurrentRouteIndex);
    }

    [Fact]
    public void TryAdvance_halts_on_decision_for_non_select_tool()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation"),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination")
            });

        var state = RoutingState.CreateInitial(plan);
        var decision = new ToolSelectionDecision(
            new ToolId("tools.any"),
            new Dictionary<string, object?>());

        var result = RouteGate.TryAdvance(plan, state, decision, new StubToolRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("route_decision_unexpected", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    private static BuildPlan CreatePlan(WorkOrderId workOrderId, IReadOnlyList<RouteRule> routeRules)
    {
        var workOrder = new WorkOrder(
            workOrderId,
            "Original request.",
            "Validate routing.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            routeRules);

        var steps = new List<BuildStep>();
        foreach (var rule in routeRules)
        {
            steps.Add(new RouteStep(
                rule.NodeId,
                $"Route {rule.NodeId}.",
                rule.NodeId,
                rule.Intent,
                rule.Owner,
                workOrderId));
        }

        var authority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false);

        return new BuildPlan(
            "plan",
            request,
            authority,
            steps,
            new[] { new BuildArtifact("plan.json", "Plan payload.") });
    }

    private sealed class StubToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new List<ToolRegistryEntry>();

        public ToolRegistryEntry? GetTool(ToolId toolId) => null;
    }
}
