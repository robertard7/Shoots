using System;
using System.Collections.Generic;
using System.Linq;
using Shoots.Contracts.Core;
using Shoots.ProviderAdapters.Bridge;
using Shoots.ProviderAdapters.Abstractions;
using Shoots.ProviderAdapters.Null;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class RuntimeOrchestrator
{
    private readonly IToolRegistry _registry;
    private readonly IAiDecisionProvider _aiDecisionProvider;
    private readonly IRuntimeNarrator _narrator;
    private readonly IProviderClient _providerClient;
    private readonly IRuntimePersistence? _persistence;
    private readonly IRunResumeStateStore? _runStateStore;

    public RuntimeOrchestrator(
        IToolRegistry registry,
        IAiDecisionProvider aiDecisionProvider,
        IRuntimeNarrator narrator,
        IProviderClient providerClient,
        IRuntimePersistence? persistence = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _aiDecisionProvider = aiDecisionProvider ?? throw new ArgumentNullException(nameof(aiDecisionProvider));
        _narrator = narrator ?? throw new ArgumentNullException(nameof(narrator));
        _providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
        _persistence = persistence;
        _runStateStore = persistence as IRunResumeStateStore;
    }

    public ExecutionEnvelope Run(BuildPlan plan, RuntimeRunOptions? options = null)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        var resolvedOptions = options ?? new RuntimeRunOptions();
        var currentPlanHash = ResolvePlanHash(plan);
        var seed = _persistence?.Load(currentPlanHash);
        var workOrderId = plan.Request.WorkOrder?.Id.Value ?? string.Empty;
        var runState = _runStateStore?.LoadByWorkOrderId(workOrderId);

        if (seed is null && !string.IsNullOrWhiteSpace(runState?.LastPlanHash))
            seed = _persistence?.Load(runState.LastPlanHash);
        var discardedWaiting = resolvedOptions.ResumeMode == ResumeMode.DiscardWaitingStartOver && seed?.State.Status == RoutingStatus.Waiting;

        if (resolvedOptions.ResumeMode == ResumeMode.DiscardWaitingStartOver || resolvedOptions.DiscardWaiting)
        {
            if (seed is not null)
                seed = null;
            if (runState is not null)
                runState = null;
        }

        var blocked = TryBuildBlockedWaitingEnvelope(plan, seed, runState, resolvedOptions);
        if (blocked is not null)
            return blocked;

        if (seed is not null && seed.State.Status is RoutingStatus.Completed or RoutingStatus.Halted)
            return seed;

        var loop = new RoutingLoop(
            plan,
            _registry,
            _aiDecisionProvider,
            _narrator,
            _providerClient,
            seed?.State,
            seed?.ToolResults,
            trace: null);

        var result = loop.Run();

        var artifacts = BuildArtifacts(plan, result.ToolResults);
        var finalStatus = ResolveFinalStatus(result.State);

        var trace = discardedWaiting
            ? AppendHostEvent(result.Trace, RoutingTraceEventKind.HostResumeDiscardWaiting, "host.resume.discard_waiting")
            : result.Trace;

        var envelope = new ExecutionEnvelope(
            plan,
            result.State,
            result.ToolResults,
            artifacts,
            trace,
            result.Telemetry,
            _registry.CatalogHash,
            finalStatus,
            result.Waiting);

        _persistence?.Save(envelope);
        SaveRunState(envelope, resolvedOptions, runState);
        return envelope;
    }

    public static ExecutionEnvelope Run(
        BuildPlan plan,
        IToolRegistry registry,
        IAiDecisionProvider? aiDecisionProvider = null,
        IRuntimePersistence? persistence = null,
        IRuntimeNarrator? narrator = null,
        RuntimeRunOptions? options = null)
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
            new NullProviderClient(),
            persistence);

        return orchestrator.Run(plan, options);
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
            new NullProviderClient(),
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
            finalStatus,
            result.Waiting);
    }


    private ExecutionEnvelope? TryBuildBlockedWaitingEnvelope(
        BuildPlan plan,
        ExecutionEnvelope? seed,
        RunResumeState? runState,
        RuntimeRunOptions options)
    {
        if (seed?.State.Status != RoutingStatus.Waiting || seed.Waiting is null || runState is null)
            return null;

        if (runState.LastOutcomeKind != RunOutcomeKind.Waiting)
            return null;

        if (!string.Equals(runState.WorkOrderId, plan.Request.WorkOrder?.Id.Value, StringComparison.Ordinal))
            return null;

        if (options.ResumeMode == ResumeMode.InjectDecision &&
            !string.IsNullOrWhiteSpace(options.InjectedDecisionDigest) &&
            !string.Equals(options.InjectedDecisionDigest, runState.LastInjectedDecisionDigest, StringComparison.Ordinal))
        {
            return null;
        }

        if (runState.ProgressToken != ComputeProgressToken(seed) && options.ResumeMode == ResumeMode.None && !options.DiscardWaiting && !options.AllowPlanChangeOverride)
            return null;

        var currentPlanHash = ResolvePlanHash(plan);
        var planChanged =
            !string.Equals(seed.Waiting.PlanHash, currentPlanHash, StringComparison.Ordinal) ||
            !string.Equals(runState.LastPlanHash, currentPlanHash, StringComparison.Ordinal);

        if (planChanged)
        {
            if (options.ResumeMode == ResumeMode.OverridePlanChange || options.AllowPlanChangeOverride)
            {
                var overrideTrace = AppendHostEvent(seed.Trace, RoutingTraceEventKind.HostResumeOverridePlanChange, "host.resume.override_plan_change");
                return seed with { Trace = overrideTrace };
            }

            var waiting = seed.Waiting with { ReasonCode = "plan_changed_requires_explicit_resume" };
            var blockedPlanTrace = AppendHostEvent(seed.Trace, RoutingTraceEventKind.HostBlockedRerunWaiting, waiting.ReasonCode);
            return seed with { Waiting = waiting, Trace = blockedPlanTrace };
        }

        var blockedTrace = AppendHostEvent(seed.Trace, RoutingTraceEventKind.HostBlockedRerunWaiting, "decision_required");
        return seed with { Trace = blockedTrace };
    }

    private void SaveRunState(ExecutionEnvelope envelope, RuntimeRunOptions options, RunResumeState? priorState)
    {
        if (_runStateStore is null)
            return;

        var outcome = envelope.State.Status switch
        {
            RoutingStatus.Completed => RunOutcomeKind.Completed,
            RoutingStatus.Halted => RunOutcomeKind.Halted,
            _ => RunOutcomeKind.Waiting
        };

        var progressToken = ComputeProgressToken(envelope);

        var state = new RunResumeState(
            envelope.State.WorkOrderId.Value,
            outcome,
            envelope.Waiting,
            options.InjectedDecisionDigest,
            ResolvePlanHash(envelope.Plan),
            RouteIntentTokenFactory.ComputeTokenHash(envelope.State.IntentToken),
            (priorState?.AttemptCounter ?? 0) + 1,
            progressToken);

        _runStateStore.SaveByWorkOrderId(envelope.State.WorkOrderId.Value, state);
    }

    private static int ComputeProgressToken(ExecutionEnvelope envelope)
    {
        var advanced = envelope.Trace.Entries.Count(entry =>
            entry.Event == RoutingTraceEventKind.NodeAdvanced ||
            entry.Event == RoutingTraceEventKind.ToolResult ||
            entry.Event == RoutingTraceEventKind.DecisionAccepted ||
            entry.Event == RoutingTraceEventKind.Completed);

        return advanced;
    }

    private static string ResolvePlanHash(BuildPlan plan)
    {
        return BuildPlanIdentity.ComputePlanHash(plan);
    }

    private static RoutingTrace AppendHostEvent(RoutingTrace trace, RoutingTraceEventKind eventKind, string detail)
    {
        var nextTick = trace.Entries.Count == 0 ? 0 : trace.Entries[^1].Tick + 1;
        var entries = trace.Entries.Concat(new[]
        {
            new RoutingTraceEntry(nextTick, eventKind, detail)
        }).ToArray();

        return trace with { Entries = entries };
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
        state = state.WithStatus(RoutingStatus.Halted);

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
