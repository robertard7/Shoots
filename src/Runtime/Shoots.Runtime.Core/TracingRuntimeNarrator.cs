using System;
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

    public void OnRouteEntered(RoutingState state, RouteStep step)
    {
        _trace.Add(RoutingTraceEventKind.RouteEntered, step.NodeId, state, step);
        _inner.OnRouteEntered(state, step);
    }

    public void OnNodeEntered(RoutingState state, RouteStep step)
    {
        _trace.Add(RoutingTraceEventKind.NodeEntered, step.NodeId, state, step);
        _inner.OnNodeEntered(state, step);
    }

    public void OnDecisionRequired(RoutingState state, RouteStep step)
    {
        _trace.Add(RoutingTraceEventKind.DecisionRequired, step.NodeId, state, step);
        _inner.OnDecisionRequired(state, step);
    }

    public void OnDecisionAccepted(RoutingState state, RouteStep step)
    {
        _trace.Add(RoutingTraceEventKind.DecisionAccepted, step.NodeId, state, step);
        _inner.OnDecisionAccepted(state, step);
    }

    public void OnNodeTransitionChosen(RoutingState state, RouteStep step, string nextNodeId)
    {
        _trace.Add(RoutingTraceEventKind.NodeTransitionChosen, $"{step.NodeId}->{nextNodeId}", state, step);
        _inner.OnNodeTransitionChosen(state, step, nextNodeId);
    }

    public void OnNodeAdvanced(RoutingState state, RouteStep step, string nextNodeId)
    {
        _trace.Add(RoutingTraceEventKind.NodeAdvanced, $"{step.NodeId}->{nextNodeId}", state, step);
        _inner.OnNodeAdvanced(state, step, nextNodeId);
    }

    public void OnNodeHalted(RoutingState state, RouteStep step, RuntimeError error)
    {
        _trace.Add(RoutingTraceEventKind.NodeHalted, error.Code, state, step, error);
        _inner.OnNodeHalted(state, step, error);
    }

    public void OnHalted(RoutingState state, RuntimeError error)
    {
        _trace.Add(RoutingTraceEventKind.Halted, error.Code, state, error: error);
        _inner.OnHalted(state, error);
    }

    public void OnCompleted(RoutingState state, RouteStep step)
    {
        _trace.Add(RoutingTraceEventKind.Completed, step.NodeId, state, step);
        _inner.OnCompleted(state, step);
    }
}
