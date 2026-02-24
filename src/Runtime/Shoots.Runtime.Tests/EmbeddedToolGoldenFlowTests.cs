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


    [Fact]
    public void Golden_flow_write_git_commit_log_terminates()
    {
        var root = Directory.CreateTempSubdirectory("runtime-tools-").FullName;
        try
        {
            var workOrder = new WorkOrder(new WorkOrderId("wo-tools-3"), "goal", WorkOrderState.Pending, DateTimeOffset.UtcNow);
            var request = new BuildRequest(
                workOrder,
                "core.route",
                new Dictionary<string, object?>(),
                new[]
                {
                    new RouteRule("write", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "init" }),
                    new RouteRule("init", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Action, new[] { "add" }),
                    new RouteRule("add", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Action, new[] { "commit" }),
                    new RouteRule("commit", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Action, new[] { "log" }),
                    new RouteRule("log", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Action, new[] { "done" }),
                    new RouteRule("done", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
                });

            var plan = BuildPlanTestFactory.CreatePlan(request, new BuildStep[]
            {
                new RouteStep("write", "Write", "write", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("init", "Init", "init", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("add", "Add", "add", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("commit", "Commit", "commit", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("log", "Log", "log", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("done", "Done", "done", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
            });

            var repoPath = Path.Combine(root, "repo");
            Directory.CreateDirectory(repoPath);

            var registry = new SnapshotToolRegistry(
                CreateToolSpec("linux.fs.write_text.v1", "path", "text"),
                CreateToolSpec("linux.proc.exec.v1", "file", "args", "cwd"),
                CreateToolSpec("linux.env.set_local.v1", "name", "value"),
                CreateToolSpec("linux.git.add.v1", "paths", "cwd"),
                CreateToolSpec("linux.git.commit.v1", "message", "cwd"),
                CreateToolSpec("linux.git.log.v1", "max", "cwd"));

            var decisions = new Queue<ToolSelectionDecision>(new[]
            {
                new ToolSelectionDecision(new ToolId("linux.fs.write_text.v1"), new Dictionary<string, object?> { ["path"] = "repo/a.txt", ["text"] = "hello" }),
                new ToolSelectionDecision(new ToolId("linux.proc.exec.v1"), new Dictionary<string, object?> { ["file"] = "git", ["args"] = new object?[] { "init" }, ["cwd"] = "repo" }),
                new ToolSelectionDecision(new ToolId("linux.git.add.v1"), new Dictionary<string, object?> { ["paths"] = new object?[] { "a.txt" }, ["cwd"] = "repo" }),
                new ToolSelectionDecision(new ToolId("linux.git.commit.v1"), new Dictionary<string, object?> { ["message"] = "init", ["cwd"] = "repo" }),
                new ToolSelectionDecision(new ToolId("linux.git.log.v1"), new Dictionary<string, object?> { ["max"] = 1, ["cwd"] = "repo" })
            });

            var client = new EmbeddedToolProviderClient(root);
            var loop = new RoutingLoop(plan, registry, new QueuedDecisionProvider(decisions), NullRuntimeNarrator.Instance, client);

            var result = loop.Run();
            Assert.Equal(RoutingStatus.Completed, result.State.Status);
            Assert.Equal(5, result.ToolResults.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Golden_flow_ensure_dir_then_exists_completes()
    {
        var root = Directory.CreateTempSubdirectory("runtime-tools-").FullName;
        try
        {
            var workOrder = new WorkOrder(new WorkOrderId("wo-tools-4"), "goal", WorkOrderState.Pending, DateTimeOffset.UtcNow);
            var request = new BuildRequest(
                workOrder,
                "core.route",
                new Dictionary<string, object?>(),
                new[]
                {
                    new RouteRule("ensure", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "exists" }),
                    new RouteRule("exists", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Action, new[] { "done" }),
                    new RouteRule("done", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
                });

            var plan = BuildPlanTestFactory.CreatePlan(request, new BuildStep[]
            {
                new RouteStep("ensure", "Ensure", "ensure", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("exists", "Exists", "exists", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("done", "Done", "done", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
            });

            var registry = new SnapshotToolRegistry(
                CreateToolSpec("linux.fs.ensure_dir.v1", "path"),
                CreateToolSpec("linux.fs.exists.v1", "path"));

            var decisions = new Queue<ToolSelectionDecision>(new[]
            {
                new ToolSelectionDecision(new ToolId("linux.fs.ensure_dir.v1"), new Dictionary<string, object?> { ["path"] = "tmpd" }),
                new ToolSelectionDecision(new ToolId("linux.fs.exists.v1"), new Dictionary<string, object?> { ["path"] = "tmpd" })
            });

            var loop = new RoutingLoop(plan, registry, new QueuedDecisionProvider(decisions), NullRuntimeNarrator.Instance, new EmbeddedToolProviderClient(root));
            var result = loop.Run();

            Assert.Equal(RoutingStatus.Completed, result.State.Status);
            Assert.Equal(2, result.ToolResults.Count);
            Assert.True(Directory.Exists(Path.Combine(root, "tmpd")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void Golden_flow_batch8_meminfo_tool_completes()
    {
        var root = Directory.CreateTempSubdirectory("runtime-tools-").FullName;
        try
        {
            var workOrder = new WorkOrder(new WorkOrderId("wo-tools-8"), "goal", WorkOrderState.Pending, DateTimeOffset.UtcNow);
            var request = new BuildRequest(
                workOrder,
                "core.route",
                new Dictionary<string, object?>(),
                new[]
                {
                    new RouteRule("mem", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "done" }),
                    new RouteRule("done", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
                });

            var plan = BuildPlanTestFactory.CreatePlan(request, new BuildStep[]
            {
                new RouteStep("mem", "Mem", "mem", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
                new RouteStep("done", "Done", "done", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
            });

            var registry = new SnapshotToolRegistry(CreateToolSpec("linux.sys.meminfo.v1"));
            var decisions = new Queue<ToolSelectionDecision>(new[]
            {
                new ToolSelectionDecision(new ToolId("linux.sys.meminfo.v1"), new Dictionary<string, object?>())
            });

            var loop = new RoutingLoop(plan, registry, new QueuedDecisionProvider(decisions), NullRuntimeNarrator.Instance, new EmbeddedToolProviderClient(root));
            var result = loop.Run();

            Assert.Equal(RoutingStatus.Completed, result.State.Status);
            Assert.Single(result.ToolResults);
            Assert.True(result.ToolResults[0].Success);
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
