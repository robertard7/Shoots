using System;
using Shoots.Contracts.Core;
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
        var envelope = new ExecutionEnvelope(
            plan,
            result.State,
            result.ToolResults,
            result.Trace,
            result.Telemetry,
            _registry.CatalogHash,
            finalStatus);
        _persistence?.Save(envelope);
        return envelope;
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
}
