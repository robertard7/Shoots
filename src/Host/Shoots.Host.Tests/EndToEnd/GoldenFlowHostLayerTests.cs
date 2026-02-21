using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Host.Tests.EndToEnd;

public sealed class GoldenFlowHostLayerTests
{
    [Fact]
    public void Run_waiting_blocked_rerun_resume_complete_and_state_snapshot_paths_are_stable()
    {
        var persistence = new InMemoryRuntimePersistence();
        var orchestrator = new RuntimeOrchestrator(
            new SampleToolRegistry(),
            new WaitingThenAcceptDecisionProvider(new ToolId("tools.sample")),
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(),
            persistence);

        var workOrderId = "wo-host-golden";
        var plan = BuildPlan(workOrderId, "plan-a");

        var waiting = orchestrator.Run(plan);
        Assert.Equal(RoutingStatus.Waiting, waiting.State.Status);
        Assert.NotNull(waiting.Waiting);

        var blocked = orchestrator.Run(plan);
        Assert.Equal(RoutingStatus.Waiting, blocked.State.Status);
        Assert.Equal(RoutingTraceEventKind.HostBlockedRerunWaiting, blocked.Trace.Entries[^1].Event);

        var resume = orchestrator.Run(plan, new RuntimeRunOptions(ResumeMode.InjectDecision, "digest-host-golden"));
        Assert.Equal(RoutingStatus.Completed, resume.State.Status);

        var tracePath = Path.GetFullPath(Path.Combine(".state", "trace", $"{workOrderId}.trace.json"));
        var artifactsPath = Path.GetFullPath(Path.Combine(".state", "artifacts", workOrderId));
        Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
        Directory.CreateDirectory(artifactsPath);
        File.WriteAllText(tracePath, System.Text.Json.JsonSerializer.Serialize(resume.Trace.Entries));

        Assert.Equal(Path.GetFullPath(Path.Combine(".state", "trace", "wo-host-golden.trace.json")), tracePath);
        Assert.True(File.Exists(tracePath));
        Assert.True(Directory.Exists(artifactsPath));
    }

    private static BuildPlan BuildPlan(string workOrderId, string planHash)
    {
        var workOrder = new WorkOrder(new WorkOrderId(workOrderId), "req", "intent", Array.Empty<string>(), Array.Empty<string>());
        var rules = new[]
        {
            new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "finish" }),
            new RouteRule("finish", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
        };
        var request = new BuildRequest(workOrder, "cmd", new Dictionary<string, object?>(), rules);
        var steps = new BuildStep[]
        {
            new RouteStep("select", "select", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
            new RouteStep("finish", "finish", "finish", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
        };

        return new BuildPlan(planHash, request, "g", "n", "e", new DelegationAuthority(new ProviderId("local"), ProviderKind.Local, "scope", true), steps, Array.Empty<BuildArtifact>());
    }

    private sealed class SampleToolRegistry : IToolRegistry
    {
        public string CatalogHash => "sample";
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => GetSnapshot();
        public ToolRegistryEntry? GetTool(ToolId toolId)
            => toolId.Value == "tools.sample" ? new ToolRegistryEntry(CreateToolSpec()) : null;

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => new[] { new ToolRegistryEntry(CreateToolSpec()) };

        private static ToolSpec CreateToolSpec() => new(
            new ToolId("tools.sample"),
            "Sample tool",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            new[] { "sample" });
    }

    private sealed class WaitingThenAcceptDecisionProvider : IAiDecisionProvider
    {
        private readonly ToolId _tool;
        private int _calls;

        public WaitingThenAcceptDecisionProvider(ToolId tool)
        {
            _tool = tool;
        }

        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
        {
            _calls++;
            if (_calls == 1)
                return null;
            return new ToolSelectionDecision(_tool, new Dictionary<string, object?>());
        }
    }
    private sealed class SuccessfulProviderClient : Shoots.ProviderAdapters.Abstractions.IProviderClient
    {
        public ValueTask<Shoots.Runtime.Abstractions.Provider.ProviderExecutionResult> ExecuteAsync(Shoots.Runtime.Abstractions.Provider.ProviderExecutionEnvelope envelope, CancellationToken ct)
        {
            var result = new ToolResult(new ToolId("tools.sample"), new Dictionary<string, object?> { ["output"] = "ok" }, true);
            return ValueTask.FromResult(new Shoots.Runtime.Abstractions.Provider.ProviderExecutionResult(
                envelope.RequestId,
                Shoots.Runtime.Abstractions.Provider.ProviderExecutionResultKind.ToolExecuted,
                result,
                null,
                null,
                null));
        }
    }
}
