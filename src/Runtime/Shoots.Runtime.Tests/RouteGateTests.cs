using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RouteGateTests
{
    [Fact]
    public void TryAdvance_halts_on_workorder_mismatch()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var state = new RoutingState(
            new WorkOrderId("wo-other"),
            CreateIntentToken(plan),
            "validate",
            RouteIntent.Validate,
            RoutingStatus.Pending);

        var result = RouteGate.TryAdvance(plan, state, null, new StubToolRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("route_workorder_mismatch", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    [Fact]
    public void TryAdvance_waits_when_select_tool_decision_missing()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);

        var result = RouteGate.TryAdvance(plan, state, null, new StubToolRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.Null(error);
        Assert.Equal(RoutingStatus.Waiting, nextState.Status);
        Assert.Equal(state.CurrentNodeId, nextState.CurrentNodeId);
    }



    [Fact]
    public void TryAdvance_bypasses_when_policy_is_bypass_with_fallback_tool()
    {
        var fallbackTool = new ToolSpec(
            new ToolId("tools.fallback"),
            "Fallback tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
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
                    new FallbackToolSelection(fallbackTool.ToolId, new Dictionary<string, object?>())),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var narrator = new RecordingNarrator();
        RouteGate.Narrator = narrator;
        try
        {
            var state = RoutingState.CreateInitial(plan);
            var result = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(fallbackTool), out var nextState, out var error);

            Assert.True(result);
            Assert.Null(error);
            Assert.Equal("terminate", nextState.CurrentNodeId);
            Assert.Contains("decision.gate.bypassed:terminate", narrator.Events);
        }
        finally
        {
            RouteGate.Narrator = null;
        }
    }

    [Fact]
    public void TryAdvance_bypass_ignores_fallback_next_node_metadata_for_routing()
    {
        var fallbackTool = new ToolSpec(
            new ToolId("tools.fallback"),
            "Fallback tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
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
                    new FallbackToolSelection(fallbackTool.ToolId, new Dictionary<string, object?>()),
                    "rogue-node"),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);
        var advanced = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(fallbackTool), out var nextState, out var error);

        Assert.True(advanced);
        Assert.Null(error);
        Assert.Equal("terminate", nextState.CurrentNodeId);
    }


    [Fact]
    public void TryAdvance_bypass_uses_graph_next_node_even_when_fallback_next_node_matches_allowed_node()
    {
        var fallbackTool = new ToolSpec(
            new ToolId("tools.fallback"),
            "Fallback tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
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
                    new FallbackToolSelection(fallbackTool.ToolId, new Dictionary<string, object?>()),
                    "terminate"),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);
        var advanced = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(fallbackTool), out var nextState, out var error);

        Assert.True(advanced);
        Assert.Null(error);
        Assert.Equal("terminate", nextState.CurrentNodeId);
    }

    [Fact]
    public void TryAdvance_bypass_halts_when_graph_has_multiple_next_nodes()
    {
        var fallbackTool = new ToolSpec(
            new ToolId("tools.fallback"),
            "Fallback tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule(
                    "select",
                    RouteIntent.SelectTool,
                    DecisionOwner.Ai,
                    "tool.selection",
                    MermaidNodeKind.Start,
                    new[] { "terminate", "review" },
                    DecisionPolicy.Bypass,
                    new FallbackToolSelection(fallbackTool.ToolId, new Dictionary<string, object?>()),
                    "terminate"),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>()),
                new RouteRule("review", RouteIntent.Review, DecisionOwner.Runtime, "review", MermaidNodeKind.Route, new[] { "terminate" })
            });

        var state = RoutingState.CreateInitial(plan);
        var advanced = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(fallbackTool), out var nextState, out var error);

        Assert.False(advanced);
        Assert.NotNull(error);
        Assert.Equal("route_step_invalid", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    [Fact]
    public void TryAdvance_bypass_with_zero_next_nodes_completes_deterministically()
    {
        var fallbackTool = new ToolSpec(
            new ToolId("tools.fallback"),
            "Fallback tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule(
                    "select",
                    RouteIntent.SelectTool,
                    DecisionOwner.Ai,
                    "tool.selection",
                    MermaidNodeKind.Start,
                    Array.Empty<string>(),
                    DecisionPolicy.Bypass,
                    new FallbackToolSelection(fallbackTool.ToolId, new Dictionary<string, object?>())),
            });

        var state = RoutingState.CreateInitial(plan);
        var advanced = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(fallbackTool), out var nextState, out var error);

        Assert.True(advanced);
        Assert.Null(error);
        Assert.Equal(RoutingStatus.Completed, nextState.Status);
    }

    [Fact]
    public void TryAdvance_halts_when_policy_is_error_and_decision_missing()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule(
                    "select",
                    RouteIntent.SelectTool,
                    DecisionOwner.Ai,
                    "tool.selection",
                    MermaidNodeKind.Start,
                    new[] { "terminate" },
                    DecisionPolicy.Error),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var narrator = new RecordingNarrator();
        RouteGate.Narrator = narrator;
        try
        {
            var state = RoutingState.CreateInitial(plan);
            var result = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(), out var nextState, out var error);

            Assert.False(result);
            Assert.NotNull(error);
            Assert.Equal("route_decision_required", error!.Code);
            Assert.Equal(RoutingStatus.Halted, nextState.Status);
            Assert.Contains("decision.gate.error", narrator.Events);
        }
        finally
        {
            RouteGate.Narrator = null;
        }
    }

    [Fact]
    public void TryAdvance_waits_when_policy_is_bypass_without_fallback_tool()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule(
                    "select",
                    RouteIntent.SelectTool,
                    DecisionOwner.Ai,
                    "tool.selection",
                    MermaidNodeKind.Start,
                    new[] { "terminate" },
                    DecisionPolicy.Bypass),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var narrator = new RecordingNarrator();
        RouteGate.Narrator = narrator;
        try
        {
            var state = RoutingState.CreateInitial(plan);
            var result = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(), out var nextState, out var error);

            Assert.False(result);
            Assert.Null(error);
            Assert.Equal(RoutingStatus.Waiting, nextState.Status);
            Assert.Contains("decision.gate.waiting", narrator.Events);
        }
        finally
        {
            RouteGate.Narrator = null;
        }
    }

    [Fact]
    public void TryAdvance_halts_on_decision_for_non_select_tool()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);
        var decision = new RouteDecision(
            null,
            new ToolSelectionDecision(
                new ToolId("tools.any"),
                new Dictionary<string, object?>()));

        var result = RouteGate.TryAdvance(plan, state, decision, new StubToolRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("route_decision_unexpected", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    [Fact]
    public void TryAdvance_completes_happy_path()
    {
        var toolSpec = new ToolSpec(
            new ToolId("tools.echo"),
            "Echo tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "validate" }),
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Route, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var narrator = new RecordingNarrator();
        RouteGate.Narrator = narrator;

        try
        {
            var registry = new SnapshotOnlyRegistry(toolSpec);
            var state = RoutingState.CreateInitial(plan);

            var decision = new RouteDecision(
                null,
                new ToolSelectionDecision(toolSpec.ToolId, new Dictionary<string, object?>()));
            var advanced = RouteGate.TryAdvance(plan, state, decision, registry, out var nextState, out var error);

            Assert.True(advanced);
            Assert.Null(error);
            Assert.Equal("validate", nextState.CurrentNodeId);

            advanced = RouteGate.TryAdvance(plan, nextState, null, registry, out var terminalState, out error);
            Assert.True(advanced);
            Assert.Null(error);
            Assert.Equal("terminate", terminalState.CurrentNodeId);

            advanced = RouteGate.TryAdvance(plan, terminalState, null, registry, out var finalState, out error);
            Assert.True(advanced);
            Assert.Null(error);
            Assert.Equal(RoutingStatus.Completed, finalState.Status);
            Assert.Contains("completed", narrator.Events);
        }
        finally
        {
            RouteGate.Narrator = null;
        }
    }

    [Fact]
    public async Task TryAdvance_keeps_narrator_isolated_per_concurrent_flow()
    {
        var toolSpec = new ToolSpec(
            new ToolId("tools.echo"),
            "Echo tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "validate" }),
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Route, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var flowAReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFlowA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var flowA = Task.Run(async () =>
        {
            var narrator = new RecordingNarrator();
            RouteGate.Narrator = narrator;
            try
            {
                flowAReady.TrySetResult(true);
                await releaseFlowA.Task.ConfigureAwait(false);
                ExecuteHappyPath(plan, toolSpec);
                return narrator.Events;
            }
            finally
            {
                RouteGate.Narrator = null;
            }
        });

        var flowB = Task.Run(async () =>
        {
            await flowAReady.Task.ConfigureAwait(false);
            var narrator = new RecordingNarrator();
            RouteGate.Narrator = narrator;
            try
            {
                releaseFlowA.TrySetResult(true);
                ExecuteHappyPath(plan, toolSpec);
                return narrator.Events;
            }
            finally
            {
                RouteGate.Narrator = null;
            }
        });

        var results = await Task.WhenAll(flowA, flowB);
        Assert.Contains("completed", results[0]);
        Assert.Contains("completed", results[1]);
    }

    [Fact]
    public void TryAdvance_halts_on_decision_too_early()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);
        var decision = new RouteDecision(
            null,
            new ToolSelectionDecision(new ToolId("tools.any"), new Dictionary<string, object?>()));

        var result = RouteGate.TryAdvance(plan, state, decision, new SnapshotOnlyRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("route_decision_unexpected", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    [Fact]
    public void TryAdvance_halts_on_decision_too_late()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "validate" }),
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Route, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var lateRule = plan.Request.RouteRules.First(rule => rule.NodeId == "validate");
        var lateToken = RouteIntentTokenFactory.Create(plan, lateRule);
        var lateState = new RoutingState(
            plan.Request.WorkOrder.Id,
            lateToken,
            "validate",
            RouteIntent.Validate,
            RoutingStatus.Pending);
        var decision = new RouteDecision(
            null,
            new ToolSelectionDecision(new ToolId("tools.any"), new Dictionary<string, object?>()));

        var result = RouteGate.TryAdvance(plan, lateState, decision, new SnapshotOnlyRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("route_decision_unexpected", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    [Fact]
    public void TryAdvance_uses_registry_snapshot_over_live()
    {
        var toolSpec = new ToolSpec(
            new ToolId("tools.snap"),
            "Snapshot tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var registry = new SnapshotOnlyRegistry(toolSpec)
        {
            LiveMissing = true
        };
        var state = RoutingState.CreateInitial(plan);
        var decision = new RouteDecision(
            null,
            new ToolSelectionDecision(toolSpec.ToolId, new Dictionary<string, object?>()));

        var result = RouteGate.TryAdvance(plan, state, decision, registry, out var nextState, out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.Equal("terminate", nextState.CurrentNodeId);
    }

    [Fact]
    public void TryAdvance_emits_halt_narration()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var narrator = new RecordingNarrator();
        RouteGate.Narrator = narrator;

        try
        {
            var state = new RoutingState(
                new WorkOrderId("wo-other"),
                CreateIntentToken(plan),
                "validate",
                RouteIntent.Validate,
                RoutingStatus.Pending);

            var result = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(), out var nextState, out var error);

            Assert.False(result);
            Assert.NotNull(error);
            Assert.Equal(RoutingStatus.Halted, nextState.Status);
            Assert.Contains("halted", narrator.Events);
        }
        finally
        {
            RouteGate.Narrator = null;
        }
    }

    [Fact]
    public void TryAdvance_does_not_advance_after_completed()
    {
        var toolSpec = new ToolSpec(
            new ToolId("tools.echo"),
            "Echo tool.",
            new ToolAuthorityScope(ProviderKind.Local, ProviderCapabilities.None),
            new List<ToolInputSpec>(),
            new List<ToolOutputSpec>(),
            Array.Empty<string>());

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var registry = new SnapshotOnlyRegistry(toolSpec);
        var state = RoutingState.CreateInitial(plan);

        var decision = new RouteDecision(
            null,
            new ToolSelectionDecision(toolSpec.ToolId, new Dictionary<string, object?>()));
        var advanced = RouteGate.TryAdvance(plan, state, decision, registry, out var nextState, out var error);

        Assert.True(advanced);
        Assert.Null(error);

        advanced = RouteGate.TryAdvance(plan, nextState, null, registry, out var finalState, out error);
        Assert.True(advanced);
        Assert.Equal(RoutingStatus.Completed, finalState.Status);

        advanced = RouteGate.TryAdvance(plan, finalState, null, registry, out var postState, out error);
        Assert.False(advanced);
        Assert.NotNull(error);
        Assert.Equal("route_state_final", error!.Code);
        Assert.Equal(RoutingStatus.Completed, postState.Status);
    }

    [Fact]
    public void TryAdvance_halts_when_select_tool_owner_is_not_ai()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Human, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);
        var decision = new RouteDecision(
            null,
            new ToolSelectionDecision(new ToolId("tools.any"), new Dictionary<string, object?>()));

        var result = RouteGate.TryAdvance(plan, state, decision, new SnapshotOnlyRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("route_owner_invalid", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    [Fact]
    public void TryAdvance_rejects_decision_on_workorder_mismatch()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("select", RouteIntent.SelectTool, DecisionOwner.Ai, "tool.selection", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            });

        var state = new RoutingState(
            new WorkOrderId("wo-other"),
            CreateIntentToken(plan),
            "select",
            RouteIntent.SelectTool,
            RoutingStatus.Pending);
        var decision = new RouteDecision(
            null,
            new ToolSelectionDecision(new ToolId("tools.any"), new Dictionary<string, object?>()));

        var result = RouteGate.TryAdvance(plan, state, decision, new SnapshotOnlyRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("route_workorder_mismatch", error!.Code);
        Assert.Equal(RoutingStatus.Halted, nextState.Status);
    }

    [Fact]
    public void Tool_result_does_not_affect_routing()
    {
        var toolResult = new ToolResult(
            new ToolId("tools.result"),
            new Dictionary<string, object?> { ["output"] = "value" },
            true);

        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminal, Array.Empty<string>())
            },
            toolResult);

        var state = RoutingState.CreateInitial(plan);

        var result = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(), out var nextState, out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.Equal("terminate", nextState.CurrentNodeId);
    }

    private static BuildPlan CreatePlan(
        WorkOrderId workOrderId,
        IReadOnlyList<RouteRule> routeRules,
        ToolResult? toolResult = null)
    {
        var workOrder = new WorkOrder(
            workOrderId,
            "Original request.",
            "Validate routing.",
            new List<string>(),
            new List<string>());

        var request = new BuildRequest(
            workOrder,
            "core.route",
            new Dictionary<string, object?>(),
            routeRules);

        var steps = new List<BuildStep>();
        foreach (var rule in routeRules)
        {
            steps.Add(new RouteStep(
                rule.NodeId,
                $"Route {rule.NodeId}.",
                rule.NodeId,
                rule.Intent,
                rule.Owner,
                workOrderId));
        }

        return BuildPlanTestFactory.CreatePlan(
            request,
            steps,
            toolResult: toolResult);
    }

    private static void ExecuteHappyPath(BuildPlan plan, ToolSpec toolSpec)
    {
        var registry = new SnapshotOnlyRegistry(toolSpec);
        var state = RoutingState.CreateInitial(plan);

        var decision = new RouteDecision(
            null,
            new ToolSelectionDecision(toolSpec.ToolId, new Dictionary<string, object?>()));

        var advanced = RouteGate.TryAdvance(plan, state, decision, registry, out var nextState, out var error);
        Assert.True(advanced);
        Assert.Null(error);

        advanced = RouteGate.TryAdvance(plan, nextState, null, registry, out var terminalState, out error);
        Assert.True(advanced);
        Assert.Null(error);

        advanced = RouteGate.TryAdvance(plan, terminalState, null, registry, out var finalState, out error);
        Assert.True(advanced);
        Assert.Null(error);
        Assert.Equal(RoutingStatus.Completed, finalState.Status);
    }

    private sealed class StubToolRegistry : IToolRegistry
    {
        public string CatalogHash => "stub";

        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => new List<ToolRegistryEntry>();

        public ToolRegistryEntry? GetTool(ToolId toolId) => null;

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => GetAllTools();
    }

    private sealed class SnapshotOnlyRegistry : IToolRegistry
    {
        private readonly List<ToolRegistryEntry> _snapshot;
        public bool LiveMissing { get; set; }
        public string CatalogHash => "snapshot";

        public SnapshotOnlyRegistry(params ToolSpec[] specs)
        {
            _snapshot = new List<ToolRegistryEntry>();
            foreach (var spec in specs)
                _snapshot.Add(new ToolRegistryEntry(spec));
        }

        public IReadOnlyList<ToolRegistryEntry> GetAllTools() => _snapshot;

        public ToolRegistryEntry? GetTool(ToolId toolId)
        {
            if (LiveMissing)
                return null;

            return _snapshot.Find(entry => entry.Spec.ToolId == toolId);
        }

        public IReadOnlyList<ToolRegistryEntry> GetSnapshot() => _snapshot;
    }

    private sealed class RecordingNarrator : IRuntimeNarrator
    {
        private readonly object _sync = new();
        private readonly List<string> _events = new();

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_sync)
                {
                    return _events.ToArray();
                }
            }
        }

        public void OnPlan(string text) => Add("plan");
        public void OnCommand(RuntimeCommandSpec command, RuntimeRequest request) => Add("command");
        public void OnResult(RuntimeResult result) => Add("result");
        public void OnError(RuntimeError error) => Add("error");
        public void OnRoute(RouteNarration narration) => Add("route");
        public void OnWorkOrderReceived(WorkOrder workOrder) => Add("workorder");
        public void OnRouteEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Add("entered");
        public void OnNodeEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Add("node.entered");
        public void OnDecisionRequired(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Add("decision.required");
        public void OnDecisionAccepted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Add("decision.accepted");
        public void OnDecisionGateWaiting(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, DecisionPolicy policy, FallbackToolSelection? fallbackToolSelection, string? fallbackNextNodeId) => Add("decision.gate.waiting");
        public void OnDecisionGateBypassed(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, DecisionPolicy policy, ToolSelectionDecision fallbackSelection, string nextNodeId) => Add($"decision.gate.bypassed:{nextNodeId}");
        public void OnDecisionGateRequiredError(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, DecisionPolicy policy, RuntimeError error) => Add("decision.gate.error");
        public void OnStepBudgetExceeded(RoutingState state, int stepBudget, RuntimeError error) => Add("budget.exceeded");
        public void OnNodeTransitionChosen(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId, RoutingDecisionSource decisionSource) => Add("node.transition");
        public void OnNodeAdvanced(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId, RoutingDecisionSource decisionSource) => Add("node.advanced");
        public void OnNodeHalted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, RuntimeError error) => Add("node.halted");
        public void OnHalted(RoutingState state, RuntimeError error) => Add("halted");
        public void OnCompleted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Add("completed");

        private void Add(string value)
        {
            lock (_sync)
            {
                _events.Add(value);
            }
        }
    }

    private static RouteIntentToken CreateIntentToken(BuildPlan plan)
    {
        var startRule = plan.Request.RouteRules.First(rule => rule.NodeKind == MermaidNodeKind.Start);
        return RouteIntentTokenFactory.Create(plan, startRule);
    }
}
