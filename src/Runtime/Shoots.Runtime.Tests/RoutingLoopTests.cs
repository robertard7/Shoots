using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;
using Shoots.Runtime.Core;
using Xunit;

namespace Shoots.Runtime.Tests;

public sealed class RoutingLoopTests
{
    [Fact]
    public void Ai_refusal_halts_routing()
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

        var plan = new BuildPlan(
            "plan",
            request,
            HashTools.ComputeSha256Hash("graph"),
            HashTools.ComputeSha256Hash("nodes"),
            HashTools.ComputeSha256Hash("edges"),
            new DelegationAuthority(
                ProviderId: new ProviderId("local"),
                Kind: ProviderKind.Local,
                PolicyId: "local-only",
                AllowsDelegation: false),
            steps,
            new[] { new BuildArtifact("plan.json", "Plan payload.") });

        var loop = new RoutingLoop(
            plan,
            new EmptyToolRegistry(),
            new RefusingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullToolExecutor());

        var result = loop.Run();

        Assert.Equal(RoutingStatus.Halted, result.State.Status);
        Assert.Equal("select", result.State.CurrentNodeId);
        Assert.Empty(result.ToolResults);
        Assert.Empty(result.Telemetry);
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

        var plan = new BuildPlan(
            "plan",
            request,
            HashTools.ComputeSha256Hash("graph"),
            HashTools.ComputeSha256Hash("nodes"),
            HashTools.ComputeSha256Hash("edges"),
            new DelegationAuthority(
                ProviderId: new ProviderId("local"),
                Kind: ProviderKind.Local,
                PolicyId: "local-only",
                AllowsDelegation: false),
            steps,
            new[] { new BuildArtifact("plan.json", "Plan payload.") });

        var loop = new RoutingLoop(
            plan,
            new SampleToolRegistry(),
            new AcceptingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new DeterministicToolExecutor(new SampleToolRegistry()));
        var first = loop.Run();

        var replay = new RoutingLoop(
            plan,
            new SampleToolRegistry(),
            new AcceptingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new DeterministicToolExecutor(new SampleToolRegistry()),
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

        var plan = new BuildPlan(
            "plan",
            request,
            HashTools.ComputeSha256Hash("graph"),
            HashTools.ComputeSha256Hash("nodes"),
            HashTools.ComputeSha256Hash("edges"),
            new DelegationAuthority(
                ProviderId: new ProviderId("local"),
                Kind: ProviderKind.Local,
                PolicyId: "local-only",
                AllowsDelegation: false),
            steps,
            new[] { new BuildArtifact("plan.json", "Plan payload.") });

        var first = new RoutingLoop(
            plan,
            new SampleToolRegistry(),
            new ToolDecisionProvider(new ToolId("tools.sample")),
            NullRuntimeNarrator.Instance,
            new DeterministicToolExecutor(new SampleToolRegistry()))
            .Run();

        var second = new RoutingLoop(
            plan,
            new AlternateToolRegistry(),
            new ToolDecisionProvider(new ToolId("tools.other")),
            NullRuntimeNarrator.Instance,
            new DeterministicToolExecutor(new AlternateToolRegistry()))
            .Run();

        var firstPath = first.Trace.Entries
            .Where(entry => entry.Event == RoutingTraceEventKind.NodeAdvanced)
            .Select(entry => (entry.FromNodeId, entry.ToNodeId))
            .ToArray();
        var secondPath = second.Trace.Entries
            .Where(entry => entry.Event == RoutingTraceEventKind.NodeAdvanced)
            .Select(entry => (entry.FromNodeId, entry.ToNodeId))
            .ToArray();

        Assert.Equal(firstPath, secondPath);
        Assert.Equal(RoutingStatus.Completed, first.State.Status);
        Assert.Equal(RoutingStatus.Completed, second.State.Status);
        Assert.Equal("terminate", first.State.CurrentNodeId);
        Assert.Equal("terminate", second.State.CurrentNodeId);
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

        var plan = new BuildPlan(
            "plan",
            request,
            HashTools.ComputeSha256Hash("graph"),
            HashTools.ComputeSha256Hash("nodes"),
            HashTools.ComputeSha256Hash("edges"),
            new DelegationAuthority(
                ProviderId: new ProviderId("local"),
                Kind: ProviderKind.Local,
                PolicyId: "local-only",
                AllowsDelegation: false),
            steps,
            new[] { new BuildArtifact("plan.json", "Plan payload.") });

        var loop = new RoutingLoop(
            plan,
            new EmptyToolRegistry(),
            new ThrowingAiDecisionProvider(),
            NullRuntimeNarrator.Instance,
            new NullToolExecutor());

        var result = loop.Run();

        Assert.Equal(RoutingStatus.Completed, result.State.Status);
        Assert.Equal("terminate", result.State.CurrentNodeId);
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
