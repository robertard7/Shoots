using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Abstractions.Provider;
using Shoots.Runtime.Core;

namespace Shoots.Runtime.Tests;

public sealed class ProviderDecisionBoundaryTests
{
    [Fact]
    public void Tool_execution_is_dispatched_through_provider_client_only()
    {
        var workOrder = new WorkOrder(new WorkOrderId("wo-boundary"), "req", "goal", new List<string>(), new List<string>());
        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var plan = BuildPlanTestFactory.CreatePlan(request, new BuildStep[]
        {
            new RouteStep("select", "select", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
            new RouteStep("terminate", "terminate", "terminate", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
        });

        var client = new RecordingProviderClient();
        var orchestrator = new RuntimeOrchestrator(new ToolRegistryStub(), new DeterministicDecisionProvider(), NullRuntimeNarrator.Instance, client);
        var envelope = orchestrator.Run(plan);

        Assert.Equal(RoutingStatus.Completed, envelope.State.Status);
        Assert.Equal(1, client.ToolExecutionCount);
    }

    private sealed class DeterministicDecisionProvider : IAiDecisionProvider
    {
        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
            => new(new ToolId("linux.fs.read_text.v1"), new Dictionary<string, object?> { ["path"] = "README.md" });
    }

    private sealed class ToolRegistryStub : IToolRegistry
    {
        private readonly ToolRegistryEntry _entry = new(new ToolSpec(
            new ToolId("linux.fs.read_text.v1"),
            "read",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.Execute),
            new[] { new ToolInputSpec("path", "string", true, "p") },
            Array.Empty<ToolOutputSpec>(),
            Array.Empty<string>()));

        public string CatalogHash => "h";
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new[] { _entry };
        public ToolRegistryEntry? GetTool(ToolId toolId) => toolId.Value == _entry.Spec.ToolId.Value ? _entry : null;
        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => GetAllTools();
    }

    private sealed class RecordingProviderClient : IProviderClient
    {
        public int ToolExecutionCount { get; private set; }

        public ValueTask<ProviderExecutionResult> ExecuteAsync(ProviderExecutionEnvelope envelope, CancellationToken ct)
        {
            if (envelope.Kind == ProviderExecutionEnvelopeKind.Tool)
                ToolExecutionCount++;

            var result = new ToolResult(envelope.ToolId ?? new ToolId("unknown"), new Dictionary<string, object?>(), true);
            return ValueTask.FromResult(new ProviderExecutionResult(envelope.RequestId, ProviderExecutionResultKind.ToolExecuted, result, null, null, null));
        }
    }
}
