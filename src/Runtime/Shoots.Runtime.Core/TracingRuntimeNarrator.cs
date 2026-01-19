using System;
using System.Collections.Generic;
using Shoots.Contracts.Core;
using Shoots.Runtime.Abstractions;

namespace Shoots.Runtime.Core;

internal sealed class TracingRuntimeNarrator : IRuntimeNarrator
{
    private readonly IRuntimeNarrator _inner;
    private readonly RoutingTraceBuilder _trace;

    public TracingRuntimeNarrator(IRuntimeNarrator inner, RoutingTraceBuilder trace)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
    }

    public void OnPlan(string text)
    {
        _trace.Add(RoutingTraceEventKind.Plan, text);
        _inner.OnPlan(text);
    }

    public void OnCommand(RuntimeCommandSpec command, RuntimeRequest request)
    {
        _trace.Add(RoutingTraceEventKind.Command, command.CommandId);
        _inner.OnCommand(command, request);
    }

    public void OnResult(RuntimeResult result)
    {
        _trace.Add(RoutingTraceEventKind.Result, result.Ok.ToString());
        _inner.OnResult(result);
    }

    public void OnError(RuntimeError error)
    {
        _trace.Add(RoutingTraceEventKind.Error, error.Code, error: error);
        _inner.OnError(error);
    }

    public void OnRoute(RouteNarration narration)
    {
        var detail = narration.DecisionRequired ? "decision_required=true" : "decision_required=false";
        _trace.Add(RoutingTraceEventKind.Route, detail, step: narration.CurrentStep, error: narration.HaltReason);
        _inner.OnRoute(narration);
    }

    public void OnWorkOrderReceived(WorkOrder workOrder)
    {
        _trace.Add(RoutingTraceEventKind.WorkOrderReceived, workOrder.Id.Value);
        _inner.OnWorkOrderReceived(workOrder);
    }

    public void OnRouteEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.RouteEntered, detail, state, step);
        _inner.OnRouteEntered(state, step, intentToken, allowedNextNodes);
    }

    public void OnNodeEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.NodeEntered, detail, state, step);
        _inner.OnNodeEntered(state, step, intentToken, allowedNextNodes);
    }

    public void OnDecisionRequired(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.DecisionRequired, detail, state, step);
        _inner.OnDecisionRequired(state, step, intentToken, allowedNextNodes);
    }

    public void OnDecisionAccepted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.DecisionAccepted, detail, state, step);
        _inner.OnDecisionAccepted(state, step, intentToken, allowedNextNodes);
    }

    public void OnNodeTransitionChosen(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, nextNodeId);
        _trace.Add(RoutingTraceEventKind.NodeTransitionChosen, detail, state, step);
        _inner.OnNodeTransitionChosen(state, step, intentToken, allowedNextNodes, nextNodeId);
    }

    public void OnNodeAdvanced(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, nextNodeId);
        _trace.Add(RoutingTraceEventKind.NodeAdvanced, detail, state, step);
        _inner.OnNodeAdvanced(state, step, intentToken, allowedNextNodes, nextNodeId);
    }

    public void OnNodeHalted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, RuntimeError error)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.NodeHalted, detail, state, step, error);
        _inner.OnNodeHalted(state, step, intentToken, allowedNextNodes, error);
    }

    public void OnHalted(RoutingState state, RuntimeError error)
    {
        _trace.Add(RoutingTraceEventKind.Halted, error.Code, state, error: error);
        _inner.OnHalted(state, error);
    }

    public void OnCompleted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.Completed, detail, state, step);
        _inner.OnCompleted(state, step, intentToken, allowedNextNodes);
    }

    private static string BuildRouteDetail(
        string nodeId,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes,
        string? selectedNextNodeId)
    {
        var nextNodes = allowedNextNodes.Count == 0
            ? string.Empty
            : string.Join(",", allowedNextNodes);
        var tokenHash = RouteIntentTokenFactory.ComputeTokenHash(intentToken);
        var detail = $"node={nodeId}|intent={tokenHash}|next={nextNodes}";
        return selectedNextNodeId is null ? detail : $"{detail}|selected={selectedNextNodeId}";
    }
}
