using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RoutingLoopTests
{
    [Fact]
    public void Ai_refusal_leaves_state_waiting()
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-loop"),
            "Original request.",
            "Route loop refusal.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection"),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination")
            });

        var steps = new BuildStep[]
        {
            new RouteStep(
                "select",
                "Select tool.",
                "select",
                RouteIntent.SelectTool,
                DecisionOwner.Ai,
                workOrder.Id),
            new RouteStep(
                "terminate",
                "Terminate route.",
                "terminate",
                RouteIntent.Terminate,
                DecisionOwner.Rule,
                workOrder.Id)
        };

        var plan = new BuildPlan(
            "plan",
            request,
            new DelegationAuthority(
                ProviderId: new ProviderId("local"),
                Kind: ProviderKind.Local,
                PolicyId: "local-only",
                AllowsDelegation: false),
            steps,
            new[] { new BuildArtifact("plan.json", "Plan payload.") });

        var loop = new RoutingLoop(
            plan,
            new EmptyToolRegistry(),
            new RefusingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullToolExecutor());

        var result = loop.Run();

        Assert.Equal(RoutingStatus.Waiting, result.State.Status);
        Assert.Equal(0, result.State.CurrentRouteIndex);
        Assert.Empty(result.ToolResults);
    }

    private sealed class RefusingAiDecisionProvider : IAiDecisionProvider
    {
        public ToolSelectionDecision? RequestDecision(WorkOrder workOrder, RouteStep step, RoutingState state) => null;
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new List<ToolRegistryEntry>();

        public ToolRegistryEntry? GetTool(ToolId toolId) => null;

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => GetAllTools();
    }
}
