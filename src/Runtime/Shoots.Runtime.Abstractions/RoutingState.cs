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
        var firstStep = ResolveFirstStep(plan);
        if (firstStep.Intent == RouteIntent.Terminate)
            throw new ArgumentException("first route step cannot be terminate", nameof(plan));

        return new RoutingState(
            plan.Request.WorkOrder.Id,
            0,
            firstStep.Intent,
            RoutingStatus.Pending);
    }

    /// <summary>
    /// Creates the canonical initial routing state from a work order and plan.
    /// </summary>
    public static RoutingState CreateInitial(WorkOrder workOrder, BuildPlan plan)
    {
        if (workOrder is null)
            throw new ArgumentNullException(nameof(workOrder));
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        var firstStep = ResolveFirstStep(plan);
        if (firstStep.Intent == RouteIntent.Terminate)
            throw new ArgumentException("first route step cannot be terminate", nameof(plan));

        return new RoutingState(
            workOrder.Id,
            0,
            firstStep.Intent,
            RoutingStatus.Pending);
    }

    private static RouteStep ResolveFirstStep(BuildPlan plan)
    {
        if (plan.Steps is null || plan.Steps.Count == 0)
            throw new ArgumentException("route steps are required", nameof(plan));
        if (plan.Steps[0] is not RouteStep firstStep)
            throw new ArgumentException("first step must be a route step", nameof(plan));
        return firstStep;
    }
}
