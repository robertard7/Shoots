using System;
using System.Linq;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public static class RouteGateNarrator
{
    public static void Notify(
        IRuntimeNarrator narrator,
        BuildPlan plan,
        RoutingState state,
        ToolSelectionDecision? decision,
        RoutingState nextState,
        RuntimeError? error)
    {
        if (narrator is null)
            throw new ArgumentNullException(nameof(narrator));
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (nextState is null)
            throw new ArgumentNullException(nameof(nextState));

        var workOrder = plan.Request.WorkOrder
            ?? throw new ArgumentException("work order is required", nameof(plan));

        if (state.CurrentRouteIndex == 0 && state.Status == RoutingStatus.Pending)
            narrator.OnWorkOrderReceived(workOrder);

        var step = plan.Steps.ElementAtOrDefault(state.CurrentRouteIndex) as RouteStep
            ?? throw new ArgumentException("route step is required", nameof(plan));

        narrator.OnRouteEntered(state, step);

        if (nextState.Status == RoutingStatus.Waiting)
            narrator.OnDecisionRequired(nextState, step);

        if (decision is not null && error is null)
            narrator.OnDecisionAccepted(nextState, step);

        if (error is not null)
            narrator.OnHalted(nextState, error);
    }
}
