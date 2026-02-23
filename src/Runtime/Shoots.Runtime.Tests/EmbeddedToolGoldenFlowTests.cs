using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Embedded;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;

namespace Shoots.Runtime.Tests;

public sealed class EmbeddedToolGoldenFlowTests
{
    [Fact]
    public void Select_tool_executes_and_reaches_terminal()
    {
        var root = Directory.CreateTempSubdirectory("runtime-tools-").FullName;
        try
        {
            var workOrder = new WorkOrder(new WorkOrderId("wo-tools"), "goal", WorkOrderState.Pending, DateTimeOffset.UtcNow);
            var request = new BuildRequest(
                workOrder,
                "core.route",
                new Dictionary<string, object?>(),
                new[]
                {
                    new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "done" }),
                    new RouteRule("done", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
                });

            var plan = BuildPlanTestFactory.CreatePlan(request, new BuildStep[]
            {
                new RouteStep("select", "Select tool", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("done", "Done", "done", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
            });

            var loop = new RoutingLoop(
                plan,
                new SingleToolRegistry(new ToolSpec(
                    new ToolId("linux.fs.mkdir.v1"),
                    "mkdir",
                    new ToolAuthorityScope(ProviderKind.Embedded, ProviderCapabilities.ToolExecution),
                    new[] { new ToolInputSpec("path", "string", true, "") },
                    new[] { new ToolOutputSpec("path", "string", "") },
                    Array.Empty<string>())),
                new ToolDecisionProvider(new ToolId("linux.fs.mkdir.v1"), new Dictionary<string, object?> { ["path"] = "created" }),
                NullRuntimeNarrator.Instance,
                new EmbeddedToolProviderClient(root));

            var result = loop.Run();

            Assert.Equal(RoutingStatus.Completed, result.State.Status);
            Assert.Single(result.ToolResults);
            Assert.True(result.ToolResults[0].Success);
            Assert.True(Directory.Exists(Path.Combine(root, "created")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class SingleToolRegistry : IToolRegistry
    {
        private readonly ToolRegistryEntry _entry;
        public SingleToolRegistry(ToolSpec spec) => _entry = new ToolRegistryEntry(spec);
        public string CatalogHash => "single";
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new[] { _entry };
        public ToolRegistryEntry? GetTool(ToolId toolId) => toolId == _entry.Spec.ToolId ? _entry : null;
        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => new[] { _entry };
    }

    private sealed class ToolDecisionProvider : IAiDecisionProvider
    {
        private readonly ToolId _toolId;
        private readonly IReadOnlyDictionary<string, object?> _bindings;
        public ToolDecisionProvider(ToolId toolId, IReadOnlyDictionary<string, object?> bindings)
        {
            _toolId = toolId;
            _bindings = bindings;
        }

        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
            => new(_toolId, _bindings);
    }
}
