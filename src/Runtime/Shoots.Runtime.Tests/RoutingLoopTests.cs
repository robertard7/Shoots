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

    private sealed class RefusingAiDecisionProvider : IAiDecisionProvider
    {
        public RouteDecision? RequestDecision(AiDecisionRequest request) => null;
    }

    private sealed class AcceptingAiDecisionProvider : IAiDecisionProvider
    {
        public RouteDecision? RequestDecision(AiDecisionRequest request)
        {
            if (request.NodeKind != MermaidNodeKind.Tool && request.NodeKind != MermaidNodeKind.Start)
                return null;

            return new RouteDecision(
                null,
                new ToolSelectionDecision(new ToolId("tools.sample"), new Dictionary<string, object?>()));
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
}
