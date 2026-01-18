using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public interface IAiDecisionProvider
{
    ToolSelectionDecision? RequestDecision(
        WorkOrder workOrder,
        RouteStep step,
        RoutingState state);
}
