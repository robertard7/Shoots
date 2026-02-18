using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Providers.Null;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RuntimeOrchestratorWaitingTests
{
    [Fact]
    public void Run_exposes_waiting_payload_for_host_consumption()
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-host-waiting"),
            "Original request.",
            "Waiting payload.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, System.Array.Empty<string>())
            });

        var plan = BuildPlanTestFactory.CreatePlan(request, new BuildStep[]
        {
            new RouteStep("select", "Select tool.", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
            new RouteStep("terminate", "Terminate route.", "terminate", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
        });

        var envelope = new RuntimeOrchestrator(
            new EmptyToolRegistry(),
            new RefusingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullProviderClient())
            .Run(plan);

        Assert.Equal(RoutingStatus.Waiting, envelope.State.Status);
        Assert.NotNull(envelope.Waiting);
        Assert.Equal(workOrder.Id, envelope.Waiting!.WorkOrderId);
        Assert.Equal("select", envelope.Waiting.RouteGateId);
        Assert.Equal("select", envelope.Waiting.CurrentNodeId);
        Assert.Equal("tool.selection", envelope.Waiting.DecisionPromptKey);
        Assert.Equal(DecisionOwner.Ai, envelope.Waiting.DecisionOwner);
        Assert.Equal(DecisionPolicy.Hard, envelope.Waiting.Policy);
    }

    private sealed class RefusingAiDecisionProvider : IAiDecisionProvider
    {
        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request) => null;
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public string CatalogHash => "empty";
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new List<ToolRegistryEntry>();
        public ToolRegistryEntry? GetTool(ToolId toolId) => null;
        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => GetAllTools();
    }
}
