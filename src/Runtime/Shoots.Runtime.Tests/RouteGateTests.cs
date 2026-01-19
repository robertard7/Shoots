using System;
using System.Collections.Generic;
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
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var state = new RoutingState(
            new WorkOrderId("wo-other"),
            CreateIntentToken(plan),
            0,
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
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);

        var result = RouteGate.TryAdvance(plan, state, null, new StubToolRegistry(), out var nextState, out var error);

        Assert.False(result);
        Assert.Null(error);
        Assert.Equal(RoutingStatus.Waiting, nextState.Status);
        Assert.Equal(state.CurrentRouteIndex, nextState.CurrentRouteIndex);
    }

    [Fact]
    public void TryAdvance_halts_on_decision_for_non_select_tool()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);
        var decision = new RouteDecision(
            "terminate",
            state.IntentToken,
            RouteIntent.Validate,
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
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Linear, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var narrator = new RecordingNarrator();
        RouteGate.Narrator = narrator;

        try
        {
            var registry = new SnapshotOnlyRegistry(toolSpec);
            var state = RoutingState.CreateInitial(plan);

            var decision = new RouteDecision(
                "validate",
                state.IntentToken,
                RouteIntent.SelectTool,
                new ToolSelectionDecision(toolSpec.ToolId, new Dictionary<string, object?>()));
            var advanced = RouteGate.TryAdvance(plan, state, decision, registry, out var nextState, out var error);

            Assert.True(advanced);
            Assert.Null(error);
            Assert.Equal(1, nextState.CurrentRouteIndex);

            advanced = RouteGate.TryAdvance(plan, nextState, null, registry, out var finalState, out error);
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
    public void TryAdvance_halts_on_decision_too_early()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);
        var decision = new RouteDecision(
            "terminate",
            state.IntentToken,
            RouteIntent.Validate,
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
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Linear, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var lateStep = plan.Steps[1] as RouteStep ?? throw new InvalidOperationException("second step must be a route step");
        var lateToken = RouteIntentTokenFactory.Create(plan.Request.WorkOrder, lateStep);
        var lateState = new RoutingState(
            plan.Request.WorkOrder.Id,
            lateToken,
            1,
            RouteIntent.Validate,
            RoutingStatus.Pending);
        var decision = new RouteDecision(
            "terminate",
            lateState.IntentToken,
            RouteIntent.SelectTool,
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
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var registry = new SnapshotOnlyRegistry(toolSpec)
        {
            LiveMissing = true
        };
        var state = RoutingState.CreateInitial(plan);
        var decision = new RouteDecision(
            "terminate",
            state.IntentToken,
            RouteIntent.SelectTool,
            new ToolSelectionDecision(toolSpec.ToolId, new Dictionary<string, object?>()));

        var result = RouteGate.TryAdvance(plan, state, decision, registry, out var nextState, out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.Equal(1, nextState.CurrentRouteIndex);
    }

    [Fact]
    public void TryAdvance_emits_halt_narration()
    {
        var plan = CreatePlan(
            new WorkOrderId("wo-plan"),
            new[]
            {
                new RouteRule("validate", RouteIntent.Validate, DecisionOwner.Runtime, "validation", MermaidNodeKind.Start, new[] { "terminate" }),
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var narrator = new RecordingNarrator();
        RouteGate.Narrator = narrator;

        try
        {
            var state = new RoutingState(
                new WorkOrderId("wo-other"),
                CreateIntentToken(plan),
                0,
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
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var registry = new SnapshotOnlyRegistry(toolSpec);
        var state = RoutingState.CreateInitial(plan);

        var decision = new RouteDecision(
            "terminate",
            state.IntentToken,
            RouteIntent.SelectTool,
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
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var state = RoutingState.CreateInitial(plan);
        var decision = new RouteDecision(
            "terminate",
            state.IntentToken,
            RouteIntent.SelectTool,
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
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            });

        var state = new RoutingState(
            new WorkOrderId("wo-other"),
            CreateIntentToken(plan),
            0,
            RouteIntent.SelectTool,
            RoutingStatus.Pending);
        var decision = new RouteDecision(
            "terminate",
            state.IntentToken,
            RouteIntent.SelectTool,
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
                new RouteRule("terminate", RouteIntent.Terminate, DecisionOwner.Rule, "termination", MermaidNodeKind.Terminate, Array.Empty<string>())
            },
            toolResult);

        var state = RoutingState.CreateInitial(plan);

        var result = RouteGate.TryAdvance(plan, state, null, new SnapshotOnlyRegistry(), out var nextState, out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.Equal(1, nextState.CurrentRouteIndex);
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

        var authority = new DelegationAuthority(
            ProviderId: new ProviderId("local"),
            Kind: ProviderKind.Local,
            PolicyId: "local-only",
            AllowsDelegation: false);

        return new BuildPlan(
            "plan",
            request,
            authority,
            steps,
            new[] { new BuildArtifact("plan.json", "Plan payload.") },
            toolResult);
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
        public List<string> Events { get; } = new();

        public void OnPlan(string text) => Events.Add("plan");
        public void OnCommand(RuntimeCommandSpec command, RuntimeRequest request) => Events.Add("command");
        public void OnResult(RuntimeResult result) => Events.Add("result");
        public void OnError(RuntimeError error) => Events.Add("error");
        public void OnRoute(RouteNarration narration) => Events.Add("route");
        public void OnWorkOrderReceived(WorkOrder workOrder) => Events.Add("workorder");
        public void OnRouteEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Events.Add("entered");
        public void OnNodeEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Events.Add("node.entered");
        public void OnDecisionRequired(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Events.Add("decision.required");
        public void OnDecisionAccepted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Events.Add("decision.accepted");
        public void OnNodeTransitionChosen(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId) => Events.Add("node.transition");
        public void OnNodeAdvanced(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId) => Events.Add("node.advanced");
        public void OnNodeHalted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, RuntimeError error) => Events.Add("node.halted");
        public void OnHalted(RoutingState state, RuntimeError error) => Events.Add("halted");
        public void OnCompleted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes) => Events.Add("completed");
    }

    private static RouteIntentToken CreateIntentToken(BuildPlan plan)
    {
        var originStep = plan.Steps[0] as RouteStep ?? throw new InvalidOperationException("first step must be a route step");
        return RouteIntentTokenFactory.Create(plan.Request.WorkOrder, originStep);
    }
}
