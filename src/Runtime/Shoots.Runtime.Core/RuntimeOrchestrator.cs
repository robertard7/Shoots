using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.Providers.Bridge;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class RuntimeOrchestrator
{
    private readonly IToolRegistry _registry;
    private readonly IAiDecisionProvider _aiDecisionProvider;
    private readonly IRuntimeNarrator _narrator;
    private readonly IToolExecutor _toolExecutor;
    private readonly IRuntimePersistence? _persistence;

    public RuntimeOrchestrator(
        IToolRegistry registry,
        IAiDecisionProvider aiDecisionProvider,
        IRuntimeNarrator narrator,
        IToolExecutor toolExecutor,
        IRuntimePersistence? persistence = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _aiDecisionProvider = aiDecisionProvider ?? throw new ArgumentNullException(nameof(aiDecisionProvider));
        _narrator = narrator ?? throw new ArgumentNullException(nameof(narrator));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _persistence = persistence;
    }

    public ExecutionEnvelope Run(BuildPlan plan)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        var seed = _persistence?.Load(plan.PlanId);
        if (seed is not null && seed.State.Status is RoutingStatus.Completed or RoutingStatus.Halted)
            return seed;

        var loop = new RoutingLoop(
            plan,
            _registry,
            _aiDecisionProvider,
            _narrator,
            _toolExecutor,
            seed?.State,
            seed?.ToolResults,
            seed?.Trace);
        var result = loop.Run();
        var finalStatus = ResolveFinalStatus(result.State);
        var artifacts = BuildArtifacts(plan, result.ToolResults);
        var envelope = new ExecutionEnvelope(
            plan,
            result.State,
            result.ToolResults,
            artifacts,
            result.Trace,
            result.Telemetry,
            _registry.CatalogHash,
            finalStatus);
        _persistence?.Save(envelope);
        return envelope;
    }

    public static ExecutionEnvelope Run(
        BuildPlan plan,
        IToolRegistry registry,
        IAiDecisionProvider? aiDecisionProvider = null,
        IRuntimePersistence? persistence = null,
        IRuntimeNarrator? narrator = null)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (registry is null)
            throw new ArgumentNullException(nameof(registry));
        var resolvedAiDecisionProvider = aiDecisionProvider
            ?? new BridgeAiDecisionProvider(ProviderRegistryFactory.CreateDefault(), "fake.local");

        var orchestrator = new RuntimeOrchestrator(
            registry,
            resolvedAiDecisionProvider,
            narrator ?? NullRuntimeNarrator.Instance,
            new DeterministicToolExecutor(registry),
            persistence);

        return orchestrator.Run(plan);
    }

    public static ExecutionEnvelope Resume(
        RoutingTrace trace,
        IToolRegistry registry,
        IAiDecisionProvider aiDecisionProvider)
    {
        if (trace is null)
            throw new ArgumentNullException(nameof(trace));
        if (registry is null)
            throw new ArgumentNullException(nameof(registry));
        if (aiDecisionProvider is null)
            throw new ArgumentNullException(nameof(aiDecisionProvider));

        var toolResults = RebuildToolResults(trace);
        var lastState = ResolveLastState(trace);

        if (!string.Equals(trace.CatalogHash, registry.CatalogHash, StringComparison.Ordinal))
            return HaltFromTrace(trace, registry, toolResults, lastState, "invalid_arguments", "Catalog hash mismatch.");

        var computed = Shoots.Runtime.Abstractions.BuildPlanHasher.ComputePlanId(
            trace.Plan.Request,
            trace.Plan.Authority,
            trace.Plan.Steps,
            trace.Plan.Artifacts,
            trace.Plan.ToolResult);
        if (!string.Equals(computed, trace.Plan.PlanId, StringComparison.Ordinal))
            return HaltFromTrace(trace, registry, toolResults, lastState, "invalid_arguments", "Plan hash mismatch.");

        var resumeState = ResolveLastNonTerminalState(trace) ?? lastState ?? RoutingState.CreateInitial(trace.Plan);

        if (string.IsNullOrWhiteSpace(resumeState.IntentToken.WorkOrderId.Value) ||
            string.IsNullOrWhiteSpace(resumeState.IntentToken.CurrentNodeId) ||
            resumeState.IntentToken.AllowedNextNodeIds is null ||
            string.IsNullOrWhiteSpace(resumeState.IntentToken.GraphStructureHash))
        {
            return HaltFromTrace(trace, registry, toolResults, resumeState, "route_intent_token_missing", "Intent token is required.");
        }

        if (trace.Plan.Request.WorkOrder is null)
            return HaltFromTrace(trace, registry, toolResults, resumeState, "route_workorder_missing", "Work order is required for routing.");

        if (string.IsNullOrWhiteSpace(trace.Plan.GraphStructureHash) ||
            string.IsNullOrWhiteSpace(trace.Plan.NodeSetHash) ||
            string.IsNullOrWhiteSpace(trace.Plan.EdgeSetHash))
        {
            return HaltFromTrace(
                trace,
                registry,
                toolResults,
                resumeState,
                "route_graph_hash_missing",
                "Graph hashes are required to resume routing.");
        }

        if (!string.Equals(resumeState.IntentToken.GraphStructureHash, trace.Plan.GraphStructureHash, StringComparison.Ordinal))
        {
            return HaltFromTrace(
                trace,
                registry,
                toolResults,
                resumeState,
                "route_graph_hash_mismatch",
                "Graph hash mismatch.");
        }

        if (string.IsNullOrWhiteSpace(resumeState.CurrentNodeId))
        {
            return HaltFromTrace(
                trace,
                registry,
                toolResults,
                resumeState,
                "route_node_missing",
                "Current node id is required to resume routing.");
        }

        var resumeStep = ResolveRouteStep(trace.Plan, resumeState.CurrentNodeId);
        if (resumeStep is null)
        {
            return HaltFromTrace(
                trace,
                registry,
                toolResults,
                resumeState,
                "route_node_missing",
                $"Route node '{resumeState.CurrentNodeId}' is not present in the plan.");
        }
        var resumeRule = trace.Plan.Request.RouteRules
            .FirstOrDefault(rule => string.Equals(rule.NodeId, resumeStep.NodeId, StringComparison.Ordinal));
        if (resumeRule is null)
        {
            return HaltFromTrace(
                trace,
                registry,
                toolResults,
                resumeState,
                "route_rule_missing",
                "Route rule is missing for the current node.");
        }

        var expectedToken = RouteIntentTokenFactory.Create(trace.Plan, resumeRule);
        var expectedHash = RouteIntentTokenFactory.ComputeTokenHash(expectedToken);
        var actualHash = RouteIntentTokenFactory.ComputeTokenHash(resumeState.IntentToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            return HaltFromTrace(
                trace,
                registry,
                toolResults,
                resumeState,
                "route_intent_token_mismatch",
                "Intent token hash mismatch.");
        }

        if (trace.Plan.Request.RouteRules is null)
        {
            return HaltFromTrace(
                trace,
                registry,
                toolResults,
                resumeState,
                "route_rules_missing",
                "Route rules are required to resume routing.");
        }

        if (!trace.Plan.Request.RouteRules.Any(rule => string.Equals(rule.NodeId, resumeStep.NodeId, StringComparison.Ordinal)))
        {
            return HaltFromTrace(
                trace,
                registry,
                toolResults,
                resumeState,
                "route_node_missing",
                $"Route node '{resumeStep.NodeId}' is not present in the Mermaid graph.");
        }

        var loop = new RoutingLoop(
            trace.Plan,
            registry,
            aiDecisionProvider,
            NullRuntimeNarrator.Instance,
            new DeterministicToolExecutor(registry),
            resumeState,
            toolResults,
            trace);

        var result = loop.Run();
        var artifacts = BuildArtifacts(trace.Plan, result.ToolResults);
        var finalStatus = ResolveFinalStatus(result.State);

        return new ExecutionEnvelope(
            trace.Plan,
            result.State,
            result.ToolResults,
            artifacts,
            result.Trace,
            result.Telemetry,
            registry.CatalogHash,
            finalStatus);
    }

    private static RouteStep? ResolveRouteStep(BuildPlan plan, string nodeId)
    {
        if (plan.Steps is null || plan.Steps.Count == 0)
            throw new ArgumentException("route steps are required", nameof(plan));
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("route node id is required", nameof(nodeId));

        return plan.Steps
            .OfType<RouteStep>()
            .FirstOrDefault(candidate => string.Equals(candidate.NodeId, nodeId, StringComparison.Ordinal));
    }

    private static ExecutionFinalStatus ResolveFinalStatus(RoutingState state)
    {
        return state.Status switch
        {
            RoutingStatus.Completed => ExecutionFinalStatus.Completed,
            RoutingStatus.Halted => ExecutionFinalStatus.Halted,
            _ => ExecutionFinalStatus.Aborted
        };
    }

    private static IReadOnlyList<ToolResult> RebuildToolResults(RoutingTrace trace)
    {
        var results = new List<ToolResult>();
        ToolId? pendingTool = null;

        foreach (var entry in trace.Entries)
        {
            if (entry.Event == RoutingTraceEventKind.ToolExecuted && entry.Detail is not null)
            {
                pendingTool = ParseToolExecutionDetail(entry.Detail);
                continue;
            }

            if (entry.Event != RoutingTraceEventKind.ToolResult || pendingTool is null || entry.Detail is null)
                continue;

            var parsed = ParseToolResult(entry.Detail);
            results.Add(new ToolResult(pendingTool, parsed.Outputs, parsed.Success));
            pendingTool = null;
        }

        return results;
    }

    private static ToolId? ParseToolExecutionDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return null;

        if (!detail.Contains('=', StringComparison.Ordinal))
            return new ToolId(detail);

        foreach (var token in detail.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            if (string.Equals(parts[0], "tool.id", StringComparison.OrdinalIgnoreCase))
                return new ToolId(parts[1]);
        }

        return null;
    }

    private static (bool Success, IReadOnlyDictionary<string, object?> Outputs) ParseToolResult(string detail)
    {
        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var success = false;

        foreach (var token in detail.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            if (string.Equals(parts[0], "success", StringComparison.OrdinalIgnoreCase))
            {
                _ = bool.TryParse(parts[1], out success);
                continue;
            }

            outputs[parts[0]] = parts[1];
        }

        return (success, outputs);
    }

    private static RoutingState? ResolveLastState(RoutingTrace trace)
    {
        return trace.Entries.LastOrDefault(entry => entry.State is not null)?.State;
    }

    private static RoutingState? ResolveLastNonTerminalState(RoutingTrace trace)
    {
        return trace.Entries
            .Where(entry => entry.State is not null)
            .Select(entry => entry.State!)
            .LastOrDefault(state => state.Status is not RoutingStatus.Completed and not RoutingStatus.Halted);
    }

    private static ExecutionEnvelope HaltFromTrace(
        RoutingTrace trace,
        IToolRegistry registry,
        IReadOnlyList<ToolResult> toolResults,
        RoutingState? seedState,
        string code,
        string message)
    {
        var state = seedState ?? RoutingState.CreateInitial(trace.Plan);
        state = state with { Status = RoutingStatus.Halted };

        var builder = new RoutingTraceBuilder(trace.Plan, registry.CatalogHash, trace);
        var error = new RuntimeError(code, message);
        builder.Add(RoutingTraceEventKind.Halted, error.Code, state, error: error);

        var artifacts = BuildArtifacts(trace.Plan, toolResults);
        var envelope = new ExecutionEnvelope(
            trace.Plan,
            state,
            toolResults,
            artifacts,
            builder.Build(),
            builder.BuildTelemetry(),
            registry.CatalogHash,
            ExecutionFinalStatus.Halted);

        return envelope;
    }

    private static IReadOnlyList<BuildArtifact> BuildArtifacts(BuildPlan plan, IReadOnlyList<ToolResult> toolResults)
    {
        var artifacts = new List<BuildArtifact>(plan.Artifacts);
        var index = 0;

        foreach (var result in toolResults)
        {
            foreach (var output in result.Outputs
                         .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                var id = $"{result.ToolId.Value}.output.{index}.{output.Key}";
                var description = $"Tool output {output.Key}={output.Value?.ToString() ?? "null"}";
                artifacts.Add(new BuildArtifact(id, description));
                index++;
            }
        }

        return artifacts;
    }
}
