using System;
using System.Linq;
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
    RouteIntentToken IntentToken,
    string CurrentNodeId,
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
        var startRule = ResolveStartRule(plan);
        var firstStep = ResolveRouteStep(plan, startRule.NodeId);
        if (firstStep.Intent == RouteIntent.Terminate)
            throw new ArgumentException("first route step cannot be terminate", nameof(plan));

        var token = RouteIntentTokenFactory.Create(plan, startRule);

        return new RoutingState(
            plan.Request.WorkOrder.Id,
            token,
            startRule.NodeId,
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
        var startRule = ResolveStartRule(plan);
        var firstStep = ResolveRouteStep(plan, startRule.NodeId);
        if (firstStep.Intent == RouteIntent.Terminate)
            throw new ArgumentException("first route step cannot be terminate", nameof(plan));

        var token = RouteIntentTokenFactory.Create(plan, startRule);

        return new RoutingState(
            workOrder.Id,
            token,
            startRule.NodeId,
            firstStep.Intent,
            RoutingStatus.Pending);
    }

    private static RouteRule ResolveStartRule(BuildPlan plan)
    {
        if (plan.Request.RouteRules is null || plan.Request.RouteRules.Count == 0)
            throw new ArgumentException("route rules are required", nameof(plan));

        var startRules = plan.Request.RouteRules
            .Where(rule => rule.NodeKind == MermaidNodeKind.Start)
            .OrderBy(rule => rule.NodeId, StringComparer.Ordinal)
            .ToArray();
        if (startRules.Length != 1)
            throw new ArgumentException("exactly one start node is required", nameof(plan));

        return startRules[0];
    }

    private static RouteStep ResolveRouteStep(BuildPlan plan, string nodeId)
    {
        if (plan.Steps is null || plan.Steps.Count == 0)
            throw new ArgumentException("route steps are required", nameof(plan));

        var step = plan.Steps
            .OfType<RouteStep>()
            .FirstOrDefault(candidate => string.Equals(candidate.NodeId, nodeId, StringComparison.Ordinal));
        if (step is null)
            throw new ArgumentException($"route step '{nodeId}' is required", nameof(plan));

        return step;
    }
}
