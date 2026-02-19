using Xunit;
using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Host.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;

namespace Shoots.Host.Tests.EndToEnd;

public sealed class HostEndToEndFlowTests
{
    [Fact]
    public void Scenario_B_waiting_then_rerun_without_injection_is_blocked()
    {
        var persistence = new InMemoryRuntimePersistence();
        var provider = new NullDecisionProvider();
        var coordinator = new HostRunCoordinator(new NullToolRegistry(), provider, persistence);
        var plan = BuildPlan("plan-hash");

        var first = coordinator.Run(plan);
        var second = coordinator.Run(plan);

        Assert.Equal(RoutingStatus.Waiting, first.State.Status);
        Assert.Equal(RoutingStatus.Waiting, second.State.Status);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, second.Trace.Entries[^1].Event);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public void Scenario_C_waiting_then_plan_change_requires_override_or_digest()
    {
        var persistence = new InMemoryRuntimePersistence();
        var coordinator = new HostRunCoordinator(new NullToolRegistry(), new NullDecisionProvider(), persistence);
        var firstPlan = BuildPlan("hash-a");
        var changedPlan = BuildPlan("hash-b");

        var first = coordinator.Run(firstPlan);
        var blocked = coordinator.Run(changedPlan);

        Assert.Equal(RoutingStatus.Waiting, first.State.Status);
        Assert.Equal(RoutingStatus.Waiting, blocked.State.Status);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, blocked.Trace.Entries[^1].Event);
    }

    private static BuildPlan BuildPlan(string planHash)
    {
        var workOrder = new WorkOrder(new WorkOrderId("wo-e2e"), "req", "intent", Array.Empty<string>(), Array.Empty<string>());
        var rules = new[]
        {
            new RouteRule("n1", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "n2" }),
            new RouteRule("n2", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
        };
        var request = new BuildRequest(workOrder, "cmd", new Dictionary<string, object?>(), rules);
        var steps = new BuildStep[]
        {
            new RouteStep("n1", "select", "n1", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
            new RouteStep("n2", "end", "n2", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
        };
        return new BuildPlan(planHash, request, "g", "n", "e", new DelegationAuthority(new ProviderId("local"), ProviderKind.Local, "scope", true), steps, Array.Empty<BuildArtifact>());
    }

    private sealed class NullToolRegistry : IToolRegistry
    {
        public string CatalogHash => "catalog";
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => Array.Empty<ToolRegistryEntry>();
        public ToolRegistryEntry? GetTool(ToolId toolId) => null;
        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => Array.Empty<ToolRegistryEntry>();
    }

    private sealed class NullDecisionProvider : IAiDecisionProvider
    {
        public int CallCount { get; private set; }
        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
        {
            CallCount++;
            return null;
        }
    }
}
