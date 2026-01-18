using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class RoutingLoop
{
    private readonly BuildPlan _plan;
    private readonly IToolRegistry _registry;
    private readonly IAiDecisionProvider _aiDecisionProvider;
    private readonly IRuntimeNarrator _narrator;
    private readonly IToolExecutor _toolExecutor;
    private readonly List<ToolResult> _toolResults = new();

    public RoutingState State { get; private set; }
    public IReadOnlyList<ToolResult> ToolResults => _toolResults;

    public RoutingLoop(
        BuildPlan plan,
        IToolRegistry registry,
        IAiDecisionProvider aiDecisionProvider,
        IRuntimeNarrator narrator,
        IToolExecutor toolExecutor)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _aiDecisionProvider = aiDecisionProvider ?? throw new ArgumentNullException(nameof(aiDecisionProvider));
        _narrator = narrator ?? throw new ArgumentNullException(nameof(narrator));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));

        if (_plan.Request.WorkOrder is null)
            throw new ArgumentException("work order is required", nameof(plan));

        State = RoutingState.CreateInitial(_plan);
    }

    public RoutingLoopResult Run()
    {
        var previousNarrator = RouteGate.Narrator;
        RouteGate.Narrator = _narrator;

        try
        {
            while (true)
            {
                var step = RequireRouteStep(_plan, State);
                var decision = ResolveDecision(step);

                if (State.Status == RoutingStatus.Waiting && decision is null)
                    break;

                var advanced = RouteGate.TryAdvance(_plan, State, decision, _registry, out var nextState, out _);
                State = nextState;

                if (!advanced)
                {
                    if (State.Status == RoutingStatus.Waiting)
                        continue;
                    break;
                }

                if (step.Intent == RouteIntent.SelectTool)
                {
                    var invocation = step.ToolInvocation
                                     ?? (decision is null
                                         ? null
                                         : new ToolInvocation(decision.ToolId, decision.Bindings, State.WorkOrderId));

                    if (invocation is not null)
                        _toolResults.Add(_toolExecutor.Execute(invocation));
                }

                if (State.Status == RoutingStatus.Completed || State.Status == RoutingStatus.Halted)
                    break;
            }
        }
        finally
        {
            RouteGate.Narrator = previousNarrator;
        }

        return new RoutingLoopResult(State, _toolResults.ToArray());
    }

    private ToolSelectionDecision? ResolveDecision(RouteStep step)
    {
        if (State.Status != RoutingStatus.Waiting)
            return null;

        return _aiDecisionProvider.RequestDecision(_plan.Request.WorkOrder!, step, State);
    }

    private static RouteStep RequireRouteStep(BuildPlan plan, RoutingState state)
    {
        if (plan.Steps is null || plan.Steps.Count == 0)
            throw new ArgumentException("route steps are required", nameof(plan));
        if (state.CurrentRouteIndex < 0 || state.CurrentRouteIndex >= plan.Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(state), "route index out of range");
        if (plan.Steps[state.CurrentRouteIndex] is not RouteStep routeStep)
            throw new ArgumentException("route step is required", nameof(plan));
        return routeStep;
    }
}
