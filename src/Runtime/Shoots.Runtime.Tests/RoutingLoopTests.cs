using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;

using Shoots.ProviderAdapters.Abstractions;
using Shoots.ProviderAdapters.Null;

using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Abstractions.Provider;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RoutingLoopTests
{
    [Fact]
    public void Ai_refusal_waits_and_returns_control()
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
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
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

        var plan = BuildPlanTestFactory.CreatePlan(request, steps);

        var loop = new RoutingLoop(
            plan,
            new EmptyToolRegistry(),
            new RefusingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullProviderClient());

        var result = loop.Run();

        Assert.Equal(RoutingStatus.Waiting, result.State.Status);
        Assert.Equal("select", result.State.CurrentNodeId);
        Assert.Empty(result.ToolResults);
        Assert.Contains(result.Telemetry, record => record.Event == RoutingTraceEventKind.DecisionRequired);
    }



    [Fact]
    public void Step_budget_exceeded_halts_deterministically()
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-budget"),
            "Original request.",
            "Route loop budget.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "review" }),
                new RouteRule("review", RouteIntent.Review, DecisionOwner.Runtime, "review", MermaidNodeKind.Route, new[] { "validate" })
            });

        var steps = new BuildStep[]
        {
            new RouteStep("validate", "Validate.", "validate", RouteIntent.Validate, DecisionOwner.Runtime, workOrder.Id),
            new RouteStep("review", "Review.", "review", RouteIntent.Review, DecisionOwner.Runtime, workOrder.Id)
        };

        var plan = BuildPlanTestFactory.CreatePlan(request, steps);

        var result = new RoutingLoop(
            plan,
            new EmptyToolRegistry(),
            new ThrowingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullProviderClient(),
            stepBudget: 4)
            .Run();

        Assert.Equal(RoutingStatus.Halted, result.State.Status);
        var error = Assert.Single(result.Trace.Entries, entry => entry.Event == RoutingTraceEventKind.Error);
        Assert.Equal("route_step_budget_exceeded", error.Error?.Code);
        Assert.Contains("budget=4", Assert.IsType<string>(error.Error?.Details));
        Assert.Contains("transition.count=4", Assert.IsType<string>(error.Error?.Details));
        Assert.Contains("node=validate", Assert.IsType<string>(error.Error?.Details));
        Assert.Contains(result.Trace.Entries, entry => entry.Event == RoutingTraceEventKind.StepBudgetExceeded);
    }

    [Fact]
    public void Trace_replays_deterministically()
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-loop"),
            "Original request.",
            "Route loop trace.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
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

        var plan = BuildPlanTestFactory.CreatePlan(request, steps);

        var loop = new RoutingLoop(
            plan,
            new SampleToolRegistry(),
            new AcceptingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(new ToolId("tools.sample")));
        var first = loop.Run();

        var replay = new RoutingLoop(
            plan,
            new SampleToolRegistry(),
            new AcceptingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(new ToolId("tools.sample")),
            trace: first.Trace);
        var second = replay.Run();

        Assert.Equal(first.Trace.Entries.Count, second.Trace.Entries.Count);
        Assert.Equal(first.Trace.Entries.Select(e => e.Event), second.Trace.Entries.Select(e => e.Event));
    }

    [Fact]
    public void Provider_tool_choice_does_not_change_route_path()
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-path"),
            "Original request.",
            "Route loop path.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
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

        var plan = BuildPlanTestFactory.CreatePlan(request, steps);

        var first = new RoutingLoop(
            plan,
            new SampleToolRegistry(),
            new ToolDecisionProvider(new ToolId("tools.sample")),
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(new ToolId("tools.sample")))
            .Run();

        var second = new RoutingLoop(
            plan,
            new AlternateToolRegistry(),
            new ToolDecisionProvider(new ToolId("tools.other")),
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(new ToolId("tools.other")))
            .Run();

        var firstPath = first.Trace.Entries
            .Where(entry => entry.Event == RoutingTraceEventKind.NodeAdvanced)
            .Select(entry => (entry.FromNodeId, entry.ToNodeId))
            .ToArray();
        var secondPath = second.Trace.Entries
            .Where(entry => entry.Event == RoutingTraceEventKind.NodeAdvanced)
            .Select(entry => (entry.FromNodeId, entry.ToNodeId))
            .ToArray();

        var expectedPath = new[] { (FromNodeId: "select", ToNodeId: "terminate") };
        Assert.Equal(expectedPath, firstPath.Take(expectedPath.Length));
        Assert.Equal(expectedPath, secondPath.Take(expectedPath.Length));
        Assert.Equal(RoutingStatus.Completed, first.State.Status);
        Assert.Equal(RoutingStatus.Completed, second.State.Status);
        Assert.Equal("terminate", first.State.CurrentNodeId);
        Assert.Equal("terminate", second.State.CurrentNodeId);
    }


    [Fact]
    public void Bypass_trace_uses_graph_transition_nodes()
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-bypass-trace"),
            "Original request.",
            "Route loop bypass trace.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule(
                    "select",
                    RouteIntent.SelectTool,
                    DecisionOwner.Ai,
                    "tool.selection",
                    MermaidNodeKind.Start,
                    new[] { "terminate" },
                    DecisionPolicy.Bypass,
                    new FallbackToolSelection(new ToolId("tools.sample"), new Dictionary<string, object?>()),
                    "rogue-node"),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var steps = new BuildStep[]
        {
            new RouteStep("select", "Select tool.", "select", RouteIntent.SelectTool, DecisionOwner.Ai, workOrder.Id),
            new RouteStep("terminate", "Terminate route.", "terminate", RouteIntent.Terminate, DecisionOwner.Rule, workOrder.Id)
        };

        var plan = BuildPlanTestFactory.CreatePlan(request, steps);

        var result = new RoutingLoop(
            plan,
            new SampleToolRegistry(),
            new RefusingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new SuccessfulProviderClient(new ToolId("tools.sample")))
            .Run();

        Assert.Equal(RoutingStatus.Completed, result.State.Status);
        var bypass = Assert.Single(result.Trace.Entries, e => e.Event == RoutingTraceEventKind.DecisionGateBypassed);
        Assert.Equal("select", bypass.FromNodeId);
        Assert.Equal("terminate", bypass.ToNodeId);
    }

    [Fact]
    public void Provider_failure_halts_with_trace_error()
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-fail"),
            "Original request.",
            "Route loop provider failure.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
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

        var plan = BuildPlanTestFactory.CreatePlan(request, steps);

        var loop = new RoutingLoop(
            plan,
            new EmptyToolRegistry(),
            new ThrowingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullProviderClient());

        var result = loop.Run();

        var errorEntry = Assert.Single(result.Trace.Entries, entry => entry.Event == RoutingTraceEventKind.Error);
        Assert.Equal("internal_error", errorEntry.Error?.Code);
        Assert.Equal(RoutingStatus.Halted, result.State.Status);
        Assert.Equal("select", result.State.CurrentNodeId);
        Assert.Empty(result.ToolResults);
        Assert.Single(result.Trace.Entries, entry => entry.Event == RoutingTraceEventKind.Halted);
    }
	[Fact]
	public void Routing_advances_without_provider_on_non_select_steps()
	{
		var workOrder = new WorkOrder(
			new WorkOrderId("wo-no-provider"),
			"Original request.",
			"Route loop without provider.",
			new List<string>(),
			new List<string>());

		var request = new BuildRequest(
			workOrder,
			"core.route",
			new Dictionary<string, object?>(),
			new[]
			{
				new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
				new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
			});

		var steps = new BuildStep[]
		{
			new RouteStep(
				"validate",
				"Validate work.",
				"validate",
				RouteIntent.Validate,
				DecisionOwner.Runtime,
				workOrder.Id),
			new RouteStep(
				"terminate",
				"Terminate route.",
				"terminate",
				RouteIntent.Terminate,
				DecisionOwner.Rule,
				workOrder.Id)
		};

		var plan = BuildPlanTestFactory.CreatePlan(request, steps);

		var loop = new RoutingLoop(
			plan,
			new EmptyToolRegistry(),
			new ThrowingAiDecisionProvider(),
			NullRuntimeNarrator.Instance,
			new NullProviderClient());

		var result = loop.Run();

		Assert.Equal(RoutingStatus.Completed, result.State.Status);
		Assert.Equal("terminate", result.State.CurrentNodeId);
		Assert.DoesNotContain(
			result.Trace.Entries,
			entry => entry.Event == RoutingTraceEventKind.DecisionRequired);
	}


    [Fact]
    public void Null_provider_halts_tool_step_deterministically()
    {
        var workOrder = new WorkOrder(
            new WorkOrderId("wo-null-provider"),
            "Original request.",
            "Route loop null provider.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
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

        var plan = BuildPlanTestFactory.CreatePlan(request, steps);

        var loop = new RoutingLoop(
            plan,
            new SampleToolRegistry(),
            new AcceptingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullProviderClient());

        var result = loop.Run();

        Assert.Equal(RoutingStatus.Halted, result.State.Status);
        Assert.Single(result.ToolResults);
        Assert.False(result.ToolResults[0].Success);
        Assert.Equal("tool.not_available", result.ToolResults[0].Outputs["error.code"]);
    }

    private sealed class RefusingAiDecisionProvider : IAiDecisionProvider
    {
        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request) => null;
    }

    private sealed class AcceptingAiDecisionProvider : IAiDecisionProvider
    {
        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
        {
            if (request.RouteStep.Intent != RouteIntent.SelectTool)
                return null;

            return new ToolSelectionDecision(new ToolId("tools.sample"), new Dictionary<string, object?>());
        }
    }

    private sealed class ToolDecisionProvider : IAiDecisionProvider
    {
        private readonly ToolId _toolId;

        public ToolDecisionProvider(ToolId toolId)
        {
            _toolId = toolId;
        }

        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
        {
            return new ToolSelectionDecision(_toolId, new Dictionary<string, object?>());
        }
    }

    private sealed class ThrowingAiDecisionProvider : IAiDecisionProvider
    {
        public ToolSelectionDecision? RequestDecision(AiDecisionRequest request)
        {
            throw new InvalidOperationException("Provider should not be called for non-select steps.");
        }
    }

    private sealed class SuccessfulProviderClient : IProviderClient
    {
        private readonly ToolId _toolId;

        public SuccessfulProviderClient(ToolId toolId)
        {
            _toolId = toolId;
        }

        public ValueTask<ProviderExecutionResult> ExecuteAsync(ProviderExecutionEnvelope envelope, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var result = new ToolResult(
                _toolId,
                new Dictionary<string, object?>
                {
                    ["output"] = "ok"
                },
                true);

            return ValueTask.FromResult(new ProviderExecutionResult(
                envelope.RequestId,
                ProviderExecutionResultKind.ToolExecuted,
                result,
                null,
                null,
                null));
        }
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public string CatalogHash => "empty";

        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new List<ToolRegistryEntry>();

        public ToolRegistryEntry? GetTool(ToolId toolId) => null;

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => GetAllTools();
    }

    private sealed class SampleToolRegistry : IToolRegistry
    {
        public string CatalogHash => "sample";

        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => GetSnapshot();

        public ToolRegistryEntry? GetTool(ToolId toolId)
        {
            return toolId.Value == "tools.sample"
                ? new ToolRegistryEntry(CreateToolSpec())
                : null;
        }

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => new[] { new ToolRegistryEntry(CreateToolSpec()) };

        private static ToolSpec CreateToolSpec()
        {
            return new ToolSpec(
                new ToolId("tools.sample"),
                "Sample tool.",
                new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
                new List<ToolInputSpec>(),
                new List<ToolOutputSpec>(),
                new[] { "sample", "test" });
        }
    }

    private sealed class AlternateToolRegistry : IToolRegistry
    {
        public string CatalogHash => "other";

        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => GetSnapshot();

        public ToolRegistryEntry? GetTool(ToolId toolId)
        {
            return toolId.Value == "tools.other"
                ? new ToolRegistryEntry(CreateToolSpec())
                : null;
        }

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => new[] { new ToolRegistryEntry(CreateToolSpec()) };

        private static ToolSpec CreateToolSpec()
        {
            return new ToolSpec(
                new ToolId("tools.other"),
                "Other tool.",
                new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
                new List<ToolInputSpec>(),
                new List<ToolOutputSpec>(),
                new[] { "sample", "test" });
        }
    }
}
