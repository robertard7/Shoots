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
    void OnNodeEntered(RoutingState state, RouteStep step);
    void OnDecisionRequired(RoutingState state, RouteStep step);
    void OnDecisionAccepted(RoutingState state, RouteStep step);
    void OnNodeTransitionChosen(RoutingState state, RouteStep step, string nextNodeId);
    void OnNodeAdvanced(RoutingState state, RouteStep step, string nextNodeId);
    void OnNodeHalted(RoutingState state, RouteStep step, RuntimeError error);
    void OnHalted(RoutingState state, RuntimeError error);
    void OnCompleted(RoutingState state, RouteStep step);
}
