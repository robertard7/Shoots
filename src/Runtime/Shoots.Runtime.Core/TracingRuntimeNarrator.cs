using System;
using System.Collections.Generic;
using System.Linq;
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
        _trace.Add(RoutingTraceEventKind.Plan, detail: text);
        _inner.OnPlan(text);
    }

    public void OnCommand(RuntimeCommandSpec command, RuntimeRequest request)
    {
        _trace.Add(RoutingTraceEventKind.Command, detail: command.CommandId);
        _inner.OnCommand(command, request);
    }

    public void OnResult(RuntimeResult result)
    {
        _trace.Add(RoutingTraceEventKind.Result, detail: result.Ok.ToString());
        _inner.OnResult(result);
    }

    public void OnError(RuntimeError error)
    {
        _trace.Add(RoutingTraceEventKind.Error, detail: error.Code, error: error);
        _inner.OnError(error);
    }

    public void OnRoute(RouteNarration narration)
    {
        var detail = narration.DecisionRequired ? "decision_required=true" : "decision_required=false";
        _trace.Add(RoutingTraceEventKind.Route, detail: detail, step: narration.CurrentStep, error: narration.HaltReason);
        _inner.OnRoute(narration);
    }

    public void OnWorkOrderReceived(WorkOrder workOrder)
    {
        _trace.Add(RoutingTraceEventKind.WorkOrderReceived, detail: workOrder.Id.Value);
        _inner.OnWorkOrderReceived(workOrder);
    }

    public void OnRouteEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.RouteEntered, detail: detail, state: state, step: step);
        _inner.OnRouteEntered(state, step, intentToken, allowedNextNodes);
    }

    public void OnNodeEntered(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.NodeEntered, detail: detail, state: state, step: step);
        _inner.OnNodeEntered(state, step, intentToken, allowedNextNodes);
    }

    public void OnDecisionRequired(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.DecisionRequired, detail: detail, state: state, step: step);
        _inner.OnDecisionRequired(state, step, intentToken, allowedNextNodes);
    }

    public void OnDecisionAccepted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.DecisionAccepted, detail: detail, state: state, step: step);
        _inner.OnDecisionAccepted(state, step, intentToken, allowedNextNodes);
    }


    public void OnDecisionGateWaiting(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, DecisionPolicy policy, FallbackToolSelection? fallbackToolSelection, string? fallbackNextNodeId)
    {
        var detail = BuildDecisionGateDetail(step.NodeId, policy, fallbackToolSelection?.ToolId.Value, fallbackNextNodeId);
        _trace.Add(RoutingTraceEventKind.DecisionGateWaiting, detail: detail, state: state, step: step);
        _inner.OnDecisionGateWaiting(state, step, intentToken, allowedNextNodes, policy, fallbackToolSelection, fallbackNextNodeId);
    }

    public void OnDecisionGateBypassed(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, DecisionPolicy policy, ToolSelectionDecision fallbackSelection, string nextNodeId)
    {
        var detail = BuildDecisionGateDetail(step.NodeId, policy, fallbackSelection.ToolId.Value, nextNodeId);
        _trace.Add(RoutingTraceEventKind.DecisionGateBypassed, detail: detail, fromNodeId: step.NodeId, toNodeId: nextNodeId, state: state, step: step);
        _inner.OnDecisionGateBypassed(state, step, intentToken, allowedNextNodes, policy, fallbackSelection, nextNodeId);
    }

    public void OnDecisionGateRequiredError(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, DecisionPolicy policy, RuntimeError error)
    {
        var detail = BuildDecisionGateDetail(step.NodeId, policy, null, null);
        _trace.Add(RoutingTraceEventKind.DecisionGateRequiredError, detail: detail, state: state, step: step, error: error);
        _inner.OnDecisionGateRequiredError(state, step, intentToken, allowedNextNodes, policy, error);
    }

    public void OnStepBudgetExceeded(RoutingState state, int stepBudget, RuntimeError error)
    {
        _trace.Add(RoutingTraceEventKind.StepBudgetExceeded, detail: $"budget={stepBudget}", state: state, error: error);
        _inner.OnStepBudgetExceeded(state, stepBudget, error);
    }

    public void OnNodeTransitionChosen(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId, RoutingDecisionSource decisionSource)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, nextNodeId);
        _trace.Add(RoutingTraceEventKind.NodeTransitionChosen, detail: detail, fromNodeId: step.NodeId, toNodeId: nextNodeId, decisionSource: decisionSource, state: state, step: step);
        _inner.OnNodeTransitionChosen(state, step, intentToken, allowedNextNodes, nextNodeId, decisionSource);
    }

    public void OnNodeAdvanced(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, string nextNodeId, RoutingDecisionSource decisionSource)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, nextNodeId);
        _trace.Add(RoutingTraceEventKind.NodeAdvanced, detail: detail, fromNodeId: step.NodeId, toNodeId: nextNodeId, decisionSource: decisionSource, state: state, step: step);
        _inner.OnNodeAdvanced(state, step, intentToken, allowedNextNodes, nextNodeId, decisionSource);
    }

    public void OnNodeHalted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes, RuntimeError error)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.NodeHalted, detail: detail, state: state, step: step, error: error);
        _inner.OnNodeHalted(state, step, intentToken, allowedNextNodes, error);
    }

    public void OnHalted(RoutingState state, RuntimeError error)
    {
        _trace.Add(RoutingTraceEventKind.Halted, detail: error.Code, state: state, error: error);
        _inner.OnHalted(state, error);
    }

    public void OnCompleted(RoutingState state, RouteStep step, RouteIntentToken intentToken, IReadOnlyList<string> allowedNextNodes)
    {
        var detail = BuildRouteDetail(step.NodeId, intentToken, allowedNextNodes, null);
        _trace.Add(RoutingTraceEventKind.Completed, detail: detail, state: state, step: step);
        _inner.OnCompleted(state, step, intentToken, allowedNextNodes);
    }

    private static string BuildDecisionGateDetail(string nodeId, DecisionPolicy policy, string? fallbackToolId, string? fallbackNextNodeId)
    {
        var detail = $"node={nodeId}|policy={policy}";
        if (!string.IsNullOrWhiteSpace(fallbackToolId))
            detail += $"|fallback.tool={fallbackToolId}";
        if (!string.IsNullOrWhiteSpace(fallbackNextNodeId))
            detail += $"|fallback.next={fallbackNextNodeId}";
        return detail;
    }

    private static string BuildRouteDetail(
        string nodeId,
        RouteIntentToken intentToken,
        IReadOnlyList<string> allowedNextNodes,
        string? selectedNextNodeId)
    {
        var nextNodes = allowedNextNodes.Count == 0
            ? string.Empty
            : string.Join(",", allowedNextNodes.OrderBy(node => node, StringComparer.Ordinal));
        var tokenHash = RouteIntentTokenFactory.ComputeTokenHash(intentToken);
        var detail = $"node={nodeId}|intent={tokenHash}|next={nextNodes}";
        return selectedNextNodeId is null ? detail : $"{detail}|selected={selectedNextNodeId}";
    }
}
