using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

public sealed class NullRuntimeNarrator : IRuntimeNarrator
{
    public static readonly NullRuntimeNarrator Instance = new();

    public NullRuntimeNarrator()
    {
    }

    public void OnPlan(string text)
    {
        // Intentionally no-op
    }

    public void OnCommand(RuntimeCommandSpec command, RuntimeRequest request)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));
        if (request is null) throw new ArgumentNullException(nameof(request));
    }

    public void OnResult(RuntimeResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
    }

    public void OnError(RuntimeError error)
    {
        if (error is null) throw new ArgumentNullException(nameof(error));
    }

    public void OnRoute(RouteNarration narration)
    {
        if (narration is null) throw new ArgumentNullException(nameof(narration));
    }

    public void OnWorkOrderReceived(WorkOrder workOrder)
    {
        if (workOrder is null) throw new ArgumentNullException(nameof(workOrder));
    }

    public void OnRouteEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (intentToken is null) throw new ArgumentNullException(nameof(intentToken));
        if (allowedNextNodes is null) throw new ArgumentNullException(nameof(allowedNextNodes));
    }

    public void OnNodeEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (intentToken is null) throw new ArgumentNullException(nameof(intentToken));
        if (allowedNextNodes is null) throw new ArgumentNullException(nameof(allowedNextNodes));
    }

    public void OnDecisionRequired(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (intentToken is null) throw new ArgumentNullException(nameof(intentToken));
        if (allowedNextNodes is null) throw new ArgumentNullException(nameof(allowedNextNodes));
    }

    public void OnDecisionAccepted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (intentToken is null) throw new ArgumentNullException(nameof(intentToken));
        if (allowedNextNodes is null) throw new ArgumentNullException(nameof(allowedNextNodes));
    }

    public void OnNodeTransitionChosen(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId, RoutingDecisionSource decisionSource)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (intentToken is null) throw new ArgumentNullException(nameof(intentToken));
        if (allowedNextNodes is null) throw new ArgumentNullException(nameof(allowedNextNodes));
        if (nextNodeId is null) throw new ArgumentNullException(nameof(nextNodeId));
    }

    public void OnNodeAdvanced(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId, RoutingDecisionSource decisionSource)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (intentToken is null) throw new ArgumentNullException(nameof(intentToken));
        if (allowedNextNodes is null) throw new ArgumentNullException(nameof(allowedNextNodes));
        if (nextNodeId is null) throw new ArgumentNullException(nameof(nextNodeId));
    }

    public void OnNodeHalted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, RuntimeError error)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (intentToken is null) throw new ArgumentNullException(nameof(intentToken));
        if (allowedNextNodes is null) throw new ArgumentNullException(nameof(allowedNextNodes));
        if (error is null) throw new ArgumentNullException(nameof(error));
    }

    public void OnHalted(RoutingState state, RuntimeError error)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (error is null) throw new ArgumentNullException(nameof(error));
    }

    public void OnCompleted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (intentToken is null) throw new ArgumentNullException(nameof(intentToken));
        if (allowedNextNodes is null) throw new ArgumentNullException(nameof(allowedNextNodes));
    }
}
