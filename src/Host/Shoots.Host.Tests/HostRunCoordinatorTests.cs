using Xunit;
using Shoots.Contracts.Core;
using Shoots.Host.Abstractions;
using Shoots.Host.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;

namespace Shoots.Host.Tests;

public sealed class HostRunCoordinatorTests
{
    [Fact]
    public void Decision_digest_is_deterministic_for_identical_inputs()
    {
        var request = new DecisionInjectionRequest("wo-1", "plan-hash", "gate-1", "tool.alpha", "{\"a\":1}");

        var first = DecisionDigest.Compute(request);
        var second = DecisionDigest.Compute(request);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Decision_digest_changes_when_bindings_change()
    {
        var first = DecisionDigest.Compute(new DecisionInjectionRequest("wo-1", "plan-hash", "gate-1", "tool.alpha", "{\"a\":1}"));
        var second = DecisionDigest.Compute(new DecisionInjectionRequest("wo-1", "plan-hash", "gate-1", "tool.alpha", "{\"a\":2}"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Blocked_rerun_does_not_mutate_attempt_or_progress_token()
    {
        var persistence = new InMemoryRuntimePersistence();
        var coordinator = new HostRunCoordinator(new NullToolRegistry(), new WaitingDecisionProvider(), persistence);
        var plan = CreatePlan("plan-a");

        var first = coordinator.Run(plan);
        Assert.Equal(RoutingStatus.Waiting, first.State.Status);

        var stateStore = (IRunResumeStateStore)persistence;
        var runStateBefore = stateStore.LoadByWorkOrderId("wo-1");
        Assert.NotNull(runStateBefore);

        var second = coordinator.Run(plan);
        Assert.Equal(RoutingStatus.Waiting, second.State.Status);

        var runStateAfter = stateStore.LoadByWorkOrderId("wo-1");
        Assert.NotNull(runStateAfter);
        Assert.Equal(runStateBefore!.AttemptCounter, runStateAfter!.AttemptCounter);
        Assert.Equal(runStateBefore.ProgressToken, runStateAfter.ProgressToken);
    }

    private static BuildPlan CreatePlan(string planId)
    {
        var workOrder = new WorkOrder(new WorkOrderId("wo-1"), "request", "intent", Array.Empty<string>(), Array.Empty<string>());
        var routeRules = new[]
        {
            new RouteRule("n1", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "n2" }, DecisionPolicy.Hard),
            new RouteRule("n2", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
        };
        var request = new BuildRequest(workOrder, "cmd", new Dictionary<string, object?>(), routeRules);
        var authority = new DelegationAuthority(new ProviderId("test"), ProviderKind.Local, "scope", true);
        var steps = new BuildStep[]
        {
            new RouteStep("n1", "select", "n1", RouteIntent.SelectTool, DecisionOwner.Ai, new WorkOrderId("wo-1")),
            new RouteStep("n2", "done", "n2", RouteIntent.Terminate, DecisionOwner.Rule, new WorkOrderId("wo-1"))
        };
        return new BuildPlan(planId, request, "g", "n", "e", authority, steps, Array.Empty<BuildArtifact>());
    }

    private sealed class NullToolRegistry : IToolRegistry
    {
        public string CatalogHash => "catalog";

        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => Array.Empty<ToolRegistryEntry>();

        public ToolRegistryEntry? GetTool(ToolId toolId) => null;

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => Array.Empty<ToolRegistryEntry>();
    }

    private sealed class WaitingDecisionProvider : IAiDecisionProvider
    {
        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
            => null;
    }
}
