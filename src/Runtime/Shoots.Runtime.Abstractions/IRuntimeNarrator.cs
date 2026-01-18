using Shoots.Contracts.Core;

namespace Shoots.Runtime.Abstractions;

public interface IRuntimeNarrator
{
    void OnPlan(string text);
    void OnCommand(RuntimeCommandSpec command, RuntimeRequest request);
    void OnResult(RuntimeResult result);
    void OnError(RuntimeError error);
    void OnRoute(RouteNarration narration);
    void OnWorkOrderReceived(WorkOrder workOrder);
    void OnRouteEntered(RoutingState state, RouteStep step);
    void OnDecisionRequired(RoutingState state, RouteStep step);
    void OnDecisionAccepted(RoutingState state, RouteStep step);
    void OnHalted(RoutingState state, RuntimeError error);
    void OnCompleted(RoutingState state, RouteStep step);
}
