using System;
using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public enum RoutingStatus
{
    Pending,
    Waiting,
    Halted,
    Completed
}

/// <summary>
/// Immutable routing progress snapshot.
/// </summary>
public sealed record RoutingState(
    WorkOrderId WorkOrderId,
    int CurrentRouteIndex,
    RouteIntent CurrentRouteIntent,
    RoutingStatus Status
)
{
    /// <summary>
    /// Creates the canonical initial routing state for a plan.
    /// </summary>
    public static RoutingState CreateInitial(BuildPlan plan)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (plan.Request.WorkOrder is null)
            throw new ArgumentException("work order is required", nameof(plan));
        if (plan.Steps is null || plan.Steps.Count == 0)
            throw new ArgumentException("route steps are required", nameof(plan));
        if (plan.Steps[0] is not RouteStep firstStep)
            throw new ArgumentException("first step must be a route step", nameof(plan));

        return new RoutingState(
            plan.Request.WorkOrder.Id,
            0,
            firstStep.Intent,
            RoutingStatus.Pending);
    }
}
