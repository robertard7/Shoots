using System.Collections.Generic;
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
    void OnRouteEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes);
    void OnNodeEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes);
    void OnDecisionRequired(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes);
    void OnDecisionAccepted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes);
    void OnNodeTransitionChosen(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId, RoutingDecisionSource decisionSource);
    void OnNodeAdvanced(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId, RoutingDecisionSource decisionSource);
    void OnNodeHalted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, RuntimeError error);
    void OnHalted(RoutingState state, RuntimeError error);
    void OnCompleted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes);
}
