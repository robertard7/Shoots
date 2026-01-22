#nullable enable
using Shoots.Runtime.Abstractions;

namespace Shoots.Builder.Core;

/// <summary>
/// Passive adapter. Builder-only. No runtime authority.
/// </summary>
public sealed class TextRuntimeNarrator : IRuntimeNarrator
{
    private static readonly Action<string> Noop = _ => { };
    private readonly Action<string> _emit;

    public TextRuntimeNarrator(Action<string> emit)
    {
        _emit = emit ?? Noop;
    }

    public void OnPlan(string text) => Emit("[plan]");

    public void OnCommand(RuntimeCommandSpec command, RuntimeRequest request) => Emit("[command]");

    public void OnResult(RuntimeResult result) => Emit("[result]");

    public void OnError(RuntimeError error) => Emit("[error]");

    public void OnRoute(RouteNarration narration) => Emit("[route]");

    public void OnWorkOrderReceived(WorkOrder workOrder) => Emit("[workorder]");

    public void OnRouteEntered(
        RoutingState state,
        RouteStep step,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes) => Emit("[route.entered]");

    public void OnNodeEntered(
        RoutingState state,
        RouteStep step,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes) => Emit("[node.entered]");

    public void OnDecisionRequired(
        RoutingState state,
        RouteStep step,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes) => Emit("[decision.required]");

    public void OnDecisionAccepted(
        RoutingState state,
        RouteStep step,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes) => Emit("[decision.accepted]");

    public void OnNodeTransitionChosen(
        RoutingState state,
        RouteStep step,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes,
        string nextNodeId,
        RoutingDecisionSource decisionSource) => Emit("[node.transition.chosen]");

    public void OnNodeAdvanced(
        RoutingState state,
        RouteStep step,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes,
        string nextNodeId,
        RoutingDecisionSource decisionSource) => Emit("[node.advanced]");

    public void OnNodeHalted(
        RoutingState state,
        RouteStep step,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes,
        RuntimeError error) => Emit("[node.halted]");

    public void OnHalted(RoutingState state, RuntimeError error) => Emit("[route.halted]");

    public void OnCompleted(
        RoutingState state,
        RouteStep step,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes) => Emit("[route.completed]");

    private void Emit(string message) => _emit(message);
}
