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

        var artifacts = BuildArtifacts(plan, result.ToolResults);
        var finalStatus = ResolveFinalStatus(result.State);

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

        var provider = aiDecisionProvider
            ?? new BridgeAiDecisionProvider(
                ProviderRegistryFactory.CreateDefault(),
                "fake.local");

        var orchestrator = new RuntimeOrchestrator(
            registry,
            provider,
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

        if (!string.Equals(trace.CatalogHash, registry.CatalogHash, StringComparison.Ordinal))
        {
            return HaltFromTrace(
                trace,
                registry,
                Array.Empty<ToolResult>(),
                null,
                "catalog_hash_mismatch",
                "Catalog hash mismatch.");
        }

        var toolResults = RebuildToolResults(trace);
        var lastState = ResolveLastState(trace)
            ?? RoutingState.CreateInitial(trace.Plan);

        var loop = new RoutingLoop(
            trace.Plan,
            registry,
            aiDecisionProvider,
            NullRuntimeNarrator.Instance,
            new DeterministicToolExecutor(registry),
            lastState,
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

    private static IReadOnlyList<ToolResult> RebuildToolResults(RoutingTrace trace)
    {
        var results = new List<ToolResult>();
        ToolId? currentTool = null;

        foreach (var entry in trace.Entries)
        {
            if (entry.Event == RoutingTraceEventKind.ToolExecuted &&
                entry.Detail is not null)
            {
                currentTool = ParseToolExecutionDetail(entry.Detail);
                continue;
            }

            if (entry.Event != RoutingTraceEventKind.ToolResult ||
                entry.Detail is null ||
                currentTool is null)
            {
                continue;
            }

            var parsed = ParseToolResult(entry.Detail);

            results.Add(new ToolResult(
                currentTool.Value,
                parsed.Outputs,
                parsed.Success));

            currentTool = null;
        }

        return results;
    }

    private static ToolId? ParseToolExecutionDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return null;

        foreach (var segment in detail.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = segment.IndexOf('=', StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var key = segment[..idx].Trim();
            var value = segment[(idx + 1)..].Trim();

            if (string.Equals(key, "tool.id", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return new ToolId(value);
            }
        }

        return null;
    }

    private static (bool Success, IReadOnlyDictionary<string, object?> Outputs)
        ParseToolResult(string detail)
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
                bool.TryParse(parts[1], out success);
                continue;
            }

            outputs[parts[0]] = parts[1];
        }

        return (success, outputs);
    }

    private static RoutingState? ResolveLastState(RoutingTrace trace)
        => trace.Entries.LastOrDefault(e => e.State is not null)?.State;

    private static ExecutionFinalStatus ResolveFinalStatus(RoutingState state)
        => state.Status switch
        {
            RoutingStatus.Completed => ExecutionFinalStatus.Completed,
            RoutingStatus.Halted => ExecutionFinalStatus.Halted,
            _ => ExecutionFinalStatus.Aborted
        };

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

        builder.Add(
            RoutingTraceEventKind.Halted,
            detail: error.Code,
            state: state,
            error: error);

        var artifacts = BuildArtifacts(trace.Plan, toolResults);

        return new ExecutionEnvelope(
            trace.Plan,
            state,
            toolResults,
            artifacts,
            builder.Build(),
            builder.BuildTelemetry(),
            registry.CatalogHash,
            ExecutionFinalStatus.Halted);
    }

    private static IReadOnlyList<BuildArtifact> BuildArtifacts(
        BuildPlan plan,
        IReadOnlyList<ToolResult> toolResults)
    {
        var artifacts = new List<BuildArtifact>(plan.Artifacts);
        var index = 0;

        foreach (var result in toolResults)
        {
            foreach (var output in result.Outputs.OrderBy(o => o.Key))
            {
                artifacts.Add(new BuildArtifact(
                    $"{result.ToolId.Value}.output.{index}.{output.Key}",
                    $"Tool output {output.Key}={output.Value ?? "null"}"));

                index++;
            }
        }

        return artifacts;
    }
}
