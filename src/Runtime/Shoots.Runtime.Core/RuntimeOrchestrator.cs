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

    public RuntimeOrchestrator(
        IToolRegistry registry,
        IAiDecisionProvider aiDecisionProvider,
        IRuntimeNarrator narrator,
        IToolExecutor toolExecutor)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _aiDecisionProvider = aiDecisionProvider ?? throw new ArgumentNullException(nameof(aiDecisionProvider));
        _narrator = narrator ?? throw new ArgumentNullException(nameof(narrator));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
    }

    public RoutingLoopResult Run(BuildPlan plan)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        var loop = new RoutingLoop(plan, _registry, _aiDecisionProvider, _narrator, _toolExecutor);
        return loop.Run();
    }
}
