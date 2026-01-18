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
);
