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



    [Fact]
    public void Multi_step_tool_flow_write_replace_read_terminates()
    {
        var root = Directory.CreateTempSubdirectory("runtime-tools-").FullName;
        try
        {
            var workOrder = new WorkOrder(new WorkOrderId("wo-tools-2"), "goal", WorkOrderState.Pending, DateTimeOffset.UtcNow);
            var request = new BuildRequest(
                workOrder,
                "core.route",
                new Dictionary<string, object?>(),
                new[]
                {
                    new RouteRule("write", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "replace" }),
                    new RouteRule("replace", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Action, new[] { "read" }),
                    new RouteRule("read", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Action, new[] { "done" }),
                    new RouteRule("done", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
                });

            var plan = BuildPlanTestFactory.CreatePlan(request, new BuildStep[]
            {
                new RouteStep("write", "Write", "write", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("replace", "Replace", "replace", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("read", "Read", "read", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("done", "Done", "done", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
            });

            var registry = new SnapshotToolRegistry(
                CreateToolSpec("linux.fs.write_text.v1", "path", "text"),
                CreateToolSpec("linux.text.replace.v1", "path", "search", "replace"),
                CreateToolSpec("linux.fs.read_text.v1", "path"));

            var decisions = new Queue<ToolSelectionDecision>(new[]
            {
                new ToolSelectionDecision(new ToolId("linux.fs.write_text.v1"), new Dictionary<string, object?> { ["path"] = "a.txt", ["text"] = "hello world" }),
                new ToolSelectionDecision(new ToolId("linux.text.replace.v1"), new Dictionary<string, object?> { ["path"] = "a.txt", ["search"] = "world", ["replace"] = "tools" }),
                new ToolSelectionDecision(new ToolId("linux.fs.read_text.v1"), new Dictionary<string, object?> { ["path"] = "a.txt" })
            });

            var loop = new RoutingLoop(plan, registry, new QueuedDecisionProvider(decisions), NullRuntimeNarrator.Instance, new EmbeddedToolProviderClient(root));
            var result = loop.Run();

            Assert.Equal(RoutingStatus.Completed, result.State.Status);
            Assert.Equal(3, result.ToolResults.Count);
            Assert.Equal("hello tools", result.ToolResults[2].Outputs["text"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ToolSpec CreateToolSpec(string id, params string[] requiredInputs) => new(
        new ToolId(id),
        id,
        new ToolAuthorityScope(ProviderKind.Embedded, ProviderCapabilities.ToolExecution),
        requiredInputs.Select(name => new ToolInputSpec(name, "string", true, string.Empty)).ToArray(),
        new[] { new ToolOutputSpec("ok", "bool", string.Empty) },
        Array.Empty<string>());

    private sealed class SingleToolRegistry : IToolRegistry
    {
        private readonly ToolRegistryEntry _entry;
        public SingleToolRegistry(ToolSpec spec) => _entry = new ToolRegistryEntry(spec);
        public string CatalogHash => "single";
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new[] { _entry };
        public ToolRegistryEntry? GetTool(ToolId toolId) => toolId == _entry.Spec.ToolId ? _entry : null;
        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => new[] { _entry };
    }

    private sealed class QueuedDecisionProvider : IAiDecisionProvider
    {
        private readonly Queue<ToolSelectionDecision> _decisions;

        public QueuedDecisionProvider(Queue<ToolSelectionDecision> decisions)
        {
            _decisions = decisions;
        }

        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
            => _decisions.Count == 0 ? null : _decisions.Dequeue();
    }

    private sealed class SnapshotToolRegistry : IToolRegistry
    {
        private readonly IReadOnlyList<ToolRegistryEntry> _entries;

        public SnapshotToolRegistry(params ToolSpec[] specs)
        {
            _entries = specs.Select(static s => new ToolRegistryEntry(s)).ToArray();
        }

        public string CatalogHash => "snapshot";
        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => _entries;
        public ToolRegistryEntry? GetTool(ToolId toolId) => _entries.FirstOrDefault(e => e.Spec.ToolId == toolId);
        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => _entries;
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
