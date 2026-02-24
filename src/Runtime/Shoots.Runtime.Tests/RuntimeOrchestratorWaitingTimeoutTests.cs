using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Null;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;

namespace Shoots.Runtime.Tests;

public sealed class RuntimeOrchestratorWaitingTimeoutTests
{
    [Fact]
    public void Waiting_timeout_triggers_deterministic_failure_with_stable_keys()
    {
        var plan = BuildWaitingPlan("wo-timeout");
        var stateStore = new InMemoryRuntimePersistence();
        var orchestrator = new RuntimeOrchestrator(
            new EmptyToolRegistry(),
            new RefusingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullProviderClient(),
            stateStore);

        _ = orchestrator.Run(plan, new RuntimeRunOptions(MaxDecisionWaits: 1));
        var second = orchestrator.Run(plan, new RuntimeRunOptions(MaxDecisionWaits: 1));

        Assert.Equal(RoutingStatus.Halted, second.State.Status);
        Assert.Contains(second.Trace.Entries, e => e.Event == RoutingTraceEventKind.Route && e.Detail == "decision.gate.waiting");
        Assert.Contains(second.Trace.Entries, e => e.Event == RoutingTraceEventKind.Route && e.Detail == "decision.waits.exhausted");
        Assert.Contains(second.Trace.Entries, e => e.Event == RoutingTraceEventKind.Error && e.Detail == "route.decision_timeout");
    }

    private static BuildPlan BuildWaitingPlan(string workOrderId)
    {
        var workOrder = new WorkOrder(new WorkOrderId(workOrderId), "r", "g", new List<string>(), new List<string>());
        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        return BuildPlanTestFactory.CreatePlan(request, new BuildStep[]
        {
            new RouteStep("select", "Select tool.", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
            new RouteStep("terminate", "Terminate route.", "terminate", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
        });
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
